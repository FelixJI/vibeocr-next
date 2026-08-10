namespace VibeOCR.App.Web;

public sealed class WorkbenchResourceAccessException(
  string message,
  Exception? innerException = null) : Exception(message, innerException);

public sealed record WorkbenchResourceLease(Uri Uri, DateTimeOffset ExpiresAt);

public sealed class WorkbenchResourceResponse(
  string contentType,
  long contentLength,
  Stream content) : IAsyncDisposable
{
  public string ContentType { get; } = contentType;

  public long ContentLength { get; } = contentLength;

  public Stream Content { get; } = content;

  public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public sealed class WorkbenchResourceBroker : IDisposable
{
  private const string ResourceHost = "app.vibeocr";
  private const string ResourceRoute = "/__resource/";
  private static readonly Uri ResourceOrigin = new("https://app.vibeocr/");

  private readonly object gate = new();
  private readonly Dictionary<string, ResourceEntry> entries = new(StringComparer.Ordinal);
  private readonly string resourceRoot;
  private readonly string resourceRootPrefix;
  private readonly TimeProvider timeProvider;
  private bool disposed;

  public WorkbenchResourceBroker(
    string resourceRoot,
    TimeProvider? timeProvider = null)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(resourceRoot);
    this.resourceRoot = Path.GetFullPath(resourceRoot);
    if (!Directory.Exists(this.resourceRoot))
    {
      throw new DirectoryNotFoundException(
        $"Workbench resource root does not exist: {this.resourceRoot}");
    }

    resourceRootPrefix = Path.EndsInDirectorySeparator(this.resourceRoot)
      ? this.resourceRoot
      : this.resourceRoot + Path.DirectorySeparatorChar;
    this.timeProvider = timeProvider ?? TimeProvider.System;
  }

  public WorkbenchResourceLease Lease(
    string relativePath,
    string contentType,
    TimeSpan lifetime)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
    ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
    if (contentType.Contains('\r', StringComparison.Ordinal) ||
        contentType.Contains('\n', StringComparison.Ordinal))
    {
      throw new ArgumentException(
        "Resource content type cannot contain line breaks.",
        nameof(contentType));
    }
    if (lifetime <= TimeSpan.Zero)
    {
      throw new ArgumentOutOfRangeException(
        nameof(lifetime),
        "Resource lease lifetime must be positive.");
    }

    string sourcePath = ResolveSourcePath(relativePath);
    DateTimeOffset expiresAt = timeProvider.GetUtcNow() + lifetime;
    string token = Guid.NewGuid().ToString("N");
    ResourceEntry entry = new(sourcePath, contentType, expiresAt);

    lock (gate)
    {
      ThrowIfDisposed();
      entries.Add(token, entry);
    }

    return new WorkbenchResourceLease(
      new Uri(ResourceOrigin, $"__resource/{token}"),
      expiresAt);
  }

  public bool Revoke(WorkbenchResourceLease lease)
  {
    ArgumentNullException.ThrowIfNull(lease);
    string token = ParseToken(lease.Uri);
    lock (gate)
    {
      ThrowIfDisposed();
      return entries.Remove(token);
    }
  }

  public ValueTask<WorkbenchResourceResponse> OpenAsync(
    Uri requestUri,
    CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(requestUri);
    cancellationToken.ThrowIfCancellationRequested();
    string token = ParseToken(requestUri);

    lock (gate)
    {
      ThrowIfDisposed();
      if (!entries.TryGetValue(token, out ResourceEntry? entry))
      {
        throw new WorkbenchResourceAccessException(
          "Workbench resource lease is unknown or revoked.");
      }
      if (entry.ExpiresAt <= timeProvider.GetUtcNow())
      {
        entries.Remove(token);
        throw new WorkbenchResourceAccessException(
          "Workbench resource lease has expired.");
      }

      try
      {
        EnsureContainedPath(entry.SourcePath);
        EnsureNoReparsePoints(entry.SourcePath);
        FileStream file = new(
          entry.SourcePath,
          FileMode.Open,
          FileAccess.Read,
          FileShare.Read,
          bufferSize: 64 * 1024,
          FileOptions.Asynchronous | FileOptions.SequentialScan);
        WorkbenchResourceResponse response = new(
          entry.ContentType,
          file.Length,
          new ReadOnlyResourceStream(file));
        return ValueTask.FromResult(response);
      }
      catch (Exception error) when (
        error is IOException or UnauthorizedAccessException)
      {
        throw new WorkbenchResourceAccessException(
          "Workbench resource cannot be opened for reading.",
          error);
      }
    }
  }

  public void Dispose()
  {
    lock (gate)
    {
      if (disposed)
      {
        return;
      }

      disposed = true;
      entries.Clear();
    }
  }

  private string ResolveSourcePath(string relativePath)
  {
    if (Path.IsPathRooted(relativePath))
    {
      throw new WorkbenchResourceAccessException(
        "Workbench resources must use a path relative to the resource root.");
    }

    string sourcePath;
    try
    {
      sourcePath = Path.GetFullPath(relativePath, resourceRoot);
    }
    catch (Exception error) when (
      error is ArgumentException or NotSupportedException or PathTooLongException)
    {
      throw new WorkbenchResourceAccessException(
        "Workbench resource path is invalid.",
        error);
    }

    EnsureContainedPath(sourcePath);
    if (!File.Exists(sourcePath))
    {
      throw new FileNotFoundException(
        "Workbench resource file does not exist.",
        sourcePath);
    }
    EnsureNoReparsePoints(sourcePath);
    return sourcePath;
  }

  private void EnsureContainedPath(string sourcePath)
  {
    string fullPath = Path.GetFullPath(sourcePath);
    if (!fullPath.StartsWith(
          resourceRootPrefix,
          StringComparison.OrdinalIgnoreCase))
    {
      throw new WorkbenchResourceAccessException(
        "Workbench resource path escapes the resource root.");
    }
  }

  private void EnsureNoReparsePoints(string sourcePath)
  {
    string relativePath = Path.GetRelativePath(resourceRoot, sourcePath);
    string current = resourceRoot;
    foreach (string segment in relativePath.Split(
      [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
      StringSplitOptions.RemoveEmptyEntries))
    {
      current = Path.Combine(current, segment);
      if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
      {
        throw new WorkbenchResourceAccessException(
          "Workbench resources cannot traverse a reparse point.");
      }
    }
  }

  private static string ParseToken(Uri requestUri)
  {
    if (!requestUri.IsAbsoluteUri ||
        !string.Equals(requestUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(requestUri.IdnHost, ResourceHost, StringComparison.OrdinalIgnoreCase) ||
        !requestUri.IsDefaultPort ||
        !string.IsNullOrEmpty(requestUri.UserInfo) ||
        !string.IsNullOrEmpty(requestUri.Query) ||
        !string.IsNullOrEmpty(requestUri.Fragment))
    {
      throw new WorkbenchResourceAccessException(
        "Workbench resource request must use the application origin.");
    }

    string path = requestUri.AbsolutePath;
    if (!path.StartsWith(ResourceRoute, StringComparison.Ordinal))
    {
      throw new WorkbenchResourceAccessException(
        "Workbench resource request route is invalid.");
    }
    string token = path[ResourceRoute.Length..];
    if (token.Length != 32 || token.Any(character =>
          character is not (>= '0' and <= '9') and
          not (>= 'a' and <= 'f')))
    {
      throw new WorkbenchResourceAccessException(
        "Workbench resource token is invalid.");
    }
    return token;
  }

  private void ThrowIfDisposed()
  {
    ObjectDisposedException.ThrowIf(disposed, this);
  }

  private sealed record ResourceEntry(
    string SourcePath,
    string ContentType,
    DateTimeOffset ExpiresAt);

  private sealed class ReadOnlyResourceStream(Stream inner) : Stream
  {
    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
      get => inner.Position;
      set => inner.Position = value;
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) =>
      inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => inner.Read(buffer);

    public override ValueTask<int> ReadAsync(
      Memory<byte> buffer,
      CancellationToken cancellationToken = default) =>
      inner.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) =>
      inner.Seek(offset, origin);

    public override void SetLength(long value) =>
      throw new NotSupportedException("The resource stream is read-only.");

    public override void Write(byte[] buffer, int offset, int count) =>
      throw new NotSupportedException("The resource stream is read-only.");

    public override void Write(ReadOnlySpan<byte> buffer) =>
      throw new NotSupportedException("The resource stream is read-only.");

    protected override void Dispose(bool disposing)
    {
      if (disposing)
      {
        inner.Dispose();
      }
      base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
      await inner.DisposeAsync();
      GC.SuppressFinalize(this);
    }
  }
}

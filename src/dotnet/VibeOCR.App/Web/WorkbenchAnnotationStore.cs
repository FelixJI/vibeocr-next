using System.Buffers.Binary;

namespace VibeOCR.App.Web;

public sealed class WorkbenchAnnotationAccessException(
  string message,
  Exception? innerException = null) : Exception(message, innerException);

public sealed record WorkbenchAnnotationLease(Uri ResourceUri, DateTimeOffset ExpiresAt);

public sealed class WorkbenchAnnotationFile : IDisposable
{
  private string? path;

  internal WorkbenchAnnotationFile(string path)
  {
    this.path = path;
  }

  public string Path => path ?? throw new ObjectDisposedException(nameof(WorkbenchAnnotationFile));

  public void Dispose()
  {
    string? ownedPath = Interlocked.Exchange(ref path, null);
    if (ownedPath is null)
    {
      return;
    }
    try
    {
      File.Delete(ownedPath);
    }
    catch (IOException)
    {
    }
    catch (UnauthorizedAccessException)
    {
    }
  }
}

/// <summary>
/// One-session, one-shot store for annotated PNGs uploaded by the workbench.
/// The Web bridge only receives an opaque same-origin URI; local paths never
/// cross the JSON protocol boundary.
/// </summary>
public sealed class WorkbenchAnnotationStore : IDisposable
{
  public const long MaximumPngBytes = 64L * 1024 * 1024;
  public const int MaximumDimensionPixels = 32_768;
  public const long MaximumImagePixels = 100_000_000;
  public const int MaximumUnconsumedEntries = 8;
  public const long MaximumSessionPngBytes = 128L * 1024 * 1024;
  public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(5);

  private const string AnnotationHost = "app.vibeocr";
  private const string AnnotationRoute = "/__annotation/";
  private static readonly Uri AnnotationOrigin = new("https://app.vibeocr/");
  private static readonly byte[] PngSignature = [
    0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
  ];
  private static readonly byte[] IhdrType = [0x49, 0x48, 0x44, 0x52];
  private static readonly byte[] IdatType = [0x49, 0x44, 0x41, 0x54];
  private static readonly byte[] IendType = [0x49, 0x45, 0x4e, 0x44];

  private readonly object gate = new();
  private readonly Dictionary<string, AnnotationEntry> entries = new(StringComparer.Ordinal);
  private readonly Dictionary<Guid, long> pendingReservations = [];
  private readonly HashSet<string> ownedFiles = new(StringComparer.OrdinalIgnoreCase);
  private readonly string sessionRoot;
  private readonly TimeProvider timeProvider;
  private readonly long maximumPngBytes;
  private readonly int maximumUnconsumedEntries;
  private readonly long maximumSessionPngBytes;
  private long unconsumedBytes;
  private long pendingBytes;
  private bool disposed;

  public WorkbenchAnnotationStore(
    string resourceRoot,
    TimeProvider? timeProvider = null,
    long maximumPngBytes = MaximumPngBytes,
    int maximumUnconsumedEntries = MaximumUnconsumedEntries,
    long maximumSessionPngBytes = MaximumSessionPngBytes)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(resourceRoot);
    string root = System.IO.Path.GetFullPath(resourceRoot);
    if (!Directory.Exists(root))
    {
      throw new DirectoryNotFoundException(
        $"Workbench resource root does not exist: {root}");
    }
    if (maximumPngBytes < PngSignature.Length || maximumPngBytes > MaximumPngBytes)
    {
      throw new ArgumentOutOfRangeException(nameof(maximumPngBytes));
    }
    if (maximumUnconsumedEntries is < 1 or > MaximumUnconsumedEntries)
    {
      throw new ArgumentOutOfRangeException(nameof(maximumUnconsumedEntries));
    }
    if (maximumSessionPngBytes < PngSignature.Length ||
        maximumSessionPngBytes > MaximumSessionPngBytes)
    {
      throw new ArgumentOutOfRangeException(nameof(maximumSessionPngBytes));
    }
    sessionRoot = System.IO.Path.Combine(
      root,
      "annotations",
      Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(sessionRoot);
    this.timeProvider = timeProvider ?? TimeProvider.System;
    this.maximumPngBytes = maximumPngBytes;
    this.maximumUnconsumedEntries = maximumUnconsumedEntries;
    this.maximumSessionPngBytes = maximumSessionPngBytes;
  }

  public async Task<WorkbenchAnnotationLease> UploadPngAsync(
    Stream content,
    CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(content);
    cancellationToken.ThrowIfCancellationRequested();
    Guid uploadId = Guid.NewGuid();
    lock (gate)
    {
      ThrowIfDisposed();
      SweepExpired(timeProvider.GetUtcNow());
      if (entries.Count + pendingReservations.Count >= maximumUnconsumedEntries)
      {
        throw new WorkbenchAnnotationAccessException(
          "Workbench annotation session has too many unconsumed uploads.");
      }
      if (unconsumedBytes + pendingBytes >= maximumSessionPngBytes)
      {
        throw new WorkbenchAnnotationAccessException(
          "Workbench annotation session byte quota is exhausted.");
      }
      pendingReservations.Add(uploadId, 0);
    }

    string token = Guid.NewGuid().ToString("N");
    string temporaryPath = System.IO.Path.Combine(sessionRoot, $"{token}.tmp");
    string destinationPath = System.IO.Path.Combine(sessionRoot, $"{token}.png");
    long written = 0;
    try
    {
      await using (FileStream destination = new(
        temporaryPath,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        bufferSize: 64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan))
      {
        byte[] buffer = new byte[64 * 1024];
        int read;
        while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
        {
          if (read > maximumPngBytes - written)
          {
            throw new WorkbenchAnnotationAccessException(
              "Annotated PNG exceeds the upload size limit.");
          }
          ReservePendingBytes(uploadId, read);
          written += read;
          await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
          ConfirmPendingBytes(uploadId, written);
        }
      }

      await ValidatePngStructureAsync(temporaryPath, cancellationToken);
      File.Move(temporaryPath, destinationPath);
      DateTimeOffset expiresAt = timeProvider.GetUtcNow() + DefaultLifetime;
      lock (gate)
      {
        ThrowIfDisposed();
        SweepExpired(timeProvider.GetUtcNow());
        if (!pendingReservations.Remove(uploadId, out long reserved) ||
            reserved != written)
        {
          throw new InvalidOperationException(
            "Workbench annotation upload reservation is inconsistent.");
        }
        pendingBytes -= reserved;
        entries.Add(token, new AnnotationEntry(destinationPath, expiresAt, written));
        ownedFiles.Add(destinationPath);
        unconsumedBytes += written;
      }
      return new WorkbenchAnnotationLease(
        new Uri(AnnotationOrigin, $"__annotation/{token}"),
        expiresAt);
    }
    catch
    {
      ReleasePendingReservation(uploadId);
      TryDelete(temporaryPath);
      TryDelete(destinationPath);
      throw;
    }
  }

  public WorkbenchAnnotationFile Take(Uri resourceUri)
  {
    ArgumentNullException.ThrowIfNull(resourceUri);
    string token = ParseToken(resourceUri);
    lock (gate)
    {
      ThrowIfDisposed();
      if (!entries.Remove(token, out AnnotationEntry? entry))
      {
        throw new WorkbenchAnnotationAccessException(
          "Annotated image lease is unknown or already consumed.");
      }
      unconsumedBytes -= entry.Length;
      if (entry.ExpiresAt <= timeProvider.GetUtcNow())
      {
        if (TryDelete(entry.Path))
        {
          ownedFiles.Remove(entry.Path);
        }
        throw new WorkbenchAnnotationAccessException(
          "Annotated image lease has expired.");
      }
      if (!File.Exists(entry.Path))
      {
        ownedFiles.Remove(entry.Path);
        throw new WorkbenchAnnotationAccessException(
          "Annotated image is unavailable.");
      }
      ownedFiles.Remove(entry.Path);
      return new WorkbenchAnnotationFile(entry.Path);
    }
  }

  public static bool IsResourceUri(Uri uri)
  {
    try
    {
      _ = ParseToken(uri);
      return true;
    }
    catch (WorkbenchAnnotationAccessException)
    {
      return false;
    }
  }

  public void Dispose()
  {
    string[] files;
    lock (gate)
    {
      if (disposed)
      {
        return;
      }
      disposed = true;
      entries.Clear();
      unconsumedBytes = 0;
      pendingReservations.Clear();
      pendingBytes = 0;
      files = [.. ownedFiles];
      ownedFiles.Clear();
    }
    foreach (string file in files)
    {
      TryDelete(file);
    }
    try
    {
      Directory.Delete(sessionRoot, recursive: false);
    }
    catch (IOException)
    {
    }
    catch (UnauthorizedAccessException)
    {
    }
  }

  private static async Task ValidatePngStructureAsync(
    string path,
    CancellationToken cancellationToken)
  {
    byte[] signature = new byte[PngSignature.Length];
    await using FileStream source = new(
      path,
      FileMode.Open,
      FileAccess.Read,
      FileShare.Read,
      bufferSize: 4096,
      FileOptions.Asynchronous | FileOptions.SequentialScan);
    await ReadExactlyAsync(source, signature, cancellationToken);
    if (!signature.AsSpan().SequenceEqual(PngSignature))
    {
      throw new WorkbenchAnnotationAccessException(
        "Annotated image is not a PNG file.");
    }

    bool firstChunk = true;
    bool hasHeader = false;
    bool hasImageData = false;
    bool imageDataEnded = false;
    bool hasEnd = false;
    byte[] chunkHeader = new byte[8];
    while (source.Position < source.Length)
    {
      await ReadExactlyAsync(source, chunkHeader, cancellationToken);
      uint dataLength = BinaryPrimitives.ReadUInt32BigEndian(chunkHeader.AsSpan(0, 4));
      if ((long)dataLength + 4 > source.Length - source.Position)
      {
        throw new WorkbenchAnnotationAccessException(
          "Annotated PNG contains a truncated chunk.");
      }

      bool isHeader = chunkHeader.AsSpan(4, 4).SequenceEqual(IhdrType);
      bool isImageData = chunkHeader.AsSpan(4, 4).SequenceEqual(IdatType);
      bool isEnd = chunkHeader.AsSpan(4, 4).SequenceEqual(IendType);
      if (firstChunk && !isHeader)
      {
        throw new WorkbenchAnnotationAccessException(
          "Annotated PNG must begin with IHDR.");
      }
      if (isHeader)
      {
        if (hasHeader || !firstChunk || dataLength != 13)
        {
          throw new WorkbenchAnnotationAccessException(
            "Annotated PNG IHDR is invalid or duplicated.");
        }
        byte[] header = new byte[13];
        await ReadExactlyAsync(source, header, cancellationToken);
        ValidatePngHeader(header);
        hasHeader = true;
      }
      else if (isEnd)
      {
        if (dataLength != 0 || !hasHeader || !hasImageData)
        {
          throw new WorkbenchAnnotationAccessException(
            "Annotated PNG IEND is invalid.");
        }
        hasEnd = true;
      }
      else
      {
        if (isImageData)
        {
          if (imageDataEnded)
          {
            throw new WorkbenchAnnotationAccessException(
              "Annotated PNG IDAT chunks must be consecutive.");
          }
          hasImageData = true;
        }
        else if (hasImageData)
        {
          imageDataEnded = true;
        }
        source.Seek(dataLength, SeekOrigin.Current);
      }

      source.Seek(4, SeekOrigin.Current); // Format-defined CRC; presence is bounded above.
      firstChunk = false;
      if (hasEnd)
      {
        if (source.Position != source.Length)
        {
          throw new WorkbenchAnnotationAccessException(
            "Annotated PNG contains data after IEND.");
        }
        break;
      }
    }
    if (!hasEnd)
    {
      throw new WorkbenchAnnotationAccessException(
        "Annotated PNG is missing IEND.");
    }
  }

  private static void ValidatePngHeader(ReadOnlySpan<byte> header)
  {
    uint width = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
    uint height = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(4, 4));
    if (width == 0 || height == 0 ||
        width > MaximumDimensionPixels || height > MaximumDimensionPixels ||
        (long)width * height > MaximumImagePixels)
    {
      throw new WorkbenchAnnotationAccessException(
        "Annotated PNG dimensions exceed the supported image bounds.");
    }
    byte bitDepth = header[8];
    byte colorType = header[9];
    bool validBitDepth = colorType switch
    {
      0 => bitDepth is 1 or 2 or 4 or 8 or 16,
      2 or 4 or 6 => bitDepth is 8 or 16,
      3 => bitDepth is 1 or 2 or 4 or 8,
      _ => false,
    };
    if (!validBitDepth || header[10] != 0 || header[11] != 0 || header[12] > 1)
    {
      throw new WorkbenchAnnotationAccessException(
        "Annotated PNG IHDR encoding is unsupported.");
    }
  }

  private static async Task ReadExactlyAsync(
    Stream source,
    Memory<byte> destination,
    CancellationToken cancellationToken)
  {
    int offset = 0;
    while (offset < destination.Length)
    {
      int read = await source.ReadAsync(destination[offset..], cancellationToken);
      if (read == 0)
      {
        throw new WorkbenchAnnotationAccessException(
          "Annotated PNG is truncated.");
      }
      offset += read;
    }
  }

  private void ReservePendingBytes(Guid uploadId, int count)
  {
    lock (gate)
    {
      ThrowIfDisposed();
      SweepExpired(timeProvider.GetUtcNow());
      if (!pendingReservations.TryGetValue(uploadId, out long reserved))
      {
        throw new InvalidOperationException(
          "Workbench annotation upload reservation is unavailable.");
      }
      if (count > maximumSessionPngBytes - unconsumedBytes - pendingBytes)
      {
        throw new WorkbenchAnnotationAccessException(
          "Workbench annotation session byte quota would be exceeded.");
      }
      pendingReservations[uploadId] = reserved + count;
      pendingBytes += count;
    }
  }

  private void ConfirmPendingBytes(Guid uploadId, long written)
  {
    lock (gate)
    {
      ThrowIfDisposed();
      if (!pendingReservations.TryGetValue(uploadId, out long reserved) ||
          reserved != written)
      {
        throw new InvalidOperationException(
          "Workbench annotation staged byte reservation is inconsistent.");
      }
    }
  }

  private void ReleasePendingReservation(Guid uploadId)
  {
    lock (gate)
    {
      if (pendingReservations.Remove(uploadId, out long reserved))
      {
        pendingBytes -= reserved;
      }
    }
  }

  private void SweepExpired(DateTimeOffset utcNow)
  {
    string[] expiredTokens = [.. entries
      .Where(pair => pair.Value.ExpiresAt <= utcNow)
      .Select(pair => pair.Key)];
    foreach (string token in expiredTokens)
    {
      AnnotationEntry entry = entries[token];
      entries.Remove(token);
      unconsumedBytes -= entry.Length;
      if (TryDelete(entry.Path))
      {
        ownedFiles.Remove(entry.Path);
      }
    }
  }

  private static string ParseToken(Uri resourceUri)
  {
    if (!resourceUri.IsAbsoluteUri ||
        !string.Equals(resourceUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(resourceUri.IdnHost, AnnotationHost, StringComparison.OrdinalIgnoreCase) ||
        !resourceUri.IsDefaultPort ||
        !string.IsNullOrEmpty(resourceUri.UserInfo) ||
        !string.IsNullOrEmpty(resourceUri.Query) ||
        !string.IsNullOrEmpty(resourceUri.Fragment) ||
        !resourceUri.AbsolutePath.StartsWith(AnnotationRoute, StringComparison.Ordinal))
    {
      throw new WorkbenchAnnotationAccessException(
        "Annotated image URI is outside the application origin.");
    }
    string token = resourceUri.AbsolutePath[AnnotationRoute.Length..];
    if (token.Length != 32 || token.Any(character =>
          character is not (>= '0' and <= '9') and
          not (>= 'a' and <= 'f')))
    {
      throw new WorkbenchAnnotationAccessException(
        "Annotated image token is invalid.");
    }
    return token;
  }

  private static bool TryDelete(string path)
  {
    try
    {
      File.Delete(path);
      return true;
    }
    catch (IOException)
    {
      return false;
    }
    catch (UnauthorizedAccessException)
    {
      return false;
    }
  }

  private void ThrowIfDisposed()
  {
    ObjectDisposedException.ThrowIf(disposed, this);
  }

  private sealed record AnnotationEntry(
    string Path,
    DateTimeOffset ExpiresAt,
    long Length);
}

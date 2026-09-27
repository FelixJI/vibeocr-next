namespace VibeOCR.App.Web;

/// <summary>Read-only, same-origin access to the verified production bundle.</summary>
internal sealed class PackagedWebAssets
{
  private readonly string root;
  private readonly Dictionary<string, Asset> assets = new(StringComparer.Ordinal);

  public PackagedWebAssets(string folder)
  {
    root = Path.GetFullPath(folder);
    foreach (string path in Directory.EnumerateFiles(root, "*", new EnumerationOptions
    {
      RecurseSubdirectories = true,
      AttributesToSkip = FileAttributes.ReparsePoint,
    }))
    {
      string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
      string? contentType = ContentType(Path.GetExtension(path));
      if (contentType is not null)
      {
        assets.Add("/" + relative, new Asset(path, contentType));
      }
    }
    if (!assets.ContainsKey("/index.html"))
    {
      throw new InvalidOperationException("Packaged workbench index is missing.");
    }
  }

  public WorkbenchResourceResponse? Open(Uri uri)
  {
    if (!uri.IsAbsoluteUri ||
        uri.Scheme != Uri.UriSchemeHttps ||
        uri.IdnHost != WebWorkbenchHost.VirtualHost ||
        !uri.IsDefaultPort ||
        uri.UserInfo.Length != 0 ||
        uri.Query.Length != 0 ||
        uri.Fragment.Length != 0 ||
        !assets.TryGetValue(uri.AbsolutePath == "/" ? "/index.html" : uri.AbsolutePath,
          out Asset? asset))
    {
      return null;
    }
    string relative = Path.GetRelativePath(root, asset.Path);
    string current = root;
    foreach (string segment in relative.Split(Path.DirectorySeparatorChar))
    {
      current = Path.Combine(current, segment);
      if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
      {
        throw new WorkbenchResourceAccessException("Packaged asset crosses a reparse point.");
      }
    }
    FileStream stream = new(asset.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
      64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    return new WorkbenchResourceResponse(asset.ContentType, stream.Length, stream);
  }

  private static string? ContentType(string extension) => extension.ToLowerInvariant() switch
  {
    ".html" => "text/html; charset=utf-8",
    ".js" => "text/javascript; charset=utf-8",
    ".css" => "text/css; charset=utf-8",
    ".png" => "image/png",
    ".ico" => "image/x-icon",
    ".svg" => "image/svg+xml",
    ".woff" => "font/woff",
    ".woff2" => "font/woff2",
    _ => null,
  };

  private sealed record Asset(string Path, string ContentType);
}

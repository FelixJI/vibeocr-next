using System;
using System.IO;
using System.Linq;

namespace VibeOCR.ProductLayout;

internal static class DotNetRuntimeProbe
{
  internal static string? FindVersion(string? programFiles = null)
  {
    string root = Path.Combine(
        programFiles ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "dotnet", "shared", "Microsoft.NETCore.App");
    if (!Directory.Exists(root))
    {
      return null;
    }

    return Directory.EnumerateDirectories(root)
        .Select(Path.GetFileName)
        .Where(value => Version.TryParse(value, out Version? version) && version!.Major == 10)
        .OrderByDescending(value => Version.Parse(value!))
        .FirstOrDefault();
  }
}

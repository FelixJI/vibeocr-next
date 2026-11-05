using System.Security.Cryptography;
using System.Text.Json;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using Xunit;

namespace VibeOCR.Platform.Tests;

public sealed class ProductRuntimeSessionTests
{
  [Theory]
  [InlineData("ready")]
  [InlineData("missing-python")]
  [InlineData("changed-python")]
  [InlineData("changed-dll")]
  [InlineData("changed-manifest")]
  public void ProductLaunchRequiresBoundInterpreterAndDll(string fault)
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-bound-runtime-{Guid.NewGuid():N}");
    PortableLayout layout = PortableLayout.Resolve(root, "production");
    string dll = Path.Combine(Path.GetDirectoryName(layout.RuntimePython)!, "python313.dll");
    string release = Path.Combine(layout.ProductRoot, "app", "metadata", "product-release-manifest.json");
    foreach (string path in new[] { layout.RuntimePython, layout.RuntimeManifest, layout.ComponentLock, release })
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(layout.RuntimePython, "synthetic interpreter");
    File.WriteAllText(dll, "synthetic CPython DLL");
    File.WriteAllText(layout.RuntimeManifest, """{"runtime_env":{"root":"runtime-env"}}""");
    string Digest(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    File.WriteAllText(layout.ComponentLock, JsonSerializer.Serialize(new
    {
      product = new { runtime_manifest_sha256 = Digest(layout.RuntimeManifest) },
      required_capabilities = new[] { "ocr" },
    }));
    var files = new Dictionary<string, object>();
    foreach (string asset in new[] { layout.RuntimePython, dll })
      files.Add(Path.GetRelativePath(root, asset).Replace(Path.DirectorySeparatorChar, '/'), new { sha256 = Digest(asset) });
    File.WriteAllText(release, JsonSerializer.Serialize(new { files }));

    if (fault == "missing-python") File.Delete(layout.RuntimePython);
    if (fault == "changed-python") File.AppendAllText(layout.RuntimePython, "changed");
    if (fault == "changed-dll") File.AppendAllText(dll, "changed");
    if (fault == "changed-manifest") File.AppendAllText(layout.RuntimeManifest, " ");
    if (fault == "ready")
    {
      InferenceSupervisorOptions options = ProductRuntimeSession.SupervisorOptions(layout, false);
      Assert.Equal(layout.RuntimePython, options.FileName);
      Assert.Equal(new[] { "-I", "-B", "-X", "utf8", "-m", "vibeocr.runtime.host.main" }, options.Arguments);
      Assert.Equal(root, options.WorkingDirectory);
    }
    else if (fault == "missing-python")
      Assert.Throws<FileNotFoundException>(() => ProductRuntimeSession.SupervisorOptions(layout, false));
    else
      Assert.Throws<InvalidDataException>(() => ProductRuntimeSession.SupervisorOptions(layout, false));
  }
}

using VibeOCR.Platform.Bootstrap;
using Xunit;

namespace VibeOCR.Platform.Tests;

public sealed class PrerequisiteDetectorTests
{
  private static readonly PortableLayout DevLayout = PortableLayout.Resolve("C:\\VibeOCR", "winui-dev");

  private static PortableLayout BundledLayout(string root) =>
      PortableLayout.Resolve(Path.Combine(root, "VibeOCR.WinUI.exe"), "production");

  private static string BundledRuntimeConfig =>
      """
      {"runtimeOptions":{"tfm":"net10.0","includedFrameworks":[
      {"name":"Microsoft.NETCore.App","version":"10.0.15"}]}}
      """;

  private static void WriteBundledApp(string root, params string[] missing)
  {
    Directory.CreateDirectory(root);
    foreach ((string relative, string? content) in new[]
    {
      ("VibeOCR.WinUI.exe", "entry"),
      ("hostpolicy.dll", "dotnet"),
      ("hostfxr.dll", "dotnet"),
      ("coreclr.dll", "dotnet"),
      ("System.Private.CoreLib.dll", "clr"),
      ("VibeOCR.WinUI.runtimeconfig.json", BundledRuntimeConfig),
      ("Microsoft.WindowsAppRuntime.dll", "war"),
      ("Microsoft.UI.Xaml.dll", "xaml"),
      ("RuntimePython", "python"),
      (@"WebView2Runtime\msedgewebview2.exe", "webview2"),
      (@"WebView2Runtime\icudtl.dat", "icu"),
    })
    {
      if (missing.Contains(relative))
      {
        continue;
      }
      string path = relative == "RuntimePython"
          ? Path.Combine(root, "runtime", "env", "runtime-env", "python.exe")
          : Path.Combine(root, relative);
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      File.WriteAllText(path, content);
    }
  }

  [Fact]
  public void WinUiDevKeepsSystemProbesForEveryMissingPrerequisite()
  {
    var detector = new PrerequisiteDetector(
        _ => new PrerequisiteSnapshot(null, null, null, false));

    PrerequisiteReport report = detector.Detect(DevLayout);

    Assert.False(report.IsReady);
    Assert.Equal(
        [
            PrerequisiteKind.DotNetRuntime,
                PrerequisiteKind.WindowsAppRuntime,
                PrerequisiteKind.WebView2Runtime,
                PrerequisiteKind.RuntimePython,
            ],
        report.Missing.Select(item => item.Kind));
  }

  [Fact]
  public void WinUiDevAcceptsCompatibleInstalledSystemVersions()
  {
    var detector = new PrerequisiteDetector(
        _ => new PrerequisiteSnapshot("10.0.9", "2.6.4", "140.0.3485.54", true));

    PrerequisiteReport report = detector.Detect(DevLayout);

    Assert.True(report.IsReady);
    Assert.Empty(report.Missing);
  }

  [Theory]
  [InlineData("9.0.17", "2.6.4", false)]
  [InlineData("10.0.9", "2.2.0", false)]
  [InlineData("10.0.9", "2.5.1", false)]
  [InlineData("10.0.9", "2.6.4", true)]
  [InlineData("10.0.9", "2.6.4.0", true)]
  [InlineData("10.0.9", "2.7.0", true)]
  public void WinUiDevEnforcesMinimumDotNetAndWindowsAppRuntimeVersions(
      string desktop,
      string windowsAppRuntime,
      bool expectedReady)
  {
    var detector = new PrerequisiteDetector(
        _ => new PrerequisiteSnapshot(desktop, windowsAppRuntime, "140.0", true));

    Assert.Equal(expectedReady, detector.Detect(DevLayout).IsReady);
  }

  [Fact]
  public void ProductionProbesTheBundledPayloadOnly()
  {
    string root = Path.Combine(
        Path.GetTempPath(), $"vibeocr-bundled-{Guid.NewGuid():N}");
    try
    {
      WriteBundledApp(root);

      // #321 F1：诊断按真实产品模式（构建属性）而不是 Profile。
      PrerequisiteReport report = new PrerequisiteDetector().Detect(
          BundledLayout(root), bundledProduct: true);

      Assert.True(report.IsReady);
      Assert.All(report.Items, item => Assert.Equal("Bundled", item.MinimumVersion));
      Assert.All(report.Items, item => Assert.Equal("repair://vibeocr/product", item.RepairUri));
      PrerequisiteStatus dotnet = report.Items.Single(item =>
          item.Kind == PrerequisiteKind.DotNetRuntime);
      Assert.Equal("10.0.15", dotnet.InstalledVersion);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Theory]
  [InlineData("hostpolicy.dll", PrerequisiteKind.DotNetRuntime)]
  [InlineData("hostfxr.dll", PrerequisiteKind.DotNetRuntime)]
  [InlineData("System.Private.CoreLib.dll", PrerequisiteKind.DotNetRuntime)]
  [InlineData("VibeOCR.WinUI.runtimeconfig.json", PrerequisiteKind.DotNetRuntime)]
  [InlineData("Microsoft.WindowsAppRuntime.dll", PrerequisiteKind.WindowsAppRuntime)]
  [InlineData("Microsoft.UI.Xaml.dll", PrerequisiteKind.WindowsAppRuntime)]
  [InlineData(@"WebView2Runtime\msedgewebview2.exe", PrerequisiteKind.WebView2Runtime)]
  public void ProductionReportsDamagedBundledComponentsAsMissing(
      string missing,
      PrerequisiteKind kind)
  {
    // #321 回归：随包载荷损坏时诊断必须失败；旧实现用已安装系统运行库
    // 掩盖损坏（本测试在旧实现上不会失败）。
    string root = Path.Combine(
        Path.GetTempPath(), $"vibeocr-bundled-missing-{Guid.NewGuid():N}");
    try
    {
      WriteBundledApp(root, missing);

      PrerequisiteReport report = new PrerequisiteDetector().Detect(
          BundledLayout(root), bundledProduct: true);

      Assert.False(report.IsReady);
      Assert.Contains(report.Missing, item => item.Kind == kind);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public void DamagedRuntimeConfigIsNotReportedAsBundled()
  {
    // F6：runtimeconfig 损坏时不得报 Bundled（缺 hostfxr 也必须失败）。
    string root = Path.Combine(
        Path.GetTempPath(), $"vibeocr-bundled-broken-{Guid.NewGuid():N}");
    try
    {
      WriteBundledApp(root);
      File.WriteAllText(
          Path.Combine(root, "VibeOCR.WinUI.runtimeconfig.json"), "{ not json");

      PrerequisiteReport report = new PrerequisiteDetector().Detect(
          BundledLayout(root), bundledProduct: true);

      PrerequisiteStatus dotnet = report.Items.Single(item =>
          item.Kind == PrerequisiteKind.DotNetRuntime);
      Assert.False(dotnet.IsInstalled);
      Assert.Null(dotnet.InstalledVersion);
      Assert.Contains(report.Missing, item => item.Kind == PrerequisiteKind.DotNetRuntime);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Theory]
  [InlineData(@"{""runtimeOptions"":{""includedFrameworks"":""not-an-array""}}")]
  [InlineData(@"[{""not"":""an object""}]")]
  [InlineData(@"{""runtimeOptions"":{""includedFrameworks"":{}}}")]
  [InlineData(@"{""runtimeOptions"":{""includedFrameworks"":[null]}}")]
  [InlineData(@"{""runtimeOptions"":{""includedFrameworks"":[{""name"":""Microsoft.NETCore.App"",""version"":""broken""}]}}")]
  [InlineData(@"{""runtimeOptions"":{""includedFrameworks"":[{""name"":""Microsoft.NETCore.App"",""version"":""10.0""}]}}")]
  [InlineData(@"{""runtimeOptions"":{""includedFrameworks"":[{""name"":""Microsoft.NETCore.App"",""version"":""9.0.15""}]}}")]
  [InlineData(@"{""runtimeOptions"":{""includedFrameworks"":[{""name"":""Microsoft.NETCore.App"",""version"":""10.0.0""}],""framework"":null}}")]
  [InlineData(@"{""runtimeOptions"":{""includedFrameworks"":[{""name"":""Microsoft.NETCore.App"",""version"":""10.0.0""}],""framework"":{}}}")]
  [InlineData(@"{""runtimeOptions"":{""includedFrameworks"":[{""name"":""Microsoft.NETCore.App"",""version"":""10.0.0""}],""frameworks"":null}}")]
  [InlineData(@"{""runtimeOptions"":{""includedFrameworks"":[{""name"":""Microsoft.NETCore.App"",""version"":""10.0.0""}],""frameworks"":[]}}")]
  public void StructurallyDamagedRuntimeConfigIsTreatedAsMissing(string runtimeConfig)
  {
    // 结构损坏的 runtimeconfig 按随包 .NET 缺失处理，不得抛给调用方。
    string root = Path.Combine(
        Path.GetTempPath(), $"vibeocr-bundled-structural-{Guid.NewGuid():N}");
    try
    {
      WriteBundledApp(root);
      File.WriteAllText(
          Path.Combine(root, "VibeOCR.WinUI.runtimeconfig.json"), runtimeConfig);

      PrerequisiteReport report = new PrerequisiteDetector().Detect(
          BundledLayout(root), bundledProduct: true);

      PrerequisiteStatus dotnet = report.Items.Single(item =>
          item.Kind == PrerequisiteKind.DotNetRuntime);
      Assert.False(dotnet.IsInstalled);
      Assert.Null(dotnet.InstalledVersion);
      Assert.Contains(report.Missing, item => item.Kind == PrerequisiteKind.DotNetRuntime);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public void DevProbeIsReadOnlyAndAlwaysReturnsFourStatuses()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-probe-{Guid.NewGuid():N}");

    PrerequisiteReport report = new PrerequisiteDetector().Detect(
        PortableLayout.Resolve(root, "winui-dev"));

    Assert.Equal(4, report.Items.Count);
    Assert.False(Directory.Exists(root));
  }

  [Theory]
  [InlineData("Microsoft.WindowsAppRuntime.2", 2, 2, false)]
  [InlineData("Microsoft.WindowsAppRuntime.2", 2, 5, false)]
  [InlineData("Microsoft.WindowsAppRuntime.2", 2, 6, true)]
  [InlineData("Microsoft.WindowsAppRuntime.CBS.2", 2, 2, false)]
  [InlineData("Microsoft.WindowsAppRuntime.CBS.2", 2, 4, false)]
  [InlineData("Microsoft.WindowsAppRuntime.CBS.2", 2, 6, true)]
  [InlineData("Microsoft.WindowsAppRuntime.CBS.1.6", 6000, 900, false)]
  public void AcceptsOnlyCompatibleWindowsAppRuntimePackageIdentities(
      string name,
      ushort major,
      ushort minor,
      bool expected)
  {
    Assert.Equal(
        expected,
        WindowsPrerequisiteProbe.IsCompatibleWindowsAppRuntimePackage(
            name,
            major,
            minor));
  }
}

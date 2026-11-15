using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using VibeOCR.ProductLayout;
using Velopack;

namespace VibeOCR.Bootstrapper;

internal static class Program
{
  private const int PrerequisiteMissing = 2;
  private const int AppMissing = 3;
  private const int InvalidArguments = 4;
  private const int LayoutInvalid = 5;
  private const string SelfTestPrerequisites = "--self-test-prerequisites";

  [STAThread]
  private static int Main(string[] args)
  {
    LegacyVelopackStateMigration.Resume(AppDomain.CurrentDomain.BaseDirectory);
    VelopackApp.Build()
        .OnAfterUpdateFastCallback(_ => LegacyVelopackStateMigration.Migrate(
            AppDomain.CurrentDomain.BaseDirectory))
        .Run();
    PortableProductRoots roots = PortableProductRoots.Resolve(
        AppDomain.CurrentDomain.BaseDirectory);
    BootstrapperLog.Initialize(Path.Combine(roots.InstallRoot, "state", "logs"));
    try
    {
      return Launch(args);
    }
    catch (Exception error)
    {
      BootstrapperLog.Error("Unexpected bootstrapper failure: " + error);
      throw;
    }
  }

  private static int Launch(string[] args)
  {
    bool selfTest = args.Contains(SelfTestPrerequisites, StringComparer.Ordinal);
    bool artifactSmoke = BootstrapperArtifactSmoke.IsRequested();
    PortableProductRoots roots = PortableProductRoots.Resolve(
        AppDomain.CurrentDomain.BaseDirectory);
    BootstrapperLog.Info(
        $"Bootstrapper starting: installRoot={roots.InstallRoot} productRoot={roots.ProductRoot}");
    ResolvedProductLayout layout;
    try
    {
      layout = ResolvedProductLayout.Open(roots.ProductRoot);
    }
    catch (InvalidDataException error)
    {
      ReportRepair(error.Message, selfTest, artifactSmoke);
      return LayoutInvalid;
    }
    string appPath = layout.AppEntry;
    string profile = ReadOption(args, "--profile") ?? "production";
    if (profile is not ("production" or "winui-dev"))
    {
      Report("Unsupported profile: " + profile);
      return InvalidArguments;
    }
    string[] missing = (artifactSmoke
        ? new string?[]
        {
          HasBoundRuntimeAssets(layout) ? null : "随包 Runtime 文件缺失",
        }
        : new string?[]
        {
          MissingBundledDotNetRuntime(layout),
          HasBundledWindowsAppRuntime(layout) ? null : "随包 Windows App Runtime 文件缺失：Microsoft.WindowsAppRuntime.dll",
          HasBundledWebView2Runtime(layout) ? null : "随包 WebView2 文件缺失：msedgewebview2.exe",
          HasBoundRuntimeAssets(layout) ? null : "随包 Runtime 文件缺失",
        })
        .Where(item => item != null).Select(item => item!).ToArray();
    if (missing.Length > 0)
    {
      ReportRepair(string.Join(Environment.NewLine, missing), selfTest, artifactSmoke);
      return PrerequisiteMissing;
    }

    if (!File.Exists(appPath))
    {
      ReportRepair("应用入口缺失：" + appPath, selfTest, artifactSmoke);
      return AppMissing;
    }

    if (selfTest)
    {
      BootstrapperLog.Info("Bootstrapper prerequisite self-test passed.");
      return 0;
    }

    string arguments = "--profile " + Quote(profile) +
        " --install-root " + Quote(roots.InstallRoot) +
        " --product-root " + Quote(roots.ProductRoot);
    var startInfo = new ProcessStartInfo
    {
      FileName = appPath,
      Arguments = arguments,
      WorkingDirectory = Path.GetDirectoryName(appPath),
      UseShellExecute = true,
    };
    Process.Start(startInfo);
    BootstrapperLog.Info($"Launched WinUI app: {appPath} {arguments}");
    return 0;
  }

  private static void Report(string message)
  {
    Console.Error.WriteLine(message);
    BootstrapperLog.Error(message);
  }

  private static void ReportRepair(string reason, bool selfTest, bool artifactSmoke)
  {
    Report(reason);
    Report("No component was downloaded or modified.");
    Report("Repair with the in-app update or a fresh complete download.");
    if (BootstrapperRepairPrompt.CreateMessage(
        reason, selfTest, artifactSmoke, Environment.UserInteractive) is { } message)
    {
      BootstrapperRepairPrompt.Show(message);
    }
  }

  private static string? ReadOption(string[] args, string name)
  {
    for (int index = 0; index + 1 < args.Length; index++)
    {
      if (string.Equals(args[index], name, StringComparison.Ordinal))
      {
        return args[index + 1];
      }
    }

    return null;
  }

  private static bool HasBoundRuntimeAssets(ResolvedProductLayout layout) =>
      File.Exists(layout.ComponentLock) &&
      File.Exists(layout.RuntimeManifest) &&
      File.Exists(layout.RuntimePython);

  // #321：随包闭包检查。安装了系统 .NET/Windows App Runtime/WebView2
  // 不再满足检查——损坏的随包载荷必须显式报修，不联网、不回退系统。
  private static string? MissingBundledDotNetRuntime(ResolvedProductLayout layout)
  {
    string[] missing = new[]
    {
      Path.Combine(layout.AppRoot, "hostfxr.dll"),
      Path.Combine(layout.AppRoot, "hostpolicy.dll"),
      Path.Combine(layout.AppRoot, "coreclr.dll"),
    }.Where(path => !File.Exists(path)).Select(path => Path.GetFileName(path)!).ToArray();
    return missing.Length == 0 ? null : "随包 .NET Runtime 文件缺失：" + string.Join("、", missing);
  }

  private static bool HasBundledWindowsAppRuntime(ResolvedProductLayout layout) =>
      File.Exists(Path.Combine(layout.AppRoot, "Microsoft.WindowsAppRuntime.dll"));

  private static bool HasBundledWebView2Runtime(ResolvedProductLayout layout) =>
      File.Exists(Path.Combine(layout.WebView2RuntimeRoot, "msedgewebview2.exe"));

  private static string Quote(string value) =>
      "\"" + value.Replace("\"", "\\\"") + "\"";

}

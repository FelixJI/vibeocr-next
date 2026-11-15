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
      Report(error.Message);
      return LayoutInvalid;
    }
    string appPath = layout.AppEntry;
    string profile = ReadOption(args, "--profile") ?? "production";
    if (profile is not ("production" or "winui-dev"))
    {
      Report("Unsupported profile: " + profile);
      return InvalidArguments;
    }
    string[] missing = (BootstrapperArtifactSmoke.IsRequested()
        ? new string?[]
        {
          HasBoundRuntimeAssets(layout) ? null : "VibeOCR bound Runtime assets",
        }
        : new string?[]
        {
          HasBundledDotNetRuntime(layout) ? null : "VibeOCR bundled .NET Runtime files",
          HasBundledWindowsAppRuntime(layout) ? null : "VibeOCR bundled Windows App Runtime files",
          HasBundledWebView2Runtime(layout) ? null : "VibeOCR bundled WebView2 Fixed Version runtime",
          HasBoundRuntimeAssets(layout) ? null : "VibeOCR bound Runtime assets",
        })
        .Where(item => item != null).Select(item => item!).ToArray();
    if (missing.Length > 0)
    {
      Report("VibeOCR bundled runtime components require repair:");
      foreach (string item in missing)
      {
        Report("- " + item);
      }

      Report("No component was downloaded or modified.");
      Report("Repair with the in-app update or a fresh complete download.");
      return PrerequisiteMissing;
    }

    if (!File.Exists(appPath))
    {
      Report("WinUI application is missing: " + appPath);
      return AppMissing;
    }

    if (args.Contains(SelfTestPrerequisites, StringComparer.Ordinal))
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
  private static bool HasBundledDotNetRuntime(ResolvedProductLayout layout) =>
      File.Exists(Path.Combine(layout.AppRoot, "hostfxr.dll")) &&
      File.Exists(Path.Combine(layout.AppRoot, "hostpolicy.dll")) &&
      File.Exists(Path.Combine(layout.AppRoot, "coreclr.dll"));

  private static bool HasBundledWindowsAppRuntime(ResolvedProductLayout layout) =>
      File.Exists(Path.Combine(layout.AppRoot, "Microsoft.WindowsAppRuntime.dll"));

  private static bool HasBundledWebView2Runtime(ResolvedProductLayout layout) =>
      File.Exists(Path.Combine(layout.WebView2RuntimeRoot, "msedgewebview2.exe"));

  private static string Quote(string value) =>
      "\"" + value.Replace("\"", "\\\"") + "\"";

}

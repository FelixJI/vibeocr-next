using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VibeOCR.ProductLayout;
using Velopack;

namespace VibeOCR.Bootstrapper;

internal static class Program
{
  private const int PrerequisiteMissing = 2;
  private const int AppMissing = 3;
  private const int InvalidArguments = 4;
  private const int LayoutInvalid = 5;
  private const uint Windows64BitBinary = 6;
  private const string SelfTestPrerequisites = "--self-test-prerequisites";

  [STAThread]
  private static int Main(string[] args)
  {
    string productRoot = AppDomain.CurrentDomain.BaseDirectory;
    LegacyVelopackStateMigration.Resume(productRoot);
    string velopackDll = Path.Combine(productRoot, "Velopack.dll");
    if (!File.Exists(velopackDll))
    {
      InitializeLog(productRoot);
      ReportRepair("随包 Velopack 更新组件缺失：" + velopackDll,
          args.Contains(SelfTestPrerequisites, StringComparer.Ordinal),
          BootstrapperArtifactSmoke.IsRequested());
      return LayoutInvalid;
    }

    try
    {
      RunVelopack(productRoot);
    }
    catch (Exception error) when (error is BadImageFormatException or FileLoadException or FileNotFoundException)
    {
      InitializeLog(productRoot);
      ReportRepair("随包 Velopack 更新组件无法加载：" + velopackDll + "\n" + error.Message,
          args.Contains(SelfTestPrerequisites, StringComparer.Ordinal),
          BootstrapperArtifactSmoke.IsRequested());
      return LayoutInvalid;
    }
    PortableProductRoots roots = InitializeLog(productRoot);
    try
    {
      return Launch(args, roots);
    }
    catch (Exception error)
    {
      BootstrapperLog.Error("Unexpected bootstrapper failure: " + error);
      throw;
    }
  }

  // Keep Velopack out of the entry point's JIT so a missing DLL can be reported.
  [MethodImpl(MethodImplOptions.NoInlining)]
  private static void RunVelopack(string productRoot) =>
      VelopackApp.Build()
          .OnAfterUpdateFastCallback(_ => LegacyVelopackStateMigration.Migrate(productRoot))
          .Run();

  private static PortableProductRoots InitializeLog(string productRoot)
  {
    PortableProductRoots roots = PortableProductRoots.Resolve(productRoot);
    BootstrapperLog.Initialize(Path.Combine(roots.InstallRoot, "state", "logs"));
    return roots;
  }

  private static int Launch(string[] args, PortableProductRoots roots)
  {
    bool selfTest = args.Contains(SelfTestPrerequisites, StringComparer.Ordinal);
    bool artifactSmoke = BootstrapperArtifactSmoke.IsRequested();
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
          BundledWebView2RuntimeError(layout),
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

    if (!GetBinaryTypeW(appPath, out uint binaryType) || binaryType != Windows64BitBinary)
    {
      ReportRepair("app.invalid-image: 应用入口损坏或不是 Windows 64 位程序：" + appPath, selfTest, artifactSmoke);
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
    try
    {
      Process.Start(startInfo);
    }
    catch (Win32Exception error)
    {
      ReportRepair("app.start-failed: 无法启动随包应用：" + appPath + "\n" + error.Message, selfTest, artifactSmoke);
      return AppMissing;
    }
    BootstrapperLog.Info($"Launched WinUI app: {appPath} {arguments}");
    return 0;
  }

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetBinaryTypeW(string applicationName, out uint binaryType);

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

  private static string? BundledWebView2RuntimeError(ResolvedProductLayout layout)
  {
    try
    {
      ResolvedProductLayout.ReadWebView2ExecutableVersion(
          Path.Combine(layout.WebView2RuntimeRoot, "msedgewebview2.exe"),
          WebView2LockVersion.LockedRuntimeVersion);
      return null;
    }
    catch (InvalidDataException error)
    {
      return error.Message;
    }
  }

  private static string Quote(string value) =>
      "\"" + value.Replace("\"", "\\\"") + "\"";

}

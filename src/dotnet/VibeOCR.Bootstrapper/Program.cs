using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using VibeOCR.ProductLayout;
using Windows.ApplicationModel;
using Windows.Management.Deployment;
using Windows.System;
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
          HasDotNetDesktop10() ? null : ".NET Desktop Runtime 10 x64",
          HasWindowsAppRuntime22() ? null : "Windows App Runtime 2.2 x64",
          HasWebView2(layout) ? null : "Microsoft Edge WebView2 Evergreen Runtime",
          HasBoundRuntimeAssets(layout) ? null : "VibeOCR bound Runtime assets",
        })
        .Where(item => item != null).Select(item => item!).ToArray();
    if (missing.Length > 0)
    {
      Report("VibeOCR prerequisites require repair:");
      foreach (string item in missing)
      {
        Report("- " + item);
      }

      Report("No component was downloaded or modified.");
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
      File.Exists(layout.RuntimeInstaller);

  private static bool HasDotNetDesktop10()
  {
    string root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "dotnet",
        "shared",
        "Microsoft.WindowsDesktop.App");
    return Directory.Exists(root) && Directory.EnumerateDirectories(root)
        .Select(Path.GetFileName)
        .Any(value => Version.TryParse(value, out Version version) && version.Major >= 10);
  }

  private static bool HasWindowsAppRuntime22()
  {
    try
    {
      return new PackageManager().FindPackagesForUser(string.Empty).Any(package =>
          IsWindowsAppRuntime22(package.Id.Name, package.Id.Version) &&
          (package.Id.Architecture == ProcessorArchitecture.X64 ||
              package.Id.Architecture == ProcessorArchitecture.Neutral) &&
          package.Status.VerifyIsOK());
    }
    catch
    {
      return false;
    }
  }

  private static bool IsWindowsAppRuntime22(string name, PackageVersion version) =>
        (name.Equals("Microsoft.WindowsAppRuntime.2", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Microsoft.WindowsAppRuntime.CBS.2", StringComparison.OrdinalIgnoreCase)) &&
      (version.Major > 2 || (version.Major == 2 && version.Minor >= 2));

  private static bool HasWebView2(ResolvedProductLayout layout)
  {
    string loaderPath = Path.Combine(layout.AppRoot, "WebView2Loader.dll");
    if (!File.Exists(loaderPath))
    {
      return false;
    }

    IntPtr module = LoadLibrary(loaderPath);
    if (module == IntPtr.Zero)
    {
      return false;
    }

    try
    {
      IntPtr entryPoint = GetProcAddress(
          module,
          "GetAvailableCoreWebView2BrowserVersionString");
      if (entryPoint == IntPtr.Zero)
      {
        return false;
      }

      var getVersion = Marshal.GetDelegateForFunctionPointer<
          GetAvailableCoreWebView2BrowserVersionString>(entryPoint);
      int result = getVersion(null, out IntPtr versionInfo);
      try
      {
        if (result < 0 || versionInfo == IntPtr.Zero)
        {
          return false;
        }

        return !string.IsNullOrWhiteSpace(Marshal.PtrToStringUni(versionInfo));
      }
      finally
      {
        if (versionInfo != IntPtr.Zero)
        {
          Marshal.FreeCoTaskMem(versionInfo);
        }
      }
    }
    catch
    {
      return false;
    }
    finally
    {
      FreeLibrary(module);
    }
  }

  [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
  private delegate int GetAvailableCoreWebView2BrowserVersionString(
      string? browserExecutableFolder,
      out IntPtr versionInfo);

  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern IntPtr LoadLibrary(string fileName);

  [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
  private static extern IntPtr GetProcAddress(IntPtr module, string procedureName);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool FreeLibrary(IntPtr module);

  private static string Quote(string value) =>
      "\"" + value.Replace("\"", "\\\"") + "\"";

}

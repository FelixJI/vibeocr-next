using Microsoft.Web.WebView2.Core;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using VibeOCR.ProductLayout;
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

    [STAThread]
    private static int Main(string[] args)
    {
        VelopackApp.Build().Run();
        string installRoot = AppDomain.CurrentDomain.BaseDirectory;
        ResolvedProductLayout layout;
        try
        {
            layout = ResolvedProductLayout.Open(installRoot);
        }
        catch (InvalidDataException error)
        {
            Console.Error.WriteLine(error.Message);
            return LayoutInvalid;
        }
        string appPath = layout.AppEntry;
        string profile = ReadOption(args, "--profile") ?? "production";
        if (profile is not ("production" or "winui-dev"))
        {
            Console.Error.WriteLine("Unsupported profile: " + profile);
            return InvalidArguments;
        }
        string[] missing = new string?[]
        {
            HasDotNetDesktop10() ? null : ".NET Desktop Runtime 10 x64",
            HasWindowsAppRuntime22() ? null : "Windows App Runtime 2.2 x64",
            HasWebView2() ? null : "Microsoft Edge WebView2 Evergreen Runtime",
            HasBoundRuntimeAssets(layout) ? null : "VibeOCR bound Runtime assets",
        }.Where(item => item != null).Select(item => item!).ToArray();
        if (missing.Length > 0)
        {
            Console.Error.WriteLine("VibeOCR prerequisites require repair:");
            foreach (string item in missing)
            {
                Console.Error.WriteLine("- " + item);
            }

            Console.Error.WriteLine("No component was downloaded or modified.");
            return PrerequisiteMissing;
        }

        if (!File.Exists(appPath))
        {
            Console.Error.WriteLine("WinUI application is missing: " + appPath);
            return AppMissing;
        }

        string arguments = "--profile " + Quote(profile) +
            " --install-root " + Quote(layout.InstallRoot);
        var startInfo = new ProcessStartInfo
        {
            FileName = appPath,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(appPath),
            UseShellExecute = true,
        };
        Process.Start(startInfo);
        return 0;
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
                package.Id.Name.Equals("Microsoft.WindowsAppRuntime.2.2", StringComparison.OrdinalIgnoreCase) &&
                (package.Id.Architecture == ProcessorArchitecture.X64 ||
                    package.Id.Architecture == ProcessorArchitecture.Neutral) &&
                package.Status.VerifyIsOK());
        }
        catch
        {
            return false;
        }
    }

    private static bool HasWebView2()
    {
        try
        {
            return !string.IsNullOrWhiteSpace(
                CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch
        {
            return false;
        }
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\"", "\\\"") + "\"";

}

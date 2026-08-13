using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace VibeOCR.Platform.Tests;

internal static class WindowsJobObjectTestProcess
{
  private const string ParentHelperArgument = "--job-object-parent-helper";

  [ModuleInitializer]
  internal static void RunParentHelper()
  {
    if (!Environment.GetCommandLineArgs().Contains(
          ParentHelperArgument,
          StringComparer.Ordinal))
    {
      return;
    }

    string ping = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.System),
      "ping.exe");
    using var child = new Process
    {
      StartInfo = new ProcessStartInfo
      {
        FileName = ping,
        WorkingDirectory = Environment.CurrentDirectory,
        UseShellExecute = false,
      },
    };
    child.StartInfo.ArgumentList.Add("127.0.0.1");
    child.StartInfo.ArgumentList.Add("-t");
    if (!child.Start())
    {
      Environment.Exit(1);
    }

    Console.WriteLine(child.Id);
    Console.Out.Flush();
    child.WaitForExit();
    Environment.Exit(child.ExitCode);
  }
}

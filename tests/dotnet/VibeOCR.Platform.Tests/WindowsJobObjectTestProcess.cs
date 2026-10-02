using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;

namespace VibeOCR.Platform.Tests;

internal static class WindowsJobObjectTestProcess
{
  private const string ParentHelperArgument = "--job-object-parent-helper";
  private const string RootHelperArgument = "--job-object-root-helper";

  [ModuleInitializer]
  internal static void RunHelperModes()
  {
    string[] arguments = Environment.GetCommandLineArgs();
    if (arguments.Contains(RootHelperArgument, StringComparer.Ordinal))
    {
      RunRootHelper();
    }
    else if (arguments.Contains(ParentHelperArgument, StringComparer.Ordinal))
    {
      RunParentHelper();
    }
  }

  // Root fixture mode: starts the parent-helper middle process with a
  // dedicated stdout pipe and reports both fixture ids serially on its own
  // stdout, so every ready pipe has exactly one writer. The ping grandchild
  // output is drained inside the middle helper and never reaches the pipes.
  private static void RunRootHelper()
  {
    using var middle = new Process
    {
      StartInfo = new ProcessStartInfo
      {
        FileName = Path.Combine(AppContext.BaseDirectory, "VibeOCR.Platform.Tests.exe"),
        WorkingDirectory = Environment.CurrentDirectory,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
      },
    };
    middle.StartInfo.ArgumentList.Add(ParentHelperArgument);
    var middleError = new ConcurrentQueue<string>();
    middle.ErrorDataReceived += (_, eventArgs) =>
    {
      if (eventArgs.Data is not null)
      {
        middleError.Enqueue(eventArgs.Data);
      }
    };
    if (!middle.Start())
    {
      Environment.Exit(1);
    }
    middle.BeginErrorReadLine();

    Console.WriteLine($"middle={middle.Id}");
    Console.Out.Flush();

    string? grandchildLine = middle.StandardOutput.ReadLine();
    if (!int.TryParse(grandchildLine, out int grandchildId))
    {
      middle.WaitForExit(milliseconds: 5_000);
      Console.Error.WriteLine(
        $"middle did not report a grandchild id; line={grandchildLine ?? "none"}; " +
        $"middle exited={middle.HasExited}" +
        (middle.HasExited ? $", code={middle.ExitCode}" : string.Empty) +
        $"; middle stderr={string.Join(Environment.NewLine, middleError)}");
      Console.Error.Flush();
      Environment.Exit(1);
    }

    Console.WriteLine($"grandchild={grandchildId}");
    Console.Out.Flush();
    middle.WaitForExit();
    Environment.Exit(middle.ExitCode);
  }

  private static void RunParentHelper()
  {
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
        RedirectStandardOutput = true,
        RedirectStandardError = true,
      },
    };
    child.StartInfo.ArgumentList.Add("127.0.0.1");
    child.StartInfo.ArgumentList.Add("-t");
    if (!child.Start())
    {
      Environment.Exit(1);
    }
    // Drain the grandchild output so only the ready line reaches the pipe.
    child.BeginOutputReadLine();
    child.BeginErrorReadLine();

    Console.WriteLine(child.Id);
    Console.Out.Flush();
    child.WaitForExit();
    Environment.Exit(child.ExitCode);
  }
}

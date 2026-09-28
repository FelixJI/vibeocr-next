using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using VibeOCR.Platform.Windows;
using Xunit;

namespace VibeOCR.Platform.Tests;

public sealed class SmartScreenCandidatesIntegrationTests
{
    public static bool InteractiveRunRequested =>
        OperatingSystem.IsWindows() && Environment.UserInteractive &&
        Environment.GetEnvironmentVariable("VIBEOCR_SMART_UIA_INTEGRATION") == "1";

    [Fact(
        Skip = "仅在显式 VIBEOCR_SMART_UIA_INTEGRATION=1 的交互桌面运行真实 UIA 合成窗口验证。",
        SkipUnless = nameof(InteractiveRunRequested))]
    [Trait("Category", "WindowsIntegration")]
    public async Task ExternalSyntheticWindowProvidesCurrentWindowAndControlPath()
    {
        Process? process = null;
        uint fixtureThread = 0;
        try
        {
            process = StartFixture();
            string? line;
            try
            {
                line = await process.StandardOutput.ReadLineAsync(
                        TestContext.Current.CancellationToken)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            }
            catch (TimeoutException error)
            {
                throw new InvalidOperationException(
                    "Synthetic fixture did not report geometry within 15 seconds.", error);
            }
            if (string.IsNullOrWhiteSpace(line))
            {
                string error = await process.StandardError.ReadToEndAsync(
                        TestContext.Current.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
                throw new InvalidOperationException(
                    $"Synthetic fixture exited before geometry: {error[..Math.Min(500, error.Length)]}");
            }
            Fixture fixture = Fixture.Parse(line!);
            fixtureThread = fixture.ThreadId;
            Assert.Equal(process.Id, fixture.ProcessId);
            foreach (nint handle in new[]
                { fixture.Root, fixture.Group, fixture.Button, fixture.Edit, fixture.Veil })
            {
                uint thread = GetWindowThreadProcessId(handle, out uint processId);
                Assert.Equal((uint)process.Id, processId);
                Assert.Equal(fixtureThread, thread);
            }

            PhysicalRectangle desktop = WithPerMonitorDpi(SmartScreenCandidates.CurrentDesktop);
            SmartScreenCandidates snapshot = WithPerMonitorDpi(
                () => SmartScreenCandidates.Capture(desktop));
            Assert.DoesNotContain(snapshot.Windows, candidate => candidate.Handle == fixture.Veil);
            PhysicalPoint buttonPoint = Center(fixture.ButtonBounds);
            SmartScreenCandidates.Window? hit = snapshot.Hit(buttonPoint);
            SmartScreenCandidates.Window? synthetic = snapshot.Windows.FirstOrDefault(
                candidate => candidate.Handle == fixture.Root);
            PhysicalRectangle? syntheticCurrent = WithPerMonitorDpi(
                () => SmartScreenCandidates.TryReadWindowBounds(fixture.Root));
            GetWindowThreadProcessId(hit?.Handle ?? 0, out uint hitProcessId);
            Assert.True(hit?.Handle == fixture.Root,
                $"Synthetic window was not the frozen topmost hit: " +
                $"syntheticPid={fixture.ProcessId}, root={fixture.Root}, " +
                $"nativeRoot={fixture.RootBounds}, nativeButton={fixture.ButtonBounds}, " +
                $"transparentLayer={fixture.Veil}, layerBounds={fixture.VeilBounds}, " +
                $"desktop={desktop}, point={buttonPoint}, rootVisible={IsWindowVisible(fixture.Root)}, " +
                $"snapshotHasRoot={synthetic is not null}, snapshotRootBounds={synthetic?.Bounds}, " +
                $"currentDwmRootBounds={syntheticCurrent}, hit={hit?.Handle}, " +
                $"hitBounds={hit?.Bounds}, hitPid={hitProcessId}, " +
                $"hitClass={WindowClass(hit?.Handle ?? 0)}.");
            SmartScreenCandidates.Window window = hit!;
            Assert.True(WithPerMonitorDpi(() => snapshot.IsCurrent(window, desktop,
                SmartScreenCandidates.TryReadWindowBounds)));

            // This is the first query in the test process: the production 200 ms UI budget
            // includes cold worker and COM startup. A timeout is a real integration failure.
            Stopwatch cold = Stopwatch.StartNew();
            var firstQuery = SmartControlQuery.Shared.QueryWithDiagnosticsAsync(
                window.Handle, buttonPoint, window.Bounds);
            IReadOnlyList<SmartControlCandidate> buttonPath =
                await firstQuery.Result.WaitAsync(TimeSpan.FromMilliseconds(200),
                    TestContext.Current.CancellationToken);
            cold.Stop();
            SmartControlQueryDiagnostic diagnostic = await firstQuery.Diagnostic;
            Assert.True(buttonPath.Count >= 2,
                $"First cold UIA query returned {buttonPath.Count} levels in " +
                $"{cold.ElapsedMilliseconds} ms; rootClass={WindowClass(fixture.Root)}, " +
                $"diagnostic={diagnostic}.");
            Assert.Equal(snapshot.ClipToCapture(fixture.GroupBounds, window),
                buttonPath[^2].Bounds);
            Assert.Equal(snapshot.ClipToCapture(fixture.ButtonBounds, window),
                buttonPath[^1].Bounds);
            Assert.Equal(snapshot.ToLocal(buttonPath[^1].Bounds),
                new PhysicalRectangle(
                    fixture.ButtonBounds.X - desktop.X,
                    fixture.ButtonBounds.Y - desktop.Y,
                    fixture.ButtonBounds.Width,
                    fixture.ButtonBounds.Height));

            PhysicalPoint editPoint = Center(fixture.EditBounds);
            IReadOnlyList<SmartControlCandidate> editPath =
                await SmartControlQuery.Shared.QueryAsync(
                    window.Handle, editPoint, window.Bounds)
                    .WaitAsync(TimeSpan.FromMilliseconds(200),
                        TestContext.Current.CancellationToken);
            Assert.True(editPath.Count >= 2);
            Assert.Equal(snapshot.ClipToCapture(fixture.GroupBounds, window),
                editPath[^2].Bounds);
            Assert.Equal(snapshot.ClipToCapture(fixture.EditBounds, window),
                editPath[^1].Bounds);

            Assert.True(WithPerMonitorDpi(() => MoveWindow(fixture.Button, 220, 50,
                fixture.ButtonBounds.Width, fixture.ButtonBounds.Height, true)));
            IReadOnlyList<SmartControlCandidate> afterMove =
                await SmartControlQuery.Shared.QueryAsync(
                    window.Handle, buttonPoint, window.Bounds)
                    .WaitAsync(TimeSpan.FromMilliseconds(200),
                        TestContext.Current.CancellationToken);
            Assert.False(SmartControlCandidate.SamePathThrough(
                buttonPath, afterMove, buttonPath.Count - 1));

            Assert.True(PostMessage(fixture.Button, 0x0010, 0, 0));
            await WaitUntilAsync(() => !IsWindow(fixture.Button), TimeSpan.FromSeconds(2));
            IReadOnlyList<SmartControlCandidate> afterClose =
                await SmartControlQuery.Shared.QueryAsync(
                    window.Handle, buttonPoint, window.Bounds)
                    .WaitAsync(TimeSpan.FromMilliseconds(200),
                        TestContext.Current.CancellationToken);
            Assert.False(SmartControlCandidate.SamePathThrough(
                buttonPath, afterClose, buttonPath.Count - 1));

            Assert.True(WithPerMonitorDpi(() => MoveWindow(fixture.Root,
                fixture.RootBounds.X + 40, fixture.RootBounds.Y + 40,
                fixture.RootBounds.Width, fixture.RootBounds.Height, true)));
            Assert.False(WithPerMonitorDpi(() => snapshot.IsCurrent(window,
                SmartScreenCandidates.CurrentDesktop(),
                SmartScreenCandidates.TryReadWindowBounds)));
        }
        finally
        {
            try
            {
                if (process is not null && !process.HasExited)
                {
                    if (fixtureThread != 0)
                        PostThreadMessage(fixtureThread, 0x0012, 0, 0);
                    if (!process.WaitForExit(2000))
                    {
                        process.Kill();
                        Assert.True(process.WaitForExit(5000),
                            "Only the spawned synthetic fixture was terminated, but it did not exit.");
                    }
                }
            }
            finally
            {
                process?.Dispose();
            }
        }
    }

    private static Process StartFixture()
    {
        string path = FindFixtureScript();
        var start = new ProcessStartInfo("pwsh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(path);
        return Process.Start(start) ??
            throw new InvalidOperationException("Synthetic fixture process did not start.");
    }

    private static string FindFixtureScript()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "tests", "dotnet",
                "VibeOCR.Platform.Tests", "smart_uia_fixture.ps1");
            if (File.Exists(path))
                return path;
        }
        throw new FileNotFoundException("Synthetic UIA fixture script is missing.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!condition() && elapsed.Elapsed < timeout)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(condition(), "Synthetic control did not close within the deadline.");
    }

    private static PhysicalPoint Center(PhysicalRectangle bounds) =>
        new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);

    private static string WindowClass(nint handle)
    {
        if (handle == 0)
            return "none";
        var buffer = new StringBuilder(128);
        return GetClassName(handle, buffer, buffer.Capacity) > 0
            ? buffer.ToString() : "unavailable";
    }

    private static T WithPerMonitorDpi<T>(Func<T> action)
    {
        nint previous = SetThreadDpiAwarenessContext((nint)(-4));
        if (previous == 0)
            throw new InvalidOperationException("Per-monitor-v2 test thread context is unavailable.");
        try
        {
            return action();
        }
        finally
        {
            SetThreadDpiAwarenessContext(previous);
        }
    }

    private sealed record Fixture(
        int ProcessId, uint ThreadId, nint Root, nint Group, nint Button, nint Edit,
        nint Veil,
        PhysicalRectangle RootBounds, PhysicalRectangle GroupBounds,
        PhysicalRectangle ButtonBounds, PhysicalRectangle EditBounds,
        PhysicalRectangle VeilBounds)
    {
        public static Fixture Parse(string line)
        {
            string[] pieces = line.Split('|');
            Assert.Equal(12, pieces.Length);
            static nint Handle(string value) => new(long.Parse(value, CultureInfo.InvariantCulture));
            static PhysicalRectangle Bounds(string value)
            {
                string[] numbers = value.Split(',');
                Assert.Equal(4, numbers.Length);
                int[] edges = numbers.Select(number =>
                    int.Parse(number, CultureInfo.InvariantCulture)).ToArray();
                return new(edges[0], edges[1], edges[2] - edges[0], edges[3] - edges[1]);
            }
            return new Fixture(
                int.Parse(pieces[0], CultureInfo.InvariantCulture),
                uint.Parse(pieces[1], CultureInfo.InvariantCulture),
                Handle(pieces[2]), Handle(pieces[3]), Handle(pieces[4]), Handle(pieces[5]),
                Handle(pieces[6]), Bounds(pieces[7]), Bounds(pieces[8]),
                Bounds(pieces[9]), Bounds(pieces[10]), Bounds(pieces[11]));
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder buffer, int capacity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveWindow(
        nint window, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message,
        nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);
}

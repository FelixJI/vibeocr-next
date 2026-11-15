using System.Diagnostics;
using System.Text.Json;
using VibeOCR.App.Services;
using VibeOCR.Platform.Bootstrap;

namespace VibeOCR.App;

// #216 capture-first-frame 冒烟专用启动握手（双门控：VIBEOCR_SELF_TEST_SMOKE
// ==capture-first-frame-e2e 且 VIBEOCR_CAPTURE_FIRST_FRAME_MONITOR 指向已验证
// layout.InstallRoot 的父目录，缺一即零等待）。冻结的工作集门要求首个 capture
// 周期起的每个采样都含本进程，而 CIM 登记 App 可能晚于首周期（实败：空 procs），
// 故在任何主窗/WebView 创建之前阻塞等待：采样器把“实际含本进程（PID+创建时间
// 按采样器微秒格核对）的完整样本”写盘后原子写出 root-first-sample.json。回执
// 缺失→有界超时；存在但损坏/身份不符→立即失败。绝不补造/过滤/补零样本。
public sealed partial class App
{
    private const string FirstFrameMonitorRootVariable = "VIBEOCR_CAPTURE_FIRST_FRAME_MONITOR";
    private static readonly TimeSpan FirstFrameRootSampleTimeout = TimeSpan.FromSeconds(30);

    internal static void WaitForFirstFrameRootSample(PortableLayout layout, TimeSpan? timeoutOverride = null)
    {
        if (Environment.GetEnvironmentVariable("VIBEOCR_SELF_TEST_SMOKE") != "capture-first-frame-e2e")
            return;
        string? configured = Environment.GetEnvironmentVariable(FirstFrameMonitorRootVariable);
        if (string.IsNullOrWhiteSpace(configured)) return;
        string expectedRoot = Path.GetDirectoryName(Path.GetFullPath(layout.InstallRoot))
            ?? throw new InvalidOperationException(
                $"Install root {layout.InstallRoot} has no parent directory.");
        if (!string.Equals(Path.GetFullPath(configured), expectedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"First-frame monitor root {configured} is not the parent of install root " +
                $"{layout.InstallRoot}; refusing to wait on an arbitrary path.");
        string receiptPath = Path.Combine(configured, "root-first-sample.json");
        // CIM CreationDate is microsecond-precision; native FILETIME has 100 ns
        // ticks - compare on the sampler's established microsecond grid.
        long expectedCreation;
        using (Process own = Process.GetCurrentProcess())
        {
            expectedCreation = own.StartTime.ToUniversalTime().ToFileTimeUtc();
        }
        int expectedPid = Environment.ProcessId;
        TimeSpan timeout = timeoutOverride ?? FirstFrameRootSampleTimeout;
        Stopwatch wait = Stopwatch.StartNew();
        while (!File.Exists(receiptPath))
        {
            if (wait.Elapsed > timeout)
                throw new TimeoutException(
                    $"First-frame monitor did not confirm a root-containing working-set sample " +
                    $"within {timeout.TotalSeconds:0}s ({receiptPath}); failing closed.");
            Thread.Sleep(50);
        }
        // 回执已存在：原子写入没有“半截合理”状态，损坏/身份不符立即失败。
        (int pid, long creation) = ReadReceiptIdentity(receiptPath);
        if (pid != expectedPid || creation / 10 != expectedCreation / 10)
            throw new InvalidOperationException(
                $"First-frame monitor receipt identity mismatch: pid={pid} creation={creation}, " +
                $"expected pid={expectedPid} creation={expectedCreation} ({receiptPath}).");
        AppLog.Info($"First-frame root sample receipt accepted after {wait.ElapsedMilliseconds}ms.");
    }

    private static (int Pid, long Creation) ReadReceiptIdentity(string receiptPath)
    {
        string json;
        long creation;
        try
        {
            json = File.ReadAllText(receiptPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"First-frame monitor receipt {receiptPath} is unreadable: {error.Message}", error);
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("pid", out JsonElement pidElement) ||
                !pidElement.TryGetInt32(out int pid) ||
                !root.TryGetProperty("creation_filetime", out JsonElement creationElement) ||
                creationElement.GetString() is not { } creationText ||
                !long.TryParse(creationText, out creation))
            {
                throw new InvalidOperationException(
                    $"First-frame monitor receipt {receiptPath} is structurally invalid.");
            }
            return (pid, creation);
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException(
                $"First-frame monitor receipt {receiptPath} is corrupt: {error.Message}", error);
        }
    }
}

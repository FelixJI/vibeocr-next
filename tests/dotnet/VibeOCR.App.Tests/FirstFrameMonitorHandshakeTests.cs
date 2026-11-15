using System.Diagnostics;
using System.Text.Json;
using VibeOCR.App;
using VibeOCR.Platform.Bootstrap;
using Xunit;

namespace VibeOCR.App.Tests;

// capture-first-frame 启动握手契约：仅该 smoke 且 monitor 根严格等于已验证
// install root 的父目录时等待；身份按采样器微秒格核对（同格接受、跨格或
// PID 不符立即失败）；回执存在但损坏/结构不全立即失败，仅缺失才有界超时。
// 环境变量为进程级状态，全部用例串行在同一测试内。
public sealed class FirstFrameMonitorHandshakeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"ff-handshake-{Guid.NewGuid():N}");
    private readonly string? _previousMonitor = Environment.GetEnvironmentVariable(
        "VIBEOCR_CAPTURE_FIRST_FRAME_MONITOR");
    private readonly string? _previousSmoke = Environment.GetEnvironmentVariable(
        "VIBEOCR_SELF_TEST_SMOKE");

    public FirstFrameMonitorHandshakeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VIBEOCR_CAPTURE_FIRST_FRAME_MONITOR", _previousMonitor);
        Environment.SetEnvironmentVariable("VIBEOCR_SELF_TEST_SMOKE", _previousSmoke);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static PortableLayout Layout(string installRoot) => new(
        "production", installRoot, installRoot, installRoot, installRoot,
        Path.Combine(installRoot, "config.json"), installRoot, installRoot,
        installRoot, installRoot, installRoot, installRoot, null);

    private static (int Pid, long Creation) OwnIdentity()
    {
        using Process own = Process.GetCurrentProcess();
        return (Environment.ProcessId, own.StartTime.ToUniversalTime().ToFileTimeUtc());
    }

    private void WriteReceipt(int pid, string creation) =>
        File.WriteAllText(Path.Combine(_root, "root-first-sample.json"), JsonSerializer.Serialize(new
        {
            pid,
            creation_filetime = creation,
            sample_started_at_ms = 1224,
            sample_at_ms = 1234,
            issued_at_ms = 1235,
        }));

    [Fact]
    public void HandshakeWaitsOnlyForThisSmokeWithTheBatchRootAndAnIdentityMatchedReceipt()
    {
        (int pid, long creation) = OwnIdentity();
        long grid = creation / 10 * 10; // 采样器微秒格（CIM µs vs 本地 100ns）。
        var layout = Layout(Path.Combine(_root, "candidate"));

        // 双门控：仅 MONITOR 变量（生产/其他 smoke）不等待。
        Environment.SetEnvironmentVariable("VIBEOCR_SELF_TEST_SMOKE", null);
        Environment.SetEnvironmentVariable("VIBEOCR_CAPTURE_FIRST_FRAME_MONITOR", _root);
        App.WaitForFirstFrameRootSample(layout, TimeSpan.FromSeconds(1));
        Environment.SetEnvironmentVariable("VIBEOCR_SELF_TEST_SMOKE", "capture-replacement-e2e");
        App.WaitForFirstFrameRootSample(layout, TimeSpan.FromSeconds(1));
        // 仅 smoke 变量不等待。
        Environment.SetEnvironmentVariable("VIBEOCR_SELF_TEST_SMOKE", "capture-first-frame-e2e");
        Environment.SetEnvironmentVariable("VIBEOCR_CAPTURE_FIRST_FRAME_MONITOR", null);
        App.WaitForFirstFrameRootSample(layout, TimeSpan.FromSeconds(1));

        // 双门控但 monitor 根不是 install root 的父目录：拒绝等待任意路径。
        Environment.SetEnvironmentVariable("VIBEOCR_CAPTURE_FIRST_FRAME_MONITOR", _root);
        Assert.Throws<InvalidOperationException>(() => App.WaitForFirstFrameRootSample(
            Layout(Path.Combine(_root, "deep", "candidate")), TimeSpan.FromSeconds(1)));

        // 回执缺失：有界超时（不补造证据）。
        Assert.Throws<TimeoutException>(
            () => App.WaitForFirstFrameRootSample(layout, TimeSpan.FromMilliseconds(150)));

        // 回执已存在但损坏/结构不全：立即失败，不重复读。
        Environment.SetEnvironmentVariable("VIBEOCR_SELF_TEST_SMOKE", "capture-first-frame-e2e");
        File.WriteAllText(Path.Combine(_root, "root-first-sample.json"), "{\"pid\":");
        Assert.Throws<InvalidOperationException>(
            () => App.WaitForFirstFrameRootSample(layout, TimeSpan.FromSeconds(1)));
        File.WriteAllText(Path.Combine(_root, "root-first-sample.json"), "{\"pid\": 1}");
        Assert.Throws<InvalidOperationException>(
            () => App.WaitForFirstFrameRootSample(layout, TimeSpan.FromSeconds(1)));

        // 身份不符立即失败：错 PID；跨微秒格创建时间（grid+10 = 下一格）。
        WriteReceipt(pid + 1, creation.ToString());
        Assert.Throws<InvalidOperationException>(
            () => App.WaitForFirstFrameRootSample(layout, TimeSpan.FromSeconds(1)));
        WriteReceipt(pid, (grid + 10).ToString());
        Assert.Throws<InvalidOperationException>(
            () => App.WaitForFirstFrameRootSample(layout, TimeSpan.FromSeconds(1)));

        // 同格（grid..grid+9 均在 creation 的微秒格内）与精确值都接受。
        WriteReceipt(pid, creation.ToString());
        App.WaitForFirstFrameRootSample(layout, TimeSpan.FromSeconds(2));
        WriteReceipt(pid, (grid + 9).ToString());
        App.WaitForFirstFrameRootSample(layout, TimeSpan.FromSeconds(2));
    }
}

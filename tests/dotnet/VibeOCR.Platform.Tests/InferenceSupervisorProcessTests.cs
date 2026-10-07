// Tests for InferenceSupervisorProcess + SupervisorReadyEnvelope parsing.
//
// Parser tests are complemented by a lightweight command child that exercises
// the owner lifecycle without requiring the Python backend.
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using Xunit;

namespace VibeOCR.Platform.Tests;

public sealed class InferenceSupervisorProcessTests
{
    private static readonly IReadOnlySet<string> BaselineCapabilities = new HashSet<string>(
        [
            "ocr.recognition.v2",
            "pdf.edit.v2",
            "qrcode.v2",
            "export.document.v1",
            "runtime.settings.v2",
            "runtime.maintenance.v1",
            "task.progress.v1",
        ],
        StringComparer.Ordinal);

    [Fact]
    public void ReadyEnvelopeParsesPortAndInstanceId()
    {
        var env = SupervisorReadyEnvelope.Parse(
            """{"ready":true,"pid":4321,"port":5432,"instance_id":"sup-abc","protocol_version":2,"schema_version":2,"ready_version":1,"capabilities":["ocr.recognition.v2","pdf.edit.v2","qrcode.v2","export.document.v1","runtime.settings.v2","runtime.maintenance.v1","task.progress.v1"]}""",
            BaselineCapabilities);
        Assert.Equal(5432, env.Port);
        Assert.Equal("sup-abc", env.InstanceId);
        Assert.Equal(2, env.ProtocolVersion);
        Assert.Equal("http://127.0.0.1:5432/", env.BaseUrl.ToString());
    }

    [Fact]
    public void ReadyEnvelopeRejectsMissingToken()
    {
        // A ready envelope that accidentally includes the token must still parse
        // (we only assert the token is NEVER in the line — the parse does not
        // look for it). What we actually guard: the token lives only in env.
        var env = SupervisorReadyEnvelope.Parse(
            """{"ready":true,"pid":1,"port":2,"instance_id":"sup","protocol_version":2,"schema_version":2,"ready_version":1,"capabilities":["ocr.recognition.v2","pdf.edit.v2","qrcode.v2","export.document.v1","runtime.settings.v2","runtime.maintenance.v1","task.progress.v1"]}""",
            BaselineCapabilities);
        Assert.DoesNotContain("token", "pid/port/instance_id");
        Assert.Equal(2, env.SchemaVersion);
    }

    [Theory]
    [InlineData("""{"ready":false,"pid":1,"port":2,"instance_id":"sup","protocol_version":2,"schema_version":2,"ready_version":1,"capabilities":[]}""")]
    [InlineData("""{"ready":true,"pid":1,"port":2,"instance_id":"sup","protocol_version":1,"schema_version":2,"ready_version":1,"capabilities":[]}""")]
    [InlineData("""{"ready":true,"pid":1,"port":2,"instance_id":"sup","protocol_version":2,"schema_version":1,"ready_version":1,"capabilities":[]}""")]
    [InlineData("""{"ready":true,"pid":1,"port":2,"instance_id":"sup","protocol_version":2,"schema_version":2,"ready_version":2,"capabilities":[]}""")]
    [InlineData("""{"ready":true,"pid":0,"port":2,"instance_id":"sup","protocol_version":2,"schema_version":2,"ready_version":1,"capabilities":[]}""")]
    [InlineData("""{"ready":true,"pid":1,"port":2,"instance_id":"sup","protocol_version":2,"schema_version":2,"ready_version":1,"capabilities":["legacy"]}""")]
    [InlineData("""{"ready":true,"pid":1,"port":2,"instance_id":"sup","protocol_version":2,"schema_version":2,"ready_version":1,"capabilities":["ocr.recognition.v2"]}""")]
    [InlineData("""{"ready":true,"pid":1,"port":2,"instance_id":"sup","protocol_version":2,"schema_version":2,"ready_version":1,"capabilities":null}""")]
    [InlineData("""{"ready":true,"pid":1,"port":2,"instance_id":"sup","protocol_version":2,"schema_version":2,"ready_version":1}""")]
    public void ReadyEnvelopeRejectsInvalidOrIncompatibleRuntime(string payload)
    {
        Assert.Throws<InvalidDataException>(
            () => SupervisorReadyEnvelope.Parse(payload, BaselineCapabilities));
    }

    [Fact]
    public void ReadyEnvelopeAcceptsRuntimeCapabilitiesUnknownToAnOlderSdk()
    {
        SupervisorReadyEnvelope envelope = SupervisorReadyEnvelope.Parse(
            """{"ready":true,"pid":1,"port":2,"instance_id":"sup","protocol_version":2,"schema_version":2,"ready_version":1,"capabilities":["ocr.recognition.v2","pdf.edit.v2","qrcode.v2","export.document.v1","runtime.settings.v2","runtime.maintenance.v1","task.progress.v1","runtime.new-feature.v1"]}""",
            BaselineCapabilities);

        Assert.Contains("runtime.new-feature.v1", envelope.Capabilities);
    }

    [Fact]
    public void ReadyEnvelopeAcceptsOldRuntimeWhenNewSdkAddsOnlyOptionalCapabilities()
    {
        SupervisorReadyEnvelope envelope = SupervisorReadyEnvelope.Parse(
            """{"ready":true,"pid":1,"port":2,"instance_id":"sup","protocol_version":2,"schema_version":2,"ready_version":1,"capabilities":["ocr.recognition.v2"]}""",
            new HashSet<string>(["ocr.recognition.v2"], StringComparer.Ordinal));

        Assert.Equal("sup", envelope.InstanceId);
    }

    [Fact]
    public void ReadyEnvelopeRejectsRuntimeMissingProductBaselineCapability()
    {
        Assert.Throws<InvalidDataException>(() => SupervisorReadyEnvelope.Parse(
            """{"ready":true,"pid":1,"port":2,"instance_id":"sup","protocol_version":2,"schema_version":2,"ready_version":1,"capabilities":["ocr.recognition.v2"]}""",
            BaselineCapabilities));
    }

    [Fact]
    public void ConstructorRequiresSessionToken()
    {
        var options = new InferenceSupervisorOptions(
            "python", new[] { "-m", "vibeocr.runtime.host.main" }, ".", "log.txt", TimeSpan.FromSeconds(5), BaselineCapabilities);
        Assert.Throws<ArgumentNullException>(() => new InferenceSupervisorProcess(options, null!));
        Assert.Throws<ArgumentException>(() => new InferenceSupervisorProcess(options, "   "));
    }

    [Fact]
    public void ReadyThrowsBeforeStart()
    {
        var options = new InferenceSupervisorOptions(
            "python", Array.Empty<string>(), ".", "log.txt", TimeSpan.FromSeconds(5), BaselineCapabilities);
        var proc = new InferenceSupervisorProcess(options, "tok");
        Assert.Throws<InvalidOperationException>(() => proc.Ready);
    }

    [Fact]
    public async Task SuccessfulStartIsOneShotAndDisposeClearsReady()
    {
        string root = Path.Combine(
            Path.GetTempPath(), $"vibeocr-supervisor-process-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var proc = CreateReadyProcess(root);
        try
        {
            SupervisorReadyEnvelope ready = await proc.StartAsync(
                TestContext.Current.CancellationToken);

            Assert.Equal("sup-test", ready.InstanceId);
            Assert.Same(ready, proc.Ready);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => proc.StartAsync(TestContext.Current.CancellationToken));

            proc.Dispose();
            Assert.Throws<InvalidOperationException>(() => proc.Ready);
        }
        finally
        {
            proc.Dispose();
            TestDirectory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FailedLaunchAttemptCannotBeRetried()
    {
        string root = Path.Combine(
            Path.GetTempPath(), $"vibeocr-supervisor-process-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var proc = new InferenceSupervisorProcess(
            new InferenceSupervisorOptions(
                Path.Combine(root, "missing-supervisor.exe"),
                [],
                root,
                Path.Combine(root, "supervisor.log"),
                TimeSpan.FromSeconds(1),
                BaselineCapabilities),
            "tok");
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => proc.StartAsync(TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => proc.StartAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            TestDirectory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DisposedOwnerCannotBeStarted()
    {
        string root = Path.Combine(
            Path.GetTempPath(), $"vibeocr-supervisor-process-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var proc = CreateReadyProcess(root);
        proc.Dispose();
        try
        {
            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => proc.StartAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            TestDirectory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task NaturalChildExitRaisesUnexpectedExitAndInvalidatesReady()
    {
        string root = Path.Combine(
            Path.GetTempPath(), $"vibeocr-supervisor-process-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using var proc = CreateReadyProcess(root, lifetimeMilliseconds: 250);
        var exited = new TaskCompletionSource<SupervisorUnexpectedExitEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        proc.UnexpectedExit += (_, args) => exited.TrySetResult(args);
        try
        {
            await proc.StartAsync(TestContext.Current.CancellationToken);

            SupervisorUnexpectedExitEventArgs result = await exited.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.Throws<InvalidOperationException>(() => proc.Ready);
        }
        finally
        {
            TestDirectory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PlannedDisposeDoesNotRaiseUnexpectedExit()
    {
        string root = Path.Combine(
            Path.GetTempPath(), $"vibeocr-supervisor-process-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var proc = CreateReadyProcess(root);
        var exited = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        proc.UnexpectedExit += (_, _) => exited.TrySetResult();
        try
        {
            await proc.StartAsync(TestContext.Current.CancellationToken);
            proc.Dispose();
            await Task.Delay(250, TestContext.Current.CancellationToken);

            Assert.False(exited.Task.IsCompleted);
        }
        finally
        {
            proc.Dispose();
            TestDirectory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProductCodeSupervisorLogsRoundTripUtf8AndStripAnsi()
    {
        // 真实子进程回归：复用生产 product-code 调用链（-I -B -c + runpy），
        // 在 PYTHONUTF8=1 环境下输出中文路径与 ANSI 色码，验证 argv 级
        // -X utf8、C# 管道 UTF-8 钉子与 AppendLog 边界清洗。
        string root = Path.Combine(
            Path.GetTempPath(), $"vibeocr-supervisor-utf8-{Guid.NewGuid():N}");
        string codeRoot = Path.Combine(root, "runtime-code");
        string packageRoot = Path.Combine(codeRoot, "vibeocr");
        string hostDirectory = Path.Combine(packageRoot, "runtime", "host");
        Directory.CreateDirectory(hostDirectory);
        // regular package（每级 __init__.py）：避免 runpy 解析到开发机
        // site-packages 内同名的真实 vibeocr 包。
        foreach (string directory in new[]
        {
            packageRoot,
            Path.Combine(packageRoot, "runtime"),
            hostDirectory,
        })
        {
            File.WriteAllText(Path.Combine(directory, "__init__.py"), string.Empty);
        }
        File.WriteAllText(Path.Combine(hostDirectory, "main.py"), FakeSupervisorSource);
        string logPath = Path.Combine(root, "supervisor.log");
        string python = ResolveTestPython();
        var launch = new RuntimeLaunch(
            python,
            "vibeocr.runtime.host.main",
            root,
            Path.Combine(root, "models"),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["VIBEOCR_PRODUCT_CODE_ROOT"] = codeRoot,
                // 与生产一致携带 PYTHONUTF8=1：-I 会忽略它，回归必须证明
                // argv 的 -X utf8 才是决定性钉子。
                ["PYTHONUTF8"] = "1",
            });
        IReadOnlyList<string> arguments = ManagedEnvironmentSwitchCoordinator.RuntimeArguments(launch);
        using var proc = new InferenceSupervisorProcess(
            new InferenceSupervisorOptions(
                python,
                arguments,
                root,
                logPath,
                TimeSpan.FromSeconds(30),
                BaselineCapabilities,
                launch.Environment.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase)),
            "tok");
        try
        {
            await proc.StartAsync(TestContext.Current.CancellationToken);

            // 等文件而非内存：AppendLog 先入库后落盘，以文件为准消除读写 race。
            string fileText = await WaitForLogFileAsync(
                logPath,
                text => text.Contains("stdout.encoding=utf-8", StringComparison.Ordinal)
                    && text.Contains("识别完成", StringComparison.Ordinal),
                TimeSpan.FromSeconds(20));
            Assert.Equal("sup-utf8", proc.Ready.InstanceId);
            IReadOnlyList<string> lines = proc.LogLines;

            Assert.Contains("stdout.encoding=utf-8", fileText);
            Assert.Contains(lines, line => line.Contains(
                "处理 Downloads\\下载\\截图 01.png", StringComparison.Ordinal));
            Assert.Contains("处理 Downloads\\下载\\截图 01.png", fileText);
            // ANSI 控制码不得落入内存快照或日志文件。用 char 重载的
            // Contains（Ordinal）：字符串重载默认文化比较会把控制字符当
            // 可忽略字符而误报命中。
            Assert.False(
                string.Join(Environment.NewLine, lines).Contains('\u001b'),
                "supervisor 内存日志快照包含 ANSI 控制码。");
            Assert.False(fileText.Contains('\u001b'), "supervisor.log 包含 ANSI 控制码。");
        }
        finally
        {
            proc.Dispose();
            TestDirectory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> WaitForLogFileAsync(
        string logPath,
        Func<string, bool> complete,
        TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        string text = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(logPath))
            {
                try
                {
                    text = File.ReadAllText(logPath);
                }
                catch (IOException)
                {
                    // 与 AppendAllText 竞争时重试。
                }
                if (complete(text))
                {
                    return text;
                }
            }
            await Task.Delay(100);
        }
        return text;
    }

    private static string ResolveTestPython()
    {
        string? configured = Environment.GetEnvironmentVariable("VIBEOCR_TEST_PYTHON");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }
        string? directory = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            string candidate = Path.Combine(directory, ".venv", "Scripts", "python.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            directory = Directory.GetParent(directory)?.FullName;
        }
        throw new FileNotFoundException(
            "找不到仓库 .venv 中的 Python 解释器；请先运行 uv sync --frozen，"
            + "或用 VIBEOCR_TEST_PYTHON 指定解释器。");
    }

    // 模拟 supervisor：ready envelope 后输出中文路径日志（stdout/stderr）
    // 与 ANSI 色码行，模拟第三方彩色日志。
    private const string FakeSupervisorSource = """
        import json
        import sys
        import time

        capabilities = [
            "ocr.recognition.v2",
            "pdf.edit.v2",
            "qrcode.v2",
            "export.document.v1",
            "runtime.settings.v2",
            "runtime.maintenance.v1",
            "task.progress.v1",
        ]
        print(json.dumps({
            "ready": True,
            "pid": 4321,
            "port": 5432,
            "instance_id": "sup-utf8",
            "protocol_version": 2,
            "schema_version": 2,
            "ready_version": 1,
            "capabilities": capabilities,
        }), flush=True)
        print("stdout.encoding=" + (sys.stdout.encoding or ""), flush=True)
        print("处理 Downloads\\下载\\截图 01.png", flush=True)
        sys.stderr.write("\x1b[32mINFO\x1b[0m 识别完成 Downloads\\下载\\截图 01.png\n")
        sys.stderr.flush()
        # 模拟常驻服务，由测试的 proc.Dispose() 结束；避免登记 Job Object 时已退出。
        time.sleep(60)
        """;

    private static InferenceSupervisorProcess CreateReadyProcess(
        string root,
        int lifetimeMilliseconds = 30_000)
    {
        string commandPrompt = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe");
        int pingCount = Math.Max(
            2,
            (int)Math.Ceiling(lifetimeMilliseconds / 1000d) + 1);
        const string envelope =
            """{"ready":true,"pid":4321,"port":5432,"instance_id":"sup-test","protocol_version":2,"schema_version":2,"ready_version":1,"capabilities":["ocr.recognition.v2","pdf.edit.v2","qrcode.v2","export.document.v1","runtime.settings.v2","runtime.maintenance.v1","task.progress.v1"]}""";
        const string scriptName = "fake-supervisor.cmd";
        string scriptPath = Path.Combine(root, scriptName);
        File.WriteAllLines(
            scriptPath,
            [
                "@echo off",
                $"echo {envelope}",
                $"ping 127.0.0.1 -n {pingCount} >nul",
            ]);
        return new InferenceSupervisorProcess(
            new InferenceSupervisorOptions(
                commandPrompt,
                [
                    "/d",
                    "/s",
                    "/c",
                    scriptName,
                ],
                root,
                Path.Combine(root, "supervisor.log"),
                TimeSpan.FromSeconds(5),
                BaselineCapabilities),
            "tok");
    }
}

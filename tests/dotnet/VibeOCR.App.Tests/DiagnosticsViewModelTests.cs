using System.Text.Json;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.ViewModels;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using VibeOCR.Platform.Bootstrap;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class DiagnosticsViewModelTests
{
  [Fact]
  public void MilestoneRecordingAndSnapshotReadsAreThreadSafe()
  {
    var viewModel = new DiagnosticsViewModel("test", new PrerequisiteReport([]));

    // 后台连接线程写入里程碑的同时，桥接读者反复取快照；快照隔离下
    // 不得抛出集合并发修改异常，且名称去重语义保持。
    Parallel.For(0, 400, index =>
    {
      viewModel.RecordMilestone($"T{index % 7}", TimeSpan.FromMilliseconds(index));
      Assert.True(viewModel.Milestones.Count is > 0 and <= 7);
    });

    Assert.Equal(7, viewModel.Milestones.Count);
    Assert.Equal(viewModel.Milestones.Count,
      viewModel.Milestones.Select(item => item.Name).Distinct().Count());
  }

  [Fact]
  public void SupervisorSnapshotReflectsLatestHealth()
  {
    var viewModel = new DiagnosticsViewModel("test", new PrerequisiteReport([]));
    Assert.Equal(SupervisorHealthState.NotReady, viewModel.Supervisor.State);

    viewModel.UpdateSupervisor(new SupervisorHealth(
      SupervisorHealthState.Ready, "sup-1", 2, null));
    Assert.Equal(SupervisorHealthState.Ready, viewModel.Supervisor.State);
    Assert.Equal("sup-1", viewModel.Supervisor.InstanceId);
  }

  [Fact]
  public void DeviceEvidenceFiltersToCurrentInstancePaddleDeviceLines()
  {
    int providerCalls = 0;
    var viewModel = new DiagnosticsViewModel(
      "test",
      new PrerequisiteReport([]),
      deviceEvidence: () =>
      {
        providerCalls++;
        return ("sup-1", (IReadOnlyList<string>)[
          "普通启动日志，不涉及设备",
          "[Paddle worker] 加载模型完成，无设备关键字",
          "[推理设备] 有设备关键字但没有 worker 来源标签",
          "[Paddle worker] [推理设备] CPU（未启用 GPU，VIBEOCR_USE_GPU != true）",
          "[Paddle worker] [GPU] GPU 可用性验证失败: boom，回退到 CPU",
        ]);
      });

    // 健康快照未指向任何实例（NotReady，InstanceId 为空）时不消费 provider。
    Assert.Empty(viewModel.DeviceEvidence);
    Assert.Equal(0, providerCalls);

    viewModel.UpdateSupervisor(new SupervisorHealth(
      SupervisorHealthState.Ready, "sup-1", 2, null));
    Assert.Equal([
      "[Paddle worker] [推理设备] CPU（未启用 GPU，VIBEOCR_USE_GPU != true）",
      "[Paddle worker] [GPU] GPU 可用性验证失败: boom，回退到 CPU",
    ], viewModel.DeviceEvidence);
    Assert.Equal(1, providerCalls);
  }

  [Fact]
  public void DeviceEvidenceCapsToLastTwentyMatchingLines()
  {
    var lines = Enumerable.Range(1, 30)
      .Select(index => $"[Paddle worker] [推理设备] 第 {index} 行")
      .ToList();
    var viewModel = new DiagnosticsViewModel(
      "test",
      new PrerequisiteReport([]),
      deviceEvidence: () => ("sup-1", lines));
    viewModel.UpdateSupervisor(new SupervisorHealth(
      SupervisorHealthState.Ready, "sup-1", 2, null));

    // 只保留匹配行的末尾 20 条，顺序不变。
    IReadOnlyList<string> evidence = viewModel.DeviceEvidence;
    Assert.Equal(20, evidence.Count);
    Assert.Equal("[Paddle worker] [推理设备] 第 11 行", evidence[0]);
    Assert.Equal("[Paddle worker] [推理设备] 第 30 行", evidence[^1]);
  }

  [Fact]
  public void DeviceEvidenceIsStaleInstanceSafe()
  {
    var viewModel = new DiagnosticsViewModel(
      "test",
      new PrerequisiteReport([]),
      deviceEvidence: () => ("sup-old", (IReadOnlyList<string>)[
        "[Paddle worker] [GPU] 旧实例日志，不得泄漏",
      ]));
    viewModel.UpdateSupervisor(new SupervisorHealth(
      SupervisorHealthState.Ready, "sup-1", 2, null));

    Assert.Empty(viewModel.DeviceEvidence);
  }

  [Fact]
  public void DeviceEvidenceIsEmptyWhenEitherInstanceIdIsMissing()
  {
    var staleProvider = new DiagnosticsViewModel(
      "test",
      new PrerequisiteReport([]),
      deviceEvidence: () => ((string?)null, (IReadOnlyList<string>)[
        "[Paddle worker] [推理设备] provider 实例缺失",
      ]));
    staleProvider.UpdateSupervisor(new SupervisorHealth(
      SupervisorHealthState.Ready, "sup-1", 2, null));
    Assert.Empty(staleProvider.DeviceEvidence);

    // 健康侧实例为空时同样为空，即使 provider 有实例日志。
    var noSupervisor = new DiagnosticsViewModel(
      "test",
      new PrerequisiteReport([]),
      deviceEvidence: () => ("sup-1", (IReadOnlyList<string>)[
        "[Paddle worker] [GPU] supervisor 实例缺失",
      ]));
    Assert.Empty(noSupervisor.DeviceEvidence);
  }

  [Fact]
  public void DeviceEvidenceWithoutProviderOrLinesIsEmpty()
  {
    var viewModel = new DiagnosticsViewModel("test", new PrerequisiteReport([]));
    viewModel.UpdateSupervisor(new SupervisorHealth(
      SupervisorHealthState.Ready, "sup-1", 2, null));

    // 未注入 provider（默认）时为空列表而不是 null。
    Assert.NotNull(viewModel.DeviceEvidence);
    Assert.Empty(viewModel.DeviceEvidence);

    var emptyProvider = new DiagnosticsViewModel(
      "test",
      new PrerequisiteReport([]),
      deviceEvidence: () => ("sup-1", (IReadOnlyList<string>)[]));
    emptyProvider.UpdateSupervisor(new SupervisorHealth(
      SupervisorHealthState.Ready, "sup-1", 2, null));
    Assert.Empty(emptyProvider.DeviceEvidence);
  }

  [Fact]
  public void DeviceEvidenceRedactsSecretsAndPaths()
  {
    var viewModel = new DiagnosticsViewModel(
      "test",
      new PrerequisiteReport([]),
      deviceEvidence: () => ("sup-1", (IReadOnlyList<string>)[
        "[Paddle worker] [GPU] token=abc123 " + new string('x', 500) + " 验证失败: path=C:\\Users\\felix\\models，回退到 CPU",
      ]));
    viewModel.UpdateSupervisor(new SupervisorHealth(
      SupervisorHealthState.Ready, "sup-1", 2, null));

    string line = Assert.Single(viewModel.DeviceEvidence);
    Assert.DoesNotContain("abc123", line);
    Assert.DoesNotContain("C:\\Users", line);
    Assert.Contains("<redacted>", line);
    Assert.Contains("[Paddle worker]", line);
    Assert.True(line.Length <= 401);
  }

  [Fact]
  public void NotifyDeviceEvidenceChangedRaisesOnlyDeviceEvidence()
  {
    var viewModel = new DiagnosticsViewModel("test", new PrerequisiteReport([]));
    var events = new List<string?>();
    viewModel.PropertyChanged += (_, args) => events.Add(args.PropertyName);

    viewModel.NotifyDeviceEvidenceChanged();

    Assert.Equal([nameof(DiagnosticsViewModel.DeviceEvidence)], events);
  }

  [Fact]
  public async Task ExportEmbedsDeviceEvidenceFromSameHealthSnapshot()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-diag-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
      int providerCalls = 0;
      var viewModel = new DiagnosticsViewModel(
        "test",
        new PrerequisiteReport([]),
        deviceEvidence: () =>
        {
          providerCalls++;
          return ("sup-1", (IReadOnlyList<string>)[
            "普通启动日志，不涉及设备",
            "[Paddle worker] [推理设备] CPU（未启用 GPU，VIBEOCR_USE_GPU != true）",
          ]);
        });
      viewModel.UpdateSupervisor(new SupervisorHealth(
        SupervisorHealthState.Ready, "sup-1", 2, null));

      string destination = Path.Combine(root, "diagnostics.json");
      await viewModel.ExportAsync(destination, TestContext.Current.CancellationToken);

      using JsonDocument document = JsonDocument.Parse(
        await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken));
      JsonElement supervisor = document.RootElement.GetProperty("supervisor");
      Assert.Equal("sup-1", supervisor.GetProperty("instance_id").GetString());
      JsonElement evidence = document.RootElement.GetProperty("device_evidence");
      Assert.Equal(
        "[Paddle worker] [推理设备] CPU（未启用 GPU，VIBEOCR_USE_GPU != true）",
        Assert.Single(evidence.GetProperty("lines").EnumerateArray()).GetString());
      Assert.Contains("不等于作业成功的实测设备",
        evidence.GetProperty("note").GetString());
      // 导出只读一次 health 和一次 provider，不做二次拉取或轮询。
      Assert.Equal(1, providerCalls);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task ExportDoesNotReuseStaleEvidenceAfterProviderRolls()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-diag-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
      int providerCalls = 0;
      var viewModel = new DiagnosticsViewModel(
        "test",
        new PrerequisiteReport([]),
        deviceEvidence: () => ++providerCalls == 1
          ? ("sup-1", (IReadOnlyList<string>)["[Paddle worker] [GPU] 首次实例日志"])
          : ("sup-2", (IReadOnlyList<string>)["[Paddle worker] [GPU] 新实例日志"]));
      viewModel.UpdateSupervisor(new SupervisorHealth(
        SupervisorHealthState.Ready, "sup-1", 2, null));

      Assert.Equal(["[Paddle worker] [GPU] 首次实例日志"], viewModel.DeviceEvidence);

      // 导出时 provider 已滚动到新实例：导出重新拉取一次，不复用属性读结果，
      // 两侧日志都不泄漏，supervisor 段仍是同一健康快照。
      string destination = Path.Combine(root, "diagnostics.json");
      await viewModel.ExportAsync(destination, TestContext.Current.CancellationToken);

      using JsonDocument document = JsonDocument.Parse(
        await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken));
      Assert.Equal("sup-1",
        document.RootElement.GetProperty("supervisor").GetProperty("instance_id").GetString());
      JsonElement evidence = document.RootElement.GetProperty("device_evidence");
      Assert.Empty(evidence.GetProperty("lines").EnumerateArray());
      Assert.Equal(2, providerCalls);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task ExportDeviceEvidenceEmptyForStaleInstance()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-diag-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
      var viewModel = new DiagnosticsViewModel(
        "test",
        new PrerequisiteReport([]),
        deviceEvidence: () => ("sup-old", (IReadOnlyList<string>)[
          "[Paddle worker] [GPU] 旧实例日志，导出也不得泄漏",
        ]));
      viewModel.UpdateSupervisor(new SupervisorHealth(
        SupervisorHealthState.Ready, "sup-1", 2, null));

      string destination = Path.Combine(root, "diagnostics.json");
      await viewModel.ExportAsync(destination, TestContext.Current.CancellationToken);

      using JsonDocument document = JsonDocument.Parse(
        await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken));
      JsonElement evidence = document.RootElement.GetProperty("device_evidence");
      Assert.Empty(evidence.GetProperty("lines").EnumerateArray());
      Assert.NotNull(evidence.GetProperty("note").GetString());
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task SupervisorHealthChangeBroadcastsExactlyOneDiagnosticsState()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-diag-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      var diagnostics = new DiagnosticsViewModel("test", new PrerequisiteReport([]));
      await using var handler = new DesktopWorkbenchCommandHandler(
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        diagnostics,
        broker,
        root,
        static () => 0,
        annotations);

      var broadcasts = new List<WorkbenchState>();
      handler.StateChanged += state => broadcasts.Add(state);

      // 一次 UpdateSupervisor 触发四个属性通知，但只有代表健康变更的
      // SupervisorStatus 允许广播一次诊断投影。
      diagnostics.UpdateSupervisor(new SupervisorHealth(
        SupervisorHealthState.Ready, "sup-1", 2, null));
      DiagnosticsWorkbenchState ready = Assert.IsType<DiagnosticsWorkbenchState>(
        Assert.Single(broadcasts));
      Assert.Equal("已就绪", ready.SupervisorStatus);
      Assert.True(ready.IsReady);

      broadcasts.Clear();
      // 里程碑写入不产生诊断广播（轨迹仅供导出/后续快照）。
      diagnostics.RecordMilestone("T5", TimeSpan.FromSeconds(1));
      Assert.Empty(broadcasts);

      diagnostics.UpdateSupervisor(new SupervisorHealth(
        SupervisorHealthState.Faulted, null, null, "连接失败"));
      var faulted = Assert.IsType<DiagnosticsWorkbenchState>(Assert.Single(broadcasts));
      Assert.Equal("连接失败", faulted.SupervisorStatus);

      broadcasts.Clear();
      // 健康变更本身不广播证据；只有真实日志事件经 Notify 重投影，
      // 且未接 provider 时证据为空。
      diagnostics.NotifyDeviceEvidenceChanged();
      var notified = Assert.IsType<DiagnosticsWorkbenchState>(Assert.Single(broadcasts));
      Assert.NotNull(notified.DeviceEvidence);
      Assert.Empty(notified.DeviceEvidence);
      await handler.DisposeAsync();
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task CopyDiagnosticsReusesRedactedExportDocumentThroughClipboardSeam()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-diag-copy-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
      var clipboard = new RecordingStructuredClipboard();
      var diagnostics = new DiagnosticsViewModel(
        "test",
        new PrerequisiteReport([]),
        deviceEvidence: () => ("sup-1", (IReadOnlyList<string>)[
          "[Paddle worker] [GPU] token=abc123 验证失败: path=C:\\Users\\felix\\models，回退到 CPU",
        ]));
      diagnostics.UpdateSupervisor(new SupervisorHealth(
        SupervisorHealthState.Ready, "sup-1", 2, "token=abc detail"));
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      await using var handler = new DesktopWorkbenchCommandHandler(
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        diagnostics,
        broker,
        root,
        static () => 0,
        annotations,
        structuredClipboard: clipboard);

      WorkbenchCommandOutcome outcome = await handler.ExecuteAsync(
        new CopyDiagnosticsCommand(),
        TestContext.Current.CancellationToken);

      // 复制复用导出的同一脱敏文档：剪贴板文本与导出文件一致，且不泄漏密钥或本地路径。
      Assert.Null(outcome.Error);
      Assert.NotNull(clipboard.Text);
      string destination = Path.Combine(root, "diagnostics.json");
      await diagnostics.ExportAsync(destination, TestContext.Current.CancellationToken);
      Assert.Equal(
        await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken),
        clipboard.Text);
      Assert.DoesNotContain("abc123", clipboard.Text);
      Assert.DoesNotContain("abc ", clipboard.Text);
      Assert.DoesNotContain("C:\\Users", clipboard.Text);
      Assert.Contains("<redacted>", clipboard.Text);
      using JsonDocument document = JsonDocument.Parse(clipboard.Text!);
      Assert.Equal(2, document.RootElement.GetProperty("schema_version").GetInt32());
      await handler.DisposeAsync();
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public void RedactCoversApiKeysBearerAuthorizationAndUrlCredentials()
  {
    var viewModel = new DiagnosticsViewModel(
      "test",
      new PrerequisiteReport([]),
      environmentInstallLog: () =>
      (IReadOnlyList<string>)[
        "2026-10-08T00:00:00Z [download/stdout] GET https://user:secretpw@mirror.example.com/simple/pkg",
        "2026-10-08T00:00:01Z [install/stderr] Authorization: Bearer eyJhbGciOi.abc.def",
        "2026-10-08T00:00:01Z [download/stdout] Bearer standalone-token-xyz",
        "2026-10-08T00:00:02Z [download/stdout] api_key=sk-live-123456 && token=tok-987 && apikey: quoted-secret",
        "2026-10-08T00:00:03Z [download/stdout] Authorization: Basic c3ludGhldGljLXVzZXI6cGFzc3dvcmQ=",
        "2026-10-08T00:00:04Z [install/stderr] Authorization=[redacted] b3RoZXItaGlzdG9yeS1yZXNpZHVl",
        "2026-10-08T00:00:05Z [install/stderr] authorization: <redacted> b3RoZXItaGlzdG9yeS1yZXNpZHVl",
        "2026-10-08T00:00:06Z [download/stdout] {\"Authorization\": \"Basic anNvbi1jcmVkZW50aWFs\"}",
        "2026-10-08T00:00:07Z [verify/stdout] Authorization: \"Basic cXVvdGVkLWNyZWQ=\"",
        "2026-10-08T00:00:08Z [verify/stdout] 使用路径 C:\\Users\\felji\\cache 验证完成",
      ]);

    using JsonDocument document = JsonDocument.Parse(viewModel.BuildRedactedExportJson());
    JsonElement lines = document.RootElement
      .GetProperty("environment_install").GetProperty("lines");
    string joined = string.Join("\n", lines.EnumerateArray().Select(item => item.GetString()));

    // 凭据值不进入文档；键名、scheme、主机与正常文本保留。
    Assert.DoesNotContain("secretpw", joined);
    Assert.DoesNotContain("eyJhbGciOi.abc.def", joined);
    Assert.DoesNotContain("standalone-token-xyz", joined);
    Assert.DoesNotContain("sk-live-123456", joined);
    Assert.DoesNotContain("tok-987", joined);
    Assert.DoesNotContain("quoted-secret", joined);
    // Basic 完整负载与历史半脱敏残值（含 JSON/引号形式）绝不残留。
    Assert.DoesNotContain("c3ludGhldGljLXVzZXI6cGFzc3dvcmQ", joined);
    Assert.DoesNotContain("b3RoZXItaGlzdG9yeS1yZXNpZHVl", joined);
    Assert.DoesNotContain("anNvbi1jcmVkZW50aWFs", joined);
    Assert.DoesNotContain("cXVvdGVkLWNyZWQ", joined);
    Assert.DoesNotContain("C:\\Users", joined);
    Assert.Contains("https://<redacted>@mirror.example.com", joined);
    Assert.Contains("Authorization: <redacted>", joined);
    Assert.Contains("Authorization=<redacted>", joined);
    Assert.Contains("authorization: <redacted>", joined);
    Assert.Contains("Bearer <redacted>", joined);
    Assert.Contains("api_key=<redacted>", joined);
    Assert.Contains("token=<redacted>", joined);
    Assert.Contains("apikey: <redacted>", joined);
    Assert.Contains("使用路径 <redacted>", joined);
  }

  [Fact]
  public async Task ExportAndCopyShareOneInstallLogSnapshotPerDocument()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-diag-install-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
      int providerCalls = 0;
      var viewModel = new DiagnosticsViewModel(
        "test",
        new PrerequisiteReport([]),
        environmentInstallLog: () =>
        {
          providerCalls++;
          return (IReadOnlyList<string>)[
            "2026-10-08T00:00:00Z [download/stdout] token=abc123 下载中",
          ];
        });

      string copied = viewModel.BuildRedactedExportJson();
      string destination = Path.Combine(root, "diagnostics.json");
      await viewModel.ExportAsync(destination, TestContext.Current.CancellationToken);

      // 复制与导出各自只读一次单一日志 getter：同一文档、同一脱敏，不把进度
      // 与日志两次独立读取拼成伪原子组合。
      Assert.Equal(2, providerCalls);
      using JsonDocument copyDocument = JsonDocument.Parse(copied);
      using JsonDocument exportDocument = JsonDocument.Parse(
        await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken));
      foreach (JsonDocument document in new[] { copyDocument, exportDocument })
      {
        JsonElement section = document.RootElement.GetProperty("environment_install");
        string line = Assert.Single(section.GetProperty("lines").EnumerateArray()).GetString()!;
        Assert.DoesNotContain("abc123", line);
        Assert.Contains("token=<redacted>", line);
        // 生命周期诚实性：不宣称退出后后台继续安装。
        Assert.Contains("后台", section.GetProperty("note").GetString()!);
      }
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public void InstallLogSectionIsEmptyWithoutProviderOrSnapshot()
  {
    var withoutProvider = new DiagnosticsViewModel("test", new PrerequisiteReport([]));
    using JsonDocument document = JsonDocument.Parse(withoutProvider.BuildRedactedExportJson());
    JsonElement section = document.RootElement.GetProperty("environment_install");
    Assert.Empty(section.GetProperty("lines").EnumerateArray());

    var emptyProvider = new DiagnosticsViewModel(
      "test", new PrerequisiteReport([]), environmentInstallLog: () => null);
    using JsonDocument emptyDocument = JsonDocument.Parse(emptyProvider.BuildRedactedExportJson());
    Assert.Empty(emptyDocument.RootElement
      .GetProperty("environment_install").GetProperty("lines").EnumerateArray());
  }

  private sealed class RecordingStructuredClipboard : IStructuredClipboardPlatform
  {
    public string? Text { get; private set; }

    public Task WriteTableAsync(string tsv, string html, CancellationToken cancellationToken)
    {
      throw new InvalidOperationException("Diagnostics copy must not write table formats.");
    }

    public Task WriteTextAsync(string text, CancellationToken cancellationToken)
    {
      Text = text;
      return Task.CompletedTask;
    }
  }
}

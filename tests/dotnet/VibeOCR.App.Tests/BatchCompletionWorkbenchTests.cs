using System.Text.Json;
using VibeOCR.App.Features.Batch;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Features.Settings;
using VibeOCR.App.Features.Shell;
using VibeOCR.App.Web;
using VibeOCR.App.ViewModels;
using VibeOCR.App.Workbench;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class BatchCompletionWorkbenchTests
{
  [Fact]
  public async Task SuccessfulBatchRunsPublishExactlyOneTerminalEachWithoutMailboxResync()
  {
    var client = new BatchRunClient();
    await using var harness = await CompletionHarness.CreateAsync(client);
    using var counter = new BatchTerminalCounter(harness.Handler);
    CancellationToken cancellationToken = TestContext.Current.CancellationToken;

    WorkbenchCommandReceipt firstAdded = await harness.AddDroppedFileAsync("first", cancellationToken);
    Assert.True(firstAdded.Ok);
    WorkbenchCommandReceipt firstStarted = await harness.StartAsync(cancellationToken);
    Assert.True(firstStarted.Ok);
    BatchWorkbenchState firstTerminal = await counter.FirstTerminal.WaitAsync(
      TimeSpan.FromSeconds(5), cancellationToken);

    // 成功终态保留：队列 1 项全部完成，且同一操作只有一次 terminal 发布。
    Assert.False(firstTerminal.IsRunning);
    Assert.Equal(1, firstTerminal.ItemCount);
    Assert.Equal(1, firstTerminal.CompletedCount);
    Assert.Equal(0, firstTerminal.FailedCount);
    Assert.Equal("batch.item.completed", Assert.Single(firstTerminal.Items!).StatusCode);
    Assert.Equal(1, client.Submissions);
    Assert.Equal(1, counter.Count);
    // 订阅端：第一次运行只产出一条 batch 终态，未触发 mailbox Resync。
    IReadOnlyList<WorkbenchStateEnvelope> firstDrained = await harness.DrainAsync(1, cancellationToken);
    Assert.Single(TerminalBatchStates(firstDrained));
    Assert.DoesNotContain(firstDrained, envelope =>
      envelope.Change is WorkbenchStateChange.Reset or WorkbenchStateChange.Ready);

    WorkbenchCommandReceipt secondAdded = await harness.AddDroppedFileAsync("second", cancellationToken);
    Assert.True(secondAdded.Ok);
    WorkbenchCommandReceipt secondStarted = await harness.StartAsync(cancellationToken);
    Assert.True(secondStarted.Ok);
    BatchWorkbenchState secondTerminal = await counter.NextTerminal(cancellationToken);
    Assert.False(secondTerminal.IsRunning);
    Assert.Equal(2, secondTerminal.ItemCount);
    Assert.Equal(2, secondTerminal.CompletedCount);
    Assert.Equal(2, client.Submissions);
    Assert.Equal(2, counter.Count);

    // 第二次运行同样只产出一条终态；mailbox 持续消费下没有重投影。
    IReadOnlyList<WorkbenchStateEnvelope> drained = await harness.DrainAsync(2, cancellationToken);
    Assert.Equal(2, TerminalBatchStates(drained).Count);
    Assert.DoesNotContain(drained, envelope =>
      envelope.Change is WorkbenchStateChange.Reset or WorkbenchStateChange.Ready);
  }

  [Fact]
  public async Task FailedBatchOutcomePublishesExactlyOneTerminalWithoutMailboxResync()
  {
    var client = new BatchRunClient { OutcomeState = ItemState.Failed };
    await using var harness = await CompletionHarness.CreateAsync(client);
    using var counter = new BatchTerminalCounter(harness.Handler);
    CancellationToken cancellationToken = TestContext.Current.CancellationToken;

    Assert.True((await harness.AddDroppedFileAsync("failed", cancellationToken)).Ok);
    Assert.True((await harness.StartAsync(cancellationToken)).Ok);
    BatchWorkbenchState terminal = await counter.FirstTerminal.WaitAsync(
      TimeSpan.FromSeconds(5), cancellationToken);

    // 失败终态保留：单文件失败计数进入终态，不重复发布。
    Assert.False(terminal.IsRunning);
    Assert.Equal(1, terminal.ItemCount);
    Assert.Equal(0, terminal.CompletedCount);
    Assert.Equal(1, terminal.FailedCount);
    Assert.Equal("batch.item.failed", Assert.Single(terminal.Items!).StatusCode);
    Assert.Equal(1, client.Submissions);
    Assert.Equal(1, counter.Count);

    IReadOnlyList<WorkbenchStateEnvelope> drained = await harness.DrainAsync(1, cancellationToken);
    Assert.Single(TerminalBatchStates(drained));
    Assert.DoesNotContain(drained, envelope =>
      envelope.Change is WorkbenchStateChange.Reset or WorkbenchStateChange.Ready);
  }

  [Fact]
  public async Task UserCancelledBatchPublishesExactlyOneTerminalWithoutMailboxResync()
  {
    var client = new BatchRunClient
    {
      ObserveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
    };
    await using var harness = await CompletionHarness.CreateAsync(client);
    using var counter = new BatchTerminalCounter(harness.Handler);
    CancellationToken cancellationToken = TestContext.Current.CancellationToken;

    Assert.True((await harness.AddDroppedFileAsync("cancelled", cancellationToken)).Ok);
    Assert.True((await harness.StartAsync(cancellationToken)).Ok);
    await client.WaitSubmittedAsync(cancellationToken);
    WorkbenchCommandReceipt cancelled = await harness.Application.ExecuteAsync(
      new WorkbenchCommandEnvelope(Guid.NewGuid(), new CancelBatchCommand()),
      cancellationToken);
    Assert.True(cancelled.Ok);
    BatchWorkbenchState terminal = await counter.FirstTerminal.WaitAsync(
      TimeSpan.FromSeconds(5), cancellationToken);

    // 取消终态保留：队列原样保留、条目为已取消，且只有一次 terminal。
    Assert.False(terminal.IsRunning);
    Assert.Equal(1, terminal.ItemCount);
    Assert.Equal("batch.item.cancelled", Assert.Single(terminal.Items!).StatusCode);
    Assert.Equal(0, terminal.CompletedCount);
    Assert.Equal(0, terminal.FailedCount);
    Assert.Equal(1, client.Submissions);
    Assert.Equal(1, counter.Count);

    IReadOnlyList<WorkbenchStateEnvelope> drained = await harness.DrainAsync(1, cancellationToken);
    Assert.Single(TerminalBatchStates(drained));
    Assert.DoesNotContain(drained, envelope =>
      envelope.Change is WorkbenchStateChange.Reset or WorkbenchStateChange.Ready);
  }

  [Fact]
  public async Task BatchInputReadFailurePublishesExactlyOneTerminalWithoutMailboxResync()
  {
    var client = new BatchRunClient();
    await using var harness = await CompletionHarness.CreateAsync(
      client, readFailure: new InvalidOperationException("批量输入读取失败"));
    using var counter = new BatchTerminalCounter(harness.Handler);
    CancellationToken cancellationToken = TestContext.Current.CancellationToken;

    Assert.True((await harness.AddDroppedFileAsync("broken", cancellationToken)).Ok);
    Assert.True((await harness.StartAsync(cancellationToken)).Ok);
    BatchWorkbenchState terminal = await counter.FirstTerminal.WaitAsync(
      TimeSpan.FromSeconds(5), cancellationToken);

    // 后台提交异常不吞终态：仍发布唯一 terminal（不重复、不 resync）。
    Assert.False(terminal.IsRunning);
    Assert.Equal(1, terminal.ItemCount);
    Assert.Equal(0, client.Submissions);
    Assert.Equal(1, counter.Count);

    IReadOnlyList<WorkbenchStateEnvelope> drained = await harness.DrainAsync(1, cancellationToken);
    Assert.Single(TerminalBatchStates(drained));
    Assert.DoesNotContain(drained, envelope =>
      envelope.Change is WorkbenchStateChange.Reset or WorkbenchStateChange.Ready);
  }

  private static IReadOnlyList<BatchWorkbenchState> TerminalBatchStates(
    IEnumerable<WorkbenchStateEnvelope> envelopes) =>
    envelopes.Select(envelope => envelope.State)
      .OfType<BatchWorkbenchState>()
      .Where(state => state.IsTerminal)
      .ToArray();

  private static string TempPng(string root, string name)
  {
    string path = Path.Combine(root, $"{name}.png");
    File.WriteAllBytes(path, [1, 2, 3, 4]);
    return path;
  }
  private sealed class CompletionHarness : IAsyncDisposable
  {
    private readonly WorkbenchResourceBroker broker;
    private readonly WorkbenchAnnotationStore annotations;
    private readonly string root;
    private readonly IAsyncEnumerator<WorkbenchStateEnvelope> subscription;
    private readonly CancellationTokenSource subscriptionEnd;
    private readonly List<WorkbenchStateEnvelope> drained = [];
    private ValueTask<bool>? firstMove;

    private CompletionHarness(
      DesktopWorkbenchCommandHandler handler,
      WorkbenchApplication application,
      BatchViewModel batch,
      WorkbenchResourceBroker broker,
      WorkbenchAnnotationStore annotations,
      string root,
      CancellationTokenSource subscriptionEnd,
      IAsyncEnumerator<WorkbenchStateEnvelope> subscription)
    {
      Handler = handler;
      Application = application;
      Batch = batch;
      this.broker = broker;
      this.annotations = annotations;
      this.root = root;
      this.subscriptionEnd = subscriptionEnd;
      this.subscription = subscription;
    }

    public DesktopWorkbenchCommandHandler Handler { get; }

    public WorkbenchApplication Application { get; }

    public BatchViewModel Batch { get; }

    public string Root => root;

    public static async Task<CompletionHarness> CreateAsync(
      BatchRunClient client,
      Exception? readFailure = null)
    {
      string root = Path.Combine(
        Path.GetTempPath(), $"vibeocr-batch-terminal-{Guid.NewGuid():N}");
      string resourceRoot = Path.Combine(root, "resources");
      Directory.CreateDirectory(resourceRoot);
      var broker = new WorkbenchResourceBroker(resourceRoot);
      var annotations = new WorkbenchAnnotationStore(resourceRoot);
      var batch = new BatchViewModel(client, new HarnessBatchFiles(readFailure));
      var handler = new DesktopWorkbenchCommandHandler(
        () => new RecognitionViewModel(client, new FixedInputService()),
        () => batch,
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        () => new SettingsViewModel(client),
        () => new ShellViewModel(new StubHotkeyRegistrar(), new StubStartupRegistrar()),
        static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker,
        resourceRoot,
        static () => 0,
        annotations);
      var application = new WorkbenchApplication(
        ["batch.add", "batch.run"], WorkbenchRoute.Recognition, handler);
      try
      {
        WorkbenchBootstrap bootstrap = await application.BootstrapAsync(
          TestContext.Current.CancellationToken);
        var end = new CancellationTokenSource();
        var harness = new CompletionHarness(
          handler, application, batch, broker, annotations, root, end,
          application.SubscribeAsync(bootstrap.Revision, end.Token)
            .GetAsyncEnumerator(end.Token));
        // async iterator 在首个 MoveNext 时才注册 mailbox。
        harness.firstMove = harness.subscription.MoveNextAsync();
        Assert.False(harness.firstMove.Value.IsCompleted,
          "The subscription must be parked on the mailbox signal after registration.");
        return harness;
      }
      catch
      {
        await application.DisposeAsync();
        broker.Dispose();
        annotations.Dispose();
        Directory.Delete(root, recursive: true);
        throw;
      }
    }

    public ValueTask<WorkbenchCommandReceipt> AddDroppedFileAsync(
      string name, CancellationToken cancellationToken) =>
      Application.ExecuteAsync(
        new WorkbenchCommandEnvelope(
          Guid.NewGuid(), new AddDroppedBatchFilesCommand([TempPng(root, name)])),
        cancellationToken);

    public ValueTask<WorkbenchCommandReceipt> StartAsync(CancellationToken cancellationToken) =>
      Application.ExecuteAsync(
        new WorkbenchCommandEnvelope(Guid.NewGuid(), new StartBatchCommand()),
        cancellationToken);

    // 消费已启动的首个 MoveNext，避免并发推进迭代器。
    public async Task<IReadOnlyList<WorkbenchStateEnvelope>> DrainAsync(
      int expectedTerminals, CancellationToken cancellationToken)
    {
      if (firstMove is not null)
      {
        ValueTask<bool> pending = firstMove.Value;
        firstMove = null;
        bool firstMoved = await pending.AsTask()
          .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.True(firstMoved,
          "The workbench subscription ended before the batch terminal arrived.");
        lock (drained) drained.Add(subscription.Current);
      }
      while (true)
      {
        lock (drained)
        {
          if (TerminalBatchStates(drained).Count >= expectedTerminals) break;
        }
        bool moved = await subscription.MoveNextAsync().AsTask()
          .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.True(moved, "The workbench subscription ended before the batch terminal arrived.");
        lock (drained) drained.Add(subscription.Current);
      }
      lock (drained) return [.. drained];
    }

    public async ValueTask DisposeAsync()
    {
      subscriptionEnd.Cancel();
      await subscription.DisposeAsync();
      await Application.DisposeAsync();
      await Handler.DisposeAsync();
      broker.Dispose();
      annotations.Dispose();
      Directory.Delete(root, recursive: true);
      subscriptionEnd.Dispose();
    }
  }

  private sealed class BatchTerminalCounter : IDisposable
  {
    private readonly DesktopWorkbenchCommandHandler handler;
    private readonly object gate = new();
    private readonly Queue<BatchWorkbenchState> terminals = new();
    private TaskCompletionSource<BatchWorkbenchState>? pending;

    public BatchTerminalCounter(DesktopWorkbenchCommandHandler handler)
    {
      this.handler = handler;
      handler.StateChanged += OnStateChanged;
    }

    public Task<BatchWorkbenchState> FirstTerminal
    {
      get
      {
        lock (gate) return NextTerminalLocked().Task;
      }
    }

    public int Count { get; private set; }

    public Task<BatchWorkbenchState> NextTerminal(CancellationToken cancellationToken)
    {
      lock (gate) return NextTerminalLocked().Task.WaitAsync(
        TimeSpan.FromSeconds(5), cancellationToken);
    }

    private TaskCompletionSource<BatchWorkbenchState> NextTerminalLocked()
    {
      if (terminals.Count > 0)
      {
        BatchWorkbenchState state = terminals.Dequeue();
        return TaskCompletionSourceFactory.FromResult(state);
      }
      pending ??= new TaskCompletionSource<BatchWorkbenchState>(
        TaskCreationOptions.RunContinuationsAsynchronously);
      return pending;
    }

    private void OnStateChanged(WorkbenchState state)
    {
      if (state is not BatchWorkbenchState { IsTerminal: true } terminal) return;
      lock (gate)
      {
        Count++;
        if (pending is not null)
        {
          pending.TrySetResult(terminal);
          pending = null;
        }
        else
        {
          terminals.Enqueue(terminal);
        }
      }
    }

    public void Dispose() => handler.StateChanged -= OnStateChanged;
  }

  private static class TaskCompletionSourceFactory
  {
    public static TaskCompletionSource<BatchWorkbenchState> FromResult(
      BatchWorkbenchState state)
    {
      var source = new TaskCompletionSource<BatchWorkbenchState>(
        TaskCreationOptions.RunContinuationsAsynchronously);
      source.TrySetResult(state);
      return source;
    }
  }

  private sealed class HarnessBatchFiles(Exception? readFailure) : IBatchFileSource
  {
    public Task<IReadOnlyList<string>> PickFilesAsync(CancellationToken cancellationToken) =>
      Task.FromResult<IReadOnlyList<string>>([]);

    public Task<(byte[] Data, string MediaType)> ReadAsync(
      string path, CancellationToken cancellationToken)
    {
      if (readFailure is not null) throw readFailure;
      return Task.FromResult((File.ReadAllBytes(path), "image/png"));
    }
  }

  private sealed class BatchRunClient : InferenceClientStub
  {
    private IReadOnlyList<JobItem> items = [];

    public Wire.Health Health { get; init; } = ModeHealth();

    public SettingsSnapshot Settings { get; init; } = new()
    {
      Extra = new Dictionary<string, JsonElement>
      {
        ["default_recognition_mode"] = JsonSerializer.SerializeToElement("windows_text"),
      },
    };

    public ItemState OutcomeState { get; init; } = ItemState.Succeeded;

    public TaskCompletionSource? ObserveGate { get; init; }

    public int Submissions { get; private set; }

    public override Task<JobRef> SubmitAsync(
      SubmitRequest request,
      IReadOnlyDictionary<string, SubmitUpload> uploads,
      CancellationToken cancellationToken)
    {
      Submissions++;
      items = request.Items.Select((item, index) => new JobItem
      {
        ItemId = $"it-{index}",
        ClientItemKey = item.ClientItemKey,
        Ordinal = item.Ordinal,
        DisplayName = item.DisplayName,
        State = ItemState.Queued,
      }).ToArray();
      return Task.FromResult(new JobRef { JobId = "job-batch-terminal", Items = items });
    }

    public override async Task<JobUpdate> ObserveAsync(
      string jobId, int afterSequence, CancellationToken cancellationToken)
    {
      if (ObserveGate is { } gate) await gate.Task.WaitAsync(cancellationToken);
      return new JobUpdate
      {
        Snapshot = new JobSnapshot
        {
          JobId = jobId,
          Kind = JobKind.Recognition,
          Priority = JobPriority.Background,
          State = JobState.Completed,
          Items = items,
        },
        Events = [],
        Outcomes = items.Select(item => new ItemOutcome
        {
          ItemId = item.ItemId,
          Attempt = 1,
          State = OutcomeState,
          PayloadType = "ocr.v1",
          Payload = OutcomeState is ItemState.Succeeded
            ? new Dictionary<string, JsonElement>
            {
              ["raw_text"] = JsonSerializer.SerializeToElement("批量识别结果"),
            }
            : null,
          ErrorCode = OutcomeState is ItemState.Succeeded ? null : "engine_failed",
        }).ToArray(),
        ThroughSequence = afterSequence + 1,
      };
    }

    public override Task<Wire.Health> GetHealthAsync(CancellationToken cancellationToken) =>
      Task.FromResult(Health);

    public override Task<SettingsSnapshot> GetSettingsAsync(CancellationToken cancellationToken) =>
      Task.FromResult(Settings);

    public override Task<SettingsSnapshot> UpdateSettingsAsync(
      SettingsSnapshot settings, CancellationToken cancellationToken) =>
      Task.FromResult(settings);

    public override Task<ResidencyStatus> GetResidencyAsync(
      CancellationToken cancellationToken) => Task.FromResult(new ResidencyStatus());

    public async Task WaitSubmittedAsync(CancellationToken cancellationToken)
    {
      long deadline = Environment.TickCount64 + 5000;
      while (Submissions == 0)
      {
        Assert.True(Environment.TickCount64 < deadline, "The batch job was never submitted.");
        await Task.Delay(10, cancellationToken);
      }
    }
  }

  private static Wire.Health ModeHealth() => new()
  {
    SchemaVersion = 2,
    InstanceId = "sup-batch-terminal",
    ProtocolVersion = 2,
    Ready = true,
    Draining = false,
    Capabilities =
    [
      RuntimeSelectionService.DefaultRecognitionModeCapability,
      RuntimeSelectionService.RecognitionModesCapability,
    ],
    CapabilityDescriptors =
    [
      new Wire.CapabilityDescriptor
      {
        Name = RuntimeSelectionService.RecognitionModesCapability,
        Lifecycle = "active",
        IntroducedIn = "2.8.0",
        DeprecatedIn = null,
        SunsetAt = null,
        Replacement = null,
        RecognitionModeCatalog = new Wire.RecognitionModeCatalog
        {
          Modes =
          [
            Mode(Wire.RecognitionModeId.RapidText,
              Wire.RecognitionModeAvailability.Ready),
            Mode(Wire.RecognitionModeId.WindowsText,
              Wire.RecognitionModeAvailability.Ready),
            Mode(Wire.RecognitionModeId.PaddleText,
              Wire.RecognitionModeAvailability.PreparationRequired),
            Mode(Wire.RecognitionModeId.PaddleStructure,
              Wire.RecognitionModeAvailability.PreparationRequired),
            Mode(Wire.RecognitionModeId.PaddleDocumentVl,
              Wire.RecognitionModeAvailability.PreparationRequired),
            Mode(Wire.RecognitionModeId.MineruDocument,
              Wire.RecognitionModeAvailability.PreparationRequired),
            Mode(Wire.RecognitionModeId.PaddleTable,
              Wire.RecognitionModeAvailability.PreparationRequired),
            Mode(Wire.RecognitionModeId.PaddleFormula,
              Wire.RecognitionModeAvailability.PreparationRequired),
          ],
        },
      },
    ],
  };

  private static Wire.RecognitionModeDescriptor Mode(
    Wire.RecognitionModeId id,
    Wire.RecognitionModeAvailability availability)
  {
    var (family, pipeline, engine, provisioning, lifecycle) = id switch
    {
      Wire.RecognitionModeId.RapidText => (Wire.RecognitionModeFamily.Text,
        Wire.ExecutionPipelineId.OCR, (Wire.OcrEngineId?)Wire.OcrEngineId.Rapidocr,
        Wire.RecognitionModeProvisioning.BaseRuntime,
        Wire.RecognitionModeLifecycleKind.Unmanaged),
      Wire.RecognitionModeId.WindowsText => (Wire.RecognitionModeFamily.Text,
        Wire.ExecutionPipelineId.OCR, (Wire.OcrEngineId?)Wire.OcrEngineId.Windows,
        Wire.RecognitionModeProvisioning.OperatingSystem,
        Wire.RecognitionModeLifecycleKind.Unmanaged),
      Wire.RecognitionModeId.PaddleText => (Wire.RecognitionModeFamily.Text,
        Wire.ExecutionPipelineId.OCR, (Wire.OcrEngineId?)Wire.OcrEngineId.Paddleocr,
        Wire.RecognitionModeProvisioning.AdvancedComponent,
        Wire.RecognitionModeLifecycleKind.ModelResidency),
      Wire.RecognitionModeId.PaddleStructure => (Wire.RecognitionModeFamily.Document,
        Wire.ExecutionPipelineId.PPStructureV3, (Wire.OcrEngineId?)null,
        Wire.RecognitionModeProvisioning.AdvancedComponent,
        Wire.RecognitionModeLifecycleKind.ModelResidency),
      Wire.RecognitionModeId.PaddleDocumentVl => (Wire.RecognitionModeFamily.Document,
        Wire.ExecutionPipelineId.PaddleOCRVL, (Wire.OcrEngineId?)null,
        Wire.RecognitionModeProvisioning.AdvancedComponent,
        Wire.RecognitionModeLifecycleKind.ModelResidency),
      Wire.RecognitionModeId.MineruDocument => (Wire.RecognitionModeFamily.Document,
        Wire.ExecutionPipelineId.MinerU, (Wire.OcrEngineId?)null,
        Wire.RecognitionModeProvisioning.AdvancedComponent,
        Wire.RecognitionModeLifecycleKind.ProcessKeepAlive),
      Wire.RecognitionModeId.PaddleTable => (Wire.RecognitionModeFamily.Specialized,
        Wire.ExecutionPipelineId.TABLERECOGNITION, (Wire.OcrEngineId?)null,
        Wire.RecognitionModeProvisioning.AdvancedComponent,
        Wire.RecognitionModeLifecycleKind.ModelResidency),
      Wire.RecognitionModeId.PaddleFormula => (Wire.RecognitionModeFamily.Specialized,
        Wire.ExecutionPipelineId.FORMULARECOGNITION, (Wire.OcrEngineId?)null,
        Wire.RecognitionModeProvisioning.AdvancedComponent,
        Wire.RecognitionModeLifecycleKind.ModelResidency),
      _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };
    bool notReady = availability != Wire.RecognitionModeAvailability.Ready;
    bool mineru = id == Wire.RecognitionModeId.MineruDocument;
    return new Wire.RecognitionModeDescriptor
    {
      Id = id,
      Family = family,
      PipelineId = pipeline,
      Engine = engine,
      Provisioning = provisioning,
      Availability = availability,
      ReasonCode = notReady ? "runtime_component_missing" : null,
      RequiredComponent = notReady ? (mineru ? "mineru-cpu" : "paddleocr-cpu") : null,
      SupportedOptions = [],
      Lifecycle = new Wire.RecognitionModeLifecycle
      {
        Kind = lifecycle,
        SupportsPreload = mineru,
        SupportsTtl = mineru,
        SupportsPinning = false,
        SupportsRelease = mineru,
      },
    };
  }

  private sealed class FixedInputService : IInputService
  {
    public Task<RecognitionInput?> PickFileAsync(CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(
        new RecognitionInput([1, 2, 3, 4], "image/png", "file.png", "file"));

    public Task<RecognitionInput?> ReadClipboardAsync(CancellationToken cancellationToken) =>
      PickFileAsync(cancellationToken);

    public Task<RecognitionInput?> CaptureScreenAsync(CancellationToken cancellationToken) =>
      PickFileAsync(cancellationToken);

    public Task<RecognitionInput?> ReadDroppedFileAsync(
      string path, CancellationToken cancellationToken) => PickFileAsync(cancellationToken);
  }

  private sealed class StubHotkeyRegistrar : IHotkeyRegistrar
  {
    public bool Register(string hotkey, out string? conflict)
    {
      conflict = null;
      return true;
    }

    public void Unregister() { }
  }

  private sealed class StubStartupRegistrar : IStartupRegistrar
  {
    public bool SetEnabled(bool enabled) => true;
  }
}

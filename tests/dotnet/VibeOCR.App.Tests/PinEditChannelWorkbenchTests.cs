using System.Text.Json;
using VibeOCR.App.Features.Configuration;
using VibeOCR.App.Features.Pins;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Features.Settings;
using VibeOCR.App.Features.Shell;
using VibeOCR.App.Inference;
using VibeOCR.App.ViewModels;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using Xunit;

namespace VibeOCR.App.Tests;

/// <summary>
/// #238 pinEdit 通道契约（C1/C2/C4）：贴图编辑会话只发布 pinEdit scope，
/// 不产出任何 recognition/imageEdit envelope；快照应用先落盘后 ack/事件；
/// 迟到会话拒绝且不写盘；编辑会话关闭不清历史投影；成功截图（含未钉图
/// 本地输出）按一次成功一条录入历史（默认关闭）。
/// </summary>
public sealed class PinEditChannelWorkbenchTests
{
  private static readonly byte[] Png = Convert.FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
  private const string ContributionBody =
    "{\"canvas\":{\"width\":1,\"height\":1},\"state\":{\"rotation\":0,\"marks\":[]},\"patches\":[]}";

  private sealed class TaggedInputService(ScreenshotSelectionAction? selectionAction = null)
    : IInputService
  {
    public Task<RecognitionInput?> PickFileAsync(CancellationToken cancellationToken) =>
        Task.FromResult<RecognitionInput?>(new RecognitionInput(Png, "image/png", "file.png", "file"));

    public Task<RecognitionInput?> ReadClipboardAsync(CancellationToken cancellationToken) =>
        PickFileAsync(cancellationToken);

    public Task<RecognitionInput?> CaptureScreenAsync(CancellationToken cancellationToken) =>
        PickFileAsync(cancellationToken);

    public Task<RecognitionInput?> CaptureScreenWithActionsAsync(
        ScreenshotSelectionActions? actions,
        CancellationToken cancellationToken)
        => Task.FromResult<RecognitionInput?>(selectionAction is null
          ? new RecognitionInput(Png, "image/png", "screenshot.png", "screenshot")
          : new RecognitionInput(Png, "image/png", "screenshot.png", "screenshot")
          {
            SelectionAction = selectionAction,
          });

    public Task<RecognitionInput?> CaptureScrollingScreenAsync(CancellationToken cancellationToken) =>
        CaptureScreenAsync(cancellationToken);

    public Task<RecognitionInput?> ReadDroppedFileAsync(
        string path, CancellationToken cancellationToken) =>
        Task.FromResult<RecognitionInput?>(null);
  }

  private sealed class CompletingInferenceClient : InferenceClientStub
  {
    public override Task<JobRef> SubmitAsync(
      SubmitRequest request,
      IReadOnlyDictionary<string, SubmitUpload> uploads,
      CancellationToken cancellationToken) => Task.FromResult(new JobRef
      {
        JobId = "job-pin-edit",
        Items =
        [
          new JobItem
          {
            ItemId = "it-0",
            ClientItemKey = request.Items[0].ClientItemKey,
            Ordinal = 0,
            DisplayName = request.Items[0].DisplayName,
            State = ItemState.Queued,
          },
        ],
      });

    public override Task<JobUpdate> ObserveAsync(
      string jobId,
      int afterSequence,
      CancellationToken cancellationToken) => Task.FromResult(new JobUpdate
      {
        Snapshot = new JobSnapshot
        {
          JobId = jobId,
          Kind = JobKind.Recognition,
          Priority = JobPriority.Interactive,
          State = JobState.Completed,
        },
        Events = Array.Empty<StageEvent>(),
        Outcomes =
        [
          new ItemOutcome
          {
            ItemId = "it-0",
            State = ItemState.Succeeded,
            Attempt = 1,
            PayloadType = "ocr.v1",
            Payload = new Dictionary<string, JsonElement>
            {
              ["raw_text"] = JsonSerializer.SerializeToElement("pin edit text"),
            },
          },
        ],
        ThroughSequence = afterSequence,
      });
  }

  /// <summary>C2：独立 pin 写入器（不复用工程 picker 事务）；记录调用序。</summary>
  private sealed class RecordingPinSnapshotWriter : IPinSnapshotWriter
  {
    public readonly List<string> Order = [];
    public Guid LastPinId;
    public byte[]? LastPng;
    public IReadOnlyList<WorkbenchExclusionBox>? LastExclusions;
    public string? PngPath;
    public string? ProjectPath;
    public string? GetSnapshotProjectPath(Guid pinId) => ProjectPath;

    public Task WriteSnapshotAsync(
      Guid pinId,
      VibpProjectManifest manifest,
      IReadOnlyList<VibpProjectEntry> entries,
      byte[] finalPng,
      CancellationToken cancellationToken, IReadOnlyList<WorkbenchExclusionBox>? excludeBoxes = null)
    {
      Order.Add("write");
      LastPinId = pinId;
      LastPng = finalPng;
      LastExclusions = excludeBoxes;
      string root = Path.Combine(Path.GetTempPath(), "vibeocr-pin-edit-writer");
      Directory.CreateDirectory(root);
      PngPath = Path.Combine(root, $"{pinId:N}.png");
      File.WriteAllBytes(PngPath, finalPng);
      Order.Add("written");
      return Task.CompletedTask;
    }

    public string? GetSnapshotPngPath(Guid pinId) => PngPath is null ? null :
      File.Exists(PngPath) ? PngPath : null;

    public void DeleteSnapshot(Guid pinId)
    {
      if (PngPath is not null) try { File.Delete(PngPath); } catch (IOException) { }
    }
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

  private sealed class OutputPlatform : IAnnotatedImagePlatform
  {
    public bool CancelSave;
    public Task CopyImageAsync(string path, CancellationToken token) => Task.CompletedTask;
    public Task<bool> SaveImageAsync(string path, CancellationToken token) => Task.FromResult(!CancelSave);
    public Task CopyTextAsync(string text, CancellationToken token) => Task.CompletedTask;
  }

  private sealed class Harness : IAsyncDisposable
  {
    public readonly string Root = Path.Combine(
      Path.GetTempPath(), $"vibeocr-pin-edit-{Guid.NewGuid():N}");
    public readonly WorkbenchResourceBroker Broker;
    public readonly WorkbenchAnnotationStore Annotations;
    public readonly WorkbenchProjectStore Projects;
    public readonly RecordingPinSnapshotWriter Writer = new();
    public readonly PinHistoryStore History;
    public readonly DesktopWorkbenchCommandHandler Handler;
    public readonly List<WorkbenchState> States = [];
    public readonly List<(Guid Session, long Revision, Guid Pin)> Applied = [];
    public readonly List<Guid> Closed = [];

    public Harness(Func<CancellationToken, Task<string?>>? savePicker = null,
      VibeOCR.Platform.Bootstrap.PortableLayout? optionsLayout = null)
    {
      Directory.CreateDirectory(Root);
      Broker = new WorkbenchResourceBroker(Root);
      Annotations = new WorkbenchAnnotationStore(Root);
      Projects = new WorkbenchProjectStore();
      History = new PinHistoryStore(Root);
      var inference = new CompletingInferenceClient();
      var inputs = new TaggedInputService();
      Handler = new DesktopWorkbenchCommandHandler(
        () => new RecognitionViewModel(inference, inputs),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        () => new SettingsViewModel(inference),
        () => new ShellViewModel(new StubHotkeyRegistrar(), new StubStartupRegistrar()),
        static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        Broker,
        Root,
        static () => 0,
        Annotations,
        inferenceAttached: () => false,
        projectStore: Projects,
        projectSavePicker: savePicker,
        pinSnapshotWriter: Writer,
        pinHistory: History,
        optionsLayout: optionsLayout);
      Handler.StateChanged += state => States.Add(state);
      Handler.PinSnapshotApplied += (session, revision, pin) => Applied.Add((session, revision, pin));
      Handler.PinEditSessionClosed += session => Closed.Add(session);
    }

    public WorkbenchCommandOutcome Execute(WorkbenchCommand command) =>
      Handler.ExecuteAsync(command, TestContext.Current.CancellationToken)
        .GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
      await Handler.DisposeAsync();
      Broker.Dispose();
      Annotations.Dispose();
      Projects.Dispose();
      try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
  }

  private static string TemporaryRoot() => Path.Combine(
    Path.GetTempPath(), $"vibeocr-pin-edit-outer-{Guid.NewGuid():N}");

  [Fact]
  public async Task PinEditSessionPublishesOnlyPinEditScope()
  {
    await using var harness = new Harness();
    Guid? sessionId = await harness.Handler.TryOpenPinEditSessionAsync(
      Guid.NewGuid(), Png, TestContext.Current.CancellationToken, [new(0, 0, 250, 1000)]);
    Assert.NotNull(sessionId);
    // C1：会话建立与后续命令只发布 pinEdit scope，不产出 recognition/imageEdit。
    Assert.All(harness.States, state => Assert.Equal("pinEdit", state.Scope));
    PinEditWorkbenchState opened = Assert.IsType<PinEditWorkbenchState>(
      harness.States.Single(state => state.Scope == "pinEdit" &&
        Assert.IsType<PinEditWorkbenchState>(state).ScreenshotSession is not null));
    Assert.Equal(sessionId.ToString()!.Replace("-", ""), opened.ScreenshotSession!.SessionId);
    Assert.NotNull(opened.Input);
    Assert.Equal(new WorkbenchExclusionBox(0, 0, 250, 1000), Assert.Single(opened.ScreenshotSession.ExcludeBoxes!));
  }

  [Fact]
  public async Task PinEditPresetSaveBindsCurrentSessionAndProjectsPinEditScope()
  {
    // #239×#238 连接契约：pinEdit.savePresetImage 走 pinEdit 通道——预设
    // 输出必须绑定当前贴图会话/修订，返回 pinEdit 投影；缺失/过期绑定
    // fail closed 且不消耗上传租约，成功保存租约仅一次消费。
    string outputRoot = Path.Combine(
      Path.GetTempPath(), $"vibeocr-pin-preset-{Guid.NewGuid():N}");
    Directory.CreateDirectory(outputRoot);
    VibeOCR.Platform.Bootstrap.PortableLayout layout =
      VibeOCR.Platform.Bootstrap.PortableLayout.Resolve(
        Path.Combine(outputRoot, "VibeOCR.Next.exe"), "production");
    layout.EnsurePortableState();
    EditorPreferencesStore.Save(layout, new(
      EditorPreferences.Default with { Output = new(NameTemplate: "{name}") },
      outputRoot));
    await using var harness = new Harness(optionsLayout: layout);
    try
    {
      Guid? session = await harness.Handler.TryOpenPinEditSessionAsync(
        Guid.NewGuid(), Png, TestContext.Current.CancellationToken);
      Assert.NotNull(session);
      WorkbenchAnnotationLease png = await harness.Annotations.UploadPngAsync(
        new MemoryStream(Png), TestContext.Current.CancellationToken);

      // 缺失与过期会话绑定：拒绝且租约仍可后续使用。
      Assert.NotNull(harness.Execute(new SavePresetImageCommand(
        png.ResourceUri.AbsoluteUri, "default", "pinEdit")).Error);
      Assert.NotNull(harness.Execute(new SavePresetImageCommand(
        png.ResourceUri.AbsoluteUri, "default", "pinEdit", session!.Value, 99)).Error);

      harness.States.Clear();
      WorkbenchCommandOutcome saved = harness.Execute(new SavePresetImageCommand(
        png.ResourceUri.AbsoluteUri, "default", "pinEdit", session.Value, 0));
      Assert.Null(saved.Error);
      Assert.NotEmpty(saved.States);
      Assert.All(saved.States, state => Assert.Equal("pinEdit", state.Scope));
      // 预设保存的返回投影仍是 pinEdit 会话状态（命令 outcome 直达，
      // 不像 applySnapshot 额外 publish 终态）。
      Assert.Contains(saved.States, state => state is PinEditWorkbenchState pin &&
        pin.ScreenshotSession is not null);
      // 输出按预设写入授权目录；pinEdit 通道无 DisplayName，回落安全默认名。
      Assert.True(File.Exists(Path.Combine(outputRoot, "vibeocr.png")));
      // 租约一次消费：重复命令无法再次取用同一上传。
      Assert.NotNull(harness.Execute(new SavePresetImageCommand(
        png.ResourceUri.AbsoluteUri, "default", "pinEdit", session.Value, 0)).Error);
    }
    finally
    {
      try { Directory.Delete(outputRoot, recursive: true); } catch (IOException) { }
    }
  }

  [Fact]
  public async Task ApplySnapshotPersistsBeforeAckAndEvent()
  {
    await using var harness = new Harness();
    Guid pinId = Guid.NewGuid();
    Guid? sessionId = await harness.Handler.TryOpenPinEditSessionAsync(
      pinId, Png, TestContext.Current.CancellationToken);
    Assert.NotNull(sessionId);
    WorkbenchProjectContributionLease contribution = await harness.Projects.UploadAsync(
      new MemoryStream(System.Text.Encoding.UTF8.GetBytes(ContributionBody)),
      this, TestContext.Current.CancellationToken);
    WorkbenchAnnotationLease png = await harness.Annotations.UploadPngAsync(
      new MemoryStream(Png), TestContext.Current.CancellationToken);

    WorkbenchCommandOutcome outcome = harness.Execute(new ApplyPinEditSnapshotCommand(
      contribution.ContributeId, png.ResourceUri.AbsoluteUri, sessionId!.Value, 0, [new(0, 0, 1000, 250)]));

    Assert.Null(outcome.Error);
    Assert.True(outcome.States.Count == 0 || outcome.States.All(state => state.Scope == "pinEdit"));
    // C3：写入完成先于事件与回执（顺序记录 writer 先落盘再 Applied）。
    Assert.Equal(["write", "written"], harness.Writer.Order);
    Assert.Equal((sessionId.Value, 0L, pinId), harness.Applied.Single());
    Assert.Equal(Png, harness.Writer.LastPng);
    Assert.Equal(new WorkbenchExclusionBox(0, 0, 1000, 250), Assert.Single(harness.Writer.LastExclusions!));
    // 会话结束：applied 终态后通道回到无会话投影。
    Assert.Contains(harness.States, state => state is PinEditWorkbenchState pin &&
      pin.StatusCode == "pinEdit.applied");
    Assert.DoesNotContain(harness.States, state => state.Scope is "recognition" or "imageEdit");
  }

  [Fact]
  public async Task ExclusionBudgetIsRejectedBeforeConsumingTheOwnedSaveLeases()
  {
    await using var harness = new Harness();
    Guid? session = await harness.Handler.TryOpenPinEditSessionAsync(Guid.NewGuid(), Png, TestContext.Current.CancellationToken);
    WorkbenchProjectContributionLease contribution = await harness.Projects.UploadAsync(
      new MemoryStream(System.Text.Encoding.UTF8.GetBytes(ContributionBody)), this, TestContext.Current.CancellationToken);
    WorkbenchAnnotationLease image = await harness.Annotations.UploadPngAsync(new MemoryStream(Png), TestContext.Current.CancellationToken);
    WorkbenchExclusionBox[] tooMany = Enumerable.Repeat(new WorkbenchExclusionBox(0, 0, 10, 10), 65).ToArray();
    WorkbenchCommandOutcome refused = harness.Execute(new ApplyPinEditSnapshotCommand(
      contribution.ContributeId, image.ResourceUri.AbsoluteUri, session!.Value, 0, tooMany));
    Assert.NotNull(refused.Error); Assert.Empty(harness.Writer.Order);
    WorkbenchCommandOutcome saved = harness.Execute(new ApplyPinEditSnapshotCommand(
      contribution.ContributeId, image.ResourceUri.AbsoluteUri, session.Value, 0, []));
    Assert.Null(saved.Error); Assert.Equal(["write", "written"], harness.Writer.Order);
  }

  [Fact]
  public async Task LateApplyIsRejectedWithoutPersisting()
  {
    await using var harness = new Harness();
    Guid? first = await harness.Handler.TryOpenPinEditSessionAsync(
      Guid.NewGuid(), Png, TestContext.Current.CancellationToken);
    Assert.NotNull(first);
    WorkbenchCommandOutcome close = harness.Execute(new ClosePinEditSessionCommand());
    Assert.True(close.States.All(state => state.Scope == "pinEdit"));
    Assert.Equal([first.Value], harness.Closed);
    Guid? second = await harness.Handler.TryOpenPinEditSessionAsync(
      Guid.NewGuid(), Png, TestContext.Current.CancellationToken);
    Assert.NotNull(second);
    Assert.NotEqual(first.Value, second.Value);

    WorkbenchProjectContributionLease contribution = await harness.Projects.UploadAsync(
      new MemoryStream(System.Text.Encoding.UTF8.GetBytes(ContributionBody)),
      this, TestContext.Current.CancellationToken);
    WorkbenchAnnotationLease png = await harness.Annotations.UploadPngAsync(
      new MemoryStream(Png), TestContext.Current.CancellationToken);
    WorkbenchCommandOutcome stale = harness.Execute(new ApplyPinEditSnapshotCommand(
      contribution.ContributeId, png.ResourceUri.AbsoluteUri, first.Value, 0));

    Assert.NotNull(stale.Error);
    Assert.Empty(harness.Writer.Order);
    Assert.Empty(harness.Applied);
  }

  [Fact]
  public async Task ClosingEditSessionKeepsHistoryProjection()
  {
    await using var harness = new Harness();
    harness.History.SetEnabled(true);
    harness.History.RecordSuccessfulCapture("capture-a", Png);
    Guid? sessionId = await harness.Handler.TryOpenPinEditSessionAsync(
      Guid.NewGuid(), Png, TestContext.Current.CancellationToken);
    Assert.NotNull(sessionId);
    harness.Execute(new ClosePinEditSessionCommand());

    WorkbenchCommandOutcome listed = harness.Execute(new ListPinHistoryCommand(null));
    PinEditWorkbenchState projection = Assert.IsType<PinEditWorkbenchState>(
      listed.States.Single(state => state.Scope == "pinEdit"));
    Assert.NotNull(projection.History);
    Assert.True(projection.History.Enabled);
    Assert.Equal(1, projection.History.Count);
    // 历史管理命令无编辑会话也可用；关闭会话不清历史列表。
    Assert.Null(projection.ScreenshotSession);
  }

  [Fact]
  public async Task HistoryOperationsPreserveTheLastSubmittedQuery()
  {
    await using var harness = new Harness();
    harness.History.SetEnabled(true);
    harness.History.RecordSuccessfulCapture("capture-a", Png);
    harness.History.RecordSuccessfulCapture("capture-b", Png);
    harness.History.EnrichRecognizedText("capture-a", "发票");
    harness.Execute(new ListPinHistoryCommand("发票"));
    harness.Handler.PublishPinHistory();
    Assert.Single(Assert.IsType<PinEditWorkbenchState>(harness.States.Last()).History!.Entries);
    string id = Assert.Single(harness.History.Projection("发票").Entries).EntryId;
    harness.Handler.PinHistoryReopenRequested += _ => Task.CompletedTask;
    var reopened = harness.Execute(new ReopenPinHistoryEntryCommand(id));
    Assert.Equal(id, Assert.Single(Assert.IsType<PinEditWorkbenchState>(
      reopened.States.Single(state => state.Scope == "pinEdit")).History!.Entries).EntryId);
    var deleted = harness.Execute(new DeletePinHistoryEntryCommand(id));
    Assert.Empty(Assert.IsType<PinEditWorkbenchState>(deleted.States.Single(state => state.Scope == "pinEdit")).History!.Entries);
    Assert.Equal(1, harness.History.Projection(null).Count);
    var reset = harness.Execute(new ListPinHistoryCommand(null));
    Assert.Single(Assert.IsType<PinEditWorkbenchState>(reset.States.Single(state => state.Scope == "pinEdit")).History!.Entries);
  }

  [Theory]
  [InlineData(ScreenshotSelectionAction.Copy, false, 1)]
  [InlineData(ScreenshotSelectionAction.Save, false, 1)]
  [InlineData(ScreenshotSelectionAction.Save, true, 0)]
  [InlineData(ScreenshotSelectionAction.Pin, false, 1)]
  public async Task SuccessfulLocalOutputCaptureRecordsHistoryOnceWhenEnabled(
    ScreenshotSelectionAction action, bool cancelSave, int expectedCount)
  {
    // C4：形状B（本地输出：复制）成功点录入；默认关闭不录。
    await using var harness = new Harness();
    var inference = new CompletingInferenceClient();
    var inputs = new TaggedInputService(action);
    var handler = new DesktopWorkbenchCommandHandler(
      () => new RecognitionViewModel(inference, inputs),
      static () => throw new InvalidOperationException(),
      static () => throw new InvalidOperationException(),
      static () => throw new InvalidOperationException(),
      () => new SettingsViewModel(inference),
      () => new ShellViewModel(new StubHotkeyRegistrar(), new StubStartupRegistrar()),
      static () => throw new InvalidOperationException(),
      new DiagnosticsViewModel("test", new PrerequisiteReport([])),
      harness.Broker,
      harness.Root,
      static () => 0,
      harness.Annotations,
      annotatedImagePlatform: new OutputPlatform { CancelSave = cancelSave },
      pinScreenshot: (image, _, _, _, _) => image.Dispose(),
      inferenceAttached: () => false,
      projectStore: harness.Projects,
      pinSnapshotWriter: harness.Writer,
      pinHistory: harness.History);
    try
    {
      Task completed = AwaitCaptureTerminal(handler);
      await handler.ExecuteAsync(new CaptureScreenshotSessionCommand(), TestContext.Current.CancellationToken);
      await completed;
      Assert.Equal(0, harness.History.Projection(null).Count);

      harness.History.SetEnabled(true);
      completed = AwaitCaptureTerminal(handler);
      await handler.ExecuteAsync(new CaptureScreenshotSessionCommand(), TestContext.Current.CancellationToken);
      await completed;
      Assert.Equal(expectedCount, harness.History.Projection(null).Count);
    }
    finally
    {
      await handler.DisposeAsync();
    }
  }

  [Fact]
  public async Task OwnedPinSaveDoesNotWaitForTheMainEditorPicker()
  {
    var pickerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var pickerResult = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
    await using var harness = new Harness(_ => { pickerEntered.TrySetResult(); return pickerResult.Task; });
    var opened = new TaskCompletionSource<ImageEditWorkbenchState>(TaskCreationOptions.RunContinuationsAsynchronously);
    harness.Handler.StateChanged += state =>
    { if (state is ImageEditWorkbenchState { IsBusy: false, ScreenshotSession: not null } ready) opened.TrySetResult(ready); };
    await harness.Handler.ExecuteAsync(new ReadImageEditClipboardCommand(), TestContext.Current.CancellationToken);
    ImageEditWorkbenchState main = await opened.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    var mainContribution = await harness.Projects.UploadAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(ContributionBody)),
      this, TestContext.Current.CancellationToken);
    Task<WorkbenchCommandOutcome> savingMain = harness.Handler.ExecuteAsync(new SaveImageEditProjectCommand(
      mainContribution.ContributeId, Guid.ParseExact(main.ScreenshotSession!.SessionId, "N"), 0), TestContext.Current.CancellationToken).AsTask();
    await pickerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    try
    {
      Guid? pinSession = await harness.Handler.TryOpenPinEditSessionAsync(Guid.NewGuid(), Png, TestContext.Current.CancellationToken);
      var pinContribution = await harness.Projects.UploadAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(ContributionBody)),
        this, TestContext.Current.CancellationToken);
      var annotation = await harness.Annotations.UploadPngAsync(new MemoryStream(Png), TestContext.Current.CancellationToken);
      harness.States.Clear();
      WorkbenchCommandOutcome saved = await harness.Handler.ExecuteAsync(new ApplyPinEditSnapshotCommand(
        pinContribution.ContributeId, annotation.ResourceUri.AbsoluteUri, pinSession!.Value, 0), TestContext.Current.CancellationToken);
      Assert.Null(saved.Error);
      Assert.False(savingMain.IsCompleted);
      Assert.All(harness.States, state => Assert.Equal("pinEdit", state.Scope));
    }
    finally { pickerResult.TrySetResult(null); await savingMain; }
  }

  private static async Task AwaitCaptureTerminal(DesktopWorkbenchCommandHandler handler)
  {
    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    void Observe(WorkbenchState state)
    { if (state.Scope == "recognition" && state.IsTerminal && !((RecognitionWorkbenchState)state).IsBusy) completion.TrySetResult(); }
    handler.StateChanged += Observe;
    try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); }
    finally { handler.StateChanged -= Observe; }
  }

  [Fact]
  public async Task CorruptProjectArchiveFallsBackToFlatEditableSessionAndKeepsVibp()
  {
    await using var harness = new Harness();
    string project = Path.Combine(harness.Root, "corrupt.vibp");
    // 非 zip 骨架：ReadAsync 在恢复描述符发布前抛出，前端无机会拒收。
    await File.WriteAllBytesAsync(project, [0x50, 0x4b, 0x03, 0x04, 0x00], TestContext.Current.CancellationToken);
    byte[] originalProject = File.ReadAllBytes(project);
    harness.Writer.ProjectPath = project;
    Guid pinId = Guid.NewGuid();
    Guid? session = await harness.Handler.TryOpenPinEditSessionAsync(
      pinId, Png, TestContext.Current.CancellationToken);
    // 工程读取失败不清整个 pinEdit：扁平会话可用、无工程投影。
    Assert.NotNull(session);
    PinEditWorkbenchState opened = Assert.IsType<PinEditWorkbenchState>(harness.States.Last());
    Assert.Null(opened.ProjectRestore);
    Assert.NotNull(opened.Input);
    await using (var response = await harness.Broker.OpenAsync(
      new Uri(opened.Input.Url), TestContext.Current.CancellationToken))
    {
      using var bytes = new MemoryStream();
      await response.Content.CopyToAsync(bytes, TestContext.Current.CancellationToken);
      Assert.Equal(Png, bytes.ToArray());
    }
    Assert.All(harness.States, state => Assert.Equal("pinEdit", state.Scope));
    Assert.Equal(originalProject, File.ReadAllBytes(project));
    // 回退后的会话可完整保存（资源无泄漏阻塞）：正常 applySnapshot 成功。
    WorkbenchProjectContributionLease contribution = await harness.Projects.UploadAsync(
      new MemoryStream(System.Text.Encoding.UTF8.GetBytes(ContributionBody)),
      this, TestContext.Current.CancellationToken);
    WorkbenchAnnotationLease finalPng = await harness.Annotations.UploadPngAsync(
      new MemoryStream(Png), TestContext.Current.CancellationToken);
    WorkbenchCommandOutcome applied = harness.Execute(new ApplyPinEditSnapshotCommand(
      contribution.ContributeId, finalPng.ResourceUri.AbsoluteUri, session!.Value, 0));
    Assert.Null(applied.Error);
    Assert.Equal((session.Value, 0L, pinId), harness.Applied.Single());
  }

  [Fact]
  public async Task RejectedPinRestoreRebindsOnlyMatchingSessionToRetainedFlatPng()
  {
    await using var harness = new Harness();
    string project = Path.Combine(harness.Root, "kept.vibp");
    var manifest = new VibpProjectManifest
    {
      SchemaVersion = VibpProjectLimits.SchemaVersion,
      Canvas = new VibpCanvas { Width = 1, Height = 1 },
      Source = new VibpSourceResource { MediaType = "image/png", Width = 1, Height = 1, Length = Png.Length },
      State = new VibpEditorState { Rotation = 0, Marks = [] }, Assets = [],
    };
    await VibpProjectFile.WriteAsync(project, manifest,
      [new("source.png", Png.Length, () => new MemoryStream(Png, false))], TestContext.Current.CancellationToken);
    byte[] originalProject = File.ReadAllBytes(project);
    harness.Writer.ProjectPath = project;
    Guid? session = await harness.Handler.TryOpenPinEditSessionAsync(Guid.NewGuid(), Png, TestContext.Current.CancellationToken);
    PinEditWorkbenchState restored = Assert.IsType<PinEditWorkbenchState>(harness.States.Last());
    var descriptor = Assert.IsType<WorkbenchProjectOpenState>(restored.ProjectRestore);
    harness.States.Clear();
    Assert.NotNull(harness.Execute(new RejectPinProjectRestoreCommand(Guid.NewGuid(), session!.Value, 0)).Error);
    WorkbenchCommandOutcome rejected = harness.Execute(new RejectPinProjectRestoreCommand(Guid.Parse(descriptor.PendingId), session.Value, 0));
    Assert.Null(rejected.Error);
    PinEditWorkbenchState fallback = Assert.IsType<PinEditWorkbenchState>(Assert.Single(rejected.States));
    Assert.All(rejected.States, state => Assert.Equal("pinEdit", state.Scope));
    Assert.Null(fallback.ProjectRestore);
    Assert.NotEqual(restored.Input!.Url, fallback.Input!.Url);
    await using (var response = await harness.Broker.OpenAsync(new Uri(fallback.Input.Url), TestContext.Current.CancellationToken))
    {
      using var bytes = new MemoryStream(); await response.Content.CopyToAsync(bytes, TestContext.Current.CancellationToken);
      Assert.Equal(Png, bytes.ToArray());
    }
    Assert.All(harness.States, state => Assert.Equal("pinEdit", state.Scope));
    Assert.Equal(originalProject, File.ReadAllBytes(project));
    Assert.NotNull(harness.Execute(new RejectPinProjectRestoreCommand(Guid.Parse(descriptor.PendingId), session.Value, 0)).Error);
    harness.Execute(new ClosePinEditSessionCommand());
    Guid? next = await harness.Handler.TryOpenPinEditSessionAsync(Guid.NewGuid(), Png, TestContext.Current.CancellationToken);
    Assert.NotNull(harness.Execute(new RejectPinProjectRestoreCommand(Guid.Parse(descriptor.PendingId), session.Value, 0)).Error);
    Assert.NotEqual(session, next);
  }

}

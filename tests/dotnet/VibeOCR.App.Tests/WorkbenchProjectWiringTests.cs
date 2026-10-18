using System.Text.Json;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Features.Settings;
using VibeOCR.App.Features.Shell;
using VibeOCR.App.Inference;
using VibeOCR.App.ViewModels;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using VibeOCR.Platform.Bootstrap;
using Xunit;

namespace VibeOCR.App.Tests;

/// <summary>#233 生产 owner 接线契约：真实 DesktopWorkbenchCommandHandler +
/// 真实 store/broker/格式模块，picker 为可替 delegate。覆盖保存成功/stale/
/// 取消/资产闭合、打开 pending→ack 切换/错误绑定回收/拒收回收与截图通道保存。</summary>
public sealed class WorkbenchProjectWiringTests
{
  private static readonly byte[] Source = PngBytes(64, 48);
  private static readonly byte[] Patch = PngBytes(4, 3);

  private sealed class PickerStub
  {
    public string? SavePath;
    public string? OpenPath;

    public Func<CancellationToken, Task<string?>> SavePicker =>
      _ => Task.FromResult(SavePath);

    public Func<CancellationToken, Task<string?>> OpenPicker =>
      _ => Task.FromResult(OpenPath);
  }

  private static DesktopWorkbenchCommandHandler CreateHandler(
    WorkbenchResourceBroker broker,
    WorkbenchAnnotationStore annotationStore,
    WorkbenchProjectStore projectStore,
    string resourceRoot,
    PickerStub picker)
  {
    var inference = new CompletingInferenceClient();
    return new DesktopWorkbenchCommandHandler(
      () => new RecognitionViewModel(inference, new ProjectInputService()),
      static () => throw new InvalidOperationException(),
      static () => throw new InvalidOperationException(),
      static () => throw new InvalidOperationException(),
      () => new SettingsViewModel(inference),
      () => new ShellViewModel(new StubHotkeyRegistrar(), new StubStartupRegistrar()),
      static () => throw new InvalidOperationException(),
      new DiagnosticsViewModel("test", new PrerequisiteReport([])),
      broker,
      resourceRoot,
      static () => 0,
      annotationStore,
      projectStore: projectStore,
      projectSavePicker: picker.SavePicker,
      projectOpenPicker: picker.OpenPicker);
  }

  private static async Task<Guid> EstablishSessionAsync(
    DesktopWorkbenchCommandHandler handler, WorkbenchRoute route)
  {
    WorkbenchCommand command = route == WorkbenchRoute.ImageEdit
      ? new ReadImageEditClipboardCommand()
      : new OpenRecognitionImageForEditCommand();
    Guid session;
    if (route == WorkbenchRoute.ImageEdit)
    {
      using var captured = new StateAwaiter<ImageEditWorkbenchState>(handler,
        state => state.ScreenshotSession is not null);
      await handler.ExecuteAsync(command, TestContext.Current.CancellationToken);
      session = Guid.Parse((await captured.Task).ScreenshotSession!.SessionId);
    }
    else
    {
      using var captured = new StateAwaiter<RecognitionWorkbenchState>(handler,
        state => state.ScreenshotSession is not null);
      await handler.ExecuteAsync(command, TestContext.Current.CancellationToken);
      session = Guid.Parse((await captured.Task).ScreenshotSession!.SessionId);
    }
    return session;
  }

  private static async Task<(Guid ContributeId, MemoryStream Body)> ContributeAsync(
    WorkbenchProjectStore store)
  {
    MemoryStream body = new(JsonSerializer.SerializeToUtf8Bytes(new
    {
      canvas = new { width = 64, height = 48 },
      state = new { rotation = 0, marks = Array.Empty<object>() },
      patches = new object[]
      {
        new { id = 1, x = 2, y = 3, width = 4, height = 3, png = Convert.ToBase64String(Patch) },
      },
    }));
    WorkbenchProjectContributionLease lease = await store.UploadAsync(
      body, TestContext.Current.CancellationToken);
    return (lease.ContributeId, body);
  }

  [Fact]
  public async Task SaveProjectWritesPackageFromHeldSessionResources()
  {
    string root = TemporaryRoot();
    string projectPath = Path.Combine(root, "saved.vibp");
    try
    {
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      using var store = new WorkbenchProjectStore();
      var picker = new PickerStub { SavePath = projectPath };
      await using var handler = CreateHandler(broker, annotations, store, root, picker);
      Guid session = await EstablishSessionAsync(handler, WorkbenchRoute.ImageEdit);
      (Guid contributeId, _) = await ContributeAsync(store);

      WorkbenchCommandOutcome outcome = await handler.ExecuteAsync(
        new SaveImageEditProjectCommand(contributeId, session, 0),
        TestContext.Current.CancellationToken);

      Assert.Null(outcome.Error);
      Assert.True(File.Exists(projectPath));
      Assert.Equal((0, null), store.Staged);
      VibpProjectArchive archive = await VibpProjectFile.ReadAsync(
        projectPath, Path.Combine(root, "open"), TestContext.Current.CancellationToken);
      Assert.Equal(Source, await File.ReadAllBytesAsync(
        archive.SourcePath, TestContext.Current.CancellationToken));
      Assert.Equal(Patch, await File.ReadAllBytesAsync(
        archive.PatchPaths[1], TestContext.Current.CancellationToken));
      Assert.Equal("image/png", archive.Manifest.Source.MediaType);
      Assert.Equal(64, archive.Manifest.Canvas.Width);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task SaveProjectRefusesStaleRevisionWithoutWritingFile()
  {
    string root = TemporaryRoot();
    string projectPath = Path.Combine(root, "stale.vibp");
    try
    {
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      using var store = new WorkbenchProjectStore();
      var picker = new PickerStub { SavePath = projectPath };
      await using var handler = CreateHandler(broker, annotations, store, root, picker);
      Guid session = await EstablishSessionAsync(handler, WorkbenchRoute.ImageEdit);
      (Guid contributeId, _) = await ContributeAsync(store);

      WorkbenchCommandOutcome outcome = await handler.ExecuteAsync(
        new SaveImageEditProjectCommand(contributeId, session, 7),
        TestContext.Current.CancellationToken);

      Assert.Equal("screenshot_session_stale", outcome.Error?.Code);
      Assert.False(File.Exists(projectPath));
      // stale 在贡献消费前拒绝：贡献保留至替换/过期，不可被旧命令重放后成功。
      Assert.Equal(1, store.Staged.Count);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task SaveProjectCancelKeepsCanvasAndWritesNothing()
  {
    string root = TemporaryRoot();
    string projectPath = Path.Combine(root, "cancel.vibp");
    try
    {
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      using var store = new WorkbenchProjectStore();
      var picker = new PickerStub();
      await using var handler = CreateHandler(broker, annotations, store, root, picker);
      Guid session = await EstablishSessionAsync(handler, WorkbenchRoute.ImageEdit);
      (Guid contributeId, _) = await ContributeAsync(store);

      WorkbenchCommandOutcome outcome = await handler.ExecuteAsync(
        new SaveImageEditProjectCommand(contributeId, session, 0),
        TestContext.Current.CancellationToken);

      Assert.Equal("annotation_operation_cancelled", outcome.Error?.Code);
      Assert.False(File.Exists(projectPath));
      // 取消后画布仍可用：同一 handler/会话换路径与新贡献再保存成功。
      picker.SavePath = projectPath;
      (Guid second, _) = await ContributeAsync(store);
      WorkbenchCommandOutcome saved = await handler.ExecuteAsync(
        new SaveImageEditProjectCommand(second, session, 0),
        TestContext.Current.CancellationToken);
      Assert.Null(saved.Error);
      Assert.True(File.Exists(projectPath));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task ScreenshotChannelSessionSavesProject()
  {
    string root = TemporaryRoot();
    string projectPath = Path.Combine(root, "capture.vibp");
    try
    {
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      using var store = new WorkbenchProjectStore();
      var picker = new PickerStub { SavePath = projectPath };
      await using var handler = CreateHandler(broker, annotations, store, root, picker);
      Guid session = await EstablishSessionAsync(handler, WorkbenchRoute.Recognition);
      (Guid contributeId, _) = await ContributeAsync(store);

      WorkbenchCommandOutcome outcome = await handler.ExecuteAsync(
        new SaveScreenshotProjectCommand(contributeId, session, 0),
        TestContext.Current.CancellationToken);

      Assert.Null(outcome.Error);
      Assert.True(File.Exists(projectPath));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task OpenProjectPublishesPendingAndAckSwitchesSession()
  {
    string root = TemporaryRoot();
    string projectPath = Path.Combine(root, "open.vibp");
    try
    {
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      using var store = new WorkbenchProjectStore();
      var picker = new PickerStub { SavePath = projectPath };
      await using var saver = CreateHandler(broker, annotations, store, root, picker);
      Guid session = await EstablishSessionAsync(saver, WorkbenchRoute.ImageEdit);
      (Guid contributeId, _) = await ContributeAsync(store);
      Assert.Null((await saver.ExecuteAsync(
        new SaveImageEditProjectCommand(contributeId, session, 0),
        TestContext.Current.CancellationToken)).Error);

      var openPicker = new PickerStub { OpenPath = projectPath };
      await using var opener = CreateHandler(broker, annotations, store, root, openPicker);
      // 打开目标：空 imageEdit 通道（SessionId=Guid.Empty，修订 0）。
      WorkbenchCommandOutcome opened = await opener.ExecuteAsync(
        new OpenImageEditProjectCommand(), TestContext.Current.CancellationToken);
      Assert.Null(opened.Error);
      WorkbenchImageOwner.ProjectOpenDescriptor? pending = opener.CurrentProjectOpen;
      Assert.NotNull(pending);
      Assert.Equal(64, pending.Manifest.Canvas.Width);
      Assert.True(WorkbenchResourceBroker.IsResourceUri(new Uri(pending.Source.Url)));
      WorkbenchProjectOpenBinding binding = pending.Binding;

      // 错误绑定 ack：精确回收 pending，旧（空）会话不动，租约已失效。
      WorkbenchCommandOutcome wrong = await opener.ExecuteAsync(
        new AckImageEditProjectOpenCommand(pending.PendingId, Guid.NewGuid(), 0, binding.Generation),
        TestContext.Current.CancellationToken);
      Assert.Equal("project_open_conflict", wrong.Error?.Code);
      Assert.Null(opener.CurrentProjectOpen);
      await Assert.ThrowsAnyAsync<WorkbenchResourceAccessException>(
        async () => await broker.OpenAsync(
          new Uri(pending.Source.Url), TestContext.Current.CancellationToken));

      // 重新打开并以匹配绑定 ack：原子切换到恢复会话。
      Assert.Null((await opener.ExecuteAsync(
        new OpenImageEditProjectCommand(), TestContext.Current.CancellationToken)).Error);
      WorkbenchImageOwner.ProjectOpenDescriptor committed = opener.CurrentProjectOpen!;
      WorkbenchCommandOutcome ack = await opener.ExecuteAsync(
        new AckImageEditProjectOpenCommand(
          committed.PendingId, committed.Binding.SessionId, committed.Binding.Revision,
          committed.Binding.Generation),
        TestContext.Current.CancellationToken);
      Assert.Null(ack.Error);
      Assert.Null(opener.CurrentProjectOpen);
      ImageEditWorkbenchState state = Assert.IsType<ImageEditWorkbenchState>(
        Assert.Single(ack.States.OfType<ImageEditWorkbenchState>()));
      Assert.NotNull(state.ScreenshotSession);
      Assert.NotEqual(Guid.Empty.ToString("N"), state.ScreenshotSession.SessionId);
      Assert.NotNull(state.Input);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task OpenProjectRejectReleasesPendingResources()
  {
    string root = TemporaryRoot();
    string projectPath = Path.Combine(root, "reject.vibp");
    try
    {
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      using var store = new WorkbenchProjectStore();
      var picker = new PickerStub { SavePath = projectPath };
      await using var saver = CreateHandler(broker, annotations, store, root, picker);
      Guid session = await EstablishSessionAsync(saver, WorkbenchRoute.ImageEdit);
      (Guid contributeId, _) = await ContributeAsync(store);
      Assert.Null((await saver.ExecuteAsync(
        new SaveImageEditProjectCommand(contributeId, session, 0),
        TestContext.Current.CancellationToken)).Error);

      var openPicker = new PickerStub { OpenPath = projectPath };
      await using var opener = CreateHandler(broker, annotations, store, root, openPicker);
      Assert.Null((await opener.ExecuteAsync(
        new OpenImageEditProjectCommand(), TestContext.Current.CancellationToken)).Error);
      WorkbenchImageOwner.ProjectOpenDescriptor pending = opener.CurrentProjectOpen!;
      // saver 会话输入 + opener 发布的 pending source 各一份同容字节。
      Assert.Equal(2, SourceSessionFiles(root).Length);

      WorkbenchCommandOutcome rejected = await opener.ExecuteAsync(
        new RejectImageEditProjectOpenCommand(
          pending.PendingId, pending.Binding.SessionId, pending.Binding.Revision),
        TestContext.Current.CancellationToken);

      Assert.Null(rejected.Error);
      Assert.Null(opener.CurrentProjectOpen);
      // 拒收后 pending 发布资源被精确回收，仅剩 saver 会话输入一份。
      Assert.Single(SourceSessionFiles(root));
      // 迟到拒收幂等；之后无 pending 可 ack。
      Assert.Null((await opener.ExecuteAsync(
        new RejectImageEditProjectOpenCommand(
          pending.PendingId, pending.Binding.SessionId, pending.Binding.Revision),
        TestContext.Current.CancellationToken)).Error);
      WorkbenchCommandOutcome late = await opener.ExecuteAsync(
        new AckImageEditProjectOpenCommand(
          pending.PendingId, pending.Binding.SessionId, pending.Binding.Revision,
          pending.Binding.Generation),
        TestContext.Current.CancellationToken);
      Assert.Equal("project_open_conflict", late.Error?.Code);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task OpenProjectRefusesCorruptPackageAndKeepsPendingEmpty()
  {
    string root = TemporaryRoot();
    string projectPath = Path.Combine(root, "broken.vibp");
    try
    {
      await File.WriteAllBytesAsync(projectPath, [1, 2, 3], TestContext.Current.CancellationToken);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      using var store = new WorkbenchProjectStore();
      var picker = new PickerStub { OpenPath = projectPath };
      await using var handler = CreateHandler(broker, annotations, store, root, picker);

      WorkbenchCommandOutcome outcome = await handler.ExecuteAsync(
        new OpenImageEditProjectCommand(), TestContext.Current.CancellationToken);

      Assert.Equal("project_format_invalid", outcome.Error?.Code);
      Assert.Null(handler.CurrentProjectOpen);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task AckSuccessReleasesDecodedPatchResourcesButKeepsSourceAndAssets()
  {
    string root = TemporaryRoot();
    string projectPath = Path.Combine(root, "patched.vibp");
    try
    {
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      using var store = new WorkbenchProjectStore();
      var picker = new PickerStub { SavePath = projectPath };
      await using var saver = CreateHandler(broker, annotations, store, root, picker);
      Guid session = await EstablishSessionAsync(saver, WorkbenchRoute.ImageEdit);
      (Guid contributeId, _) = await ContributeAsync(store);
      Assert.Null((await saver.ExecuteAsync(
        new SaveImageEditProjectCommand(contributeId, session, 0),
        TestContext.Current.CancellationToken)).Error);

      var openPicker = new PickerStub { OpenPath = projectPath };
      await using var opener = CreateHandler(broker, annotations, store, root, openPicker);
      Assert.Null((await opener.ExecuteAsync(
        new OpenImageEditProjectCommand(), TestContext.Current.CancellationToken)).Error);
      WorkbenchImageOwner.ProjectOpenDescriptor pending = opener.CurrentProjectOpen!;
      // ack 前：opener 发布的 patch PNG 在会话资源目录中恰一份。
      Assert.Single(PatchSessionFiles(root));
      Assert.Equal(2, SourceSessionFiles(root).Length);

      WorkbenchCommandOutcome ack = await opener.ExecuteAsync(
        new AckImageEditProjectOpenCommand(
          pending.PendingId, pending.Binding.SessionId, pending.Binding.Revision,
          pending.Binding.Generation),
        TestContext.Current.CancellationToken);

      Assert.Null(ack.Error);
      // Web 已全部 decode 后 ack：补丁字节对宿主是死数据，成功切换后必须
      // 回收；source（新会话输入）与 saver 输入不受影响。
      Assert.Empty(PatchSessionFiles(root));
      Assert.Equal(2, SourceSessionFiles(root).Length);
      // 已释放租约不可再经 broker 取回。
      await Assert.ThrowsAnyAsync<WorkbenchResourceAccessException>(
        async () => await broker.OpenAsync(
          new Uri(pending.Patches[1].Url), TestContext.Current.CancellationToken));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task DisposeDuringInFlightProjectUploadLeavesNothingStaged()
  {
    // /__project 销毁竞态的定向风险测试：host DisposeAsync→store.RevokeAll
    // 与在途上传的代际竞争不在销毁后残留贡献/响应（响应构造见 annotation
    // 对称守卫，需 core，不在无 GUI 阶段驱动）。
    using var store = new WorkbenchProjectStore();
    GatedJsonStream stream = new(
      "{\"canvas\":{\"width\":8,\"height\":8},\"state\":{\"rotation\":0,\"marks\":[]},\"patches\":[]}");
    Task upload = Task.Run(() => store.UploadAsync(stream, TestContext.Current.CancellationToken));
    await stream.ReadStarted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    store.Dispose();
    stream.Unblock();

    await Assert.ThrowsAnyAsync<Exception>(() => upload);
    Assert.Equal((0, null), store.Staged);
  }

  [Fact]
  public async Task ProjectOpenStateCarriesReferencesAndBindingOnly()
  {
    string root = TemporaryRoot();
    string projectPath = Path.Combine(root, "projected.vibp");
    try
    {
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      using var store = new WorkbenchProjectStore();
      var picker = new PickerStub { SavePath = projectPath };
      await using var saver = CreateHandler(broker, annotations, store, root, picker);
      Guid session = await EstablishSessionAsync(saver, WorkbenchRoute.ImageEdit);
      (Guid contributeId, _) = await ContributeAsync(store);
      Assert.Null((await saver.ExecuteAsync(
        new SaveImageEditProjectCommand(contributeId, session, 0),
        TestContext.Current.CancellationToken)).Error);

      var openPicker = new PickerStub { OpenPath = projectPath };
      await using var opener = CreateHandler(broker, annotations, store, root, openPicker);
      var opened = await opener.ExecuteAsync(
        new OpenImageEditProjectCommand(), TestContext.Current.CancellationToken);
      WorkbenchProjectOpenState projection = Assert.IsType<ImageEditWorkbenchState>(
        Assert.Single(opened.States.OfType<ImageEditWorkbenchState>())).ProjectOpen!;

      // 绑定：空会话（全零 id）+ 当前代际；状态仅携带受控引用，不内联 manifest。
      Assert.Equal("00000000000000000000000000000000", projection.SessionId);
      Assert.Equal(0, projection.Revision);
      // 绑定与 descriptor 真源一致（空通道代际合法为 0）。
      Assert.Equal(opener.CurrentProjectOpen!.Binding.Generation, projection.Generation);
      Assert.Equal(opener.CurrentProjectOpen.PendingId.ToString("N"), projection.PendingId);
      Assert.All(
        [projection.Manifest.Url, projection.Source.Url, projection.Patches.Single().Resource.Url],
        url => Assert.StartsWith("https://app.vibeocr/__resource/", url));
      // 状态帧有界：携带引用+绑定时始终满足 64KiB 桥限额。
      string frame = WorkbenchBridgeCodec.SerializeState(
        Guid.NewGuid(),
        new WorkbenchStateEnvelope(1, "imageEdit", WorkbenchStateChange.Replace,
          new ImageEditWorkbenchState(false, "recognition.session", ProjectOpen: projection)));
      Assert.True(System.Text.Encoding.UTF8.GetByteCount(frame) <= WorkbenchBridgeCodec.MaxMessageBytes);

      // manifest 经受控 GET 可读且为同一严格 v1 语义（非第二真源）。
      byte[] manifestBytes;
      await using (WorkbenchResourceResponse response = await broker.OpenAsync(
        new Uri(projection.Manifest.Url), TestContext.Current.CancellationToken))
      {
        using var buffered = new MemoryStream();
        await response.Content.CopyToAsync(buffered, TestContext.Current.CancellationToken);
        manifestBytes = buffered.ToArray();
      }
      VibpProjectManifest manifest = System.Text.Json.JsonSerializer.Deserialize<VibpProjectManifest>(
        manifestBytes, VibpProjectRules.ManifestJson)!;
      Assert.Equal(64, manifest.Canvas.Width);
      Assert.Equal("image/png", manifest.Source.MediaType);
      Assert.Equal((2, 3, 4, 3), (manifest.State.Patches[0].X, manifest.State.Patches[0].Y,
        manifest.State.Patches[0].Width, manifest.State.Patches[0].Height));

      // ack 成功：投影置空，manifest/patch 释放（Web 已 fetch/decode 并持有），
      // source 接管为新会话输入。
      var ack = await opener.ExecuteAsync(
        new AckImageEditProjectOpenCommand(
          Guid.ParseExact(projection.PendingId, "N"), Guid.Empty, 0, projection.Generation),
        TestContext.Current.CancellationToken);
      Assert.Null(ack.Error);
      Assert.Null(Assert.IsType<ImageEditWorkbenchState>(
        Assert.Single(ack.States.OfType<ImageEditWorkbenchState>())).ProjectOpen);
      Assert.False(File.Exists(ManifestFileFor(root, manifestBytes)));
      await Assert.ThrowsAnyAsync<WorkbenchResourceAccessException>(
        async () => await broker.OpenAsync(
          new Uri(projection.Manifest.Url), TestContext.Current.CancellationToken));
      Assert.NotNull(Assert.IsType<ImageEditWorkbenchState>(
        Assert.Single(ack.States.OfType<ImageEditWorkbenchState>())).Input);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task LegalOversizeStateManifestTravelsByControlledResource()
  {
    string root = TemporaryRoot();
    string projectPath = Path.Combine(root, "large.vibp");
    try
    {
      // 合去 ≤128 entry：source+manifest+1 patch；state >64KiB 且 ≤256KiB。
      VibpMark[] marks = [.. Enumerable.Range(1, 28).Select(index => new VibpMark
      {
        Id = $"mark-{index}",
        Tool = "text",
        Start = new VibpPoint { X = 1, Y = 1 },
        End = new VibpPoint { X = 2, Y = 2 },
        Text = new string('a', VibpProjectLimits.MaximumMarkTextLength),
        TextWidth = 100,
      })];
      byte[] source = VibpProjectFileTests.BuildPng(64, 48);
      byte[] patch = VibpProjectFileTests.BuildPng(4, 3);
      var manifest = new VibpProjectManifest
      {
        SchemaVersion = 1,
        Canvas = new VibpCanvas { Width = 64, Height = 48 },
        Source = new VibpSourceResource
        {
          MediaType = "image/png", Width = 64, Height = 48, Length = source.Length,
        },
        Assets = [],
        State = new VibpEditorState { Rotation = 0, Marks = marks, Patches =
        [
          new VibpPatch { Id = 1, X = 2, Y = 3, Width = 4, Height = 3, Length = patch.Length },
        ] },
      };
      VibpProjectFile.Write(projectPath, manifest,
      [
        new VibpProjectEntry("source.png", source.Length, () => new MemoryStream(source)),
        new VibpProjectEntry("patch-1.png", patch.Length, () => new MemoryStream(patch)),
      ]);

      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      using var store = new WorkbenchProjectStore();
      var openPicker = new PickerStub { OpenPath = projectPath };
      await using var opener = CreateHandler(broker, annotations, store, root, openPicker);

      var opened = await opener.ExecuteAsync(
        new OpenImageEditProjectCommand(), TestContext.Current.CancellationToken);

      // 不因 >64KiB 的合法 state 拒绝：状态帧仅引用+绑定，manifest 走受控 GET。
      Assert.Null(opened.Error);
      WorkbenchProjectOpenState projection = Assert.IsType<ImageEditWorkbenchState>(
        Assert.Single(opened.States.OfType<ImageEditWorkbenchState>())).ProjectOpen!;
      string frame = WorkbenchBridgeCodec.SerializeState(
        Guid.NewGuid(),
        new WorkbenchStateEnvelope(1, "imageEdit", WorkbenchStateChange.Replace,
          new ImageEditWorkbenchState(false, "recognition.session", ProjectOpen: projection)));
      Assert.True(System.Text.Encoding.UTF8.GetByteCount(frame) <= WorkbenchBridgeCodec.MaxMessageBytes);

      byte[] manifestBytes;
      await using (WorkbenchResourceResponse response = await broker.OpenAsync(
        new Uri(projection.Manifest.Url), TestContext.Current.CancellationToken))
      {
        using var buffered = new MemoryStream();
        await response.Content.CopyToAsync(buffered, TestContext.Current.CancellationToken);
        manifestBytes = buffered.ToArray();
      }
      VibpProjectManifest parsed = System.Text.Json.JsonSerializer.Deserialize<VibpProjectManifest>(
        manifestBytes, VibpProjectRules.ManifestJson)!;
      Assert.Equal(28, parsed.State.Marks.Count);
      Assert.All(parsed.State.Marks, mark => Assert.Equal(8192, mark.Text!.Length));

      // ack 后 manifest 与 patch 均回收（文件与租约），不降低工程能力。
      var ack = await opener.ExecuteAsync(
        new AckImageEditProjectOpenCommand(
          Guid.ParseExact(projection.PendingId, "N"), Guid.Empty, 0, projection.Generation),
        TestContext.Current.CancellationToken);
      Assert.Null(ack.Error);
      Assert.False(File.Exists(ManifestFileFor(root, manifestBytes)));
      Assert.Empty(PatchSessionFiles(root));
      await Assert.ThrowsAnyAsync<WorkbenchResourceAccessException>(
        async () => await broker.OpenAsync(
          new Uri(projection.Manifest.Url), TestContext.Current.CancellationToken));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  /// <summary>会话资源目录中与指定 manifest 字节一致的文件路径（无则 null）。</summary>
  private static string? ManifestFileFor(string root, byte[] manifestBytes)
  {
    string directory = Path.Combine(root, "session");
    if (!Directory.Exists(directory)) return null;
    return Directory.GetFiles(directory)
      .SingleOrDefault(file => File.ReadAllBytes(file).SequenceEqual(manifestBytes));
  }

  /// <summary>会话资源目录中与补丁内容一致的字节数。</summary>
  private static string[] PatchSessionFiles(string root)
  {
    string directory = Path.Combine(root, "session");
    if (!Directory.Exists(directory)) return [];
    return Directory.GetFiles(directory)
      .Where(file => File.ReadAllBytes(file).SequenceEqual(Patch))
      .ToArray();
  }

  /// <summary>会话资源目录中与源图内容一致的字节数（内容比对，不依赖 token 命名）。</summary>
  private static string[] SourceSessionFiles(string root)
  {
    string directory = Path.Combine(root, "session");
    if (!Directory.Exists(directory)) return [];
    return Directory.GetFiles(directory)
      .Where(file => File.ReadAllBytes(file).SequenceEqual(Source))
      .ToArray();
  }

  /// <summary>首读阻塞的门控流：驱动销毁与在途上传的代际竞争。</summary>
  private sealed class GatedJsonStream(string json) : Stream
  {
    private readonly TaskCompletionSource readStarted = new(
      TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource unblock = new(
      TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly byte[] content = System.Text.Encoding.UTF8.GetBytes(json);
    private int position;

    public Task ReadStarted => readStarted.Task;

    public void Unblock() => unblock.TrySetResult();

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => content.LongLength;

    public override long Position
    {
      get => position;
      set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override async ValueTask<int> ReadAsync(
      Memory<byte> buffer,
      CancellationToken cancellationToken = default)
    {
      readStarted.TrySetResult();
      await unblock.Task.WaitAsync(cancellationToken);
      int count = Math.Min(buffer.Length, content.Length - position);
      content.AsSpan(position, count).CopyTo(buffer.Span);
      position += count;
      return count;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
      throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }

  private static string TemporaryRoot()
  {
    string root = Path.Combine(
      Path.GetTempPath(),
      $"vibeocr-project-wiring-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    return root;
  }

  /// <summary>真实 PNG 输入（平台可解码），使保存路径的维度读取真实工作。</summary>
  private sealed class ProjectInputService : IInputService
  {
    public Task<RecognitionInput?> PickFileAsync(CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(new RecognitionInput(
        Source, "image/png", "edit.png", "file"));

    public Task<RecognitionInput?> ReadClipboardAsync(CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(new RecognitionInput(
        Source, "image/png", "clipboard.png", "clipboard"));

    public Task<RecognitionInput?> CaptureScreenAsync(CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(new RecognitionInput(
        Source, "image/png", "capture.png", "capture"));

    public Task<RecognitionInput?> ReadDroppedFileAsync(string path, CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(null);
  }

  private sealed class CompletingInferenceClient : InferenceClientStub;

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

  private sealed class StateAwaiter<TState> : IDisposable where TState : WorkbenchState
  {
    private readonly DesktopWorkbenchCommandHandler handler;
    private readonly Func<TState, bool> predicate;
    private readonly TaskCompletionSource<TState> ready =
      new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StateAwaiter(DesktopWorkbenchCommandHandler handler, Func<TState, bool> predicate)
    {
      this.handler = handler;
      this.predicate = predicate;
      handler.StateChanged += OnStateChanged;
    }

    public Task<TState> Task => ready.Task.WaitAsync(
      TimeSpan.FromSeconds(10),
      TestContext.Current.CancellationToken);

    private void OnStateChanged(WorkbenchState state)
    {
      if (state is TState typed && predicate(typed))
      {
        ready.TrySetResult(typed);
      }
    }

    public void Dispose() => handler.StateChanged -= OnStateChanged;
  }

  private static byte[] PngBytes(int width, int height) =>
    VibpProjectFileTests.BuildPng(width, height);
}

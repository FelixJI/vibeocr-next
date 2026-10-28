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
    public int SaveCalls;
    public int OpenCalls;
    public Func<CancellationToken, Task<string?>>? SaveOverride;
    public Func<CancellationToken, Task<string?>>? OpenOverride;

    public Func<CancellationToken, Task<string?>> SavePicker => token =>
    {
      SaveCalls++;
      return SaveOverride?.Invoke(token) ?? Task.FromResult(SavePath);
    };

    public Func<CancellationToken, Task<string?>> OpenPicker => token =>
    {
      OpenCalls++;
      return OpenOverride?.Invoke(token) ?? Task.FromResult(OpenPath);
    };
  }

  private static DesktopWorkbenchCommandHandler CreateHandler(
    WorkbenchResourceBroker broker,
    WorkbenchAnnotationStore annotationStore,
    WorkbenchProjectStore projectStore,
    string resourceRoot,
    PickerStub picker,
    string sourceMediaType = "image/png",
    Action<RecognitionViewModel>? observeViewModel = null)
  {
    var inference = new CompletingInferenceClient();
    return new DesktopWorkbenchCommandHandler(
      () =>
      {
        var model = new RecognitionViewModel(inference, new ProjectInputService(sourceMediaType));
        observeViewModel?.Invoke(model);
        return model;
      },
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
    WorkbenchProjectStore store,
    byte[]? patchPng = null)
  {
    MemoryStream body = new(JsonSerializer.SerializeToUtf8Bytes(new
    {
      canvas = new { width = 64, height = 48 },
      state = new { rotation = 0, marks = Array.Empty<object>() },
      patches = new object[]
      {
        new { id = 1, x = 2, y = 3, width = 4, height = 3, png = Convert.ToBase64String(patchPng ?? Patch) },
      },
    }));
    WorkbenchProjectContributionLease lease = await store.UploadAsync(
      body, TestContext.Current.CancellationToken);
    return (lease.ContributeId, body);
  }

  /// <summary>多补丁贡献（1×1 PNG，元数据小图）。</summary>
  private static MemoryStream BuildContribution(
    VibpCanvas canvas,
    object? state,
    params (int Id, int X, int Y, int Width, int Height, byte[] Png)[] patches)
  {
    return new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new
    {
      canvas = new { width = canvas.Width, height = canvas.Height },
      state = state ?? new { rotation = 0, marks = Array.Empty<object>() },
      patches = patches.Select(patch => new
      {
        id = patch.Id,
        x = patch.X,
        y = patch.Y,
        width = patch.Width,
        height = patch.Height,
        png = Convert.ToBase64String(patch.Png),
      }).ToArray(),
    }, VibpProjectRules.ManifestJson));
  }

  private static byte[] Png1X1() => PngBytes(1, 1);

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
  public async Task SaveProjectRefusesMismatchedSourceContainerAndKeepsTarget()
  {
    string root = TemporaryRoot();
    string projectPath = Path.Combine(root, "mismatched.vibp");
    try
    {
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      using var store = new WorkbenchProjectStore();
      var picker = new PickerStub { SavePath = projectPath };
      // 会话输入声明 image/jpeg 但真实字节是 PNG（生产错报容器）：保存
      // 必须在写入前拒绝，不产出不可重开的工程，原目标文件与会话保留。
      await File.WriteAllBytesAsync(projectPath, [0xAA, 0xBB, 0xCC],
        TestContext.Current.CancellationToken);
      byte[] originalTarget = await File.ReadAllBytesAsync(
        projectPath, TestContext.Current.CancellationToken);
      await using var handler = CreateHandler(
        broker, annotations, store, root, picker, sourceMediaType: "image/jpeg");
      Guid session = await EstablishSessionAsync(handler, WorkbenchRoute.ImageEdit);
      (Guid contributeId, _) = await ContributeAsync(store);

      WorkbenchCommandOutcome outcome = await handler.ExecuteAsync(
        new SaveImageEditProjectCommand(contributeId, session, 0),
        TestContext.Current.CancellationToken);

      Assert.Equal("project_format_invalid", outcome.Error?.Code);
      // 旧目标文件字节不变；会话仍可用（同会话正确声明后可再保存）。
      Assert.Equal(originalTarget, await File.ReadAllBytesAsync(
        projectPath, TestContext.Current.CancellationToken));
      Assert.Null(handler.CurrentProjectOpen);
      // 会话仍有效：同一会话/修订的新贡献再保存，仍确定性拒绝于同一容器
      // 根因（若会话被失败路径清除，这里会是 screenshot_session_stale）。
      (Guid second, _) = await ContributeAsync(store);
      WorkbenchCommandOutcome retried = await handler.ExecuteAsync(
        new SaveImageEditProjectCommand(second, session, 0),
        TestContext.Current.CancellationToken);
      Assert.Equal("project_format_invalid", retried.Error?.Code);
      Assert.Equal(originalTarget, await File.ReadAllBytesAsync(
        projectPath, TestContext.Current.CancellationToken));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  private sealed class GatedOpenPickerStub
  {
    private readonly TaskCompletionSource entered = new(
      TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string?> gate =
      new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>picker 已被调用（同步信号，避免猜时序）。</summary>
    public Task Entered => entered.Task;
    public Func<CancellationToken, Task<string?>> Picker => _ =>
    {
      entered.TrySetResult();
      return gate.Task;
    };
    public void Release(string? path) => gate.TrySetResult(path);
  }

  [Theory]
  [InlineData(true, true)]
  [InlineData(true, false)]
  [InlineData(false, true)]
  public async Task ProjectSaveAndOpenSharePickerSingleFlight(bool firstSave, bool secondSave)
  {
    string root = TemporaryRoot();
    try
    {
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      using var store = new WorkbenchProjectStore();
      var gate = new GatedOpenPickerStub();
      var picker = new PickerStub
      {
        SaveOverride = firstSave ? gate.Picker : null,
        OpenOverride = firstSave ? null : gate.Picker,
      };
      await using var handler = CreateHandler(broker, annotations, store, root, picker);
      Guid session = await EstablishSessionAsync(handler, WorkbenchRoute.ImageEdit);
      (Guid firstId, _) = await ContributeAsync(store);
      Task<WorkbenchCommandOutcome> first = handler.ExecuteAsync(firstSave
        ? new SaveImageEditProjectCommand(firstId, session, 0)
        : new OpenImageEditProjectCommand(), TestContext.Current.CancellationToken).AsTask();
      await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
      (Guid secondId, _) = await ContributeAsync(store);
      WorkbenchCommandOutcome second = await handler.ExecuteAsync(secondSave
        ? new SaveImageEditProjectCommand(secondId, session, 0)
        : new OpenImageEditProjectCommand(), TestContext.Current.CancellationToken);
      Assert.Equal("image_import_in_progress", second.Error?.Code);
      Assert.Equal(1, picker.SaveCalls + picker.OpenCalls);
      gate.Release(null);
      Assert.Equal("annotation_operation_cancelled", (await first).Error?.Code);
      // guard 释放且拒绝的第二次保存未消费贡献。
      WorkbenchCommandOutcome retried = await handler.ExecuteAsync(secondSave
        ? new SaveImageEditProjectCommand(secondId, session, 0)
        : new OpenImageEditProjectCommand(), TestContext.Current.CancellationToken);
      Assert.Equal("annotation_operation_cancelled", retried.Error?.Code);
      Assert.Equal(2, picker.SaveCalls + picker.OpenCalls);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Theory]
  [InlineData(true, false)]
  [InlineData(false, true)]
  public async Task PickerFailureReleasesSharedProjectGuard(bool saving, bool cancelled)
  {
    string root = TemporaryRoot();
    try
    {
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      using var store = new WorkbenchProjectStore();
      using var cancellation = new CancellationTokenSource();
      cancellation.Cancel();
      Func<CancellationToken, Task<string?>> fail = _ => cancelled
        ? Task.FromCanceled<string?>(cancellation.Token)
        : Task.FromException<string?>(new IOException("Picker failed."));
      var picker = new PickerStub
      {
        SaveOverride = saving ? fail : null,
        OpenOverride = saving ? null : fail,
      };
      await using var handler = CreateHandler(broker, annotations, store, root, picker);
      Guid session = await EstablishSessionAsync(handler, WorkbenchRoute.ImageEdit);
      (Guid contribution, _) = await ContributeAsync(store);
      WorkbenchCommand command = saving
        ? new SaveImageEditProjectCommand(contribution, session, 0)
        : new OpenImageEditProjectCommand();
      if (cancelled)
      {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
          handler.ExecuteAsync(command, TestContext.Current.CancellationToken).AsTask());
      }
      else
      {
        Assert.Equal("desktop_command_failed", (await handler.ExecuteAsync(command,
          TestContext.Current.CancellationToken)).Error?.Code);
      }
      picker.SaveOverride = null;
      picker.OpenOverride = null;
      (Guid retry, _) = await ContributeAsync(store);
      WorkbenchCommandOutcome next = await handler.ExecuteAsync(saving
        ? new OpenImageEditProjectCommand()
        : new SaveImageEditProjectCommand(retry, session, 0), TestContext.Current.CancellationToken);
      Assert.Equal("annotation_operation_cancelled", next.Error?.Code);
      Assert.Equal(2, picker.SaveCalls + picker.OpenCalls);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task SaveProjectRefusesCompleteManifestOverflowBeforePicker()
  {
    string root = TemporaryRoot();
    try
    {
      VibpMark[] marks = [.. Enumerable.Range(1, 32).Select(index => new VibpMark
      {
        Id = $"mark-{index}", Tool = "text",
        Start = new VibpPoint { X = 1, Y = 1 }, End = new VibpPoint { X = 2, Y = 2 },
        Text = new string('a', VibpProjectLimits.MaximumMarkTextLength), TextWidth = 100,
      })];
      var state = new VibpEditorState { Rotation = 0, Marks = marks };
      long excess = JsonSerializer.SerializeToUtf8Bytes(state, VibpProjectRules.ManifestJson).LongLength -
        WorkbenchProjectStore.MaximumStateBytes + 1;
      for (int i = marks.Length - 1; excess > 0; i--)
      {
        int removed = (int)Math.Min(excess, marks[i].Text!.Length - 1);
        marks[i] = marks[i] with { Text = marks[i].Text![..^removed] };
        excess -= removed;
      }
      Assert.Equal(WorkbenchProjectStore.MaximumStateBytes - 1,
        JsonSerializer.SerializeToUtf8Bytes(state, VibpProjectRules.ManifestJson).LongLength);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      using var store = new WorkbenchProjectStore();
      var picker = new PickerStub { SavePath = Path.Combine(root, "untouched.vibp") };
      File.WriteAllBytes(picker.SavePath, [11, 22]);
      await using var handler = CreateHandler(broker, annotations, store, root, picker);
      Guid session = await EstablishSessionAsync(handler, WorkbenchRoute.ImageEdit);
      using MemoryStream body = BuildContribution(new VibpCanvas { Width = 64, Height = 48 }, state);
      WorkbenchProjectContributionLease lease = await store.UploadAsync(body, TestContext.Current.CancellationToken);
      WorkbenchCommandOutcome saved = await handler.ExecuteAsync(
        new SaveImageEditProjectCommand(lease.ContributeId, session, 0), TestContext.Current.CancellationToken);
      Assert.Equal("project_format_invalid", saved.Error?.Code);
      Assert.Equal(0, picker.SaveCalls);
      Assert.Equal(new byte[] { 11, 22 }, File.ReadAllBytes(picker.SavePath));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task SaveProjectRefusesSourceLengthOverflowBeforePicker()
  {
    string root = TemporaryRoot();
    try
    {
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      using var store = new WorkbenchProjectStore();
      var picker = new PickerStub { SavePath = Path.Combine(root, "untouched.vibp") };
      File.WriteAllBytes(picker.SavePath, [11, 22]);
      await using var handler = CreateHandler(broker, annotations, store, root, picker);
      Guid session = await EstablishSessionAsync(handler, WorkbenchRoute.ImageEdit);
      string sourcePath = Assert.Single(SourceSessionFiles(root));
      using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Write))
      {
        source.SetLength(VibpProjectLimits.MaximumSourceBytes + 1);
      }
      (Guid contribution, _) = await ContributeAsync(store);
      WorkbenchCommandOutcome saved = await handler.ExecuteAsync(
        new SaveImageEditProjectCommand(contribution, session, 0), TestContext.Current.CancellationToken);
      Assert.Equal("project_format_invalid", saved.Error?.Code);
      Assert.Equal(0, picker.SaveCalls);
      Assert.Equal(new byte[] { 11, 22 }, File.ReadAllBytes(picker.SavePath));
      // 校验失败没有撤销旧会话；恢复fixture真实长度后同session仍能保存。
      using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Write))
      {
        source.SetLength(Source.Length);
      }
      (Guid retry, _) = await ContributeAsync(store);
      Assert.Null((await handler.ExecuteAsync(new SaveImageEditProjectCommand(retry, session, 0),
        TestContext.Current.CancellationToken)).Error);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task OpenProjectSingleFlightRejectsSecondPickerWhileFirstInFlight()
  {
    string root = TemporaryRoot();
    try
    {
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      using var store = new WorkbenchProjectStore();
      var gate = new GatedOpenPickerStub();
      var inference = new CompletingInferenceClient();
      await using var handler = new DesktopWorkbenchCommandHandler(
        () => new RecognitionViewModel(inference, new ProjectInputService()),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        () => new SettingsViewModel(inference),
        () => new ShellViewModel(new StubHotkeyRegistrar(), new StubStartupRegistrar()),
        static () => throw new InvalidOperationException(),
        new DiagnosticsViewModel("test", new PrerequisiteReport([])),
        broker,
        root,
        static () => 0,
        annotations,
        projectStore: store,
        projectOpenPicker: gate.Picker);

      Task<WorkbenchCommandOutcome> first = Task.Run(async () => await handler.ExecuteAsync(
        new OpenImageEditProjectCommand(), TestContext.Current.CancellationToken));
      // 同步等待第一个 picker 确实进入（无 delay/yield 猜时序）。
      await gate.Entered.WaitAsync(
        TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
      // 第二次请求同步拒绝：不叠加第二个原生 picker。
      WorkbenchCommandOutcome second = await handler.ExecuteAsync(
        new OpenImageEditProjectCommand(), TestContext.Current.CancellationToken);
      Assert.Equal("image_import_in_progress", second.Error?.Code);

      // 取消路径：释放 picker 为取消，单飞 guard 归零，后续可再次打开。
      gate.Release(null);
      WorkbenchCommandOutcome cancelled = await first.WaitAsync(
        TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
      Assert.Equal("annotation_operation_cancelled", cancelled.Error?.Code);
      WorkbenchCommandOutcome third = await handler.ExecuteAsync(
        new OpenImageEditProjectCommand(), TestContext.Current.CancellationToken);
      Assert.Equal("annotation_operation_cancelled", third.Error?.Code);
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Fact]
  public async Task SaveProjectRefusesEntryOverrunBeforePicker()
  {
    // 有效 ≤126 补丁 + marks 唯一引用 3 个资产：合计 2+3+126=131 > 128，
    // 宿主 picker 前拒绝（上传侧各单项均合法，不放宽上传限制）。
    byte[] png = Png1X1();
    string[] assetIds = new[] { "a", "b", "c" }.Select(prefix => prefix + new string('0', 31)).ToArray();
    MemoryStream body = new(JsonSerializer.SerializeToUtf8Bytes(new
    {
      canvas = new { width = 64, height = 48 },
      state = new
      {
        rotation = 0,
        marks = assetIds.Select((assetId, index) => new
        {
          id = $"mark-{index + 1}",
          tool = "image",
          start = new { x = 1, y = 1 },
          end = new { x = 20, y = 20 },
          assetId,
        }).ToArray(),
      },
      patches = Enumerable.Range(1, 126).Select(id => new
      {
        id,
        x = 0,
        y = 0,
        width = 1,
        height = 1,
        png = Convert.ToBase64String(png),
      }).ToArray(),
    }));

    string root = TemporaryRoot();
    string projectPath = Path.Combine(root, "overrun.vibp");
    try
    {
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      using var store = new WorkbenchProjectStore();
      var picker = new PickerStub { SavePath = projectPath };
      await using var handler = CreateHandler(broker, annotations, store, root, picker);
      Guid session = await EstablishSessionAsync(handler, WorkbenchRoute.ImageEdit);
      WorkbenchProjectContributionLease lease = await store.UploadAsync(
        body, TestContext.Current.CancellationToken);

      WorkbenchCommandOutcome outcome = await handler.ExecuteAsync(
        new SaveImageEditProjectCommand(lease.ContributeId, session, 0),
        TestContext.Current.CancellationToken);

      Assert.Equal("image_import_refused", outcome.Error?.Code);
      Assert.False(File.Exists(projectPath));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Theory]
  [InlineData("invalid-bit-depth")]
  [InlineData("invalid-color-type")]
  [InlineData("truncate")]
  [InlineData("drop-iend")]
  [InlineData("zeroed-idat")]
  [InlineData("random-idat")]
  [InlineData("bad-crc")]
  public async Task SaveProjectFullyDecodesPatchPngBeforeAtomicWrite(string mutation)
  {
    string root = TemporaryRoot();
    string projectPath = Path.Combine(root, $"bad-{mutation}.vibp");
    try
    {
      await File.WriteAllBytesAsync(projectPath, [0x12, 0x34],
        TestContext.Current.CancellationToken);
      byte[] originalTarget = await File.ReadAllBytesAsync(
        projectPath, TestContext.Current.CancellationToken);
      // 均能通过上传侧签名+IHDR 维度结构检查（旧实现会直接写出不可重开
      // 的工程）：无效 IHDR 编码由平台解码拒绝；截断/缺 IEND/IDAT 全零/
      // 全随机/坏 CRC 由共享结构+标准 CRC32 校验拒绝（防 WIC 宽容）。
      byte[] corrupted = mutation switch
      {
        "invalid-bit-depth" => Mutate(Patch, 24, 99),
        "invalid-color-type" => Mutate(Patch, 25, 99),
        "truncate" => Patch[..^30],
        "drop-iend" => Patch[..^12],
        "zeroed-idat" => ZeroIdat(Patch),
        "random-idat" => RandomizeIdat(Patch, 42),
        "bad-crc" => Mutate(Patch, Patch.Length - 6, (byte)(Patch[^6] ^ 0xFF)),
        _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
      };
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      using var store = new WorkbenchProjectStore();
      var picker = new PickerStub { SavePath = projectPath };
      await using var handler = CreateHandler(broker, annotations, store, root, picker);
      Guid session = await EstablishSessionAsync(handler, WorkbenchRoute.ImageEdit);
      (Guid contributeId, _) = await ContributeAsync(store, corrupted);

      WorkbenchCommandOutcome outcome = await handler.ExecuteAsync(
        new SaveImageEditProjectCommand(contributeId, session, 0),
        TestContext.Current.CancellationToken);

      Assert.Equal("project_format_invalid", outcome.Error?.Code);
      Assert.Equal(originalTarget, await File.ReadAllBytesAsync(
        projectPath, TestContext.Current.CancellationToken));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  private static byte[] Mutate(byte[] source, int offset, byte value)
  {
    byte[] mutated = [.. source];
    mutated[offset] = value;
    return mutated;
  }

  /// <summary>IDAT 数据区置零/随机化（chunk 头之后、尾 CRC 之前）。</summary>
  private static byte[] MutateIdat(byte[] png, Random? random)
  {
    byte[] mutated = [.. png];
    int idatDataStart = FindIdatDataStart(mutated);
    for (int i = idatDataStart; i < mutated.Length - 4; i++)
    {
      mutated[i] = random is null ? (byte)0 : (byte)random.Next(256);
    }
    return mutated;
  }

  private static byte[] ZeroIdat(byte[] png) => MutateIdat(png, null);

  private static byte[] RandomizeIdat(byte[] png, int seed) => MutateIdat(png, new Random(seed));

  private static int FindIdatDataStart(byte[] png)
  {
    // 签名 8 + IHDR 块（12+13）→ 下一 chunk 头 8 字节后即 IDAT 数据。
    int offset = 8 + 25 + 8;
    if (offset >= png.Length ||
      !png.AsSpan(offset - 4, 4).SequenceEqual("IDAT"u8))
    {
      throw new InvalidOperationException("fixture does not start IDAT at expected offset");
    }
    return offset;
  }

  /// <summary>平台实测诚实记录（TEMP/task233-r7-wic-tolerance-evidence.md）：
  /// WIC 对截断/缺 IEND/IDAT 全零/全随机均宽容；共享结构+CRC 校验在平台
  /// 解码前拒绝这些样本，无效 IHDR 编码仍由平台解码拒绝。</summary>

  [Fact]
  public async Task RestoredAssetsRetireWhenAckDocumentCancels()
  {
    string root = TemporaryRoot();
    string projectPath = Path.Combine(root, "retire.vibp");
    try
    {
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      using var store = new WorkbenchProjectStore();
      // 直接构造含 image 资产的包（保存链路由其他测试覆盖）。
      byte[] source = PngBytes(64, 48);
      byte[] assetPng = PngBytes(8, 6);
      string assetId = new string('a', 32);
      var manifest = new VibpProjectManifest
      {
        SchemaVersion = 1,
        Canvas = new VibpCanvas { Width = 64, Height = 48 },
        Source = new VibpSourceResource
        {
          MediaType = "image/png",
          Width = 64,
          Height = 48,
          Length = source.Length,
        },
        Assets = [new VibpAssetResource
        {
          Id = assetId, Width = 8, Height = 6, Length = assetPng.Length,
        }],
        State = new VibpEditorState
        {
          Rotation = 0,
          Marks = [new VibpMark
          {
            Id = "mark-1",
            Tool = "image",
            Start = new VibpPoint { X = 1, Y = 1 },
            End = new VibpPoint { X = 16, Y = 12 },
            AssetId = assetId,
          }],
          Patches = [],
        },
      };
      VibpProjectFile.Write(projectPath, manifest,
      [
        new VibpProjectEntry("source.png", source.Length, () => new MemoryStream(source)),
        new VibpProjectEntry($"asset-{assetId}.png", assetPng.Length,
          () => new MemoryStream(assetPng)),
      ]);

      var openPicker = new PickerStub { OpenPath = projectPath };
      await using var opener = CreateHandler(broker, annotations, store, root, openPicker);
      Assert.Null((await opener.ExecuteAsync(
        new OpenImageEditProjectCommand(),
        TestContext.Current.CancellationToken)).Error);
      WorkbenchImageOwner.ProjectOpenDescriptor? pending = opener.CurrentProjectOpen;
      Assert.NotNull(pending);
      // ack 携带受控文档 token（宿主路由）：恢复资产绑定该身份。
      using var document = CancellationTokenSource.CreateLinkedTokenSource(
        TestContext.Current.CancellationToken);
      WorkbenchCommandOutcome committed = await opener.ExecuteAsync(
        new AckImageEditProjectOpenCommand(
          pending.PendingId, pending.Binding.SessionId, pending.Binding.Revision,
          pending.Binding.Generation), document.Token);
      Assert.Null(committed.Error);
      Guid oldSession = Guid.Parse(Assert.Single(committed.States.OfType<ImageEditWorkbenchState>())
        .ScreenshotSession!.SessionId);
      Assert.NotEmpty(pending.Assets);
      string assetUrl = pending.Assets.Values.First().Url;
      Assert.True(await ResourceExistsAsync(broker, assetUrl));

      // 下一次打开在 Detached 回调中取消：旧文档资产不能先被 Clear 清掉。
      Assert.Null((await opener.ExecuteAsync(new OpenImageEditProjectCommand(),
        TestContext.Current.CancellationToken)).Error);
      WorkbenchImageOwner.ProjectOpenDescriptor replacement = opener.CurrentProjectOpen!;
      using var cancelledDocument = new CancellationTokenSource();
      void CancelSwitch(Guid session, long revision) => cancelledDocument.Cancel();
      opener.ScreenshotSessionDetached += CancelSwitch;
      WorkbenchCommandOutcome refused = await opener.ExecuteAsync(new AckImageEditProjectOpenCommand(
        replacement.PendingId, replacement.Binding.SessionId, replacement.Binding.Revision,
        replacement.Binding.Generation), cancelledDocument.Token);
      opener.ScreenshotSessionDetached -= CancelSwitch;
      Assert.Equal("project_open_conflict", refused.Error?.Code);
      Assert.True(await ResourceExistsAsync(broker, assetUrl));
      Assert.False(await ResourceExistsAsync(broker, replacement.Assets[assetId].Url));
      openPicker.SavePath = Path.Combine(root, "old-assets-survive.vibp");
      using (MemoryStream body = BuildContribution(manifest.Canvas, manifest.State))
      {
        WorkbenchProjectContributionLease lease = await store.UploadAsync(body, TestContext.Current.CancellationToken);
        Assert.Null((await opener.ExecuteAsync(new SaveImageEditProjectCommand(
          lease.ContributeId, oldSession, 0), TestContext.Current.CancellationToken)).Error);
      }
      VibpProjectArchive savedOld = await VibpProjectFile.ReadAsync(openPicker.SavePath!,
        Path.Combine(root, "old-assets-survive"), TestContext.Current.CancellationToken);
      Assert.Equal(manifest.State.Marks, savedOld.Manifest.State.Marks);
      Assert.Equal(assetPng, File.ReadAllBytes(savedOld.AssetPaths[assetId]));

      // 文档取消（导航/恢复/重载）：既有 RetireDocumentImports 精确回收
      // 恢复资产租约与文件。
      document.Cancel();
      await Assert.ThrowsAsync<WorkbenchResourceAccessException>(async () =>
        await broker.OpenAsync(new Uri(assetUrl), TestContext.Current.CancellationToken));
      // 具体核对目标资产文件（按内容匹配）已被删除，而非任意首个文件。
      string sessionDirectory = Path.Combine(root, "session");
      if (Directory.Exists(sessionDirectory))
      {
        Assert.DoesNotContain(Directory.GetFiles(sessionDirectory), file =>
          File.ReadAllBytes(file).SequenceEqual(assetPng));
      }
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task AckFailureBeforeCommitKeepsOldSessionInputAndAssets(bool callbackThrows)
  {
    string root = TemporaryRoot();
    string projectPath = Path.Combine(root, "race.vibp");
    try
    {
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      using var store = new WorkbenchProjectStore();
      // 含 image 资产的真实包。
      byte[] source = PngBytes(64, 48);
      byte[] assetPng = PngBytes(8, 6);
      string assetId = new string('b', 32);
      var manifest = new VibpProjectManifest
      {
        SchemaVersion = 1,
        Canvas = new VibpCanvas { Width = 64, Height = 48 },
        Source = new VibpSourceResource
        {
          MediaType = "image/png",
          Width = 64,
          Height = 48,
          Length = source.Length,
        },
        Assets = [new VibpAssetResource
        {
          Id = assetId, Width = 8, Height = 6, Length = assetPng.Length,
        }],
        State = new VibpEditorState
        {
          Rotation = 0,
          Marks = [new VibpMark
          {
            Id = "mark-1",
            Tool = "image",
            Start = new VibpPoint { X = 1, Y = 1 },
            End = new VibpPoint { X = 16, Y = 12 },
            AssetId = assetId,
          }],
          Patches = [],
        },
      };
      VibpProjectFile.Write(projectPath, manifest,
      [
        new VibpProjectEntry("source.png", source.Length, () => new MemoryStream(source)),
        new VibpProjectEntry($"asset-{assetId}.png", assetPng.Length,
          () => new MemoryStream(assetPng)),
      ]);

      // 同一 handler 建立旧会话与真实 VM 输入。
      var openPicker = new PickerStub { OpenPath = projectPath };
      RecognitionViewModel? oldModel = null;
      await using var opener = CreateHandler(broker, annotations, store, root, openPicker,
        observeViewModel: model => oldModel = model);
      Guid priorSession = await EstablishSessionAsync(opener, WorkbenchRoute.ImageEdit);
      Assert.NotNull(oldModel);
      RecognitionInput oldInput = Assert.IsType<RecognitionInput>(oldModel.CurrentInput);
      string oldSourcePath = Assert.Single(SourceSessionFiles(root));
      Assert.Null((await opener.ExecuteAsync(
        new OpenImageEditProjectCommand(),
        TestContext.Current.CancellationToken)).Error);
      WorkbenchImageOwner.ProjectOpenDescriptor? pending = opener.CurrentProjectOpen;
      Assert.NotNull(pending);

      using var document = CancellationTokenSource.CreateLinkedTokenSource(
        TestContext.Current.CancellationToken);
      // 确定性取消点：Detached 外部回调返回前，必须尚未释放旧数据。
      void InterruptCommit(Guid session, long revision)
      {
        if (callbackThrows) throw new IOException("Detached subscriber failed.");
        document.Cancel();
      }
      opener.ScreenshotSessionDetached += InterruptCommit;

      WorkbenchCommandOutcome cancelled = await opener.ExecuteAsync(
        new AckImageEditProjectOpenCommand(
          pending.PendingId, pending.Binding.SessionId, pending.Binding.Revision,
          pending.Binding.Generation), document.Token);
      opener.ScreenshotSessionDetached -= InterruptCommit;
      Assert.Equal(callbackThrows ? "desktop_command_failed" : "project_open_conflict", cancelled.Error?.Code);
      Assert.Same(oldInput, oldModel.CurrentInput);
      Assert.Equal(Source, File.ReadAllBytes(oldSourcePath));
      Assert.Null(opener.CurrentProjectOpen);
      Assert.False(await ResourceExistsAsync(broker, pending.Source.Url));
      Assert.False(await ResourceExistsAsync(broker, pending.Assets[assetId].Url));
      // 用旧会话真正保存并重开，证明没有只保留一个孤立 source。
      openPicker.SavePath = Path.Combine(root, "old-survives.vibp");
      (Guid contribution, _) = await ContributeAsync(store);
      WorkbenchCommandOutcome saved = await opener.ExecuteAsync(
        new SaveImageEditProjectCommand(contribution, priorSession, 0),
        TestContext.Current.CancellationToken);
      Assert.Null(saved.Error);
      VibpProjectArchive oldArchive = await VibpProjectFile.ReadAsync(openPicker.SavePath!,
        Path.Combine(root, "old-survives"), TestContext.Current.CancellationToken);
      Assert.Equal(Source, File.ReadAllBytes(oldArchive.SourcePath));
      // 随后成功提交才释放旧 VM 输入和旧资源。
      Assert.Null((await opener.ExecuteAsync(new OpenImageEditProjectCommand(),
        TestContext.Current.CancellationToken)).Error);
      WorkbenchImageOwner.ProjectOpenDescriptor retryOpen = opener.CurrentProjectOpen!;
      Assert.Null((await opener.ExecuteAsync(new AckImageEditProjectOpenCommand(
        retryOpen.PendingId, retryOpen.Binding.SessionId, retryOpen.Binding.Revision,
        retryOpen.Binding.Generation), TestContext.Current.CancellationToken)).Error);
      Assert.Null(oldModel.CurrentInput);
      Assert.Null(oldModel.ResultInput);
      Assert.False(File.Exists(oldSourcePath));
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }

  private static async Task<bool> ResourceExistsAsync(
    WorkbenchResourceBroker broker, string url)
  {
    try
    {
      await using WorkbenchResourceResponse response = await broker.OpenAsync(
        new Uri(url), TestContext.Current.CancellationToken);
      return true;
    }
    catch (WorkbenchResourceAccessException)
    {
      return false;
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
  public async Task AckAfterOldCanvasEditConflictsAndReclaimsPending()
  {
    string root = TemporaryRoot();
    try
    {
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      using var store = new WorkbenchProjectStore();
      var picker = new PickerStub { SavePath = Path.Combine(root, "conflict.vibp") };
      await using var handler = CreateHandler(broker, annotations, store, root, picker);
      Guid session = await EstablishSessionAsync(handler, WorkbenchRoute.ImageEdit);
      (Guid contribution, _) = await ContributeAsync(store);
      Assert.Null((await handler.ExecuteAsync(
        new SaveImageEditProjectCommand(contribution, session, 0),
        TestContext.Current.CancellationToken)).Error);
      picker.OpenPath = picker.SavePath;
      Assert.Null((await handler.ExecuteAsync(
        new OpenImageEditProjectCommand(), TestContext.Current.CancellationToken)).Error);
      WorkbenchImageOwner.ProjectOpenDescriptor pending = handler.CurrentProjectOpen!;
      Assert.Equal(2, SourceSessionFiles(root).Length);
      WorkbenchCommandOutcome edit = await handler.ExecuteAsync(
        new NotifyImageEditRevisionCommand(session, 1), TestContext.Current.CancellationToken);
      ImageEditWorkbenchState edited = Assert.Single(edit.States.OfType<ImageEditWorkbenchState>());
      WorkbenchCommandOutcome ack = await handler.ExecuteAsync(
        new AckImageEditProjectOpenCommand(
          pending.PendingId, session, 0, pending.Binding.Generation),
        TestContext.Current.CancellationToken);
      Assert.Equal("project_open_conflict", ack.Error?.Code);
      Assert.Empty(ack.States);
      Assert.Null(handler.CurrentProjectOpen);
      Assert.Single(SourceSessionFiles(root));
      Assert.Empty(PatchSessionFiles(root));
      await Assert.ThrowsAnyAsync<WorkbenchResourceAccessException>(async () =>
        await broker.OpenAsync(new Uri(pending.Source.Url), TestContext.Current.CancellationToken));
      // 原会话/修订仍可保存，输入不曾被打开工程覆盖。
      (Guid second, _) = await ContributeAsync(store);
      WorkbenchCommandOutcome saved = await handler.ExecuteAsync(
        new SaveImageEditProjectCommand(second, session, 1),
        TestContext.Current.CancellationToken);
      Assert.Null(saved.Error);
      ImageEditWorkbenchState current = Assert.Single(saved.States.OfType<ImageEditWorkbenchState>());
      Assert.Equal(edited.Input, current.Input);
      Assert.Equal(edited.ScreenshotSession, current.ScreenshotSession);
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
    const string assetId = "0123456789abcdef0123456789abcdef";
    byte[] asset = PngBytes(8, 6);
    try
    {
      VibpMark mark = new()
      {
        Id = "mark-1",
        Tool = "image",
        AssetId = assetId,
        Start = new() { X = 2, Y = 3 },
        End = new() { X = 20, Y = 18 },
      };
      var manifest = new VibpProjectManifest
      {
        SchemaVersion = 1,
        Canvas = new() { Width = 64, Height = 48 },
        Source = new() { MediaType = "image/png", Width = 64, Height = 48, Length = Source.Length },
        Assets = [new() { Id = assetId, Width = 8, Height = 6, Length = asset.Length }],
        State = new()
        {
          Rotation = 0,
          Marks = [mark],
          Patches = [new() { Id = 1, X = 2, Y = 3, Width = 4, Height = 3, Length = Patch.Length }],
        },
      };
      VibpProjectFile.Write(projectPath, manifest,
      [
        new("source.png", Source.Length, () => new MemoryStream(Source)),
        new($"asset-{assetId}.png", asset.Length, () => new MemoryStream(asset)),
        new("patch-1.png", Patch.Length, () => new MemoryStream(Patch)),
      ]);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      using var store = new WorkbenchProjectStore();
      var picker = new PickerStub { OpenPath = projectPath, SavePath = Path.Combine(root, "resaved.vibp") };
      await using var opener = CreateHandler(broker, annotations, store, root, picker);
      Assert.Null((await opener.ExecuteAsync(
        new OpenImageEditProjectCommand(), TestContext.Current.CancellationToken)).Error);
      WorkbenchImageOwner.ProjectOpenDescriptor pending = opener.CurrentProjectOpen!;
      Assert.Single(PatchSessionFiles(root));
      Assert.Single(pending.Assets);

      WorkbenchCommandOutcome ack = await opener.ExecuteAsync(
        new AckImageEditProjectOpenCommand(
          pending.PendingId, pending.Binding.SessionId, pending.Binding.Revision,
          pending.Binding.Generation), TestContext.Current.CancellationToken);
      Assert.Null(ack.Error);
      Assert.Empty(PatchSessionFiles(root));
      Assert.Single(SourceSessionFiles(root));
      await Assert.ThrowsAnyAsync<WorkbenchResourceAccessException>(async () =>
        await broker.OpenAsync(new Uri(pending.Patches[1].Url), TestContext.Current.CancellationToken));
      await using (WorkbenchResourceResponse response = await broker.OpenAsync(
        new Uri(pending.Assets[assetId].Url), TestContext.Current.CancellationToken))
      {
        using var bytes = new MemoryStream();
        await response.Content.CopyToAsync(bytes, TestContext.Current.CancellationToken);
        Assert.Equal(asset, bytes.ToArray());
      }

      Guid session = Guid.Parse(Assert.Single(ack.States.OfType<ImageEditWorkbenchState>())
        .ScreenshotSession!.SessionId);
      using var body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new
      {
        canvas = manifest.Canvas,
        state = new { rotation = 0, marks = new[] { mark }, patches = Array.Empty<object>() },
        patches = new[] { new { id = 1, x = 2, y = 3, width = 4, height = 3, png = Convert.ToBase64String(Patch) } },
      }, VibpProjectRules.ManifestJson));
      WorkbenchProjectContributionLease lease = await store.UploadAsync(body, TestContext.Current.CancellationToken);
      Assert.Null((await opener.ExecuteAsync(
        new SaveImageEditProjectCommand(lease.ContributeId, session, 0),
        TestContext.Current.CancellationToken)).Error);
      VibpProjectArchive reopened = await VibpProjectFile.ReadAsync(
        picker.SavePath!, Path.Combine(root, "reopened"), TestContext.Current.CancellationToken);
      Assert.Equal(assetId, Assert.Single(reopened.Manifest.State.Marks).AssetId);
      Assert.Equal(asset, File.ReadAllBytes(reopened.AssetPaths[assetId]));
      Assert.Equal(Patch, File.ReadAllBytes(reopened.PatchPaths[1]));
      Assert.Null((await opener.ExecuteAsync(
        new CloseImageEditSessionCommand(), TestContext.Current.CancellationToken)).Error);
      Assert.Empty(SourceSessionFiles(root));
      await Assert.ThrowsAnyAsync<WorkbenchResourceAccessException>(async () =>
        await broker.OpenAsync(new Uri(pending.Assets[assetId].Url), TestContext.Current.CancellationToken));
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
      // 事件帧必须真正携带 projectOpen（含绑定与全部受控引用，含 manifest URL）。
      Assert.Contains("\"projectOpen\":{", frame);
      Assert.Contains($"\"pendingId\":\"{projection.PendingId}\"", frame);
      Assert.Contains($"\"sessionId\":\"{projection.SessionId}\"", frame);
      Assert.Contains(projection.Manifest.Url, frame);
      Assert.Contains(projection.Source.Url, frame);
      Assert.Contains(projection.Patches.Single().Resource.Url, frame);

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
          MediaType = "image/png",
          Width = 64,
          Height = 48,
          Length = source.Length,
        },
        Assets = [],
        State = new VibpEditorState
        {
          Rotation = 0,
          Marks = marks,
          Patches =
        [
          new VibpPatch { Id = 1, X = 2, Y = 3, Width = 4, Height = 3, Length = patch.Length },
        ]
        },
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
      // 大清单工程的事件帧同样必须携带 projectOpen 与 manifest URL。
      Assert.Contains("\"projectOpen\":{", frame);
      Assert.Contains(projection.Manifest.Url, frame);

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

  /// <summary>真实 PNG 输入（平台可解码）；媒体类型可参数化以模拟
  /// 声明容器与真实字节不符（PNG 改名 .jpg）的生产错误。</summary>
  private sealed class ProjectInputService(string mediaType = "image/png") : IInputService
  {
    public Task<RecognitionInput?> PickFileAsync(CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(new RecognitionInput(
        Source, mediaType, "edit.png", "file"));

    public Task<RecognitionInput?> ReadClipboardAsync(CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(new RecognitionInput(
        Source, mediaType, "clipboard.png", "clipboard"));

    public Task<RecognitionInput?> CaptureScreenAsync(CancellationToken cancellationToken) =>
      Task.FromResult<RecognitionInput?>(new RecognitionInput(
        Source, mediaType, "capture.png", "capture"));

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
    public bool IsEnabled => false;
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

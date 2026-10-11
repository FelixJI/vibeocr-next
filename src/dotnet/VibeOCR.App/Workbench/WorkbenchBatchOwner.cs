using VibeOCR.App.Features.Batch;
using VibeOCR.App.Features.Settings;
using VibeOCR.App.Services;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace VibeOCR.App.Workbench;

internal sealed class WorkbenchBatchOwner(Func<BatchViewModel> batchFactory, Func<nint> windowHandle, WorkbenchSettingsOwner settingsOwner, WorkbenchResourcesOwner resources, WorkbenchSelectionOwner selectionPolicy, WorkbenchOperations operations)
{
  internal bool Handles(WorkbenchCommand command) => command is AddBatchFilesCommand or AddDroppedBatchFilesCommand or ExportBatchCommand or StartBatchCommand or CancelBatchCommand or ClearBatchCommand or MoveBatchItemCommand or RemoveBatchItemCommand or SetBatchItemPageRangeCommand or SetBatchWindowCommand or SetBatchTaskEngineCommand;

  internal async ValueTask<WorkbenchState?> ExecuteAsync(WorkbenchCommand command, CancellationToken cancellationToken) => command switch
  {
    AddBatchFilesCommand => await AddBatchFilesAsync(cancellationToken),
    AddDroppedBatchFilesCommand dropped => await AddDroppedBatchFilesAsync(
      dropped,
      cancellationToken),
    ExportBatchCommand export => await ExportBatchAsync(export, cancellationToken),
    StartBatchCommand => await StartBatchAsync(cancellationToken),
    CancelBatchCommand => CancelBatch(),
    ClearBatchCommand => ClearBatch(),
    MoveBatchItemCommand move => await MoveBatchItemAsync(move, cancellationToken),
    RemoveBatchItemCommand remove => await RemoveBatchItemAsync(remove, cancellationToken),
    SetBatchItemPageRangeCommand range => SetBatchItemPageRange(range),
    SetBatchWindowCommand window => await SetBatchWindowAsync(window, cancellationToken),
    SetBatchTaskEngineCommand taskEngine => SetBatchTaskEngine(taskEngine),
    _ => throw new InvalidOperationException("Unsupported Batch workbench command."),
  };

  private readonly Dictionary<Guid, (RecognizeResponse Result, WorkbenchResourceReference Reference)> batchStructured = [];
  private BatchViewModel? batch;
  private string? batchTaskEngine;
  private bool batchExportIncomplete;
  private long batchGeneration;
  private CancellationTokenSource? batchPreparation;
  private int batchWindowStart;
  private async Task<BatchWorkbenchState> AddBatchFilesAsync(
    CancellationToken cancellationToken)
  {
    batch ??= batchFactory();
    await batch.PickFilesAsync(cancellationToken);
    return await BatchStateAsync(batch, cancellationToken);
  }

  private async Task<BatchWorkbenchState> AddDroppedBatchFilesAsync(
    AddDroppedBatchFilesCommand command,
    CancellationToken cancellationToken)
  {
    batch ??= batchFactory();
    batch.AddFiles(command.Paths);
    return await BatchStateAsync(batch, cancellationToken);
  }

  private async Task<BatchWorkbenchState> ExportBatchAsync(
    ExportBatchCommand command,
    CancellationToken cancellationToken)
  {
    batch ??= batchFactory();
    var picker = new FolderPicker
    {
      SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
    };
    picker.FileTypeFilter.Add("*");
    WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle());
    StorageFolder? folder = await picker.PickSingleFolderAsync();
    if (folder is not null)
    {
      IReadOnlyList<ExportResult> exports = await batch.ExportAllAsync(
        folder.Path, command.Format, cancellationToken);
      batchExportIncomplete = exports.Any(result => result.Incomplete is true);
    }
    return await BatchStateAsync(batch, cancellationToken);
  }

  private Task<BatchWorkbenchState?> StartBatchAsync(
    CancellationToken cancellationToken)
  {
    batch ??= batchFactory();
    if (batchPreparation is not null || batch.IsRunning) return Task.FromResult<BatchWorkbenchState?>(BatchState(batch));
    if (settingsOwner.IsEnvironmentSwitching) return Task.FromResult<BatchWorkbenchState?>(BatchState(batch));
    if (settingsOwner.CurrentModel?.RecognitionSelection is not null) SynchronizeBatchMode(requireUsable: true);
    var preparation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, operations.Token);
    batchPreparation = preparation;
    long generation = Interlocked.Increment(ref batchGeneration);
    BatchWorkbenchState start = new(
      true,
      batch.Items.Count,
      batch.CompletedCount,
      batch.FailedCount);
    return Task.FromResult(operations.Start(start,
      () => CompleteBatchAsync(generation, preparation)));
  }

  private async Task CompleteBatchAsync(
    long generation,
    CancellationTokenSource preparation)
  {
    CancellationToken cancellationToken = preparation.Token;
    // 终态在 finally 清理 preparation 后重算并只发布一次，避免清理前的伪运行终态与重复 terminal 触发 mailbox resync。
    try
    {
      if (!await selectionPolicy.EnsureSelectionLoadedForSubmitAsync(cancellationToken)) return;
      cancellationToken.ThrowIfCancellationRequested();
      if (generation != Volatile.Read(ref batchGeneration)) return;
      SynchronizeBatchMode(requireUsable: true);
      await batch!.StartAsync(cancellationToken);
      // BatchStateAsync 只为发布/缓存结构化结果资源，终态由 finally 重算。
      await BatchStateAsync(batch, cancellationToken);
    }
    catch (OperationCanceledException) { }
    catch (Exception error)
    {
      AppLog.Error("Batch workbench operation failed", error);
    }
    finally
    {
      bool ownsPreparation = ReferenceEquals(batchPreparation, preparation);
      if (ownsPreparation) batchPreparation = null;
      preparation.Dispose();
      if (ownsPreparation) operations.PublishTerminal(BatchState(batch!));
    }
  }

  private BatchWorkbenchState CancelBatch()
  {
    batch ??= batchFactory();
    Interlocked.Increment(ref batchGeneration);
    batchPreparation?.Cancel();
    batch.CancelAll();
    return BatchState(batch);
  }

  private BatchWorkbenchState ClearBatch()
  {
    batch ??= batchFactory();
    Interlocked.Increment(ref batchGeneration);
    batchPreparation?.Cancel();
    batch.ResetTemporaryQueue();
    batchExportIncomplete = false;
    batchWindowStart = 0;
    batchStructured.Clear();
    return BatchState(batch);
  }

  private async Task<BatchWorkbenchState> MoveBatchItemAsync(
    MoveBatchItemCommand command,
    CancellationToken cancellationToken)
  {
    batch ??= batchFactory();
    if (batch.IsRunning)
    {
      throw new InvalidOperationException("A running batch cannot be reordered.");
    }
    batch.Move(command.ItemId, command.Delta);
    return await BatchStateAsync(batch, cancellationToken);
  }

  private BatchWorkbenchState SetBatchItemPageRange(SetBatchItemPageRangeCommand command)
  {
    batch ??= batchFactory();
    batch.SetPageRange(command.ItemId, command.PageRange);
    batchStructured.Remove(command.ItemId);
    return BatchState(batch);
  }

  private async Task<BatchWorkbenchState> RemoveBatchItemAsync(
    RemoveBatchItemCommand command,
    CancellationToken cancellationToken)
  {
    batch ??= batchFactory();
    batch.Remove(command.ItemId);
    batchStructured.Remove(command.ItemId);
    return await BatchStateAsync(batch, cancellationToken);
  }

  private async Task<BatchWorkbenchState> SetBatchWindowAsync(
    SetBatchWindowCommand command,
    CancellationToken cancellationToken)
  {
    batch ??= batchFactory();
    batchWindowStart = DesktopWorkbenchCommandHandler.ClampWindowStart(command.Start, batch.Items.Count, 40);
    return await BatchStateAsync(batch, cancellationToken);
  }

  private BatchWorkbenchState SetBatchTaskEngine(SetBatchTaskEngineCommand command)
  {
    batch ??= batchFactory();
    if (batch.IsRunning) throw new InvalidOperationException("A running batch cannot change recognition mode.");
    RecognitionSelectionSnapshot? snapshot = settingsOwner.CurrentModel?.RecognitionSelection;
    RuntimeSelectionService? selection = snapshot?.Catalog;
    if (command.Engine is not null)
    {
      if (snapshot is null || selection?.SupportsRecognitionModes is not true)
        throw new RuntimeSelectionException(RuntimeSelectionErrorKind.CapabilityMissing,
          "The runtime does not provide recognition modes.");
      RecognitionModeOption mode = selection.SelectRecognitionMode(command.Engine);
      snapshot.MineruConfigFor(mode.Id);
    }
    batchTaskEngine = command.Engine;
    SynchronizeBatchMode();
    return BatchState(batch);
  }

  internal void SynchronizeBatchMode(bool requireUsable = false)
  {
    RecognitionSelectionSnapshot? snapshot = settingsOwner.CurrentModel?.RecognitionSelection;
    if (batch is null || snapshot?.Catalog.SupportsRecognitionModes is not true)
    {
      if (requireUsable) WorkbenchSelectionOwner.EnsureUsableModeOverrideOrThrow(selection: snapshot?.Catalog, taskEngine: batchTaskEngine);
      batch?.SetRecognitionMode(null, taskModeId: batchTaskEngine);
      return;
    }
    RuntimeSelectionService selection = snapshot.Catalog;
    string? effectiveId = WorkbenchSelectionOwner.EffectiveModeIdOrDefault(
      snapshot, batchTaskEngine, requireUsable);
    RecognitionModeOption? mode = string.IsNullOrWhiteSpace(effectiveId) ? null : requireUsable
      ? selection.SelectRecognitionMode(effectiveId)
      : WorkbenchSelectionOwner.TryFindRecognitionMode(selection, effectiveId);
    MineruConfig? config = requireUsable
      ? snapshot.MineruConfigFor(mode?.Id)
      : WorkbenchSelectionOwner.TryProjectMineruConfig(snapshot, mode?.Id, out MineruConfig? projected)
        ? projected : null;
    // 原生 Office 仅 flash 档可整篇解析：flash 可用性随配置一起冻结给
    // 批量任务，提交时按原生格式分组，不改 PDF/图片默认档。
    bool mineruFlashUsable = mode is not null && selection.IsMineruTierUsable(MineruTier.Flash);
    batch.SetRecognitionMode(mode, config, selectionPolicy.GetModeOptions(mode), batchTaskEngine, mineruFlashUsable);
  }

  /// <summary>
  /// PDF 页面 OCR 与单次/批量共享同一模式合同：切换目录/环境后重投影，
  /// 提交前 requireUsable 严格协商；显式选择的模式在当前环境不可用时
  /// 拒绝提交并指向环境，不静默回退通用文字识别。
  /// </summary>
  internal BatchWorkbenchState CurrentBatchState() => batch is null
    ? new BatchWorkbenchState(false, 0, 0, 0, Engines: BatchEngines())
    : BatchState(batch);

  private IReadOnlyList<RecognitionEngineChoice>? BatchEngines()
  {
    string? defaultId = selectionPolicy.RuntimeDefaultModeId;
    return selectionPolicy.RecognitionEngines()?.Select(choice => choice with
    {
      // IsDefault 从目录投影继承；Selected 仅显式选择或（未覆盖时）默认。
      Selected = choice.Engine == batchTaskEngine ||
        (batchTaskEngine is null && choice.Engine == defaultId),
      IsTaskOverride = choice.Engine == batchTaskEngine,
    }).ToArray();
  }

  private BatchWorkbenchState BatchState(BatchViewModel viewModel)
  {
    SynchronizeBatchMode();
    batchWindowStart = DesktopWorkbenchCommandHandler.ClampWindowStart(batchWindowStart, viewModel.Items.Count, 40);
    return new BatchWorkbenchState(
    viewModel.IsRunning || batchPreparation is not null,
    viewModel.Items.Count,
    viewModel.CompletedCount,
    viewModel.FailedCount,
    viewModel.Items
      .Skip(batchWindowStart)
      .Take(40)
      .Select(item => new BatchWorkbenchItem(
        item.Id,
        DesktopWorkbenchCommandHandler.Truncate(item.Name, 80),
        $"batch.item.{item.State.ToString().ToLowerInvariant()}",
        item.Result is not null
          ? DesktopWorkbenchCommandHandler.Truncate(item.Result.Text, 120)
          : item.State == BatchItemState.Failed && item.Error is not null
            ? DesktopWorkbenchCommandHandler.Truncate(item.Error, 120)
            : null,
        item.State == BatchItemState.Completed && item.Result is not null
          ? batchStructured.GetValueOrDefault(item.Id).Reference
          : null,
        item.PageRange,
        !BatchCommands.IsNativeOfficeDocument(item.Path)))
      .ToArray(),
    batchWindowStart,
    BatchEngines(),
    batchTaskEngine,
    batchExportIncomplete,
    BatchInputKindNotice(viewModel));
  }

  /// <summary>含 Word/Excel/PowerPoint 文档且为 MinerU 模式时的档位说明。</summary>
  private static string? BatchInputKindNotice(BatchViewModel viewModel) =>
    viewModel is { HasNativeOfficeInputs: true, IsMineruDocumentMode: true }
      ? viewModel.MineruFlashTierAvailable
        ? "Word、Excel、PowerPoint 文档使用 Flash 档整篇解析；PDF 和图片沿用当前档位。"
        : "包含 Word、Excel 或 PowerPoint 文档：这些文档需要在 Flash 档解析，当前环境暂不可用；请在设置中检查 MinerU 连接后重试。"
      : null;

  /// <summary>
  /// 可视窗口内已完成项的结构化结果发布：以 Result 对象身份为键缓存，
  /// 重跑/移除后旧引用不再复用；与单次/PDF 共用同一发布路径。
  /// </summary>
  private async Task<BatchWorkbenchState> BatchStateAsync(
    BatchViewModel viewModel,
    CancellationToken cancellationToken)
  {
    batchWindowStart = DesktopWorkbenchCommandHandler.ClampWindowStart(batchWindowStart, viewModel.Items.Count, 40);
    foreach (BatchItemViewModel item in viewModel.Items.Skip(batchWindowStart).Take(40))
    {
      if (item.State != BatchItemState.Completed ||
        item.Result?.ContentBlocks is not { Length: > 0 })
      {
        batchStructured.Remove(item.Id);
        continue;
      }
      if (batchStructured.TryGetValue(item.Id, out var cached) &&
        ReferenceEquals(cached.Result, item.Result))
      {
        continue;
      }
      WorkbenchResourceReference? reference = await resources.PublishStructuredResultAsync(
        item.Result.ContentBlocks,
        viewModel.FetchResultAssetAsync,
        cancellationToken);
      if (reference is null)
      {
        batchStructured.Remove(item.Id);
      }
      else
      {
        batchStructured[item.Id] = (item.Result, reference);
      }
    }
    return BatchState(viewModel);
  }

  internal void Stop() { Interlocked.Increment(ref batchGeneration); batchPreparation?.Cancel(); batch?.CancelAll(); }
  internal void Release() => batchStructured.Clear();
}

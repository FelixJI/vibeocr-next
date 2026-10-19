using System.Text.Json;
using VibeOCR.App.Features.QrCode;
using VibeOCR.App.Features.Recognition;
using Windows.Graphics.Imaging;
using VibeOCR.App.Web;

namespace VibeOCR.App.Workbench;

/// <summary>
/// #238 pin owned 快照写入器（Features/Pins 实现）：宿主完成贡献校验与
/// manifest 组装后，由写入器把 .vibp 工程与最终合成 PNG 原子落盘到该贴图
/// 独立 owned 目录。写入器不得触碰 pins 目录之外的任何路径。
/// </summary>
internal interface IPinSnapshotWriter
{
  /// <summary>工程、PNG 与最终坐标系 OCR 排除框一并提交；任一失败保留旧快照。</summary>
  Task WriteSnapshotAsync(
    Guid pinId,
    VibpProjectManifest manifest,
    IReadOnlyList<VibpProjectEntry> entries,
    byte[] finalPng,
    CancellationToken cancellationToken,
    IReadOnlyList<WorkbenchExclusionBox>? excludeBoxes = null);

  /// <summary>该贴图当前快照 PNG 的 owned 路径；从未编辑过为 null。</summary>
  string? GetSnapshotPngPath(Guid pinId);

  string? GetSnapshotProjectPath(Guid pinId) => null;

  /// <summary>删除该贴图 owned 快照目录；仅清本应用拥有的固定子目录。</summary>
  void DeleteSnapshot(Guid pinId);
}

/// <summary>历史条目图像读取结果：重开贴图所需的字节与显示尺寸。</summary>
internal sealed record PinHistoryImage(byte[] Png, int Width, int Height);

/// <summary>
/// #238 截图历史记录器（Features/Pins 实现）：显式启用后记录成功截图会话
/// （含未钉图），默认关闭；条数/总字节双上限，最旧先淘汰；单条超界明确
/// 拒绝且不影响截图流程。一次截图一次成功只录一条（captureKey 去重）。
/// </summary>
internal interface IPinHistoryRecorder
{
  bool IsEnabled { get; }

  /// <summary>设置开关并原子持久化；关闭不清除既有条目。</summary>
  void SetEnabled(bool enabled);

  /// <summary>成功截图录入（best-effort：任何存储失败只丢条目不抛出）。</summary>
  void RecordSuccessfulCapture(string captureKey, byte[] png);

  /// <summary>捕获图归一化失败：只更新历史提示，不回滚成功截图。</summary>
  void ReportRecordingFailure(string message);

  /// <summary>用已有识别/取字文本充实条目（无自动 OCR；仅覆盖空文本）。</summary>
  void EnrichRecognizedText(string captureKey, string text);

  /// <summary>当前投影（可带检索词过滤名称/日期/已存文本）。</summary>
  PinHistoryState Projection(string? query);

  /// <summary>读取条目图像字节与尺寸；缺失/损坏返回 null（可跳过）。</summary>
  PinHistoryImage? TryReadImage(string entryId);

  /// <summary>删除单条（仅 owned 目录内该条目）；不存在返回 false。</summary>
  bool DeleteEntry(string entryId);

  /// <summary>清空全部条目，返回删除条数；只触碰 owned 目录清单。</summary>
  int ClearAll();
}

/// <summary>
/// #238 贴图内编辑（WorkbenchImageOwner 分部）：独立 pinEdit 通道的会话
/// 建立/快照应用/历史管理。与 recognition/imageEdit 通道完全隔离：本分部
/// 发布的状态全部为 pinEdit scope，保存成功 ack 前必须已落盘。
/// </summary>
internal sealed partial class WorkbenchImageOwner
{
  /// <summary>pin 快照单飞：与工程保存单飞分离，不占用 picker 事务槽。</summary>
  private int pinSnapshotInFlight;

  /// <summary>当前 pinEdit 会话绑定的贴图 id；无会话为 null。</summary>
  private Guid? pinEditPinId;
  private WorkbenchProjectOpenState? pinProjectRestore;
  private WorkbenchResourceReference? pinFlatInput;
  private readonly List<WorkbenchResourceReference> pinProjectTransient = [];
  private int pinOpenInFlight;

  /// <summary>快照成功应用：sessionId/revision 绑定 + pin owned 快照已落盘。
  /// 宿主（MainWindow 协调器）据此换装贴图静态内容并失效其旧文字层。</summary>
  internal event Action<Guid, long, Guid>? PinSnapshotApplied;

  /// <summary>历史条目重开请求：宿主负责贴图创建（含 4 张上限预检）。</summary>
  internal event Func<PinHistoryImage, Task>? PinHistoryReopenRequested;
  internal event Action? PinManagerRequested;

  private WorkbenchState ShowPinManager()
  { PinManagerRequested?.Invoke(); return CurrentPinEditState(); }

  /// <summary>贴图编辑会话被关闭（放弃路径）：宿主恢复静态现场并释放槽。</summary>
  internal event Action<Guid>? PinEditSessionClosed;

  /// <summary>进入单编辑槽：为贴图当前 PNG 建立独立 pinEdit 会话。</summary>
  internal async Task<Guid?> TryOpenPinEditSessionAsync(
    Guid pinId,
    byte[] png,
    CancellationToken cancellationToken,
    IReadOnlyList<WorkbenchExclusionBox>? excludeBoxes = null)
  {
    WorkbenchExclusionBox.ValidateCollection(excludeBoxes ?? []);
    if (pinSnapshotWriter is null)
    {
      throw new InvalidOperationException("Pin editing is unavailable.");
    }
    if (pinEditChannel.SessionId is not null || Interlocked.Exchange(ref pinOpenInFlight, 1) != 0)
    {
      // 宿主协调器保证单槽；此处兜底拒绝并发开槽。
      return null;
    }
    try
    {
      WorkbenchResourceReference input = await resources.PublishBytesAsync(
        png, "image/png", ".png", cancellationToken);
      AdvanceImageGeneration(pinEditChannel);
      ClearScreenshotSession(pinEditChannel, notifyDetached: false);
      pinEditChannel.SessionId = Guid.NewGuid();
      pinEditChannel.Revision = 0;
      pinEditChannel.SceneEditing = false;
      pinEditChannel.SessionInput = input;
      pinEditChannel.ExcludeBoxes = excludeBoxes ?? [];
      pinEditPinId = pinId;
      if (pinSnapshotWriter.GetSnapshotProjectPath(pinId) is { } projectPath)
      {
        try { await RestorePinProjectAsync(projectPath, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
          // .vibp 损坏/不兼容/读取失败发生在恢复描述符发布前（前端无机会拒收）：
          // 回退扁平 PNG 会话，原 .vibp 保留供后续恢复或诊断；部分发布的工程
          // 资源与已采纳导入全部回收，不泄漏；真正取消继续外抛。
          VibeOCR.App.Services.AppLog.Warn($"Pin project restore failed; flat pin remains editable: {error.Message}");
          FallBackToFlatPinEditInput();
        }
      }
      operations.Publish(PinEditState(false, "pinEdit.session"));
      return pinEditChannel.SessionId;
    }
    catch
    {
      ClearPinProjectRestore();
      ClearScreenshotSession(pinEditChannel, notifyDetached: false);
      pinEditPinId = null;
      throw;
    }
    finally { Volatile.Write(ref pinOpenInFlight, 0); }
  }

  private async Task RestorePinProjectAsync(string path, CancellationToken cancellationToken)
  {
    string extraction = Path.Combine(Path.GetTempPath(), "vibeocr-project-open", Guid.NewGuid().ToString("N"));
    try
    {
      VibpProjectArchive archive = await VibpProjectFile.ReadAsync(path, extraction, cancellationToken);
      WorkbenchResourceReference source = await resources.PublishBytesAsync(
        await File.ReadAllBytesAsync(archive.SourcePath, cancellationToken), archive.Manifest.Source.MediaType,
        WorkbenchResourcesOwner.ExtensionForMediaType(archive.Manifest.Source.MediaType), cancellationToken);
      pinFlatInput = pinEditChannel.SessionInput;
      pinEditChannel.SessionInput = source;
      List<WorkbenchProjectOpenAsset> assets = [];
      foreach (VibpAssetResource asset in archive.Manifest.Assets)
      {
        WorkbenchResourceReference reference = await resources.PublishBytesAsync(
          await File.ReadAllBytesAsync(archive.AssetPaths[asset.Id], cancellationToken), "image/png", ".png", cancellationToken);
        lock (pinEditImport) pinEditImport.Adopted[reference.Url] = new StagedImportAsset(
          reference, asset.Id, (long)asset.Width * asset.Height, asset.Width, asset.Height, CancellationToken.None);
        assets.Add(new(asset.Id, reference));
      }
      List<WorkbenchProjectOpenPatch> patches = [];
      foreach ((int id, string patchPath) in archive.PatchPaths)
      {
        WorkbenchResourceReference reference = await resources.PublishBytesAsync(
          await File.ReadAllBytesAsync(patchPath, cancellationToken), "image/png", ".png", cancellationToken);
        pinProjectTransient.Add(reference);
        patches.Add(new(id, reference));
      }
      WorkbenchResourceReference manifest = await resources.PublishBytesAsync(
        JsonSerializer.SerializeToUtf8Bytes(archive.Manifest, VibpProjectRules.ManifestJson),
        "application/json; charset=utf-8", ".json", cancellationToken);
      pinProjectTransient.Add(manifest);
      pinProjectRestore = new(Guid.NewGuid().ToString("N"), pinEditChannel.SessionId!.Value.ToString("N"),
        0, Volatile.Read(ref pinEditChannel.Generation), manifest, source, assets, patches);
    }
    finally { TryDeleteProjectDirectory(extraction); }
  }

  private WorkbenchState RejectPinProjectRestore(RejectPinProjectRestoreCommand command)
  {
    ValidateScreenshotSession(pinEditChannel, command.SessionId, command.Revision);
    if (pinProjectRestore?.PendingId != command.PendingId.ToString("N") || pinFlatInput is not { } flat)
      throw new ScreenshotSessionStaleException();
    if (pinEditChannel.SessionInput is { } source) resources.ReleaseResource(source);
    pinEditChannel.SessionInput = flat;
    pinFlatInput = null;
    ReleaseChannelImports(pinEditImport);
    ClearPinProjectRestore();
    AdvanceImageGeneration(pinEditChannel);
    return PinEditState(false, "pinEdit.session");
  }

  /// <summary>工程恢复失败回退：恢复扁平输入、回收部分发布的工程资源与已采纳
  /// 导入，保留无工程投影的 pinEdit 会话（与拒收路径同一资源终态）。</summary>
  private void FallBackToFlatPinEditInput()
  {
    if (pinFlatInput is { } flat)
    {
      if (pinEditChannel.SessionInput is { } current) resources.ReleaseResource(current);
      pinEditChannel.SessionInput = flat;
      pinFlatInput = null;
    }
    ReleaseChannelImports(pinEditImport);
    ClearPinProjectRestore();
  }

  private void ClearPinProjectRestore()
  {
    pinProjectRestore = null;
    if (pinFlatInput is { } flat) resources.ReleaseResource(flat);
    pinFlatInput = null;
    foreach (WorkbenchResourceReference reference in pinProjectTransient) resources.ReleaseResource(reference);
    pinProjectTransient.Clear();
  }

  private WorkbenchState ClosePinEditSession()
  {
    if (Volatile.Read(ref pinSnapshotInFlight) != 0) throw new ImageImportInProgressException();
    Guid? closedSession = pinEditChannel.SessionId;
    pinEditPinId = null;
    ClearPinProjectRestore();
    WorkbenchState state = CloseScreenshotSession(pinEditChannel);
    if (closedSession is { } id)
    {
      // 换装/放弃路径不走 ScreenshotSessionDetached（那会触发旧会话失效
      // 语义）；专用事件让协调器恢复静态现场。
      PinEditSessionClosed?.Invoke(id);
      operations.PublishTerminal(PinEditState(false, "pinEdit.closed"));
    }
    return state;
  }

  private async Task<WorkbenchState> CopyPinEditAnnotatedImageAsync(
    CopyPinEditAnnotatedImageCommand command,
    CancellationToken cancellationToken)
  {
    using WorkbenchAnnotationFile annotation = annotationStore.Take(
      new Uri(command.ResourceUri));
    await annotatedImagePlatform.CopyImageAsync(annotation.Path, cancellationToken);
    return CurrentPinEditState();
  }

  private async Task<WorkbenchState> SavePinEditAnnotatedImageAsync(
    SavePinEditAnnotatedImageCommand command,
    CancellationToken cancellationToken)
  {
    using WorkbenchAnnotationFile annotation = annotationStore.Take(
      new Uri(command.ResourceUri));
    if (!await annotatedImagePlatform.SaveImageAsync(annotation.Path, cancellationToken))
    {
      throw new AnnotatedImageOperationCancelledException();
    }
    return CurrentPinEditState();
  }

  /// <summary>
  /// 贴图编辑完成：消费 /__project 贡献与最终 PNG annotation，复用工程
  /// 序列化校验后原子写入 pin owned 快照；成功 ack 前已落盘，随后事件通知
  /// 协调器换装并结束槽内会话。任一失败保留编辑现场（会话与贡献外状态
  /// 不变，可重试）。迟到会话/修订拒绝（现场仍由当前会话持有）。
  /// </summary>
  private async Task<WorkbenchState?> ApplyPinSnapshotAsync(
    ApplyPinEditSnapshotCommand command,
    CancellationToken cancellationToken)
  {
    if (pinSnapshotWriter is null)
    {
      throw new ImageImportRefusedException("workbench.error.imageImportUnavailable");
    }
    if (Interlocked.Exchange(ref pinSnapshotInFlight, 1) != 0)
    {
      throw new ImageImportInProgressException();
    }
    bool publishedBusy = false;
    try
    {
      ValidateScreenshotSession(pinEditChannel, command.SessionId, command.Revision);
      IReadOnlyList<WorkbenchExclusionBox> snapshotExclusions = command.ExcludeBoxes ?? pinEditChannel.ExcludeBoxes;
      WorkbenchExclusionBox.ValidateCollection(snapshotExclusions);
      WorkbenchProjectContribution? contribution = projectStore?.TryTake(command.ContributeId);
      if (contribution is null)
      {
        throw new ImageImportRefusedException("workbench.error.screenshotSessionStale");
      }
      operations.Publish(PinEditState(true, "pinEdit.applying"));
      publishedBusy = true;
      // 最终合成 PNG：annotation 单次消费，读入内存后立即归还租约。
      using WorkbenchAnnotationFile annotation = annotationStore.Take(
        new Uri(command.ResourceUri));
      byte[] finalPng = await File.ReadAllBytesAsync(annotation.Path, cancellationToken);
      Guid pinId = pinEditPinId ?? throw new ScreenshotSessionStaleException();
      await PersistProjectSnapshotAsync(pinEditChannel, pinEditImport, contribution,
        command.SessionId, command.Revision, (manifest, entries) =>
          pinSnapshotWriter.WriteSnapshotAsync(pinId, manifest, entries, finalPng, cancellationToken, snapshotExclusions),
        cancellationToken);
      PinSnapshotApplied?.Invoke(command.SessionId, command.Revision, pinId);
      pinEditPinId = null;
      ClearPinProjectRestore();
      ClearScreenshotSession(pinEditChannel, notifyDetached: false);
      operations.PublishTerminal(PinEditState(false, "pinEdit.applied"));
      return null;
    }
    catch (InvalidDataException)
    {
      throw new ImageImportRefusedException("workbench.error.imageImportRefused");
    }
    finally
    {
      try
      {
        if (publishedBusy)
        {
          operations.PublishTerminal(CurrentPinEditState());
        }
      }
      finally
      {
        Volatile.Write(ref pinSnapshotInFlight, 0);
      }
    }
  }

  // ---- #238 截图历史：开关/检索/重开/删除/清空（无编辑会话时也可用）。 ----

  private PinHistoryState? PinHistoryProjection() =>
    pinHistory?.Projection(null);

  private WorkbenchState SetPinHistoryEnabled(SetPinHistoryEnabledCommand command)
  {
    if (pinHistory is null)
    {
      throw new ImageImportRefusedException("workbench.error.imageImportUnavailable");
    }
    pinHistory.SetEnabled(command.Enabled);
    return PinEditState(false, "pinEdit.historyUpdated");
  }

  private WorkbenchState ListPinHistory(ListPinHistoryCommand command)
  {
    if (pinHistory is null)
    {
      throw new ImageImportRefusedException("workbench.error.imageImportUnavailable");
    }
    return PinEditState(false, SessionStatusCode(pinEditChannel)) with
    {
      History = pinHistory.Projection(command.Query),
    };
  }

  private async Task<WorkbenchState> ReopenPinHistoryEntryAsync(
    ReopenPinHistoryEntryCommand command,
    CancellationToken cancellationToken)
  {
    if (pinHistory is null)
    {
      throw new ImageImportRefusedException("workbench.error.imageImportUnavailable");
    }
    PinHistoryImage? image = pinHistory.TryReadImage(command.EntryId);
    if (image is null)
    {
      throw new ImageImportRefusedException("workbench.error.imageImportUnavailable");
    }
    // 会话身份由协调器在 UI 线程创建贴图时分配；此处仅回读事件。
    if (PinHistoryReopenRequested is not { } reopen)
      throw new ImageImportRefusedException("workbench.error.imageImportUnavailable");
    cancellationToken.ThrowIfCancellationRequested();
    await reopen(image);
    return PinEditState(false, "pinEdit.historyReopenRequested");
  }

  private WorkbenchState DeletePinHistoryEntry(DeletePinHistoryEntryCommand command)
  {
    if (pinHistory is null)
    {
      throw new ImageImportRefusedException("workbench.error.imageImportUnavailable");
    }
    if (!pinHistory.DeleteEntry(command.EntryId))
    {
      throw new ImageImportRefusedException("workbench.error.imageImportUnavailable");
    }
    return PinEditState(false, "pinEdit.historyUpdated");
  }

  private WorkbenchState ClearPinHistory()
  {
    if (pinHistory is null)
    {
      throw new ImageImportRefusedException("workbench.error.imageImportUnavailable");
    }
    pinHistory.ClearAll();
    return PinEditState(false, "pinEdit.historyUpdated");
  }

  private void EnrichHistoryText(ImageSessionChannel channel, string text)
  {
    if (pinHistory is null || channel.HistoryCaptureKey is not { } key || string.IsNullOrWhiteSpace(text)) return;
    try
    {
      pinHistory.EnrichRecognizedText(key, text);
      operations.Publish(PinEditState(false, "pinEdit.historyUpdated"));
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
    { VibeOCR.App.Services.AppLog.Warn($"History OCR text persistence failed: {error.Message}"); }
  }

  private Task<bool> RecordCapturedHistoryAsync(
    string captureKey, RecognitionInput input, long generation, CancellationToken cancellationToken) =>
    RecordHistoryImageAsync(captureKey, input.Data, input.MediaType,
      () => generation == Volatile.Read(ref recognitionChannel.Generation), cancellationToken);

  private async Task<bool> RecordHistoryImageAsync(
    string captureKey, byte[] data, string mediaType, Func<bool> isCurrent,
    CancellationToken cancellationToken)
  {
    bool Current() => !cancellationToken.IsCancellationRequested && isCurrent();
    if (!Current()) return false;
    if (pinHistory?.IsEnabled != true) return true;
    try
    {
      // 仅归一化历史副本；OCR/编辑保留原始捕获 BMP，普通复制/保存
      // 保留用户选择的 JPEG 字节。
      byte[] png = mediaType == "image/png" ? data :
        await QrCodeSavePlatform.EncodeImageAsync(data, BitmapEncoder.PngEncoderId, cancellationToken);
      if (!Current()) return false;
      RecordPinHistory(captureKey, png);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return false; }
    catch (Exception error)
    {
      if (!Current()) return false;
      ReportHistoryFailure(error);
    }
    return Current();
  }

  private void ReportHistoryFailure(Exception error)
  {
    pinHistory?.ReportRecordingFailure($"截图已成功，但历史保存失败：{error.Message}");
    operations.Publish(PinEditState(false, "pinEdit.historyUpdated"));
    VibeOCR.App.Services.AppLog.Warn($"Screenshot history recording failed: {error.Message}");
  }

  private async Task RecordHistorySnapshotAsync(
    ImageSessionChannel channel, WorkbenchAnnotationFile image, string? captureKey, CancellationToken cancellationToken)
  {
    bool Current() => captureKey is not null && channel.HistoryCaptureKey == captureKey;
    if (!Current() || pinHistory?.IsEnabled != true) return;
    try
    {
      byte[] data = await File.ReadAllBytesAsync(image.Path, cancellationToken);
      await RecordHistoryImageAsync(captureKey!, data, image.MediaType, Current, cancellationToken);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
    { if (Current()) ReportHistoryFailure(error); }
  }

  /// <summary>统一 PNG 历史录入：捕获归一化与成功输出复用同一去重存储。</summary>
  private void RecordPinHistory(string captureKey, byte[] png)
  {
    if (pinHistory?.IsEnabled != true)
    {
      return;
    }
    try
    {
      pinHistory.RecordSuccessfulCapture(captureKey, png);
      operations.Publish(PinEditState(false, "pinEdit.historyUpdated"));
    }
    catch (Exception error)
    {
      // best-effort：历史录入失败绝不影响截图流程。
      VibeOCR.App.Services.AppLog.Warn(
        $"Pin history recording failed for capture {captureKey}: {error.Message}");
    }
  }
}

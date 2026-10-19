using System.Text.Json;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Web;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace VibeOCR.App.Workbench;

/// <summary>#233 工程保存/打开的宿主实现（WorkbenchImageOwner 分部）：
/// 保存贡献一次性消费 + 会话持有资源直读 + picker 授权 + 原子写；
/// 打开完整解析→pending 资源发布→ack 绑定验证后原子切换。</summary>
internal sealed partial class WorkbenchImageOwner
{
  private readonly WorkbenchProjectOpenTransaction projectOpen = new();

  /// <summary>当前 pending 打开描述符：供 Web 阶段状态投影与接线测试消费；
  /// ack 提交后置空（资源所有权移交会话），终止由事务回调回收。</summary>
  internal ProjectOpenDescriptor? CurrentProjectOpen => Volatile.Read(ref currentProjectOpen);

  private ProjectOpenDescriptor? currentProjectOpen;
  /// <summary>保存/打开共享单飞：包含 picker await，取消/异常/完成释放。</summary>
  private int projectOperationInFlight;

  internal sealed record ProjectOpenDescriptor(
    Guid PendingId,
    WorkbenchProjectOpenBinding Binding,
    VibpProjectManifest Manifest,
    WorkbenchResourceReference ManifestResource,
    WorkbenchResourceReference Source,
    IReadOnlyDictionary<string, WorkbenchResourceReference> Assets,
    IReadOnlyDictionary<int, WorkbenchResourceReference> Patches);

  internal event Action<Guid, string>? ProjectOpenTerminated
  {
    add => projectOpen.PendingTerminated += value;
    remove => projectOpen.PendingTerminated -= value;
  }

  /// <summary>pending 投影：仅绑定与受控资源引用（manifest 语义由 Web GET
  /// manifest 获得）；ack 成功后置空，Web 持有已 decode 数据。</summary>
  private WorkbenchProjectOpenState? ProjectOpenProjection() =>
    Volatile.Read(ref currentProjectOpen) is { } pending
      ? BuildProjectOpenProjection(pending)
      : null;

  private static WorkbenchProjectOpenState BuildProjectOpenProjection(
    ProjectOpenDescriptor descriptor) => new(
    descriptor.PendingId.ToString("N"),
    descriptor.Binding.SessionId.ToString("N"),
    descriptor.Binding.Revision,
    descriptor.Binding.Generation,
    descriptor.ManifestResource,
    descriptor.Source,
    [.. descriptor.Manifest.Assets.Select(asset => new WorkbenchProjectOpenAsset(
      asset.Id, descriptor.Assets[asset.Id]))],
    [.. descriptor.Manifest.State.Patches.Select(patch => new WorkbenchProjectOpenPatch(
      patch.Id, descriptor.Patches[patch.Id]))]);

  /// <summary>事务终止（取消/超时/替换）回调：清理描述符并经既有状态邮箱发布
  /// 终态（可能来自计时器线程，不直接触碰 UI 状态）。</summary>
  internal void OnProjectOpenTerminated(Guid pendingId, string reason)
  {
    if (Volatile.Read(ref currentProjectOpen) is { PendingId: var id } && id == pendingId)
    {
      Volatile.Write(ref currentProjectOpen, null);
    }
    operations.PublishTerminal(ImageEditState(
      false,
      reason == "timeout" ? "imageEdit.projectOpenTimeout" : "imageEdit.projectOpenCancelled"));
  }

  /// <summary>Stop/销毁路径：取消 pending 并释放其资源（旧会话已由通道回收）。</summary>
  private void ReleaseProjectOpen()
  {
    ProjectOpenDescriptor? descriptor = Volatile.Read(ref currentProjectOpen);
    if (descriptor is not null)
    {
      projectOpen.Cancel(descriptor.PendingId);
    }
    projectOpen.Dispose();
  }

  private async Task<WorkbenchState?> SaveProjectAsync(
    ImageSessionChannel channel, ImportChannelState import, Guid contributeId,
    Guid sessionId, long revision, CancellationToken cancellationToken)
  {
    if (projectStore is null || projectSavePicker is null)
      throw new ImageImportRefusedException("workbench.error.imageImportUnavailable");
    if (Interlocked.Exchange(ref projectOperationInFlight, 1) != 0)
      throw new ImageImportInProgressException();
    bool publishedBusy = false;
    try
    {
      ValidateScreenshotSession(channel, sessionId, revision);
      WorkbenchProjectContribution contribution = projectStore.TryTake(contributeId) ??
        throw new ImageImportRefusedException("workbench.error.screenshotSessionStale");
      operations.Publish(SessionState(channel, true, SessionStatusCode(channel)));
      publishedBusy = true;
      long generation = Volatile.Read(ref channel.Generation);
      await PersistProjectSnapshotAsync(channel, import, contribution, sessionId, revision,
        async (manifest, entries) =>
        {
          string? path = await projectSavePicker(cancellationToken);
          if (path is null) throw new AnnotatedImageOperationCancelledException();
          if (channel.SessionId != sessionId || channel.Revision != revision ||
            generation != Volatile.Read(ref channel.Generation)) throw new ScreenshotSessionStaleException();
          await VibpProjectFile.WriteAsync(path, manifest, entries, cancellationToken);
        }, cancellationToken);
      return CurrentChannelState(channel);
    }
    finally
    {
      try { if (publishedBusy) operations.PublishTerminal(CurrentChannelState(channel)); }
      finally { Volatile.Write(ref projectOperationInFlight, 0); }
    }
  }

  // Shared serialization/decoder boundary for picker projects and owned pin snapshots.
  private async Task PersistProjectSnapshotAsync(
    ImageSessionChannel channel, ImportChannelState import, WorkbenchProjectContribution contribution,
    Guid sessionId, long revision,
    Func<VibpProjectManifest, IReadOnlyList<VibpProjectEntry>, Task> persist,
    CancellationToken cancellationToken)
  {
    // entry 总预算前置（picker 前）：manifest+source+被引用资产+补丁 ≤128。
    // 只计当前 marks 唯一引用的资产；Web 侧同界早拒，宿主仍独立收紧。
    HashSet<string> referencedAssetIds = [..
      contribution.State.Marks.Select(mark => mark.AssetId).OfType<string>()];
    if (2L + referencedAssetIds.Count + contribution.Patches.Count >
      VibpProjectLimits.MaximumEntries)
    {
      throw new ImageImportRefusedException("workbench.error.imageImportRefused");
    }
    // 写入所需身份先快照：picker 异步期间以此再核验，防替换/GC 竞态。
    long generation = Volatile.Read(ref channel.Generation);
    WorkbenchResourceReference? input = channel.SessionInput;
    if (input is null)
    {
      throw new ImageImportRefusedException("workbench.error.imageImportRefused");
    }
    List<(WorkbenchResourceReference Reference, string AssetId, int Width, int Height)> held = [];
    lock (import)
    {
      foreach (StagedImportAsset asset in import.Adopted.Values)
      {
        if (asset.Pixels > 0)
        {
          held.Add((asset.Reference, asset.AssetId, asset.Width, asset.Height));
        }
      }
    }

    if (!resources.TryGetSessionFile(input.Url, out string? sourcePath) || !File.Exists(sourcePath))
    {
      throw new ImageImportRefusedException("workbench.error.imageImportRefused");
    }
    // 保存期间持有真实文件句柄；会话回收可删除路径，但不能截断在途读取。
    List<FileStream> opened = [];
    try
    {
      FileStream source = new(sourcePath, FileMode.Open, FileAccess.Read,
        FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
      opened.Add(source);
      long sourceLength = source.Length;
      if (sourceLength < 1 || sourceLength > VibpProjectLimits.MaximumSourceBytes)
      {
        throw new VibpProjectFormatException(
          "Project source entry length is outside the byte budget.");
      }
      string sourceEntryName = VibpProjectRules.SourceEntryName(input.MediaType);
      List<VibpAssetResource> assetResources = [];
      List<VibpProjectEntry> entries = [new(sourceEntryName, sourceLength, () => source)];
      foreach ((WorkbenchResourceReference reference, string assetId, int width, int height) in held)
      {
        if (!referencedAssetIds.Contains(assetId))
        {
          continue;
        }
        if (!string.Equals(reference.MediaType, "image/png", StringComparison.Ordinal) ||
          !resources.TryGetSessionFile(reference.Url, out string? assetPath))
        {
          throw new ImageImportRefusedException("workbench.error.imageImportRefused");
        }
        FileStream asset = new(assetPath, FileMode.Open, FileAccess.Read,
          FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        opened.Add(asset);
        long assetLength = asset.Length;
        assetResources.Add(new VibpAssetResource
        {
          Id = assetId,
          Width = width,
          Height = height,
          Length = assetLength,
        });
        entries.Add(new($"asset-{assetId}.png", assetLength, () => asset));
      }
      (int sourceWidth, int sourceHeight) =
        await ReadProjectImageDimensionsAsync(sourcePath, input.MediaType, cancellationToken);
      foreach (WorkbenchProjectPatchBytes patch in contribution.Patches)
      {
        byte[] png = patch.Png;
        entries.Add(new($"patch-{patch.Id}.png", png.LongLength,
          () => new MemoryStream(png, writable: false)));
      }
      // 补丁字节直接来自 Web 上传：原子写入前用真实平台 decoder 完整验证
      // （codec/尺寸 + 全像素解码，捕获缺 IDAT/IEND/截断/损坏）；预算先限
      // 1MP/8MP 再逐块解码，取消随 token 传播。
      long decodedPixels = 0;
      foreach (WorkbenchProjectPatchBytes patch in contribution.Patches)
      {
        long pixels = (long)patch.Width * patch.Height;
        if (pixels > VibpProjectLimits.MaximumPatchPixels)
        {
          throw new VibpProjectFormatException(
            $"Project patch {patch.Id} exceeds the per-patch pixel budget.");
        }
        decodedPixels += pixels;
        if (decodedPixels > VibpProjectLimits.MaximumPatchAggregatePixels)
        {
          throw new VibpProjectFormatException(
            "Project patches exceed the aggregate pixel budget.");
        }
        await ValidatePatchPngAsync(patch, cancellationToken);
      }

      var manifest = new VibpProjectManifest
      {
        SchemaVersion = VibpProjectLimits.SchemaVersion,
        Canvas = contribution.Canvas,
        Source = new VibpSourceResource
        {
          MediaType = input.MediaType,
          Width = sourceWidth,
          Height = sourceHeight,
          Length = sourceLength,
        },
        Assets = assetResources,
        State = contribution.State with
        {
          Patches = [.. contribution.Patches.Select(patch => new VibpPatch
          {
            Id = patch.Id,
            X = patch.X,
            Y = patch.Y,
            Width = patch.Width,
            Height = patch.Height,
            Length = patch.Png.LongLength,
          })],
        },
      };
      // 使用最终写入的同一完整 manifest 计算预算，不为上传 state 猜固定裕量。
      VibpProjectRules.ValidateManifest(manifest);
      if (JsonSerializer.SerializeToUtf8Bytes(manifest, VibpProjectRules.ManifestJson).LongLength >
        VibpProjectLimits.MaximumManifestBytes)
      {
        throw new VibpProjectFormatException("Project manifest exceeds the byte budget.");
      }
      cancellationToken.ThrowIfCancellationRequested();
      if (channel.SessionId != sessionId || channel.Revision != revision ||
        generation != Volatile.Read(ref channel.Generation)) throw new ScreenshotSessionStaleException();
      await persist(manifest, entries);
      if (channel.SessionId != sessionId || channel.Revision != revision ||
        generation != Volatile.Read(ref channel.Generation)) throw new ScreenshotSessionStaleException();
    }
    finally
    {
      foreach (FileStream stream in opened) stream.Dispose();
    }
  }

  private async Task<WorkbenchState?> OpenProjectAsync(CancellationToken cancellationToken)
  {
    if (projectStore is null || projectOpenPicker is null)
    {
      throw new ImageImportRefusedException("workbench.error.imageImportUnavailable");
    }
    // 单飞：等待 picker 前同步占用；取消/异常/销毁全部释放。快速双击时
    // 第二次请求同步拒绝，不会叠加第二个原生 picker 或交错 pending。
    List<WorkbenchResourceReference> published = [];
    string? extractDirectory = null;
    if (Interlocked.Exchange(ref projectOperationInFlight, 1) != 0)
    {
      throw new ImageImportInProgressException();
    }
    try
    {
      if (Volatile.Read(ref currentProjectOpen) is not null || projectOpen.HasPending)
      {
        throw new ImageImportInProgressException();
      }
      // UI 同步 busy：picker/解析/发布期间“打开工程”按钮按既有 busy 语义禁用。
      operations.Publish(ImageEditState(true, "imageEdit.projectOpening"));
      string? path = await projectOpenPicker(cancellationToken);
      if (path is null)
      {
        throw new AnnotatedImageOperationCancelledException();
      }
      // 绑定旧会话身份（imageEdit 通道；无会话时 SessionId 为空但代际仍受核验）。
      ImageSessionChannel target = imageEditChannel;
      WorkbenchProjectOpenBinding binding = new(
        target.SessionId ?? Guid.Empty,
        target.Revision,
        Volatile.Read(ref target.Generation));

      string extractPath = Path.Combine(
        Path.GetTempPath(), "vibeocr-project-open", Guid.NewGuid().ToString("N"));
      extractDirectory = extractPath;
      VibpProjectArchive archive;
      try
      {
        archive = await VibpProjectFile.ReadAsync(path, extractPath, cancellationToken);
      }
      catch
      {
        TryDeleteProjectDirectory(extractPath);
        throw;
      }
      WorkbenchResourceReference source = (await resources.PublishFileAsync(
        await File.ReadAllBytesAsync(archive.SourcePath, cancellationToken),
        archive.Manifest.Source.MediaType,
        WorkbenchResourcesOwner.ExtensionForMediaType(archive.Manifest.Source.MediaType),
        cancellationToken)).Reference;
      published.Add(source);
      Dictionary<string, WorkbenchResourceReference> assets = new(StringComparer.Ordinal);
      foreach ((string assetId, string assetPath) in archive.AssetPaths)
      {
        WorkbenchResourceReference reference = (await resources.PublishFileAsync(
          await File.ReadAllBytesAsync(assetPath, cancellationToken),
          "image/png", ".png", cancellationToken)).Reference;
        published.Add(reference);
        assets.Add(assetId, reference);
      }
      Dictionary<int, WorkbenchResourceReference> patches = [];
      foreach ((int patchId, string patchPath) in archive.PatchPaths)
      {
        WorkbenchResourceReference reference = (await resources.PublishFileAsync(
          await File.ReadAllBytesAsync(patchPath, cancellationToken),
          "image/png", ".png", cancellationToken)).Reference;
        published.Add(reference);
        patches.Add(patchId, reference);
      }
      TryDeleteProjectDirectory(extractDirectory);

      // manifest 本体经既有受控资源路径发布（application/json，/__resource/
      // 同源 GET）：合法 ≤256KiB state 不受 64KiB 桥限制；同一解析真源的重
      // 序列化，非第二真源。生命周期随 pending，ack 成功后与补丁一并释放。
      WorkbenchResourceReference manifestResource = (await resources.PublishFileAsync(
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(archive.Manifest, VibpProjectRules.ManifestJson),
        "application/json; charset=utf-8", ".json", cancellationToken)).Reference;
      published.Add(manifestResource);

      // pending 登记：release 恰好回收全部已发布资源；ack 前旧会话不动。
      WorkbenchProjectPendingOpen open = projectOpen.Begin(binding, () =>
      {
        foreach (WorkbenchResourceReference reference in published)
        {
          resources.ReleaseResource(reference);
        }
      });
      Volatile.Write(ref currentProjectOpen, new ProjectOpenDescriptor(
        open.PendingId, binding, archive.Manifest, manifestResource, source, assets, patches));
      return ImageEditState(
        false,
        target.SessionId is null ? "recognition.ready" : SessionStatusCode(target));
    }
    catch
    {
      // 失败/取消同样退出 busy，回到当前非忙碌投影（旧会话保持）。
      operations.PublishTerminal(ImageEditState(
        false,
        imageEditChannel.SessionId is null ? "recognition.ready" : SessionStatusCode(imageEditChannel)));
      foreach (WorkbenchResourceReference reference in published)
      {
        resources.ReleaseResource(reference);
      }
      if (extractDirectory is not null)
      {
        TryDeleteProjectDirectory(extractDirectory);
      }
      throw;
    }
    finally
    {
      Volatile.Write(ref projectOperationInFlight, 0);
    }
  }

  private WorkbenchState? AcknowledgeProjectOpen(
    AckImageEditProjectOpenCommand ack,
    CancellationToken documentToken)
  {
    ProjectOpenDescriptor? descriptor = Volatile.Read(ref currentProjectOpen);
    if (descriptor is null || descriptor.PendingId != ack.PendingId)
    {
      throw new WorkbenchProjectOpenConflictException(
        "Project open acknowledgement is late or unknown.");
    }
    projectOpen.Acknowledge(
      ack.PendingId,
      new WorkbenchProjectOpenBinding(ack.SessionId, ack.Revision, ack.Generation));
    ImageSessionChannel target = imageEditChannel;
    if ((target.SessionId ?? Guid.Empty) != ack.SessionId ||
      target.Revision != ack.Revision ||
      Volatile.Read(ref target.Generation) != ack.Generation)
    {
      // 提交后通道已被替换：回收已承诺资源，当前通道语义保持。
      ReleaseDescriptorResources(descriptor);
      Volatile.Write(ref currentProjectOpen, null);
      throw new WorkbenchProjectOpenConflictException(
        "Project open acknowledgement no longer matches the current session.");
    }
    // Detached 订阅方可能同步取消 ack 文档。先通知、再检查，旧会话的
    // 输入/资产/VM 在此仍完整；提交段不再触发该外部回调。
    try
    {
      if (!documentToken.IsCancellationRequested && target.SessionId is { } previousSession)
      {
        ScreenshotSessionDetached?.Invoke(previousSession, target.Revision);
      }
    }
    catch
    {
      ReleaseDescriptorResources(descriptor);
      Volatile.Write(ref currentProjectOpen, null);
      throw;
    }
    // ack 所属文档已取消（导航/恢复/重载后迟到 ack）：拒绝切换，旧会话
    // 与既有导入保持；不向已死文档插入恢复资产（同步 Register 竞态）。
    if (documentToken.IsCancellationRequested ||
      (target.SessionId ?? Guid.Empty) != ack.SessionId || target.Revision != ack.Revision ||
      Volatile.Read(ref target.Generation) != ack.Generation)
    {
      ReleaseDescriptorResources(descriptor);
      Volatile.Write(ref currentProjectOpen, null);
      throw new WorkbenchProjectOpenConflictException(
        "Project open acknowledgement was cancelled or its session changed before commit.");
    }
    // 提交点：上面的文档检查通过。以下同步段不再通知 Detached；此后
    // 文档取消按普通会话文档退休语义处理，不回滚已提交的新工程。
    // 原子切换：释放旧会话/导入并安装恢复基准与资产（逻辑 frame 随
    // manifest/state 携带，不做坐标重投影）。
    AdvanceImageGeneration(target);
    ClearScreenshotSession(target, notifyDetached: false);
    InvalidateScreenshotTextLayer(target);
    target.ViewModel?.Cancel();
    target.ViewModel?.InvalidateResult();
    target.ViewModel?.ReleaseInput();
    target.SessionId = Guid.NewGuid();
    target.Revision = 0;
    target.SessionInput = descriptor.Source;
    Dictionary<string, (int Width, int Height)> assetDims = archiveAssetDims(descriptor);
    lock (imageEditImport)
    {
      // 恢复资产绑定 ack 所属 Web 文档取消身份：导航/恢复/重载时由既有
      // RetireDocumentImports 精确回收，不随新文档重建泄漏到会话替换。
      if (documentToken.CanBeCanceled &&
        !imageEditImport.Documents.ContainsKey(documentToken))
      {
        imageEditImport.Documents[documentToken] = documentToken.Register(() =>
          RetireDocumentImports(imageEditChannel, imageEditImport, documentToken));
      }
      foreach ((string assetId, WorkbenchResourceReference reference) in descriptor.Assets)
      {
        (int width, int height) = assetDims.TryGetValue(assetId, out (int, int) dims)
          ? dims
          : (0, 0);
        imageEditImport.Adopted[reference.Url] = new StagedImportAsset(
          reference, assetId, (long)width * height, width, height, documentToken);
      }
      // 提前检查与 Register 之间的取消竞态（与普通导入 StartSystemImport
      // 的注册后取消处理同模式）：同步回调先回收了空集，此处再按刚插入
      // 资产携带的同一文档 token 精确回收，并清理 Documents 注册（锁可
      // 重入），不让已取消文档残留资产。
      if (documentToken.IsCancellationRequested)
      {
        RetireDocumentImports(imageEditChannel, imageEditImport, documentToken);
      }
    }
    // patch 与 manifest 在 ack 前提下已被 Web fetch/decode，宿主侧字节是死
    // 数据（合同：确认后不重新依赖 TTL 读取已准备位图/清单）：成功切换即
    // 释放文件与租约；source（新会话输入）与 assets 所有权不受影响。
    foreach (WorkbenchResourceReference patch in descriptor.Patches.Values)
    {
      resources.ReleaseResource(patch);
    }
    resources.ReleaseResource(descriptor.ManifestResource);
    Volatile.Write(ref currentProjectOpen, null);
    return ImageEditState(false, "recognition.session");
  }

  private static Dictionary<string, (int Width, int Height)> archiveAssetDims(
    ProjectOpenDescriptor descriptor) =>
    descriptor.Manifest.Assets.ToDictionary(
      asset => asset.Id,
      asset => (asset.Width, asset.Height),
      StringComparer.Ordinal);

  private WorkbenchState? RejectProjectOpen(RejectImageEditProjectOpenCommand reject)
  {
    ProjectOpenDescriptor? descriptor = Volatile.Read(ref currentProjectOpen);
    if (descriptor is not null && descriptor.PendingId == reject.PendingId)
    {
      // 事务取消执行 release；描述符由终止回调清理；迟到拒收幂等。
      projectOpen.Cancel(descriptor.PendingId);
    }
    return CurrentImageEditState();
  }

  private void ReleaseDescriptorResources(ProjectOpenDescriptor descriptor)
  {
    foreach (WorkbenchResourceReference reference in descriptor.Patches.Values
      .Concat(descriptor.Assets.Values)
      .Append(descriptor.Source)
      .Append(descriptor.ManifestResource))
    {
      resources.ReleaseResource(reference);
    }
  }

  /// <summary>保存路径的会话 source 维度/容器核实：直接复用读取路径的
  /// VibpProjectFile.ReadImageDimensionsAsync（同一 codec 判定 + Oriented
  /// 尺寸），声明容器与真实字节不符时在写入前拒绝，不产出不可重开的工程。</summary>
  private static Task<(int Width, int Height)> ReadProjectImageDimensionsAsync(
    string path, string mediaType, CancellationToken cancellationToken) =>
    VibpProjectFile.ReadImageDimensionsAsync(path, mediaType, cancellationToken);

  /// <summary>写入前对 Web 上传补丁做真实平台完整解码验证：PNG codec、
  /// IHDR 尺寸=声明矩形、GetPixelDataAsync 迫使完整 IDAT/IEND 像素流
  /// 解析；缺块/截断/损坏在原子写入前拒绝（旧目标/画布保留）。
  /// 结构+标准 CRC32 先于平台解码：防止 WIC 对损坏 IDAT/CRC 容忍
  /// （已实测）导致写出不可重开包；与 annotation 共享唯一结构实现。</summary>
  private static async Task ValidatePatchPngAsync(
    WorkbenchProjectPatchBytes patch, CancellationToken cancellationToken)
  {
    try
    {
      using var structure = new MemoryStream(patch.Png, writable: false);
      await WorkbenchAnnotationStore.ValidatePngStructureCoreAsync(
        structure, cancellationToken);
    }
    catch (Exception error) when (error is not (VibpProjectFormatException or OperationCanceledException))
    {
      throw new VibpProjectFormatException(
        $"Project patch {patch.Id} PNG structure or CRC is invalid.", error);
    }
    try
    {
      using InMemoryRandomAccessStream stream = new();
      using DataWriter writer = new(stream);
      writer.WriteBytes(patch.Png);
      await writer.StoreAsync().AsTask(cancellationToken);
      writer.DetachStream();
      stream.Seek(0);
      BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
      if (decoder.DecoderInformation.CodecId != BitmapDecoder.PngDecoderId)
      {
        throw new VibpProjectFormatException(
          $"Project patch {patch.Id} is not a PNG image.");
      }
      if ((int)decoder.OrientedPixelWidth != patch.Width ||
        (int)decoder.OrientedPixelHeight != patch.Height)
      {
        throw new VibpProjectFormatException(
          $"Project patch {patch.Id} PNG dimensions do not match its rectangle.");
      }
      _ = await decoder.GetPixelDataAsync().AsTask(cancellationToken);
    }
    catch (Exception error) when (error is not (VibpProjectFormatException or OperationCanceledException))
    {
      throw new VibpProjectFormatException(
        $"Project patch {patch.Id} PNG cannot be fully decoded.", error);
    }
  }

  private static void TryDeleteProjectDirectory(string directory)
  {
    try
    {
      if (Directory.Exists(directory))
      {
        Directory.Delete(directory, recursive: true);
      }
    }
    catch (IOException)
    {
    }
    catch (UnauthorizedAccessException)
    {
    }
  }
}

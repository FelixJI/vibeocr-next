using System.Text.Json;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Web;
using Windows.Graphics.Imaging;

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

  internal sealed record ProjectOpenDescriptor(
    Guid PendingId,
    WorkbenchProjectOpenBinding Binding,
    VibpProjectManifest Manifest,
    WorkbenchResourceReference Source,
    IReadOnlyDictionary<string, WorkbenchResourceReference> Assets,
    IReadOnlyDictionary<int, WorkbenchResourceReference> Patches);

  internal event Action<Guid, string>? ProjectOpenTerminated
  {
    add => projectOpen.PendingTerminated += value;
    remove => projectOpen.PendingTerminated -= value;
  }

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
      imageEditChannel.SessionId is null ? "recognition.ready" : SessionStatusCode(imageEditChannel)));
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
    ImageSessionChannel channel,
    ImportChannelState import,
    Guid contributeId,
    Guid sessionId,
    long revision,
    CancellationToken cancellationToken)
  {
    if (projectStore is null || projectSavePicker is null)
    {
      throw new ImageImportRefusedException("workbench.error.imageImportUnavailable");
    }
    ValidateScreenshotSession(channel, sessionId, revision);
    WorkbenchProjectContribution? contribution = projectStore.TryTake(contributeId);
    if (contribution is null)
    {
      throw new ImageImportRefusedException("workbench.error.screenshotSessionStale");
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

    string? path = await projectSavePicker(cancellationToken);
    if (path is null)
    {
      // 取消不丢画布：会话/贡献不受影响（贡献已消费，可由 Web 重新上传）。
      throw new AnnotatedImageOperationCancelledException();
    }
    // 异步边界再核验：会话/修订/代际任一变化都不落盘（旧文件天然保留）。
    if (channel.SessionId != sessionId || channel.Revision != revision ||
      generation != Volatile.Read(ref channel.Generation))
    {
      throw new ScreenshotSessionStaleException();
    }
    if (!resources.TryGetSessionFile(input.Url, out string? sourcePath) || !File.Exists(sourcePath))
    {
      throw new ImageImportRefusedException("workbench.error.imageImportRefused");
    }
    string sourceEntryName = VibpProjectRules.SourceEntryName(input.MediaType);
    (int sourceWidth, int sourceHeight) =
      await ReadProjectImageDimensionsAsync(sourcePath, input.MediaType, cancellationToken);
    long sourceLength = new FileInfo(sourcePath).Length;

    // 只持久化被当前 marks 引用的资产：manifest 语义要求声明资产全部被引用；
    // 引用未知资产的 mark 由 ValidateManifest 在写入时拒绝（fail closed）。
    HashSet<string> referenced = [..
      contribution.State.Marks.Select(mark => mark.AssetId).OfType<string>()];
    List<VibpAssetResource> assetResources = [];
    List<VibpProjectEntry> entries =
    [
      new(sourceEntryName, sourceLength, () => new FileStream(
        sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
        64 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous)),
    ];
    foreach ((WorkbenchResourceReference reference, string assetId, int width, int height) in held)
    {
      if (!referenced.Contains(assetId))
      {
        continue;
      }
      if (!string.Equals(reference.MediaType, "image/png", StringComparison.Ordinal))
      {
        throw new ImageImportRefusedException("workbench.error.imageImportRefused");
      }
      if (!resources.TryGetSessionFile(reference.Url, out string? assetPath) || !File.Exists(assetPath))
      {
        throw new ImageImportRefusedException("workbench.error.imageImportRefused");
      }
      long assetLength = new FileInfo(assetPath).Length;
      assetResources.Add(new VibpAssetResource
      {
        Id = assetId,
        Width = width,
        Height = height,
        Length = assetLength,
      });
      entries.Add(new($"asset-{assetId}.png", assetLength, () => new FileStream(
        assetPath, FileMode.Open, FileAccess.Read, FileShare.Read,
        64 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous)));
    }
    foreach (WorkbenchProjectPatchBytes patch in contribution.Patches)
    {
      byte[] png = patch.Png;
      entries.Add(new($"patch-{patch.Id}.png", png.LongLength,
        () => new MemoryStream(png, writable: false)));
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
    // 最终身份核验后同步原子写：失败抛出，旧工程文件保持。
    if (channel.SessionId != sessionId || channel.Revision != revision ||
      generation != Volatile.Read(ref channel.Generation))
    {
      throw new ScreenshotSessionStaleException();
    }
    VibpProjectFile.Write(path, manifest, entries);
    return CurrentChannelState(channel);
  }

  private async Task<WorkbenchState?> OpenProjectAsync(CancellationToken cancellationToken)
  {
    if (projectStore is null || projectOpenPicker is null)
    {
      throw new ImageImportRefusedException("workbench.error.imageImportUnavailable");
    }
    if (Volatile.Read(ref currentProjectOpen) is not null || projectOpen.HasPending)
    {
      throw new ImageImportInProgressException();
    }
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

    string extractDirectory = Path.Combine(
      Path.GetTempPath(), "vibeocr-project-open", Guid.NewGuid().ToString("N"));
    VibpProjectArchive archive;
    try
    {
      archive = await VibpProjectFile.ReadAsync(path, extractDirectory, cancellationToken);
    }
    catch
    {
      TryDeleteProjectDirectory(extractDirectory);
      throw;
    }
    List<WorkbenchResourceReference> published = [];
    try
    {
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

      // pending 登记：release 恰好回收全部已发布资源；ack 前旧会话不动。
      WorkbenchProjectPendingOpen open = projectOpen.Begin(binding, () =>
      {
        foreach (WorkbenchResourceReference reference in published)
        {
          resources.ReleaseResource(reference);
        }
      });
      Volatile.Write(ref currentProjectOpen, new ProjectOpenDescriptor(
        open.PendingId, binding, archive.Manifest, source, assets, patches));
      return ImageEditState(
        false,
        target.SessionId is null ? "recognition.ready" : SessionStatusCode(target));
    }
    catch
    {
      foreach (WorkbenchResourceReference reference in published)
      {
        resources.ReleaseResource(reference);
      }
      TryDeleteProjectDirectory(extractDirectory);
      throw;
    }
  }

  private WorkbenchState? AcknowledgeProjectOpen(AckImageEditProjectOpenCommand ack)
  {
    ProjectOpenDescriptor? descriptor = Volatile.Read(ref currentProjectOpen);
    if (descriptor is null || descriptor.PendingId != ack.PendingId)
    {
      throw new WorkbenchProjectOpenConflictException(
        "Project open acknowledgement is late or unknown.");
    }
    WorkbenchProjectPendingOpen committed = projectOpen.Acknowledge(
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
      return CurrentImageEditState();
    }
    // 原子切换：释放旧会话/导入并安装恢复基准与资产（逻辑 frame 随
    // manifest/state 携带，不做坐标重投影）。
    AdvanceImageGeneration(target);
    ClearScreenshotSession(target);
    InvalidateScreenshotTextLayer(target);
    target.SessionId = Guid.NewGuid();
    target.Revision = 0;
    target.SessionInput = descriptor.Source;
    Dictionary<string, (int Width, int Height)> assetDims = archiveAssetDims(descriptor);
    lock (imageEditImport)
    {
      foreach ((string assetId, WorkbenchResourceReference reference) in descriptor.Assets)
      {
        (int width, int height) = assetDims.TryGetValue(assetId, out (int, int) dims)
          ? dims
          : (0, 0);
        imageEditImport.Adopted[reference.Url] = new StagedImportAsset(
          reference, assetId, (long)width * height, width, height, default);
      }
    }
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
      .Append(descriptor.Source))
    {
      resources.ReleaseResource(reference);
    }
  }

  /// <summary>平台元数据级维度读取（Oriented 尺寸，与读取路径同语义），
  /// 容器不匹配/不可解码按格式异常拒绝。</summary>
  private static async Task<(int Width, int Height)> ReadProjectImageDimensionsAsync(
    string path, string mediaType, CancellationToken cancellationToken)
  {
    try
    {
      await using FileStream stream = new(
        path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
      using var random = stream.AsRandomAccessStream();
      BitmapDecoder decoder = await BitmapDecoder.CreateAsync(random).AsTask(cancellationToken);
      (int width, int height) = (checked((int)decoder.OrientedPixelWidth), checked((int)decoder.OrientedPixelHeight));
      if (width < 1 || height < 1)
      {
        throw new VibpProjectFormatException($"Project source {mediaType} cannot be decoded.");
      }
      return (width, height);
    }
    catch (Exception error) when (error is not (VibpProjectFormatException or OperationCanceledException))
    {
      throw new VibpProjectFormatException(
        $"Project source {mediaType} metadata cannot be read.", error);
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

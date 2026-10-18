using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using VibeOCR.App.Workbench;

namespace VibeOCR.App.Web;

/// <summary>窄 project 上传（/__project）的格式/预算错误；上传失败不产生任何暂存。</summary>
public sealed class WorkbenchProjectUploadException(
  string message,
  Exception? innerException = null) : Exception(message, innerException);

/// <summary>一次保存贡献的暂存租约：contributeId 供保存命令一次性消费。</summary>
public sealed record WorkbenchProjectContributionLease(Guid ContributeId, DateTimeOffset ExpiresAt);

/// <summary>Web 贡献的已应用补丁像素：原图未旋转整数空间矩形 + PNG 字节。</summary>
public sealed record WorkbenchProjectPatchBytes(
  int Id,
  int X,
  int Y,
  int Width,
  int Height,
  byte[] Png);

/// <summary>一次保存贡献：逻辑 canvas frame、编辑器 state 与补丁像素；
/// source/assets 字节由宿主会话文件提供，不在上传内。</summary>
public sealed record WorkbenchProjectContribution(
  VibpCanvas Canvas,
  VibpEditorState State,
  IReadOnlyList<WorkbenchProjectPatchBytes> Patches);

/// <summary>
/// #233 保存贡献窄同源上传的单会话暂存：Web 经既有受控同源边界 POST
/// /__project（JSON：canvas+state+base64 补丁），宿主在此完成结构校验与
/// 预算收敛后暂存，等待保存命令按 contributeId 一次性取走。任一时刻最多
/// 一份未消费贡献（新上传替换旧的），过期未消费即丢弃；不放宽 64KiB 桥
/// 或 /__annotation 图像端点。
/// 上传阶段只验证结构/全局边界（严格根形状、整数/正值、32768 边、像素与
/// 字节预算、PNG IHDR=矩形）；补丁矩形⊆实际 source 在组装 manifest 时用
/// 宿主会话持有的真实 source 尺寸验证（VibpProjectRules.ValidateState），
/// 不信任 Web 上传提供的任何 source 尺寸。上传期间发生文档重置
/// （RevokeAll/Dispose）以代际拒绝提交，不暂存旧文档贡献。
/// </summary>
public sealed class WorkbenchProjectStore : IDisposable
{
  /// <summary>上传 wire body 总预算（含 base64 展开前的 JSON 文本）。</summary>
  public const long MaximumUploadBytes = 64L << 20;
  /// <summary>state 序列化预算：与 manifest 预算同源（256 KiB）。</summary>
  public const long MaximumStateBytes = VibpProjectLimits.MaximumManifestBytes;
  /// <summary>entry 总数边界下补丁的最大数量（2=manifest+source，无资产）。</summary>
  public const int MaximumContributionPatches = VibpProjectLimits.MaximumEntries - 2;
  public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(5);

  private static readonly byte[] PngSignature = [
    0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
  ];
  /// <summary>根/子对象统一严格形状：camelCase、未知字段拒绝、重复键拒绝。</summary>
  private static readonly JsonSerializerOptions UploadJson = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false,
  };

  private readonly object gate = new();
  private readonly TimeProvider timeProvider;
  private readonly TimeSpan lifetime;
  /// <summary>按宿主/文档身份分槽：任一时刻每槽最多一份未消费贡献；
  /// 匿名调用共享一个槽，保存单宿主/既有测试语义。</summary>
  private readonly Dictionary<object, Slot> slots =
    new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
  private bool disposed;

  private sealed class Slot
  {
    public StagedContribution? Staged;
    public long Generation;
    /// <summary>槽内请求序号：每次上传开始递增，仅最新请求可提交。
    /// 防同槽两上传重叠（老大 POST 慢读、新小 POST 后发先完成）时旧请求
    /// 覆盖新贡献——代际只随撤销/销毁推进，不区分同代重叠请求。</summary>
    public long Requests;
    public ITimer? ExpiryTimer;
  }

  internal (int Count, Guid? Id) Staged
  {
    get
    {
      lock (gate)
      {
        StagedContribution? first = slots.Values
          .Select(slot => slot.Staged)
          .FirstOrDefault(staged => staged is not null);
        int count = slots.Values.Count(slot => slot.Staged is not null);
        return (count, first?.Lease.ContributeId);
      }
    }
  }

  internal int ActiveExpiryTimers
  {
    get
    {
      lock (gate)
      {
        return slots.Values.Count(slot => slot.ExpiryTimer is not null);
      }
    }
  }

  public WorkbenchProjectStore(
    TimeProvider? timeProvider = null,
    TimeSpan? contributionLifetime = null)
  {
    this.timeProvider = timeProvider ?? TimeProvider.System;
    lifetime = contributionLifetime ?? DefaultLifetime;
    if (lifetime <= TimeSpan.Zero)
    {
      throw new ArgumentOutOfRangeException(nameof(contributionLifetime));
    }
  }

  /// <summary>接收并暂存一次保存贡献（匿名槽，兼容单宿主/既有测试）。</summary>
  public Task<WorkbenchProjectContributionLease> UploadAsync(
    Stream content,
    CancellationToken cancellationToken = default) =>
    UploadAsync(content, AnonymousCaller, cancellationToken);

  /// <summary>按宿主身份接收贡献：多宿主（主窗口/独立编辑窗）各自槽位
  /// 互不替换，导航/重置仅撤销该宿主槽位；同槽已有未消费贡献被替换。</summary>
  public async Task<WorkbenchProjectContributionLease> UploadAsync(
    Stream content,
    object caller,
    CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull(caller);
    cancellationToken.ThrowIfCancellationRequested();
    Slot slot;
    long uploadGeneration;
    long request;
    lock (gate)
    {
      ThrowIfDisposed();
      slot = SlotFor(caller);
      uploadGeneration = slot.Generation;
      request = ++slot.Requests;
    }
    byte[] body = await ReadBoundedAsync(content, MaximumUploadBytes, cancellationToken);
    WorkbenchProjectContribution contribution = ParseContribution(body);

    lock (gate)
    {
      ThrowIfDisposed();
      // 撤销/销毁（代际）或同槽更新请求（序号）任一变化，旧请求不提交。
      if (slot.Generation != uploadGeneration || slot.Requests != request)
      {
        throw new WorkbenchProjectUploadException(
          "Project contribution upload was superseded by a newer request or document reset.");
      }
      SweepExpired(timeProvider.GetUtcNow());
      slot.Staged = new StagedContribution(
        new WorkbenchProjectContributionLease(Guid.NewGuid(), timeProvider.GetUtcNow() + lifetime),
        contribution);
      // 主动到期回收：无人再上传/消费时也按时释放补丁字节强引用。
      // 回调精确匹配当前槽内代际+id：迟到触发不得清除替换后的新贡献。
      long stagedGeneration = slot.Generation;
      Guid stagedId = slot.Staged.Lease.ContributeId;
      slot.ExpiryTimer?.Dispose();
      DateTimeOffset dueAt = slot.Staged.Lease.ExpiresAt;
      slot.ExpiryTimer = timeProvider.CreateTimer(
        _ => ExpireStaged(slot, stagedGeneration, stagedId),
        null,
        dueAt - timeProvider.GetUtcNow(),
        Timeout.InfiniteTimeSpan);
      return slot.Staged.Lease;
    }
  }

  /// <summary>保存命令一次性取走贡献：id 未知/已消费/已过期均返回 null。
  /// 扫描全部槽位：任一宿主上传的贡献都可被保存命令按 id 消费。</summary>
  public WorkbenchProjectContribution? TryTake(Guid contributeId)
  {
    lock (gate)
    {
      ThrowIfDisposed();
      SweepExpired(timeProvider.GetUtcNow());
      foreach (Slot slot in slots.Values)
      {
        if (slot.Staged is { } current && current.Lease.ContributeId == contributeId)
        {
          slot.Staged = null;
          CancelExpiryTimer(slot);
          return current.Contribution;
        }
      }
      return null;
    }
  }

  /// <summary>撤销全部槽（应用销毁/全局重置）。</summary>
  public void RevokeAll()
  {
    lock (gate)
    {
      foreach (Slot slot in slots.Values)
      {
        RevokeSlot(slot);
      }
    }
  }

  /// <summary>仅撤销指定宿主槽位（该宿主文档重置）：先推进槽内代际使在途
  /// 上传不能提交，再安全移除槽位（宿主销毁后不再被 store 强持有）；其他
  /// 宿主的贡献与在途上传不受影响；幂等。</summary>
  public void RevokeAll(object caller)
  {
    ArgumentNullException.ThrowIfNull(caller);
    lock (gate)
    {
      if (slots.Remove(caller, out Slot? slot))
      {
        RevokeSlot(slot);
      }
    }
  }

  public void Dispose()
  {
    lock (gate)
    {
      disposed = true;
      foreach (Slot slot in slots.Values)
      {
        RevokeSlot(slot);
      }
      slots.Clear();
    }
  }

  private static void RevokeSlot(Slot slot)
  {
    slot.Staged = null;
    slot.Generation++;
    CancelExpiryTimer(slot);
  }

  /// <summary>到期回调：仅当仍是自己槽内代际的同一贡献且已过期时清除。</summary>
  private void ExpireStaged(Slot slot, long stagedGeneration, Guid stagedId)
  {
    lock (gate)
    {
      if (slot.Generation != stagedGeneration)
      {
        return;
      }
      if (slot.Staged is { Lease.ContributeId: var id } && id == stagedId)
      {
        slot.Staged = null;
        CancelExpiryTimer(slot);
      }
    }
  }

  private static void CancelExpiryTimer(Slot slot)
  {
    slot.ExpiryTimer?.Dispose();
    slot.ExpiryTimer = null;
  }

  private static readonly object AnonymousCaller = new();

  private Slot SlotFor(object caller)
  {
    if (!slots.TryGetValue(caller, out Slot? slot))
    {
      slot = new Slot();
      slots[caller] = slot;
    }
    return slot;
  }

  /// <summary>根/子对象均为严格 typed DTO（未知字段/重复键/缺字段拒绝）。</summary>
  private static WorkbenchProjectContribution ParseContribution(byte[] body)
  {
    WorkbenchProjectUploadException Fail(string message) => new(message);
    WorkbenchProjectUpload upload;
    try
    {
      upload = JsonSerializer.Deserialize<WorkbenchProjectUpload>(body, UploadJson) ??
        throw Fail("Project contribution is empty.");
    }
    catch (WorkbenchProjectUploadException)
    {
      throw;
    }
    catch (Exception error) when (error is JsonException or InvalidOperationException)
    {
      throw new WorkbenchProjectUploadException(
        "Project contribution JSON is invalid.", error);
    }

    VibpCanvas canvas = upload.Canvas ?? throw Fail("Project contribution canvas is null.");
    if (canvas.Width is < 1 or > VibpProjectLimits.MaximumImageSide ||
      canvas.Height is < 1 or > VibpProjectLimits.MaximumImageSide ||
      (long)canvas.Width * canvas.Height > VibpProjectLimits.MaximumCanvasPixels)
    {
      throw Fail("Project contribution canvas is outside the supported bounds.");
    }

    VibpEditorState state = upload.State ?? throw Fail("Project contribution state is null.");
    if (state.Marks is null || state.Patches is null)
    {
      throw Fail("Project contribution state nullifies a required list.");
    }
    // 补丁描述符唯一来源是上传 patches 数组：state 内携带即拒绝，避免双真源。
    if (state.Patches.Count > 0)
    {
      throw Fail("Project contribution state must not carry patch descriptors.");
    }
    long stateBytes = JsonSerializer.SerializeToUtf8Bytes(state, VibpProjectRules.ManifestJson).LongLength;
    if (stateBytes > MaximumStateBytes)
    {
      throw Fail("Project contribution state exceeds the 256 KiB budget.");
    }

    IReadOnlyList<WorkbenchProjectPatchUpload?> patches = upload.Patches ??
      throw Fail("Project contribution patches list is null.");
    if (patches.Count > MaximumContributionPatches)
    {
      throw Fail($"Project contribution declares more than {MaximumContributionPatches} patches.");
    }
    List<WorkbenchProjectPatchBytes> accepted = [];
    HashSet<int> ids = [];
    long pixelTotal = 0;
    long byteTotal = 0;
    foreach (WorkbenchProjectPatchUpload? patch in patches)
    {
      if (patch is null)
      {
        throw Fail("Project contribution patch is null.");
      }
      if (patch.Id < 1 || !ids.Add(patch.Id))
      {
        throw Fail("Project contribution patch id is malformed or duplicated.");
      }
      // 上传阶段仅结构/全局边界；rect⊆实际 source 由 manifest 组装时按宿主
      // 会话持有的真实 source 验证（逻辑 frame 可小于自然图，坐标可越过 canvas）。
      if (patch.X < 0 || patch.Y < 0 ||
        patch.Width < 1 || patch.Width > VibpProjectLimits.MaximumImageSide ||
        patch.Height < 1 || patch.Height > VibpProjectLimits.MaximumImageSide)
      {
        throw Fail($"Project contribution patch {patch.Id} rectangle is malformed.");
      }
      long pixels = (long)patch.Width * patch.Height;
      if (pixels > VibpProjectLimits.MaximumPatchPixels)
      {
        throw Fail($"Project contribution patch {patch.Id} exceeds the per-patch pixel budget.");
      }
      pixelTotal += pixels;
      if (pixelTotal > VibpProjectLimits.MaximumPatchAggregatePixels)
      {
        throw Fail("Project contribution patches exceed the aggregate pixel budget.");
      }
      if (string.IsNullOrEmpty(patch.Png))
      {
        throw Fail($"Project contribution patch {patch.Id} has no PNG payload.");
      }
      // base64 展开前的预算预检，避免解码本身越过 64 MiB 聚合预算。
      if (byteTotal + patch.Png.Length * 3L / 4 + 1 > VibpProjectLimits.MaximumPatchAggregateBytes)
      {
        throw Fail("Project contribution patches exceed the aggregate encoded byte budget.");
      }
      byte[] png;
      try
      {
        png = Convert.FromBase64String(patch.Png);
      }
      catch (FormatException error)
      {
        throw new WorkbenchProjectUploadException(
          $"Project contribution patch {patch.Id} PNG is not valid base64.", error);
      }
      byteTotal += png.LongLength;
      if (byteTotal > VibpProjectLimits.MaximumPatchAggregateBytes)
      {
        throw Fail("Project contribution patches exceed the aggregate encoded byte budget.");
      }
      RequirePatchPng(patch.Id, patch.Width, patch.Height, png);
      accepted.Add(new WorkbenchProjectPatchBytes(patch.Id, patch.X, patch.Y, patch.Width, patch.Height, png));
    }
    return new WorkbenchProjectContribution(canvas, state, accepted);
  }

  /// <summary>补丁 PNG 结构最小校验：签名 + IHDR 维度必须等于声明矩形。
  /// 与 /__annotation 同级别的前置结构检查；完整解码仍由 Web pending 确认承担。</summary>
  private static void RequirePatchPng(int id, int width, int height, byte[] png)
  {
    if (png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(PngSignature) ||
      !png.AsSpan(12, 4).SequenceEqual("IHDR"u8))
    {
      throw new WorkbenchProjectUploadException(
        $"Project contribution patch {id} is not a PNG image.");
    }
    int pngWidth = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
    int pngHeight = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
    if (pngWidth != width || pngHeight != height)
    {
      throw new WorkbenchProjectUploadException(
        $"Project contribution patch {id} PNG dimensions do not match its rectangle.");
    }
  }

  private static async Task<byte[]> ReadBoundedAsync(
    Stream content, long maximumBytes, CancellationToken cancellationToken)
  {
    using var buffered = new MemoryStream();
    byte[] chunk = new byte[64 * 1024];
    long total = 0;
    int read;
    while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
    {
      total += read;
      if (total > maximumBytes)
      {
        throw new WorkbenchProjectUploadException(
          "Project contribution upload exceeds the 64 MiB budget.");
      }
      await buffered.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
    }
    return buffered.ToArray();
  }

  private void SweepExpired(DateTimeOffset utcNow)
  {
    foreach (Slot slot in slots.Values)
    {
      if (slot.Staged is { Lease.ExpiresAt: var expiresAt } current && expiresAt <= utcNow)
      {
        slot.Staged = null;
        CancelExpiryTimer(slot);
        _ = current;
      }
    }
  }

  private void ThrowIfDisposed()
  {
    ObjectDisposedException.ThrowIf(disposed, this);
  }

  /// <summary>上传 JSON 根形状（严格：未知字段/重复键/缺字段拒绝）。</summary>
  private sealed record WorkbenchProjectUpload(
    VibpCanvas? Canvas,
    VibpEditorState? State,
    IReadOnlyList<WorkbenchProjectPatchUpload?>? Patches);

  /// <summary>上传 JSON 的补丁项形状（png 为 base64 文本）。</summary>
  private sealed record WorkbenchProjectPatchUpload(
    int Id,
    int X,
    int Y,
    int Width,
    int Height,
    string? Png);

  private sealed record StagedContribution(
    WorkbenchProjectContributionLease Lease,
    WorkbenchProjectContribution Contribution);
}

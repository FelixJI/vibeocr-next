using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using Xunit;

namespace VibeOCR.App.Tests;

/// <summary>#233 保存贡献窄同源上传（/__project JSON）：结构/预算校验、
/// 单未消费替换、一次性消费、过期回收，以及贡献→原子工程写入的拼装路径。</summary>
public sealed class WorkbenchProjectStoreTests
{
  private static readonly JsonSerializerOptions Camel =
    new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

  [Fact]
  public async Task UploadStagesContributionAndTakeIsOneShot()
  {
    using var store = new WorkbenchProjectStore();
    byte[] patch = PngBytes(4, 3);

    WorkbenchProjectContributionLease lease = await store.UploadAsync(
      Body(new VibpCanvas { Width = 64, Height = 48 }, null, (1, 0, 0, 4, 3, patch)),
      TestContext.Current.CancellationToken);

    Assert.NotEqual(Guid.Empty, lease.ContributeId);
    Assert.Equal((1, (Guid?)lease.ContributeId), store.Staged);

    WorkbenchProjectContribution? contribution = store.TryTake(lease.ContributeId);
    Assert.NotNull(contribution);
    Assert.Equal(64, contribution.Canvas.Width);
    Assert.Empty(contribution.State.Marks);
    WorkbenchProjectPatchBytes uploaded = Assert.Single(contribution.Patches);
    Assert.Equal(patch, uploaded.Png);

    // 一次性消费：第二次取走为 null。
    Assert.Null(store.TryTake(lease.ContributeId));
    Assert.Equal((0, null), store.Staged);
  }

  [Fact]
  public async Task UploadReplacesPreviousUnconsumedContribution()
  {
    using var store = new WorkbenchProjectStore();
    WorkbenchProjectContributionLease first = await store.UploadAsync(
      Body(new VibpCanvas { Width = 8, Height = 8 }, null), TestContext.Current.CancellationToken);
    WorkbenchProjectContributionLease second = await store.UploadAsync(
      Body(new VibpCanvas { Width = 9, Height = 9 }, null), TestContext.Current.CancellationToken);

    Assert.NotEqual(first.ContributeId, second.ContributeId);
    Assert.Equal((1, (Guid?)second.ContributeId), store.Staged);
    Assert.Null(store.TryTake(first.ContributeId));
    Assert.NotNull(store.TryTake(second.ContributeId));
  }

  [Fact]
  public async Task UploadRejectsCanvasOverTotalPixelBudget()
  {
    using var store = new WorkbenchProjectStore();
    // 元数据级拒绝：8000×8001 超 64MP 总量（不分配任何巨大画布）；
    // 32768² 同样拒绝。
    foreach ((int width, int height) in new[] { (8000, 8001), (32768, 32768) })
    {
      WorkbenchProjectUploadException error = await Assert.ThrowsAsync<WorkbenchProjectUploadException>(
        () => store.UploadAsync(
          Body(new VibpCanvas { Width = width, Height = height }, null),
          TestContext.Current.CancellationToken));
      Assert.Contains("bounds", error.Message);
    }
    // 边界值 8000×8000 恰好 64MP：接受。
    WorkbenchProjectContributionLease edge = await store.UploadAsync(
      Body(new VibpCanvas { Width = 8000, Height = 8000 }, null),
      TestContext.Current.CancellationToken);
    Assert.NotNull(store.TryTake(edge.ContributeId));
  }

  [Fact]
  public async Task ExpiryTimerActivelyReleasesStagedContribution()
  {
    // 无人再上传/消费：到期计时器主动清除强引用，无需真实 sleep。
    AdjustableTimeProvider clock = new();
    using var store = new WorkbenchProjectStore(clock);
    WorkbenchProjectContributionLease lease = await store.UploadAsync(
      Body(new VibpCanvas { Width = 8, Height = 8 }, null),
      TestContext.Current.CancellationToken);
    Assert.Equal((1, (Guid?)lease.ContributeId), store.Staged);
    Assert.Equal(1, store.ActiveExpiryTimers);

    clock.Advance(WorkbenchProjectStore.DefaultLifetime + TimeSpan.FromSeconds(1));

    Assert.Equal((0, null), store.Staged);
    Assert.Equal(0, store.ActiveExpiryTimers);
    Assert.Null(store.TryTake(lease.ContributeId));
  }

  [Fact]
  public async Task LateExpiryCallbackDoesNotClearReplacedContribution()
  {
    AdjustableTimeProvider clock = new();
    using var store = new WorkbenchProjectStore(clock);
    WorkbenchProjectContributionLease first = await store.UploadAsync(
      Body(new VibpCanvas { Width = 8, Height = 8 }, null),
      TestContext.Current.CancellationToken);
    // 替换为第二份贡献：旧计时器被取消，即使迟到触发也不清新贡献。
    WorkbenchProjectContributionLease second = await store.UploadAsync(
      Body(new VibpCanvas { Width = 9, Height = 9 }, null),
      TestContext.Current.CancellationToken);

    clock.Advance(WorkbenchProjectStore.DefaultLifetime + TimeSpan.FromSeconds(1));

    // 只剩第二份到期：第一份的迟到回调已被取消（无交叉清除）。
    Assert.Equal((0, null), store.Staged);
  }

  [Fact]
  public async Task ConsumeAndRevokeCancelExpiryTimer()
  {
    AdjustableTimeProvider clock = new();
    using var store = new WorkbenchProjectStore(clock);
    WorkbenchProjectContributionLease consumed = await store.UploadAsync(
      Body(new VibpCanvas { Width = 8, Height = 8 }, null),
      TestContext.Current.CancellationToken);
    Assert.NotNull(store.TryTake(consumed.ContributeId));
    Assert.Equal(0, store.ActiveExpiryTimers);

    WorkbenchProjectContributionLease revoked = await store.UploadAsync(
      Body(new VibpCanvas { Width = 8, Height = 8 }, null),
      TestContext.Current.CancellationToken);
    store.RevokeAll();
    Assert.Equal(0, store.ActiveExpiryTimers);

    // 取消计时器后推进时间：无任何触发，状态保持空。
    clock.Advance(WorkbenchProjectStore.DefaultLifetime * 2);
    Assert.Equal((0, null), store.Staged);
  }

  [Fact]
  public async Task UploadRejectsStructureInvalidPngAfterSharedCrcValidation()
  {
    // 直接相关 annotation 共享实现回归：结构+CRC 校验拒绝坏样本并接受
    // 真实 CRC fixture（流式入口，供 project 补丁复用的同一实现）。
    using var store = new WorkbenchProjectStore();
    byte[] valid = PngBytes(4, 3);
    await WorkbenchAnnotationStore.ValidatePngStructureCoreAsync(
      new MemoryStream(valid), TestContext.Current.CancellationToken);

    await Assert.ThrowsAsync<InvalidDataException>(() =>
      WorkbenchAnnotationStore.ValidatePngStructureCoreAsync(
        new MemoryStream(valid[..^30]), TestContext.Current.CancellationToken));
    byte[] zeroed = [.. valid];
    for (int i = 41; i < zeroed.Length - 4; i++) zeroed[i] = 0;
    await Assert.ThrowsAsync<InvalidDataException>(() =>
      WorkbenchAnnotationStore.ValidatePngStructureCoreAsync(
        new MemoryStream(zeroed), TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task ExpiredContributionIsNotConsumed()
  {
    AdjustableTimeProvider clock = new();
    using var store = new WorkbenchProjectStore(clock);
    WorkbenchProjectContributionLease lease = await store.UploadAsync(
      Body(new VibpCanvas { Width = 8, Height = 8 }, null), TestContext.Current.CancellationToken);

    clock.Advance(WorkbenchProjectStore.DefaultLifetime + TimeSpan.FromSeconds(1));

    Assert.Null(store.TryTake(lease.ContributeId));
    Assert.Equal((0, null), store.Staged);
  }

  [Fact]
  public async Task RevokeAllDropsUnconsumedContribution()
  {
    using var store = new WorkbenchProjectStore();
    WorkbenchProjectContributionLease lease = await store.UploadAsync(
      Body(new VibpCanvas { Width = 8, Height = 8 }, null), TestContext.Current.CancellationToken);

    store.RevokeAll();

    Assert.Null(store.TryTake(lease.ContributeId));
  }

  [Fact]
  public async Task UploadRejectsStateOverBudget()
  {
    using var store = new WorkbenchProjectStore();
    VibpMark[] marks = [.. Enumerable.Range(1, 40).Select(index => new VibpMark
    {
      Id = $"mark-{index}",
      Tool = "text",
      Start = new VibpPoint { X = 1, Y = 1 },
      End = new VibpPoint { X = 2, Y = 2 },
      Text = new string('a', VibpProjectLimits.MaximumMarkTextLength),
      TextWidth = 100,
    })];
    var state = new VibpEditorState { Rotation = 0, Marks = marks };

    WorkbenchProjectUploadException error = await Assert.ThrowsAsync<WorkbenchProjectUploadException>(
      () => store.UploadAsync(
        Body(new VibpCanvas { Width = 8, Height = 8 }, state),
        TestContext.Current.CancellationToken));

    Assert.Contains("256 KiB", error.Message);
    Assert.Equal((0, null), store.Staged);
  }

  [Fact]
  public async Task UploadRejectsStateCarryingPatchDescriptors()
  {
    using var store = new WorkbenchProjectStore();
    var state = new VibpEditorState
    {
      Rotation = 0,
      Marks = [],
      Patches = [new VibpPatch { Id = 1, X = 0, Y = 0, Width = 4, Height = 3, Length = 10 }],
    };

    WorkbenchProjectUploadException error = await Assert.ThrowsAsync<WorkbenchProjectUploadException>(
      () => store.UploadAsync(
        Body(new VibpCanvas { Width = 8, Height = 8 }, state),
        TestContext.Current.CancellationToken));

    Assert.Contains("patch descriptors", error.Message);
  }

  [Fact]
  public async Task UploadAcceptsPatchRectangleBeyondLogicalCanvas()
  {
    using var store = new WorkbenchProjectStore();
    byte[] patch = PngBytes(100, 50);

    // 逻辑 frame 300x200，补丁自然坐标 x=350：上传阶段不得用 canvas 拒绝
    // （rect⊆实际 source 由组装 manifest 时验证）。
    WorkbenchProjectContributionLease lease = await store.UploadAsync(
      Body(new VibpCanvas { Width = 300, Height = 200 }, null, (1, 350, 0, 100, 50, patch)),
      TestContext.Current.CancellationToken);

    WorkbenchProjectContribution? contribution = store.TryTake(lease.ContributeId);
    Assert.NotNull(contribution);
    WorkbenchProjectPatchBytes accepted = Assert.Single(contribution.Patches);
    Assert.Equal(350, accepted.X);
  }

  [Fact]
  public async Task PatchRectangleCheckedAgainstActualSourceAtAssembly()
  {
    string directory = NewTempDirectory();
    try
    {
      using var store = new WorkbenchProjectStore();
      // 逻辑 frame 300x200，自然图 640x480：补丁越过 canvas 但在 source 内。
      byte[] source = PngBytes(640, 480);
      byte[] inside = PngBytes(100, 50);
      WorkbenchProjectContributionLease insideLease = await store.UploadAsync(
        Body(new VibpCanvas { Width = 300, Height = 200 }, null, (1, 350, 40, 100, 50, inside)),
        TestContext.Current.CancellationToken);
      WorkbenchProjectContribution? insideContribution = store.TryTake(insideLease.ContributeId);
      Assert.NotNull(insideContribution);

      string packagePath = Path.Combine(directory, "inside.vibp");
      VibpProjectFile.Write(packagePath,
        ManifestFor(insideContribution, 640, 480, source.LongLength),
      [
        new VibpProjectEntry("source.png", source.Length, () => new MemoryStream(source)),
        new VibpProjectEntry("patch-1.png", inside.Length, () => new MemoryStream(inside)),
      ]);
      VibpProjectArchive archive = await VibpProjectFile.ReadAsync(
        packagePath, Path.Combine(directory, "open-inside"), TestContext.Current.CancellationToken);
      Assert.Equal(inside, File.ReadAllBytes(archive.PatchPaths[1]));

      // 实际 source 越界：上传接受（结构边界内），组装 manifest 时拒绝。
      byte[] outside = PngBytes(100, 50);
      WorkbenchProjectContributionLease outsideLease = await store.UploadAsync(
        Body(new VibpCanvas { Width = 300, Height = 200 }, null, (1, 600, 0, 100, 50, outside)),
        TestContext.Current.CancellationToken);
      WorkbenchProjectContribution? outsideContribution = store.TryTake(outsideLease.ContributeId);
      Assert.NotNull(outsideContribution);

      VibpProjectFormatException error = Assert.Throws<VibpProjectFormatException>(() =>
        VibpProjectFile.Write(Path.Combine(directory, "outside.vibp"),
          ManifestFor(outsideContribution, 640, 480, source.LongLength),
        [
          new VibpProjectEntry("source.png", source.Length, () => new MemoryStream(source)),
          new VibpProjectEntry("patch-1.png", outside.Length, () => new MemoryStream(outside)),
        ]));
      Assert.Contains("outside the source image bounds", error.Message);
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  private static VibpProjectManifest ManifestFor(
    WorkbenchProjectContribution contribution,
    int sourceWidth,
    int sourceHeight,
    long sourceLength) => new()
    {
      SchemaVersion = VibpProjectLimits.SchemaVersion,
      Canvas = contribution.Canvas,
      Source = new VibpSourceResource
      {
        MediaType = "image/png",
        Width = sourceWidth,
        Height = sourceHeight,
        Length = sourceLength,
      },
      Assets = [],
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

  [Fact]
  public async Task UploadRejectsMalformedPatchRectangle()
  {
    using var store = new WorkbenchProjectStore();
    byte[] patch = PngBytes(4, 3);

    WorkbenchProjectUploadException error = await Assert.ThrowsAsync<WorkbenchProjectUploadException>(
      () => store.UploadAsync(
        Body(new VibpCanvas { Width = 8, Height = 8 }, null, (1, -1, 0, 4, 3, patch)),
        TestContext.Current.CancellationToken));

    Assert.Contains("malformed", error.Message);
    Assert.Equal((0, null), store.Staged);
  }

  [Fact]
  public async Task UploadRejectsDuplicatePatchIds()
  {
    using var store = new WorkbenchProjectStore();
    byte[] patch = PngBytes(4, 3);

    WorkbenchProjectUploadException error = await Assert.ThrowsAsync<WorkbenchProjectUploadException>(
      () => store.UploadAsync(
        Body(new VibpCanvas { Width = 8, Height = 8 }, null,
          (1, 0, 0, 4, 3, patch), (1, 0, 0, 4, 3, patch)),
        TestContext.Current.CancellationToken));

    Assert.Contains("duplicated", error.Message);
  }

  [Fact]
  public async Task UploadRejectsPngDimensionMismatch()
  {
    using var store = new WorkbenchProjectStore();
    byte[] patch = PngBytes(5, 3);

    WorkbenchProjectUploadException error = await Assert.ThrowsAsync<WorkbenchProjectUploadException>(
      () => store.UploadAsync(
        Body(new VibpCanvas { Width = 8, Height = 8 }, null, (1, 0, 0, 4, 3, patch)),
        TestContext.Current.CancellationToken));

    Assert.Contains("dimensions do not match", error.Message);
  }

  [Fact]
  public async Task UploadRejectsBodyOverBudget()
  {
    using var store = new WorkbenchProjectStore();

    WorkbenchProjectUploadException error = await Assert.ThrowsAsync<WorkbenchProjectUploadException>(
      () => store.UploadAsync(
        new EndlessStream(), TestContext.Current.CancellationToken));

    Assert.Contains("64 MiB", error.Message);
    Assert.Equal((0, null), store.Staged);
  }

  [Fact]
  public async Task UploadRejectsDuplicateRootProperties()
  {
    using var store = new WorkbenchProjectStore();

    WorkbenchProjectUploadException error = await Assert.ThrowsAsync<WorkbenchProjectUploadException>(
      () => store.UploadAsync(RawBody(
        "{\"canvas\":{\"width\":8,\"height\":8},\"canvas\":{\"width\":8,\"height\":8}," +
        "\"state\":{\"rotation\":0,\"marks\":[]},\"patches\":[]}"),
        TestContext.Current.CancellationToken));

    Assert.Contains("JSON is invalid", error.Message);
    Assert.Equal((0, null), store.Staged);
  }

  [Fact]
  public async Task UploadRejectsUnknownRootField()
  {
    using var store = new WorkbenchProjectStore();

    WorkbenchProjectUploadException error = await Assert.ThrowsAsync<WorkbenchProjectUploadException>(
      () => store.UploadAsync(RawBody(
        "{\"canvas\":{\"width\":8,\"height\":8},\"state\":{\"rotation\":0,\"marks\":[]}," +
        "\"patches\":[],\"extra\":1}"),
        TestContext.Current.CancellationToken));

    Assert.Contains("JSON is invalid", error.Message);
    Assert.Equal((0, null), store.Staged);
  }

  [Fact]
  public async Task UploadSupersededByRevokeDuringReadDoesNotStage()
  {
    using var store = new WorkbenchProjectStore();
    GatedJsonStream stream = new(
      "{\"canvas\":{\"width\":8,\"height\":8},\"state\":{\"rotation\":0,\"marks\":[]},\"patches\":[]}");
    Task upload = Task.Run(() => store.UploadAsync(stream, TestContext.Current.CancellationToken));
    await stream.ReadStarted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    store.RevokeAll();
    stream.Unblock();

    WorkbenchProjectUploadException error = await Assert.ThrowsAsync<WorkbenchProjectUploadException>(
      () => upload);
    Assert.Contains("superseded", error.Message);
    Assert.Equal((0, null), store.Staged);
  }

  [Fact]
  public void ProjectUploadRequestRequiresExactJsonMediaType()
  {
    Uri uri = new("https://app.vibeocr/__project");
    Assert.True(WebWorkbenchHost.IsProjectUploadRequest(
      uri, "POST", "application/json"));
    Assert.True(WebWorkbenchHost.IsProjectUploadRequest(
      uri, "POST", "application/json; charset=utf-8"));
    Assert.False(WebWorkbenchHost.IsProjectUploadRequest(
      uri, "POST", "application/jsonx"));
    Assert.False(WebWorkbenchHost.IsProjectUploadRequest(
      uri, "POST", "text/plain"));
    Assert.False(WebWorkbenchHost.IsProjectUploadRequest(
      uri, "GET", "application/json"));
    Assert.False(WebWorkbenchHost.IsProjectUploadRequest(
      new Uri("https://app.vibeocr/__project?x=1"), "POST", "application/json"));
  }

  [Fact]
  public async Task ContributionFeedsAtomicProjectWriteRoundTrip()
  {
    string directory = NewTempDirectory();
    try
    {
      using var store = new WorkbenchProjectStore();
      byte[] source = PngBytes(64, 48);
      byte[] patch = PngBytes(4, 3);
      WorkbenchProjectContributionLease lease = await store.UploadAsync(
        Body(new VibpCanvas { Width = 64, Height = 48 }, null, (1, 2, 3, 4, 3, patch)),
        TestContext.Current.CancellationToken);
      WorkbenchProjectContribution? contribution = store.TryTake(lease.ContributeId);
      Assert.NotNull(contribution);

      // 宿主拼装：会话持有 source 字节 + 贡献 state/补丁 → manifest+entries。
      var manifest = new VibpProjectManifest
      {
        SchemaVersion = VibpProjectLimits.SchemaVersion,
        Canvas = contribution.Canvas,
        Source = new VibpSourceResource
        {
          MediaType = "image/png",
          Width = 64,
          Height = 48,
          Length = source.Length,
        },
        Assets = [],
        State = contribution.State with
        {
          Patches = [.. contribution.Patches.Select(patchBytes => new VibpPatch
          {
            Id = patchBytes.Id,
            X = patchBytes.X,
            Y = patchBytes.Y,
            Width = patchBytes.Width,
            Height = patchBytes.Height,
            Length = patchBytes.Png.LongLength,
          })],
        },
      };
      string packagePath = Path.Combine(directory, "project.vibp");
      VibpProjectFile.Write(packagePath, manifest,
      [
        new VibpProjectEntry("source.png", source.Length, () => new MemoryStream(source)),
        new VibpProjectEntry("patch-1.png", patch.Length, () => new MemoryStream(patch)),
      ]);

      VibpProjectArchive archive = await VibpProjectFile.ReadAsync(
        packagePath, Path.Combine(directory, "open"), TestContext.Current.CancellationToken);
      Assert.Equal(source, File.ReadAllBytes(archive.SourcePath));
      Assert.Equal(patch, File.ReadAllBytes(archive.PatchPaths[1]));
      byte[] firstWrite = File.ReadAllBytes(packagePath);

      // 保存中断（entry 长度不闭合）保留旧工程文件。
      Assert.Throws<VibpProjectFormatException>(() => VibpProjectFile.Write(packagePath, manifest,
      [
        new VibpProjectEntry("source.png", source.Length, () => new MemoryStream(source)),
        new VibpProjectEntry("patch-1.png", patch.Length, () => new MemoryStream(patch[..^1])),
      ]));
      Assert.Equal(firstWrite, File.ReadAllBytes(packagePath));
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  private static MemoryStream Body(
    VibpCanvas canvas,
    VibpEditorState? state,
    params (int Id, int X, int Y, int Width, int Height, byte[] Png)[] patches)
  {
    JsonObject root = new()
    {
      ["canvas"] = JsonSerializer.SerializeToNode(new { canvas.Width, canvas.Height }, Camel),
      ["state"] = JsonSerializer.SerializeToNode(
        state ?? new VibpEditorState { Rotation = 0, Marks = [] }, Camel),
      ["patches"] = new JsonArray(
      [.. patches.Select(patch => JsonSerializer.SerializeToNode(new
      {
        id = patch.Id,
        x = patch.X,
        y = patch.Y,
        width = patch.Width,
        height = patch.Height,
        png = Convert.ToBase64String(patch.Png),
      }, Camel))]),
    };
    return new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(root));
  }

  private static MemoryStream RawBody(string json) =>
    new(Encoding.UTF8.GetBytes(json));

  private static string NewTempDirectory() =>
    Directory.CreateDirectory(Path.Combine(
      Path.GetTempPath(), $"vibp-upload-{Guid.NewGuid():N}")).FullName;

  /// <summary>首读阻塞的门控流：驱动 RevokeAll 与在途上传的代际竞争。</summary>
  private sealed class GatedJsonStream(string json) : Stream
  {
    private readonly TaskCompletionSource readStarted = new(
      TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource unblock = new(
      TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly byte[] content = Encoding.UTF8.GetBytes(json);
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

  /// <summary>恒有数据的流：用于触发 64 MiB body 预算的流式早拒。</summary>
  private sealed class EndlessStream : Stream
  {
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
      get => throw new NotSupportedException();
      set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
      Array.Fill(buffer, (byte)' ', offset, count);
      return count;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }

  private sealed class AdjustableTimeProvider : TimeProvider
  {
    private readonly List<ManualTimer> timers = [];
    private DateTimeOffset utcNow = DateTimeOffset.UnixEpoch;

    public override DateTimeOffset GetUtcNow() => utcNow;

    public override ITimer CreateTimer(
      TimerCallback callback,
      object? state,
      TimeSpan dueTime,
      TimeSpan period)
    {
      ManualTimer timer = new(callback, utcNow + dueTime);
      lock (timers)
      {
        timers.Add(timer);
      }
      return timer;
    }

    /// <summary>推进时钟并触发所有到期未取消的计时器（同步、确定性）。</summary>
    public void Advance(TimeSpan duration)
    {
      utcNow += duration;
      List<ManualTimer> due = [];
      lock (timers)
      {
        due.AddRange(timers.Where(timer => !timer.Disposed && timer.Due <= utcNow));
      }
      foreach (ManualTimer timer in due)
      {
        timer.Fire();
      }
    }

    private sealed class ManualTimer(TimerCallback callback, DateTimeOffset due) : ITimer
    {
      public DateTimeOffset Due { get; } = due;

      public bool Disposed { get; private set; }

      public void Fire() => callback(null);

      public bool Change(TimeSpan dueTime, TimeSpan period) => true;

      public void Dispose() => Disposed = true;

      public ValueTask DisposeAsync()
      {
        Disposed = true;
        return ValueTask.CompletedTask;
      }
    }
  }

  /// <summary>真实 PNG（8bit RGB，完整 zlib/Adler32/CRC32，平台可解码）。</summary>
  private static byte[] PngBytes(int width, int height)
  {
    byte[] scanlines = new byte[height * (1 + width * 3)];
    for (int index = 0; index < scanlines.Length; index++)
    {
      scanlines[index] = (byte)(index % 251);
    }
    using var deflated = new MemoryStream();
    using (var deflater = new DeflateStream(deflated, CompressionLevel.Fastest, leaveOpen: true))
    {
      deflater.Write(scanlines);
    }
    byte[] zlib = [0x78, 0x01, .. deflated.ToArray(), .. Adler32(scanlines)];
    byte[] header = new byte[13];
    BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width);
    BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), height);
    header[8] = 8;
    header[9] = 2;
    header[10] = 0;
    header[11] = 0;
    header[12] = 0;
    return
    [
      0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
      .. PngChunk("IHDR", header),
      .. PngChunk("IDAT", zlib),
      .. PngChunk("IEND", []),
    ];
  }

  private static byte[] PngChunk(string type, byte[] data)
  {
    using var chunk = new MemoryStream();
    Span<byte> length = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)data.Length));
    chunk.Write(length);
    byte[] typeBytes = Encoding.ASCII.GetBytes(type);
    chunk.Write(typeBytes);
    chunk.Write(data);
    // 标准 PNG CRC32（type+data），与生产共享校验一致。
    byte[] payload = [.. typeBytes, .. data];
    chunk.Write(VibpProjectFileTests.CrcChunkBytes(payload));
    return chunk.ToArray();
  }

  private static byte[] Adler32(byte[] data)
  {
    uint a = 1;
    uint b = 0;
    foreach (byte value in data)
    {
      a = (a + value) % 65521;
      b = (b + a) % 65521;
    }
    uint checksum = (b << 16) | a;
    return
    [
      (byte)(checksum >> 24), (byte)(checksum >> 16),
      (byte)(checksum >> 8), (byte)checksum,
    ];
  }
}

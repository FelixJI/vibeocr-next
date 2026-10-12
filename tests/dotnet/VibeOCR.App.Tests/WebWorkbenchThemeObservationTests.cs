using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using Xunit;

namespace VibeOCR.App.Tests;

// 主题观察通知（#217 原生 chrome 同步接缝）：既有 settings 真值在 bootstrap
// 初始值与订阅流变化时各通知一次，重复同值不重发；fixedRoute 投递契约不变。
// Posted/Observed 由后台订阅线程写、测试线程轮询读，一律 ConcurrentQueue。
public sealed class WebWorkbenchThemeObservationTests
{
  private static string Bootstrap(Guid requestId) => JsonSerializer.Serialize(new
  {
    version = 2,
    kind = "request",
    id = requestId,
    type = "app.bootstrap",
    payload = new { },
  });

  private static SettingsWorkbenchState Settings(WorkbenchTheme theme) =>
    new(theme, IsBusy: false, StatusCode: "idle", Backend: null, StartupEnabled: false);

  private static WorkbenchStateEnvelope State(long revision, WorkbenchTheme theme) =>
    new(revision, "settings", WorkbenchStateChange.Replace, Settings(theme));

  [Fact]
  public async Task BootstrapThemeNotifiesOnceAndStreamChangesDeduplicate()
  {
    await using ThemeFixture fixture = new(
      bootstrapTheme: WorkbenchTheme.Dark,
      streamThemes: [WorkbenchTheme.Dark, WorkbenchTheme.Dark, WorkbenchTheme.Light]);
    await fixture.Host.ReceiveWebMessageAsync(Bootstrap(Guid.NewGuid()), fixture.Document);
    Assert.Equal([WorkbenchTheme.Dark], fixture.ObservedSnapshot);

    fixture.ReleaseStream();
    await WaitUntilObservedAsync(fixture, expected: 2);
    Assert.Equal([WorkbenchTheme.Dark, WorkbenchTheme.Light], fixture.ObservedSnapshot);
  }

  [Fact]
  public async Task BootstrapWithoutSettingsStateDefersToFirstObservedTheme()
  {
    await using ThemeFixture fixture = new(
      bootstrapTheme: null,
      streamThemes: [WorkbenchTheme.Light]);
    await fixture.Host.ReceiveWebMessageAsync(Bootstrap(Guid.NewGuid()), fixture.Document);
    Assert.Empty(fixture.ObservedSnapshot);

    fixture.ReleaseStream();
    await WaitUntilObservedAsync(fixture, expected: 1);
    Assert.Equal([WorkbenchTheme.Light], fixture.ObservedSnapshot);
  }

  [Fact]
  public async Task FixedRouteEditorDeliversThemeChangesToWebAndNativeObservers()
  {
    await using ThemeFixture fixture = new(
      bootstrapTheme: WorkbenchTheme.Dark,
      streamThemes: [WorkbenchTheme.Dark, WorkbenchTheme.Dark, WorkbenchTheme.Light],
      fixedRoute: WorkbenchRoute.ImageEdit);
    await fixture.Host.ReceiveWebMessageAsync(Bootstrap(Guid.NewGuid()), fixture.Document);
    Assert.Equal([WorkbenchTheme.Dark], fixture.ObservedSnapshot);

    // 主题随帧同链投递：以实际 Posted 的 settings 帧到达为准再断言。
    fixture.ReleaseStream();
    await WaitUntilPostedSettingsThemesAsync(fixture, ["light"]);
    Assert.Equal([WorkbenchTheme.Dark, WorkbenchTheme.Light], fixture.ObservedSnapshot);
    // 重复同值不投递；仅真实变化（light）到达 WebView；初始 dark 在 bootstrap。
    Assert.Equal(["light"], PostedSettingsThemes(fixture));
    Assert.Contains(fixture.PostedSnapshot, json =>
    {
      using JsonDocument document = JsonDocument.Parse(json);
      return document.RootElement.GetProperty("type").GetString() == "app.bootstrap" &&
        document.RootElement.GetProperty("payload").GetProperty("theme").GetString() == "dark";
    });
  }

  [Fact]
  public async Task RejectedStateDispatchRequestsRecoveryInsteadOfPublishingInline()
  {
    await using ThemeFixture fixture = new(null, [WorkbenchTheme.Light]);
    var recovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.Host.RecoveryRequired += () => recovery.TrySetResult();
    fixture.Host.RegisterBridge(fixture.Document, fixture.Posted.Enqueue, _ => false);
    await fixture.Host.ReceiveWebMessageAsync(Bootstrap(Guid.NewGuid()), fixture.Document);
    fixture.ReleaseStream();
    await recovery.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    Assert.Empty(PostedSettingsThemes(fixture));
  }

  [Fact]
  public async Task StaleSubscriptionThemeLandingAfterRebootstrapIsDroppedAtDelivery()
  {
    // 回归：旧订阅越过取消检查进入主题通知后，二次 bootstrap 已发布当前
    // 主题才落地旧主题。enqueue 只排队、由测试在受控时机执行，模拟真实
    // dispatcher 的异步出队，构造确定顺序。
    await using RaceFixture fixture = new();
    await fixture.Host.ReceiveWebMessageAsync(Bootstrap(Guid.NewGuid()), fixture.Document);
    fixture.ReleaseFirstStream();
    // 旧实现在消费点发出 System 通知（StaleThemeEntered）；新实现只把通知
    // 随帧排队（StreamFrameQueued）。首个信号即区分两条路径。
    Task firstSignal = await Task.WhenAny(fixture.StaleThemeEntered.Task, fixture.StreamFrameQueued.Task)
      .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    await fixture.Host.ReceiveWebMessageAsync(Bootstrap(Guid.NewGuid()), fixture.Document);
    fixture.RunQueued();
    if (ReferenceEquals(firstSignal, fixture.StaleThemeEntered.Task))
    {
      // 旧路径：被挂起的 System 通知落地后（Dark 已先记录）再断言失败。
      await fixture.StaleThemeLanded.Task.WaitAsync(
        TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }
    // 新路径：旧帧回调已在 RunQueued 内同步执行并被身份校验丢弃。
    Assert.Equal([WorkbenchTheme.Light, WorkbenchTheme.Dark], fixture.ObservedSnapshot);
  }

  /// <summary>双 bootstrap 竞态夹具：enqueue 只排队不出队，由测试控制
  /// 出队时机；System 主题订阅方在 Dark 落地前挂起。</summary>
  private sealed class RaceFixture : IAsyncDisposable
  {
    private readonly TaskCompletionSource firstStreamGate =
      new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource secondStreamGate =
      new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int bootstrapCalls;
    private volatile bool firstStreamReleased;

    public readonly string Root = Path.Combine(Path.GetTempPath(), $"workbench-theme-race-{Guid.NewGuid():N}");
    public readonly WebWorkbenchHost Host;
    public readonly WorkbenchResourceBroker Broker;
    public readonly WorkbenchAnnotationStore Annotations;
    public readonly ConcurrentQueue<string> Posted = [];
    public readonly ConcurrentQueue<WorkbenchTheme> Observed = [];
    public readonly object Document = new();
    public readonly TaskCompletionSource StaleThemeEntered =
      new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource StaleThemeLanded =
      new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource DarkThemeRecorded =
      new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource StreamFrameQueued =
      new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<Action> queued = [];
    public List<string> PostedSnapshot => Posted.ToList();
    public List<WorkbenchTheme> ObservedSnapshot => Observed.ToList();

    public RaceFixture()
    {
      Directory.CreateDirectory(Root);
      Broker = new WorkbenchResourceBroker(Root);
      Annotations = new WorkbenchAnnotationStore(Root);
      Host = new WebWorkbenchHost(
        new RaceApplication(this),
        Broker,
        Annotations,
        applicationOwner: false,
        fixedRoute: null);
      Host.RegisterBridge(Document, Posted.Enqueue, action =>
      {
        queued.Enqueue(action);
        if (firstStreamReleased && !StreamFrameQueued.Task.IsCompleted)
          StreamFrameQueued.TrySetResult();
        return true;
      });
      Host.ThemeObserved += theme =>
      {
        if (theme == WorkbenchTheme.System)
        {
          StaleThemeEntered.TrySetResult();
          DarkThemeRecorded.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
          Observed.Enqueue(theme);
          StaleThemeLanded.TrySetResult();
          return;
        }
        Observed.Enqueue(theme);
        if (theme == WorkbenchTheme.Dark) DarkThemeRecorded.TrySetResult();
      };
    }

    public void ReleaseFirstStream()
    {
      firstStreamReleased = true;
      firstStreamGate.TrySetResult();
    }

    public void RunQueued()
    {
      while (queued.TryDequeue(out Action? action)) action();
    }

    public async ValueTask DisposeAsync()
    {
      firstStreamGate.TrySetResult();
      secondStreamGate.TrySetResult();
      DarkThemeRecorded.TrySetResult();
      await Host.DisposeAsync();
      Broker.Dispose();
      Annotations.Dispose();
      Directory.Delete(Root, recursive: true);
    }

    private sealed class RaceApplication(RaceFixture owner) : IWorkbenchApplication
    {
      public ValueTask<WorkbenchBootstrap> BootstrapAsync(CancellationToken cancellationToken)
      {
        WorkbenchTheme theme = Interlocked.Increment(ref owner.bootstrapCalls) == 1
          ? WorkbenchTheme.Light
          : WorkbenchTheme.Dark;
        return ValueTask.FromResult(new WorkbenchBootstrap(
          WorkbenchProtocol.Version,
          Guid.NewGuid(),
          0,
          WorkbenchRoute.Recognition,
          [State(0, theme)],
          new HashSet<string>()));
      }

      public ValueTask<WorkbenchCommandReceipt> ExecuteAsync(
        WorkbenchCommandEnvelope envelope,
        CancellationToken cancellationToken) => throw new InvalidOperationException(
        "Theme race tests do not execute commands.");

      public async IAsyncEnumerable<WorkbenchStateEnvelope> SubscribeAsync(
        long afterRevision,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        IReadOnlySet<string>? scopes = null)
      {
        TaskCompletionSource gate = Volatile.Read(ref owner.bootstrapCalls) == 1
          ? owner.firstStreamGate
          : owner.secondStreamGate;
        try { await gate.Task.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { yield break; }
        if (ReferenceEquals(gate, owner.firstStreamGate))
          yield return State(++afterRevision, WorkbenchTheme.System);
      }

      public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
  }

  private static async Task WaitUntilObservedAsync(ThemeFixture fixture, int expected)
  {
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while (fixture.Observed.Count < expected && !timeout.Token.IsCancellationRequested)
      await Task.Delay(TimeSpan.FromMilliseconds(20));
    Assert.True(fixture.Observed.Count >= expected,
      $"theme observations did not reach {expected}: [{string.Join(", ", fixture.ObservedSnapshot)}]");
  }

  /// <summary>已投递到 WebView 的 settings 主题帧快照（小写枚举值）。</summary>
  private static List<string> PostedSettingsThemes(ThemeFixture fixture) =>
    fixture.PostedSnapshot
      .Select(json =>
      {
        using JsonDocument document = JsonDocument.Parse(json);
        return (document.RootElement.GetProperty("type").GetString() == "app.state" &&
          document.RootElement.GetProperty("payload").GetProperty("scope").GetString() == "settings")
          ? document.RootElement.GetProperty("payload").GetProperty("state").GetProperty("theme").GetString()
          : null;
      })
      .Where(theme => theme is not null)
      .Select(theme => theme!)
      .ToList();

  private static async Task WaitUntilPostedSettingsThemesAsync(ThemeFixture fixture, IReadOnlyList<string> expected)
  {
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    List<string> current;
    while ((current = PostedSettingsThemes(fixture)).SequenceEqual(expected) is false &&
      !timeout.Token.IsCancellationRequested)
      await Task.Delay(TimeSpan.FromMilliseconds(20));
    Assert.True(current.SequenceEqual(expected),
      $"posted settings themes were [{string.Join(", ", current)}], expected [{string.Join(", ", expected)}]");
  }

  private sealed class ThemeFixture : IAsyncDisposable
  {
    public readonly string Root = Path.Combine(Path.GetTempPath(), $"workbench-theme-{Guid.NewGuid():N}");
    public readonly WebWorkbenchHost Host;
    public readonly WorkbenchResourceBroker Broker;
    public readonly WorkbenchAnnotationStore Annotations;
    public readonly ConcurrentQueue<string> Posted = [];
    public readonly ConcurrentQueue<WorkbenchTheme> Observed = [];
    public readonly object Document = new();
    public List<string> PostedSnapshot => Posted.ToList();
    public List<WorkbenchTheme> ObservedSnapshot => Observed.ToList();
    private readonly TaskCompletionSource StreamGate =
      new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ThemeFixture(
      WorkbenchTheme? bootstrapTheme,
      IReadOnlyList<WorkbenchTheme> streamThemes,
      WorkbenchRoute? fixedRoute = null)
    {
      Directory.CreateDirectory(Root);
      Broker = new WorkbenchResourceBroker(Root);
      Annotations = new WorkbenchAnnotationStore(Root);
      Host = new WebWorkbenchHost(
        new ThemeApplication(bootstrapTheme, streamThemes, StreamGate),
        Broker,
        Annotations,
        applicationOwner: false,
        fixedRoute);
      Host.RegisterBridge(Document, Posted.Enqueue, action => { action(); return true; });
      Host.ThemeObserved += Observed.Enqueue;
    }

    public void ReleaseStream() => StreamGate.TrySetResult();

    public async ValueTask DisposeAsync()
    {
      StreamGate.TrySetResult();
      await Host.DisposeAsync();
      Broker.Dispose();
      Annotations.Dispose();
      Directory.Delete(Root, recursive: true);
    }
  }

  private sealed class ThemeApplication(
    WorkbenchTheme? bootstrapTheme,
    IReadOnlyList<WorkbenchTheme> streamThemes,
    TaskCompletionSource streamGate) : IWorkbenchApplication
  {
    public async ValueTask<WorkbenchBootstrap> BootstrapAsync(CancellationToken cancellationToken)
    {
      await Task.CompletedTask;
      IReadOnlyList<WorkbenchStateEnvelope> states = bootstrapTheme is { } theme
        ? [State(0, theme)]
        : [];
      return new WorkbenchBootstrap(
        WorkbenchProtocol.Version,
        Guid.NewGuid(),
        0,
        WorkbenchRoute.Recognition,
        states,
        new HashSet<string>());
    }

    public ValueTask<WorkbenchCommandReceipt> ExecuteAsync(
      WorkbenchCommandEnvelope envelope,
      CancellationToken cancellationToken) => throw new InvalidOperationException(
      "Theme observation tests do not execute commands.");

    public async IAsyncEnumerable<WorkbenchStateEnvelope> SubscribeAsync(
      long afterRevision,
      [EnumeratorCancellation] CancellationToken cancellationToken,
      IReadOnlySet<string>? scopes = null)
    {
      try
      {
        await streamGate.Task.WaitAsync(cancellationToken);
      }
      catch (OperationCanceledException)
      {
        yield break;
      }
      long revision = afterRevision;
      foreach (WorkbenchTheme theme in streamThemes)
        yield return State(++revision, theme);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }
}

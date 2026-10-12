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

    // 通知先于投递触发：以实际 Posted 的 settings 帧到达为准再断言。
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

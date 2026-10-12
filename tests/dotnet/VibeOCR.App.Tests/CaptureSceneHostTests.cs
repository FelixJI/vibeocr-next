using System.Runtime.CompilerServices;
using System.Text.Json;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class CaptureSceneHostTests
{
  [Fact]
  public async Task PixelsMustOwnCurrentBridgeDocumentBootstrapSessionAndInputAndOnlyCompleteOnce()
  {
    await using var fixture = new Fixture();
    var first = await fixture.BootstrapAsync();
    foreach (CaptureSceneDescriptor stale in new[] {
      first.Scene with { SurfaceId = Guid.NewGuid() }, first.Scene with { CaptureSessionId = Guid.NewGuid().ToString("N") },
      first.Scene with { InputUrl = first.Scene.InputUrl + "stale" }, first.Scene with { InputRevision = 1 },
      first.Scene with { BootstrapGeneration = first.Scene.BootstrapGeneration + 1 },
      first.Scene with { DocumentGeneration = first.Scene.DocumentGeneration + 1 } })
      await fixture.ReportAsync(first.Session, stale);
    await fixture.ReportAsync(Guid.NewGuid(), first.Scene);
    await fixture.Host.ReceiveWebMessageAsync(Reply(first.Session, first.Scene), new object());
    Assert.DoesNotContain("capture-pixel-ready", fixture.States);

    var second = await fixture.BootstrapAsync();
    await fixture.ReportAsync(first.Session, first.Scene);
    fixture.Host.ExpireCapturePixels(first.Scene); // Superseded deadline cannot reload the newer bootstrap.
    Assert.DoesNotContain("recovery-reload", fixture.States);
    await fixture.ReportAsync(second.Session, second.Scene);
    await fixture.ReportAsync(second.Session, second.Scene);
    Assert.Equal(1, fixture.States.Count(state => state == "capture-pixel-ready"));
    fixture.Host.ResetWebDocument();
    await fixture.ReportAsync(second.Session, second.Scene);
    Assert.Equal(1, fixture.States.Count(state => state == "capture-pixel-ready"));
    var changed = await fixture.BootstrapAsync();
    fixture.Host.ObserveCaptureState(new(2, "recognition", WorkbenchStateChange.Replace,
      new RecognitionWorkbenchState(false, "recognition.session", Input: new(changed.Scene.InputUrl + "new", "image/png", 100),
        ScreenshotSession: new(changed.Scene.CaptureSessionId, 0, SceneEditing: true))));
    await fixture.ReportAsync(changed.Session, changed.Scene);
    Assert.Equal(1, fixture.States.Count(state => state == "capture-pixel-ready"));
    var third = await fixture.BootstrapAsync();
    await fixture.Host.DisposeAsync();
    fixture.Host.ExpireCapturePixels(third.Scene);
    await fixture.ReportAsync(third.Session, third.Scene);
    Assert.Equal(1, fixture.States.Count(state => state == "capture-pixel-ready"));
    Assert.Equal(0, fixture.NativeRecoveries);
  }

  [Fact]
  public async Task BootstrapReadyDoesNotReplenishCaptureRecoveryButPixelReadyDoes()
  {
    await using var fixture = new Fixture();
    var first = await fixture.BootstrapAsync();
    await fixture.ReportAsync(first.Session, first.Scene, "sourceDecode");
    Assert.Contains("recovery-reload", fixture.States);
    var second = await fixture.BootstrapAsync();
    Assert.Contains("bridge-ready", fixture.States);
    fixture.Host.ExpireCapturePixels(second.Scene);
    Assert.Equal(1, fixture.NativeRecoveries);
    await fixture.ReportAsync(second.Session, second.Scene);
    Assert.DoesNotContain("capture-pixel-ready", fixture.States);

    var third = await fixture.BootstrapAsync();
    await fixture.ReportAsync(third.Session, third.Scene);
    fixture.Host.HandleRendererFailure("RenderProcessExited");
    Assert.Equal(2, fixture.States.Count(state => state == "recovery-reload"));
    var fourth = await fixture.BootstrapAsync();
    fixture.Host.HandleRendererFailure("RenderProcessExited");
    fixture.Host.ExpireCapturePixels(fourth.Scene);
    Assert.Equal(2, fixture.NativeRecoveries);
    Assert.Equal(2, fixture.States.Count(state => state == "recovery-reload"));
  }

  [Fact]
  public void CaptureMetadataSurvivesBothBootstrapShapesWithoutRelaxingFrameBudget()
  {
    var configuration = new CaptureSceneDescriptor(Guid.NewGuid(), Guid.NewGuid().ToString("N"),
      "https://app.vibeocr/__resource/input", 0, 2, 3, 10, 20, 100, 50, 3840, 2160, "select");
    var states = new WorkbenchStateEnvelope[] {
      new(1, "shell", WorkbenchStateChange.Replace, new ShellWorkbenchState(WorkbenchRoute.ImageEdit)),
      new(1, "recognition", WorkbenchStateChange.Replace, new RecognitionWorkbenchState(false, new string('x', 40000))),
      new(1, "imageEdit", WorkbenchStateChange.Replace, new ImageEditWorkbenchState(false, new string('x', 40000))),
    };
    var bootstrap = new WorkbenchBootstrap(2, Guid.NewGuid(), 1, WorkbenchRoute.ImageEdit, states, new HashSet<string>());
    var large = WorkbenchBridgeCodec.SerializeBootstrapResponse(Guid.NewGuid(), bootstrap, configuration);
    var small = WorkbenchBridgeCodec.SerializeBootstrapResponse(Guid.NewGuid(), bootstrap with { States = [states[0]] }, configuration);
    Assert.True(large.RequiresSnapshot);
    Assert.False(small.RequiresSnapshot);
    foreach (var response in new[] { large, small })
    {
      using JsonDocument document = JsonDocument.Parse(response.Json);
      Assert.Equal(JsonSerializer.Serialize(configuration, Options), document.RootElement.GetProperty("payload").GetProperty("captureScene").GetRawText());
      Assert.True(System.Text.Encoding.UTF8.GetByteCount(response.Json) <= WorkbenchBridgeCodec.MaxMessageBytes);
    }
    Assert.Throws<WorkbenchBridgeProtocolException>(() => CaptureSceneMessages.Parse(Reply(Guid.NewGuid(), configuration, "unbounded reason")));
  }

  private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
  private static string Reply(Guid session, CaptureSceneDescriptor scene, string? failure = null) => JsonSerializer.Serialize(new {
    version = 2, kind = "event", id = Guid.NewGuid(), type = failure is null ? "capture.pixelReady" : "capture.pixelFailed",
    payload = failure is null ? (object)new { sessionId = session, captureScene = scene }
      : new { sessionId = session, captureScene = scene, reason = failure },
  }, Options);

  private sealed class Fixture : IAsyncDisposable
  {
    private readonly string root = Path.Combine(Path.GetTempPath(), $"capture-pixels-{Guid.NewGuid():N}");
    private readonly object sender = new();
    private readonly List<string> posted = [];
    private readonly WorkbenchResourceBroker broker;
    private readonly WorkbenchAnnotationStore annotations;
    public WebWorkbenchHost Host { get; }
    public List<string> States { get; } = [];
    public int NativeRecoveries;

    public Fixture()
    {
      Directory.CreateDirectory(root);
      Guid capture = Guid.NewGuid();
      broker = new(root); annotations = new(root);
      Host = new(new CaptureApplication(capture), broker, annotations, applicationOwner: false, fixedRoute: WorkbenchRoute.ImageEdit) {
        CaptureScene = new(Guid.NewGuid(), capture, 10, 20, 100, 50, 3840, 2160, "select"),
      };
      Host.RegisterBridge(sender, posted.Add);
      Host.StateChanged += States.Add;
      Host.RecoveryRequired += () => NativeRecoveries++;
    }

    public async Task<(Guid Session, CaptureSceneDescriptor Scene)> BootstrapAsync()
    {
      await Host.ReceiveWebMessageAsync(JsonSerializer.Serialize(new { version = 2, kind = "request", id = Guid.NewGuid(), type = "app.bootstrap", payload = new { } }), sender);
      using JsonDocument document = JsonDocument.Parse(posted[^1]);
      JsonElement payload = document.RootElement.GetProperty("payload");
      return (payload.GetProperty("sessionId").GetGuid(), payload.GetProperty("captureScene").Deserialize<CaptureSceneDescriptor>(Options)!);
    }
    public Task ReportAsync(Guid session, CaptureSceneDescriptor scene, string? failure = null) => Host.ReceiveWebMessageAsync(Reply(session, scene, failure), sender);
    public async ValueTask DisposeAsync()
    {
      await Host.DisposeAsync(); broker.Dispose(); annotations.Dispose(); Directory.Delete(root, recursive: true);
    }
  }

  private sealed class CaptureApplication(Guid capture) : IWorkbenchApplication
  {
    public ValueTask<WorkbenchBootstrap> BootstrapAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new WorkbenchBootstrap(
      2, Guid.NewGuid(), 1, WorkbenchRoute.Recognition, [
        new(1, "shell", WorkbenchStateChange.Replace, new ShellWorkbenchState(WorkbenchRoute.Recognition)),
        new(1, "recognition", WorkbenchStateChange.Replace, new RecognitionWorkbenchState(false, "recognition.session",
          Input: new("https://app.vibeocr/__resource/input", "image/png", 100),
          ScreenshotSession: new(capture.ToString("N"), 0, SceneEditing: true))),
      ], new HashSet<string>()));
    public ValueTask<WorkbenchCommandReceipt> ExecuteAsync(WorkbenchCommandEnvelope envelope, CancellationToken cancellationToken) => throw new InvalidOperationException("Pixels are not commands.");
    public async IAsyncEnumerable<WorkbenchStateEnvelope> SubscribeAsync(long afterRevision,
      [EnumeratorCancellation] CancellationToken cancellationToken, IReadOnlySet<string>? scopes = null)
    {
      await Task.Delay(Timeout.Infinite, cancellationToken);
      yield break;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }
}

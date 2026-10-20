using System.Text;
using System.Text.Json;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using VibeOCR.App.Features.Configuration;
using Windows.Storage.Streams;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class WebWorkbenchHostPolicyTests
{
  [Fact]
  public async Task SceneHostProjectsItsRouteAndDisposesOnlyItsOwnSubscription()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-scene-host-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
      await using var app = new WorkbenchApplication([], WorkbenchRoute.Recognition);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      var scene = new WebWorkbenchHost(app, broker, annotations,
        applicationOwner: false, fixedRoute: WorkbenchRoute.ImageEdit);
      WorkbenchBootstrap bootstrap = await app.BootstrapAsync(TestContext.Current.CancellationToken);
      WorkbenchBootstrap projected = scene.ProjectBootstrap(bootstrap);
      Assert.Equal(WorkbenchRoute.ImageEdit, projected.Route);
      Assert.Equal(WorkbenchRoute.Recognition, bootstrap.Route);
      Assert.Equal(WorkbenchRoute.ImageEdit, Assert.IsType<ShellWorkbenchState>(
        projected.States.Single(item => item.Scope == "shell").State).Route);
      var navigation = new WorkbenchStateEnvelope(1, "shell", WorkbenchStateChange.Replace,
        new ShellWorkbenchState(WorkbenchRoute.Pdf));
      Assert.Equal(WorkbenchRoute.ImageEdit,
        Assert.IsType<ShellWorkbenchState>(scene.ProjectState(navigation).State).Route);
      var settings = new WorkbenchStateEnvelope(2, "settings", WorkbenchStateChange.Replace,
        new SettingsWorkbenchState(WorkbenchTheme.Dark, true, "settings.ready", "cpu", false,
          EnvironmentInstallLog: [new string('文', 20000)], EnvironmentStatus: "安装环境", EnvironmentBusy: true));
      string sceneSettings = WorkbenchBridgeCodec.SerializeState(Guid.Empty, scene.ProjectState(settings));
      using (JsonDocument projectedSettings = JsonDocument.Parse(sceneSettings))
      {
        JsonElement state = projectedSettings.RootElement.GetProperty("payload").GetProperty("state");
        Assert.Equal("dark", state.GetProperty("theme").GetString()); Assert.Single(state.EnumerateObject());
      }
      var full = bootstrap with { States = [.. bootstrap.States, settings,
        new WorkbenchStateEnvelope(3, "pdf", WorkbenchStateChange.Replace, new PdfWorkbenchState(true, "pdf.open", 64, 0)),
        new WorkbenchStateEnvelope(4, "batch", WorkbenchStateChange.Replace, new BatchWorkbenchState(true, 40, 0, 0))] };
      Assert.DoesNotContain(scene.ProjectBootstrap(full).States, state => state.Scope is "pdf" or "batch");
      using (JsonDocument sceneBootstrap = JsonDocument.Parse(WorkbenchBridgeCodec.SerializeBootstrap(Guid.NewGuid(), scene.ProjectBootstrap(full))))
      {
        var payload = sceneBootstrap.RootElement.GetProperty("payload");
        Assert.Equal("dark", payload.GetProperty("theme").GetString());
        var lightSettings = payload.GetProperty("features").GetProperty("settings");
        Assert.Equal("dark", lightSettings.GetProperty("theme").GetString());
        Assert.Single(lightSettings.EnumerateObject());
      }

      await scene.DisposeAsync();
      Assert.Equal(WorkbenchRoute.Recognition,
        (await app.BootstrapAsync(TestContext.Current.CancellationToken)).Route);
      string path = Path.Combine(root, "active.txt");
      await File.WriteAllTextAsync(path, "still owned by main", TestContext.Current.CancellationToken);
      WorkbenchResourceLease lease = broker.Lease("active.txt", "text/plain", TimeSpan.FromMinutes(1));
      await using var response = await broker.OpenAsync(lease.Uri, TestContext.Current.CancellationToken);
      Assert.Equal("text/plain", response.ContentType);
    }
    finally { Directory.Delete(root, recursive: true); }
  }

  [Fact]
  public async Task DisposingPinHostCompletesEverySharedSnapshotWaiter()
  {
    string root = Path.Combine(Path.GetTempPath(), $"pin-flush-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    using var broker = new WorkbenchResourceBroker(root);
    using var annotations = new WorkbenchAnnotationStore(root);
    await using var app = new WorkbenchApplication([], WorkbenchRoute.ImageEdit);
    var host = new WebWorkbenchHost(app, broker, annotations, applicationOwner: false, pinEditHost: true);
    Task<bool> first = host.RequestPinSnapshotFlushAsync(Guid.NewGuid(), _ => { });
    Task<bool> second = host.FlushPinSnapshotAsync(Guid.NewGuid());
    await host.DisposeAsync();
    Assert.False(await first.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
    Assert.False(await second.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task OldDocumentFailureCannotCompleteANewFlushForTheSamePinSession()
  {
    string root = Path.Combine(Path.GetTempPath(), $"pin-stale-flush-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    using var broker = new WorkbenchResourceBroker(root);
    using var annotations = new WorkbenchAnnotationStore(root);
    await using var app = new WorkbenchApplication([], WorkbenchRoute.ImageEdit);
    await using var host = new WebWorkbenchHost(app, broker, annotations, applicationOwner: false, pinEditHost: true);
    object sender = new();
    host.RegisterBridge(sender, _ => { });
    Guid source = Guid.NewGuid();
    string firstRequest = "", currentRequest = "";
    Task<bool> first = host.RequestPinSnapshotFlushAsync(source, json => firstRequest = json);
    host.ResetWebDocument();
    Assert.False(await first);
    Task<bool> current = host.RequestPinSnapshotFlushAsync(source, json => currentRequest = json);
    Task<bool> shared = host.FlushPinSnapshotAsync(source);
    using JsonDocument previous = JsonDocument.Parse(firstRequest);
    using JsonDocument latest = JsonDocument.Parse(currentRequest);
    Guid previousId = Guid.Parse(previous.RootElement.GetProperty("id").GetString()!);
    Guid currentId = Guid.Parse(latest.RootElement.GetProperty("id").GetString()!);
    Assert.NotEqual(previousId, currentId);
    string Failure(Guid id, Guid session) => JsonSerializer.Serialize(new
    {
      version = WorkbenchProtocol.Version, kind = "event", id = id.ToString("N"), type = "pinEdit.snapshotResult",
      payload = new { sessionId = session.ToString("N"), ok = false },
    });
    await host.ReceiveWebMessageAsync(Failure(previousId, source), sender);
    Assert.False(current.IsCompleted, "A stale failure completed the new request for the same native session.");
    Assert.False(shared.IsCompleted);
    await host.ReceiveWebMessageAsync(Failure(currentId, Guid.NewGuid()), sender);
    Assert.False(current.IsCompleted);
    await host.ReceiveWebMessageAsync(Failure(currentId, source), sender);
    Assert.False(await current);
    Assert.False(await shared);
  }

  [Fact]
  public async Task SuccessfulSnapshotAcknowledgementCanDisposeItsOwnHostBeforeCompletingWaiters()
  {
    string root = Path.Combine(Path.GetTempPath(), $"pin-ack-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    using var broker = new WorkbenchResourceBroker(root);
    using var annotations = new WorkbenchAnnotationStore(root);
    await using var app = new WorkbenchApplication([], WorkbenchRoute.ImageEdit);
    await using var host = new WebWorkbenchHost(app, broker, annotations, applicationOwner: false, pinEditHost: true);
    object sender = new();
    host.RegisterBridge(sender, _ => { });
    Guid source = Guid.NewGuid();
    typeof(WebWorkbenchHost).GetField("committedPinSnapshot", System.Reflection.BindingFlags.Instance |
      System.Reflection.BindingFlags.NonPublic)!.SetValue(host, source);
    string request = "";
    Task<bool> first = host.RequestPinSnapshotFlushAsync(source, json => request = json);
    Task<bool> second = host.FlushPinSnapshotAsync(source);
    using JsonDocument sent = JsonDocument.Parse(request);
    string requestId = sent.RootElement.GetProperty("id").GetString()!;
    int acknowledged = 0;
    host.PinSnapshotAcknowledged += async () => { acknowledged++; await host.DisposeAsync(); };
    string Result(string id) => JsonSerializer.Serialize(new
    {
      version = WorkbenchProtocol.Version, kind = "event", id, type = "pinEdit.snapshotResult",
      payload = new { sessionId = source.ToString("N"), ok = true },
    });
    await host.ReceiveWebMessageAsync(Result(Guid.NewGuid().ToString("N")), sender);
    Assert.False(first.IsCompleted);
    Assert.Equal(0, acknowledged);
    await host.ReceiveWebMessageAsync(Result(requestId), sender);
    await host.ReceiveWebMessageAsync(Result(requestId), sender);
    Assert.True(await first);
    Assert.True(await second);
    Assert.Equal(1, acknowledged);
  }

  [Fact]
  public async Task ButtonSnapshotSuccessWithoutANativeFlushAcknowledgesOnlyItsCommitOnce()
  {
    string root = Path.Combine(Path.GetTempPath(), $"pin-button-ack-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    using var broker = new WorkbenchResourceBroker(root);
    using var annotations = new WorkbenchAnnotationStore(root);
    await using var app = new WorkbenchApplication([], WorkbenchRoute.ImageEdit);
    await using var host = new WebWorkbenchHost(app, broker, annotations, applicationOwner: false, pinEditHost: true);
    object sender = new();
    host.RegisterBridge(sender, _ => { });
    Guid source = Guid.NewGuid();
    typeof(WebWorkbenchHost).GetField("committedPinSnapshot", System.Reflection.BindingFlags.Instance |
      System.Reflection.BindingFlags.NonPublic)!.SetValue(host, source);
    int acknowledged = 0;
    host.PinSnapshotAcknowledged += () => { acknowledged++; return Task.CompletedTask; };
    string result = JsonSerializer.Serialize(new
    {
      version = WorkbenchProtocol.Version, kind = "event", id = Guid.NewGuid().ToString("N"), type = "pinEdit.snapshotResult",
      payload = new { sessionId = source.ToString("N"), ok = true },
    });
    await host.ReceiveWebMessageAsync(result, sender);
    await host.ReceiveWebMessageAsync(result, sender);
    Assert.Equal(1, acknowledged);
  }

  [Fact]
  public async Task PinSnapshotTimeoutCompletesEverySharedWaiterAndAllowsAnotherRequest()
  {
    string root = Path.Combine(Path.GetTempPath(), $"pin-timeout-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    using var broker = new WorkbenchResourceBroker(root);
    using var annotations = new WorkbenchAnnotationStore(root);
    await using var app = new WorkbenchApplication([], WorkbenchRoute.ImageEdit);
    await using var host = new WebWorkbenchHost(app, broker, annotations, applicationOwner: false, pinEditHost: true);
    Task<bool> first = host.RequestPinSnapshotFlushAsync(Guid.NewGuid(), _ => { });
    Task<bool> second = host.FlushPinSnapshotAsync(Guid.NewGuid());
    Assert.False(await first.WaitAsync(TimeSpan.FromSeconds(65), TestContext.Current.CancellationToken));
    Assert.False(await second.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
    Task<bool> retry = host.RequestPinSnapshotFlushAsync(Guid.NewGuid(), _ => { });
    Assert.False(retry.IsCompleted);
    await host.DisposeAsync();
    Assert.False(await retry);
  }

  [Fact]
  public async Task PdfPreviewFlushCorrelatesOneRequestAndAbandonsOnCancellationOrDispose()
  {
    string root = Path.Combine(Path.GetTempPath(), $"pdf-flush-{Guid.NewGuid():N}"); Directory.CreateDirectory(root);
    using var broker = new WorkbenchResourceBroker(root); using var annotations = new WorkbenchAnnotationStore(root);
    await using var app = new WorkbenchApplication([], WorkbenchRoute.Pdf);
    var host = new WebWorkbenchHost(app, broker, annotations, applicationOwner: false);
    Guid session = Guid.NewGuid(); string doc = Guid.NewGuid().ToString("N"); string request = "";
    using var cancel = new CancellationTokenSource();
    Task<bool> first = host.RequestPdfPreviewFlushAsync(session, doc, json => request = json, cancel.Token);
    Assert.False(await host.RequestPdfPreviewFlushAsync(session, doc, _ => throw new InvalidOperationException(), CancellationToken.None));
    using JsonDocument sent = JsonDocument.Parse(request); string id = sent.RootElement.GetProperty("id").GetString()!;
    string Reply(string responseId, Guid responseSession, string responseDoc, bool ok) => JsonSerializer.Serialize(new {
      version = 2, kind = "response", id = responseId, type = "pdf.flushPreview", payload = new { sessionId = responseSession, documentId = responseDoc, ok } });
    host.AcceptPdfPreviewFlushResponse("{}"); // 没有匹配 correlation 的未知消息无副作用。
    host.AcceptPdfPreviewFlushResponse(Reply(Guid.NewGuid().ToString(), session, doc, true));
    host.AcceptPdfPreviewFlushResponse(Reply(id, Guid.NewGuid(), doc, true));
    host.AcceptPdfPreviewFlushResponse(Reply(id, session, Guid.NewGuid().ToString("N"), true));
    Assert.False(first.IsCompleted);
    host.AcceptPdfPreviewFlushResponse(Reply(id, session, doc, true)); Assert.True(await first);
    Task<bool> cancelled = host.RequestPdfPreviewFlushAsync(session, doc, _ => { }, cancel.Token);
    cancel.Cancel(); Assert.False(await cancelled);
    host.AcceptPdfPreviewFlushResponse(Reply(id, session, doc, true));
    Task<bool> disposed = host.RequestPdfPreviewFlushAsync(session, doc, _ => { }, CancellationToken.None);
    Assert.False(disposed.IsCompleted); await host.DisposeAsync(); Assert.False(await disposed);
  }

  [Theory]
  [InlineData("[]")]
  [InlineData("null")]
  [InlineData("{\"type\":5}")]
  [InlineData("{\"type\":null}")]
  public void InvalidEnvelopeDoesNotEnterPreviewOrBootstrapDispatch(string json)
  {
    using JsonDocument document = JsonDocument.Parse(json);
    Assert.False(WebWorkbenchHost.IsPdfPreviewFlushResponse(document.RootElement));
    Assert.False(WebWorkbenchHost.IsBootstrap(json));
    Assert.Throws<WorkbenchBridgeProtocolException>(() =>
      WorkbenchBridgeCodec.ParseCommand(json, Guid.NewGuid()));
  }

  [Fact]
  public async Task PackagedAssetsServeOnlyExactSameOriginBundleFiles()
  {
    string parent = Path.Combine(Path.GetTempPath(), $"vibeocr-assets-{Guid.NewGuid():N}");
    string bundle = Path.Combine(parent, "dist");
    Directory.CreateDirectory(Path.Combine(bundle, "assets"));
    try
    {
      await File.WriteAllTextAsync(Path.Combine(bundle, "index.html"), "<main>workbench</main>",
        TestContext.Current.CancellationToken);
      await File.WriteAllTextAsync(Path.Combine(bundle, "assets", "app.js"), "export const ready = true;",
        TestContext.Current.CancellationToken);
      await File.WriteAllTextAsync(Path.Combine(parent, "outside.js"), "secret",
        TestContext.Current.CancellationToken);
      var assets = new PackagedWebAssets(bundle);

      await using WorkbenchResourceResponse response = Assert.IsType<WorkbenchResourceResponse>(
        assets.Open(new Uri("https://app.vibeocr/assets/app.js")));
      Assert.Equal("text/javascript; charset=utf-8", response.ContentType);
      using StreamReader reader = new(response.Content);
      Assert.Equal("export const ready = true;", await reader.ReadToEndAsync(
        TestContext.Current.CancellationToken));
      Assert.Null(assets.Open(new Uri("https://app.vibeocr/assets/../outside.js")));
      Assert.Null(assets.Open(new Uri("https://app.vibeocr/assets/app.js?bypass=1")));
      Assert.Null(assets.Open(new Uri("https://other.vibeocr/assets/app.js")));
      Assert.Null(assets.Open(new Uri("http://app.vibeocr/assets/app.js")));
    }
    finally
    {
      Directory.Delete(parent, recursive: true);
    }
  }

  [Theory]
  [InlineData("https://app.vibeocr/")]
  [InlineData("https://app.vibeocr/index.html")]
  [InlineData("https://app.vibeocr/index.html#/pdf")]
  public void NavigationAllowsOnlyTheWorkbenchDocument(string url) =>
    Assert.True(WebWorkbenchHost.IsNavigationAllowed(new Uri(url)));

  [Theory]
  [InlineData("https://app.vibeocr/other.html")]
  [InlineData("https://app.vibeocr/index.html?debug=1")]
  [InlineData("https://example.test/index.html")]
  [InlineData("http://app.vibeocr/index.html")]
  [InlineData("https://user@app.vibeocr/index.html")]
  public void NavigationRejectsEveryOtherTarget(string url) =>
    Assert.False(WebWorkbenchHost.IsNavigationAllowed(new Uri(url)));

  [Theory]
  [InlineData("https://app.vibeocr/__annotation", "POST", "image/png", true)]
  [InlineData("https://app.vibeocr/__annotation", "POST", "image/jpeg", true)]
  [InlineData("https://app.vibeocr/__annotation", "GET", "image/png", false)]
  [InlineData("https://app.vibeocr/__annotation/", "POST", "image/png", false)]
  [InlineData("https://app.vibeocr/__annotation?debug=1", "POST", "image/png", false)]
  [InlineData("https://example.test/__annotation", "POST", "image/png", false)]
  [InlineData("http://app.vibeocr/__annotation", "POST", "image/png", false)]
  [InlineData("https://app.vibeocr/__annotation", "POST", "text/plain", false)]
  public void AnnotationUploadAllowsOnlyExactSameOriginPngPost(
    string url,
    string method,
    string contentType,
    bool expected) => Assert.Equal(
      expected,
      WebWorkbenchHost.IsAnnotationUploadRequest(
        new Uri(url),
        method,
        contentType));

  [Fact]
  public async Task FixedRouteSettingsIncludeAuthoritativeOutputPresetButNoEnvironmentLogs()
  {
    string root = Path.Combine(Path.GetTempPath(), $"editor-settings-projection-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
      await using var app = new WorkbenchApplication([], WorkbenchRoute.ImageEdit);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      await using var host = new WebWorkbenchHost(app, broker, annotations, applicationOwner: false, fixedRoute: WorkbenchRoute.ImageEdit, pinEditHost: true);
      EditorPreferences preferences = EditorPreferences.Default with { Output = new(Quality: 0.7, NameTemplate: "custom-{name}") };
      var settings = new SettingsWorkbenchState(WorkbenchTheme.Dark, true, "ready", "cpu", false,
        EnvironmentInstallLog: ["private environment log"], EditorPreferences: preferences, ImageOutputDirectory: root);
      string json = WorkbenchBridgeCodec.SerializeState(Guid.NewGuid(), host.ProjectState(new(1, "settings", WorkbenchStateChange.Replace, settings)));
      using JsonDocument document = JsonDocument.Parse(json);
      JsonElement state = document.RootElement.GetProperty("payload").GetProperty("state");
      Assert.Equal(0.7, state.GetProperty("editorPreferences").GetProperty("output").GetProperty("quality").GetDouble());
      Assert.Equal(root, state.GetProperty("imageOutputDirectory").GetString());
      Assert.False(state.TryGetProperty("environmentInstallLog", out _));
    }
    finally { Directory.Delete(root, recursive: true); }
  }

  [Fact]
  public async Task AnnotationDiscardIsScopedToUploadingHostAndReleasesQuotaImmediately()
  {
    string root = Path.Combine(Path.GetTempPath(), $"annotation-discard-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
      await using var app = new WorkbenchApplication([], WorkbenchRoute.ImageEdit);
      using var broker = new WorkbenchResourceBroker(root);
      using var store = new WorkbenchAnnotationStore(root);
      await using var owner = new WebWorkbenchHost(app, broker, store, applicationOwner: false);
      await using var other = new WebWorkbenchHost(app, broker, store, applicationOwner: false);
      for (int index = 0; index < 10; index++)
      {
        WorkbenchAnnotationLease lease = await store.UploadPngAsync(new MemoryStream(VibpProjectFileTests.BuildPng(2, 2)), TestContext.Current.CancellationToken);
        owner.TrackAnnotationLease(lease.ResourceUri);
        Assert.False(other.RevokeUploadedAnnotation(lease.ResourceUri));
        Assert.True(owner.RevokeUploadedAnnotation(lease.ResourceUri));
        Assert.False(owner.RevokeUploadedAnnotation(lease.ResourceUri));
        Assert.Throws<WorkbenchAnnotationAccessException>(() => store.Take(lease.ResourceUri));
      }
      WorkbenchAnnotationLease foreign = await store.UploadPngAsync(new MemoryStream(VibpProjectFileTests.BuildPng(2, 2)), TestContext.Current.CancellationToken);
      other.TrackAnnotationLease(foreign.ResourceUri);
      Assert.False(owner.RevokeUploadedAnnotation(foreign.ResourceUri));
      using WorkbenchAnnotationFile retained = store.Take(foreign.ResourceUri);
      Assert.Equal(2, retained.Width);
    }
    finally { Directory.Delete(root, recursive: true); }
  }

  [Fact]
  public void RecoveryAllowsOneAutomaticAttemptPerReadyEpisode()
  {
    var policy = new WorkbenchRecoveryPolicy();

    Assert.Equal(WorkbenchRecoveryAction.Reload, policy.RegisterFailure());
    Assert.Equal(
      WorkbenchRecoveryAction.ShowNativeRecovery,
      policy.RegisterFailure());

    policy.MarkReady();

    Assert.Equal(WorkbenchRecoveryAction.Reload, policy.RegisterFailure());
  }

  [Fact]
  public void FormulaHostInitializationDefersItsOnlyNavigationWhileOtherHostsNavigateImmediately()
  {
    // 公式窗口：初始化不发导航，首次导航由 RenderFormulaAsync 唯一发出，
    // 不存在可与初始 pin 页交错的被抢占导航（官方 Navigation IDs 契约允许
    // Start1→Start2→Complete1 交错，计数式判定不可靠，已改为消除双导航）。
    Assert.Null(WebWorkbenchHost.InitialNavigationUri(
      WorkbenchRoute.ImageEdit, pinEditRoute: true, deferInitialNavigation: true));
    // 普通主窗/贴图编辑/截图场景保持原有立即导航。
    Assert.Equal(WebWorkbenchHost.StartUri.AbsoluteUri,
      WebWorkbenchHost.InitialNavigationUri(fixedRoute: null, pinEditRoute: false, deferInitialNavigation: false));
    Assert.Equal($"{WebWorkbenchHost.StartUri.AbsoluteUri}#/imageEdit?pin=1",
      WebWorkbenchHost.InitialNavigationUri(WorkbenchRoute.ImageEdit, pinEditRoute: true, deferInitialNavigation: false));
    Assert.Equal($"{WebWorkbenchHost.StartUri.AbsoluteUri}#/imageEdit?scene=1",
      WebWorkbenchHost.InitialNavigationUri(WorkbenchRoute.ImageEdit, pinEditRoute: false, deferInitialNavigation: false));
  }

  [Fact]
  public async Task BufferResourceReleasesSourceAfterCopy()
  {
    byte[] expected = Encoding.UTF8.GetBytes("resource-content");
    TrackingStream source = new(expected);
    WorkbenchResourceResponse response = new(
      "text/plain; charset=utf-8",
      expected.Length,
      source);

    using IRandomAccessStream buffered = await WebWorkbenchHost.BufferResourceAsync(
      response,
      TestContext.Current.CancellationToken);

    Assert.True(source.IsDisposed);
    using Stream reader = buffered.AsStreamForRead();
    using MemoryStream copy = new();
    await reader.CopyToAsync(copy, TestContext.Current.CancellationToken);
    Assert.Equal(expected, copy.ToArray());
  }

  [Fact]
  public async Task BufferResourceReleasesSourceWhenCopyFails()
  {
    TrackingStream source = new([1], failReads: true);
    WorkbenchResourceResponse response = new(
      "application/octet-stream",
      1,
      source);

    Exception? error = await Record.ExceptionAsync(async () =>
    {
      using IRandomAccessStream _ = await WebWorkbenchHost.BufferResourceAsync(
        response,
        TestContext.Current.CancellationToken);
    });

    Assert.NotNull(error);
    Assert.True(source.IsDisposed);
  }

  [Fact]
  public async Task BufferBytesRewindsJsonResponse()
  {
    byte[] expected = Encoding.UTF8.GetBytes("{\"resourceUri\":\"opaque\"}");

    using IRandomAccessStream buffered = await WebWorkbenchHost.BufferBytesAsync(
      expected,
      TestContext.Current.CancellationToken);

    using Stream reader = buffered.AsStreamForRead();
    using MemoryStream copy = new();
    await reader.CopyToAsync(copy, TestContext.Current.CancellationToken);
    Assert.Equal(expected, copy.ToArray());
  }

  private sealed class TrackingStream(
    byte[] content,
    bool failReads = false) : MemoryStream(content)
  {
    public bool IsDisposed { get; private set; }

    public override int Read(byte[] buffer, int offset, int count)
    {
      ThrowIfReadFails();
      return base.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer)
    {
      ThrowIfReadFails();
      return base.Read(buffer);
    }

    public override Task<int> ReadAsync(
      byte[] buffer,
      int offset,
      int count,
      CancellationToken cancellationToken)
    {
      if (failReads)
      {
        return Task.FromException<int>(new IOException("read failed"));
      }
      return base.ReadAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask<int> ReadAsync(
      Memory<byte> buffer,
      CancellationToken cancellationToken = default)
    {
      if (failReads)
      {
        return ValueTask.FromException<int>(new IOException("read failed"));
      }
      return base.ReadAsync(buffer, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
      if (disposing)
      {
        IsDisposed = true;
      }
      base.Dispose(disposing);
    }

    private void ThrowIfReadFails()
    {
      if (failReads)
      {
        throw new IOException("read failed");
      }
    }
  }
}

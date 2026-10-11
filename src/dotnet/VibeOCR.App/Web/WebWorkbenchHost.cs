using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System.Text;
using System.Text.Json;
using VibeOCR.App.Workbench;
using Windows.Storage.Streams;

namespace VibeOCR.App.Web;

public enum WorkbenchRecoveryAction
{
  Reload,
  ShowNativeRecovery,
}

public sealed class WorkbenchRecoveryPolicy
{
  private bool automaticRecoveryUsed;

  public WorkbenchRecoveryAction RegisterFailure()
  {
    if (automaticRecoveryUsed)
    {
      return WorkbenchRecoveryAction.ShowNativeRecovery;
    }
    automaticRecoveryUsed = true;
    return WorkbenchRecoveryAction.Reload;
  }

  public void MarkReady() => automaticRecoveryUsed = false;
}

public sealed class WebWorkbenchHost : IAsyncDisposable
{
  public const string VirtualHost = "app.vibeocr";
  public static readonly Uri StartUri = new($"https://{VirtualHost}/index.html");

  private readonly IWorkbenchApplication application;
  private readonly WorkbenchResourceBroker resourceBroker;
  private readonly WorkbenchAnnotationStore annotationStore;
  private readonly WorkbenchRecoveryPolicy recoveryPolicy = new();
  private PackagedWebAssets? packagedAssets;
  private CoreWebView2? core;
  // 生产由 InitializeAsync 与 core 同点绑定、DisposeAsync 同点清空；换绑/清空后旧文档在途消息被抑制。
  private (object Sender, Action<string> Post)? bridge;
  private Windows.Foundation.TypedEventHandler<CoreWebView2, CoreWebView2WebMessageReceivedEventArgs>? webMessageReceived;
  private DispatcherQueue? dispatcher;
  private CancellationTokenSource? subscriptionCancellation;
  private Task? subscription;
  private Guid? sessionId;
  private long bootstrapGeneration;
  // 导航/渲染进程失败替换整个 Web 文档时推进；同文档 bootstrap 换 session 不推进。
  private long documentGeneration;
  private bool disposed;
  private readonly bool applicationOwner;
  private readonly WorkbenchRoute? fixedRoute;
  private static readonly IReadOnlySet<string> SceneScopes = new HashSet<string>(StringComparer.Ordinal)
  { "shell", "recognition", "settings" };
  private readonly WorkbenchDispatcherDelivery stateDelivery = new();
  internal byte[]? CaptureBackground { get; set; }
  internal bool HasBridgeBinding => bridge is not null;
  internal bool HasWebViewBindings => core is not null;
  internal bool HasSubscription => subscription is { IsCompleted: false };
  private readonly CancellationTokenSource lifetime = new();
  private readonly List<Uri> uploadedAnnotations = [];
  private PendingPdfPreviewFlush? pendingPdfPreviewFlush;
  private sealed record PendingPdfPreviewFlush(Guid Id, Guid Session, string DocumentId, TaskCompletionSource<bool> Completion);

  public Task<bool> FlushPdfPreviewAsync(string documentId) => core is { } current && sessionId is { } session && !disposed
    ? RequestPdfPreviewFlushAsync(session, documentId, current.PostWebMessageAsJson, lifetime.Token)
    : Task.FromResult(false);

  // 此接缝只排空已有 PDF 局部编辑状态，不传文件路径、不执行第二套 PDF 命令。
  internal async Task<bool> RequestPdfPreviewFlushAsync(Guid session, string documentId, Action<string> publish, CancellationToken ct)
  {
    if (pendingPdfPreviewFlush is not null) return false;
    var pending = new PendingPdfPreviewFlush(Guid.NewGuid(), session, documentId,
      new(TaskCreationOptions.RunContinuationsAsynchronously));
    pendingPdfPreviewFlush = pending;
    try
    {
      publish(WorkbenchBridgeCodec.SerializePdfPreviewFlush(pending.Id, session, documentId));
      return await pending.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
    }
    catch (Exception error) when (error is TimeoutException or OperationCanceledException) { return false; }
    finally { if (ReferenceEquals(pendingPdfPreviewFlush, pending)) pendingPdfPreviewFlush = null; }
  }

  internal static bool IsPdfPreviewFlushResponse(JsonElement root) => root.ValueKind == JsonValueKind.Object &&
    root.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String &&
    type.GetString() == "pdf.flushPreview";

  internal void AcceptPdfPreviewFlushResponse(string json)
  {
    if (pendingPdfPreviewFlush is not { } pending) return;
    using JsonDocument message = JsonDocument.Parse(json);
    if (message.RootElement.ValueKind != JsonValueKind.Object ||
      !message.RootElement.TryGetProperty("id", out JsonElement identity) || identity.ValueKind != JsonValueKind.String ||
      !Guid.TryParse(identity.GetString(), out Guid id) || id != pending.Id) return;
    var response = WorkbenchBridgeCodec.ParsePdfPreviewFlushResponse(json);
    if (response.Session == pending.Session && response.DocumentId == pending.DocumentId)
      pending.Completion.TrySetResult(response.Ok);
  }
  private void AbandonPdfPreviewFlush() => pendingPdfPreviewFlush?.Completion.TrySetResult(false);

  public WebWorkbenchHost(
    IWorkbenchApplication application,
    WorkbenchResourceBroker resourceBroker,
    WorkbenchAnnotationStore annotationStore,
    bool applicationOwner = true,
    WorkbenchRoute? fixedRoute = null)
  {
    this.application = application ?? throw new ArgumentNullException(nameof(application));
    this.resourceBroker = resourceBroker ?? throw new ArgumentNullException(nameof(resourceBroker));
    this.annotationStore = annotationStore ?? throw new ArgumentNullException(nameof(annotationStore));
    this.applicationOwner = applicationOwner;
    this.fixedRoute = fixedRoute;
  }

  public event Action<string>? StateChanged;

  public event Action<Exception>? ProtocolViolation;

  public event Action? RecoveryRequired;

  public static bool IsNavigationAllowed(Uri uri)
  {
    if (!uri.IsAbsoluteUri ||
        !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(uri.IdnHost, VirtualHost, StringComparison.OrdinalIgnoreCase) ||
        !uri.IsDefaultPort ||
        !string.IsNullOrEmpty(uri.UserInfo) ||
        !string.IsNullOrEmpty(uri.Query))
    {
      return false;
    }
    return uri.AbsolutePath is "/" or "/index.html";
  }

  internal static bool IsAnnotationUploadRequest(
    Uri uri,
    string method,
    string? contentType) =>
    uri.IsAbsoluteUri &&
    string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
    string.Equals(uri.IdnHost, VirtualHost, StringComparison.OrdinalIgnoreCase) &&
    uri.IsDefaultPort &&
    string.IsNullOrEmpty(uri.UserInfo) &&
    string.IsNullOrEmpty(uri.Query) &&
    string.IsNullOrEmpty(uri.Fragment) &&
    string.Equals(uri.AbsolutePath, "/__annotation", StringComparison.Ordinal) &&
    string.Equals(method, "POST", StringComparison.Ordinal) &&
    (string.Equals(contentType?.Trim(), "image/png", StringComparison.OrdinalIgnoreCase) ||
      string.Equals(contentType?.Trim(), "image/jpeg", StringComparison.OrdinalIgnoreCase));

  public async Task InitializeAsync(WebView2 webView, string assetFolder)
  {
    ArgumentNullException.ThrowIfNull(webView);
    ArgumentException.ThrowIfNullOrWhiteSpace(assetFolder);
    ObjectDisposedException.ThrowIf(disposed, this);
    if (core is not null)
    {
      throw new InvalidOperationException("Workbench host is already initialized.");
    }

    string assets = Path.GetFullPath(assetFolder);
    string applicationRoot = Path.GetFullPath(AppContext.BaseDirectory);
    if (!assets.StartsWith(applicationRoot, StringComparison.OrdinalIgnoreCase) ||
        !File.Exists(Path.Combine(assets, "index.html")))
    {
      throw new InvalidOperationException(
        "Workbench assets must be a packaged production bundle.");
    }

    dispatcher = webView.DispatcherQueue;
    await webView.EnsureCoreWebView2Async();
    lifetime.Token.ThrowIfCancellationRequested();
    CoreWebView2 next = webView.CoreWebView2;
    StateChanged?.Invoke("webview-initialized");
    // WebView2 does not raise WebResourceRequested for a mapped virtual host.
    // Serve the packaged bundle and opaque resources through one same-origin route.
    packagedAssets = new PackagedWebAssets(assets);
    ConfigureSettings(next.Settings);
    next.NavigationStarting += OnNavigationStarting;
    next.NavigationCompleted += OnNavigationCompleted;
    next.NewWindowRequested += OnNewWindowRequested;
    next.PermissionRequested += OnPermissionRequested;
    next.DownloadStarting += OnDownloadStarting;
    // WinRT event senders can use a different managed wrapper; bind the registered core.
    webMessageReceived = (_, args) => OnWebMessageReceived(next, args);
    next.WebMessageReceived += webMessageReceived;
    next.ProcessFailed += OnProcessFailed;
    next.AddWebResourceRequestedFilter(
      $"https://{VirtualHost}/*",
      CoreWebView2WebResourceContext.All);
    next.WebResourceRequested += OnWebResourceRequested;
    core = next;
    RegisterBridge(next, next.PostWebMessageAsJson);
    next.Navigate(fixedRoute is null ? StartUri.AbsoluteUri : StartUri.AbsoluteUri + "#/imageEdit?scene=1");
  }

  public void Reload()
  {
    CoreWebView2 current = core ??
      throw new InvalidOperationException("Workbench host is not initialized.");
    current.Reload();
  }

  private static void ConfigureSettings(CoreWebView2Settings settings)
  {
    settings.AreBrowserAcceleratorKeysEnabled = false;
    settings.AreDefaultContextMenusEnabled = false;
    settings.AreDevToolsEnabled = false;
    settings.IsBuiltInErrorPageEnabled = false;
    settings.IsGeneralAutofillEnabled = false;
    settings.IsPasswordAutosaveEnabled = false;
    settings.IsStatusBarEnabled = false;
    settings.IsWebMessageEnabled = true;
  }

  private void OnNavigationStarting(
    CoreWebView2 sender,
    CoreWebView2NavigationStartingEventArgs args)
  {
    if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out Uri? uri) ||
        !IsNavigationAllowed(uri))
    {
      args.Cancel = true;
      return;
    }
    ResetWebDocument();
    StateChanged?.Invoke("navigation-starting");
  }

  private void OnNavigationCompleted(
    CoreWebView2 sender,
    CoreWebView2NavigationCompletedEventArgs args)
  {
    if (args.IsSuccess)
    {
      StateChanged?.Invoke("navigation-complete");
      return;
    }
    StateChanged?.Invoke($"navigation-failed:{args.WebErrorStatus}");
    Recover(sender);
  }

  private static void OnNewWindowRequested(
    CoreWebView2 sender,
    CoreWebView2NewWindowRequestedEventArgs args) => args.Handled = true;

  private static void OnPermissionRequested(
    CoreWebView2 sender,
    CoreWebView2PermissionRequestedEventArgs args)
  {
    args.State = CoreWebView2PermissionState.Deny;
    args.Handled = true;
  }

  private static void OnDownloadStarting(
    CoreWebView2 sender,
    CoreWebView2DownloadStartingEventArgs args) => args.Cancel = true;

  private async void OnWebMessageReceived(
    CoreWebView2 sender,
    CoreWebView2WebMessageReceivedEventArgs args)
  {
    if (disposed || bridge is not { } attached || !ReferenceEquals(attached.Sender, sender)) return;
    string message;
    // WinRT getter 必须在早守卫之后、try 内读取，异常按原有路径上报而不从 async void 漏出。
    try { message = args.WebMessageAsJson; }
    catch (Exception error)
    {
      ReportBridgeFailure(sender, Volatile.Read(ref documentGeneration), error);
      return;
    }
    await ReceiveWebMessageAsync(message, sender);
  }

  // 绑定消息泵文档身份与投递动作；InitializeAsync 注册真实 core，测试注册替身。
  internal void RegisterBridge(object sender, Action<string> post) => bridge = (sender, post);

  internal async Task ReceiveWebMessageAsync(string message, object sender)
  {
    if (disposed || bridge is not { } attached || !ReferenceEquals(attached.Sender, sender)) return;
    long document = Volatile.Read(ref documentGeneration);
    long? requestBootstrapGeneration = null;
    try
    {
      if (message.Length > WorkbenchBridgeCodec.MaxMessageBytes) throw new WorkbenchBridgeProtocolException("Bridge message exceeds the size limit.");
      using (JsonDocument incoming = JsonDocument.Parse(message))
      {
        if (IsPdfPreviewFlushResponse(incoming.RootElement))
        { AcceptPdfPreviewFlushResponse(message); return; }
      }
      if (IsBootstrap(message))
      {
        Guid requestId = WorkbenchBridgeCodec.ParseBootstrapRequest(message);
        long generation = Interlocked.Increment(ref bootstrapGeneration);
        requestBootstrapGeneration = generation;
        subscriptionCancellation?.Cancel();
        sessionId = null;
        StateChanged?.Invoke("bootstrap-received");
        WorkbenchBootstrap bootstrap = await application.BootstrapAsync(
          lifetime.Token);
        bootstrap = ProjectBootstrap(bootstrap);
        if (disposed || bridge is not { } registered || !ReferenceEquals(registered.Sender, sender) ||
          generation != Volatile.Read(ref bootstrapGeneration)) return;
        var response = WorkbenchBridgeCodec.SerializeBootstrapResponse(requestId, bootstrap);
        sessionId = bootstrap.SessionId;
        registered.Post(response.Json);
        StartSubscription(bootstrap, response.RequiresSnapshot);
        return;
      }

      Guid activeSession = sessionId ?? throw new WorkbenchBridgeProtocolException(
        "Workbench bridge command arrived before bootstrap.");
      WorkbenchCommandEnvelope envelope = WorkbenchBridgeCodec.ParseCommand(
        message,
        activeSession);
      if (fixedRoute is not null && envelope.Command is NavigateWorkbenchCommand)
        throw new WorkbenchBridgeProtocolException("Scene navigation cannot change the workbench route.");
      WorkbenchCommandReceipt receipt = await application.ExecuteAsync(
        envelope,
        lifetime.Token);
      // 同一文档内 bootstrap 换 session 不丢已接受回执；换文档/换桥/销毁后旧回执抑制。
      if (bridge is { } deliver && OwnsCurrentBridgeDocument(sender, document))
        deliver.Post(WorkbenchBridgeCodec.SerializeReceipt(receipt));
    }
    catch (Exception error)
    {
      if (requestBootstrapGeneration is { } generation &&
        generation != Volatile.Read(ref bootstrapGeneration)) return;
      ReportBridgeFailure(sender, document, error);
    }
  }

  // 迟到的旧文档消息（换文档/换桥/销毁后）不再拥有回执与失败上报权。
  private bool OwnsCurrentBridgeDocument(object sender, long document) =>
    !disposed && document == Volatile.Read(ref documentGeneration) &&
    bridge is { } current && ReferenceEquals(current.Sender, sender);

  // typed 违规走 ProtocolViolation，其余走命令失败恢复；均只对当前文档生效。
  private void ReportBridgeFailure(object sender, long document, Exception error)
  {
    if (error is WorkbenchBridgeProtocolException or OperationCanceledException or JsonException)
    {
      if (OwnsCurrentBridgeDocument(sender, document)) ProtocolViolation?.Invoke(error);
      return;
    }
    if (!OwnsCurrentBridgeDocument(sender, document)) return;
    VibeOCR.App.Services.AppLog.Error("Workbench command failed", error);
    StateChanged?.Invoke($"bridge-command-failed:{error.GetType().Name}");
    RecoveryRequired?.Invoke();
  }

  internal WorkbenchBootstrap ProjectBootstrap(WorkbenchBootstrap bootstrap) => fixedRoute is { } route
    ? bootstrap with { Route = route, States = bootstrap.States.Where(state => SceneScopes.Contains(state.Scope)).Select(ProjectState).ToArray() }
    : bootstrap;

  internal WorkbenchStateEnvelope ProjectState(WorkbenchStateEnvelope state) =>
    fixedRoute is { } route ? state.State switch
    {
      ShellWorkbenchState => state with { State = new ShellWorkbenchState(route) },
      SettingsWorkbenchState settings => state with { State = new ThemeWorkbenchState(settings.Theme) },
      RecognitionWorkbenchState recognition => state with { State = recognition with { Result = null, StructuredResult = null, Engines = null } },
      _ => state,
    } : state;

  internal static bool IsBootstrap(string json)
  {
    if (json.Length > WorkbenchBridgeCodec.MaxMessageBytes)
    {
      return false;
    }
    try
    {
      using System.Text.Json.JsonDocument document =
        System.Text.Json.JsonDocument.Parse(json);
      return document.RootElement.ValueKind == JsonValueKind.Object &&
        document.RootElement.TryGetProperty("type", out var type) &&
        type.ValueKind == JsonValueKind.String && type.GetString() == "app.bootstrap";
    }
    catch (System.Text.Json.JsonException)
    {
      return false;
    }
  }

  private void StartSubscription(WorkbenchBootstrap bootstrap, bool requiresSnapshot = false)
  {
    subscriptionCancellation?.Cancel();
    subscriptionCancellation?.Dispose();
    subscriptionCancellation = new CancellationTokenSource();
    CancellationToken token = subscriptionCancellation.Token;
    subscription = PublishStatesAsync(bootstrap.SessionId, bootstrap.Revision, token, requiresSnapshot ? bootstrap : null);
  }

  private async Task PublishStatesAsync(
    Guid activeSession,
    long afterRevision,
    CancellationToken cancellationToken,
    WorkbenchBootstrap? startup = null)
  {
    try
    {
      CoreWebView2? target = core;
      if (startup is not null)
      {
        WorkbenchStateEnvelope[] frames = [new(startup.Revision, "shell", WorkbenchStateChange.Reset, null),
          .. startup.States.Select(state => state with { Revision = startup.Revision, Change = WorkbenchStateChange.Replace }),
          new(startup.Revision, "shell", WorkbenchStateChange.Ready, null)];
        foreach (WorkbenchStateEnvelope frame in frames)
          if (!await PublishStateFrameAsync(activeSession, frame, target, cancellationToken)) return;
      }
      if (disposed || sessionId != activeSession || !ReferenceEquals(core, target)) return;
      recoveryPolicy.MarkReady();
      StateChanged?.Invoke("bridge-ready");
      WorkbenchTheme? lastTheme = null;
      bool resync = false;
      await foreach (WorkbenchStateEnvelope state in application.SubscribeAsync(
        afterRevision,
        cancellationToken,
        fixedRoute is null ? null : SceneScopes))
      {
        if (state.Change == WorkbenchStateChange.Reset) resync = true;
        if (fixedRoute is not null && state.State is SettingsWorkbenchState settings)
        {
          if (!resync && lastTheme == settings.Theme) continue;
          lastTheme = settings.Theme;
        }
        if (!await PublishStateFrameAsync(activeSession, ProjectState(state), target, cancellationToken)) return;
        if (state.Change == WorkbenchStateChange.Ready) resync = false;
      }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
    }
    catch (Exception error)
    {
      if (!disposed && sessionId == activeSession)
      {
        VibeOCR.App.Services.AppLog.Error("Workbench state delivery failed", error);
        RecoveryRequired?.Invoke();
      }
    }
  }

  private async Task<bool> PublishStateFrameAsync(Guid activeSession, WorkbenchStateEnvelope state, CoreWebView2? target, CancellationToken cancellationToken)
  {
    bool Current() => !disposed && sessionId == activeSession && ReferenceEquals(core, target) && !cancellationToken.IsCancellationRequested;
    if (!Current()) return false;
    string json = WorkbenchBridgeCodec.SerializeState(activeSession, state);
    await stateDelivery.PostAsync(action => dispatcher?.TryEnqueue(action.Invoke) == true, () =>
    {
      if (Current()) target?.PostWebMessageAsJson(json);
    }, cancellationToken);
    return Current();
  }

  private void OnProcessFailed(
    CoreWebView2 sender,
    CoreWebView2ProcessFailedEventArgs args)
  {
    ResetWebDocument();
    StateChanged?.Invoke($"process-failed:{args.ProcessFailedKind}");
    Recover(sender);
  }

  // 导航与渲染进程失败共用：换文档即作废 session/bootstrap 代际与在途 flush。
  internal void ResetWebDocument()
  {
    Interlocked.Increment(ref bootstrapGeneration);
    Interlocked.Increment(ref documentGeneration);
    sessionId = null;
    subscriptionCancellation?.Cancel();
    AbandonPdfPreviewFlush();
  }

  private void Recover(CoreWebView2 sender)
  {
    if (recoveryPolicy.RegisterFailure() == WorkbenchRecoveryAction.Reload)
    {
      sender.Reload();
      return;
    }
    RecoveryRequired?.Invoke();
  }

  private async void OnWebResourceRequested(
    CoreWebView2 sender,
    CoreWebView2WebResourceRequestedEventArgs args)
  {
    if (disposed) return;
    var deferral = args.GetDeferral();
    IRandomAccessStream? buffered = null;
    try
    {
      if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out Uri? uri))
      {
        args.Response = Forbidden(sender);
        return;
      }
      string contentType = string.Equals(
        args.Request.Method,
        "POST",
        StringComparison.Ordinal)
        ? args.Request.Headers.GetHeader("Content-Type")
        : string.Empty;
      if (IsAnnotationUploadRequest(uri, args.Request.Method, contentType))
      {
        using Stream upload = args.Request.Content.AsStreamForRead();
        WorkbenchAnnotationLease lease = await annotationStore.UploadImageAsync(
          upload, contentType.ToLowerInvariant().Trim(),
          lifetime.Token);
        if (disposed)
        {
          annotationStore.Revoke(lease.ResourceUri);
          return;
        }
        uploadedAnnotations.Add(lease.ResourceUri);
        byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
          resourceUri = lease.ResourceUri.AbsoluteUri,
        }));
        buffered = await BufferBytesAsync(payload, CancellationToken.None);
        args.Response = sender.Environment.CreateWebResourceResponse(
          buffered,
          201,
          "Created",
          "Content-Type: application/json; charset=utf-8\r\n" +
          $"Content-Length: {payload.LongLength}\r\n" +
          "Cache-Control: no-store\r\n" +
          "X-Content-Type-Options: nosniff");
        buffered = null;
        return;
      }
      if (!string.Equals(args.Request.Method, "GET", StringComparison.Ordinal))
      {
        args.Response = Forbidden(sender);
        return;
      }
      WorkbenchResourceResponse response = CaptureBackground is { } background &&
        uri.AbsoluteUri == $"https://{VirtualHost}/__capture_background"
        ? new WorkbenchResourceResponse("image/bmp", background.Length, new MemoryStream(background, writable: false))
        : uri.AbsolutePath.StartsWith(
        "/__resource/", StringComparison.Ordinal)
        ? await resourceBroker.OpenAsync(uri)
        : packagedAssets?.Open(uri) ??
          throw new WorkbenchResourceAccessException("Unknown workbench resource.");
      contentType = response.ContentType;
      long contentLength = response.ContentLength;
      buffered = await BufferResourceAsync(response, CancellationToken.None);
      args.Response = sender.Environment.CreateWebResourceResponse(
        buffered,
        200,
        "OK",
        $"Content-Type: {contentType}\r\n" +
        $"Content-Length: {contentLength}\r\n" +
        "Cache-Control: no-store\r\n" +
        "X-Content-Type-Options: nosniff");
      buffered = null;
    }
    catch (Exception)
    {
      buffered?.Dispose();
      args.Response = Forbidden(sender);
    }
    finally
    {
      deferral.Complete();
    }
  }

  internal static async Task<IRandomAccessStream> BufferResourceAsync(
    WorkbenchResourceResponse response,
    CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(response);
    InMemoryRandomAccessStream buffered = new();
    try
    {
      await using WorkbenchResourceResponse ownedResponse = response;
      using IRandomAccessStream source = response.Content.AsRandomAccessStream();
      await RandomAccessStream.CopyAsync(source, buffered).AsTask(cancellationToken);
      buffered.Seek(0);
      return buffered;
    }
    catch
    {
      buffered.Dispose();
      throw;
    }
  }

  internal static async Task<IRandomAccessStream> BufferBytesAsync(
    byte[] content,
    CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(content);
    InMemoryRandomAccessStream buffered = new();
    try
    {
      using DataWriter writer = new(buffered);
      writer.WriteBytes(content);
      await writer.StoreAsync().AsTask(cancellationToken);
      writer.DetachStream();
      buffered.Seek(0);
      return buffered;
    }
    catch
    {
      buffered.Dispose();
      throw;
    }
  }

  private static CoreWebView2WebResourceResponse Forbidden(CoreWebView2 sender) =>
    sender.Environment.CreateWebResourceResponse(
      new InMemoryRandomAccessStream(),
      403,
      "Forbidden",
      "Content-Type: text/plain; charset=utf-8\r\n" +
      "Cache-Control: no-store\r\n" +
      "X-Content-Type-Options: nosniff");

  public async ValueTask DisposeAsync()
  {
    if (disposed)
    {
      return;
    }
    disposed = true;
    AbandonPdfPreviewFlush();
    CaptureBackground = null;
    lifetime.Cancel();
    subscriptionCancellation?.Cancel();
    if (subscription is not null)
    {
      try
      {
        await subscription;
      }
      catch (OperationCanceledException)
      {
      }
    }
    subscriptionCancellation?.Dispose();

    CoreWebView2? current = Interlocked.Exchange(ref core, null);
    bridge = null;
    if (current is not null)
    {
      current.NavigationStarting -= OnNavigationStarting;
      current.NavigationCompleted -= OnNavigationCompleted;
      current.NewWindowRequested -= OnNewWindowRequested;
      current.PermissionRequested -= OnPermissionRequested;
      current.DownloadStarting -= OnDownloadStarting;
      current.WebMessageReceived -= webMessageReceived;
      webMessageReceived = null;
      current.ProcessFailed -= OnProcessFailed;
      current.WebResourceRequested -= OnWebResourceRequested;
    }
    foreach (Uri uri in uploadedAnnotations) annotationStore.Revoke(uri);
    uploadedAnnotations.Clear();
    lifetime.Dispose();
    if (applicationOwner)
    {
      await application.DisposeAsync();
      annotationStore.Dispose();
      resourceBroker.Dispose();
    }
  }
}

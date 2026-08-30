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
  private CoreWebView2? core;
  private DispatcherQueue? dispatcher;
  private CancellationTokenSource? subscriptionCancellation;
  private Task? subscription;
  private Guid? sessionId;
  private bool disposed;

  public WebWorkbenchHost(
    IWorkbenchApplication application,
    WorkbenchResourceBroker resourceBroker,
    WorkbenchAnnotationStore annotationStore)
  {
    this.application = application ?? throw new ArgumentNullException(nameof(application));
    this.resourceBroker = resourceBroker ?? throw new ArgumentNullException(nameof(resourceBroker));
    this.annotationStore = annotationStore ?? throw new ArgumentNullException(nameof(annotationStore));
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
    string.Equals(contentType?.Trim(), "image/png", StringComparison.OrdinalIgnoreCase);

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
    CoreWebView2 next = webView.CoreWebView2;
    next.SetVirtualHostNameToFolderMapping(
      VirtualHost,
      assets,
      CoreWebView2HostResourceAccessKind.DenyCors);
    ConfigureSettings(next.Settings);
    next.NavigationStarting += OnNavigationStarting;
    next.NavigationCompleted += OnNavigationCompleted;
    next.NewWindowRequested += OnNewWindowRequested;
    next.PermissionRequested += OnPermissionRequested;
    next.DownloadStarting += OnDownloadStarting;
    next.WebMessageReceived += OnWebMessageReceived;
    next.ProcessFailed += OnProcessFailed;
    next.AddWebResourceRequestedFilter(
      $"https://{VirtualHost}/__resource/*",
      CoreWebView2WebResourceContext.All);
    next.AddWebResourceRequestedFilter(
      $"https://{VirtualHost}/__annotation",
      CoreWebView2WebResourceContext.All);
    next.WebResourceRequested += OnWebResourceRequested;
    core = next;
    next.Navigate(StartUri.AbsoluteUri);
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

  private static void OnNavigationStarting(
    CoreWebView2 sender,
    CoreWebView2NavigationStartingEventArgs args)
  {
    if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out Uri? uri) ||
        !IsNavigationAllowed(uri))
    {
      args.Cancel = true;
    }
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
    try
    {
      string message = args.WebMessageAsJson;
      if (IsBootstrap(message))
      {
        Guid requestId = WorkbenchBridgeCodec.ParseBootstrapRequest(message);
        WorkbenchBootstrap bootstrap = await application.BootstrapAsync(
          CancellationToken.None);
        sessionId = bootstrap.SessionId;
        sender.PostWebMessageAsJson(
          WorkbenchBridgeCodec.SerializeBootstrap(requestId, bootstrap));
        StartSubscription(bootstrap);
        recoveryPolicy.MarkReady();
        StateChanged?.Invoke("bridge-ready");
        return;
      }

      Guid activeSession = sessionId ?? throw new WorkbenchBridgeProtocolException(
        "Workbench bridge command arrived before bootstrap.");
      WorkbenchCommandEnvelope envelope = WorkbenchBridgeCodec.ParseCommand(
        message,
        activeSession);
      WorkbenchCommandReceipt receipt = await application.ExecuteAsync(
        envelope,
        CancellationToken.None);
      sender.PostWebMessageAsJson(WorkbenchBridgeCodec.SerializeReceipt(receipt));
    }
    catch (Exception error) when (
      error is WorkbenchBridgeProtocolException or OperationCanceledException)
    {
      ProtocolViolation?.Invoke(error);
    }
    catch (Exception error)
    {
      StateChanged?.Invoke($"bridge-command-failed:{error.GetType().Name}");
      RecoveryRequired?.Invoke();
    }
  }

  private static bool IsBootstrap(string json)
  {
    if (json.Length > WorkbenchBridgeCodec.MaxMessageBytes)
    {
      return false;
    }
    try
    {
      using System.Text.Json.JsonDocument document =
        System.Text.Json.JsonDocument.Parse(json);
      return document.RootElement.TryGetProperty("type", out var type) &&
        type.GetString() == "app.bootstrap";
    }
    catch (System.Text.Json.JsonException)
    {
      return false;
    }
  }

  private void StartSubscription(WorkbenchBootstrap bootstrap)
  {
    subscriptionCancellation?.Cancel();
    subscriptionCancellation?.Dispose();
    subscriptionCancellation = new CancellationTokenSource();
    CancellationToken token = subscriptionCancellation.Token;
    subscription = PublishStatesAsync(bootstrap.SessionId, bootstrap.Revision, token);
  }

  private async Task PublishStatesAsync(
    Guid activeSession,
    long afterRevision,
    CancellationToken cancellationToken)
  {
    try
    {
      await foreach (WorkbenchStateEnvelope state in application.SubscribeAsync(
        afterRevision,
        cancellationToken))
      {
        string json = WorkbenchBridgeCodec.SerializeState(activeSession, state);
        dispatcher?.TryEnqueue(() => core?.PostWebMessageAsJson(json));
      }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
    }
  }

  private void OnProcessFailed(
    CoreWebView2 sender,
    CoreWebView2ProcessFailedEventArgs args)
  {
    StateChanged?.Invoke($"process-failed:{args.ProcessFailedKind}");
    sessionId = null;
    Recover(sender);
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
        WorkbenchAnnotationLease lease = await annotationStore.UploadPngAsync(
          upload,
          CancellationToken.None);
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
      WorkbenchResourceResponse response = await resourceBroker.OpenAsync(uri);
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
    if (current is not null)
    {
      current.NavigationStarting -= OnNavigationStarting;
      current.NavigationCompleted -= OnNavigationCompleted;
      current.NewWindowRequested -= OnNewWindowRequested;
      current.PermissionRequested -= OnPermissionRequested;
      current.DownloadStarting -= OnDownloadStarting;
      current.WebMessageReceived -= OnWebMessageReceived;
      current.ProcessFailed -= OnProcessFailed;
      current.WebResourceRequested -= OnWebResourceRequested;
      current.ClearVirtualHostNameToFolderMapping(VirtualHost);
    }
    await application.DisposeAsync();
    annotationStore.Dispose();
    resourceBroker.Dispose();
  }
}

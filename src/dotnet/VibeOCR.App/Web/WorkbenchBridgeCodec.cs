using System.Text;
using System.Text.Json;
using VibeOCR.App.Workbench;
using VibeOCR.App.Features.Recognition;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Windows;

namespace VibeOCR.App.Web;

public sealed class WorkbenchBridgeProtocolException(
  string message,
  Exception? innerException = null) : Exception(message, innerException);

public static class WorkbenchBridgeCodec
{
  public const int MaxMessageBytes = 64 * 1024;
  private static readonly JsonSerializerOptions SerializerOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
  };
  private static readonly HashSet<string> EnvelopeFields =
    ["version", "kind", "id", "type", "payload"];
  private static readonly HashSet<string> EmptyFields = [];
  private static readonly HashSet<string> RecognitionOptionsFields = ["modeId", "options"];
  private static readonly HashSet<string> CommandPayloadFields =
    ["sessionId", "command"];
  private static readonly HashSet<string> CommandFields =
    ["scope", "action", "arguments"];
  private static readonly HashSet<string> RouteArgumentFields = ["route"];
  private static readonly HashSet<string> TextArgumentFields = ["text"];
  private static readonly HashSet<string> ThemeArgumentFields = ["theme"];
  private static readonly HashSet<string> DegreesArgumentFields = ["degrees"];
  private static readonly HashSet<string> FormatArgumentFields = ["format"];
  private static readonly HashSet<string> EnabledArgumentFields = ["enabled"];
  private static readonly HashSet<string> ActionIdArgumentFields = ["actionId"];
  private static readonly HashSet<string> ActionHotkeyArgumentFields =
    ["actionId", "hotkey"];
  private static readonly HashSet<string> ToolbarLayoutArgumentFields =
    ["edge", "autoHide"];
  private static readonly HashSet<string> BatchMoveArgumentFields = ["itemId", "delta"];
  private static readonly HashSet<string> BatchItemArgumentFields = ["itemId"];
  private static readonly HashSet<string> PagesArgumentFields = ["pages"];
  private static readonly HashSet<string> StartArgumentFields = ["start"];
  private static readonly HashSet<string> UrlArgumentFields = ["url"];
  private static readonly HashSet<string> SourceKindOnlyFields = ["kind"];
  private static readonly HashSet<string> SourceArgumentFields = ["kind", "sourceId"];
  private static readonly HashSet<string> AcceleratorArgumentFields = ["accelerator"];
  private static readonly HashSet<string> FeatureArgumentFields = ["featureId", "enabled"];
  private static readonly HashSet<string> EnvironmentNameArgumentFields = ["name"];
  private static readonly HashSet<string> EnvironmentIdArgumentFields = ["environmentId"];
  private static readonly HashSet<string> EnvironmentRecipeArgumentFields = ["environmentId", "recipe", "sourceId"];
  private static readonly HashSet<string> EnvironmentRecipeOnlyArgumentFields = ["environmentId", "recipe"];
  private static readonly HashSet<string> PlanIdArgumentFields = ["planId", "sourceId"];
  private static readonly HashSet<string> PlanIdOnlyArgumentFields = ["planId"];
  private static readonly HashSet<string> EnvironmentSourceDefaultsArgumentFields =
    ["packageSourceId", "modelSourceId"];
  private static readonly HashSet<string> EnvironmentSourcesArgumentFields =
    ["environmentId", "packageSourceId", "modelSourceId"];
  private static readonly HashSet<string> MineruModeArgumentFields = ["mode"];
  private static readonly HashSet<string> MineruRemoteUrlArgumentFields =
    ["mode", "apiUrl"];
  private static readonly HashSet<string> MineruConnectionArgumentFields =
    ["mode", "apiUrl", "apiKey"];
  private static readonly HashSet<string> TaskEngineArgumentFields = ["engine"];
  private static readonly HashSet<string> ResourceUriArgumentFields = ["resourceUri"];
  private static readonly HashSet<string> SessionRevisionArgumentFields =
    ["sessionId", "revision"];
  private static readonly HashSet<string> SessionResourceArgumentFields =
    ["resourceUri", "sessionId", "revision"];
  private static readonly HashSet<string> StructuredCopyArgumentFields =
    ["resourceUri", "blockIndex", "format"];
  private static readonly HashSet<string> SessionSelectionArgumentFields =
    ["sessionId", "revision", "text"];

  public static Guid ParseBootstrapRequest(string json)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(json);
    EnsureMessageSize(json);
    try
    {
      using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
      {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
      });
      JsonElement root = document.RootElement;
      EnsureObjectWithFields(root, EnvelopeFields, "envelope");
      if (root.GetProperty("version").GetInt32() != WorkbenchProtocol.Version ||
          root.GetProperty("kind").GetString() != "request" ||
          root.GetProperty("type").GetString() != "app.bootstrap" ||
          !Guid.TryParse(root.GetProperty("id").GetString(), out Guid id))
      {
        throw new WorkbenchBridgeProtocolException(
          "Workbench bridge bootstrap envelope is invalid.");
      }
      EnsureObjectWithFields(root.GetProperty("payload"), EmptyFields, "bootstrap payload");
      return id;
    }
    catch (WorkbenchBridgeProtocolException)
    {
      throw;
    }
    catch (Exception error) when (
      error is JsonException or InvalidOperationException or KeyNotFoundException)
    {
      throw new WorkbenchBridgeProtocolException(
        "Workbench bridge bootstrap JSON is invalid.",
        error);
    }
  }

  public static string SerializeBootstrap(Guid requestId, WorkbenchBootstrap bootstrap)
  {
    ArgumentNullException.ThrowIfNull(bootstrap);
    Dictionary<string, object?> features = bootstrap.States
      .Where(state => state.Scope != "shell")
      .ToDictionary(
        state => state.Scope,
        state => (object?)state.State,
        StringComparer.Ordinal);
    string json = JsonSerializer.Serialize(new
    {
      version = WorkbenchProtocol.Version,
      kind = "response",
      id = requestId,
      type = "app.bootstrap",
      payload = new
      {
        sessionId = bootstrap.SessionId,
        revision = bootstrap.Revision,
        route = FormatRoute(bootstrap.Route),
        theme = "system",
        capabilities = bootstrap.Capabilities.Order(StringComparer.Ordinal),
        features,
      },
    }, SerializerOptions);
    EnsureMessageSize(json);
    return json;
  }

  public static string SerializeReceipt(WorkbenchCommandReceipt receipt)
  {
    ArgumentNullException.ThrowIfNull(receipt);
    object? problem = receipt.Error is null
      ? null
      : new
      {
        receipt.Error.Code,
        category = receipt.Error.Category.ToString(),
        receipt.Error.Retryable,
        receipt.Error.MessageKey,
      };
    string json = JsonSerializer.Serialize(new
    {
      version = WorkbenchProtocol.Version,
      kind = "response",
      id = receipt.Id,
      type = "app.command",
      payload = new
      {
        revision = receipt.Revision,
        ok = receipt.Ok,
        problem,
      },
    }, SerializerOptions);
    EnsureMessageSize(json);
    return json;
  }

  public static string SerializeState(
    Guid sessionId,
    WorkbenchStateEnvelope state)
  {
    ArgumentNullException.ThrowIfNull(state);
    object? statePayload = SerializeWorkbenchState(state.State);
    string json = JsonSerializer.Serialize(new
    {
      version = WorkbenchProtocol.Version,
      kind = "event",
      id = Guid.NewGuid(),
      type = "app.state",
      payload = new
      {
        sessionId,
        revision = state.Revision,
        scope = state.Scope,
        change = state.Change.ToString().ToLowerInvariant(),
        state = statePayload,
      },
    }, SerializerOptions);
    EnsureMessageSize(json);
    return json;
  }

  public static WorkbenchCommandEnvelope ParseCommand(
    string json,
    Guid expectedSessionId)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(json);
    EnsureMessageSize(json);

    try
    {
      using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
      {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
      });
      JsonElement root = document.RootElement;
      EnsureObjectWithFields(root, EnvelopeFields, "envelope");
      if (root.GetProperty("version").GetInt32() != WorkbenchProtocol.Version ||
          root.GetProperty("kind").GetString() != "request" ||
          root.GetProperty("type").GetString() != "app.command")
      {
        throw new WorkbenchBridgeProtocolException(
          "Workbench bridge command envelope is invalid.");
      }
      if (!Guid.TryParse(root.GetProperty("id").GetString(), out Guid id))
      {
        throw new WorkbenchBridgeProtocolException(
          "Workbench bridge command id is invalid.");
      }

      JsonElement payload = root.GetProperty("payload");
      EnsureObjectWithFields(payload, CommandPayloadFields, "command payload");
      if (!Guid.TryParse(payload.GetProperty("sessionId").GetString(), out Guid sessionId) ||
          sessionId != expectedSessionId)
      {
        throw new WorkbenchBridgeProtocolException(
          "Workbench bridge command session is invalid.");
      }

      JsonElement command = payload.GetProperty("command");
      EnsureObjectWithFields(command, CommandFields, "command");
      string? scope = command.GetProperty("scope").GetString();
      string? action = command.GetProperty("action").GetString();
      JsonElement arguments = command.GetProperty("arguments");
      WorkbenchCommand typedCommand = ParseTypedCommand(scope, action, arguments);
      return new WorkbenchCommandEnvelope(id, typedCommand);
    }
    catch (WorkbenchBridgeProtocolException)
    {
      throw;
    }
    catch (Exception error) when (
      error is JsonException or InvalidOperationException or KeyNotFoundException)
    {
      throw new WorkbenchBridgeProtocolException(
        "Workbench bridge command JSON is invalid.",
        error);
    }
  }

  private static WorkbenchRoute ParseRoute(string? route) => route switch
  {
    "recognition" => WorkbenchRoute.Recognition,
    "batch" => WorkbenchRoute.Batch,
    "qrcode" => WorkbenchRoute.QrCode,
    "pdf" => WorkbenchRoute.Pdf,
    "settings" => WorkbenchRoute.Settings,
    "about" => WorkbenchRoute.About,
    "diagnostics" => WorkbenchRoute.Diagnostics,
    _ => throw new WorkbenchBridgeProtocolException(
      "Workbench bridge route is invalid."),
  };

  private static WorkbenchCommand ParseTypedCommand(
    string? scope,
    string? action,
    JsonElement arguments)
  {
    switch (scope, action)
    {
      case ("shell", "navigate"):
        EnsureObjectWithFields(arguments, RouteArgumentFields, "command arguments");
        return new NavigateWorkbenchCommand(
          ParseRoute(arguments.GetProperty("route").GetString()));
      case ("recognition", "selectImage"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new SelectRecognitionImageCommand();
      case ("recognition", "readClipboard"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new ReadRecognitionClipboardCommand();
      case ("recognition", "captureScreen"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new CaptureRecognitionScreenCommand();
      case ("recognition", "captureScreenshotSession"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new CaptureScreenshotSessionCommand();
      case ("recognition", "captureScreenshotTextSession"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new CaptureScreenshotTextSessionCommand();
      case ("recognition", "captureScrollingScreenshot"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new CaptureScrollingScreenshotCommand();
      case ("recognition", "closeScreenshotSession"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new CloseScreenshotSessionCommand();
      case ("recognition", "notifyScreenshotRevision"):
        EnsureObjectWithFields(
          arguments, SessionRevisionArgumentFields, "command arguments");
        return new NotifyScreenshotSessionRevisionCommand(
          ParseGuidArgument(arguments, "sessionId"),
          ParseContentRevision(arguments));
      case ("recognition", "copyScreenshotImage"):
        EnsureObjectWithFields(
          arguments, SessionResourceArgumentFields, "command arguments");
        return new CopyScreenshotImageCommand(
          ReadAnnotationResourceUri(arguments),
          ParseGuidArgument(arguments, "sessionId"),
          ParseContentRevision(arguments));
      case ("recognition", "saveScreenshotImage"):
        EnsureObjectWithFields(
          arguments, SessionResourceArgumentFields, "command arguments");
        return new SaveScreenshotImageCommand(
          ReadAnnotationResourceUri(arguments),
          ParseGuidArgument(arguments, "sessionId"),
          ParseContentRevision(arguments));
      case ("recognition", "pinScreenshotImage"):
        EnsureObjectWithFields(
          arguments, SessionResourceArgumentFields, "command arguments");
        return new PinScreenshotImageCommand(
          ReadAnnotationResourceUri(arguments),
          ParseGuidArgument(arguments, "sessionId"),
          ParseContentRevision(arguments));
      case ("recognition", "recognizeScreenshotImage"):
        EnsureObjectWithFields(
          arguments, SessionResourceArgumentFields, "command arguments");
        return new RecognizeScreenshotImageCommand(
          ReadAnnotationResourceUri(arguments),
          ParseGuidArgument(arguments, "sessionId"),
          ParseContentRevision(arguments));
      case ("recognition", "prepareScreenshotTextLayer"):
        EnsureObjectWithFields(
          arguments, SessionResourceArgumentFields, "command arguments");
        return new PrepareScreenshotTextLayerCommand(
          ReadAnnotationResourceUri(arguments),
          ParseGuidArgument(arguments, "sessionId"),
          ParseContentRevision(arguments));
      case ("recognition", "cancelScreenshotTextLayer"):
        EnsureObjectWithFields(
          arguments, SessionRevisionArgumentFields, "command arguments");
        return new CancelScreenshotTextLayerCommand(
          ParseGuidArgument(arguments, "sessionId"),
          ParseContentRevision(arguments));
      case ("recognition", "copyScreenshotSelection"):
        return new CopyScreenshotSelectionCommand(
          ParseGuidArgument(arguments, "sessionId"),
          ParseContentRevision(arguments),
          ParseSelectionText(arguments));
      case ("recognition", "cancel"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new CancelRecognitionCommand();
      case ("recognition", "copy"):
        return new CopyRecognitionResultCommand(ParseFormat(
          arguments,
          ["plain", "markdown", "rich"]));
      case ("recognition", "copyStructured"):
        return ParseStructuredCopy(arguments);
      case ("recognition", "export"):
        return new ExportRecognitionResultCommand(ParseFormat(
          arguments,
          ["text", "markdown", "html", "docx", "xlsx"]));
      case ("recognition", "copyAnnotatedImage"):
        return new CopyAnnotatedImageCommand(ParseAnnotationResourceUri(arguments));
      case ("recognition", "saveAnnotatedImage"):
        return new SaveAnnotatedImageCommand(ParseAnnotationResourceUri(arguments));
      case ("batch", "addFiles"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new AddBatchFilesCommand();
      case ("batch", "exportAll"):
        return new ExportBatchCommand(ParseFormat(
          arguments,
          ["text", "markdown", "html", "docx", "xlsx"]));
      case ("batch", "start"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new StartBatchCommand();
      case ("batch", "cancel"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new CancelBatchCommand();
      case ("batch", "clear"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new ClearBatchCommand();
      case ("batch", "moveItem"):
        EnsureObjectWithFields(arguments, BatchMoveArgumentFields, "command arguments");
        int delta = arguments.GetProperty("delta").GetInt32();
        if (delta is not (-1 or 1))
        {
          throw new WorkbenchBridgeProtocolException(
            "Workbench batch move delta is invalid.");
        }
        return new MoveBatchItemCommand(
          ParseGuidArgument(arguments, "itemId"),
          delta);
      case ("batch", "removeItem"):
        EnsureObjectWithFields(arguments, BatchItemArgumentFields, "command arguments");
        return new RemoveBatchItemCommand(ParseGuidArgument(arguments, "itemId"));
      case ("batch", "setWindow"):
        return new SetBatchWindowCommand(ParseWindowStart(arguments));
      case ("batch", "setTaskEngine"):
        return new SetBatchTaskEngineCommand(ParseTaskEngine(arguments));
      case ("pdf", "setTaskEngine"):
        return new SetPdfTaskEngineCommand(ParseTaskEngine(arguments));
      case ("pdf", "open"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new OpenPdfCommand();
      case ("pdf", "rotate"):
        return ParseRotate(arguments);
      case ("pdf", "close"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new ClosePdfCommand();
      case ("pdf", "deletePages"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new DeletePdfPagesCommand();
      case ("pdf", "ocrPages"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new OcrPdfPagesCommand();
      case ("pdf", "save"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new SavePdfCommand();
      case ("pdf", "selectPages"):
        EnsureObjectWithFields(arguments, PagesArgumentFields, "command arguments");
        return new SelectPdfPagesCommand(ParsePageIndexes(
          arguments.GetProperty("pages")));
      case ("pdf", "setWindow"):
        return new SetPdfWindowCommand(ParseWindowStart(arguments));
      case ("qrcode", "generate"):
        EnsureObjectWithFields(arguments, TextArgumentFields, "command arguments");
        string? text = arguments.GetProperty("text").GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
          throw new WorkbenchBridgeProtocolException(
            "Workbench QR code text is invalid.");
        }
        return new GenerateQrCodeCommand(text);
      case ("qrcode", "decode"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new DecodeQrCodeCommand();
      case ("qrcode", "decodeClipboard"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new DecodeQrCodeClipboardCommand();
      case ("qrcode", "cancel"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new CancelQrCodeCommand();
      case ("qrcode", "clear"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new ClearQrCodeCommand();
      case ("qrcode", "save"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new SaveQrCodeCommand();
      case ("qrcode", "openUrl"):
        EnsureObjectWithFields(arguments, UrlArgumentFields, "command arguments");
        string? url = arguments.GetProperty("url").GetString();
        if (!IsAllowedWebUri(url))
        {
          throw new WorkbenchBridgeProtocolException(
            "Workbench QR code URL is invalid.");
        }
        return new OpenQrCodeUrlCommand(url!);
      case ("about", "openProject"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new OpenProjectPageCommand();
      case ("settings", "refreshRuntime"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new RefreshRuntimeCommand();
      case ("settings", "createEnvironment"):
        EnsureObjectWithFields(arguments, EnvironmentNameArgumentFields, "command arguments");
        string? environmentName = arguments.GetProperty("name").GetString();
        if (string.IsNullOrWhiteSpace(environmentName) || environmentName.Length > 80)
          throw new WorkbenchBridgeProtocolException("运行环境名称无效。");
        return new CreateEnvironmentCommand(environmentName);
      case ("settings", "previewEnvironmentInstall"):
      {
        bool hasPreviewSource = !HasExactFields(arguments, EnvironmentRecipeOnlyArgumentFields);
        EnsureObjectWithFields(
          arguments,
          hasPreviewSource ? EnvironmentRecipeArgumentFields : EnvironmentRecipeOnlyArgumentFields,
          "command arguments");
        string recipeEnvironmentId = ParseEnvironmentId(arguments);
        string? recipe = arguments.GetProperty("recipe").GetString();
        if (recipe is not ("rapidocr-cpu" or "paddleocr-cpu" or "paddleocr-cuda" or
            "mineru-cpu" or "rapidocr+mineru-cpu" or "rapidocr+mineru-cuda"))
          throw new WorkbenchBridgeProtocolException("运行环境配方无效。");
        // sourceId 省略 → 本次显式“跟随环境/全局配置”；目录 id 由管理器校验，
        // 桥只校验形状，不硬编码目录成员。
        string? previewSourceId = hasPreviewSource
          ? ParseSourceId(arguments.GetProperty("sourceId"))
          : null;
        return new PreviewEnvironmentInstallCommand(recipeEnvironmentId, recipe, previewSourceId);
      }
      case ("settings", "confirmEnvironmentInstall"):
      {
        bool hasConfirmSource = !HasExactFields(arguments, PlanIdOnlyArgumentFields);
        EnsureObjectWithFields(
          arguments,
          hasConfirmSource ? PlanIdArgumentFields : PlanIdOnlyArgumentFields,
          "command arguments");
        string? environmentPlanId = arguments.GetProperty("planId").GetString();
        if (string.IsNullOrWhiteSpace(environmentPlanId) || environmentPlanId.Length > 64)
          throw new WorkbenchBridgeProtocolException("环境安装计划无效。");
        string? confirmedSourceId = hasConfirmSource
          ? ParseSourceId(arguments.GetProperty("sourceId"))
          : null;
        return new ConfirmEnvironmentInstallCommand(environmentPlanId, confirmedSourceId);
      }
      case ("settings", "setEnvironmentSources"):
      {
        bool scoped = !HasExactFields(arguments, EnvironmentSourceDefaultsArgumentFields);
        EnsureObjectWithFields(
          arguments,
          scoped ? EnvironmentSourcesArgumentFields : EnvironmentSourceDefaultsArgumentFields,
          "command arguments");
        string? sourceEnvironmentId = scoped ? ParseEnvironmentId(arguments) : null;
        return new SetEnvironmentSourcesCommand(
          sourceEnvironmentId,
          ParseSourceId(arguments.GetProperty("packageSourceId")),
          ParseSourceId(arguments.GetProperty("modelSourceId")));
      }
      case ("settings", "cancelEnvironmentInstall"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new CancelEnvironmentInstallCommand();
      case ("settings", "invalidateEnvironmentPlan"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new InvalidateEnvironmentPlanCommand();
      case ("settings", "switchEnvironment"):
        EnsureObjectWithFields(arguments, EnvironmentIdArgumentFields, "command arguments");
        return new SwitchEnvironmentCommand(ParseEnvironmentId(arguments));
      case ("settings", "deleteEnvironment"):
        EnsureObjectWithFields(arguments, EnvironmentIdArgumentFields, "command arguments");
        return new DeleteEnvironmentCommand(ParseEnvironmentId(arguments));
      case ("settings", "repairEmptyEnvironment"):
        EnsureObjectWithFields(arguments, EnvironmentIdArgumentFields, "command arguments");
        return new RepairEmptyEnvironmentCommand(ParseEnvironmentId(arguments));
      case ("settings", "setTheme"):
        EnsureObjectWithFields(arguments, ThemeArgumentFields, "command arguments");
        return new SetThemeCommand(ParseTheme(
          arguments.GetProperty("theme").GetString()));
      case ("settings", "setStartup"):
        EnsureObjectWithFields(arguments, EnabledArgumentFields, "command arguments");
        return new SetStartupCommand(arguments.GetProperty("enabled").GetBoolean());
      case ("settings", "setActionHotkey"):
        bool hasHotkey = !HasExactFields(arguments, ActionIdArgumentFields);
        if (hasHotkey)
        {
          EnsureObjectWithFields(arguments, ActionHotkeyArgumentFields, "command arguments");
        }
        else
        {
          EnsureObjectWithFields(arguments, ActionIdArgumentFields, "command arguments");
        }
        string? actionHotkey = hasHotkey
          ? arguments.GetProperty("hotkey").GetString()
          : null;
        if (actionHotkey is not null &&
          (actionHotkey.Length == 0 || actionHotkey.Length > 64))
        {
          throw new WorkbenchBridgeProtocolException(
            "Workbench action hotkey is invalid.");
        }
        return new SetActionHotkeyCommand(
          ParseActionId(arguments),
          actionHotkey);
      case ("settings", "resetActionHotkey"):
        EnsureObjectWithFields(arguments, ActionIdArgumentFields, "command arguments");
        return new ResetActionHotkeyCommand(ParseActionId(arguments));
      case ("settings", "setFloatingToolbarEnabled"):
        EnsureObjectWithFields(arguments, EnabledArgumentFields, "command arguments");
        return new SetFloatingToolbarEnabledCommand(
          arguments.GetProperty("enabled").GetBoolean());
      case ("settings", "setFloatingToolbarLayout"):
        EnsureObjectWithFields(arguments, ToolbarLayoutArgumentFields, "command arguments");
        return new SetFloatingToolbarLayoutCommand(
          ParseToolbarEdge(arguments.GetProperty("edge").GetString()),
          arguments.GetProperty("autoHide").GetBoolean());
      case ("settings", "showFloatingToolbar"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new ShowFloatingToolbarCommand();
      case ("settings", "hideFloatingToolbar"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new HideFloatingToolbarCommand();
      case ("settings", "setSource"):
        bool hasSourceId = !HasExactFields(arguments, SourceKindOnlyFields);
        if (hasSourceId)
        {
          EnsureObjectWithFields(arguments, SourceArgumentFields, "command arguments");
        }
        else
        {
          EnsureObjectWithFields(arguments, SourceKindOnlyFields, "command arguments");
        }
        string kind = arguments.GetProperty("kind").GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(kind) || kind.Length > 64)
        {
          throw new WorkbenchBridgeProtocolException("Workbench source kind is invalid.");
        }
        string? sourceId = hasSourceId
          ? arguments.GetProperty("sourceId").GetString()
          : null;
        if (sourceId is not null && (sourceId.Length == 0 || sourceId.Length > 64))
        {
          throw new WorkbenchBridgeProtocolException("Workbench source id is invalid.");
        }
        return new SetDownloadSourceCommand(kind, sourceId);
      case ("settings", "setAccelerator"):
        EnsureObjectWithFields(arguments, AcceleratorArgumentFields, "command arguments");
        string? accelerator = arguments.GetProperty("accelerator").GetString();
        if (accelerator is not ("cpu" or "nvidia_cuda"))
        {
          throw new WorkbenchBridgeProtocolException(
            "Workbench accelerator is invalid.");
        }
        return new SetAcceleratorCommand(accelerator);
      case ("settings", "setFeature"):
        EnsureObjectWithFields(arguments, FeatureArgumentFields, "command arguments");
        string? featureId = arguments.GetProperty("featureId").GetString();
        if (string.IsNullOrWhiteSpace(featureId) || featureId.Length > 64)
        {
          throw new WorkbenchBridgeProtocolException("Workbench feature is invalid.");
        }
        return new SetRuntimeFeatureCommand(
          featureId,
          arguments.GetProperty("enabled").GetBoolean());
      case ("settings", "setMineruConnection"):
        return ParseMineruConnection(arguments);
      case ("settings", "prepareMineruConnection"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new PrepareMineruConnectionCommand();
      case ("settings", "installRuntime"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new InstallRuntimeCommand();
      case ("settings", "confirmRuntimeInstall"):
        EnsureObjectWithFields(arguments, new HashSet<string> { "planId" }, "command arguments");
        string? planId = arguments.GetProperty("planId").GetString();
        if (string.IsNullOrWhiteSpace(planId) || planId.Length > 256)
          throw new WorkbenchBridgeProtocolException("安装计划标识无效。");
        return new ConfirmRuntimeInstallCommand(planId);
      case ("settings", "cancelRuntimeMaintenance"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new CancelRuntimeMaintenanceCommand();
      case ("settings", "retryRuntimeMaintenance"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new RetryRuntimeMaintenanceCommand();
      case ("recognition", "setOptions"):
        EnsureObjectWithFields(arguments, RecognitionOptionsFields, "command arguments");
        string? modeId = arguments.GetProperty("modeId").GetString();
        if (modeId is not ("paddle_text" or "paddle_table" or "paddle_formula" or "paddle_structure" or "paddle_document_vl"))
          throw new WorkbenchBridgeProtocolException("Unsupported Paddle recognition mode.");
        PaddleModeOptions options = arguments.GetProperty("options")
          .Deserialize<PaddleModeOptions>(PaddleModeOptions.JsonOptions)
          ?? throw new WorkbenchBridgeProtocolException("Recognition options must be an object.");
        return new SetRecognitionOptionsCommand(modeId, options);
      case ("recognition", "setTaskEngine"):
        return new SetTaskEngineCommand(ParseTaskEngine(arguments));
      case ("update", "check"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new CheckUpdateCommand();
      case ("update", "download"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new DownloadUpdateCommand();
      case ("update", "cancel"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new CancelUpdateCommand();
      case ("update", "cancelRuntimeMaintenance"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new CancelRuntimeForUpdateCommand();
      case ("diagnostics", "export"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new ExportDiagnosticsCommand();
      default:
        throw new WorkbenchBridgeProtocolException(
          "Workbench bridge command type is not supported.");
    }
  }

  private static CopyStructuredResultCommand ParseStructuredCopy(
    JsonElement arguments)
  {
    EnsureObjectWithFields(arguments, StructuredCopyArgumentFields, "command arguments");
    string? value = arguments.GetProperty("resourceUri").GetString();
    if (value is null || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
      !WorkbenchResourceBroker.IsResourceUri(uri))
    {
      throw new WorkbenchBridgeProtocolException(
        "Workbench structured result URI is invalid.");
    }
    int blockIndex = arguments.GetProperty("blockIndex").GetInt32();
    if (blockIndex is < 0 or > 4095)
    {
      throw new WorkbenchBridgeProtocolException(
        "Workbench structured block index is invalid.");
    }
    string? format = arguments.GetProperty("format").GetString();
    if (format is not ("table" or "latex"))
    {
      throw new WorkbenchBridgeProtocolException(
        "Workbench structured copy format is invalid.");
    }
    return new CopyStructuredResultCommand(uri.AbsoluteUri, blockIndex, format);
  }

  private static string ParseAnnotationResourceUri(JsonElement arguments)
  {
    EnsureObjectWithFields(arguments, ResourceUriArgumentFields, "command arguments");
    return ReadAnnotationResourceUri(arguments);
  }

  private static string ReadAnnotationResourceUri(JsonElement arguments)
  {
    string? value = arguments.GetProperty("resourceUri").GetString();
    if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
        !WorkbenchAnnotationStore.IsResourceUri(uri))
    {
      throw new WorkbenchBridgeProtocolException(
        "Workbench annotated image URI is invalid.");
    }
    return uri.AbsoluteUri;
  }

  private static long ParseContentRevision(JsonElement arguments)
  {
    long revision = arguments.GetProperty("revision").GetInt64();
    if (revision is < 0 or > 1_000_000_000)
    {
      throw new WorkbenchBridgeProtocolException(
        "Workbench screenshot session revision is invalid.");
    }
    return revision;
  }

  private static string ParseSelectionText(JsonElement arguments)
  {
    EnsureObjectWithFields(arguments, SessionSelectionArgumentFields, "command arguments");
    string? text = arguments.GetProperty("text").GetString();
    if (string.IsNullOrWhiteSpace(text) || text.Length > 16_384)
    {
      throw new WorkbenchBridgeProtocolException(
        "Workbench screenshot selection text is invalid.");
    }
    return text;
  }

  private static RotatePdfCommand ParseRotate(JsonElement arguments)
  {
    HashSet<string> fields = arguments.ValueKind == JsonValueKind.Object
      ? arguments.EnumerateObject().Select(property => property.Name).ToHashSet(
        StringComparer.Ordinal)
      : [];
    if (fields.Count == 0)
    {
      EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
      return new RotatePdfCommand();
    }
    EnsureObjectWithFields(arguments, DegreesArgumentFields, "command arguments");
    int degrees = arguments.GetProperty("degrees").GetInt32();
    if (degrees is not (90 or -90))
    {
      throw new WorkbenchBridgeProtocolException(
        "Workbench PDF rotation is invalid.");
    }
    return new RotatePdfCommand(degrees);
  }

  private static SetMineruConnectionCommand ParseMineruConnection(
    JsonElement arguments)
  {
    // local 仅携带 mode；remote 携带完整 url/key，多余或缺失字段 fail closed。
    if (arguments.ValueKind != JsonValueKind.Object ||
      !arguments.TryGetProperty("mode", out JsonElement modeElement) ||
      modeElement.ValueKind != JsonValueKind.String)
    {
      throw new WorkbenchBridgeProtocolException(
        "Workbench MinerU mode is invalid.");
    }
    string mode = modeElement.GetString()!;
    if (mode is not ("local" or "remote"))
    {
      throw new WorkbenchBridgeProtocolException(
        "Workbench MinerU mode is invalid.");
    }
    if (mode == "local")
    {
      EnsureObjectWithFields(arguments, MineruModeArgumentFields, "command arguments");
      return new SetMineruConnectionCommand(mode, null, null);
    }
    bool hasApiKeyField = !HasExactFields(arguments, MineruRemoteUrlArgumentFields);
    if (hasApiKeyField)
    {
      EnsureObjectWithFields(
        arguments, MineruConnectionArgumentFields, "command arguments");
    }
    else
    {
      EnsureObjectWithFields(
        arguments, MineruRemoteUrlArgumentFields, "command arguments");
    }
    string? apiUrl = arguments.GetProperty("apiUrl").GetString();
    if (string.IsNullOrEmpty(apiUrl) ||
      apiUrl.Length > MineruConnectionSettings.MaxApiUrlLength)
    {
      throw new WorkbenchBridgeProtocolException(
        "Workbench MinerU service url is invalid.");
    }
    if (!arguments.TryGetProperty("apiKey", out JsonElement apiKeyElement))
    {
      // 省略 apiKey=保留 Backend 已存 Key；宿主读回当前设置后合并。
      return new SetMineruConnectionCommand(mode, apiUrl, null);
    }
    if (apiKeyElement.ValueKind != JsonValueKind.String)
    {
      throw new WorkbenchBridgeProtocolException(
        "Workbench MinerU api key is invalid.");
    }
    string apiKey = apiKeyElement.GetString()!;
    // 空字符串=显式清除；长度无任意上限，由桥接消息总限额约束。
    if (apiKey.Contains('\r') || apiKey.Contains('\n'))
    {
      throw new WorkbenchBridgeProtocolException(
        "Workbench MinerU api key is invalid.");
    }
    return new SetMineruConnectionCommand(mode, apiUrl, apiKey);
  }

  private static string ParseEnvironmentId(JsonElement arguments)
  {
    string? value = arguments.GetProperty("environmentId").GetString();
    if (value == "legacy" || (value is not null && Guid.TryParseExact(value, "N", out _)))
      return value;
    throw new WorkbenchBridgeProtocolException("运行环境标识无效。");
  }

  /// <summary>来源 id：null（清除/跟随）或形状合法的目录 id；目录成员由管理器校验。</summary>
  private static string? ParseSourceId(JsonElement element) =>
    element.ValueKind switch
    {
      JsonValueKind.Null => null,
      JsonValueKind.String when element.GetString() is { } id &&
        id.Length <= 40 && id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-') => id,
      _ => throw new WorkbenchBridgeProtocolException("下载来源标识无效。"),
    };

  private static string ParseActionId(JsonElement arguments)
  {
    string? actionId = arguments.GetProperty("actionId").GetString();
    if (string.IsNullOrWhiteSpace(actionId) || actionId.Length > 64)
    {
      throw new WorkbenchBridgeProtocolException(
        "Workbench action id is invalid.");
    }
    return actionId;
  }

  private static ScreenEdge ParseToolbarEdge(string? edge) => edge switch
  {
    "top" => ScreenEdge.Top,
    "bottom" => ScreenEdge.Bottom,
    "left" => ScreenEdge.Left,
    "right" => ScreenEdge.Right,
    _ => throw new WorkbenchBridgeProtocolException(
      "Workbench floating toolbar edge is invalid."),
  };

  private static string? ParseTaskEngine(JsonElement arguments)
  {
    bool hasEngine = !HasExactFields(arguments, EmptyFields);
    EnsureObjectWithFields(arguments,
      hasEngine ? TaskEngineArgumentFields : EmptyFields, "command arguments");
    string? engine = hasEngine ? arguments.GetProperty("engine").GetString() : null;
    if (hasEngine && (string.IsNullOrEmpty(engine) || engine.Length > 32))
    {
      throw new WorkbenchBridgeProtocolException("Workbench task engine is invalid.");
    }
    return engine;
  }

  private static int ParseWindowStart(JsonElement arguments)
  {
    EnsureObjectWithFields(arguments, StartArgumentFields, "command arguments");
    int start = arguments.GetProperty("start").GetInt32();
    if (start is < 0 or > 1_000_000)
    {
      throw new WorkbenchBridgeProtocolException(
        "Workbench collection window is invalid.");
    }
    return start;
  }

  private static string ParseFormat(
    JsonElement arguments,
    string[] allowed)
  {
    EnsureObjectWithFields(arguments, FormatArgumentFields, "command arguments");
    string? format = arguments.GetProperty("format").GetString();
    if (format is null || !allowed.Contains(format))
    {
      throw new WorkbenchBridgeProtocolException(
        "Workbench result format is invalid.");
    }
    return format;
  }

  private static WorkbenchTheme ParseTheme(string? theme) => theme switch
  {
    "system" => WorkbenchTheme.System,
    "light" => WorkbenchTheme.Light,
    "dark" => WorkbenchTheme.Dark,
    _ => throw new WorkbenchBridgeProtocolException(
      "Workbench theme is invalid."),
  };

  private static string FormatRoute(WorkbenchRoute route) => route switch
  {
    WorkbenchRoute.Recognition => "recognition",
    WorkbenchRoute.Batch => "batch",
    WorkbenchRoute.QrCode => "qrcode",
    WorkbenchRoute.Pdf => "pdf",
    WorkbenchRoute.Settings => "settings",
    WorkbenchRoute.About => "about",
    WorkbenchRoute.Diagnostics => "diagnostics",
    _ => throw new WorkbenchBridgeProtocolException(
      "Workbench bridge route is invalid."),
  };

  private static object? SerializeWorkbenchState(WorkbenchState? state) => state switch
  {
    ShellWorkbenchState shell => new { route = FormatRoute(shell.Route) },
    RecognitionWorkbenchState recognition => new
    {
      recognition.IsBusy,
      recognition.StatusCode,
      recognition.Input,
      recognition.Result,
      engines = recognition.Engines ?? [],
      recognition.TaskEngine,
      screenshotSession = recognition.ScreenshotSession is null ? null : new
      {
        sessionId = recognition.ScreenshotSession.SessionId,
        revision = recognition.ScreenshotSession.Revision,
        textSelectionRequested = recognition.ScreenshotSession.TextSelectionRequested,
      },
      textLayer = recognition.TextLayer is null ? null : new
      {
        status = recognition.TextLayer.Status,
        reason = recognition.TextLayer.Reason,
        binding = recognition.TextLayer.Binding is null ? null : new
        {
          sessionId = recognition.TextLayer.Binding.SessionId,
          revision = recognition.TextLayer.Binding.Revision,
        },
        modeId = recognition.TextLayer.ModeId,
        serviceInstance = recognition.TextLayer.ServiceInstance,
        image = recognition.TextLayer.Image,
        lines = recognition.TextLayer.Lines ?? [],
      },
      structuredResult = recognition.StructuredResult,
    },
    BatchWorkbenchState batch => new
    {
      batch.IsRunning,
      batch.ItemCount,
      batch.CompletedCount,
      batch.FailedCount,
      items = batch.Items ?? [],
      batch.WindowStart,
      engines = batch.Engines ?? [],
      batch.TaskEngine,
      exportIncomplete = batch.ExportIncomplete,
    },
    PdfWorkbenchState pdf => new
    {
      pdf.IsBusy,
      pdf.StatusCode,
      pdf.PageCount,
      pdf.SelectedPage,
      selectedPages = pdf.SelectedPages ?? [],
      pages = pdf.Pages ?? [],
      pdf.WindowStart,
      engines = pdf.Engines ?? [],
      taskEngine = pdf.TaskEngine,
    },
    QrCodeWorkbenchState qrCode => new
    {
      qrCode.IsBusy,
      qrCode.StatusCode,
      qrCode.Results,
      qrCode.GeneratedResource,
      items = qrCode.Items ?? [],
    },
    SettingsWorkbenchState settings => new
    {
      theme = settings.Theme.ToString().ToLowerInvariant(),
      settings.IsBusy,
      settings.StatusCode,
      settings.Backend,
      settings.StartupEnabled,
      sources = settings.Sources ?? [],
      settings.PendingBackend,
      settings.CanSwitchBackend,
      features = settings.Features ?? [],
      hotkeyActions = settings.HotkeyActions is null ? [] : settings.HotkeyActions.Select(action => new
      {
        action.ActionId,
        action.DisplayName,
        action.ConfiguredHotkey,
        action.RegisteredHotkey,
        action.Error,
        action.DefaultHotkey,
      }),
      floatingToolbar = settings.FloatingToolbar is null ? null : new
      {
        settings.FloatingToolbar.Enabled,
        settings.FloatingToolbar.Edge,
        settings.FloatingToolbar.AutoHide,
        settings.FloatingToolbar.Visibility,
        settings.FloatingToolbar.Error,
      },
      settings.StatusMessage,
      settings.ServiceStatus,
      settings.MaintenanceStatus,
      settings.MaintenancePhase,
      settings.ProgressText,
      settings.ProgressDetail,
      settings.ProgressPercent,
      progressActive = settings.ProgressActive,
      settings.CanPreviewInstall,
      environments = settings.Environments ?? [],
      settings.ActiveEnvironmentId,
      environmentPlan = settings.EnvironmentPlan,
      settings.EnvironmentStatus,
      settings.EnvironmentBusy,
      environmentSources = settings.EnvironmentSources ?? [],
      environmentDefaultSourceIds = settings.EnvironmentDefaultSourceIds ?? [],
      environmentUnknownDefaultSourceIds = settings.EnvironmentUnknownDefaultSourceIds ?? [],
      environmentPackageSourceIds = settings.EnvironmentPackageSourceIds ?? [],
      settings.EnvironmentCanCancelInstall,
      mineruConnection = settings.MineruConnection is null ? null : new
      {
        settings.MineruConnection.Supported,
        settings.MineruConnection.Mode,
        apiUrl = settings.MineruConnection.ApiUrl,
        hasApiKey = settings.MineruConnection.HasApiKey,
      },
      installPlan = settings.InstallPlan is not { } plan ? null : new
      {
        plan.PlanId, plan.ExpiresAt,
        accelerator = plan.Accelerator == VibeOCR.Runtime.Contracts.Generated.Host.Accelerator.Cpu ? "CPU" : "NVIDIA CUDA",
        plan.ProfileId, plan.EffectiveComponentIds, plan.EffectiveDownloadSourceIds,
        components = plan.Components.Select(component => new
        { component.ComponentId, component.Action, component.DependencyState, component.ReasonCodes }),
        blockers = plan.Blockers.Select(blocker => new { blocker.Code, blocker.ComponentId, blocker.NextAction }),
        cost = new { plan.Cost.DownloadBytes, plan.Cost.AdditionalDiskBytes, plan.Cost.UnknownReasonCodes },
      },
      maintenance = settings.Maintenance is null ? null : new
      {
        settings.Maintenance.IsRunning,
        settings.Maintenance.StatusCode,
        settings.Maintenance.OperationId,
        requestedComponentIds = settings.Maintenance.RequestedComponentIds,
        effectiveComponentIds = settings.Maintenance.EffectiveComponentIds,
        requestedSourceIds = settings.Maintenance.RequestedSourceIds,
        effectiveSourceIds = settings.Maintenance.EffectiveSourceIds,
        settings.Maintenance.CanCancel,
        settings.Maintenance.CanRetry,
        settings.Maintenance.FailureReason,
        settings.Maintenance.FailureCode,
      },
    },
    UpdateWorkbenchState update => new
    {
      update.IsBusy,
      update.StatusCode,
      update.LatestVersion,
      update.UpdateAvailable,
      update.CanCancelRuntimeMaintenance,
    },
    AboutWorkbenchState about => new
    {
      about.Version,
      about.License,
      about.ProjectUrl,
    },
    DiagnosticsWorkbenchState diagnostics => new
    {
      diagnostics.SupervisorStatus,
      diagnostics.ProtocolStatus,
      diagnostics.IsReady,
      diagnostics.Milestones,
    },
    null => null,
    _ => throw new WorkbenchBridgeProtocolException(
      "Workbench state type is not supported."),
  };

  private static void EnsureMessageSize(string json)
  {
    if (Encoding.UTF8.GetByteCount(json) > MaxMessageBytes)
    {
      throw new WorkbenchBridgeProtocolException(
        "Workbench bridge message exceeds the size limit.");
    }
  }

  private static Guid ParseGuidArgument(JsonElement arguments, string name)
  {
    if (!Guid.TryParse(arguments.GetProperty(name).GetString(), out Guid value))
    {
      throw new WorkbenchBridgeProtocolException(
        $"Workbench {name} is invalid.");
    }
    return value;
  }

  private static IReadOnlyList<int> ParsePageIndexes(JsonElement element)
  {
    if (element.ValueKind != JsonValueKind.Array)
    {
      throw new WorkbenchBridgeProtocolException(
        "Workbench PDF page selection must be an array.");
    }
    int[] pages = element.EnumerateArray().Select(page => page.GetInt32()).ToArray();
    if (pages.Length > 512 || pages.Any(page => page is < 0 or > 4095) ||
        pages.Distinct().Count() != pages.Length)
    {
      throw new WorkbenchBridgeProtocolException(
        "Workbench PDF page selection is invalid.");
    }
    return pages;
  }

  private static bool IsAllowedWebUri(string? value) =>
    value is not null && value.Length <= 2048 &&
    Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
    uri.Scheme is "http" or "https" &&
    string.IsNullOrEmpty(uri.UserInfo);

  private static bool HasExactFields(
    JsonElement element,
    IReadOnlySet<string> expected)
  {
    if (element.ValueKind != JsonValueKind.Object)
    {
      return false;
    }
    return element
      .EnumerateObject()
      .Select(property => property.Name)
      .ToHashSet(StringComparer.Ordinal)
      .SetEquals(expected);
  }

  private static void EnsureObjectWithFields(
    JsonElement element,
    IReadOnlySet<string> expected,
    string label)
  {
    if (element.ValueKind != JsonValueKind.Object)
    {
      throw new WorkbenchBridgeProtocolException(
        $"Workbench bridge {label} must be an object.");
    }
    HashSet<string> actual = element
      .EnumerateObject()
      .Select(property => property.Name)
      .ToHashSet(StringComparer.Ordinal);
    if (!actual.SetEquals(expected))
    {
      throw new WorkbenchBridgeProtocolException(
        $"Workbench bridge {label} fields are invalid.");
    }
  }
}

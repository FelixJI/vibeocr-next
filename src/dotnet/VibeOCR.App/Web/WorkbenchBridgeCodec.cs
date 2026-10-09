using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeOCR.App.Workbench;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Features.Pdf;
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
  private static readonly HashSet<string> QrGenerateFields = ["text", "format", "captionMode", "captionText"];
  private static readonly HashSet<string> ForceArgumentFields = ["force"];
  private static readonly HashSet<string> TextArgumentFields = ["text"];
  private static readonly HashSet<string> ThemeArgumentFields = ["theme"];
  private static readonly HashSet<string> DegreesArgumentFields = ["degrees"];
  private static readonly HashSet<string> FormatArgumentFields = ["format"];
  private static readonly HashSet<string> EnabledArgumentFields = ["enabled"];
  private static readonly HashSet<string> ActionIdArgumentFields = ["actionId"];
  private static readonly HashSet<string> RecordingArgumentFields = ["recordingId"];
  private static readonly HashSet<string> ActionHotkeyArgumentFields =
    ["actionId", "hotkey"];
  private static readonly HashSet<string> ToolbarLayoutArgumentFields =
    ["edge", "autoHide"];
  private static readonly HashSet<string> ToolbarPreferencesArgumentFields =
    ["lingerMs", "theme", "peekPixels"];
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
  private static readonly HashSet<string> RecipeOnlyArgumentFields = ["recipe"];
  private static readonly HashSet<string> PlanIdArgumentFields = ["planId", "sourceId"];
  private static readonly HashSet<string> PlanIdOnlyArgumentFields = ["planId"];
  private static readonly HashSet<string> EnvironmentSourceDefaultsArgumentFields =
    ["packageSourceId", "modelSourceId"];
  private static readonly HashSet<string> EnvironmentSourcesArgumentFields =
    ["environmentId", "packageSourceId", "modelSourceId"];
  private static readonly HashSet<string> IndependentSourceDefaultsArgumentFields =
    ["packageSourceId", "paddleocrModelSourceId", "mineruModelSourceId"];
  private static readonly HashSet<string> IndependentEnvironmentSourcesArgumentFields =
    ["environmentId", "packageSourceId", "paddleocrModelSourceId", "mineruModelSourceId"];
  private static readonly HashSet<string> MineruModeArgumentFields = ["mode"];
  private static readonly HashSet<string> MineruRemoteUrlArgumentFields =
    ["mode", "apiUrl"];
  private static readonly HashSet<string> MineruConnectionArgumentFields =
    ["mode", "apiUrl", "apiKey"];
  private static readonly HashSet<string> MineruRecognitionArgumentFields =
    ["tier", "ocrMode", "pageRange", "language"];
  private static readonly HashSet<string> DefaultModeArgumentFields = ["mode"];
  private const int MaxRecognitionModeIdLength = 64;
  private static readonly HashSet<string> TaskEngineArgumentFields = ["engine"];
  private static readonly HashSet<string> ResourceUriArgumentFields = ["resourceUri"];
  private static readonly HashSet<string> SessionRevisionArgumentFields =
    ["sessionId", "revision"];
  private static readonly HashSet<string> SessionResourceArgumentFields =
    ["resourceUri", "sessionId", "revision"];
  private static readonly HashSet<string> SessionResourceWithExclusionArgumentFields =
    ["resourceUri", "sessionId", "revision", "excludeBoxes"];
  private static readonly HashSet<string> PinResourceArgumentFields =
    ["resourceUri", "sessionId", "revision", "excludeBoxes"];
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
    json = BoundInstallLogToMessage(json);
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
    json = BoundInstallLogToMessage(json);
    EnsureMessageSize(json);
    return json;
  }

  internal static string SerializePdfPreviewFlush(Guid id, Guid session, string documentId) =>
    JsonSerializer.Serialize(new { version = WorkbenchProtocol.Version, kind = "request", id,
      type = "pdf.flushPreview", payload = new { sessionId = session, documentId } }, SerializerOptions);

  internal static (Guid Id, Guid Session, string DocumentId, bool Ok) ParsePdfPreviewFlushResponse(string json)
  {
    EnsureMessageSize(json);
    try
    {
      using JsonDocument document = JsonDocument.Parse(json);
      JsonElement root = document.RootElement;
      EnsureObjectWithFields(root, EnvelopeFields, "preview flush envelope");
      JsonElement payload = root.GetProperty("payload");
      EnsureObjectWithFields(payload, new HashSet<string>(StringComparer.Ordinal) { "sessionId", "documentId", "ok" }, "preview flush payload");
      if (root.GetProperty("version").GetInt32() != WorkbenchProtocol.Version ||
        root.GetProperty("kind").GetString() != "response" || root.GetProperty("type").GetString() != "pdf.flushPreview" ||
        !Guid.TryParse(root.GetProperty("id").GetString(), out Guid id) ||
        !Guid.TryParse(payload.GetProperty("sessionId").GetString(), out Guid session) ||
        !Guid.TryParseExact(payload.GetProperty("documentId").GetString(), "N", out _))
        throw new WorkbenchBridgeProtocolException("Preview flush response identity is invalid.");
      return (id, session, payload.GetProperty("documentId").GetString()!, payload.GetProperty("ok").GetBoolean());
    }
    catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
    { throw new WorkbenchBridgeProtocolException("Preview flush response is invalid.", error); }
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
      string? documentId = null; long? documentRevision = null;
      if (scope == "pdf" && action != "open")
      {
        documentId = arguments.GetProperty("documentId").GetString();
        if (!Guid.TryParseExact(documentId, "N", out _)) throw new WorkbenchBridgeProtocolException("PDF document identity is missing or invalid.");
        documentRevision = arguments.GetProperty("documentRevision").GetInt64();
        if (documentRevision < 0) throw new WorkbenchBridgeProtocolException("Invalid PDF revision");
        JsonObject fields = JsonNode.Parse(arguments.GetRawText())!.AsObject(); fields.Remove("documentId"); fields.Remove("documentRevision");
        arguments = JsonSerializer.SerializeToElement(fields);
      }
      WorkbenchCommand typedCommand = ParseTypedCommand(scope, action, arguments);
      if (documentId is not null) typedCommand = new PdfBoundCommand(documentId, typedCommand, documentRevision);
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
    "imageEdit" => WorkbenchRoute.ImageEdit,
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
      case ("imageEdit", "selectImage"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new SelectImageEditFileCommand();
      case ("imageEdit", "readClipboard"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new ReadImageEditClipboardCommand();
      case ("imageEdit", "notifyScreenshotRevision"):
        EnsureObjectWithFields(
          arguments, SessionRevisionArgumentFields, "command arguments");
        return new NotifyImageEditRevisionCommand(
          ParseGuidArgument(arguments, "sessionId"),
          ParseContentRevision(arguments));
      case ("imageEdit", "closeScreenshotSession"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new CloseImageEditSessionCommand();
      case ("imageEdit", "copyScreenshotImage"):
        EnsureObjectWithFields(
          arguments, SessionResourceArgumentFields, "command arguments");
        return new CopyImageEditImageCommand(
          ReadAnnotationResourceUri(arguments),
          ParseGuidArgument(arguments, "sessionId"),
          ParseContentRevision(arguments));
      case ("imageEdit", "saveScreenshotImage"):
        EnsureObjectWithFields(
          arguments, SessionResourceArgumentFields, "command arguments");
        return new SaveImageEditImageCommand(
          ReadAnnotationResourceUri(arguments),
          ParseGuidArgument(arguments, "sessionId"),
          ParseContentRevision(arguments));
      case ("imageEdit", "pinScreenshotImage"):
      {
        // excludeBoxes 可选：无该字段保持旧命令形状（空排除区）。
        bool withExclusions = !HasExactFields(arguments, SessionResourceArgumentFields);
        EnsureObjectWithFields(
          arguments,
          withExclusions ? PinResourceArgumentFields : SessionResourceArgumentFields,
          "command arguments");
        return new PinImageEditImageCommand(
          ReadAnnotationResourceUri(arguments),
          ParseGuidArgument(arguments, "sessionId"),
          ParseContentRevision(arguments),
          withExclusions ? ParseExclusionBoxes(arguments) : []);
      }
      case ("imageEdit", "recognizeScreenshotImage"):
        EnsureObjectWithFields(
          arguments, SessionResourceWithExclusionArgumentFields, "command arguments");
        return new RecognizeImageEditImageCommand(
          ReadAnnotationResourceUri(arguments),
          ParseGuidArgument(arguments, "sessionId"),
          ParseContentRevision(arguments),
          ParseExclusionBoxes(arguments));
      case ("imageEdit", "prepareScreenshotTextLayer"):
        EnsureObjectWithFields(
          arguments, SessionResourceArgumentFields, "command arguments");
        return new PrepareImageEditTextLayerCommand(
          ReadAnnotationResourceUri(arguments),
          ParseGuidArgument(arguments, "sessionId"),
          ParseContentRevision(arguments));
      case ("imageEdit", "cancelScreenshotTextLayer"):
        EnsureObjectWithFields(
          arguments, SessionRevisionArgumentFields, "command arguments");
        return new CancelImageEditTextLayerCommand(
          ParseGuidArgument(arguments, "sessionId"),
          ParseContentRevision(arguments));
      case ("imageEdit", "copyScreenshotSelection"):
        return new CopyImageEditSelectionCommand(
          ParseGuidArgument(arguments, "sessionId"),
          ParseContentRevision(arguments),
          ParseSelectionText(arguments));
      case ("imageEdit", "copyAnnotatedImage"):
        return new CopyImageEditAnnotatedImageCommand(ParseAnnotationResourceUri(arguments));
      case ("imageEdit", "saveAnnotatedImage"):
        return new SaveImageEditAnnotatedImageCommand(ParseAnnotationResourceUri(arguments));
      case ("recognition", "selectImage"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new SelectRecognitionImageCommand();
      case ("recognition", "readClipboard"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new ReadRecognitionClipboardCommand();
      case ("recognition", "openImageForEdit"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new OpenRecognitionImageForEditCommand();
      case ("recognition", "pasteImageForEdit"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new PasteRecognitionImageForEditCommand();
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
      {
        // excludeBoxes 可选：无该字段保持旧命令形状（空排除区）。
        bool withExclusions = !HasExactFields(arguments, SessionResourceArgumentFields);
        EnsureObjectWithFields(
          arguments,
          withExclusions ? PinResourceArgumentFields : SessionResourceArgumentFields,
          "command arguments");
        return new PinScreenshotImageCommand(
          ReadAnnotationResourceUri(arguments),
          ParseGuidArgument(arguments, "sessionId"),
          ParseContentRevision(arguments),
          withExclusions ? ParseExclusionBoxes(arguments) : []);
      }
      case ("recognition", "recognizeScreenshotImage"):
        EnsureObjectWithFields(
          arguments, SessionResourceWithExclusionArgumentFields, "command arguments");
        return new RecognizeScreenshotImageCommand(
          ReadAnnotationResourceUri(arguments),
          ParseGuidArgument(arguments, "sessionId"),
          ParseContentRevision(arguments),
          ParseExclusionBoxes(arguments));
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
      case ("batch", "setItemPageRange"):
        EnsureObjectWithFields(arguments, new HashSet<string> { "itemId", "pageRange" }, "command arguments");
        return new SetBatchItemPageRangeCommand(ParseGuidArgument(arguments, "itemId"),
          arguments.GetProperty("pageRange").GetString());
      case ("batch", "removeItem"):
        EnsureObjectWithFields(arguments, BatchItemArgumentFields, "command arguments");
        return new RemoveBatchItemCommand(ParseGuidArgument(arguments, "itemId"));
      case ("batch", "setWindow"):
        return new SetBatchWindowCommand(ParseWindowStart(arguments));
      case ("batch", "setTaskEngine"):
        return new SetBatchTaskEngineCommand(ParseTaskEngine(arguments));
      case ("pdf", "setTaskEngine"):
        return new SetPdfTaskEngineCommand(ParseTaskEngine(arguments));
      case ("pdf", "activateDocument"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments"); return new ActivatePdfDocumentCommand();
      case ("pdf", "saveAs"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments"); return new SavePdfAsCommand();
      case ("pdf", "exportDocuments"):
        EnsureObjectWithFields(arguments, new HashSet<string> { "modifiedOnly", "retry" }, "command arguments");
        return new ExportPdfDocumentsCommand(arguments.GetProperty("modifiedOnly").GetBoolean(), arguments.GetProperty("retry").GetBoolean());
      case ("pdf", "cancelExport"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments"); return new CancelPdfExportCommand();
      case ("pdf", "setPreviewPosition"):
        EnsureObjectWithFields(arguments, new HashSet<string> { "position" }, "command arguments");
        JsonElement position = arguments.GetProperty("position");
        EnsureObjectWithFields(position, new HashSet<string> { "zoom", "left", "top", "showBoxes", "block", "draft", "revision", "page" }, "PDF preview position");
        PdfPreviewPosition view = position.Deserialize<PdfPreviewPosition>(SerializerOptions) ?? throw new WorkbenchBridgeProtocolException("Missing PDF position");
        if (!double.IsFinite(view.Left) || !double.IsFinite(view.Top) || view.Left < 0 || view.Top < 0 || view.Zoom is { } zoom && (!double.IsFinite(zoom) || zoom < .01 || zoom > 8) || view.Draft.Length > 65536)
          throw new WorkbenchBridgeProtocolException("Invalid PDF position");
        return new SetPdfPreviewPositionCommand(view);
      case ("pdf", "open"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new OpenPdfCommand();
      case ("pdf", "rotate"):
        return ParseRotate(arguments);
      case ("pdf", "orient"):
        EnsureObjectWithFields(arguments, new HashSet<string> { "range", "landscape" }, "command arguments");
        return new OrientPdfCommand(ParsePdfRange(arguments), arguments.GetProperty("landscape").GetBoolean());
      case ("pdf", "correctOrientation"):
        EnsureObjectWithFields(arguments, new HashSet<string> { "range" }, "command arguments");
        return new CorrectPdfOrientationCommand(ParsePdfRange(arguments));
      case ("pdf", "insertBlank"):
        EnsureObjectWithFields(arguments, new HashSet<string> { "afterIndex", "width", "height", "revision" }, "command arguments");
        double width = arguments.GetProperty("width").GetDouble(), height = arguments.GetProperty("height").GetDouble();
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
          throw new WorkbenchBridgeProtocolException("PDF page size is invalid.");
        return new InsertPdfBlankCommand(arguments.GetProperty("afterIndex").GetInt32(), width, height, arguments.GetProperty("revision").GetInt64());
      case ("pdf", "insertFrom"):
        EnsureObjectWithFields(arguments, new HashSet<string> { "afterIndex", "revision" }, "command arguments");
        return new InsertPdfFromCommand(arguments.GetProperty("afterIndex").GetInt32(), arguments.GetProperty("revision").GetInt64());
      case ("pdf", "movePage"):
        EnsureObjectWithFields(arguments, new HashSet<string> { "fromIndex", "toIndex", "revision" }, "command arguments");
        return new MovePdfPageCommand(arguments.GetProperty("fromIndex").GetInt32(), arguments.GetProperty("toIndex").GetInt32(), arguments.GetProperty("revision").GetInt64());
      case ("pdf", "close"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new ClosePdfCommand();
      case ("pdf", "deletePages"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new DeletePdfPagesCommand();
      case ("pdf", "ocrPages"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new OcrPdfPagesCommand();
      case ("pdf", "addTextLayers"):
        EnsureObjectWithFields(arguments, new HashSet<string> { "range", "overwrite" }, "command arguments");
        string? range = arguments.GetProperty("range").GetString();
        if (range is not ("selected" or "all" or "unlayered"))
          throw new WorkbenchBridgeProtocolException("PDF range is invalid.");
        return new AddPdfTextLayersCommand(range, arguments.GetProperty("overwrite").GetBoolean());
      case ("pdf", "deleteTextLayers"):
        EnsureObjectWithFields(arguments, new HashSet<string> { "pages", "revision", "confirmed" }, "command arguments");
        if (!arguments.GetProperty("confirmed").GetBoolean())
          throw new WorkbenchBridgeProtocolException("PDF deletion requires confirmation.");
        return new DeletePdfTextLayersCommand(ParsePageIndexes(arguments.GetProperty("pages")), arguments.GetProperty("revision").GetInt64());
      case ("pdf", "cancel"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new CancelPdfCommand();
      case ("pdf", "setProcessingSettings"):
        EnsureObjectWithFields(arguments, new HashSet<string> { "settings" }, "command arguments");
        JsonElement pdfSettings = arguments.GetProperty("settings");
        EnsureObjectWithFields(pdfSettings, new HashSet<string> { "renderDpi", "maxPixels", "fontSizeRatio", "fontSizeRetryCount", "fontSizeShrinkFactor", "minFontSize", "compressOnSave", "cleanOnSave" }, "PDF settings");
        PdfProcessingSettings processingSettings = pdfSettings.Deserialize<PdfProcessingSettings>(SerializerOptions)
          ?? throw new WorkbenchBridgeProtocolException("PDF settings missing.");
        processingSettings.Validate();
        return new SetPdfProcessingSettingsCommand(processingSettings);
      case ("pdf", "save"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new SavePdfCommand();
      case ("pdf", "selectPages"):
        EnsureObjectWithFields(arguments, PagesArgumentFields, "command arguments");
        return new SelectPdfPagesCommand(ParsePageIndexes(
          arguments.GetProperty("pages")));
      case ("pdf", "selectAll"):
        EnsureObjectWithFields(arguments, new HashSet<string> { "selected" }, "command arguments");
        return new SelectAllPdfPagesCommand(arguments.GetProperty("selected").GetBoolean());
      case ("pdf", "selectPage"):
        EnsureObjectWithFields(arguments, new HashSet<string> { "page", "selected" }, "command arguments");
        return new SelectPdfPageCommand(arguments.GetProperty("page").GetInt32(), arguments.GetProperty("selected").GetBoolean());
      case ("pdf", "setCurrentPage"):
        EnsureObjectWithFields(arguments, new HashSet<string> { "page" }, "command arguments");
        return new SetCurrentPdfPageCommand(arguments.GetProperty("page").GetInt32());
      case ("pdf", "updateBlockText"):
        EnsureObjectWithFields(arguments, new HashSet<string> { "page", "blockIndex", "newText", "expectedOldText", "revision", "sessionId" }, "command arguments");
        string? expectedOld = arguments.GetProperty("expectedOldText").GetString();
        string? pdfSession = arguments.GetProperty("sessionId").GetString();
        string newText = arguments.GetProperty("newText").GetString()!;
        if (string.IsNullOrWhiteSpace(newText) || expectedOld is null || string.IsNullOrWhiteSpace(pdfSession))
          throw new WorkbenchBridgeProtocolException("PDF block text is invalid.");
        return new UpdatePdfBlockTextCommand(
          arguments.GetProperty("page").GetInt32(),
          arguments.GetProperty("blockIndex").GetInt32(),
          newText,
          expectedOld,
          arguments.GetProperty("revision").GetInt64(), pdfSession);
      case ("pdf", "retryPageInspect"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new RetryPdfPageInspectCommand();
      case ("pdf", "setWindow"):
        return new SetPdfWindowCommand(ParseWindowStart(arguments));
      case ("qrcode", "generate"):
        bool extendedQr = !HasExactFields(arguments, TextArgumentFields);
        EnsureObjectWithFields(arguments, extendedQr ? QrGenerateFields : TextArgumentFields, "command arguments");
        string? text = arguments.GetProperty("text").GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
          throw new WorkbenchBridgeProtocolException(
            "Workbench QR code text is invalid.");
        }
        string format = extendedQr ? arguments.GetProperty("format").GetString()! : "qrcode";
        string captionMode = extendedQr ? arguments.GetProperty("captionMode").GetString()! : "off";
        string captionText = extendedQr ? arguments.GetProperty("captionText").GetString()! : "";
        if (format is not ("qrcode" or "code128" or "ean13") ||
            captionMode is not ("off" or "payload" or "custom") || captionText is null)
          throw new WorkbenchBridgeProtocolException("Workbench code generation options are invalid.");
        return new GenerateQrCodeCommand(text, format, captionMode, captionText);
      case ("qrcode", "decodeCurrent"):
        EnsureObjectWithFields(arguments, ForceArgumentFields, "command arguments");
        return new DecodeCurrentQrCodeCommand(arguments.GetProperty("force").GetBoolean());
      case ("qrcode", "copyImage"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new CopyQrCodeImageCommand();
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
        bool independent = arguments.TryGetProperty("paddleocrModelSourceId", out _) ||
          arguments.TryGetProperty("mineruModelSourceId", out _);
        HashSet<string> defaultsFields = independent
          ? IndependentSourceDefaultsArgumentFields : EnvironmentSourceDefaultsArgumentFields;
        HashSet<string> scopedFields = independent
          ? IndependentEnvironmentSourcesArgumentFields : EnvironmentSourcesArgumentFields;
        bool scoped = !HasExactFields(arguments, defaultsFields);
        EnsureObjectWithFields(arguments, scoped ? scopedFields : defaultsFields, "command arguments");
        string? sourceEnvironmentId = scoped ? ParseEnvironmentId(arguments) : null;
        return new SetEnvironmentSourcesCommand(
          sourceEnvironmentId,
          ParseSourceId(arguments.GetProperty("packageSourceId")),
          independent ? null : ParseSourceId(arguments.GetProperty("modelSourceId")),
          independent ? ParseSourceId(arguments.GetProperty("paddleocrModelSourceId")) : null,
          independent ? ParseSourceId(arguments.GetProperty("mineruModelSourceId")) : null,
          independent);
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
      case ("settings", "setEnvironmentCleanupPage"):
        EnsureObjectWithFields(arguments, new HashSet<string>(StringComparer.Ordinal) { "page" }, "command arguments");
        JsonElement cleanupPage = arguments.GetProperty("page");
        if (cleanupPage.ValueKind != JsonValueKind.Number || !cleanupPage.TryGetInt32(out int page) || page < 0)
          throw new WorkbenchBridgeProtocolException("清理页码无效。");
        return new SetEnvironmentCleanupPageCommand(page);
      case ("settings", "previewEnvironmentCleanup"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new PreviewEnvironmentCleanupCommand();
      case ("settings", "cancelEnvironmentCleanup"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new CancelEnvironmentCleanupCommand();
      case ("settings", "runEnvironmentCleanup"):
        EnsureObjectWithFields(arguments, new HashSet<string>(StringComparer.Ordinal) { "planId", "itemIds" }, "command arguments");
        string? cleanupPlanId = arguments.GetProperty("planId").GetString();
        JsonElement cleanupIds = arguments.GetProperty("itemIds");
        if (cleanupPlanId is null || cleanupPlanId.Length != 32 || !cleanupPlanId.All(Uri.IsHexDigit) ||
            cleanupIds.ValueKind != JsonValueKind.Array || cleanupIds.GetArrayLength() is < 1 or > 2048 ||
            cleanupIds.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()) || item.GetString()!.Length > 1024))
          throw new WorkbenchBridgeProtocolException("空间清理选择无效。");
        string[] selectedCleanupIds = cleanupIds.EnumerateArray().Select(item => item.GetString()!).ToArray();
        if (selectedCleanupIds.Distinct(StringComparer.Ordinal).Count() != selectedCleanupIds.Length)
          throw new WorkbenchBridgeProtocolException("空间清理选择包含重复项目。");
        return new RunEnvironmentCleanupCommand(cleanupPlanId, selectedCleanupIds);
      case ("settings", "deleteEnvironment"):
        EnsureObjectWithFields(arguments, EnvironmentIdArgumentFields, "command arguments");
        return new DeleteEnvironmentCommand(ParseEnvironmentId(arguments));
      case ("settings", "repairEmptyEnvironment"):
        EnsureObjectWithFields(arguments, EnvironmentIdArgumentFields, "command arguments");
        return new RepairEmptyEnvironmentCommand(ParseEnvironmentId(arguments));
      case ("settings", "findCompatibleEnvironment"):
      {
        // 桥只做协议 id 类型守卫（与 preview 同一锁定 id 集）；目录真值由
        // 宿主按 Runtime 目录核验，未知输入拒绝，不降 input validation。
        EnsureObjectWithFields(arguments, RecipeOnlyArgumentFields, "command arguments");
        string? compatibleRecipe = arguments.GetProperty("recipe").GetString();
        if (compatibleRecipe is not ("rapidocr-cpu" or "paddleocr-cpu" or "paddleocr-cuda" or
            "mineru-cpu" or "rapidocr+mineru-cpu" or "rapidocr+mineru-cuda"))
          throw new WorkbenchBridgeProtocolException("运行环境配方无效。");
        return new FindCompatibleEnvironmentCommand(compatibleRecipe);
      }
      case ("settings", "prepareEnvironment"):
      {
        // 与 preview/findCompatible 同一 6-id 协议白名单；目录真值仍由宿主
        // 按 Runtime 目录核验。
        EnsureObjectWithFields(arguments, RecipeOnlyArgumentFields, "command arguments");
        string? prepareRecipe = arguments.GetProperty("recipe").GetString();
        if (prepareRecipe is not ("rapidocr-cpu" or "paddleocr-cpu" or "paddleocr-cuda" or
            "mineru-cpu" or "rapidocr+mineru-cpu" or "rapidocr+mineru-cuda"))
          throw new WorkbenchBridgeProtocolException("运行环境配方无效。");
        return new PrepareEnvironmentCommand(prepareRecipe);
      }
      case ("settings", "setTheme"):
        EnsureObjectWithFields(arguments, ThemeArgumentFields, "command arguments");
        return new SetThemeCommand(ParseTheme(
          arguments.GetProperty("theme").GetString()));
      case ("settings", "setStartup"):
        EnsureObjectWithFields(arguments, EnabledArgumentFields, "command arguments");
        return new SetStartupCommand(arguments.GetProperty("enabled").GetBoolean());
      case ("settings", "beginHotkeyRecording"):
        EnsureObjectWithFields(arguments, RecordingArgumentFields, "command arguments");
        return new BeginHotkeyRecordingCommand(ParseGuidArgument(arguments, "recordingId"));
      case ("settings", "endHotkeyRecording"):
        EnsureObjectWithFields(arguments, RecordingArgumentFields, "command arguments");
        return new EndHotkeyRecordingCommand(ParseGuidArgument(arguments, "recordingId"));
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
      case ("settings", "setFloatingToolbarPreferences"):
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.EnumerateObject().Any() ||
          arguments.EnumerateObject().Any(field => !ToolbarPreferencesArgumentFields.Contains(field.Name)))
        {
          throw new WorkbenchBridgeProtocolException("Workbench floating toolbar preference is invalid.");
        }
        int? lingerMs = null;
        int? peekPixels = null;
        string? toolbarTheme = null;
        if (arguments.TryGetProperty("lingerMs", out JsonElement delayArgument))
        {
          if (delayArgument.ValueKind != JsonValueKind.Number || !delayArgument.TryGetInt32(out int delay) ||
            delay is < 100 or > 5000)
          {
            throw new WorkbenchBridgeProtocolException("Workbench floating toolbar preference is invalid.");
          }
          lingerMs = delay;
        }
        if (arguments.TryGetProperty("theme", out JsonElement themeArgument))
        {
          toolbarTheme = themeArgument.GetString();
          if (toolbarTheme is not ("system" or "light" or "dark"))
          {
            throw new WorkbenchBridgeProtocolException("Workbench floating toolbar preference is invalid.");
          }
        }
        if (arguments.TryGetProperty("peekPixels", out JsonElement peekArgument))
        {
          if (peekArgument.ValueKind != JsonValueKind.Number || !peekArgument.TryGetInt32(out int pixels) ||
            pixels is < 1 or > 20)
          {
            throw new WorkbenchBridgeProtocolException("Workbench floating toolbar preference is invalid.");
          }
          peekPixels = pixels;
        }
        return new SetFloatingToolbarPreferencesCommand(lingerMs, toolbarTheme, peekPixels);
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
      case ("settings", "setMineruRecognition"):
        return ParseMineruRecognition(arguments);
      case ("settings", "setDefaultRecognitionMode"):
        return ParseDefaultRecognitionMode(arguments);
      case ("settings", "prepareMineruConnection"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new PrepareMineruConnectionCommand();
      case ("settings", "prepareRemoteHost"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new PrepareRemoteHostCommand();
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
      case ("diagnostics", "copy"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new CopyDiagnosticsCommand();
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

  private const int MaximumExclusionBoxes = 64;
  private static readonly HashSet<string> ExclusionBoxFields = ["x", "y", "width", "height"];

  /// <summary>
  /// 解析可选 excludeBoxes：[0,1000] 归一化矩形，正面积为限；
  /// 每个盒子字段精确匹配（与顶层 trustboundary 一致），数值必须有限，
  /// 越界、退化、非对象和超量整体拒绝，不静默截断用户掩膜。
  /// </summary>
  private static IReadOnlyList<WorkbenchExclusionBox> ParseExclusionBoxes(
    JsonElement arguments)
  {
    JsonElement boxes = arguments.GetProperty("excludeBoxes");
    if (boxes.ValueKind != JsonValueKind.Array || boxes.GetArrayLength() > MaximumExclusionBoxes)
    {
      throw new WorkbenchBridgeProtocolException(
        "Workbench pin exclusion boxes are invalid.");
    }
    List<WorkbenchExclusionBox> parsed = [];
    foreach (JsonElement box in boxes.EnumerateArray())
    {
      EnsureObjectWithFields(box, ExclusionBoxFields, "pin exclusion box");
      double boxX = box.GetProperty("x").GetDouble();
      double boxY = box.GetProperty("y").GetDouble();
      double boxWidth = box.GetProperty("width").GetDouble();
      double boxHeight = box.GetProperty("height").GetDouble();
      if (!double.IsFinite(boxX) || !double.IsFinite(boxY) ||
        !double.IsFinite(boxWidth) || !double.IsFinite(boxHeight))
      {
        throw new WorkbenchBridgeProtocolException(
          "Workbench pin exclusion box is invalid.");
      }
      if (boxX < 0 || boxY < 0 ||
        boxX + boxWidth > 1000 || boxY + boxHeight > 1000 ||
        boxWidth <= 0 || boxHeight <= 0)
      {
        throw new WorkbenchBridgeProtocolException(
          "Workbench pin exclusion box is out of range.");
      }
      parsed.Add(new WorkbenchExclusionBox(boxX, boxY, boxWidth, boxHeight));
    }
    return parsed;
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
    EnsureObjectWithFields(arguments, arguments.TryGetProperty("range", out _) ? new HashSet<string> { "degrees", "range" } : DegreesArgumentFields, "command arguments");
    int degrees = arguments.GetProperty("degrees").GetInt32();
    if (degrees is not (90 or -90))
    {
      throw new WorkbenchBridgeProtocolException(
        "Workbench PDF rotation is invalid.");
    }
    return new RotatePdfCommand(degrees, arguments.TryGetProperty("range", out _) ? ParsePdfRange(arguments) : "selected");
  }

  private static string ParsePdfRange(JsonElement arguments)
  {
    string? range = arguments.GetProperty("range").GetString();
    return range is "selected" or "all" ? range : throw new WorkbenchBridgeProtocolException("PDF range is invalid.");
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

  private static SetMineruRecognitionCommand ParseMineruRecognition(
    JsonElement arguments)
  {
    EnsureObjectWithFields(arguments, MineruRecognitionArgumentFields, "command arguments");
    string? tier = arguments.GetProperty("tier").GetString();
    string? ocrMode = arguments.GetProperty("ocrMode").GetString();
    string? pageRange = arguments.GetProperty("pageRange").GetString();
    string? language = arguments.GetProperty("language").GetString();
    if (!MineruRecognitionSettings.TryParseTier(tier, out _))
    {
      throw new WorkbenchBridgeProtocolException(
        "Workbench MinerU tier is invalid.");
    }
    if (!MineruRecognitionSettings.TryParseOcrMode(ocrMode, out _))
    {
      throw new WorkbenchBridgeProtocolException(
        "Workbench MinerU ocr mode is invalid.");
    }
    // 与 Platform 契约同一语法/限额；语义（页数、文件类型）由 Backend 负责。
    if (!MineruRecognitionSettings.IsValidPageRange(pageRange))
    {
      throw new WorkbenchBridgeProtocolException(
        "Workbench MinerU page range is invalid.");
    }
    if (string.IsNullOrEmpty(language) || language.Length > 32)
    {
      throw new WorkbenchBridgeProtocolException(
        "Workbench MinerU language is invalid.");
    }
    return new SetMineruRecognitionCommand(tier!, ocrMode!, pageRange!, language);
  }

  private static SetDefaultRecognitionModeCommand ParseDefaultRecognitionMode(
    JsonElement arguments)
  {
    // 仅接受非空字符串模式 id；未知/不可用由宿主目录校验 fail closed。
    if (arguments.ValueKind != JsonValueKind.Object ||
      !arguments.TryGetProperty("mode", out JsonElement modeElement) ||
      modeElement.ValueKind != JsonValueKind.String)
    {
      throw new WorkbenchBridgeProtocolException(
        "Workbench default recognition mode is invalid.");
    }
    string mode = modeElement.GetString()!;
    if (string.IsNullOrWhiteSpace(mode) || mode.Length > MaxRecognitionModeIdLength)
    {
      throw new WorkbenchBridgeProtocolException(
        "Workbench default recognition mode is invalid.");
    }
    EnsureObjectWithFields(arguments, DefaultModeArgumentFields, "command arguments");
    return new SetDefaultRecognitionModeCommand(mode);
  }

  private static string ParseEnvironmentId(JsonElement arguments)
  {
    string? value = arguments.GetProperty("environmentId").GetString();
    // 环境标识由 Runtime 分配，支持可读名称及历史 UUID；这里仅约束
    // 单个目录段的消息形状，目录成员、重名与保留名由 Runtime 校验。
    if (value is { Length: > 0 and <= 64 } && value == value.Trim().TrimEnd('.') &&
        value is not ("." or "..") && value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
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
    WorkbenchRoute.ImageEdit => "imageEdit",
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
    ImageEditWorkbenchState imageEdit => new
    {
      imageEdit.IsBusy,
      imageEdit.StatusCode,
      imageEdit.Input,
      screenshotSession = imageEdit.ScreenshotSession is null ? null : new
      {
        sessionId = imageEdit.ScreenshotSession.SessionId,
        revision = imageEdit.ScreenshotSession.Revision,
        textSelectionRequested = imageEdit.ScreenshotSession.TextSelectionRequested,
        sceneEditing = imageEdit.ScreenshotSession.SceneEditing,
        excludeBoxes = imageEdit.ScreenshotSession.ExcludeBoxes ?? [],
      },
      textLayer = imageEdit.TextLayer is null ? null : new
      {
        status = imageEdit.TextLayer.Status,
        reason = imageEdit.TextLayer.Reason,
        binding = imageEdit.TextLayer.Binding is null ? null : new
        {
          sessionId = imageEdit.TextLayer.Binding.SessionId,
          revision = imageEdit.TextLayer.Binding.Revision,
        },
        modeId = imageEdit.TextLayer.ModeId,
        serviceInstance = imageEdit.TextLayer.ServiceInstance,
        image = imageEdit.TextLayer.Image,
        lines = imageEdit.TextLayer.Lines ?? [],
      },
    },
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
        sceneEditing = recognition.ScreenshotSession.SceneEditing,
        excludeBoxes = recognition.ScreenshotSession.ExcludeBoxes ?? [],
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
      inputKindNotice = batch.InputKindNotice,
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
      pdf.Revision, pdf.IsModified, pdf.DetectedCount, pdf.TextLayerCount, pdf.AddedCount,
      pdf.Phase, pdf.ProgressCurrent, pdf.ProgressTotal, pdf.Summary, pdf.CanAddTextLayer, pdf.ProcessingSettings,
      pagePreview = pdf.PagePreview,
      pageInspect = pdf.PageInspect,
      pageInspectStatusCode = pdf.PageInspectStatusCode,
      sessionId = pdf.SessionId,
      canInspectPage = pdf.CanInspectPage,
      canCorrectText = pdf.CanCorrectText,
      documentId = pdf.DocumentId, documents = pdf.Documents ?? [], previewPosition = pdf.PreviewPosition,
      canCopyExport = pdf.CanCopyExport, exporting = pdf.Exporting, exportItems = pdf.ExportItems ?? [], exportGeneration = pdf.ExportGeneration,
    },
    QrCodeWorkbenchState qrCode => new
    {
      qrCode.IsBusy,
      qrCode.StatusCode,
      qrCode.Results,
      qrCode.GeneratedResource,
      qrCode.PreviewRevision,
      qrCode.NeedsPreviewDecode,
      qrCode.StatusMessage,
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
        settings.FloatingToolbar.LingerMs,
        settings.FloatingToolbar.PeekPixels,
        settings.FloatingToolbar.Theme,
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
      environmentResolvedDefaultSources = settings.EnvironmentResolvedDefaultSources ?? [],
      environmentUnknownDefaultSourceIds = settings.EnvironmentUnknownDefaultSourceIds ?? [],
      environmentPackageSourceIds = settings.EnvironmentPackageSourceIds ?? [],
      settings.EnvironmentCanCancelInstall,
      settings.EnvironmentInstallProgress,
      settings.EnvironmentInstallLog,
      settings.EnvironmentSupportsInstallProgress,
      settings.EnvironmentSupportsCleanup,
      settings.EnvironmentCleanupPlan,
      settings.EnvironmentCleanupResult,
      settings.EnvironmentCanCancelCleanup,
      settings.EnvironmentCleanupPage,
      settings.EnvironmentCleanupPageCount,
      environmentRecipes = settings.EnvironmentRecipes ?? [],
      environmentHardware = settings.EnvironmentHardware is null ? null : new
      {
        nvidiaDriverStatus = settings.EnvironmentHardware.NvidiaDriverStatus,
        nvidiaDriverReason = settings.EnvironmentHardware.NvidiaDriverReason,
        nvidiaDriverVersion = settings.EnvironmentHardware.NvidiaDriverVersion,
      },
      environmentCompatibility = settings.EnvironmentCompatibility is null ? null : new
      {
        settings.EnvironmentCompatibility.Recipe,
        selectedEnvironmentId = settings.EnvironmentCompatibility.SelectedEnvironmentId,
        selectedEnvironmentRevision = settings.EnvironmentCompatibility.SelectedEnvironmentRevision,
        selectionReason = settings.EnvironmentCompatibility.SelectionReason,
        environments = settings.EnvironmentCompatibility.Environments ?? [],
      },
      mineruConnection = settings.MineruConnection is null ? null : new
      {
        settings.MineruConnection.Supported,
        settings.MineruConnection.Mode,
        apiUrl = settings.MineruConnection.ApiUrl,
        hasApiKey = settings.MineruConnection.HasApiKey,
      },
      defaultRecognitionMode = settings.DefaultRecognitionMode is null ? null : new
      {
        settings.DefaultRecognitionMode.Supported,
        settings.DefaultRecognitionMode.ModeId,
        settings.DefaultRecognitionMode.Stored,
      },
      mineruRecognition = settings.MineruRecognition is null ? null : new
      {
        settings.MineruRecognition.Supported,
        settings.MineruRecognition.Stored,
        settings.MineruRecognition.Tier,
        settings.MineruRecognition.OcrMode,
        settings.MineruRecognition.PageRange,
        settings.MineruRecognition.Language,
        settings.MineruRecognition.Invalid,
        settings.MineruRecognition.InvalidReason,
        tiers = settings.MineruRecognition.Tiers ?? [],
        languages = settings.MineruRecognition.Languages ?? [],
        settings.MineruRecognition.DefaultTier,
      },
      recognitionModes = settings.RecognitionModes ?? [],
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
      diagnostics.DeviceEvidence,
    },
    null => null,
    _ => throw new WorkbenchBridgeProtocolException(
      "Workbench state type is not supported."),
  };

  private static string BoundInstallLogToMessage(string json)
  {
    if (Encoding.UTF8.GetByteCount(json) <= MaxMessageBytes) return json;
    JsonNode root = JsonNode.Parse(json)!;
    JsonNode? payload = root["payload"];
    JsonNode? settings = payload?["state"] ?? payload?["features"]?["settings"];
    if (settings?["environmentInstallLog"] is not JsonArray logs || logs.Count == 0) return json;
    string[] entries = logs.Select(item => item!.GetValue<string>()).ToArray();
    // The event's log is already in the host log list; preserve all authority
    // fields and trim only this optional, newly added display material.
    if (settings["environmentInstallProgress"] is { } progress) progress["log"] = null;
    logs.Clear();
    logs.Add("[较早输出已截断；显示内容受桥接容量限制]");
    int available = MaxMessageBytes - Encoding.UTF8.GetByteCount(root.ToJsonString(SerializerOptions));
    if (available < 0) return root.ToJsonString(SerializerOptions); // Authority alone still fails closed.
    var tail = new List<string>();
    for (int index = entries.Length - 1; index >= 0; index--)
    {
      int bytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(entries[index], SerializerOptions)) + 1;
      if (bytes <= available)
      {
        tail.Add(entries[index]);
        available -= bytes;
        continue;
      }
      if (tail.Count == 0 && available > 3)
      {
        // Keep a useful suffix even when one Unicode line uses the entire budget.
        string last = entries[index];
        int low = 0, high = last.Length;
        while (low < high)
        {
          int middle = (low + high) / 2;
          int start = middle < last.Length && char.IsLowSurrogate(last[middle]) ? middle + 1 : middle;
          int size = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(last[start..], SerializerOptions)) + 1;
          if (size <= available) high = middle;
          else low = middle + 1;
        }
        if (low < last.Length && char.IsLowSurrogate(last[low])) low++;
        if (low < last.Length) tail.Add(last[low..]);
      }
      break;
    }
    for (int index = tail.Count - 1; index >= 0; index--) logs.Add(tail[index]);
    return root.ToJsonString(SerializerOptions);
  }

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

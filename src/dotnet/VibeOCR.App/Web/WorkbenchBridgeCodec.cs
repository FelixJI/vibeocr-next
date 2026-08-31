using System.Text;
using System.Text.Json;
using VibeOCR.App.Workbench;

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
  private static readonly HashSet<string> HotkeyArgumentFields = ["hotkey"];
  private static readonly HashSet<string> BatchMoveArgumentFields = ["itemId", "delta"];
  private static readonly HashSet<string> BatchItemArgumentFields = ["itemId"];
  private static readonly HashSet<string> ConcurrencyArgumentFields = ["concurrency"];
  private static readonly HashSet<string> PagesArgumentFields = ["pages"];
  private static readonly HashSet<string> StartArgumentFields = ["start"];
  private static readonly HashSet<string> UrlArgumentFields = ["url"];
  private static readonly HashSet<string> EngineArgumentFields = ["engine"];
  private static readonly HashSet<string> SourceKindOnlyFields = ["kind"];
  private static readonly HashSet<string> SourceArgumentFields = ["kind", "sourceId"];
  private static readonly HashSet<string> AcceleratorArgumentFields = ["accelerator"];
  private static readonly HashSet<string> FeatureArgumentFields = ["featureId", "enabled"];
  private static readonly HashSet<string> TaskEngineArgumentFields = ["engine"];
  private static readonly HashSet<string> ResourceUriArgumentFields = ["resourceUri"];

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
      case ("recognition", "cancel"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new CancelRecognitionCommand();
      case ("recognition", "copy"):
        return new CopyRecognitionResultCommand(ParseFormat(
          arguments,
          ["plain", "markdown", "rich"]));
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
      case ("batch", "setConcurrency"):
        EnsureObjectWithFields(arguments, ConcurrencyArgumentFields, "command arguments");
        int concurrency = arguments.GetProperty("concurrency").GetInt32();
        if (concurrency is < 1 or > 8)
        {
          throw new WorkbenchBridgeProtocolException(
            "Workbench batch concurrency is invalid.");
        }
        return new SetBatchConcurrencyCommand(concurrency);
      case ("batch", "setWindow"):
        return new SetBatchWindowCommand(ParseWindowStart(arguments));
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
      case ("settings", "setTheme"):
        EnsureObjectWithFields(arguments, ThemeArgumentFields, "command arguments");
        return new SetThemeCommand(ParseTheme(
          arguments.GetProperty("theme").GetString()));
      case ("settings", "setStartup"):
        EnsureObjectWithFields(arguments, EnabledArgumentFields, "command arguments");
        return new SetStartupCommand(arguments.GetProperty("enabled").GetBoolean());
      case ("settings", "setHotkey"):
        EnsureObjectWithFields(arguments, HotkeyArgumentFields, "command arguments");
        string? hotkey = arguments.GetProperty("hotkey").GetString();
        if (string.IsNullOrWhiteSpace(hotkey) || hotkey.Length > 64)
        {
          throw new WorkbenchBridgeProtocolException("Workbench hotkey is invalid.");
        }
        return new SetHotkeyCommand(hotkey);
      case ("settings", "setEngine"):
        EnsureObjectWithFields(arguments, EngineArgumentFields, "command arguments");
        string? engine = arguments.GetProperty("engine").GetString();
        if (string.IsNullOrWhiteSpace(engine) || engine.Length > 32)
        {
          throw new WorkbenchBridgeProtocolException("Workbench engine is invalid.");
        }
        return new SetOcrEngineCommand(engine);
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
      case ("settings", "installRuntime"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new InstallRuntimeCommand();
      case ("settings", "cancelRuntimeMaintenance"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new CancelRuntimeMaintenanceCommand();
      case ("settings", "retryRuntimeMaintenance"):
        EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        return new RetryRuntimeMaintenanceCommand();
      case ("recognition", "setTaskEngine"):
        bool hasTaskEngine = !HasExactFields(arguments, EmptyFields);
        if (hasTaskEngine)
        {
          EnsureObjectWithFields(arguments, TaskEngineArgumentFields, "command arguments");
        }
        else
        {
          EnsureObjectWithFields(arguments, EmptyFields, "command arguments");
        }
        string? taskEngine = hasTaskEngine
          ? arguments.GetProperty("engine").GetString()
          : null;
        if (taskEngine is not null &&
            (taskEngine.Length == 0 || taskEngine.Length > 32))
        {
          throw new WorkbenchBridgeProtocolException("Workbench task engine is invalid.");
        }
        return new SetTaskEngineCommand(taskEngine);
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

  private static string ParseAnnotationResourceUri(JsonElement arguments)
  {
    EnsureObjectWithFields(arguments, ResourceUriArgumentFields, "command arguments");
    string? value = arguments.GetProperty("resourceUri").GetString();
    if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
        !WorkbenchAnnotationStore.IsResourceUri(uri))
    {
      throw new WorkbenchBridgeProtocolException(
        "Workbench annotated image URI is invalid.");
    }
    return uri.AbsoluteUri;
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
    },
    BatchWorkbenchState batch => new
    {
      batch.IsRunning,
      batch.ItemCount,
      batch.CompletedCount,
      batch.FailedCount,
      items = batch.Items ?? [],
      batch.Concurrency,
      batch.WindowStart,
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
      settings.Hotkey,
      engines = settings.Engines ?? [],
      settings.SelectedEngine,
      settings.EngineChoiceRequired,
      sources = settings.Sources ?? [],
      settings.PendingBackend,
      settings.CanSwitchBackend,
      features = settings.Features ?? [],
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

using System.Text;
using VibeOCR.App.Features.Batch;
using VibeOCR.App.Features.Pdf;
using VibeOCR.App.Features.QrCode;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Features.Settings;
using VibeOCR.App.Features.Shell;
using VibeOCR.App.Features.Update;
using VibeOCR.App.Services;
using VibeOCR.App.Web;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace VibeOCR.App.Workbench;

public sealed class DesktopWorkbenchCommandHandler :
  IWorkbenchCommandHandler,
  IWorkbenchStateSource,
  IAsyncDisposable
{
  public static IReadOnlySet<string> Capabilities { get; } = new HashSet<string>(
    [
      "recognition.file",
      "recognition.clipboard",
      "recognition.capture",
      "recognition.results",
      "batch.add",
      "batch.export",
      "batch.run",
      "pdf.open",
      "pdf.rotate",
      "pdf.edit",
      "pdf.save",
      "qrcode.generate",
      "qrcode.decode",
      "qrcode.clipboard",
      "qrcode.save",
      "qrcode.openUrl",
      "about.openProject",
      "runtime.refresh",
      "settings.shell",
      "update.check",
      "update.install",
      "diagnostics.export",
    ],
    StringComparer.Ordinal);

  private readonly Func<RecognitionViewModel> recognitionFactory;
  private readonly Func<BatchViewModel> batchFactory;
  private readonly Func<QrCodeViewModel> qrCodeFactory;
  private readonly Func<PdfViewModel> pdfFactory;
  private readonly Func<SettingsViewModel> settingsFactory;
  private readonly Lazy<ShellViewModel> shell;
  private readonly Lazy<UpdateViewModel> update;
  private readonly VibeOCR.App.ViewModels.DiagnosticsViewModel diagnostics;
  private readonly WorkbenchResourceBroker resourceBroker;
  private readonly string resourceRoot;
  private readonly Func<nint> windowHandle;
  private readonly List<string> generatedFiles = [];
  private readonly HashSet<Task> backgroundOperations = [];
  private readonly HashSet<int> selectedPdfPages = [];
  private readonly Dictionary<int, WorkbenchResourceReference> pdfThumbnails = [];
  private RecognitionViewModel? recognition;
  private ResultActions? resultActions;
  private BatchViewModel? batch;
  private QrCodeViewModel? qrCode;
  private PdfViewModel? pdf;
  private SettingsViewModel? settings;
  private WorkbenchTheme theme = WorkbenchTheme.System;
  private WorkbenchResourceReference? generatedQrResource;
  private long recognitionGeneration;
  private long batchGeneration;
  private long pdfGeneration;
  private long qrCodeGeneration;
  private long updateGeneration;
  private int batchWindowStart;
  private int pdfWindowStart;
  private int disposed;

  public DesktopWorkbenchCommandHandler(
    Func<RecognitionViewModel> recognitionFactory,
    Func<BatchViewModel> batchFactory,
    Func<QrCodeViewModel> qrCodeFactory,
    Func<PdfViewModel> pdfFactory,
    Func<SettingsViewModel> settingsFactory,
    Func<ShellViewModel> shellFactory,
    Func<UpdateViewModel> updateFactory,
    VibeOCR.App.ViewModels.DiagnosticsViewModel diagnostics,
    WorkbenchResourceBroker resourceBroker,
    string resourceRoot,
    Func<nint> windowHandle)
  {
    this.recognitionFactory = recognitionFactory ??
      throw new ArgumentNullException(nameof(recognitionFactory));
    this.batchFactory = batchFactory ?? throw new ArgumentNullException(nameof(batchFactory));
    this.qrCodeFactory = qrCodeFactory ??
      throw new ArgumentNullException(nameof(qrCodeFactory));
    this.pdfFactory = pdfFactory ?? throw new ArgumentNullException(nameof(pdfFactory));
    this.settingsFactory = settingsFactory ??
      throw new ArgumentNullException(nameof(settingsFactory));
    ArgumentNullException.ThrowIfNull(shellFactory);
    ArgumentNullException.ThrowIfNull(updateFactory);
    shell = new Lazy<ShellViewModel>(shellFactory);
    update = new Lazy<UpdateViewModel>(updateFactory);
    this.diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    this.resourceBroker = resourceBroker ??
      throw new ArgumentNullException(nameof(resourceBroker));
    this.resourceRoot = Path.GetFullPath(resourceRoot);
    this.windowHandle = windowHandle ?? throw new ArgumentNullException(nameof(windowHandle));
  }

  public IReadOnlyList<WorkbenchState> InitialStates =>
  [
    new RecognitionWorkbenchState(false, "recognition.ready"),
    new BatchWorkbenchState(false, 0, 0, 0),
    new PdfWorkbenchState(false, "pdf.empty", 0, -1),
    new QrCodeWorkbenchState(false, "qrcode.ready", [], null),
    new SettingsWorkbenchState(
      theme,
      false,
      "settings.ready",
      "unknown",
      shell.Value.StartWithSystem,
      shell.Value.RegisteredHotkey),
    new UpdateWorkbenchState(false, "update.current", null, false),
    AboutState(),
    DiagnosticsState(),
  ];

  public event Action<WorkbenchState>? StateChanged;

  public async ValueTask<WorkbenchCommandOutcome> ExecuteAsync(
    WorkbenchCommand command,
    CancellationToken cancellationToken)
  {
    ArgumentNullException.ThrowIfNull(command);
    cancellationToken.ThrowIfCancellationRequested();
    try
    {
      WorkbenchState state = command switch
      {
        SelectRecognitionImageCommand => StartRecognition(
          viewModel => viewModel.RecognizeFileAsync(cancellationToken),
          cancellationToken),
        RecognizeDroppedFileCommand dropped => StartRecognition(
          viewModel => viewModel.RecognizeDroppedFileAsync(
            dropped.Path,
            cancellationToken),
          cancellationToken),
        ReadRecognitionClipboardCommand => StartRecognition(
          viewModel => viewModel.RecognizeClipboardAsync(cancellationToken),
          cancellationToken),
        CaptureRecognitionScreenCommand => StartRecognition(
          viewModel => viewModel.RecognizeScreenshotAsync(cancellationToken),
          cancellationToken),
        CancelRecognitionCommand => CancelRecognition(),
        CopyRecognitionResultCommand copy => await CopyRecognitionAsync(
          copy,
          cancellationToken),
        ExportRecognitionResultCommand export => await ExportRecognitionAsync(
          export,
          cancellationToken),
        AddBatchFilesCommand => await AddBatchFilesAsync(cancellationToken),
        AddDroppedBatchFilesCommand dropped => AddDroppedBatchFiles(dropped),
        ExportBatchCommand export => await ExportBatchAsync(export, cancellationToken),
        StartBatchCommand => StartBatch(cancellationToken),
        CancelBatchCommand => CancelBatch(),
        ClearBatchCommand => ClearBatch(),
        MoveBatchItemCommand move => MoveBatchItem(move),
        RemoveBatchItemCommand remove => RemoveBatchItem(remove),
        SetBatchConcurrencyCommand concurrency => SetBatchConcurrency(concurrency),
        SetBatchWindowCommand window => SetBatchWindow(window),
        OpenPdfCommand => await OpenPdfAsync(cancellationToken),
        OpenDroppedPdfCommand dropped => await OpenDroppedPdfAsync(
          dropped,
          cancellationToken),
        RotatePdfCommand rotate => await RotatePdfAsync(rotate, cancellationToken),
        ClosePdfCommand => ClosePdf(),
        DeletePdfPagesCommand => await DeletePdfPagesAsync(cancellationToken),
        OcrPdfPagesCommand => StartPdfOcr(cancellationToken),
        SavePdfCommand => await SavePdfAsync(cancellationToken),
        SelectPdfPagesCommand select => SelectPdfPages(select),
        SetPdfWindowCommand window => await SetPdfWindowAsync(
          window,
          cancellationToken),
        GenerateQrCodeCommand generate => StartQrCode(
          viewModel =>
          {
            viewModel.GenerateText = generate.Text;
            return viewModel.GenerateAsync(cancellationToken);
          },
          publishGeneratedImage: true,
          cancellationToken),
        DecodeQrCodeCommand => StartQrCode(
          viewModel => viewModel.DecodeAsync(QrCodeInputKind.File, cancellationToken),
          publishGeneratedImage: false,
          cancellationToken),
        DecodeDroppedQrCodeCommand dropped => StartQrCode(
          viewModel => viewModel.DecodeDroppedFileAsync(dropped.Path, cancellationToken),
          publishGeneratedImage: false,
          cancellationToken),
        DecodeQrCodeClipboardCommand => StartQrCode(
          viewModel => viewModel.DecodeAsync(
            QrCodeInputKind.Clipboard,
            cancellationToken),
          publishGeneratedImage: false,
          cancellationToken),
        CancelQrCodeCommand => CancelQrCode(),
        ClearQrCodeCommand => ClearQrCode(),
        SaveQrCodeCommand => await SaveQrCodeAsync(cancellationToken),
        OpenQrCodeUrlCommand openUrl => await OpenQrCodeUrlAsync(
          openUrl,
          cancellationToken),
        OpenProjectPageCommand => await OpenProjectPageAsync(cancellationToken),
        RefreshRuntimeCommand => await RefreshRuntimeAsync(cancellationToken),
        SetThemeCommand setTheme => SetTheme(setTheme),
        SetStartupCommand startup => SetStartup(startup),
        SetHotkeyCommand hotkey => SetHotkey(hotkey),
        CheckUpdateCommand => await CheckUpdateAsync(cancellationToken),
        DownloadUpdateCommand => StartUpdateDownload(cancellationToken),
        CancelUpdateCommand => CancelUpdate(),
        ExportDiagnosticsCommand => await ExportDiagnosticsAsync(cancellationToken),
        _ => throw new InvalidOperationException("Unsupported desktop workbench command."),
      };
      return new WorkbenchCommandOutcome([state], null);
    }
    catch (OperationCanceledException)
    {
      throw;
    }
    catch (Exception error) when (
      error is IOException or UnauthorizedAccessException or InvalidOperationException or
        ClipboardBusyException)
    {
      return new WorkbenchCommandOutcome(
        [],
        new WorkbenchProblem(
          "desktop_command_failed",
          WorkbenchProblemCategory.Unavailable,
          true,
          "workbench.error.desktopCommandFailed"));
    }
  }

  private RecognitionWorkbenchState StartRecognition(
    Func<RecognitionViewModel, Task> action,
    CancellationToken cancellationToken)
  {
    recognition ??= recognitionFactory();
    long generation = Interlocked.Increment(ref recognitionGeneration);
    Track(CompleteRecognitionAsync(action, generation, cancellationToken));
    return new RecognitionWorkbenchState(true, "recognition.running");
  }

  private async Task CompleteRecognitionAsync(
    Func<RecognitionViewModel, Task> action,
    long generation,
    CancellationToken cancellationToken)
  {
    try
    {
      RecognitionWorkbenchState state = await RunRecognitionAsync(action, cancellationToken);
      if (generation == Volatile.Read(ref recognitionGeneration))
      {
        StateChanged?.Invoke(state);
      }
    }
    catch (OperationCanceledException)
    {
      if (generation == Volatile.Read(ref recognitionGeneration))
      {
        StateChanged?.Invoke(new RecognitionWorkbenchState(
          false,
          "recognition.cancelled"));
      }
    }
    catch (Exception error)
    {
      AppLog.Error("Recognition workbench operation failed", error);
      if (generation == Volatile.Read(ref recognitionGeneration))
      {
        StateChanged?.Invoke(new RecognitionWorkbenchState(false, "recognition.failed"));
      }
    }
  }

  private async Task<RecognitionWorkbenchState> RunRecognitionAsync(
    Func<RecognitionViewModel, Task> action,
    CancellationToken cancellationToken)
  {
    recognition ??= recognitionFactory();
    await action(recognition);
    WorkbenchResourceReference? input = null;
    if (recognition.CurrentInput is { } currentInput)
    {
      input = await PublishBytesAsync(
        currentInput.Data,
        currentInput.MediaType,
        ExtensionForMediaType(currentInput.MediaType),
        cancellationToken);
    }
    WorkbenchResourceReference? result = string.IsNullOrEmpty(recognition.ResultText)
      ? null
      : await PublishBytesAsync(
        Encoding.UTF8.GetBytes(recognition.ResultText),
        "text/plain; charset=utf-8",
        ".txt",
        cancellationToken);
    if (recognition.Result is not null)
    {
      resultActions = recognition.CreateResultActions(
        new WindowsResultActionPlatform(windowHandle));
    }
    return new RecognitionWorkbenchState(
      recognition.IsBusy,
      RecognitionStatusCode(recognition),
      input,
      result);
  }

  private RecognitionWorkbenchState CancelRecognition()
  {
    recognition ??= recognitionFactory();
    Interlocked.Increment(ref recognitionGeneration);
    recognition.Cancel();
    return new RecognitionWorkbenchState(
      false,
      "recognition.cancelled",
      null,
      null);
  }

  private async Task<RecognitionWorkbenchState> CopyRecognitionAsync(
    CopyRecognitionResultCommand command,
    CancellationToken cancellationToken)
  {
    ResultActions actions = resultActions ??
      throw new InvalidOperationException("No recognition result is available.");
    ResultCopyFormat format = command.Format switch
    {
      "rich" => ResultCopyFormat.Rich,
      "markdown" => ResultCopyFormat.Markdown,
      _ => ResultCopyFormat.Plain,
    };
    await actions.CopyAsync(format, cancellationToken);
    return await CurrentRecognitionStateAsync("recognition.copied", cancellationToken);
  }

  private async Task<RecognitionWorkbenchState> ExportRecognitionAsync(
    ExportRecognitionResultCommand command,
    CancellationToken cancellationToken)
  {
    ResultActions actions = resultActions ??
      throw new InvalidOperationException("No recognition result is available.");
    ResultExportFormat format = command.Format switch
    {
      "docx" => ResultExportFormat.Docx,
      "html" => ResultExportFormat.Html,
      "markdown" => ResultExportFormat.Markdown,
      "xlsx" => ResultExportFormat.Xlsx,
      _ => ResultExportFormat.Text,
    };
    await actions.ExportAsync(format, cancellationToken);
    return await CurrentRecognitionStateAsync("recognition.exported", cancellationToken);
  }

  private async Task<RecognitionWorkbenchState> CurrentRecognitionStateAsync(
    string statusCode,
    CancellationToken cancellationToken)
  {
    RecognitionViewModel viewModel = recognition ??
      throw new InvalidOperationException("Recognition is unavailable.");
    WorkbenchResourceReference? input = viewModel.CurrentInput is { } currentInput
      ? await PublishBytesAsync(
        currentInput.Data,
        currentInput.MediaType,
        ExtensionForMediaType(currentInput.MediaType),
        cancellationToken)
      : null;
    WorkbenchResourceReference? result = string.IsNullOrEmpty(viewModel.ResultText)
      ? null
      : await PublishBytesAsync(
        Encoding.UTF8.GetBytes(viewModel.ResultText),
        "text/plain; charset=utf-8",
        ".txt",
        cancellationToken);
    return new RecognitionWorkbenchState(false, statusCode, input, result);
  }

  private async Task<BatchWorkbenchState> AddBatchFilesAsync(
    CancellationToken cancellationToken)
  {
    batch ??= batchFactory();
    await batch.PickFilesAsync(cancellationToken);
    return BatchState(batch);
  }

  private BatchWorkbenchState AddDroppedBatchFiles(
    AddDroppedBatchFilesCommand command)
  {
    batch ??= batchFactory();
    batch.AddFiles(command.Paths);
    return BatchState(batch);
  }

  private async Task<BatchWorkbenchState> ExportBatchAsync(
    ExportBatchCommand command,
    CancellationToken cancellationToken)
  {
    batch ??= batchFactory();
    var picker = new FolderPicker
    {
      SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
    };
    picker.FileTypeFilter.Add("*");
    WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle());
    StorageFolder? folder = await picker.PickSingleFolderAsync();
    if (folder is not null)
    {
      await batch.ExportAllAsync(folder.Path, command.Format, cancellationToken);
    }
    return BatchState(batch);
  }

  private async Task<BatchWorkbenchState> StartBatchAsync(
    CancellationToken cancellationToken)
  {
    batch ??= batchFactory();
    await batch.StartAsync(cancellationToken);
    return BatchState(batch);
  }

  private BatchWorkbenchState StartBatch(CancellationToken cancellationToken)
  {
    batch ??= batchFactory();
    long generation = Interlocked.Increment(ref batchGeneration);
    Track(CompleteBatchAsync(generation, cancellationToken));
    return new BatchWorkbenchState(
      true,
      batch.Items.Count,
      batch.CompletedCount,
      batch.FailedCount);
  }

  private async Task CompleteBatchAsync(
    long generation,
    CancellationToken cancellationToken)
  {
    try
    {
      BatchWorkbenchState state = await StartBatchAsync(cancellationToken);
      if (generation == Volatile.Read(ref batchGeneration))
      {
        StateChanged?.Invoke(state);
      }
    }
    catch (OperationCanceledException)
    {
      if (generation == Volatile.Read(ref batchGeneration))
      {
        StateChanged?.Invoke(BatchState(batch!));
      }
    }
    catch (Exception error)
    {
      AppLog.Error("Batch workbench operation failed", error);
      if (generation == Volatile.Read(ref batchGeneration))
      {
        StateChanged?.Invoke(BatchState(batch!));
      }
    }
  }

  private BatchWorkbenchState CancelBatch()
  {
    batch ??= batchFactory();
    Interlocked.Increment(ref batchGeneration);
    batch.CancelAll();
    return BatchState(batch);
  }

  private BatchWorkbenchState ClearBatch()
  {
    batch ??= batchFactory();
    Interlocked.Increment(ref batchGeneration);
    batch.ResetTemporaryQueue();
    batchWindowStart = 0;
    return BatchState(batch);
  }

  private BatchWorkbenchState MoveBatchItem(MoveBatchItemCommand command)
  {
    batch ??= batchFactory();
    if (batch.IsRunning)
    {
      throw new InvalidOperationException("A running batch cannot be reordered.");
    }
    batch.Move(command.ItemId, command.Delta);
    return BatchState(batch);
  }

  private BatchWorkbenchState RemoveBatchItem(RemoveBatchItemCommand command)
  {
    batch ??= batchFactory();
    batch.Remove(command.ItemId);
    return BatchState(batch);
  }

  private BatchWorkbenchState SetBatchConcurrency(
    SetBatchConcurrencyCommand command)
  {
    batch ??= batchFactory();
    if (batch.IsRunning)
    {
      throw new InvalidOperationException(
        "Batch concurrency cannot change while a batch is running.");
    }
    batch.Concurrency = command.Concurrency;
    return BatchState(batch);
  }

  private BatchWorkbenchState SetBatchWindow(SetBatchWindowCommand command)
  {
    batch ??= batchFactory();
    batchWindowStart = ClampWindowStart(command.Start, batch.Items.Count, 40);
    return BatchState(batch);
  }

  private async Task<PdfWorkbenchState> OpenPdfAsync(CancellationToken cancellationToken)
  {
    pdf ??= pdfFactory();
    Interlocked.Increment(ref pdfGeneration);
    await pdf.OpenAsync(cancellationToken);
    ResetPdfSelection(selectFirstPage: true);
    return await PdfStateAsync(pdf, cancellationToken);
  }

  private async Task<PdfWorkbenchState> OpenDroppedPdfAsync(
    OpenDroppedPdfCommand command,
    CancellationToken cancellationToken)
  {
    pdf ??= pdfFactory();
    Interlocked.Increment(ref pdfGeneration);
    await pdf.OpenPathAsync(command.Path, cancellationToken);
    ResetPdfSelection(selectFirstPage: true);
    return await PdfStateAsync(pdf, cancellationToken);
  }

  private async Task<PdfWorkbenchState> RotatePdfAsync(
    RotatePdfCommand command,
    CancellationToken cancellationToken)
  {
    pdf ??= pdfFactory();
    Interlocked.Increment(ref pdfGeneration);
    PdfViewModel viewModel = pdf;
    int[] pages = SelectedPdfPages(viewModel);
    if (pages.Length > 0)
    {
      await viewModel.RotateAsync(pages, command.Degrees, cancellationToken);
      pdfThumbnails.Clear();
    }
    return await PdfStateAsync(viewModel, cancellationToken);
  }

  private PdfWorkbenchState ClosePdf()
  {
    pdf ??= pdfFactory();
    Interlocked.Increment(ref pdfGeneration);
    pdf.CloseSession();
    ResetPdfSelection(selectFirstPage: false);
    return PdfState(pdf);
  }

  private async Task<PdfWorkbenchState> DeletePdfPagesAsync(
    CancellationToken cancellationToken)
  {
    pdf ??= pdfFactory();
    Interlocked.Increment(ref pdfGeneration);
    PdfViewModel viewModel = pdf;
    int[] pages = SelectedPdfPages(viewModel);
    if (pages.Length > 0)
    {
      await viewModel.DeletePagesAsync(pages, cancellationToken);
      ResetPdfSelection(selectFirstPage: true);
    }
    return await PdfStateAsync(viewModel, cancellationToken);
  }

  private async Task<PdfWorkbenchState> OcrPdfPagesAsync(
    CancellationToken cancellationToken)
  {
    pdf ??= pdfFactory();
    PdfViewModel viewModel = pdf;
    int[] pages = SelectedPdfPages(viewModel);
    if (pages.Length > 0)
    {
      await viewModel.StartOcrAsync(pages, overwrite: false, cancellationToken);
    }
    return PdfState(viewModel);
  }

  private PdfWorkbenchState StartPdfOcr(CancellationToken cancellationToken)
  {
    pdf ??= pdfFactory();
    long generation = Interlocked.Increment(ref pdfGeneration);
    Track(CompletePdfOcrAsync(generation, cancellationToken));
    return PdfState(pdf) with { IsBusy = true };
  }

  private async Task CompletePdfOcrAsync(
    long generation,
    CancellationToken cancellationToken)
  {
    try
    {
      PdfWorkbenchState state = await OcrPdfPagesAsync(cancellationToken);
      if (generation == Volatile.Read(ref pdfGeneration))
      {
        StateChanged?.Invoke((await PdfStateAsync(pdf!, cancellationToken)) with
        {
          StatusCode = state.StatusCode,
        });
      }
    }
    catch (OperationCanceledException)
    {
      if (generation == Volatile.Read(ref pdfGeneration))
      {
        StateChanged?.Invoke(PdfState(pdf!));
      }
    }
    catch (Exception error)
    {
      AppLog.Error("PDF OCR workbench operation failed", error);
      if (generation == Volatile.Read(ref pdfGeneration))
      {
        StateChanged?.Invoke(PdfState(pdf!));
      }
    }
  }

  private async Task<PdfWorkbenchState> SavePdfAsync(
    CancellationToken cancellationToken)
  {
    pdf ??= pdfFactory();
    var picker = new FileSavePicker
    {
      SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
      SuggestedFileName = "vibeocr-output",
    };
    picker.FileTypeChoices.Add("PDF", [".pdf"]);
    WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle());
    StorageFile? file = await picker.PickSaveFileAsync();
    if (file is not null)
    {
      await pdf.SaveAsync(file.Path, cancellationToken);
    }
    return PdfState(pdf);
  }

  private PdfWorkbenchState SelectPdfPages(SelectPdfPagesCommand command)
  {
    pdf ??= pdfFactory();
    if (command.Pages.Any(page => page < 0 || page >= pdf.PageCount))
    {
      throw new InvalidOperationException("The PDF page selection is stale.");
    }
    selectedPdfPages.Clear();
    foreach (int page in command.Pages)
    {
      selectedPdfPages.Add(page);
    }
    pdf.SelectedPage = selectedPdfPages.Order().FirstOrDefault(-1);
    return PdfState(pdf);
  }

  private async Task<PdfWorkbenchState> SetPdfWindowAsync(
    SetPdfWindowCommand command,
    CancellationToken cancellationToken)
  {
    pdf ??= pdfFactory();
    pdfWindowStart = ClampWindowStart(command.Start, pdf.PageCount, 64);
    return await PdfStateAsync(pdf, cancellationToken);
  }

  private QrCodeWorkbenchState StartQrCode(
    Func<QrCodeViewModel, Task> action,
    bool publishGeneratedImage,
    CancellationToken cancellationToken)
  {
    qrCode ??= qrCodeFactory();
    long generation = Interlocked.Increment(ref qrCodeGeneration);
    Track(CompleteQrCodeAsync(
      action,
      publishGeneratedImage,
      generation,
      cancellationToken));
    return QrCodeState(qrCode) with
    {
      IsBusy = true,
      StatusCode = "qrcode.running",
    };
  }

  private async Task CompleteQrCodeAsync(
    Func<QrCodeViewModel, Task> action,
    bool publishGeneratedImage,
    long generation,
    CancellationToken cancellationToken)
  {
    try
    {
      await action(qrCode!);
      if (generation != Volatile.Read(ref qrCodeGeneration))
      {
        return;
      }
      WorkbenchResourceReference? nextGeneratedResource = generatedQrResource;
      if (publishGeneratedImage)
      {
        nextGeneratedResource = null;
        if (!string.IsNullOrWhiteSpace(qrCode!.GeneratedImageBase64))
        {
          nextGeneratedResource = await PublishBytesAsync(
            Convert.FromBase64String(qrCode.GeneratedImageBase64),
            "image/png",
            ".png",
            cancellationToken);
        }
      }
      if (generation == Volatile.Read(ref qrCodeGeneration))
      {
        generatedQrResource = nextGeneratedResource;
        StateChanged?.Invoke(QrCodeState(qrCode!));
      }
    }
    catch (OperationCanceledException)
    {
      if (generation == Volatile.Read(ref qrCodeGeneration))
      {
        StateChanged?.Invoke(QrCodeState(qrCode!) with
        {
          IsBusy = false,
          StatusCode = "qrcode.cancelled",
        });
      }
    }
    catch (Exception error)
    {
      AppLog.Error("QR code workbench operation failed", error);
      if (generation == Volatile.Read(ref qrCodeGeneration))
      {
        StateChanged?.Invoke(QrCodeState(qrCode!) with
        {
          IsBusy = false,
          StatusCode = "qrcode.failed",
        });
      }
    }
  }

  private QrCodeWorkbenchState CancelQrCode()
  {
    qrCode ??= qrCodeFactory();
    Interlocked.Increment(ref qrCodeGeneration);
    qrCode.Cancel();
    return QrCodeState(qrCode) with
    {
      IsBusy = false,
      StatusCode = "qrcode.cancelled",
    };
  }

  private QrCodeWorkbenchState ClearQrCode()
  {
    qrCode ??= qrCodeFactory();
    Interlocked.Increment(ref qrCodeGeneration);
    qrCode.Cancel();
    qrCode.Codes.Clear();
    qrCode.ReleaseGeneratedImage();
    generatedQrResource = null;
    return QrCodeState(qrCode);
  }

  private async Task<QrCodeWorkbenchState> SaveQrCodeAsync(
    CancellationToken cancellationToken)
  {
    qrCode ??= qrCodeFactory();
    if (!string.IsNullOrEmpty(qrCode.GeneratedImageBase64))
    {
      var commands = new QrCodeSaveCommands(new QrCodeSavePlatform(windowHandle));
      await commands.SaveAsync(
        qrCode.GeneratedImageBase64,
        "qrcode.png",
        cancellationToken);
    }
    return QrCodeState(qrCode);
  }

  private async Task<QrCodeWorkbenchState> OpenQrCodeUrlAsync(
    OpenQrCodeUrlCommand command,
    CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    qrCode ??= qrCodeFactory();
    bool wasDecoded = qrCode.Codes.Any(code =>
      code.IsUrl is true &&
      string.Equals(code.Data, command.Url, StringComparison.Ordinal));
    if (!wasDecoded || !TryParseAllowedQrUri(command.Url, out Uri? uri))
    {
      throw new InvalidOperationException("Only a decoded HTTP URL can be opened.");
    }
    if (!await Launcher.LaunchUriAsync(uri!))
    {
      throw new InvalidOperationException("Windows could not open the decoded URL.");
    }
    return QrCodeState(qrCode);
  }

  private async Task<AboutWorkbenchState> OpenProjectPageAsync(
    CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    if (!await Launcher.LaunchUriAsync(shell.Value.ProjectUri))
    {
      throw new InvalidOperationException("Windows could not open the project page.");
    }
    return AboutState();
  }

  private async Task<SettingsWorkbenchState> RefreshRuntimeAsync(
    CancellationToken cancellationToken)
  {
    settings ??= settingsFactory();
    await settings.LoadSnapshotAsync(cancellationToken);
    return SettingsState(settings);
  }

  private SettingsWorkbenchState SetTheme(SetThemeCommand command)
  {
    theme = command.Theme;
    return settings is null
      ? new SettingsWorkbenchState(
        theme,
        false,
        "settings.ready",
        "unknown",
        shell.Value.StartWithSystem,
        shell.Value.RegisteredHotkey)
      : SettingsState(settings);
  }

  private SettingsWorkbenchState SetStartup(SetStartupCommand command)
  {
    settings ??= settingsFactory();
    shell.Value.SetStartWithSystem(command.Enabled);
    return SettingsState(settings);
  }

  private SettingsWorkbenchState SetHotkey(SetHotkeyCommand command)
  {
    settings ??= settingsFactory();
    shell.Value.PendingHotkey = command.Hotkey;
    shell.Value.ApplyHotkey();
    return SettingsState(settings);
  }

  private async Task<UpdateWorkbenchState> CheckUpdateAsync(
    CancellationToken cancellationToken)
  {
    Interlocked.Increment(ref updateGeneration);
    await update.Value.CheckAsync(cancellationToken);
    return new UpdateWorkbenchState(
      update.Value.IsBusy,
      update.Value.StatusCode,
      update.Value.LatestVersion,
      update.Value.UpdateAvailable);
  }

  private async Task<UpdateWorkbenchState> DownloadUpdateAsync(
    CancellationToken cancellationToken)
  {
    await update.Value.DownloadAndApplyAsync(cancellationToken);
    return UpdateState();
  }

  private UpdateWorkbenchState StartUpdateDownload(CancellationToken cancellationToken)
  {
    long generation = Interlocked.Increment(ref updateGeneration);
    Track(CompleteUpdateDownloadAsync(generation, cancellationToken));
    return UpdateState() with { IsBusy = true };
  }

  private async Task CompleteUpdateDownloadAsync(
    long generation,
    CancellationToken cancellationToken)
  {
    try
    {
      UpdateWorkbenchState state = await DownloadUpdateAsync(cancellationToken);
      if (generation == Volatile.Read(ref updateGeneration))
      {
        StateChanged?.Invoke(state);
      }
    }
    catch (OperationCanceledException)
    {
      if (generation == Volatile.Read(ref updateGeneration))
      {
        StateChanged?.Invoke(UpdateState());
      }
    }
    catch (Exception error)
    {
      AppLog.Error("Update workbench operation failed", error);
      if (generation == Volatile.Read(ref updateGeneration))
      {
        StateChanged?.Invoke(UpdateState());
      }
    }
  }

  private void Track(Task operation)
  {
    lock (backgroundOperations)
    {
      backgroundOperations.Add(operation);
    }
    _ = operation.ContinueWith(
      completed =>
      {
        lock (backgroundOperations)
        {
          backgroundOperations.Remove(completed);
        }
      },
      CancellationToken.None,
      TaskContinuationOptions.ExecuteSynchronously,
      TaskScheduler.Default);
  }

  private UpdateWorkbenchState CancelUpdate()
  {
    Interlocked.Increment(ref updateGeneration);
    update.Value.Cancel();
    return UpdateState();
  }

  private async Task<DiagnosticsWorkbenchState> ExportDiagnosticsAsync(
    CancellationToken cancellationToken)
  {
    string destination = Path.Combine(
      Path.GetDirectoryName(resourceRoot) ?? resourceRoot,
      $"vibeocr-diagnostics-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
    await diagnostics.ExportAsync(destination, cancellationToken);
    return DiagnosticsState();
  }

  private async Task<WorkbenchResourceReference> PublishBytesAsync(
    byte[] data,
    string mediaType,
    string extension,
    CancellationToken cancellationToken)
  {
    string relative = Path.Combine("session", $"{Guid.NewGuid():N}{extension}");
    string destination = Path.Combine(resourceRoot, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    await File.WriteAllBytesAsync(destination, data, cancellationToken);
    generatedFiles.Add(destination);
    WorkbenchResourceLease lease = resourceBroker.Lease(
      relative,
      mediaType,
      TimeSpan.FromHours(1));
    return new WorkbenchResourceReference(
      lease.Uri.AbsoluteUri,
      mediaType,
      data.LongLength);
  }

  private BatchWorkbenchState BatchState(BatchViewModel viewModel)
  {
    batchWindowStart = ClampWindowStart(batchWindowStart, viewModel.Items.Count, 40);
    return new BatchWorkbenchState(
    viewModel.IsRunning,
    viewModel.Items.Count,
    viewModel.CompletedCount,
    viewModel.FailedCount,
    viewModel.Items
      .Skip(batchWindowStart)
      .Take(40)
      .Select(item => new BatchWorkbenchItem(
        item.Id,
        Truncate(item.Name, 80),
        $"batch.item.{item.State.ToString().ToLowerInvariant()}",
        item.Result is null ? null : Truncate(item.Result.Text, 120)))
      .ToArray(),
    viewModel.Concurrency,
    batchWindowStart);
  }

  private PdfWorkbenchState PdfState(PdfViewModel viewModel)
  {
    pdfWindowStart = ClampWindowStart(pdfWindowStart, viewModel.PageCount, 64);
    return new PdfWorkbenchState(
      viewModel.IsBusy,
      viewModel.PageCount > 0 ? "pdf.open" : "pdf.empty",
      viewModel.PageCount,
      viewModel.SelectedPage,
      selectedPdfPages.Order().ToArray(),
      Enumerable.Range(
        pdfWindowStart,
        Math.Min(viewModel.PageCount - pdfWindowStart, 64))
      .Select(index => new PdfWorkbenchPage(
        index,
        PdfPageStatus(viewModel, index),
        pdfThumbnails.GetValueOrDefault(index)))
      .ToArray(),
      pdfWindowStart);
  }

  private async Task<PdfWorkbenchState> PdfStateAsync(
    PdfViewModel viewModel,
    CancellationToken cancellationToken)
  {
    pdfWindowStart = ClampWindowStart(pdfWindowStart, viewModel.PageCount, 64);
    int visiblePageEnd = Math.Min(viewModel.PageCount, pdfWindowStart + 64);
    for (int index = pdfWindowStart; index < visiblePageEnd; index++)
    {
      if (pdfThumbnails.ContainsKey(index)) continue;
      byte[]? thumbnail = await viewModel.RenderThumbnailAsync(index, cancellationToken);
      if (thumbnail is { Length: > 0 })
      {
        pdfThumbnails[index] = await PublishBytesAsync(
          thumbnail,
          "image/png",
          ".png",
          cancellationToken);
      }
    }
    return PdfState(viewModel);
  }

  private int[] SelectedPdfPages(PdfViewModel viewModel) => selectedPdfPages
    .Where(page => page >= 0 && page < viewModel.PageCount)
    .Order()
    .ToArray();

  private void ResetPdfSelection(bool selectFirstPage)
  {
    selectedPdfPages.Clear();
    pdfThumbnails.Clear();
    pdfWindowStart = 0;
    if (selectFirstPage && pdf is { PageCount: > 0 })
    {
      selectedPdfPages.Add(0);
      pdf.SelectedPage = 0;
    }
    else if (pdf is not null)
    {
      pdf.SelectedPage = -1;
    }
  }

  private static string PdfPageStatus(PdfViewModel viewModel, int index) =>
    index < viewModel.Pages.Count
      ? $"pdf.page.{viewModel.Pages[index].State.ToString().ToLowerInvariant()}"
      : "pdf.page.none";

  private QrCodeWorkbenchState QrCodeState(QrCodeViewModel viewModel)
  {
    QrCodeWorkbenchResult[] items = viewModel.Codes
      .Take(4)
      .Select(code =>
      {
        bool isComplete = code.Data.Length <= 2048;
        bool isOpenableUrl = isComplete && code.IsUrl is true &&
          TryParseAllowedQrUri(code.Data, out _);
        return new QrCodeWorkbenchResult(
          Truncate(code.Data, 2048),
          Truncate(code.Format, 40),
          isOpenableUrl);
      })
      .ToArray();
    return new QrCodeWorkbenchState(
      viewModel.IsBusy,
      items.Length > 0 ? "qrcode.decoded" : "qrcode.ready",
      [],
      generatedQrResource,
      items);
  }

  private SettingsWorkbenchState SettingsState(SettingsViewModel viewModel) => new(
    theme,
    viewModel.IsBusy,
    viewModel.RestartRequired ? "settings.restartRequired" : "settings.ready",
    viewModel.Backend,
    shell.Value.StartWithSystem,
    shell.Value.RegisteredHotkey);

  private UpdateWorkbenchState UpdateState() => new(
    update.Value.IsBusy,
    update.Value.StatusCode,
    update.Value.LatestVersion,
    update.Value.UpdateAvailable);

  private AboutWorkbenchState AboutState() => new(
    shell.Value.AppVersion,
    shell.Value.License,
    shell.Value.ProjectUri.AbsoluteUri);

  private DiagnosticsWorkbenchState DiagnosticsState() => new(
    diagnostics.SupervisorStatus,
    diagnostics.ProtocolStatus,
    diagnostics.IsReady,
    diagnostics.Milestones
      .OrderBy(milestone => milestone.Name)
      .Select(milestone => milestone.Name)
      .ToArray());

  private static string RecognitionStatusCode(RecognitionViewModel viewModel) =>
    viewModel.IsBusy
      ? "recognition.running"
      : viewModel.HasResult
        ? "recognition.completed"
        : "recognition.ready";

  private static string ExtensionForMediaType(string mediaType) => mediaType switch
  {
    "image/jpeg" => ".jpg",
    "image/bmp" => ".bmp",
    "image/webp" => ".webp",
    "image/gif" => ".gif",
    _ => ".png",
  };

  private static string Truncate(string value, int maximumLength) =>
    value.Length <= maximumLength
      ? value
      : string.Concat(value.AsSpan(0, maximumLength - 1), "…");

  private static int ClampWindowStart(int requested, int count, int windowSize)
  {
    if (count <= 0) return 0;
    int maximumStart = ((count - 1) / windowSize) * windowSize;
    return Math.Clamp(requested, 0, maximumStart);
  }

  private static bool TryParseAllowedQrUri(string value, out Uri? uri)
  {
    uri = null;
    bool parsed = value.Length <= 2048 &&
      Uri.TryCreate(value, UriKind.Absolute, out uri);
    return parsed && uri is not null && uri.Scheme is "http" or "https" &&
      string.IsNullOrEmpty(uri.UserInfo);
  }

  public async ValueTask DisposeAsync()
  {
    if (Interlocked.Exchange(ref disposed, 1) != 0)
    {
      return;
    }
    Interlocked.Increment(ref recognitionGeneration);
    Interlocked.Increment(ref batchGeneration);
    Interlocked.Increment(ref pdfGeneration);
    Interlocked.Increment(ref qrCodeGeneration);
    Interlocked.Increment(ref updateGeneration);
    recognition?.Cancel();
    batch?.CancelAll();
    qrCode?.Cancel();
    pdf?.Cancel();
    if (update.IsValueCreated)
    {
      update.Value.Cancel();
    }
    Task[] operations;
    lock (backgroundOperations)
    {
      operations = backgroundOperations.ToArray();
    }
    await Task.WhenAll(operations).ConfigureAwait(false);
    foreach (string file in generatedFiles)
    {
      try
      {
        File.Delete(file);
      }
      catch (IOException)
      {
      }
      catch (UnauthorizedAccessException)
      {
      }
    }
    generatedFiles.Clear();
  }
}

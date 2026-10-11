using VibeOCR.App.Features.QrCode;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Services;
using Windows.System;

namespace VibeOCR.App.Workbench;

internal sealed class WorkbenchQrCodeOwner(IAnnotatedImagePlatform annotatedImagePlatform, Func<QrCodeViewModel> qrCodeFactory, Func<nint> windowHandle, WorkbenchResourcesOwner resources, WorkbenchOperations operations)
{
  internal bool Handles(WorkbenchCommand command) => command is GenerateQrCodeCommand or CopyQrCodeImageCommand or DecodeCurrentQrCodeCommand or DecodeQrCodeCommand or DecodeDroppedQrCodeCommand or DecodeQrCodeClipboardCommand or CancelQrCodeCommand or ClearQrCodeCommand or SaveQrCodeCommand or OpenQrCodeUrlCommand;

  internal async ValueTask<WorkbenchState?> ExecuteAsync(WorkbenchCommand command, CancellationToken cancellationToken) => command switch
  {
    GenerateQrCodeCommand generate => StartQrCode(
      viewModel =>
      {
        viewModel.GenerateText = generate.Text;
        viewModel.GenerateFormat = generate.Format;
        viewModel.CaptionMode = generate.CaptionMode switch { "payload" => QrCodeCaptionMode.Payload, "custom" => QrCodeCaptionMode.Custom, _ => QrCodeCaptionMode.Off };
        viewModel.CaptionText = generate.CaptionText;
        return viewModel.GenerateAsync(cancellationToken);
      },
      publishGeneratedImage: true,
      cancellationToken),
    CopyQrCodeImageCommand => await CopyQrCodeImageAsync(cancellationToken),
    DecodeCurrentQrCodeCommand current => StartQrCode(
      viewModel => viewModel.DecodeCurrentPreviewAsync(current.Force, cancellationToken),
      publishGeneratedImage: false,
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
    CancelQrCodeCommand => await CancelQrCodeAsync(cancellationToken),
    ClearQrCodeCommand => ClearQrCode(),
    SaveQrCodeCommand => await SaveQrCodeAsync(cancellationToken),
    OpenQrCodeUrlCommand openUrl => await OpenQrCodeUrlAsync(
      openUrl,
      cancellationToken),
    _ => throw new InvalidOperationException("Unsupported QrCode workbench command."),
  };

  private QrCodeViewModel? qrCode;
  private WorkbenchResourceReference? generatedQrResource;
  private long qrCodeGeneration;
  private long publishedQrRevision;
  private string? qrPreviewPath;
  private QrCodeWorkbenchState? StartQrCode(
    Func<QrCodeViewModel, Task> action,
    bool publishGeneratedImage,
    CancellationToken cancellationToken)
  {
    qrCode ??= qrCodeFactory();
    long generation = Interlocked.Increment(ref qrCodeGeneration);
    QrCodeWorkbenchState start = QrCodeState(qrCode) with
    {
      IsBusy = true,
      StatusCode = "qrcode.running",
    };
    return operations.Start(start,
      () => CompleteQrCodeAsync(
        action, publishGeneratedImage, generation, cancellationToken));
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
      long previewRevision = qrCode!.PreviewRevision;
      string? nextPreviewPath = qrPreviewPath;
      if (previewRevision != publishedQrRevision && qrCode.GeneratedImageBase64 is { } preview)
      {
        (nextGeneratedResource, nextPreviewPath) = await resources.PublishFileAsync(
          Convert.FromBase64String(preview), qrCode.PreviewMediaType,
          qrCode.PreviewMediaType == "image/jpeg" ? ".jpg" : ".png", cancellationToken);
      }
      if (generation == Volatile.Read(ref qrCodeGeneration))
      {
        generatedQrResource = nextGeneratedResource;
        qrPreviewPath = nextPreviewPath;
        publishedQrRevision = previewRevision;
        QrCodeViewModel currentQrCode = qrCode!;
        QrCodeWorkbenchState state = QrCodeState(currentQrCode);
        operations.PublishTerminal(!publishGeneratedImage && currentQrCode.DecodeUnavailable
          ? state with { StatusCode = "qrcode.decodeUnavailable" }
          : publishGeneratedImage && currentQrCode.GenerateInvalidInput
            ? state with { StatusCode = "qrcode.invalidInput" }
          : (publishGeneratedImage && currentQrCode.GenerateFailed) ||
            (!publishGeneratedImage && currentQrCode.DecodeFailed)
            ? state with { StatusCode = publishGeneratedImage ? "qrcode.generateFailed" : "qrcode.failed" }
            : state);
      }
    }
    catch (OperationCanceledException)
    {
      if (generation == Volatile.Read(ref qrCodeGeneration))
      {
        operations.PublishTerminal(QrCodeState(qrCode!) with
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
        operations.PublishTerminal(QrCodeState(qrCode!) with
        {
          IsBusy = false,
          StatusCode = publishGeneratedImage ? "qrcode.generateFailed" : "qrcode.failed",
        });
      }
    }
  }

  private async Task<QrCodeWorkbenchState> CancelQrCodeAsync(CancellationToken cancellationToken)
  {
    qrCode ??= qrCodeFactory();
    Interlocked.Increment(ref qrCodeGeneration);
    qrCode.Cancel();
    if (qrCode.HasPreview && publishedQrRevision != qrCode.PreviewRevision)
      await CompleteQrCodeAsync(_ => Task.CompletedTask, false, Volatile.Read(ref qrCodeGeneration), cancellationToken);
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
    qrPreviewPath = null;
    return QrCodeState(qrCode);
  }

  private async Task<QrCodeWorkbenchState> CopyQrCodeImageAsync(CancellationToken cancellationToken)
  {
    qrCode ??= qrCodeFactory();
    if (qrPreviewPath is null || publishedQrRevision != qrCode.PreviewRevision) throw new InvalidOperationException("当前没有可复制的预览图片。");
    await annotatedImagePlatform.CopyImageAsync(qrPreviewPath, cancellationToken);
    return QrCodeState(qrCode) with { StatusCode = "qrcode.copied" };
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
          DesktopWorkbenchCommandHandler.Truncate(code.Data, 2048),
          DesktopWorkbenchCommandHandler.Truncate(code.Format, 40),
          isOpenableUrl);
      })
      .ToArray();
    return new QrCodeWorkbenchState(
      viewModel.IsBusy,
      items.Length > 0 ? "qrcode.decoded" : viewModel.HasPreview && !viewModel.NeedsPreviewDecode ? "qrcode.noCodes" : "qrcode.ready",
      [],
      generatedQrResource,
      items,
      viewModel.PreviewRevision,
      viewModel.NeedsPreviewDecode,
      viewModel.GenerateInvalidInput ? viewModel.GenerateStatus : viewModel.DecodeStatus);
  }

  private static bool TryParseAllowedQrUri(string value, out Uri? uri)
  {
    uri = null;
    bool parsed = value.Length <= 2048 &&
      Uri.TryCreate(value, UriKind.Absolute, out uri);
    return parsed && uri is not null && uri.Scheme is "http" or "https" &&
      string.IsNullOrEmpty(uri.UserInfo);
  }

  internal void Stop() { Interlocked.Increment(ref qrCodeGeneration); qrCode?.Cancel(); }
}

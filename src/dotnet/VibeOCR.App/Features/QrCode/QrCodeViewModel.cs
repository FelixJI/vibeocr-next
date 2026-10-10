using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.App.Inference;
using VibeOCR.Platform.Inference;
using VibeOCR.App.Services;

namespace VibeOCR.App.Features.QrCode;

public enum QrCodeInputKind { File, Clipboard, DroppedFile }
public sealed record QrCodeInput(byte[] Data, string MediaType, string DisplayName);
public interface IQrCodeInput
{
    Task<QrCodeInput?> PickFileAsync(CancellationToken ct);
    Task<QrCodeInput?> ReadClipboardAsync(CancellationToken ct);
    Task<QrCodeInput?> ReadDroppedFileAsync(string path, CancellationToken ct);
}

/// <summary>
/// QR/barcode workbench state. The current preview (generated image or imported
/// image) is one authoritative byte array guarded by <see cref="_previewRevision"/>;
/// <see cref="GeneratedImageBase64"/> and the decode entry points all map onto it.
/// </summary>
public sealed class QrCodeViewModel(IQrCodeClient qrClient, IQrCodeInput input) : INotifyPropertyChanged
{
    private CancellationTokenSource? _activeRun;
    private readonly object _runLock = new();
    private long _generation;
    private bool _isBusy;
    private string _decodeStatus = "请粘贴或选择图片";
    private string _generateStatus = string.Empty;
    private string _generateText = string.Empty;
    private string _generateFormat = "qrcode";
    private QrCodeCaptionMode _captionMode;
    private string _captionText = string.Empty;
    private byte[]? _previewData;
    private string _previewMediaType = "image/png";
    private long _previewRevision;
    private long _lastDecodedRevision;
    private long _lastAutoDecodeRevision;

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<QrCodeResult> Codes { get; } = [];
    public bool IsBusy { get => _isBusy; private set => SetField(ref _isBusy, value); }
    public string DecodeStatus { get => _decodeStatus; private set => SetField(ref _decodeStatus, value); }
    /// <summary>Base64 of the single authoritative current preview (generated or imported), if any.</summary>
    public string? GeneratedImageBase64 => Volatile.Read(ref _previewData) is byte[] preview
        ? Convert.ToBase64String(preview)
        : null;
    public string PreviewMediaType => _previewMediaType;
    /// <summary>Increments every time the authoritative preview changes; 0 means no preview yet.</summary>
    public long PreviewRevision => Interlocked.Read(ref _previewRevision);
    /// <summary>Revision of the preview that was last really decoded; 0 means never decoded.</summary>
    public long LastDecodedRevision => Interlocked.Read(ref _lastDecodedRevision);
    public bool HasPreview => Volatile.Read(ref _previewData) is not null;
    /// <summary>True when the current preview exists but has not been decoded at its revision yet.</summary>
    public bool NeedsPreviewDecode => HasPreview && LastDecodedRevision != PreviewRevision;
    public string GenerateStatus { get => _generateStatus; private set => SetField(ref _generateStatus, value); }
    public bool GenerateFailed { get; private set; }
    public bool GenerateInvalidInput { get; private set; }
    public bool DecodeFailed { get; private set; }
    public bool DecodeUnavailable { get; private set; }
    public string GenerateText { get => _generateText; set => SetField(ref _generateText, value); }
    public string GenerateFormat { get => _generateFormat; set => SetField(ref _generateFormat, value); }
    public QrCodeCaptionMode CaptionMode { get => _captionMode; set => SetField(ref _captionMode, value); }
    public string CaptionText { get => _captionText; set => SetField(ref _captionText, value); }
    public bool HasCodes => Codes.Count > 0;

    public Task DecodeAsync(QrCodeInputKind kind, CancellationToken ct) => kind switch
    {
        QrCodeInputKind.File => DecodeAsync(input.PickFileAsync, ct),
        QrCodeInputKind.Clipboard => DecodeAsync(input.ReadClipboardAsync, ct),
        QrCodeInputKind.DroppedFile => throw new ArgumentOutOfRangeException(nameof(kind), "DroppedFile requires a path."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public Task DecodeDroppedFileAsync(string path, CancellationToken ct) =>
        DecodeAsync(token => input.ReadDroppedFileAsync(path, token), ct);

    /// <summary>Decode the current preview. Skips when this revision was already decoded; pass <c>true</c> to re-recognize.</summary>
    public Task DecodeCurrentPreviewAsync(CancellationToken ct) => DecodeCurrentPreviewAsync(force: false, ct);

    public async Task DecodeCurrentPreviewAsync(bool force, CancellationToken ct)
    {
        byte[]? preview0 = Volatile.Read(ref _previewData);
        if (preview0 is null)
        {
            DecodeStatus = "当前没有可识别的预览图片";
            return;
        }
        long currentRevision = PreviewRevision;
        if (!force && (!NeedsPreviewDecode || Interlocked.Exchange(ref _lastAutoDecodeRevision, currentRevision) == currentRevision)) return;
        (CancellationTokenSource run, long generation) = BeginRun(ct);
        if (generation == Volatile.Read(ref _generation)) { IsBusy = true; DecodeStatus = "正在识别"; }
        try
        {
            DecodeFailed = false;
            DecodeUnavailable = false;
            // Read the revision before the bytes: a concurrent swap can only leave an
            // older revision with newer bytes, which merely re-queues a decode and
            // never binds an undecoded preview as decoded.
            long revision = Interlocked.Read(ref _previewRevision);
            byte[] preview = Volatile.Read(ref _previewData) ?? preview0;
            await DecodePreviewCoreAsync(preview, revision, run, generation);
        }
        catch (OperationCanceledException) { if (generation == Volatile.Read(ref _generation)) DecodeStatus = "已取消"; }
        catch (InferenceClientNotAttachedException error) { if (generation == Volatile.Read(ref _generation)) { DecodeFailed = true; DecodeUnavailable = true; DecodeStatus = "识别运行环境未就绪"; AppLog.Error("QR preview decode unavailable", error); } }
        catch (InferenceClientException error) { if (generation == Volatile.Read(ref _generation)) { DecodeFailed = true; DecodeUnavailable = error.Code is HttpV2ErrorCode.BackendUnavailable or HttpV2ErrorCode.TransientBackend; DecodeStatus = DecodeUnavailable ? "识别运行环境暂不可用" : "识别失败"; } }
        catch (Exception) when (generation == Volatile.Read(ref _generation)) { DecodeFailed = true; DecodeStatus = "识别失败，请重试"; }
        finally { if (generation == Volatile.Read(ref _generation)) IsBusy = false; EndRun(run); }
    }

    public void Cancel()
    {
        lock (_runLock)
        {
            Interlocked.Increment(ref _generation);
            _activeRun?.Cancel();
        }
        IsBusy = false;
    }
    public IReadOnlyList<QrCodeResult> OpenableUrls() => Codes.Where(c => c.IsUrl is true).ToArray();
    public string CopyAll() => string.Join("\n", Codes.Select(c => c.Data));

    public async Task GenerateAsync(CancellationToken cancellationToken)
    {
        (CancellationTokenSource run, long generation) = BeginRun(cancellationToken);
        try
        {
            IsBusy = true;
            GenerateFailed = false;
            GenerateInvalidInput = false;
            GenerateStatus = "正在生成";
            // Snapshot every user-editable input once: text edited while the run is
            // in flight must never leak into the caption of what was actually encoded.
            string text = GenerateText;
            string format = GenerateFormat;
            QrCodeCaptionMode captionMode = CaptionMode;
            string captionCustom = CaptionText;
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("请输入要编码的内容");
            QrCodeGeneratedImage generated = await qrClient.GenerateAsync(text, format, run.Token);
            run.Token.ThrowIfCancellationRequested();
            if (captionMode != QrCodeCaptionMode.Off)
            {
                // The caption always describes this run's real payload.
                string captionText = captionMode == QrCodeCaptionMode.Payload ? LocalQrCodeGenerator.NormalizePayload(text, format) : captionCustom;
                if (!string.IsNullOrWhiteSpace(captionText))
                {
                    generated = await LocalQrCodeGenerator.AppendCaptionAsync(generated, captionText, run.Token);
                    run.Token.ThrowIfCancellationRequested();
                }
            }
            if (generation == Volatile.Read(ref _generation))
            {
                SetPreview(Convert.FromBase64String(generated.Base64Png), generated.MediaType);
                GenerateStatus = "已生成";
            }
        }
        catch (OperationCanceledException) { if (generation == Volatile.Read(ref _generation)) GenerateStatus = "已取消"; }
        catch (ArgumentException error) { if (generation == Volatile.Read(ref _generation)) { GenerateFailed = true; GenerateInvalidInput = true; GenerateStatus = error.Message; } }
        catch (Exception error)
        {
            AppLog.Error("QR code generation failed", error);
            if (generation == Volatile.Read(ref _generation)) { GenerateFailed = true; GenerateStatus = "二维码生成失败，请重试"; }
        }
        finally
        {
            if (generation == Volatile.Read(ref _generation)) IsBusy = false;
            EndRun(run);
        }
    }

    /// <summary>
    /// Clears the authoritative preview and invalidates any in-flight run so late
    /// results can neither revive the preview nor repopulate decoded results.
    /// (The handler's clear command also cancels first; cancelling here keeps the
    /// guarantee for any direct caller.)
    /// </summary>
    public void ReleaseGeneratedImage()
    {
        lock (_runLock)
        {
            Interlocked.Increment(ref _generation);
            _activeRun?.Cancel();
        }
        IsBusy = false;
        _previewData = null;
        Interlocked.Increment(ref _previewRevision);
        NotifyPreviewChanged();
    }

    private async Task DecodeAsync(Func<CancellationToken, Task<QrCodeInput?>> loadInput, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(loadInput);
        (CancellationTokenSource run, long generation) = BeginRun(cancellationToken);
        if (generation == Volatile.Read(ref _generation)) { IsBusy = true; DecodeStatus = "正在读取输入"; }
        try
        {
            DecodeFailed = false;
            DecodeUnavailable = false;
            QrCodeInput? imageInput = await loadInput(run.Token);
            run.Token.ThrowIfCancellationRequested();
            if (imageInput is null) { if (generation == Volatile.Read(ref _generation)) DecodeStatus = "已取消选择"; return; }
            if (generation != Volatile.Read(ref _generation)) return;
            SetPreview(imageInput.Data, string.IsNullOrWhiteSpace(imageInput.MediaType) ? "image/png" : imageInput.MediaType);
            long revision = Interlocked.Read(ref _previewRevision);
            Interlocked.Exchange(ref _lastAutoDecodeRevision, revision);
            DecodeStatus = "正在识别";
            await DecodePreviewCoreAsync(imageInput.Data, revision, run, generation);
        }
        catch (OperationCanceledException) { if (generation == Volatile.Read(ref _generation)) DecodeStatus = "已取消"; }
        catch (InferenceClientNotAttachedException) { if (generation == Volatile.Read(ref _generation)) { DecodeFailed = true; DecodeUnavailable = true; DecodeStatus = "识别运行环境未就绪"; } }
        catch (InferenceClientException error) { if (generation == Volatile.Read(ref _generation)) { DecodeFailed = true; DecodeUnavailable = error.Code is HttpV2ErrorCode.BackendUnavailable or HttpV2ErrorCode.TransientBackend; DecodeStatus = DecodeUnavailable ? "识别运行环境暂不可用" : "识别失败"; } }
        catch (Exception error) when (error is InvalidDataException or UnauthorizedAccessException or FileNotFoundException) { if (generation == Volatile.Read(ref _generation)) { DecodeFailed = true; DecodeStatus = "无法读取输入图片"; } }
        catch (Exception) when (generation == Volatile.Read(ref _generation)) { DecodeFailed = true; DecodeStatus = "识别失败，请重试"; }
        finally { if (generation == Volatile.Read(ref _generation)) IsBusy = false; EndRun(run); }
    }

    /// <summary>Really decodes the given preview pixels and binds <see cref="LastDecodedRevision"/> only on success.</summary>
    private async Task DecodePreviewCoreAsync(
        byte[] preview, long previewRevision, CancellationTokenSource run, long generation)
    {
        // Raw preview bytes cross the seam; the only remaining base64 use is
        // the wire-compatible HTTP client boundary (#213).
        IReadOnlyList<QrCodeDecodedItem> decoded = await qrClient.DecodeAsync(preview, run.Token);
        run.Token.ThrowIfCancellationRequested();
        // Gate generation AND preview revision together before writing any result:
        // a newer preview (or a cancelled/cleared run) must never be overwritten by
        // this run's stale codes, not even transiently.
        if (generation != Volatile.Read(ref _generation) ||
            previewRevision != Interlocked.Read(ref _previewRevision)) return;
        Codes.Clear();
        foreach (QrCodeDecodedItem item in decoded) Codes.Add(new QrCodeResult { Data = item.Data, Format = item.Format, IsUrl = item.IsUrl });
        DecodeStatus = Codes.Count == 0 ? "未识别到二维码/条形码" : $"识别到 {Codes.Count} 条结果";
        Interlocked.Exchange(ref _lastDecodedRevision, previewRevision);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NeedsPreviewDecode)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCodes)));
    }

    private void SetPreview(byte[] data, string mediaType)
    {
        Codes.Clear();
        Volatile.Write(ref _previewData, data);
        _previewMediaType = mediaType;
        Interlocked.Increment(ref _previewRevision);
        NotifyPreviewChanged();
    }

    private void NotifyPreviewChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GeneratedImageBase64)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPreview)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NeedsPreviewDecode)));
    }

    private (CancellationTokenSource Run, long Generation) BeginRun(CancellationToken cancellationToken)
    {
        var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_runLock)
        {
            long generation = Interlocked.Increment(ref _generation);
            _activeRun?.Cancel();
            _activeRun = run;
            return (run, generation);
        }
    }

    private void EndRun(CancellationTokenSource run)
    {
        lock (_runLock)
        {
            if (ReferenceEquals(_activeRun, run)) _activeRun = null;
            run.Dispose();
        }
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
}

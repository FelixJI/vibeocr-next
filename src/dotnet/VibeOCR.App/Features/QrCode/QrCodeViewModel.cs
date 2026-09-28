using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
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

public sealed class QrCodeViewModel(IQrCodeClient qrClient, IQrCodeInput input) : INotifyPropertyChanged
{
    private CancellationTokenSource? _activeRun;
    private readonly object _runLock = new();
    private long _generation;
    private bool _isBusy;
    private string _decodeStatus = "请粘贴或选择图片";
    private string? _generatedImageBase64;
    private string _generateStatus = string.Empty;
    private string _generateText = string.Empty;
    private string _generateFormat = "qrcode";

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<QrCodeResult> Codes { get; } = [];
    public bool IsBusy { get => _isBusy; private set => SetField(ref _isBusy, value); }
    public string DecodeStatus { get => _decodeStatus; private set => SetField(ref _decodeStatus, value); }
    public string? GeneratedImageBase64 { get => _generatedImageBase64; private set => SetField(ref _generatedImageBase64, value); }
    public string GenerateStatus { get => _generateStatus; private set => SetField(ref _generateStatus, value); }
    public bool GenerateFailed { get; private set; }
    public bool GenerateInvalidInput { get; private set; }
    public bool DecodeFailed { get; private set; }
    public bool DecodeUnavailable { get; private set; }
    public string GenerateText { get => _generateText; set => SetField(ref _generateText, value); }
    public string GenerateFormat { get => _generateFormat; set => SetField(ref _generateFormat, value); }
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
            if (string.IsNullOrWhiteSpace(GenerateText))
                throw new ArgumentException("请输入要编码的内容");
            QrCodeGeneratedImage generated = await qrClient.GenerateAsync(GenerateText, GenerateFormat, run.Token);
            run.Token.ThrowIfCancellationRequested();
            if (generation == Volatile.Read(ref _generation)) { GeneratedImageBase64 = generated.Base64Png; GenerateStatus = "已生成"; }
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

    public void ReleaseGeneratedImage() { GeneratedImageBase64 = null; }

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
            if (generation == Volatile.Read(ref _generation)) DecodeStatus = "正在识别";
            string base64Image = Convert.ToBase64String(imageInput.Data);
            IReadOnlyList<QrCodeDecodedItem> decoded = await qrClient.DecodeAsync(base64Image, run.Token);
            run.Token.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _generation)) return;
            Codes.Clear();
            foreach (QrCodeDecodedItem item in decoded) Codes.Add(new QrCodeResult { Data = item.Data, Format = item.Format, IsUrl = item.IsUrl });
            DecodeStatus = Codes.Count == 0 ? "未识别到二维码/条形码" : $"识别到 {Codes.Count} 条结果";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCodes)));
        }
        catch (OperationCanceledException) { if (generation == Volatile.Read(ref _generation)) DecodeStatus = "已取消"; }
        catch (InferenceClientNotAttachedException) { if (generation == Volatile.Read(ref _generation)) { DecodeFailed = true; DecodeUnavailable = true; DecodeStatus = "识别运行环境未就绪"; } }
        catch (InferenceClientException error) { if (generation == Volatile.Read(ref _generation)) { DecodeFailed = true; DecodeUnavailable = error.Code is HttpV2ErrorCode.BackendUnavailable or HttpV2ErrorCode.TransientBackend; DecodeStatus = DecodeUnavailable ? "识别运行环境暂不可用" : "识别失败"; } }
        catch (Exception error) when (error is InvalidDataException or UnauthorizedAccessException or FileNotFoundException) { if (generation == Volatile.Read(ref _generation)) { DecodeFailed = true; DecodeStatus = "无法读取输入图片"; } }
        catch (Exception) when (generation == Volatile.Read(ref _generation)) { DecodeFailed = true; DecodeStatus = "Supervisor 已断开，请重试"; }
        finally { if (generation == Volatile.Read(ref _generation)) IsBusy = false; EndRun(run); }
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

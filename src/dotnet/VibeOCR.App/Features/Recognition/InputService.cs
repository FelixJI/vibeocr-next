using System.Buffers.Binary;
using Windows.Graphics.Imaging;
using VibeOCR.App.Web;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using VibeOCR.Platform.Windows;

namespace VibeOCR.App.Features.Recognition;

public sealed record RecognitionInput(
    byte[] Data,
    string MediaType,
    string DisplayName,
    string Origin, PhysicalRectangle? CaptureBounds = null)
{
    internal ScreenshotCaptureScene? CaptureScene { get; set; }
    internal ScreenshotCaptureScene? TakeCaptureScene()
    {
        ScreenshotCaptureScene? scene = CaptureScene;
        CaptureScene = null;
        return scene;
    }
    internal void DisposeCaptureScene() => TakeCaptureScene()?.Dispose();
}

public interface IInputService
{
    Task<RecognitionInput?> PickFileAsync(CancellationToken cancellationToken);
    Task<RecognitionInput?> ReadClipboardAsync(CancellationToken cancellationToken);
    Task<RecognitionInput?> CaptureScreenAsync(CancellationToken cancellationToken);
    Task<RecognitionInput?> CaptureScrollingScreenAsync(CancellationToken cancellationToken) =>
        Task.FromException<RecognitionInput?>(new NotSupportedException("Scrolling capture is unavailable."));
    Task<RecognitionInput?> ReadDroppedFileAsync(string path, CancellationToken cancellationToken);
}

public sealed class InputService : IInputService
{
    private const long MaximumInputBytes = 256L << 20;
    private readonly Func<nint> _windowHandle;
    private readonly IScreenRegionPicker _screenRegionPicker;
    private readonly IScreenRegionPicker _scrollingRegionPicker;

    public InputService(Func<nint> windowHandle, IScreenRegionPicker? screenRegionPicker = null,
        IScreenRegionPicker? scrollingRegionPicker = null)
    {
        _windowHandle = windowHandle ?? throw new ArgumentNullException(nameof(windowHandle));
        _screenRegionPicker = screenRegionPicker ?? new ScreenRegionPicker(windowHandle);
        _scrollingRegionPicker = scrollingRegionPicker ?? new ScreenRegionPicker(windowHandle, scrolling: true);
    }

    public async Task<RecognitionInput?> PickFileAsync(CancellationToken cancellationToken)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            ViewMode = PickerViewMode.Thumbnail,
        };
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".bmp");
        picker.FileTypeFilter.Add(".webp");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _windowHandle());
        StorageFile? file = await picker.PickSingleFileAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return file is null ? null : await ReadFileAsync(file.Path, "file", cancellationToken);
    }

    public async Task<RecognitionInput?> ReadClipboardAsync(CancellationToken cancellationToken)
    {
        DataPackageView content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Bitmap))
        {
            throw new InvalidDataException("Clipboard does not contain an image.");
        }

        RandomAccessStreamReference reference = await content.GetBitmapAsync();
        using IRandomAccessStreamWithContentType stream = await reference.OpenReadAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (stream.Size > MaximumInputBytes)
        {
            throw new InvalidDataException("Clipboard image exceeds 256 MiB.");
        }

        using var reader = new DataReader(stream.GetInputStreamAt(0));
        uint size = checked((uint)stream.Size);
        await reader.LoadAsync(size);
        byte[] data = new byte[size];
        reader.ReadBytes(data);
        await ValidateImageAsync(data, cancellationToken);
        return new RecognitionInput(
            data,
            string.IsNullOrWhiteSpace(stream.ContentType) ? "image/png" : stream.ContentType,
            "clipboard",
            "clipboard");
    }

    public Task<RecognitionInput?> CaptureScreenAsync(CancellationToken cancellationToken) =>
        CaptureAsync(_screenRegionPicker, "screenshot", cancellationToken);

    public Task<RecognitionInput?> CaptureScrollingScreenAsync(CancellationToken cancellationToken) =>
        CaptureAsync(_scrollingRegionPicker, "scrolling-screenshot", cancellationToken);

    private static async Task<RecognitionInput?> CaptureAsync(
        IScreenRegionPicker picker, string origin, CancellationToken cancellationToken)
    {
        ScreenRegionSelection? selection = await picker.PickAsync(cancellationToken);
        if (selection is null)
        {
            return null;
        }

        try
        {
            return new RecognitionInput(
                EncodeTopDownBmp(
                    selection.Bgra,
                    selection.Bounds.Width,
                    selection.PixelHeight ?? selection.Bounds.Height,
                    selection.Stride),
                "image/bmp",
                origin + ".bmp",
                origin, selection.Bounds) { CaptureScene = selection.CaptureScene };
        }
        catch
        {
            selection.CaptureScene?.Dispose();
            throw;
        }
    }

    public Task<RecognitionInput?> ReadDroppedFileAsync(
        string path,
        CancellationToken cancellationToken) => ReadFileAsync(path, "drop", cancellationToken);

    private static async Task<RecognitionInput?> ReadFileAsync(
        string path,
        string origin,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Input image was not found.", path);
        }

        if (info.Length > MaximumInputBytes)
        {
            throw new InvalidDataException("Input image exceeds 256 MiB.");
        }

        byte[] data = await File.ReadAllBytesAsync(path, cancellationToken);
        await ValidateImageAsync(data, cancellationToken);
        return new RecognitionInput(data, MediaType(path), info.Name, origin);
    }

    private static async Task ValidateImageAsync(byte[] data, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(data);
        using var random = stream.AsRandomAccessStream();
        try
        {
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(random).AsTask(cancellationToken);
            if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0 ||
                decoder.PixelWidth > WorkbenchAnnotationStore.MaximumDimensionPixels ||
                decoder.PixelHeight > WorkbenchAnnotationStore.MaximumDimensionPixels ||
                (long)decoder.PixelWidth * decoder.PixelHeight > WorkbenchAnnotationStore.MaximumImagePixels)
                throw new InvalidDataException("图片像素尺寸超出支持范围。");
            _ = await decoder.GetPixelDataAsync().AsTask(cancellationToken);
        }
        catch (Exception error) when (error is not (InvalidDataException or OperationCanceledException))
        {
            throw new InvalidDataException("无法解码图片。", error);
        }
    }

    private static string MediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        _ => throw new InvalidDataException("Unsupported image format."),
    };

    internal static byte[] EncodeTopDownBmp(byte[] bgra, int width, int height, int stride)
    {
        int pixelBytes = checked(stride * height);
        if (stride != checked(width * 4) || bgra.Length != pixelBytes)
        {
            throw new InvalidDataException("Invalid BGRA capture buffer.");
        }

        byte[] bmp = new byte[checked(54 + pixelBytes)];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), -height);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28), 32);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(34), pixelBytes);
        bgra.CopyTo(bmp, 54);
        return bmp;
    }
}

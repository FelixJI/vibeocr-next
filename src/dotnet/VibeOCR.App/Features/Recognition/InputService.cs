using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Windows.Graphics.Imaging;
using VibeOCR.App.Services;
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

    /// <summary>普通截图动作栏的显式动作；null/编辑沿用既有会话语义。</summary>
    internal ScreenshotSelectionAction? SelectionAction { get; init; }

    /// <summary>动作栏显式选择的 typed 模式/引擎 id；仅 Recognize 动作携带。</summary>
    internal string? SelectionRecognitionMode { get; init; }
    internal ScreenshotRecognitionIntent? SelectionRecognitionIntent { get; init; }

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

    /// <summary>
    /// 普通截图入口：把宿主动作栏目录投影交给选区 picker。默认实现回落
    /// 到直接入口行为，供专用识别/选字入口与测试桩保持既有确认语义。
    /// </summary>
    Task<RecognitionInput?> CaptureScreenWithActionsAsync(
        ScreenshotSelectionActions? actions,
        CancellationToken cancellationToken) => CaptureScreenAsync(cancellationToken);

    Task<RecognitionInput?> CaptureScrollingScreenAsync(CancellationToken cancellationToken) =>
        Task.FromException<RecognitionInput?>(new NotSupportedException("Scrolling capture is unavailable."));

    /// <summary>
    /// #236 直接目标截图（全屏/活动窗口/显示器/上次区域）。默认实现保持
    /// 既有测试桩语义（不可用），生产 InputService 覆盖。
    /// </summary>
    Task<RecognitionInput?> CaptureAreaAsync(
        ScreenshotCaptureRequest request,
        CancellationToken cancellationToken) =>
        Task.FromException<RecognitionInput?>(
            new NotSupportedException("Direct area capture is unavailable."));

    /// <summary>#236 延时截图：倒计时（可取消）后进入既有框选链路。</summary>
    Task<RecognitionInput?> CaptureDelayedRegionAsync(
        ScreenshotSelectionActions? actions,
        CancellationToken cancellationToken) =>
        Task.FromException<RecognitionInput?>(
            new NotSupportedException("Delayed capture is unavailable."));

    Task<RecognitionInput?> ReadDroppedFileAsync(string path, CancellationToken cancellationToken);
}

public sealed record SystemImportImageItem(
    byte[] Data,
    string MediaType,
    int PixelWidth,
    int PixelHeight);

/// <summary>
/// 对象导入读取结果：一次 DataPackage 快照只按优先级采一类内容
/// （Bitmap → 授权 StorageItems 图片 → 纯文本），文件拖拽携带的
/// 同名文本不再产生第二个对象；Source 记录实际命中的格式。
/// </summary>
public sealed record ImportedSystemContent(
    IReadOnlyList<SystemImportImageItem> Images,
    string? Text,
    string Source)
{
    public const string BitmapSource = "bitmap";
    public const string StorageItemsSource = "storageItems";
    public const string TextSource = "text";
}

/// <summary>
/// #232 对象导入的窄读取接缝：系统剪贴板/原生拖入 DataPackage 只由
/// C# 宿主读取与校验，Web 不接受本地路径或网络 URL 充当授权。
/// 实现必须真实读流并做硬截止校验，超限整批抛出拒绝，不截断。
/// </summary>
public interface ISystemImportReader
{
    Task<ImportedSystemContent?> ReadClipboardForImportAsync(CancellationToken cancellationToken);

    Task<ImportedSystemContent?> ReadPackageForImportAsync(
        DataPackageView view,
        CancellationToken cancellationToken);
}

/// <summary>导入读取被明确拒绝（无内容/超限/格式不支持）；Reason 供回执映射。</summary>
public sealed class SystemImportRejectException(
    string reason,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Reason { get; } = reason;
}

public sealed class InputService : IInputService, ISystemImportReader
{
    private const long MaximumInputBytes = 256L << 20;
    private const long MaximumCaptureBytes = 256L << 20;
    /// <summary>#232 单个导入图片的编码体积/像素/单边硬上限（拒绝，不降采样）。</summary>
    internal const long MaximumImportImageBytes = 16L << 20;
    internal const long MaximumImportImagePixels = 16_000_000;
    internal const int MaximumImportImageSide = 8192;
    /// <summary>#232 导入文本硬上限：UTF-16 code unit 数与行数（拒绝，不截断）。</summary>
    internal const int MaximumImportTextLength = 4096;
    internal const int MaximumImportTextLines = 128;
    /// <summary>#232 单次导入（粘贴/拖入）的项目数上限。</summary>
    internal const int MaximumImportItems = 16;
    internal const long MaximumImportBatchPixels = 32_000_000;
    internal const long MaximumImportBatchBytes = 64L << 20;
    private readonly Func<nint> _windowHandle;
    private readonly IScreenRegionPicker _screenRegionPicker;
    private readonly IScreenRegionPicker _scrollingRegionPicker;
    private readonly ICapturePreferencesStore? _preferences;

    public InputService(Func<nint> windowHandle, IScreenRegionPicker? screenRegionPicker = null,
        IScreenRegionPicker? scrollingRegionPicker = null,
        ICapturePreferencesStore? preferences = null)
    {
        _windowHandle = windowHandle ?? throw new ArgumentNullException(nameof(windowHandle));
        _screenRegionPicker = screenRegionPicker ?? new ScreenRegionPicker(windowHandle, preferences: preferences);
        _scrollingRegionPicker = scrollingRegionPicker ?? new ScreenRegionPicker(windowHandle, scrolling: true);
        _preferences = preferences;
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
        using IRandomAccessStreamWithContentType stream = await reference.OpenReadAsync().AsTask(cancellationToken);
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

    public Task<RecognitionInput?> CaptureScreenWithActionsAsync(
        ScreenshotSelectionActions? actions,
        CancellationToken cancellationToken) =>
        CaptureAsync(_screenRegionPicker, "screenshot", cancellationToken, actions);

    public Task<RecognitionInput?> CaptureScrollingScreenAsync(CancellationToken cancellationToken) =>
        CaptureAsync(_scrollingRegionPicker, "scrolling-screenshot", cancellationToken);

    /// <summary>
    /// #236 直接目标截图：隐藏宿主 → 冻结目标矩形（可选光标叠加）→ 恢复
    /// 宿主；失败/取消经既有异常路径发布 recognition.failed/cancelled。
    /// </summary>
    public async Task<RecognitionInput?> CaptureAreaAsync(
        ScreenshotCaptureRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind is ScreenshotCaptureKind.Region or ScreenshotCaptureKind.DelayedRegion)
        {
            throw new ArgumentException(
                $"区域框选模式应走既有选区链路：{request.Kind}", nameof(request));
        }

        CapturePreferences preferences = _preferences?.Load() ?? new CapturePreferences();
        nint owner = _windowHandle();
        nint foreground = GetForegroundWindow();
        IReadOnlyList<ScreenMonitorTarget> layoutBefore = ScreenTargetQuery.EnumerateMonitors();
        PhysicalRectangle desktopBefore = ScreenTargetQuery.VirtualDesktop();
        bool visible = IsWindowVisible(owner);
        bool minimized = IsIconic(owner);
        ShowWindow(owner, 0);
        try
        {
            await Task.Delay(180, cancellationToken);
            EnsureLayoutUnchanged(layoutBefore, desktopBefore);
            PhysicalRectangle bounds = ResolveTargetBounds(request, preferences, owner, foreground);
            await using var capture = new ScreenCaptureService(
                Guid.NewGuid(), cursorOverlay: new GdiScreenCursorOverlay());
            CapturedFrame frame = capture.Capture(bounds, TimeSpan.FromMinutes(1), preferences.IncludeCursor);
            byte[] bgra = capture.Read(frame);
            EnsureLayoutUnchanged(layoutBefore, desktopBefore);
            if (bounds != ResolveTargetBounds(request, preferences, owner, foreground))
                throw new CaptureValidationException(
                    CaptureRefusal.LayoutChanged, "截图目标已移动或改变大小，请重新截图。");
            cancellationToken.ThrowIfCancellationRequested();
            return new RecognitionInput(
                EncodeTopDownBmp(bgra, bounds.Width, bounds.Height, frame.Stride),
                "image/bmp",
                "screenshot.bmp",
                "screenshot",
                bounds);
        }
        finally
        {
            ScreenRegionPicker.RestoreOwnerState(owner, visible, minimized, foreground);
        }
    }

    public async Task<RecognitionInput?> CaptureDelayedRegionAsync(
        ScreenshotSelectionActions? actions,
        CancellationToken cancellationToken)
    {
        CapturePreferences preferences = _preferences?.Load() ?? new CapturePreferences();
        if (!await DelayedCaptureOverlay.RunAsync(preferences.EffectiveDelaySeconds, cancellationToken))
        {
            return null;
        }

        return await CaptureScreenWithActionsAsync(actions, cancellationToken);
    }

    private static void EnsureLayoutUnchanged(
        IReadOnlyList<ScreenMonitorTarget> previous, PhysicalRectangle desktop)
    {
        if (ScreenTargetQuery.VirtualDesktop() != desktop ||
            !ScreenTargetQuery.LayoutsMatch(previous, ScreenTargetQuery.EnumerateMonitors()))
            throw new CaptureValidationException(
                CaptureRefusal.LayoutChanged, "截图期间显示器布局已变化，请重新截图。");
    }

    private static PhysicalRectangle ResolveTargetBounds(
        ScreenshotCaptureRequest request,
        CapturePreferences preferences,
        nint owner,
        nint foreground) =>
        ResolveTargetBounds(request, preferences, owner, foreground, ScreenTargetQuery.VirtualDesktop());

    /// <summary>
    /// #245 C1 可测 seam：生产路径按真实虚拟桌面进入同一实现。分配上限按
    /// “实际捕获目标”而非整个虚拟桌面——巨大/稀疏桌面上小窗口、固定/上次
    /// 区域与单屏目标仍可捕获；全屏目标本身即整桌面，上限语义不变。
    /// 实际分配上限不删除。
    /// </summary>
    internal static PhysicalRectangle ResolveTargetBounds(
        ScreenshotCaptureRequest request,
        CapturePreferences preferences,
        nint owner,
        nint foreground,
        PhysicalRectangle desktop)
    {
        desktop.Validate();

        PhysicalRectangle bounds = request.Kind switch
        {
            ScreenshotCaptureKind.Fullscreen => desktop,
            ScreenshotCaptureKind.ActiveWindow =>
                ResolveActiveWindow(preferences, request, owner, foreground),
            ScreenshotCaptureKind.Monitor => ResolveMonitor(request.MonitorDevice),
            ScreenshotCaptureKind.FixedRegion => ResolveFixedRegion(preferences, desktop),
            ScreenshotCaptureKind.LastRegion => ResolveLastRegion(preferences, desktop),
            _ => throw new ArgumentException($"未知截图目标：{request.Kind}", nameof(request)),
        };
        bounds.Validate();
        PhysicalRectangle? clipped = SmartScreenCandidates.Intersect(bounds, desktop);
        if (clipped is null)
        {
            throw new CaptureValidationException(
                CaptureRefusal.RegionUnavailable, "截图目标不在当前桌面范围内，请重试。");
        }

        PhysicalRectangle target = clipped.Value;
        if (checked((long)target.Width * target.Height * 4) > MaximumCaptureBytes)
        {
            throw new CaptureValidationException(
                CaptureRefusal.TooLarge,
                target == desktop
                    ? "全屏截图超过 256 MiB 上限，请改用区域/显示器截图。"
                    : "截图目标区域超过 256 MiB 上限，请缩小目标后重试。");
        }

        return target;
    }

    internal static PhysicalRectangle ResolveActiveWindow(
        CapturePreferences preferences, ScreenshotCaptureRequest request, nint owner, nint foreground)
    {
        // 菜单/悬浮栏入口：捕获时的前台是菜单宿主或本应用窗口；使用打开
        // 菜单前记录的真实目标窗口（request.TargetWindow）。热键入口无
        // 记录时沿用进入时前台；宿主自身持前台则隐藏后重查。
        nint window = request.TargetWindow != 0 && request.TargetWindow != owner
            ? request.TargetWindow
            : foreground == owner && owner != 0
                ? GetForegroundWindow()
                : foreground;
        PhysicalRectangle? bounds = window == 0 || window == owner
            ? null
            : ScreenTargetQuery.TryGetWindowBounds(window, preferences.EffectiveWindowBoundsMode);
        return bounds ?? throw new CaptureValidationException(
            CaptureRefusal.WindowUnavailable,
            "目标活动窗口已不可用（消失/最小化），请在其置顶后重试。");
    }

    private static PhysicalRectangle ResolveFixedRegion(
        CapturePreferences preferences, PhysicalRectangle desktop)
    {
        PhysicalRectangle fixedRegion = preferences.FixedRegion
            ?? throw new CaptureValidationException(
                CaptureRefusal.MissingFixedRegion,
                "尚未设置固定截图区域，请先在截图选项中设置。");
        if (!CapturePreferencesStore.IsRegionInsideDesktop(fixedRegion, desktop))
        {
            throw new CaptureValidationException(
                CaptureRefusal.RegionUnavailable,
                "固定截图区域不在当前桌面范围内（显示器布局可能已变化），请在截图选项中重新设置。");
        }

        return fixedRegion;
    }

    private static PhysicalRectangle ResolveMonitor(string? deviceName)
    {
        IReadOnlyList<ScreenMonitorTarget> monitors = ScreenTargetQuery.EnumerateMonitors();
        if (monitors.Count == 0)
        {
            throw new CaptureValidationException(
                CaptureRefusal.MonitorUnavailable, "未检测到可用显示器，无法截图。");
        }

        if (string.IsNullOrEmpty(deviceName))
        {
            return monitors.Count == 1
                ? monitors[0].Bounds
                : throw new CaptureValidationException(
                    CaptureRefusal.MonitorUnavailable, "存在多台显示器，请从截图模式菜单选择目标显示器。");
        }

        return monitors.FirstOrDefault(
            monitor => string.Equals(monitor.DeviceName, deviceName, StringComparison.Ordinal))
            is { } matched
            ? matched.Bounds
            : throw new CaptureValidationException(
                CaptureRefusal.MonitorUnavailable, "所选显示器当前不可用（可能已断开），请重新选择。");
    }

    private static PhysicalRectangle ResolveLastRegion(
        CapturePreferences preferences, PhysicalRectangle desktop)
    {
        LastRegionCapture? last = preferences.LastRegion
            ?? throw new CaptureValidationException(
                CaptureRefusal.RegionUnavailable, "尚无上次截图区域，请先完成一次框选截图。");
        if (!CapturePreferencesStore.IsLastRegionUsable(
                last, ScreenTargetQuery.EnumerateMonitors(), desktop))
        {
            throw new CaptureValidationException(
                CaptureRefusal.RegionUnavailable,
                "上次截图区域已不可用（显示器布局变化或区域越界），请重新框选。");
        }

        return last.Bounds;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    private async Task<RecognitionInput?> CaptureAsync(
        IScreenRegionPicker picker, string origin, CancellationToken cancellationToken,
        ScreenshotSelectionActions? actions = null)
    {
        ScreenRegionSelection? selection = actions is null
            ? await picker.PickAsync(cancellationToken)
            : await picker.PickAsync(actions, cancellationToken);
        if (selection is null)
        {
            return null;
        }

        try
        {
            RecognitionInput input = new RecognitionInput(
                EncodeTopDownBmp(
                    selection.Bgra,
                    selection.Bounds.Width,
                    selection.PixelHeight ?? selection.Bounds.Height,
                    selection.Stride),
                "image/bmp",
                origin + ".bmp",
                origin, selection.Bounds)
            {
                CaptureScene = selection.CaptureScene,
                SelectionAction = selection.SelectionAction,
                SelectionRecognitionMode = selection.RecognitionModeId,
                SelectionRecognitionIntent = selection.RecognitionIntent,
            };
            if (selection.FrozenRegion is { } frozen)
                ScreenRegionPicker.EnsureFrozenLayoutCurrent(frozen, ScreenTargetQuery.EnumerateMonitors);
            // 上次区域只持久化坐标与布局快照，绝不保存像素（#236 AC3）；
            // 持久化失败只告警，不得让已成功截图失败或泄漏 CaptureScene。
            try
            {
                if (selection.FrozenRegion is { } region)
                    _preferences?.SaveLastRegion(region);
                else
                    _preferences?.SaveLastRegion(selection.Bounds);
            }
            catch (Exception saveError) when (
                saveError is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                AppLog.Warn($"保存上次截图区域失败：{saveError.Message}");
            }

            return input;
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

    public Task<ImportedSystemContent?> ReadClipboardForImportAsync(CancellationToken cancellationToken)
    {
        DataPackageView content = Clipboard.GetContent();
        return ReadDataPackageCoreAsync(content, cancellationToken);
    }

    public Task<ImportedSystemContent?> ReadPackageForImportAsync(
        DataPackageView view,
        CancellationToken cancellationToken) => ReadDataPackageCoreAsync(view, cancellationToken);

    /// <summary>轻量导入读取器：不构造选区 picker，仅供工作台对象导入注入。</summary>
    internal static ISystemImportReader CreateImportReader() => new ImportOnlyReader();

    private sealed class ImportOnlyReader : ISystemImportReader
    {
        public Task<ImportedSystemContent?> ReadClipboardForImportAsync(CancellationToken cancellationToken)
        {
            DataPackageView content = Clipboard.GetContent();
            return ReadDataPackageCoreAsync(content, cancellationToken);
        }

        public Task<ImportedSystemContent?> ReadPackageForImportAsync(
            DataPackageView view,
            CancellationToken cancellationToken) =>
            ReadDataPackageCoreAsync(view, cancellationToken);
    }

    private static async Task<ImportedSystemContent?> ReadDataPackageCoreAsync(
        DataPackageView view,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(view);
        cancellationToken.ThrowIfCancellationRequested();
        if (view.Contains(StandardDataFormats.Bitmap))
        {
            RandomAccessStreamReference reference = await view.GetBitmapAsync().AsTask(cancellationToken);
            using IRandomAccessStreamWithContentType stream = await reference.OpenReadAsync().AsTask(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            SystemImportImageItem image = await ReadImportImageAsync(
                stream,
                string.IsNullOrWhiteSpace(stream.ContentType) ? "image/png" : stream.ContentType,
                cancellationToken);
            return new ImportedSystemContent([image], null, ImportedSystemContent.BitmapSource);
        }
        if (view.Contains(StandardDataFormats.StorageItems))
        {
            IReadOnlyList<IStorageItem> items = await view.GetStorageItemsAsync().AsTask(cancellationToken);
            List<StorageFile> files = items.OfType<StorageFile>().ToList();
            if (files.Count != items.Count)
                throw new SystemImportRejectException("invalid", "不支持导入文件夹；本批未导入。");
            if (files.Count == 0) return null;
            if (files.Count > MaximumImportItems)
                throw new SystemImportRejectException("batch", $"单次最多导入 {MaximumImportItems} 个项目。");
            foreach (StorageFile file in files)
            {
                if (!IsSupportedImportImageExtension(Path.GetExtension(file.Name)))
                    throw new SystemImportRejectException(
                        "invalid", $"不支持导入的文件类型：{file.Name}");
            }
            List<SystemImportImageItem> images = [];
            long remainingPixels = MaximumImportBatchPixels, remainingBytes = MaximumImportBatchBytes;
            foreach (StorageFile file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using IRandomAccessStreamWithContentType stream = await file.OpenReadAsync().AsTask(cancellationToken);
                SystemImportImageItem image = await ReadImportImageAsync(
                    stream, MediaType(file.Path), cancellationToken, remainingPixels, remainingBytes);
                images.Add(image);
                remainingPixels -= (long)image.PixelWidth * image.PixelHeight;
                remainingBytes -= image.Data.LongLength;
            }
            return new ImportedSystemContent(images, null, ImportedSystemContent.StorageItemsSource);
        }
        if (view.Contains(StandardDataFormats.Text))
        {
            string text = await view.GetTextAsync().AsTask(cancellationToken);
            ValidateImportText(text);
            return new ImportedSystemContent([], text, ImportedSystemContent.TextSource);
        }
        return null;
    }

    /// <summary>硬截止读取并真实解码一张导入图片：先按头部长度拒绝，再按
    /// 实际读流/解码结果拒绝；解码成功才返回，杜绝伪造扩展名。</summary>
    private static async Task<SystemImportImageItem> ReadImportImageAsync(
        IRandomAccessStreamWithContentType stream,
        string mediaType,
        CancellationToken cancellationToken,
        long remainingPixels = MaximumImportBatchPixels,
        long remainingBytes = MaximumImportBatchBytes)
    {
        if (stream.Size > MaximumImportImageBytes)
            throw new SystemImportRejectException(
                "oversize", "导入图片超过 16 MiB 上限。");
        if ((long)stream.Size > remainingBytes)
            throw new SystemImportRejectException("budget", "导入资源总体积超过 64 MiB 上限。");
        byte[] data = new byte[(int)stream.Size];
        using (DataReader reader = new(stream.GetInputStreamAt(0)))
        {
            uint loaded = await reader.LoadAsync((uint)data.Length).AsTask(cancellationToken);
            if (loaded != data.Length)
                throw new SystemImportRejectException(
                    "invalid", "导入图片读取不完整。");
            reader.ReadBytes(data);
        }
        cancellationToken.ThrowIfCancellationRequested();
        using MemoryStream memory = new(data, writable: false);
        using var random = memory.AsRandomAccessStream();
        try
        {
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(random).AsTask(cancellationToken);
            uint width = decoder.PixelWidth, height = decoder.PixelHeight;
            if (width == 0 || height == 0 ||
                width > MaximumImportImageSide || height > MaximumImportImageSide ||
                (long)width * height > MaximumImportImagePixels)
                throw new SystemImportRejectException(
                    "oversize", "导入图片尺寸超出支持范围（单边 ≤8192，总像素 ≤1600 万）。");
            if ((long)width * height > remainingPixels)
                throw new SystemImportRejectException("budget", "导入图片总像素超过 3200 万上限。");
            _ = await decoder.GetPixelDataAsync().AsTask(cancellationToken);
            if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                mediaType = decoder.DecoderInformation.MimeTypes.FirstOrDefault() ?? "image/png";
            return new SystemImportImageItem(data, mediaType, (int)width, (int)height);
        }
        catch (Exception error) when (
            error is not (SystemImportRejectException or OperationCanceledException))
        {
            throw new SystemImportRejectException("invalid", "无法解码导入图片。", error);
        }
    }

    internal static void ValidateImportText(string text)
    {
        if (text.Length > MaximumImportTextLength)
            throw new SystemImportRejectException(
                "oversize", $"导入文本超过 {MaximumImportTextLength} 字符上限。");
        int lines = 1;
        foreach (char character in text)
            if (character == '\n')
                lines++;
        if (lines > MaximumImportTextLines)
            throw new SystemImportRejectException(
                "oversize", $"导入文本超过 {MaximumImportTextLines} 行上限。");
    }

    internal static bool IsSupportedImportImageExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp" => true,
        _ => false,
    };

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

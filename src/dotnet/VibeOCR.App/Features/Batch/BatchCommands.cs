using VibeOCR.Contracts.HttpV2;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace VibeOCR.App.Features.Batch;

public interface IBatchFileSource
{
    Task<IReadOnlyList<string>> PickFilesAsync(CancellationToken cancellationToken);
    Task<(byte[] Data, string MediaType)> ReadAsync(string path, CancellationToken cancellationToken);
}

public sealed class BatchFileSource(Func<nint> windowHandle) : IBatchFileSource
{
    private const long MaximumInputBytes = 256L << 20;

    public async Task<IReadOnlyList<string>> PickFilesAsync(CancellationToken cancellationToken)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, ViewMode = PickerViewMode.Thumbnail };
        foreach (string extension in BatchCommands.Extensions) picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle());
        IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return files.Select(file => file.Path).ToArray();
    }

    public async Task<(byte[] Data, string MediaType)> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Batch input was not found.", path);
        if (info.Length > MaximumInputBytes) throw new InvalidDataException("Batch input exceeds 256 MiB.");
        byte[] data = await File.ReadAllBytesAsync(info.FullName, cancellationToken);
        return (data, BatchCommands.MediaType(info.Extension));
    }
}

/// <summary>
/// 批量输入的扩展名与 MIME，与 Runtime 共享 MIME 映射
/// （documents/utils/mime_types.py）对齐：图片全集 + PDF/Office 文档。
/// 映射外格式（csv/odt/epub 等 MinerU 官方支持的输入）会在 MinerU
/// 上传边界被拒绝；扩充需先扩展共享 MIME 契约与 MinerU 服务路由。
/// </summary>
public static class BatchCommands
{
    private const string MineruPipeline = "MinerU";

    public static IReadOnlyList<string> Extensions { get; } =
    [
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".jp2",
        ".pdf", ".docx", ".pptx", ".xlsx",
    ];

    public static string MediaType(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".bmp" => "image/bmp",
        ".gif" => "image/gif",
        ".tif" or ".tiff" => "image/tiff",
        ".webp" => "image/webp",
        ".jp2" => "image/jp2",
        ".pdf" => "application/pdf",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        _ => throw new InvalidDataException($"Unsupported batch input format: {extension}"),
    };

    /// <summary>需要 MinerU 文档解析的输入（PDF/Office 文档，非图片）。</summary>
    public static bool IsDocument(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is
            ".pdf" or ".docx" or ".pptx" or ".xlsx";

    /// <summary>
    /// 原生 Office 文档：MinerU 4 仅在 flash 档整篇解析且不支持页范围；
    /// PDF/图片全档可用。
    /// </summary>
    public static bool IsNativeOfficeDocument(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".docx" or ".pptx" or ".xlsx";

    /// <summary>
    /// 提交冻结点的按文件输入门禁：返回 null 表示可提交，否则返回带
    /// UNSUPPORTED_INPUT_KIND 前缀的拒绝原因。mineruPageRange 仅对
    /// 原生 Office 生效（不支持非整篇范围）。
    /// </summary>
    public static string? DescribeInputRejection(
        string path,
        string pipeline,
        MineruTier? effectiveMineruTier,
        string? mineruPageRange = null)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            _ = MediaType(extension);
        }
        catch (InvalidDataException)
        {
            return $"UNSUPPORTED_INPUT_KIND: 不支持的批量输入格式 {extension}；支持的格式为图片与 PDF/Word/Excel/PowerPoint 文档。";
        }
        if (!IsDocument(path) || string.Equals(extension, ".pdf", StringComparison.Ordinal)) return null;
        if (pipeline != MineruPipeline)
        {
            return $"UNSUPPORTED_INPUT_KIND: {Path.GetFileName(path)} 是文档，请先将识别模式切换为 MinerU 文档解析；当前 {pipeline} 模式仅支持图片。";
        }
        if (!IsNativeOfficeDocument(path)) return null;
        if (mineruPageRange is not null && mineruPageRange != MineruConfig.AllPages)
        {
            return $"UNSUPPORTED_INPUT_KIND: Word/Excel/PowerPoint 文档不支持按页范围解析（当前设置 {mineruPageRange}）；请改为整篇解析。";
        }
        if (effectiveMineruTier is not MineruTier.Flash)
        {
            return "UNSUPPORTED_INPUT_KIND: Word/Excel/PowerPoint 文档需要在 Flash 档整篇解析，当前环境的 Flash 档不可用；请在设置中检查 MinerU 连接后重试。";
        }
        return null;
    }

    /// <summary>失败呈现：白名单 reason 显示可行动中文提示，未知详情不回显。</summary>
    public static string DescribeFailure(string? code, string? reason) => reason switch
    {
        "mineru_api_endpoint_incompatible" => "MinerU 连接地址不可用或不兼容（HTTP 404/405）；请在设置中检查 MinerU 连接地址。",
        "mineru_api_authentication_failed" => "MinerU 远程鉴权失败（HTTP 401/403）；请在设置中检查 MinerU 连接令牌。",
        _ => code ?? "INFERENCE_FAILED",
    };

    public static string UniqueOutputPath(string directory, string sourcePath, string format, ISet<string> reserved)
    {
        string extension = format switch { "markdown" => ".md", "html" => ".html", "docx" => ".docx", "xlsx" => ".xlsx", _ => ".txt" };
        string stem = Path.GetFileNameWithoutExtension(sourcePath);
        string candidate = Path.Combine(directory, stem + extension);
        for (int suffix = 1; File.Exists(candidate) || !reserved.Add(candidate); suffix++) candidate = Path.Combine(directory, $"{stem}_{suffix}{extension}");
        return candidate;
    }
}

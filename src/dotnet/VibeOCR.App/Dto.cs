// Standalone DTOs that were originally in VibeOCR.Contracts v1 protocol.
// Moved here so the v1 Contracts files can be deleted. These are simple
// data carriers — no protocol envelope, no IProtocolValidatable.
namespace VibeOCR.App;

public sealed record RecognizeResponse
{
    public required string Text { get; init; }
    public int? PreprocAngle { get; init; }
    public int? DocOrientationAngle { get; init; }
    public string Pipeline { get; init; } = "OCR";
    public string? RawText { get; init; }
    public string? MarkdownText { get; init; }
    public string? HtmlText { get; init; }
    public System.Text.Json.JsonElement[]? RawBlocks { get; init; }
    public System.Text.Json.JsonElement[]? ContentBlocks { get; init; }
    /// <summary>
    /// payload 顶层可选 reference_image 资产描述（形状同 content_list[].image：
    /// job_id/item_id/asset_id/media_type/width/height/size_bytes；失败
    /// {available:false,reason}），为行框 bbox 所在的预处理（旋转/去畸变）
    /// 像素空间。null=字段缺省；非 null 而非 Object（explicit null/畸形）
    /// =存在但不可用，宿主不得回退原图。
    /// </summary>
    public System.Text.Json.JsonElement? ReferenceImage { get; init; }
}

public sealed record QrCodeResult
{
    public string Data { get; init; } = "";
    public string Format { get; init; } = "";
    public bool? IsUrl { get; init; }
}

public static class ProtocolConstants
{
    public const int Version = 2;
}

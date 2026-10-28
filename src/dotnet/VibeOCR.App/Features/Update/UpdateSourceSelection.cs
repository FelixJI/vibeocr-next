namespace VibeOCR.App.Features.Update;

/// <summary>
/// 可选更新源的用户可见投影：脱敏端点（不含凭据/查询串），供设置界面
/// 的分段控制器展示检测与下载来源。
/// </summary>
public sealed record UpdateSourceOption(
    string Id,
    string Kind,
    string DisplayName,
    string Endpoint,
    bool Selected);

/// <summary>当前更新源目录：候选选项 + 选定源的有效检测地址 + 加载错误。</summary>
public sealed record UpdateSourceCatalog(
    IReadOnlyList<UpdateSourceOption> Options,
    string DetectionEndpoint,
    string? Error = null);

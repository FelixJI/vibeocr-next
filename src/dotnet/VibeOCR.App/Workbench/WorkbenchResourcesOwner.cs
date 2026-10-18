using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Inference;
using VibeOCR.App.Web;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Inference;

namespace VibeOCR.App.Workbench;

internal sealed class WorkbenchResourcesOwner(WorkbenchResourceBroker resourceBroker, string resourceRoot)
{
  private readonly object gate = new();
  private readonly List<string> generatedFiles = [];
  private readonly Dictionary<string, string> resourceFiles = new(StringComparer.Ordinal);
  private readonly Dictionary<string, IReadOnlyList<WorkbenchResourceReference>> structuredResourceChildren = new(StringComparer.Ordinal);
  private readonly Dictionary<string, string> structuredResourceFiles = new(StringComparer.Ordinal);
  internal Task<WorkbenchResourceReference?> PublishStructuredResultAsync(
    RecognitionViewModel viewModel,
    CancellationToken cancellationToken)
    => PublishStructuredResultAsync(viewModel.Result, viewModel.FetchResultAssetAsync, cancellationToken);

  internal Task<WorkbenchResourceReference?> PublishStructuredResultAsync(
    RecognizeResponse? result,
    Func<string, string, string, CancellationToken, Task<byte[]>> fetchAsset,
    CancellationToken cancellationToken)
  {
    // 纯文本 OCR 无 content_list：用权威 text_blocks 合成 lines 区块
    // （modes 为展示/复制的拼接权威），供结果面板的排版模式、低置信度
    // 提示与行内联编辑使用；结构化结果仍优先使用 content_list。
    if (result?.ContentBlocks is not { Length: > 0 } && result?.RawBlocks is { Length: > 0 })
    {
      JsonObject? linesBlock = RecognitionLinesBlock.BuildBlock(
        result.RawBlocks, result.RawText ?? result.Text);
      if (linesBlock is not null)
      {
        return PublishLinesStructuredResultAsync(linesBlock, cancellationToken);
      }
    }
    return PublishStructuredResultAsync(
      result?.ContentBlocks,
      fetchAsset,
      cancellationToken);
  }

  private async Task<WorkbenchResourceReference?> PublishLinesStructuredResultAsync(
    JsonObject linesBlock,
    CancellationToken cancellationToken)
  {
    JsonArray json = [linesBlock];
    (WorkbenchResourceReference structured, string structuredPath) =
      await PublishFileAsync(
        Encoding.UTF8.GetBytes(json.ToJsonString()),
        "application/json; charset=utf-8", ".json", cancellationToken);
    lock (gate)
    {
      structuredResourceFiles[structured.Url] = structuredPath;
      structuredResourceChildren[structured.Url] = [];
    }
    return structured;
  }

  /// <summary>
  /// 结构化结果与授权图片资产的唯一发布路径：单次/批量项/PDF 页共用，
  /// 不复制三套。图片经 job/item/asset 授权取回后以 opaque 资源发布；
  /// 最终 JSON 也注册进剪贴板复制的资源表，供 CopyStructuredResultAsync
  /// 重读权威内容。
  /// </summary>
  internal async Task<WorkbenchResourceReference?> PublishStructuredResultAsync(
    System.Text.Json.JsonElement[]? blocks,
    Func<string, string, string, CancellationToken, Task<byte[]>>? fetchAsset,
    CancellationToken cancellationToken)
  {
    if (blocks is not { Length: > 0 } content) return null;
    List<WorkbenchResourceReference> children = [];
    bool adopted = false;
    try
    {
      JsonArray json = [];
      int imageCount = 0;
      foreach (JsonElement block in content)
      {
        JsonNode? node = JsonNode.Parse(block.GetRawText());
        if (node is not JsonObject item)
        {
          json.Add(node);
          continue;
        }
        if (item["image"] is JsonObject asset)
        {
          // resource 只承载宿主发布的 opaque 资源引用：wire 原样携带的
          // 不可信副本先清除，成功取回后再写回权威引用。
          asset.Remove("resource");
          if (++imageCount > 32)
          {
            asset["available"] = false;
            asset["reason"] = "本项图片数量超过 32 张预览上限";
          }
          else if (!(asset["available"] is JsonValue available &&
            available.TryGetValue<bool>(out bool canRead) && !canRead) &&
            fetchAsset is not null &&
            asset["job_id"] is JsonValue job && job.TryGetValue<string>(out string? jobId) &&
            asset["item_id"] is JsonValue sourceItem && sourceItem.TryGetValue<string>(out string? itemId) &&
            asset["asset_id"] is JsonValue identity && identity.TryGetValue<string>(out string? assetId))
          {
            try
            {
              byte[] bytes = await fetchAsset(
                jobId!, itemId!, assetId!, cancellationToken);
              WorkbenchResourceReference reference = await PublishBytesAsync(
                bytes, "image/png", ".png", cancellationToken);
              children.Add(reference);
              asset["resource"] = JsonSerializer.SerializeToNode(new
              {
                url = reference.Url,
                mediaType = reference.MediaType,
                byteLength = reference.ByteLength,
              });
            }
            // HttpClient 超时按图片降级；调用者取消仍向上传播。
            catch (Exception error) when (error is IOException or
              ArgumentException or NotSupportedException or InferenceClientException or
              HttpRequestException or VibeOCR.Runtime.Client.RuntimeClientException or
              InvalidDataException or InferenceClientNotAttachedException ||
              error is OperationCanceledException { InnerException: TimeoutException } &&
              !cancellationToken.IsCancellationRequested)
            {
              asset["available"] = false;
              asset["reason"] = "结果图片已失效或无法读取";
            }
          }
        }
        json.Add(item);
      }
      (WorkbenchResourceReference structured, string structuredPath) =
        await PublishFileAsync(
          Encoding.UTF8.GetBytes(json.ToJsonString()),
          "application/json; charset=utf-8", ".json", cancellationToken);
      lock (gate)
      {
        structuredResourceFiles[structured.Url] = structuredPath;
        structuredResourceChildren[structured.Url] = children; adopted = true;
      }
      return structured;
    }
    finally { if (!adopted) foreach (WorkbenchResourceReference child in children) ReleaseResource(child); }
  }

  internal bool TryGetStructuredFile(string resourceUri, [NotNullWhen(true)] out string? path)
  {
    lock (gate) return structuredResourceFiles.TryGetValue(resourceUri, out path);
  }

  /// <summary>#233 会话持有 source/assets 的直接文件路径读取：不经 broker
  /// 租约/TTL 重新 GET，供保存命令在写入期间持有资源字节；租约过期不
  /// 影响本查询，文件删除只随 ReleaseResource/Release 发生。</summary>
  internal bool TryGetSessionFile(string resourceUri, [NotNullWhen(true)] out string? path)
  {
    lock (gate) return resourceFiles.TryGetValue(resourceUri, out path);
  }
  internal void ReleaseResource(WorkbenchResourceReference resource)
  {
    lock (gate)
    {
      structuredResourceFiles.Remove(resource.Url);
      if (structuredResourceChildren.Remove(resource.Url, out var children))
        foreach (WorkbenchResourceReference child in children) ReleaseResource(child);
      if (!Uri.TryCreate(resource.Url, UriKind.Absolute, out Uri? uri)) return;
      resourceBroker.Revoke(new WorkbenchResourceLease(uri, DateTimeOffset.MaxValue));
      // Published input files belong to the app and are retained only while leased.
      if (resourceFiles.TryGetValue(resource.Url, out string? path))
      {
        try
        {
          File.Delete(path);
          resourceFiles.Remove(resource.Url);
          generatedFiles.Remove(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
      }
    }
  }

  internal async Task<WorkbenchResourceReference> PublishBytesAsync(
    byte[] data,
    string mediaType,
    string extension,
    CancellationToken cancellationToken) =>
    (await PublishFileAsync(data, mediaType, extension, cancellationToken)).Reference;

  internal async Task<(WorkbenchResourceReference Reference, string Path)> PublishFileAsync(
    byte[] data,
    string mediaType,
    string extension,
    CancellationToken cancellationToken)
  {
    string relative = Path.Combine("session", $"{Guid.NewGuid():N}{extension}");
    string destination = Path.Combine(resourceRoot, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    try
    {
      await File.WriteAllBytesAsync(destination, data, cancellationToken);
      cancellationToken.ThrowIfCancellationRequested();
      WorkbenchResourceLease lease = resourceBroker.Lease(relative, mediaType, TimeSpan.FromHours(1));
      lock (gate)
      {
        generatedFiles.Add(destination);
        resourceFiles[lease.Uri.AbsoluteUri] = destination;
      }
      return (new WorkbenchResourceReference(lease.Uri.AbsoluteUri, mediaType, data.LongLength), destination);
    }
    catch
    {
      File.Delete(destination);
      throw;
    }
  }

  internal static string ExtensionForMediaType(string mediaType) => mediaType switch
  {
    "image/jpeg" => ".jpg",
    "image/bmp" => ".bmp",
    "image/webp" => ".webp",
    "image/gif" => ".gif",
    _ => ".png",
  };

  internal void Release()
  {
    lock (gate)
    {
      foreach (string file in generatedFiles)
      {
        try { File.Delete(file); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
      }
      generatedFiles.Clear(); structuredResourceFiles.Clear(); structuredResourceChildren.Clear();
    }
  }
}

using System.Security.Cryptography;
using System.Text.Json;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Bootstrap;

namespace VibeOCR.Platform.Inference;

public sealed class ProductRuntimeSession(
    InferenceSupervisorProcess process,
    InferenceHttpClient client,
    QrCodeHttpClient qrClient,
    RuntimeStatusSnapshot status) : IAsyncDisposable
{
  public InferenceSupervisorProcess Process { get; } = process;
  public InferenceHttpClient Client { get; } = client;
  public QrCodeHttpClient QrClient { get; } = qrClient;
  public RuntimeStatusSnapshot Status { get; } = status;
  public string InstanceId => Status.InstanceId;

  internal static IReadOnlyList<string> SupervisorArguments { get; } =
      ["-I", "-B", "-X", "utf8", "-m", "vibeocr.runtime.host.main"];

  internal static InferenceSupervisorOptions SupervisorOptions(PortableLayout layout, bool injectSoakCrash)
  {
    using var componentLock = JsonDocument.Parse(File.ReadAllBytes(layout.ComponentLock));
    byte[] manifest = File.ReadAllBytes(layout.RuntimeManifest);
    string? expected = componentLock.RootElement.GetProperty("product").GetProperty("runtime_manifest_sha256").GetString();
    if (Convert.ToHexStringLower(SHA256.HashData(manifest)) != expected)
      throw new InvalidDataException("产品 Runtime 清单与组件绑定不匹配，请重新解压完整产品包。");
    using var runtime = JsonDocument.Parse(manifest);
    if (runtime.RootElement.GetProperty("runtime_env").GetProperty("root").GetString() != "runtime-env")
      throw new InvalidDataException("产品缺少随包 Runtime 树，请重新解压完整产品包。");

    // 启动边界复用发布清单对解释器和 CPython DLL 的既有逐文件绑定。
    using var release = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(layout.ProductRoot, "app", "metadata", "product-release-manifest.json")));
    JsonElement files = release.RootElement.GetProperty("files");
    foreach (string path in new[] { layout.RuntimePython, Path.Combine(Path.GetDirectoryName(layout.RuntimePython)!, "python313.dll") })
    {
      string relative = Path.GetRelativePath(layout.ProductRoot, path).Replace(Path.DirectorySeparatorChar, '/');
      using FileStream stream = File.OpenRead(path);
      if (!files.TryGetProperty(relative, out JsonElement asset) ||
          Convert.ToHexStringLower(SHA256.HashData(stream)) != asset.GetProperty("sha256").GetString())
        throw new InvalidDataException($"产品 Runtime 文件与发布包不匹配：{relative}。请重新解压完整产品包。");
    }

    var environment = new Dictionary<string, string>
    {
      ["VIBEOCR_PRODUCT_RUNTIME"] = "1",
      ["VIBEOCR_PRODUCT_ROOT"] = layout.InstallRoot,
      ["VIBEOCR_LAYOUT_MANIFEST"] = layout.PortableLayoutManifest ?? "",
      ["VIBEOCR_RUNTIME_MANIFEST"] = layout.RuntimeManifest,
      ["VIBEOCR_COMPONENT_LOCK"] = layout.ComponentLock,
    };
    if (injectSoakCrash)
      environment["VIBEOCR_SUPERVISOR_SOAK_CRASH_AFTER_READY"] = "1";
    return new InferenceSupervisorOptions(layout.RuntimePython,
        SupervisorArguments,
        layout.ProductRoot, Path.Combine(layout.DataRoot, "supervisor.log"),
        TimeSpan.FromSeconds(90), RuntimeCapabilityRequirements.Read(layout.ComponentLock), environment);
  }

  public static async Task<ProductRuntimeSession> StartAsync(
      PortableLayout layout, CancellationToken cancellationToken, bool injectSoakCrash = false)
  {
    InferenceSupervisorOptions options;
    try { options = SupervisorOptions(layout, injectSoakCrash); }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
    { throw new InvalidDataException($"产品 Runtime 无法验证，请重新解压完整产品包。{error.Message}", error); }
    string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    var process = new InferenceSupervisorProcess(options, token);
    InferenceHttpClient? client = null;
    QrCodeHttpClient? qrClient = null;
    try
    {
      SupervisorReadyEnvelope ready = await process.StartAsync(cancellationToken).ConfigureAwait(false);
      client = new InferenceHttpClient(ready.BaseUrl, token);
      qrClient = new QrCodeHttpClient(ready.BaseUrl, token);
      var health = await client.GetHealthAsync(cancellationToken).ConfigureAwait(false);
      RuntimeStatusSnapshot status = await client.GetRuntimeStatusAsync(cancellationToken).ConfigureAwait(false);
      if (!health.Ready || health.Draining || health.InstanceId != ready.InstanceId ||
          status.InstanceId != ready.InstanceId || status.ServiceState != RuntimeServiceState.Ready)
        throw new InvalidDataException("产品识别服务未通过健康检查，请检查随包 Runtime。");
      cancellationToken.ThrowIfCancellationRequested();
      return new(process, client, qrClient, status);
    }
    catch
    {
      process.Dispose();
      if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
      if (qrClient is not null) await qrClient.DisposeAsync().ConfigureAwait(false);
      throw;
    }
  }

  public async ValueTask DisposeAsync()
  {
    Process.Dispose();
    await Client.DisposeAsync().ConfigureAwait(false);
    await QrClient.DisposeAsync().ConfigureAwait(false);
  }
}

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using Windows.Graphics.Imaging;
using VibeOCR.App.Services;

namespace VibeOCR.App.Workbench;

/// <summary>保存时提供的资源 entry：Name 必须在 manifest 派生白名单内，
/// Length 为声明长度；单次写入仅调用一次 OpenRead，流从零读取并由写入方释放。</summary>
public sealed record VibpProjectEntry(string Name, long Length, Func<Stream> OpenRead);

/// <summary>打开成功的工程包：条目已按声明长度与实际字节核对并解包到
/// 调用方目录（未解码，不驻留位图）；后续发布/完整解码由宿主 owner 在
/// pending 确认事务中完成。</summary>
public sealed record VibpProjectArchive
{
  public required VibpProjectManifest Manifest { get; init; }
  public required string SourcePath { get; init; }
  /// <summary>assetId → 已解包资源路径。</summary>
  public required IReadOnlyDictionary<string, string> AssetPaths { get; init; }
  /// <summary>patchId → 已解包 PNG 路径。</summary>
  public required IReadOnlyDictionary<int, string> PatchPaths { get; init; }
}

/// <summary>#233 .vibp v1 工程包容器：标准 ZipArchive 读写，宿主唯一读写方。
/// 打开侧只做有界流式解析、精确 entry 白名单与平台编码元数据校验
/// （BitmapDecoder 头级 PixelWidth/Height，不整图解码）；完整像素解码责任
/// 在既有边界（web pending 解码/宿主输入校验），元数据校验不冒充解码。</summary>
public static class VibpProjectFile
{
  private const int CopyBufferSize = 64 * 1024;
  // WIC WebP 解码器 CLSID（随系统 WebP image extension 提供）：
  // BitmapDecoder 未投影同名常量；由真实平台 fixture 测试在本机 decoder 上实证。
  internal static readonly Guid WebpDecoderId = new("7693e886-51c9-4070-8419-9f70738ec8fa");

  /// <summary>原子写入工程包：manifest 语义校验 + entry 集精确闭合后，
  /// 经 AtomicFile.Write 落盘；任何失败保留旧文件并清理临时文件。
  /// 写侧不复验图片内容：source/资产/补丁字节来自宿主会话已验证文件。</summary>
  public static void Write(string path, VibpProjectManifest manifest, IReadOnlyList<VibpProjectEntry> entries) =>
    Write(path, manifest, entries, VibpProjectLimits.MaximumFileBytes);

  /// <summary>#233 生产异步写入入口：受控后台线程执行同步压缩复制，
  /// 在每个 entry 前后、每 chunk 与原子提交前观察取消，不阻塞 UI 调用
  /// 线程；异常/取消保留旧目标文件并清理临时文件（AtomicFile 既有语义）。</summary>
  public static Task WriteAsync(
    string path,
    VibpProjectManifest manifest,
    IReadOnlyList<VibpProjectEntry> entries,
    CancellationToken cancellationToken) =>
    Task.Run(() => Write(path, manifest, entries, VibpProjectLimits.MaximumFileBytes, cancellationToken),
      CancellationToken.None);

  /// <summary>带输出文件预算的写入（测试 seam）：压缩后真实输出长度在原子提交前校验。</summary>
  internal static void Write(
    string path,
    VibpProjectManifest manifest,
    IReadOnlyList<VibpProjectEntry> entries,
    long maximumOutputBytes,
    CancellationToken cancellationToken = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    ArgumentNullException.ThrowIfNull(manifest);
    ArgumentNullException.ThrowIfNull(entries);

    VibpProjectRules.ValidateManifest(manifest);

    Dictionary<string, VibpProjectEntry> provided = new(StringComparer.Ordinal);
    foreach (VibpProjectEntry entry in entries)
    {
      if (string.IsNullOrEmpty(entry.Name) || !provided.TryAdd(entry.Name, entry))
      {
        throw new VibpProjectFormatException(
          $"Project entry list has an empty or duplicated name: {entry.Name}");
      }
      if (entry.Length < 1)
      {
        throw new VibpProjectFormatException(
          $"Project entry {entry.Name} declares a non-positive length.");
      }
      ArgumentNullException.ThrowIfNull(entry.OpenRead);
    }
    List<string> expected = VibpProjectRules.ExpectedResourceEntryNames(manifest);
    foreach (string name in expected)
    {
      if (!provided.ContainsKey(name))
      {
        throw new VibpProjectFormatException(
          $"Project entry {name} is declared by the manifest but not provided.");
      }
    }
    foreach (string name in provided.Keys)
    {
      if (!expected.Contains(name))
      {
        throw new VibpProjectFormatException(
          $"Project entry {name} is not declared by the manifest.");
      }
    }
    // 声明长度与提供长度逐一闭合：不允许写出自读失败的包。
    foreach ((string name, long declaredLength) in VibpProjectRules.ExpectedResourceEntries(manifest))
    {
      if (provided[name].Length != declaredLength)
      {
        throw new VibpProjectFormatException(
          $"Project entry {name} length {provided[name].Length} does not match the manifest declared {declaredLength}.");
      }
    }

    byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, VibpProjectRules.ManifestJson);
    if (manifestBytes.LongLength > VibpProjectLimits.MaximumManifestBytes)
    {
      throw new VibpProjectFormatException(
        "Serialized project manifest exceeds the 256 KiB budget.");
    }

    AtomicFile.Write(path, destination =>
    {
      cancellationToken.ThrowIfCancellationRequested();
      using (var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true))
      {
        ZipArchiveEntry manifestEntry = archive.CreateEntry(VibpProjectLimits.ManifestEntryName);
        using (Stream stream = manifestEntry.Open())
        {
          stream.Write(manifestBytes);
        }
        foreach ((string name, VibpProjectEntry entry) in
          expected.Select(name => (name, provided[name])))
        {
          // entry 边界观察取消：后台压缩期间随时可中止。
          cancellationToken.ThrowIfCancellationRequested();
          ZipArchiveEntry zipEntry = archive.CreateEntry(name);
          using Stream source = entry.OpenRead();
          using Stream target = zipEntry.Open();
          CopyDeclared(source, target, entry.Length, name, cancellationToken);
        }
      }
      // 压缩后真实输出长度（含 ZIP 元数据开销）在原子替换前校验；
      // 超预算抛异常由 AtomicFile 保留旧文件并清理临时文件。
      if (destination.Length > maximumOutputBytes)
      {
        throw new VibpProjectFormatException(
          "Project package exceeds the 192 MiB file budget on write.");
      }
    }, cancellationToken);
  }

  /// <summary>有界读取工程包到 destinationDirectory：先 entry 总数/白名单/
  /// manifest 校验，再按实际流式字节数限额解包每个 entry 并核对声明长度，
  /// 最后用平台解码器元数据校验容器类型与维度（与 manifest 声明一致）。
  /// 任何失败抛 VibpProjectFormatException，调用方保持当前工程不变。</summary>
  public static Task<VibpProjectArchive> ReadAsync(
    string path,
    string destinationDirectory,
    CancellationToken cancellationToken = default) =>
    ReadAsync(path, destinationDirectory, VibpProjectLimits.MaximumDecompressedBytes, cancellationToken);

  /// <summary>带全局解压预算的读取（测试 seam）：manifest 与所有 entry 的实际字节数
  /// 共享同一计费，任一 chunk 读完且写盘前即检测。</summary>
  internal static async Task<VibpProjectArchive> ReadAsync(
    string path,
    string destinationDirectory,
    long maximumDecompressedBytes,
    CancellationToken cancellationToken = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
    cancellationToken.ThrowIfCancellationRequested();

    try
    {
      using var file = new FileStream(
        path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize,
        FileOptions.SequentialScan | FileOptions.Asynchronous);
      if (file.Length > VibpProjectLimits.MaximumFileBytes)
      {
        throw new VibpProjectFormatException(
          "Project file exceeds the 192 MiB package budget.");
      }
      // 中央目录有界预检：在 ZipArchive 物化全部 entry 对象前，用固定
      // 46 字节记录头+流式寻位实际计数（≤128），不信任 EOCD 16 位 count
      // （可伪造）；拒绝 ZIP64/多盘/越界结构，防止数百万空 entry 驱动
      // 巨量对象分配。仍由标准库完成实际解压。
      ValidateCentralDirectoryBudget(file, VibpProjectLimits.MaximumEntries);
      using var archive = new ZipArchive(file, ZipArchiveMode.Read);
      if (archive.Entries.Count > VibpProjectLimits.MaximumEntries)
      {
        throw new VibpProjectFormatException(
          $"Project package has more than {VibpProjectLimits.MaximumEntries} entries.");
      }
      Dictionary<string, ZipArchiveEntry> entries = CollectEntries(archive);

      long decompressedTotal = 0;
      VibpProjectManifest manifest;
      (manifest, decompressedTotal) = await ReadManifestAsync(
        entries, decompressedTotal, cancellationToken);
      ValidateEntrySet(manifest, entries);

      Directory.CreateDirectory(destinationDirectory);
      string sourceEntryName = VibpProjectRules.SourceEntryName(manifest.Source.MediaType);
      (string sourcePath, decompressedTotal) = await ExtractEntryAsync(
        entries[sourceEntryName],
        Path.Combine(destinationDirectory, sourceEntryName),
        manifest.Source.Length,
        maximumDecompressedBytes,
        decompressedTotal,
        cancellationToken);
      (int width, int height) = await ReadImageDimensionsAsync(
        sourcePath, manifest.Source.MediaType, cancellationToken);
      RequireSameDimensions("source", width, height,
        manifest.Source.Width, manifest.Source.Height);

      Dictionary<string, string> assetPaths = new(StringComparer.Ordinal);
      foreach (VibpAssetResource asset in manifest.Assets)
      {
        string entryName = $"asset-{asset.Id}.png";
        (string assetPath, decompressedTotal) = await ExtractEntryAsync(
          entries[entryName],
          Path.Combine(destinationDirectory, entryName),
          asset.Length,
          maximumDecompressedBytes,
          decompressedTotal,
          cancellationToken);
        (width, height) = await ReadImageDimensionsAsync(assetPath, "image/png", cancellationToken);
        RequireSameDimensions($"asset {asset.Id}", width, height, asset.Width, asset.Height);
        assetPaths.Add(asset.Id, assetPath);
      }

      Dictionary<int, string> patchPaths = [];
      foreach (VibpPatch patch in manifest.State.Patches)
      {
        string entryName = $"patch-{patch.Id}.png";
        (string patchPath, decompressedTotal) = await ExtractEntryAsync(
          entries[entryName],
          Path.Combine(destinationDirectory, entryName),
          patch.Length,
          maximumDecompressedBytes,
          decompressedTotal,
          cancellationToken);
        (width, height) = await ReadImageDimensionsAsync(patchPath, "image/png", cancellationToken);
        RequireSameDimensions($"patch {patch.Id}", width, height, patch.Width, patch.Height);
        patchPaths.Add(patch.Id, patchPath);
      }

      return new VibpProjectArchive
      {
        Manifest = manifest,
        SourcePath = sourcePath,
        AssetPaths = assetPaths,
        PatchPaths = patchPaths,
      };
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or
      InvalidDataException or NotSupportedException)
    {
      throw new VibpProjectFormatException(
        "Project package cannot be read.", error);
    }
  }

  /// <summary>平台元数据级维度/容器校验：FileStream → IRandomAccessStream →
  /// BitmapDecoder.CreateAsync 读取 OrientedPixelWidth/Height（与 InputService
  /// 导入路径及浏览器 <img> natural 尺寸同语义：EXIF 方向已应用，编辑器/补丁
  /// 的原图坐标空间即该定向后尺寸），不调用 GetPixelDataAsync，不分配整图像素缓冲。
  /// 保存路径（会话持有 source 的媒体类型核实）与读取路径共用同一 codec 判定，
  /// 防止 PNG 改名 .jpg 后写出不可重开的工程。</summary>
  internal static async Task<(int Width, int Height)> ReadImageDimensionsAsync(
    string path, string mediaType, CancellationToken cancellationToken)
  {
    Guid expectedCodec = mediaType switch
    {
      "image/png" => BitmapDecoder.PngDecoderId,
      "image/jpeg" => BitmapDecoder.JpegDecoderId,
      "image/bmp" => BitmapDecoder.BmpDecoderId,
      "image/webp" => WebpDecoderId,
      _ => throw new VibpProjectFormatException(
        $"Unsupported source media type: {mediaType}"),
    };
    try
    {
      await using FileStream stream = new(
        path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
      using var random = stream.AsRandomAccessStream();
      BitmapDecoder decoder = await BitmapDecoder.CreateAsync(random).AsTask(cancellationToken);
      if (decoder.DecoderInformation.CodecId != expectedCodec)
      {
        throw new VibpProjectFormatException(
          $"Project resource declared as {mediaType} is a different container.");
      }
      int width = checked((int)decoder.OrientedPixelWidth);
      int height = checked((int)decoder.OrientedPixelHeight);
      if (width < 1 || height < 1 ||
          width > VibpProjectLimits.MaximumImageSide || height > VibpProjectLimits.MaximumImageSide ||
          (long)width * height > VibpProjectLimits.MaximumSourcePixels)
      {
        throw new VibpProjectFormatException(
          $"Project {mediaType} resource dimensions exceed the supported image bounds.");
      }
      return (width, height);
    }
    catch (Exception error) when (error is not (VibpProjectFormatException or OperationCanceledException))
    {
      throw new VibpProjectFormatException(
        $"Project {mediaType} resource metadata cannot be read.", error);
    }
  }

  private static Dictionary<string, ZipArchiveEntry> CollectEntries(ZipArchive archive)
  {
    Dictionary<string, ZipArchiveEntry> entries = new(StringComparer.Ordinal);
    foreach (ZipArchiveEntry entry in archive.Entries)
    {
      string name = entry.FullName;
      if (name.Length == 0 || name.Contains('/') || name.Contains('\\') ||
          name.Contains(':') || name.EndsWith('.'))
      {
        throw new VibpProjectFormatException(
          $"Project package entry {name} is not a flat resource name.");
      }
      if (!entries.TryAdd(name, entry))
      {
        throw new VibpProjectFormatException(
          $"Project package contains a duplicated entry {name}.");
      }
    }
    if (!entries.ContainsKey(VibpProjectLimits.ManifestEntryName))
    {
      throw new VibpProjectFormatException(
        "Project package is missing manifest.json.");
    }
    return entries;
  }

  private static async Task<(VibpProjectManifest Manifest, long DecompressedTotal)> ReadManifestAsync(
    Dictionary<string, ZipArchiveEntry> entries, long decompressedTotal,
    CancellationToken cancellationToken)
  {
    (byte[] manifestBytes, long manifestLength) = await ReadEntryBoundedAsync(
      entries[VibpProjectLimits.ManifestEntryName], VibpProjectLimits.MaximumManifestBytes,
      cancellationToken);
    decompressedTotal += manifestLength;
    try
    {
      VibpProjectManifest? manifest =
        JsonSerializer.Deserialize<VibpProjectManifest>(manifestBytes, VibpProjectRules.ManifestJson);
      if (manifest is null)
      {
        throw new VibpProjectFormatException("Project manifest is empty.");
      }
      if (manifest.SchemaVersion != VibpProjectLimits.SchemaVersion)
      {
        throw new VibpProjectFormatException(
          $"Unsupported .vibp schemaVersion {manifest.SchemaVersion}; " +
          $"expected {VibpProjectLimits.SchemaVersion}.");
      }
      VibpProjectRules.ValidateManifest(manifest);
      return (manifest, decompressedTotal);
    }
    catch (JsonException error)
    {
      throw new VibpProjectFormatException(
        "Project manifest is not valid v1 JSON.", error);
    }
  }

  private static void ValidateEntrySet(VibpProjectManifest manifest, Dictionary<string, ZipArchiveEntry> entries)
  {
    HashSet<string> declared = [
      .. VibpProjectRules.ExpectedResourceEntryNames(manifest),
      VibpProjectLimits.ManifestEntryName,
    ];
    foreach (string name in declared)
    {
      if (!entries.ContainsKey(name))
      {
        throw new VibpProjectFormatException(
          $"Project package is missing declared entry {name}.");
      }
    }
    foreach (string name in entries.Keys)
    {
      if (!declared.Contains(name))
      {
        throw new VibpProjectFormatException(
          $"Project package contains undeclared entry {name}.");
      }
    }
  }

  private static async Task<(byte[] Bytes, long Length)> ReadEntryBoundedAsync(
    ZipArchiveEntry entry, long maximumBytes, CancellationToken cancellationToken)
  {
    await using Stream stream = entry.Open();
    byte[] buffer = new byte[CopyBufferSize];
    using var content = new MemoryStream();
    long total = 0;
    int read;
    while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
    {
      total += read;
      if (total > maximumBytes)
      {
        throw new VibpProjectFormatException(
          $"Project entry {entry.FullName} exceeds its byte budget.");
      }
      content.Write(buffer, 0, read);
    }
    return (content.ToArray(), total);
  }

  /// <summary>按实际流式读取量限额解包：manifest 与全部 entry 共享同一全局解压预算，
  /// 且每个 chunk 读完、写盘前即检测累计总量与该 entry 声明长度；
  /// 声明长度已在 manifest 校验 ≤ 单 entry 上限，不信任 ZIP 元数据的长度。
  /// 异步 chunk 读写并逐 chunk 响应取消：不在 UI 线程持续同步解压。</summary>
  internal static async Task<(string Path, long DecompressedTotal)> ExtractEntryAsync(
    ZipArchiveEntry entry,
    string destinationPath,
    long declaredLength,
    long maximumDecompressedBytes,
    long decompressedTotal,
    CancellationToken cancellationToken)
  {
    await using Stream source = entry.Open();
    await using var destination = new FileStream(
      destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
      CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
    byte[] buffer = new byte[CopyBufferSize];
    long total = 0;
    int read;
    while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
    {
      total += read;
      decompressedTotal += read;
      if (total > declaredLength)
      {
        throw new VibpProjectFormatException(
          $"Project entry {entry.FullName} exceeds its declared length {declaredLength}.");
      }
      if (decompressedTotal > maximumDecompressedBytes)
      {
        throw new VibpProjectFormatException(
          "Project package exceeds the 192 MiB decompressed budget.");
      }
      await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
    }
    if (total != declaredLength)
    {
      throw new VibpProjectFormatException(
        $"Project entry {entry.FullName} length {total} does not match the declared {declaredLength}.");
    }
    return (destinationPath, decompressedTotal);
  }

  private static void CopyDeclared(
    Stream source, Stream target, long declaredLength, string name, CancellationToken cancellationToken)
  {
    byte[] buffer = new byte[CopyBufferSize];
    long total = 0;
    int read;
    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
    {
      cancellationToken.ThrowIfCancellationRequested();
      total += read;
      if (total > declaredLength)
      {
        throw new VibpProjectFormatException(
          $"Project entry {name} produced more bytes than declared.");
      }
      target.Write(buffer, 0, read);
    }
    if (total != declaredLength)
    {
      throw new VibpProjectFormatException(
        $"Project entry {name} produced {total} bytes but declared {declaredLength}.");
    }
  }

  private static void RequireSameDimensions(string field, int actualWidth, int actualHeight, int declaredWidth, int declaredHeight)
  {
    if (actualWidth != declaredWidth || actualHeight != declaredHeight)
    {
      throw new VibpProjectFormatException(
        $"Project {field} is {actualWidth}x{actualHeight} but the manifest declares {declaredWidth}x{declaredHeight}.");
    }
  }

  private const uint EndOfCentralDirectorySignature = 0x06054b50;
  private const uint CentralDirectoryHeaderSignature = 0x02014b50;
  private const uint Zip64EndOfCentralDirectoryLocatorSignature = 0x07064b50;
  private const int EndOfCentralDirectoryFixedBytes = 22;
  private const int Zip64LocatorBytes = 20;
  private const int CentralDirectoryHeaderFixedBytes = 46;

  /// <summary>中央目录有界预检（ZipArchive 物化 entry 对象前）：
  /// 尾部有界扫描最后一个 EOCD（后 64KiB+22），完整解析盘号/entry 数/
  /// 目录大小/偏移/注释长度；拒绝多盘、任何 ZIP64 哨兵或 locator（v1
  /// 预算 ≤192MiB/≤128 entry 不需要 ZIP64）、注释长度不闭合到文件尾
  /// （防止伪造前位 EOCD）、目录偏移/大小越界。随后按 EOCD 偏移寻位
  /// 中央目录逐记录读 46 字节固定头（名称/额外/注释仅寻位不读入）真实
  /// 计数——不信任 EOCD 16 位 count（可伪造）；超限在物化前拒绝。
  /// 闭合校验：真实记录数与 EOCD totalEntries 一致、扫描终点落在
  /// EOCD 声明的目录大小范围内、每记录本地头偏移在文件内。</summary>
  private static void ValidateCentralDirectoryBudget(FileStream file, int maximumEntries)
  {
    long fileLength = file.Length;
    long searchFloor = Math.Max(0, fileLength - 64 * 1024 - EndOfCentralDirectoryFixedBytes);
    byte[] tail = new byte[fileLength - searchFloor];
    file.Position = searchFloor;
    file.ReadExactly(tail, 0, tail.Length);

    // 从尾到头找 EOCD 签名（最后一次出现）。
    int eocdOffset = -1;
    for (int index = tail.Length - EndOfCentralDirectoryFixedBytes; index >= 0; index--)
    {
      if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(index, 4)) ==
        EndOfCentralDirectorySignature)
      {
        eocdOffset = index;
        break;
      }
    }
    if (eocdOffset < 0)
    {
      throw new VibpProjectFormatException(
        "Project package is missing its zip end-of-central-directory.");
    }
    ReadOnlySpan<byte> eocd = tail.AsSpan(eocdOffset, EndOfCentralDirectoryFixedBytes);
    ushort thisDisk = BinaryPrimitives.ReadUInt16LittleEndian(eocd.Slice(4, 2));
    ushort startDisk = BinaryPrimitives.ReadUInt16LittleEndian(eocd.Slice(6, 2));
    ushort diskEntries = BinaryPrimitives.ReadUInt16LittleEndian(eocd.Slice(8, 2));
    ushort totalEntries = BinaryPrimitives.ReadUInt16LittleEndian(eocd.Slice(10, 2));
    uint directorySize = BinaryPrimitives.ReadUInt32LittleEndian(eocd.Slice(12, 4));
    uint directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(eocd.Slice(16, 4));
    ushort commentLength = BinaryPrimitives.ReadUInt16LittleEndian(eocd.Slice(20, 2));
    if (thisDisk != 0 || startDisk != 0)
    {
      throw new VibpProjectFormatException(
        "Project package spans multiple disks.");
    }
    // 注释长度必须闭合到文件尾：确认这确实是最后一个 EOCD，防止标准库
    // 向前选到另一个伪造 EOCD 绕过预算。
    long eocdAbsolute = searchFloor + eocdOffset;
    if (eocdAbsolute + EndOfCentralDirectoryFixedBytes + commentLength != fileLength)
    {
      throw new VibpProjectFormatException(
        "Project package end-of-central-directory comment length is invalid.");
    }
    // v1 不支持 ZIP64：任何哨兵值（16/32 位 0xFFFF/0xFFFFFFFF）即拒绝。
    if (diskEntries == 0xFFFF || totalEntries == 0xFFFF ||
      directorySize == 0xFFFFFFFF || directoryOffset == 0xFFFFFFFF)
    {
      throw new VibpProjectFormatException(
        "Project package uses unsupported ZIP64 extensions.");
    }
    // EOCD 前紧邻 20 字节若为 ZIP64 locator 同样拒绝（无哨兵变体）。
    if (eocdOffset >= Zip64LocatorBytes &&
      BinaryPrimitives.ReadUInt32LittleEndian(
        tail.AsSpan(eocdOffset - Zip64LocatorBytes, 4)) ==
        Zip64EndOfCentralDirectoryLocatorSignature)
    {
      throw new VibpProjectFormatException(
        "Project package uses unsupported ZIP64 extensions.");
    }
    if (directoryOffset >= fileLength ||
      (long)directoryOffset + directorySize > eocdAbsolute)
    {
      throw new VibpProjectFormatException(
        "Project package central directory offset or size is out of bounds.");
    }

    // 真实计数：逐记录固定头寻位（名称/额外/注释不读入），超限在
    // ZipArchive 物化前拒绝；越界/本地头偏移越界同样拒绝。
    file.Position = directoryOffset;
    byte[] header = new byte[CentralDirectoryHeaderFixedBytes];
    int records = 0;
    long position = directoryOffset;
    long directoryEnd = (long)directoryOffset + directorySize;
    while (position + CentralDirectoryHeaderFixedBytes <= fileLength)
    {
      file.Position = position;
      file.ReadExactly(header, 0, header.Length);
      if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4)) !=
        CentralDirectoryHeaderSignature)
      {
        break;
      }
      records++;
      if (records > maximumEntries)
      {
        throw new VibpProjectFormatException(
          $"Project package has more than {maximumEntries} entries.");
      }
      ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28, 2));
      ushort extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30, 2));
      ushort recordCommentLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32, 2));
      uint localHeaderOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(42, 4));
      if (localHeaderOffset >= fileLength)
      {
        throw new VibpProjectFormatException(
          "Project package central directory record points outside the file.");
      }
      long advance = CentralDirectoryHeaderFixedBytes +
        (long)nameLength + extraLength + recordCommentLength;
      if (position + advance > fileLength)
      {
        throw new VibpProjectFormatException(
          "Project package central directory record is truncated.");
      }
      position += advance;
    }
    if (records == 0)
    {
      throw new VibpProjectFormatException(
        "Project package central directory is empty.");
    }
    // 闭合：真实计数与 EOCD totalEntries 一致；扫描终点落在声明目录范围
    // 内（多余目录字节或提前终止均拒绝）。
    if (records != totalEntries)
    {
      throw new VibpProjectFormatException(
        "Project package central directory entry count does not match its declaration.");
    }
    if (position != directoryEnd)
    {
      throw new VibpProjectFormatException(
        "Project package central directory size does not match its records.");
    }
  }
}

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using VibeOCR.App.Workbench;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace VibeOCR.App.Tests;

/// <summary>#233 .vibp v1 工程格式：真实包 round-trip 与损坏/超限/引用/几何拒绝路径。
/// 图片 fixture 全部是平台可解码的真实编码字节（PNG 自带完整 zlib/CRC，
/// JPEG/BMP/EXIF 定向 JPEG 经 WIC BitmapEncoder 真实编码），不使用伪头。</summary>
public sealed class VibpProjectFileTests
{
  private const string AssetId = "0123456789abcdef0123456789abcdef";

  [Fact]
  public async Task RoundTripSupportsRealWebpSource()
  {
    // 真实 VP8 WebP（Pillow 生成），验证本机 WIC 解码器 CodecId 与 round-trip。
    byte[] source = Convert.FromBase64String(
      "UklGRjoAAABXRUJQVlA4IC4AAACQAQCdASoEAAMAAUAmJaACdLoAA5gA/vCbQ/4DdfFtMv/ucD/uyf/2yf+pAAAA");
    Assert.Equal("RIFF"u8.ToArray(), source[..4]);
    (Guid codecId, int width, int height) = await ProbeImageAsync(source);
    // CodecId 由本机 decoder 实证，与模块常量一致（不凭记忆断言）。
    Assert.Equal(VibpProjectFile.WebpDecoderId, codecId);
    Assert.Equal((4, 3), (width, height));

    string directory = NewTempDirectory();
    try
    {
      var manifest = new VibpProjectManifest
      {
        SchemaVersion = 1,
        Canvas = new VibpCanvas { Width = 900, Height = 600 },
        Source = new VibpSourceResource
        {
          MediaType = "image/webp", Width = width, Height = height, Length = source.Length,
        },
        Assets = [],
        State = new VibpEditorState { Rotation = 0, Marks = [] },
      };
      string packagePath = Path.Combine(directory, "project.vibp");
      VibpProjectFile.Write(packagePath, manifest,
        [new VibpProjectEntry("source.webp", source.Length, () => new MemoryStream(source))]);

      VibpProjectArchive archive = await VibpProjectFile.ReadAsync(
        packagePath, Path.Combine(directory, "open"), TestContext.Current.CancellationToken);

      Assert.Equal(source, File.ReadAllBytes(archive.SourcePath));
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public async Task RoundTripsSolidToolAndContinuousIntensityBrushSize()
  {
    string directory = NewTempDirectory();
    try
    {
      byte[] source = PngBytes(64, 48);
      var manifest = new VibpProjectManifest
      {
        SchemaVersion = 1,
        Canvas = new VibpCanvas { Width = 900, Height = 600 },
        Source = new VibpSourceResource
        {
          MediaType = "image/png", Width = 64, Height = 48, Length = source.Length,
        },
        Assets = [],
        State = new VibpEditorState
        {
          Rotation = 0,
          Marks =
          [
            new VibpMark
            {
              Id = "mark-1",
              Tool = "solid",
              Start = new VibpPoint { X = 1, Y = 2 },
              End = new VibpPoint { X = 30, Y = 20 },
              Style = new VibpMarkStyle
              {
                Color = "#010203",
                Intensity = 1.7,
                BrushSize = 42.5,
                StrokeOpacity = 65,
              },
            },
          ],
        },
      };
      string packagePath = Path.Combine(directory, "solid.vibp");
      VibpProjectFile.Write(packagePath, manifest,
        [new VibpProjectEntry("source.png", source.Length, () => new MemoryStream(source))]);

      VibpProjectArchive archive = await VibpProjectFile.ReadAsync(
        packagePath, Path.Combine(directory, "open"), TestContext.Current.CancellationToken);

      VibpMark roundTripped = Assert.Single(archive.Manifest.State.Marks);
      VibpMarkStyle? roundTrippedStyle = roundTripped.Style;
      Assert.NotNull(roundTrippedStyle);
      Assert.Equal("solid", roundTripped.Tool);
      Assert.Equal(1.7, roundTrippedStyle.Intensity);
      Assert.Equal(42.5, roundTrippedStyle.BrushSize);
      Assert.Equal(65, roundTrippedStyle.StrokeOpacity);

      // 超界 intensity 仍拒绝。
      var invalid = manifest with
      {
        State = manifest.State with
        {
          Marks = [roundTripped with { Style = roundTrippedStyle with { Intensity = 3.5 } }],
        },
      };
      Assert.Throws<VibpProjectFormatException>(() => VibpProjectFile.Write(
        Path.Combine(directory, "bad.vibp"), invalid,
        [new VibpProjectEntry("source.png", source.Length, () => new MemoryStream(source))]));
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public void CanvasTotalPixelBudgetIsEnforcedOnMetadataOnly()
  {
    string directory = NewTempDirectory();
    try
    {
      byte[] source = PngBytes(8, 6);
      VibpProjectManifest Base(int width, int height) => new()
      {
        SchemaVersion = 1,
        Canvas = new VibpCanvas { Width = width, Height = height },
        Source = new VibpSourceResource
        {
          MediaType = "image/png", Width = 8, Height = 6, Length = source.Length,
        },
        Assets = [],
        State = new VibpEditorState { Rotation = 0, Marks = [] },
      };
      // 边界：8000×8000 恰好 64MP 允许；小合法 source 不受影响。
      VibpProjectFile.Write(Path.Combine(directory, "edge.vibp"), Base(8000, 8000),
        [new VibpProjectEntry("source.png", source.Length, () => new MemoryStream(source))]);
      // 超 1 像素即拒绝；32768² 巨大逻辑 canvas 同样拒绝（元数据层，不分配）。
      foreach ((int width, int height) in new[] { (8000, 8001), (32768, 32768) })
      {
        VibpProjectFormatException error = Assert.Throws<VibpProjectFormatException>(
          () => VibpProjectFile.Write(
            Path.Combine(directory, $"bad-{width}x{height}.vibp"), Base(width, height),
            [new VibpProjectEntry("source.png", source.Length, () => new MemoryStream(source))]));
        Assert.Contains("canvas", error.Message);
      }
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public void ResizeBudgetMatchesEditorOutputLimitsOnMetadataOnly()
  {
    string directory = NewTempDirectory();
    try
    {
      byte[] source = PngBytes(8, 6);
      VibpProjectManifest Base(int width, int height) => new()
      {
        SchemaVersion = 1,
        Canvas = new VibpCanvas { Width = 64, Height = 48 },
        Source = new VibpSourceResource
        {
          MediaType = "image/png", Width = 8, Height = 6, Length = source.Length,
        },
        Assets = [],
        State = new VibpEditorState
        {
          Rotation = 0,
          Marks = [],
          Resize = new VibpSize { Width = width, Height = height },
        },
      };
      // 边界：8000×8000 恰好 64MP 且单边 ≤16384 允许。
      VibpProjectFile.Write(Path.Combine(directory, "edge.vibp"), Base(8000, 8000),
        [new VibpProjectEntry("source.png", source.Length, () => new MemoryStream(source))]);
      // 单边超 16384（旧 32768 边界内、总量未超）与总量超 64MP 均拒绝。
      foreach ((int width, int height) in new[] { (16385, 2), (16384, 4096) })
      {
        VibpProjectFormatException error = Assert.Throws<VibpProjectFormatException>(
          () => VibpProjectFile.Write(
            Path.Combine(directory, $"bad-{width}x{height}.vibp"), Base(width, height),
            [new VibpProjectEntry("source.png", source.Length, () => new MemoryStream(source))]));
        Assert.Contains("resize", error.Message);
      }
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public async Task RoundTripsZeroWidthTextOutlineAndRejectsNegative()
  {
    string directory = NewTempDirectory();
    try
    {
      byte[] source = PngBytes(64, 48);
      var manifest = new VibpProjectManifest
      {
        SchemaVersion = 1,
        Canvas = new VibpCanvas { Width = 900, Height = 600 },
        Source = new VibpSourceResource
        {
          MediaType = "image/png", Width = 64, Height = 48, Length = source.Length,
        },
        Assets = [],
        State = new VibpEditorState
        {
          Rotation = 0,
          Marks =
          [
            new VibpMark
            {
              Id = "mark-1",
              Tool = "text",
              Start = new VibpPoint { X = 1, Y = 1 },
              End = new VibpPoint { X = 80, Y = 40 },
              Text = "勾开再关",
              TextWidth = 100,
              Style = new VibpMarkStyle
              {
                TextOutline = new VibpTextOutlineStyle { Color = "#161616", Width = 0 },
              },
            },
          ],
        },
      };
      string packagePath = Path.Combine(directory, "outline.vibp");
      VibpProjectFile.Write(packagePath, manifest,
        [new VibpProjectEntry("source.png", source.Length, () => new MemoryStream(source))]);

      VibpProjectArchive archive = await VibpProjectFile.ReadAsync(
        packagePath, Path.Combine(directory, "open"), TestContext.Current.CancellationToken);
      VibpMark roundTripped = Assert.Single(archive.Manifest.State.Marks);
      Assert.NotNull(roundTripped.Style?.TextOutline);
      Assert.Equal(0, roundTripped.Style.TextOutline.Width);
      Assert.Equal("#161616", roundTripped.Style.TextOutline.Color);

      // 负数与超上限仍拒绝。
      foreach (double badWidth in new[] { -0.5, 1001 })
      {
        var invalid = manifest with
        {
          State = manifest.State with
          {
            Marks =
            [
              roundTripped with
              {
                Style = roundTripped.Style with
                {
                  TextOutline = new VibpTextOutlineStyle { Color = "#161616", Width = badWidth },
                },
              },
            ],
          },
        };
        Assert.Throws<VibpProjectFormatException>(() => VibpProjectFile.Write(
          Path.Combine(directory, $"bad-outline-{badWidth}.vibp"), invalid,
          [new VibpProjectEntry("source.png", source.Length, () => new MemoryStream(source))]));
      }
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Theory]
  [InlineData("mosaic", 0)]
  [InlineData("mosaic", 1)]
  [InlineData("mosaic", 2)]
  [InlineData("blur", 0)]
  [InlineData("blur", 1)]
  [InlineData("blur", 2)]
  public async Task RoundTripsRectangleAndBrushEffects(string tool, int pointCount)
  {
    string directory = NewTempDirectory();
    try
    {
      byte[] source = PngBytes(64, 48);
      VibpMark mark = new()
      {
        Id = "mark-1", Tool = tool,
        Start = new VibpPoint { X = 1, Y = 2 },
        End = new VibpPoint { X = 30, Y = 20 },
        Points = pointCount == 0 ? null : new VibpPoint[]
          { new() { X = 1, Y = 2 }, new() { X = 30, Y = 20 } }.Take(pointCount).ToArray(),
        Style = new VibpMarkStyle { Intensity = 1.7, BrushSize = 42.5 },
      };
      var manifest = new VibpProjectManifest
      {
        SchemaVersion = 1, Canvas = new VibpCanvas { Width = 900, Height = 600 },
        Source = new VibpSourceResource
        {
          MediaType = "image/png", Width = 64, Height = 48, Length = source.Length,
        },
        Assets = [], State = new VibpEditorState { Rotation = 0, Marks = [mark] },
      };
      string path = Path.Combine(directory, "effect.vibp");
      VibpProjectEntry[] entries =
        [new("source.png", source.Length, () => new MemoryStream(source))];
      VibpProjectFile.Write(path, manifest, entries);
      VibpProjectArchive archive = await VibpProjectFile.ReadAsync(
        path, Path.Combine(directory, "open"), TestContext.Current.CancellationToken);
      VibpMark restored = Assert.Single(archive.Manifest.State.Marks);
      Assert.Equal(tool, restored.Tool);
      Assert.Equal(mark.Style, restored.Style);
      Assert.Equal(mark.Points, restored.Points);

      if (pointCount > 0)
      {
        foreach (IReadOnlyList<VibpPoint> points in new IReadOnlyList<VibpPoint>[]
        {
          [],
          [new() { X = double.NaN, Y = 2 }, new() { X = 3, Y = 4 }],
          Enumerable.Repeat(new VibpPoint { X = 1, Y = 2 },
            VibpProjectLimits.MaximumMarkPoints / 2 + 1).ToArray(),
        })
        {
          var invalid = manifest with
          {
            State = manifest.State with
            {
              Marks = [mark with { Points = points }, mark with { Id = "mark-2", Points = points }],
            },
          };
          Assert.Throws<VibpProjectFormatException>(() =>
            VibpProjectFile.Write(path, invalid, entries));
        }
      }
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public async Task RejectsGlobalDecompressedBudgetAcrossEntries()
  {
    string directory = NewTempDirectory();
    try
    {
      byte[] source = PngBytes(64, 48);
      string manifestJson = ManifestJson(source.Length);
      string packagePath = Path.Combine(directory, "project.vibp");
      WriteRawPackage(packagePath, manifestJson, ("source.png", source));

      // 预算只够 manifest + 5 字节：source 流式读到中段必须触发全局计费拒绝。
      long tinyBudget = Encoding.UTF8.GetByteCount(manifestJson) + 5;
      VibpProjectFormatException error = await Assert.ThrowsAsync<VibpProjectFormatException>(
        () => VibpProjectFile.ReadAsync(
          packagePath, Path.Combine(directory, "open"), tinyBudget, TestContext.Current.CancellationToken));
      Assert.Contains("decompressed", error.Message);
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public void WriteRejectsManifestAndEntryLengthMismatch()
  {
    string directory = NewTempDirectory();
    try
    {
      byte[] source = PngBytes(4, 3);
      var manifest = new VibpProjectManifest
      {
        SchemaVersion = 1,
        Canvas = new VibpCanvas { Width = 900, Height = 600 },
        Source = new VibpSourceResource
        {
          MediaType = "image/png", Width = 4, Height = 3, Length = source.Length,
        },
        Assets = [],
        State = new VibpEditorState { Rotation = 0, Marks = [] },
      };
      string packagePath = Path.Combine(directory, "project.vibp");
      // entry 声明长度与 manifest 不一致：不允许写出自读失败的包。
      VibpProjectFormatException error = Assert.Throws<VibpProjectFormatException>(() =>
        VibpProjectFile.Write(packagePath, manifest,
          [new VibpProjectEntry("source.png", source.Length - 1, () => new MemoryStream(source))]));
      Assert.Contains("does not match", error.Message);
      Assert.False(File.Exists(packagePath));
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public void WriteRejectsOutputExceedingFileBudgetBeforeCommit()
  {
    string directory = NewTempDirectory();
    try
    {
      byte[] source = PngBytes(4, 3);
      var manifest = new VibpProjectManifest
      {
        SchemaVersion = 1,
        Canvas = new VibpCanvas { Width = 900, Height = 600 },
        Source = new VibpSourceResource
        {
          MediaType = "image/png", Width = 4, Height = 3, Length = source.Length,
        },
        Assets = [],
        State = new VibpEditorState { Rotation = 0, Marks = [] },
      };
      string packagePath = Path.Combine(directory, "project.vibp");
      // 压缩后真实输出远大于 10 字节：原子提交前拒绝且不留临时文件。
      Assert.Throws<VibpProjectFormatException>(() => VibpProjectFile.Write(
        packagePath, manifest,
        [new VibpProjectEntry("source.png", source.Length, () => new MemoryStream(source))],
        maximumOutputBytes: 10));
      Assert.False(File.Exists(packagePath));
      Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Theory]
  [InlineData("canvas")]
  [InlineData("source")]
  [InlineData("state")]
  public async Task RejectsExplicitNullManifestSections(string section)
  {
    string directory = NewTempDirectory();
    try
    {
      byte[] source = PngBytes(4, 3);
      string manifest = "{\n" +
        "  \"schemaVersion\": 1,\n" +
        $"  \"canvas\": {SectionJson(section, "canvas", "{ \"width\": 900, \"height\": 600 }")},\n" +
        $"  \"source\": {SectionJson(section, "source", $"{{ \"mediaType\": \"image/png\", \"width\": 4, \"height\": 3, \"length\": {source.Length} }}")},\n" +
        "  \"assets\": [],\n" +
        $"  \"state\": {SectionJson(section, "state", "{ \"rotation\": 0, \"marks\": [] }")}\n" +
        "}";
      string packagePath = Path.Combine(directory, "project.vibp");
      WriteRawPackage(packagePath, manifest, ("source.png", source));

      VibpProjectFormatException error = await Assert.ThrowsAsync<VibpProjectFormatException>(
        () => VibpProjectFile.ReadAsync(
          packagePath, Path.Combine(directory, "open"), TestContext.Current.CancellationToken));
      Assert.Contains("required section", error.Message);
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  private static string SectionJson(string section, string target, string value) =>
    section == target ? "null" : value;

  [Fact]
  public async Task RejectsExplicitNullMarkPoint()
  {
    string directory = NewTempDirectory();
    try
    {
      byte[] source = PngBytes(4, 3);
      string manifest = ManifestJson(source.Length).Replace("\"marks\": []",
        "\"marks\": [" + MarkJson("\"tool\": \"rectangle\", \"start\": null") + "]");
      string packagePath = Path.Combine(directory, "project.vibp");
      WriteRawPackage(packagePath, manifest, ("source.png", source));

      await Assert.ThrowsAsync<VibpProjectFormatException>(
        () => VibpProjectFile.ReadAsync(
          packagePath, Path.Combine(directory, "open"), TestContext.Current.CancellationToken));
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public async Task RejectsDuplicateJsonProperties()
  {
    string directory = NewTempDirectory();
    try
    {
      byte[] source = PngBytes(4, 3);
      string manifest = ManifestJson(source.Length).Replace(
        "\"width\": 900", "\"width\": 900, \"width\": 900");
      string packagePath = Path.Combine(directory, "project.vibp");
      WriteRawPackage(packagePath, manifest, ("source.png", source));

      await Assert.ThrowsAsync<VibpProjectFormatException>(
        () => VibpProjectFile.ReadAsync(
          packagePath, Path.Combine(directory, "open"), TestContext.Current.CancellationToken));
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public async Task RejectsNullAssetIdInManifest()
  {
    string directory = NewTempDirectory();
    try
    {
      byte[] source = PngBytes(4, 3);
      byte[] asset = PngBytes(2, 2);
      // 显式 null 资产 id：不可信输入必须得到格式异常而非裸 ArgumentNullException。
      string manifest = $$"""
        {
          "schemaVersion": 1,
          "canvas": { "width": 900, "height": 600 },
          "source": { "mediaType": "image/png", "width": 4, "height": 3, "length": {{source.Length}} },
          "assets": [
            { "id": null, "width": 2, "height": 2, "length": {{asset.Length}} }
          ],
          "state": { "rotation": 0, "marks": [] }
        }
        """;
      string packagePath = Path.Combine(directory, "project.vibp");
      WriteRawPackage(packagePath, manifest,
        ("source.png", source),
        ($"asset-{AssetId}.png", asset));

      VibpProjectFormatException error = await Assert.ThrowsAsync<VibpProjectFormatException>(
        () => VibpProjectFile.ReadAsync(
          packagePath, Path.Combine(directory, "open"), TestContext.Current.CancellationToken));
      Assert.Contains("asset id", error.Message);
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public async Task RejectsNullMarkIdInManifest()
  {
    string directory = NewTempDirectory();
    try
    {
      byte[] source = PngBytes(4, 3);
      string manifest = ManifestJson(source.Length).Replace("\"marks\": []",
        "\"marks\": [" + MarkJson("\"tool\": \"rectangle\", \"start\": { \"x\": 1, \"y\": 1 }").Replace(
          "\"id\": \"mark-1\"", "\"id\": null") + "]");
      string packagePath = Path.Combine(directory, "project.vibp");
      WriteRawPackage(packagePath, manifest, ("source.png", source));

      VibpProjectFormatException error = await Assert.ThrowsAsync<VibpProjectFormatException>(
        () => VibpProjectFile.ReadAsync(
          packagePath, Path.Combine(directory, "open"), TestContext.Current.CancellationToken));
      Assert.Contains("mark id", error.Message);
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public async Task RejectsNullMarkToolAsFormatException()
  {
    string directory = NewTempDirectory();
    try
    {
      byte[] source = PngBytes(4, 3);
      string manifest = ManifestJson(source.Length).Replace("\"marks\": []",
        "\"marks\": [" + MarkJson("\"tool\": null, \"start\": { \"x\": 1, \"y\": 1 }") + "]");
      string packagePath = Path.Combine(directory, "project.vibp");
      WriteRawPackage(packagePath, manifest, ("source.png", source));

      VibpProjectFormatException error = await Assert.ThrowsAsync<VibpProjectFormatException>(
        () => VibpProjectFile.ReadAsync(
          packagePath, Path.Combine(directory, "open"), TestContext.Current.CancellationToken));
      Assert.Contains("unknown tool", error.Message);
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public async Task RoundTripPreservesBytesAndManifestSemantics()
  {
    string directory = NewTempDirectory();
    try
    {
      byte[] source = PngBytes(6, 4);
      byte[] asset = PngBytes(2, 2);
      byte[] patch1 = PngBytes(3, 1);
      byte[] patch2 = PngBytes(1, 2);
      VibpProjectManifest manifest = FullManifest(source, asset, patch1, patch2);
      string packagePath = Path.Combine(directory, "project.vibp");

      VibpProjectFile.Write(packagePath, manifest,
      [
        new VibpProjectEntry("source.png", source.Length, () => new MemoryStream(source)),
        new VibpProjectEntry($"asset-{AssetId}.png", asset.Length, () => new MemoryStream(asset)),
        new VibpProjectEntry("patch-1.png", patch1.Length, () => new MemoryStream(patch1)),
        new VibpProjectEntry("patch-2.png", patch2.Length, () => new MemoryStream(patch2)),
      ]);

      string extractDirectory = Path.Combine(directory, "open");
      VibpProjectArchive archive = await VibpProjectFile.ReadAsync(
        packagePath, extractDirectory, TestContext.Current.CancellationToken);

      string expected = JsonSerializer.Serialize(manifest, VibpProjectRules.ManifestJson);
      string actual = JsonSerializer.Serialize(archive.Manifest, VibpProjectRules.ManifestJson);
      Assert.Equal(expected, actual);

      Assert.Equal(source, File.ReadAllBytes(archive.SourcePath));
      Assert.Equal(asset, File.ReadAllBytes(archive.AssetPaths[AssetId]));
      Assert.Equal(patch1, File.ReadAllBytes(archive.PatchPaths[1]));
      Assert.Equal(patch2, File.ReadAllBytes(archive.PatchPaths[2]));
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Theory]
  [InlineData("image/jpeg")]
  [InlineData("image/bmp")]
  public async Task RoundTripSupportsNonPngSources(string mediaType)
  {
    string directory = NewTempDirectory();
    try
    {
      byte[] source = await EncodeImageAsync(mediaType, 4, 3);
      string entryName = mediaType switch
      {
        "image/jpeg" => "source.jpg",
        _ => "source.bmp",
      };
      var manifest = new VibpProjectManifest
      {
        SchemaVersion = 1,
        Canvas = new VibpCanvas { Width = 900, Height = 600 },
        Source = new VibpSourceResource
        {
          MediaType = mediaType, Width = 4, Height = 3, Length = source.Length,
        },
        Assets = [],
        State = new VibpEditorState { Rotation = 0, Marks = [] },
      };
      string packagePath = Path.Combine(directory, "project.vibp");
      VibpProjectFile.Write(packagePath, manifest,
        [new VibpProjectEntry(entryName, source.Length, () => new MemoryStream(source))]);

      VibpProjectArchive archive = await VibpProjectFile.ReadAsync(
        packagePath, Path.Combine(directory, "open"), TestContext.Current.CancellationToken);

      Assert.Equal(4, archive.Manifest.Source.Width);
      Assert.Equal(source, File.ReadAllBytes(archive.SourcePath));
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  /// <summary>EXIF 定向 JPEG：manifest 声明浏览器 natural（定向后）尺寸；
  /// 校验必须沿 OrientedPixelWidth/Height 语义，未定向 PixelWidth 会错判为不匹配。</summary>
  [Fact]
  public async Task RoundTripSupportsExifOrientedJpegSource()
  {
    string directory = NewTempDirectory();
    try
    {
      byte[] source = await EncodeOrientedJpegAsync(4, 3, orientation: 6);
      (int orientedWidth, int orientedHeight, int pixelWidth, int pixelHeight) =
        await ProbeJpegDimensionsAsync(source);
      // fixture 事实自证：EXIF orientation=6 已写入，定向后宽高互换。
      Assert.Equal(4, pixelWidth);
      Assert.Equal(3, pixelHeight);
      Assert.Equal(3, orientedWidth);
      Assert.Equal(4, orientedHeight);

      var manifest = new VibpProjectManifest
      {
        SchemaVersion = 1,
        Canvas = new VibpCanvas { Width = 900, Height = 600 },
        Source = new VibpSourceResource
        {
          MediaType = "image/jpeg",
          Width = orientedWidth,
          Height = orientedHeight,
          Length = source.Length,
        },
        Assets = [],
        State = new VibpEditorState { Rotation = 0, Marks = [] },
      };
      string packagePath = Path.Combine(directory, "project.vibp");
      VibpProjectFile.Write(packagePath, manifest,
        [new VibpProjectEntry("source.jpg", source.Length, () => new MemoryStream(source))]);

      VibpProjectArchive archive = await VibpProjectFile.ReadAsync(
        packagePath, Path.Combine(directory, "open"), TestContext.Current.CancellationToken);

      Assert.Equal(orientedWidth, archive.Manifest.Source.Width);
      Assert.Equal(orientedHeight, archive.Manifest.Source.Height);
      Assert.Equal(source, File.ReadAllBytes(archive.SourcePath));
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public async Task RejectsSourceContainerMismatch()
  {
    string directory = NewTempDirectory();
    try
    {
      // manifest 声明 PNG，entry 实际是平台真实编码的 JPEG。
      byte[] jpeg = await EncodeImageAsync("image/jpeg", 4, 3);
      var manifest = new VibpProjectManifest
      {
        SchemaVersion = 1,
        Canvas = new VibpCanvas { Width = 900, Height = 600 },
        Source = new VibpSourceResource
        {
          MediaType = "image/png", Width = 4, Height = 3, Length = jpeg.Length,
        },
        Assets = [],
        State = new VibpEditorState { Rotation = 0, Marks = [] },
      };
      string packagePath = Path.Combine(directory, "project.vibp");
      VibpProjectFile.Write(packagePath, manifest,
        [new VibpProjectEntry("source.png", jpeg.Length, () => new MemoryStream(jpeg))]);

      VibpProjectFormatException error = await Assert.ThrowsAsync<VibpProjectFormatException>(
        () => VibpProjectFile.ReadAsync(
          packagePath, Path.Combine(directory, "open"), TestContext.Current.CancellationToken));
      Assert.Contains("different container", error.Message);
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public async Task RejectsUnknownSchemaVersion()
  {
    string directory = NewTempDirectory();
    try
    {
      string packagePath = Path.Combine(directory, "project.vibp");
      WriteRawPackage(packagePath,
        """
        {
          "schemaVersion": 2,
          "canvas": { "width": 900, "height": 600 },
          "source": { "mediaType": "image/png", "width": 4, "height": 3, "length": 999 },
          "assets": [],
          "state": { "rotation": 0, "marks": [] }
        }
        """);

      VibpProjectFormatException error = await Assert.ThrowsAsync<VibpProjectFormatException>(
        () => VibpProjectFile.ReadAsync(
          packagePath, Path.Combine(directory, "open"), TestContext.Current.CancellationToken));
      Assert.Contains("schemaVersion", error.Message);
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public async Task RejectsMissingManifestEntry()
  {
    string directory = NewTempDirectory();
    try
    {
      string packagePath = Path.Combine(directory, "project.vibp");
      WriteRawPackage(packagePath, (string?)null, ("source.png", PngBytes(4, 3)));

      VibpProjectFormatException error = await Assert.ThrowsAsync<VibpProjectFormatException>(
        () => VibpProjectFile.ReadAsync(
          packagePath, Path.Combine(directory, "open"), TestContext.Current.CancellationToken));
      Assert.Contains("manifest.json", error.Message);
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public async Task RejectsDuplicateEntryNames()
  {
    string directory = NewTempDirectory();
    try
    {
      byte[] source = PngBytes(4, 3);
      string packagePath = Path.Combine(directory, "project.vibp");
      WriteRawPackage(packagePath, ManifestJson(source.Length),
        ("source.png", source),
        ("source.png", source));

      VibpProjectFormatException error = await Assert.ThrowsAsync<VibpProjectFormatException>(
        () => VibpProjectFile.ReadAsync(
          packagePath, Path.Combine(directory, "open"), TestContext.Current.CancellationToken));
      Assert.Contains("duplicated", error.Message);
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public async Task RejectsUndeclaredAndMissingEntries()
  {
    string directory = NewTempDirectory();
    try
    {
      byte[] source = PngBytes(4, 3);
      string packageWithExtra = Path.Combine(directory, "extra.vibp");
      WriteRawPackage(packageWithExtra, ManifestJson(source.Length),
        ("source.png", source),
        ("extra.bin", [1]));
      VibpProjectFormatException extra = await Assert.ThrowsAsync<VibpProjectFormatException>(
        () => VibpProjectFile.ReadAsync(
          packageWithExtra, Path.Combine(directory, "open1"), TestContext.Current.CancellationToken));
      Assert.Contains("undeclared", extra.Message);

      string packageMissingSource = Path.Combine(directory, "missing.vibp");
      WriteRawPackage(packageMissingSource, ManifestJson(source.Length),
        ("extra-alias.png", source));
      await Assert.ThrowsAsync<VibpProjectFormatException>(
        () => VibpProjectFile.ReadAsync(
          packageMissingSource, Path.Combine(directory, "open2"), TestContext.Current.CancellationToken));
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public async Task RejectsDeclaredLengthMismatchWithActualBytes()
  {
    string directory = NewTempDirectory();
    try
    {
      byte[] source = PngBytes(4, 3);
      string packagePath = Path.Combine(directory, "project.vibp");
      WriteRawPackage(packagePath, ManifestJson(declaredSourceLength: 3),
        ("source.png", source));

      VibpProjectFormatException error = await Assert.ThrowsAsync<VibpProjectFormatException>(
        () => VibpProjectFile.ReadAsync(
          packagePath, Path.Combine(directory, "open"), TestContext.Current.CancellationToken));
      Assert.Contains("declared", error.Message);
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public async Task RejectsEntryCountOverLimit()
  {
    string directory = NewTempDirectory();
    try
    {
      string packagePath = Path.Combine(directory, "project.vibp");
      using (var file = new FileStream(packagePath, FileMode.CreateNew))
      using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
      {
        for (int index = 0; index <= VibpProjectLimits.MaximumEntries; index++)
        {
          ZipArchiveEntry entry = archive.CreateEntry($"e{index}.bin");
          using Stream stream = entry.Open();
          stream.WriteByte(1);
        }
      }

      VibpProjectFormatException error = await Assert.ThrowsAsync<VibpProjectFormatException>(
        () => VibpProjectFile.ReadAsync(
          packagePath, Path.Combine(directory, "open"), TestContext.Current.CancellationToken));
      Assert.Contains("entries", error.Message);
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public async Task RejectsNestedOrDirectoryEntryNames()
  {
    string directory = NewTempDirectory();
    try
    {
      byte[] source = PngBytes(4, 3);
      string packagePath = Path.Combine(directory, "project.vibp");
      WriteRawPackage(packagePath, ManifestJson(source.Length),
        ("source.png", source),
        ("nested/source.png", source));

      await Assert.ThrowsAsync<VibpProjectFormatException>(
        () => VibpProjectFile.ReadAsync(
          packagePath, Path.Combine(directory, "open"), TestContext.Current.CancellationToken));

      string directoryEntryPath = Path.Combine(directory, "dir-entry.vibp");
      WriteRawPackage(directoryEntryPath, ManifestJson(source.Length),
        ("source.png", source),
        ("source.png/", []));

      await Assert.ThrowsAsync<VibpProjectFormatException>(
        () => VibpProjectFile.ReadAsync(
          directoryEntryPath, Path.Combine(directory, "open2"), TestContext.Current.CancellationToken));
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public async Task RejectsOversizeManifestEntry()
  {
    string directory = NewTempDirectory();
    try
    {
      string packagePath = Path.Combine(directory, "project.vibp");
      byte[] manifest = new byte[(int)VibpProjectLimits.MaximumManifestBytes + 1];
      WriteRawPackage(packagePath, manifest, ("source.png", PngBytes(4, 3)));

      VibpProjectFormatException error = await Assert.ThrowsAsync<VibpProjectFormatException>(
        () => VibpProjectFile.ReadAsync(
          packagePath, Path.Combine(directory, "open"), TestContext.Current.CancellationToken));
      Assert.Contains("budget", error.Message);
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public async Task RejectsUnknownManifestFieldAndMissingRequiredField()
  {
    string directory = NewTempDirectory();
    try
    {
      byte[] source = PngBytes(4, 3);
      string extra = Path.Combine(directory, "extra.vibp");
      WriteRawPackage(extra, ManifestJson(source.Length).Replace(
        "\"canvas\"", "\"oops\": 1,\n  \"canvas\""), ("source.png", source));
      await Assert.ThrowsAsync<VibpProjectFormatException>(
        () => VibpProjectFile.ReadAsync(
          extra, Path.Combine(directory, "open1"), TestContext.Current.CancellationToken));

      string missing = Path.Combine(directory, "missing.vibp");
      WriteRawPackage(missing, """
        {
          "schemaVersion": 1,
          "source": { "mediaType": "image/png", "width": 4, "height": 3, "length": 10 }
        }
        """, ("source.png", source));
      await Assert.ThrowsAsync<VibpProjectFormatException>(
        () => VibpProjectFile.ReadAsync(
          missing, Path.Combine(directory, "open2"), TestContext.Current.CancellationToken));
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public async Task RejectsPatchRectOutsideSourceBounds()
  {
    string directory = NewTempDirectory();
    try
    {
      byte[] source = PngBytes(4, 3);
      string manifest = ManifestJson(source.Length).Replace(
        "\"marks\": []",
        "\"marks\": [], \"patches\": [{ \"id\": 1, \"x\": 2, \"y\": 0, \"width\": 3, \"height\": 1, \"length\": 10 }]");
      string packagePath = Path.Combine(directory, "project.vibp");
      WriteRawPackage(packagePath, manifest,
        ("source.png", source),
        ("patch-1.png", PngBytes(3, 1)));

      await Assert.ThrowsAsync<VibpProjectFormatException>(
        () => VibpProjectFile.ReadAsync(
          packagePath, Path.Combine(directory, "open"), TestContext.Current.CancellationToken));
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public async Task RejectsPatchPngDimensionMismatchWithRect()
  {
    string directory = NewTempDirectory();
    try
    {
      byte[] source = PngBytes(4, 3);
      byte[] patch = PngBytes(2, 2);
      string manifest = ManifestJson(source.Length).Replace(
        "\"marks\": []",
        "\"marks\": [], \"patches\": [{ \"id\": 1, \"x\": 0, \"y\": 0, \"width\": 2, \"height\": 1, \"length\": " +
        patch.Length + " }]");
      string packagePath = Path.Combine(directory, "project.vibp");
      WriteRawPackage(packagePath, manifest,
        ("source.png", source),
        ("patch-1.png", patch));

      VibpProjectFormatException error = await Assert.ThrowsAsync<VibpProjectFormatException>(
        () => VibpProjectFile.ReadAsync(
          packagePath, Path.Combine(directory, "open"), TestContext.Current.CancellationToken));
      Assert.Contains("patch 1", error.Message);
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public async Task RejectsPatchPerPixelBudget()
  {
    string directory = NewTempDirectory();
    try
    {
      byte[] source = PngBytes(1024, 1024);
      byte[] patch = PngBytes(2, 2);
      string manifest = ManifestJson(source.Length).Replace(
        "\"mediaType\": \"image/png\", \"width\": 4, \"height\": 3",
        "\"mediaType\": \"image/png\", \"width\": 1024, \"height\": 1024").Replace(
        "\"marks\": []",
        "\"marks\": [], \"patches\": [{ \"id\": 1, \"x\": 0, \"y\": 0, \"width\": 1001, \"height\": 1000, \"length\": " +
        patch.Length + " }]");
      string packagePath = Path.Combine(directory, "project.vibp");
      WriteRawPackage(packagePath, manifest,
        ("source.png", source),
        ("patch-1.png", patch));

      VibpProjectFormatException error = await Assert.ThrowsAsync<VibpProjectFormatException>(
        () => VibpProjectFile.ReadAsync(
          packagePath, Path.Combine(directory, "open"), TestContext.Current.CancellationToken));
      Assert.Contains("pixel budget", error.Message);
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public async Task RejectsUnknownAssetReference()
  {
    string directory = NewTempDirectory();
    try
    {
      byte[] source = PngBytes(4, 3);
      byte[] asset = PngBytes(2, 2);
      string manifest = ManifestJson(source.Length, assetId: AssetId).Replace(
        "\"assetId\": \"" + AssetId + "\"",
        "\"assetId\": \"ffffffffffffffffffffffffffffffff\"");
      string packagePath = Path.Combine(directory, "project.vibp");
      WriteRawPackage(packagePath, manifest,
        ("source.png", source),
        ($"asset-{AssetId}.png", asset));

      VibpProjectFormatException error = await Assert.ThrowsAsync<VibpProjectFormatException>(
        () => VibpProjectFile.ReadAsync(
          packagePath, Path.Combine(directory, "open"), TestContext.Current.CancellationToken));
      Assert.Contains("unknown asset", error.Message);
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public async Task RejectsUnreferencedAsset()
  {
    string directory = NewTempDirectory();
    try
    {
      byte[] source = PngBytes(4, 3);
      byte[] asset = PngBytes(2, 2);
      // 声明资产并补齐 entry，但 marks 为空：没有任何 image mark 引用。
      string manifest = $$"""
        {
          "schemaVersion": 1,
          "canvas": { "width": 900, "height": 600 },
          "source": { "mediaType": "image/png", "width": 4, "height": 3, "length": {{source.Length}} },
          "assets": [
            { "id": "{{AssetId}}", "width": 2, "height": 2, "length": {{asset.Length}} }
          ],
          "state": { "rotation": 0, "marks": [] }
        }
        """;
      string packagePath = Path.Combine(directory, "project.vibp");
      WriteRawPackage(packagePath, manifest,
        ("source.png", source),
        ($"asset-{AssetId}.png", asset));

      VibpProjectFormatException error = await Assert.ThrowsAsync<VibpProjectFormatException>(
        () => VibpProjectFile.ReadAsync(
          packagePath, Path.Combine(directory, "open"), TestContext.Current.CancellationToken));
      Assert.Contains("not referenced", error.Message);
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public async Task RejectsDuplicateMarkIds()
  {
    string directory = NewTempDirectory();
    try
    {
      byte[] source = PngBytes(4, 3);
      string manifest = ManifestJson(source.Length).Replace("\"marks\": []",
        "\"marks\": [" + MarkJson("\"tool\": \"rectangle\", \"start\": { \"x\": 1, \"y\": 1 }, \"groupId\": \"group-1\"") + "," +
        MarkJson("\"tool\": \"rectangle\", \"start\": { \"x\": 1, \"y\": 1 }, \"groupId\": \"group-2\"") + "]");
      string packagePath = Path.Combine(directory, "project.vibp");
      WriteRawPackage(packagePath, manifest, ("source.png", source));

      VibpProjectFormatException error = await Assert.ThrowsAsync<VibpProjectFormatException>(
        () => VibpProjectFile.ReadAsync(
          packagePath, Path.Combine(directory, "open"), TestContext.Current.CancellationToken));
      Assert.Contains("duplicated", error.Message);
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public async Task RejectsUnknownMarkTool()
  {
    string directory = NewTempDirectory();
    try
    {
      byte[] source = PngBytes(4, 3);
      string manifest = ManifestJson(source.Length).Replace("\"marks\": []",
        "\"marks\": [" + MarkJson("\"tool\": \"spray\", \"start\": { \"x\": 1, \"y\": 1 }") + "]");
      string packagePath = Path.Combine(directory, "project.vibp");
      WriteRawPackage(packagePath, manifest, ("source.png", source));

      VibpProjectFormatException error = await Assert.ThrowsAsync<VibpProjectFormatException>(
        () => VibpProjectFile.ReadAsync(
          packagePath, Path.Combine(directory, "open"), TestContext.Current.CancellationToken));
      Assert.Contains("unknown tool", error.Message);
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public async Task RejectsOutOfRangeMarkCoordinate()
  {
    string directory = NewTempDirectory();
    try
    {
      byte[] source = PngBytes(4, 3);
      string manifest = ManifestJson(source.Length).Replace("\"marks\": []",
        "\"marks\": [" + MarkJson("\"tool\": \"rectangle\", \"start\": { \"x\": 100000000, \"y\": 0 }") + "]");
      string packagePath = Path.Combine(directory, "project.vibp");
      WriteRawPackage(packagePath, manifest, ("source.png", source));

      VibpProjectFormatException error = await Assert.ThrowsAsync<VibpProjectFormatException>(
        () => VibpProjectFile.ReadAsync(
          packagePath, Path.Combine(directory, "open"), TestContext.Current.CancellationToken));
      Assert.Contains("coordinate", error.Message);
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public async Task RejectsMarkRotationNotMultipleOf90()
  {
    string directory = NewTempDirectory();
    try
    {
      byte[] source = PngBytes(4, 3);
      string manifest = ManifestJson(source.Length).Replace("\"marks\": []",
        "\"marks\": [" + MarkJson("\"tool\": \"rectangle\", \"start\": { \"x\": 1, \"y\": 1 }, \"rotation\": 45") + "]");
      string packagePath = Path.Combine(directory, "project.vibp");
      WriteRawPackage(packagePath, manifest, ("source.png", source));

      VibpProjectFormatException error = await Assert.ThrowsAsync<VibpProjectFormatException>(
        () => VibpProjectFile.ReadAsync(
          packagePath, Path.Combine(directory, "open"), TestContext.Current.CancellationToken));
      Assert.Contains("multiple of 90", error.Message);
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public void WriteRejectsEntryProducingMoreBytesThanDeclared()
  {
    string directory = NewTempDirectory();
    try
    {
      byte[] source = PngBytes(4, 3);
      var manifest = new VibpProjectManifest
      {
        SchemaVersion = 1,
        Canvas = new VibpCanvas { Width = 900, Height = 600 },
        Source = new VibpSourceResource
        {
          MediaType = "image/png", Width = 4, Height = 3, Length = source.Length - 1,
        },
        Assets = [],
        State = new VibpEditorState { Rotation = 0, Marks = [] },
      };
      string packagePath = Path.Combine(directory, "project.vibp");
      Assert.Throws<VibpProjectFormatException>(() => VibpProjectFile.Write(
        packagePath, manifest,
        [new VibpProjectEntry("source.png", source.Length - 1, () => new MemoryStream(source))]));
      Assert.False(File.Exists(packagePath));
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  [Fact]
  public void WriteFailurePreservesExistingPackageAndCleansTemporaryFiles()
  {
    string directory = NewTempDirectory();
    try
    {
      byte[] source = PngBytes(4, 3);
      var manifest = new VibpProjectManifest
      {
        SchemaVersion = 1,
        Canvas = new VibpCanvas { Width = 900, Height = 600 },
        Source = new VibpSourceResource
        {
          MediaType = "image/png", Width = 4, Height = 3, Length = source.Length,
        },
        Assets = [],
        State = new VibpEditorState { Rotation = 0, Marks = [] },
      };
      string packagePath = Path.Combine(directory, "project.vibp");
      VibpProjectFile.Write(packagePath, manifest,
        [new VibpProjectEntry("source.png", source.Length, () => new MemoryStream(source))]);
      byte[] original = File.ReadAllBytes(packagePath);

      Assert.Throws<IOException>(() => VibpProjectFile.Write(packagePath, manifest,
        [new VibpProjectEntry("source.png", source.Length, () => new ExplodingStream(source))]));

      Assert.Equal(original, File.ReadAllBytes(packagePath));
      Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }
    finally
    {
      Directory.Delete(directory, recursive: true);
    }
  }

  private static string NewTempDirectory() =>
    Directory.CreateDirectory(Path.Combine(
      Path.GetTempPath(), $"vibp-{Guid.NewGuid():N}")).FullName;

  private static VibpProjectManifest FullManifest(
    byte[] source, byte[] asset, byte[] patch1, byte[] patch2) => new()
  {
    SchemaVersion = 1,
    Canvas = new VibpCanvas { Width = 900, Height = 600 },
    Source = new VibpSourceResource
    {
      MediaType = "image/png", Width = 6, Height = 4, Length = source.Length,
    },
    Assets =
    [
      new VibpAssetResource
      {
        Id = AssetId, Width = 2, Height = 2, Length = asset.Length,
      },
    ],
    State = new VibpEditorState
    {
      Rotation = 90,
      Crop = new VibpCrop
      {
        Start = new VibpPoint { X = 10, Y = 12 },
        End = new VibpPoint { X = 400.5, Y = 300.25 },
      },
      Resize = new VibpSize { Width = 320, Height = 200 },
      Marks =
      [
        new VibpMark
        {
          Id = "mark-1", Tool = "image",
          Start = new VibpPoint { X = 100, Y = 120 },
          End = new VibpPoint { X = 180, Y = 200 },
          AssetId = AssetId,
          GroupId = "group-1",
          Locked = true,
          Rotation = 90,
          FlipX = true,
          ObjectScale = 1.5,
        },
        new VibpMark
        {
          Id = "mark-2", Tool = "pen",
          Start = new VibpPoint { X = 1, Y = 1 },
          End = new VibpPoint { X = 9, Y = 9 },
          Points =
          [
            new VibpPoint { X = 1, Y = 1 },
            new VibpPoint { X = 4, Y = 5 },
            new VibpPoint { X = 9, Y = 9 },
          ],
          Style = new VibpMarkStyle
          {
            Color = "#f38b35",
            StrokeWidth = 5,
            StrokeOpacity = 80,
          },
        },
        new VibpMark
        {
          Id = "mark-3", Tool = "text",
          Start = new VibpPoint { X = 20, Y = 20 },
          End = new VibpPoint { X = 120, Y = 60 },
          Text = "标题文字",
          TextWidth = 100.5,
          Style = new VibpMarkStyle
          {
            FontFamily = "Segoe UI",
            FontBold = true,
            FontItalic = false,
            FontUnderline = true,
            TextAlign = "center",
            FontSize = 32,
            TextOutline = new VibpTextOutlineStyle { Color = "#ffffff", Width = 2 },
            TextBackground = new VibpFillStyle { Color = "#1f6feb", Opacity = 40 },
          },
        },
        new VibpMark
        {
          Id = "mark-4", Tool = "numbering",
          Start = new VibpPoint { X = 50, Y = 50 },
          End = new VibpPoint { X = 70, Y = 70 },
          Ordinal = 3,
          Style = new VibpMarkStyle
          {
            NumberFormat = "upper",
            NumberShape = "circle",
          },
        },
        new VibpMark
        {
          Id = "mark-5", Tool = "rectangle",
          Start = new VibpPoint { X = 200, Y = 200 },
          End = new VibpPoint { X = 300, Y = 260 },
          Style = new VibpMarkStyle
          {
            Color = "#e02020",
            LineStyle = "dashed",
            CornerRadius = 6,
            Fill = new VibpFillStyle { Color = "#12a150", Opacity = 50, Enabled = true },
          },
        },
        new VibpMark
        {
          Id = "mark-6", Tool = "callout",
          Start = new VibpPoint { X = 220, Y = 220 },
          End = new VibpPoint { X = 260, Y = 240 },
          Text = "说明",
          Style = new VibpMarkStyle { CalloutStyle = "arrow", ArrowStyle = "hollow", ArrowDouble = true },
        },
      ],
      Patches =
      [
        new VibpPatch { Id = 1, X = 0, Y = 0, Width = 3, Height = 1, Length = patch1.Length },
        new VibpPatch { Id = 2, X = 4, Y = 1, Width = 1, Height = 2, Length = patch2.Length },
      ],
    },
  };

  /// <summary>构造合法基础 manifest JSON（供负路径直接改写）。</summary>
  private static string ManifestJson(long? declaredSourceLength = null, string? assetId = null)
  {
    byte[] source = PngBytes(4, 3);
    long length = declaredSourceLength ?? source.Length;
    string assets = assetId is null
      ? "[]"
      : $$"""
        [
          { "id": "{{assetId}}", "width": 2, "height": 2, "length": {{PngBytes(2, 2).Length}} }
        ]
        """;
    string marks = assetId is null
      ? "[]"
      : $$"""
        [
          {
            "id": "mark-1",
            "tool": "image",
            "start": { "x": 1, "y": 1 },
            "end": { "x": 2, "y": 2 },
            "assetId": "{{assetId}}"
          }
        ]
        """;
    return $$"""
      {
        "schemaVersion": 1,
        "canvas": { "width": 900, "height": 600 },
        "source": { "mediaType": "image/png", "width": 4, "height": 3, "length": {{length}} },
        "assets": {{assets}},
        "state": { "rotation": 0, "marks": {{marks}} }
      }
      """;
  }

  private static string MarkJson(string replacement) => $$"""
    {
      "id": "mark-1",
      "end": { "x": 3, "y": 2 },
      {{replacement}}
    }
    """;

  private static void WriteRawPackage(
    string path, string? manifestJson, params (string Name, byte[] Data)[] entries)
  {
    using var file = new FileStream(path, FileMode.CreateNew);
    using var archive = new ZipArchive(file, ZipArchiveMode.Create);
    if (manifestJson is not null)
    {
      ZipArchiveEntry manifestEntry = archive.CreateEntry("manifest.json");
      using (Stream stream = manifestEntry.Open())
      {
        stream.Write(Encoding.UTF8.GetBytes(manifestJson));
      }
    }
    foreach ((string name, byte[] data) in entries)
    {
      ZipArchiveEntry entry = archive.CreateEntry(name);
      using Stream stream = entry.Open();
      stream.Write(data);
    }
  }

  private static void WriteRawPackage(
    string path, byte[] manifest, params (string Name, byte[] Data)[] entries)
  {
    using var file = new FileStream(path, FileMode.CreateNew);
    using var archive = new ZipArchive(file, ZipArchiveMode.Create);
    ZipArchiveEntry manifestEntry = archive.CreateEntry("manifest.json");
    using (Stream stream = manifestEntry.Open())
    {
      stream.Write(manifest);
    }
    foreach ((string name, byte[] data) in entries)
    {
      ZipArchiveEntry entry = archive.CreateEntry(name);
      using Stream stream = entry.Open();
      stream.Write(data);
    }
  }

  private sealed class ExplodingStream(byte[] payload) : Stream
  {
    private int written;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => payload.Length;
    public override long Position { get; set; }

    public override int Read(byte[] buffer, int offset, int count)
    {
      if (written >= payload.Length / 2)
      {
        throw new IOException("simulated device failure");
      }
      int chunk = Math.Min(count, payload.Length / 2 - written);
      Array.Copy(payload, written, buffer, offset, chunk);
      written += chunk;
      return chunk;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }

  // ---- 真实编码字节构造：PNG 自带完整 zlib/CRC；JPEG/BMP/EXIF 定向 JPEG 走 WIC 平台编码。----

  /// <summary>经 WIC BitmapEncoder 真实编码的 JPEG/BMP 字节（平台可解码）。</summary>
  private static async Task<byte[]> EncodeImageAsync(string mediaType, int width, int height)
  {
    Guid encoderId = mediaType switch
    {
      "image/jpeg" => BitmapEncoder.JpegEncoderId,
      _ => BitmapEncoder.BmpEncoderId,
    };
    using var memory = new InMemoryRandomAccessStream();
    BitmapEncoder encoder = await BitmapEncoder.CreateAsync(encoderId, memory);
    encoder.SetPixelData(
      BitmapPixelFormat.Rgba8, BitmapAlphaMode.Straight,
      (uint)width, (uint)height, 96, 96, new byte[width * height * 4]);
    await encoder.FlushAsync();
    return await ReadAllBytesAsync(memory);
  }

  /// <summary>带 System.Photo.Orientation 的真实 JPEG（orientation 6 = 90° 顺时针）。</summary>
  private static async Task<byte[]> EncodeOrientedJpegAsync(int width, int height, ushort orientation)
  {
    using var memory = new InMemoryRandomAccessStream();
    BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, memory);
    encoder.SetPixelData(
      BitmapPixelFormat.Rgba8, BitmapAlphaMode.Straight,
      (uint)width, (uint)height, 96, 96, new byte[width * height * 4]);
    BitmapPropertySet properties = new()
    {
      ["System.Photo.Orientation"] = new BitmapTypedValue(orientation, PropertyType.UInt16),
    };
    await encoder.BitmapProperties.SetPropertiesAsync(properties);
    await encoder.FlushAsync();
    return await ReadAllBytesAsync(memory);
  }

  /// <summary>fixture 自证：EXIF 确实写入且 WIC 定向语义生效（未定向/定向宽高互换）。</summary>
  private static async Task<(int OrientedWidth, int OrientedHeight, int PixelWidth, int PixelHeight)>
    ProbeJpegDimensionsAsync(byte[] jpeg)
  {
    using var memory = await WriteToStreamAsync(jpeg);
    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(memory);
    return ((int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight,
      (int)decoder.PixelWidth, (int)decoder.PixelHeight);
  }

  /// <summary>本机平台 decoder 实证：CodecId + 定向后尺寸。</summary>
  private static async Task<(Guid CodecId, int Width, int Height)> ProbeImageAsync(byte[] image)
  {
    using var memory = await WriteToStreamAsync(image);
    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(memory);
    return (decoder.DecoderInformation.CodecId,
      (int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight);
  }

  private static async Task<InMemoryRandomAccessStream> WriteToStreamAsync(byte[] image)
  {
    var memory = new InMemoryRandomAccessStream();
    using (var writer = new DataWriter(memory))
    {
      writer.WriteBytes(image);
      await writer.StoreAsync();
      writer.DetachStream();
    }
    memory.Seek(0);
    return memory;
  }

  private static async Task<byte[]> ReadAllBytesAsync(InMemoryRandomAccessStream memory)
  {
    using var reader = new DataReader(memory.GetInputStreamAt(0));
    await reader.LoadAsync((uint)memory.Size);
    byte[] bytes = new byte[memory.Size];
    reader.ReadBytes(bytes);
    return bytes;
  }

  private static readonly uint[] Crc32Table = BuildCrc32Table();

  private static uint[] BuildCrc32Table()
  {
    uint[] table = new uint[256];
    for (uint index = 0; index < 256; index++)
    {
      uint value = index;
      for (int bit = 0; bit < 8; bit++)
      {
        value = (value & 1) != 0 ? 0xedb88320u ^ (value >> 1) : value >> 1;
      }
      table[index] = value;
    }
    return table;
  }

  private static uint Crc32(byte[] data)
  {
    uint crc = 0xffffffffu;
    foreach (byte value in data)
    {
      crc = Crc32Table[(crc ^ value) & 0xff] ^ (crc >> 8);
    }
    return crc ^ 0xffffffffu;
  }

  private static byte[] Adler32(byte[] data)
  {
    uint a = 1, b = 0;
    foreach (byte value in data)
    {
      a = (a + value) % 65521u;
      b = (b + a) % 65521u;
    }
    uint checksum = b << 16 | a;
    return [(byte)(checksum >> 24), (byte)(checksum >> 16), (byte)(checksum >> 8), (byte)checksum];
  }

  private static byte[] PngChunk(string type, byte[] data)
  {
    byte[] payload = [.. Encoding.ASCII.GetBytes(type), .. data];
    byte[] chunk = new byte[12 + data.Length];
    BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(0, 4), (uint)data.Length);
    payload.CopyTo(chunk, 4);
    BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length, 4), Crc32(payload));
    return chunk;
  }

  /// <summary>真实 PNG（8bit RGB，完整 zlib/Adler32/CRC32，平台可解码）。</summary>
  private static byte[] PngBytes(int width, int height)
  {
    return BuildPngCore(width, height);
  }

  /// <summary>供同仓其他接线测试复用的真实 PNG 构造。</summary>
  internal static byte[] BuildPng(int width, int height) => BuildPngCore(width, height);

  private static byte[] BuildPngCore(int width, int height)
  {
    byte[] scanlines = new byte[height * (1 + width * 3)];
    for (int index = 0; index < scanlines.Length; index++)
    {
      scanlines[index] = (byte)(index % 251);
    }
    using var deflated = new MemoryStream();
    using (var deflater = new DeflateStream(deflated, CompressionLevel.Fastest, leaveOpen: true))
    {
      deflater.Write(scanlines);
    }
    byte[] zlib = [0x78, 0x01, .. deflated.ToArray(), .. Adler32(scanlines)];
    byte[] header = new byte[13];
    BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width);
    BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), height);
    header[8] = 8;   // bit depth
    header[9] = 2;   // truecolor
    header[10] = 0;  // compression
    header[11] = 0;  // filter
    header[12] = 0;  // interlace
    return
    [
      0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a,
      .. PngChunk("IHDR", header),
      .. PngChunk("IDAT", zlib),
      .. PngChunk("IEND", []),
    ];
  }
}

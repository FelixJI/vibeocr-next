using VibeOCR.App.Features.Batch;
using VibeOCR.App.Features.QrCode;
using VibeOCR.App.Services;
using VibeOCR.App.Web;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace VibeOCR.App.Workbench;

internal sealed class WorkbenchImageConversionOwner(Func<nint> windowHandle, WorkbenchSettingsOwner settingsOwner, WorkbenchResourcesOwner resources, WorkbenchAnnotationStore annotationStore)
{
  internal List<BatchItemViewModel> Items { get; } = [];
  private int windowStart;
  internal bool Handles(WorkbenchCommand command) => command is AddImageConversionFilesCommand or AddDroppedImageConversionFilesCommand or ClearImageConversionCommand or RemoveImageConversionItemCommand or MoveImageConversionItemCommand or SetImageConversionWindowCommand or PrepareImageConversionCommand or ExportConvertedImageCommand;

  internal async ValueTask<WorkbenchState?> ExecuteAsync(WorkbenchCommand command, CancellationToken token)
  {
    switch (command)
    {
      case AddImageConversionFilesCommand:
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, ViewMode = PickerViewMode.Thumbnail };
        foreach (string extension in BatchCommands.Extensions.Where(extension => BatchCommands.MediaType(extension).StartsWith("image/", StringComparison.Ordinal))) picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle());
        IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
        token.ThrowIfCancellationRequested();
        AddFiles(files.Select(file => file.Path));
        break;
      case AddDroppedImageConversionFilesCommand dropped:
        AddFiles(dropped.Paths);
        break;
      case ClearImageConversionCommand:
        Items.Clear(); Release(); windowStart = 0;
        break;
      case RemoveImageConversionItemCommand remove:
        Items.RemoveAll(item => item.Id == remove.ItemId);
        break;
      case MoveImageConversionItemCommand move:
        int index = Items.FindIndex(item => item.Id == move.ItemId);
        int target = index + move.Delta;
        if (move.Delta is not (-1 or 1) || index < 0 || target < 0 || target >= Items.Count) throw new InvalidOperationException("图片移动位置无效。");
        (Items[index], Items[target]) = (Items[target], Items[index]);
        break;
      case SetImageConversionWindowCommand window:
        windowStart = Math.Max(0, window.Start);
        break;
      case PrepareImageConversionCommand prepare:
        return await PrepareImageAsync(prepare.Index, token);
      case ExportConvertedImageCommand export:
        return await ExportImageAsync(export, token);
      default: throw new InvalidOperationException("Unsupported image conversion command.");
    }
    return CurrentState();
  }

  private void AddFiles(IEnumerable<string> paths)
  {
    string[] inputs = paths.Select(Path.GetFullPath).ToArray();
    if (inputs.Any(path => !BatchCommands.MediaType(Path.GetExtension(path)).StartsWith("image/", StringComparison.Ordinal)))
      throw new InvalidDataException("图像转换仅接受图片，请将文档添加到批量识别。");
    var known = Items.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
    foreach (string path in inputs) if (known.Add(path)) Items.Add(new BatchItemViewModel(path));
  }

  internal ImageConversionWorkbenchState CurrentState()
  {
    windowStart = Math.Min(windowStart, Math.Max(0, Items.Count - 1));
    return new(Items.Count, Items.Skip(windowStart).Take(50).Select(item => new ImageConversionItem(item.Id, item.Name)).ToArray(), windowStart);
  }
  internal void Release()
  {
    imageExportSnapshot = [];
    if (exportImage is not null) resources.ReleaseResource(exportImage);
    exportImage = null; exportImageId = null;
  }
  private WorkbenchResourceReference? exportImage;
  private Guid? exportImageId;
  private BatchItemViewModel[] imageExportSnapshot = [];
  private int exportImageIndex;

  private async Task<ImageConversionWorkbenchState> PrepareImageAsync(int index, CancellationToken token)
  {
    if (exportImage is not null) resources.ReleaseResource(exportImage);
    exportImage = null;
    exportImageId = null;
    if (index == 0) imageExportSnapshot = Items.ToArray();
    if (index < 0 || index >= imageExportSnapshot.Length) throw new InvalidDataException("批量图片项已变化。");
    BatchItemViewModel item = imageExportSnapshot[index];
    if (!Items.Contains(item)) throw new InvalidDataException("批量图片项已被移除。");
    exportImageIndex = index;
    string extension = Path.GetExtension(item.Path).ToLowerInvariant();
    if (!BatchCommands.MediaType(extension).StartsWith("image/", StringComparison.Ordinal))
      throw new InvalidDataException("此队列项不是可导出的图片。");
    await using FileStream input = File.OpenRead(item.Path);
    if (input.Length is < 1 or > 67108864) throw new InvalidDataException("图片超过 64MiB 上限。");
    byte[] bytes;
    try
    {
      using var random = input.AsRandomAccessStream();
      BitmapDecoder decoder = await BitmapDecoder.CreateAsync(random).AsTask(token);
      if (decoder.OrientedPixelWidth == 0 || decoder.OrientedPixelHeight == 0 ||
        decoder.OrientedPixelWidth > 32768 || decoder.OrientedPixelHeight > 32768 ||
        (long)decoder.OrientedPixelWidth * decoder.OrientedPixelHeight > 64_000_000)
        throw new InvalidDataException("图片超过 64MP 或单边 32768 上限。");
      input.Position = 0;
      byte[] original = new byte[checked((int)input.Length)];
      await input.ReadExactlyAsync(original, token);
      // 与宿主已有编码器共用真实解码：固定首帧/首页，TIFF 等不交给浏览器解码。
      bytes = await QrCodeSavePlatform.EncodeImageAsync(original, BitmapEncoder.PngEncoderId, token,
        bypassPassthrough: true, respectExifOrientation: true);
    }
    catch (Exception error) when (error is COMException or NotSupportedException)
    {
      AppLog.Warn($"Batch image format decode unavailable ({extension}): {error.Message}");
      return CurrentState() with { ExportImageId = item.Id, ExportImageError = "decode_unavailable" };
    }
    if (bytes.LongLength > 67108864) throw new InvalidDataException("图片静态编码超过 64MiB 上限。");
    exportImage = await resources.PublishBytesAsync(bytes, "image/png", ".png", token);
    exportImageId = item.Id;
    return CurrentState() with { ExportImage = exportImage, ExportImageId = exportImageId };
  }

  private async Task<ImageConversionWorkbenchState> ExportImageAsync(ExportConvertedImageCommand command, CancellationToken token)
  {
    try
    {
      try { settingsOwner.ValidatePresetBinding(command.PresetId, command.PresetBinding); }
      catch { annotationStore.Revoke(new Uri(command.ResourceUri)); throw; }
      using WorkbenchAnnotationFile image = annotationStore.Take(new Uri(command.ResourceUri));
      BatchItemViewModel item = Items.FirstOrDefault(item => item.Id == command.ItemId)
        ?? throw new InvalidDataException("批量图片项已被移除，未写入。");
      if (exportImageId != item.Id || exportImage is null) throw new InvalidDataException("批量图片授权已失效。");
      await settingsOwner.SavePresetImageAsync(image, command.PresetId, item.Name, exportImageIndex + 1, token);
      return CurrentState();
    }
    finally
    {
      if (exportImage is not null) resources.ReleaseResource(exportImage);
      exportImage = null;
      exportImageId = null;
    }
  }

}

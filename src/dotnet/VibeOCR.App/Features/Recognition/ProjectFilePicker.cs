using Windows.Storage;
using Windows.Storage.Pickers;

namespace VibeOCR.App.Features.Recognition;

/// <summary>#233 .vibp 工程文件的宿主 picker 授权（保存/打开）；与既有
/// 图片/PDF 选择器同一 WinRT FilePicker + InitializeWithWindow 模式，
/// 不新增第二套文件授权路径。</summary>
public sealed class ProjectFilePicker(Func<nint> windowHandle)
{
  public async Task<string?> PickSavePathAsync(CancellationToken cancellationToken)
  {
    var picker = new FileSavePicker
    {
      SuggestedStartLocation = PickerLocationId.PicturesLibrary,
      SuggestedFileName = "vibeocr-project",
    };
    picker.FileTypeChoices.Add("VibeOCR 工程", [".vibp"]);
    WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle());
    StorageFile? file = await picker.PickSaveFileAsync();
    cancellationToken.ThrowIfCancellationRequested();
    return file?.Path;
  }

  public async Task<string?> PickOpenPathAsync(CancellationToken cancellationToken)
  {
    var picker = new FileOpenPicker
    {
      SuggestedStartLocation = PickerLocationId.PicturesLibrary,
      ViewMode = PickerViewMode.Thumbnail,
    };
    picker.FileTypeFilter.Add(".vibp");
    WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle());
    StorageFile? file = await picker.PickSingleFileAsync();
    cancellationToken.ThrowIfCancellationRequested();
    return file?.Path;
  }
}

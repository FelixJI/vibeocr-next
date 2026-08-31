using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace VibeOCR.App.Features.Recognition;

public interface IAnnotatedImagePlatform
{
  Task CopyPngAsync(string sourcePath, CancellationToken cancellationToken);

  Task<bool> SavePngAsync(string sourcePath, CancellationToken cancellationToken);
}

public sealed class AnnotatedImagePlatform(Func<nint> windowHandle) : IAnnotatedImagePlatform
{
  public async Task CopyPngAsync(
    string sourcePath,
    CancellationToken cancellationToken)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
    StorageFile source = await StorageFile.GetFileFromPathAsync(sourcePath);
    var package = new DataPackage
    {
      RequestedOperation = DataPackageOperation.Copy,
    };
    package.SetBitmap(RandomAccessStreamReference.CreateFromFile(source));
    for (int attempt = 0; ; attempt++)
    {
      cancellationToken.ThrowIfCancellationRequested();
      try
      {
        Clipboard.SetContent(package);
        Clipboard.Flush();
        return;
      }
      catch (COMException) when (attempt < 4)
      {
        await Task.Delay(TimeSpan.FromMilliseconds(40 * (attempt + 1)), cancellationToken);
      }
      catch (COMException error)
      {
        throw new ClipboardBusyException(error);
      }
    }
  }

  public async Task<bool> SavePngAsync(
    string sourcePath,
    CancellationToken cancellationToken)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
    var picker = new FileSavePicker
    {
      SuggestedStartLocation = PickerLocationId.PicturesLibrary,
      SuggestedFileName = "vibeocr-annotated",
    };
    picker.FileTypeChoices.Add("PNG 图片", [".png"]);
    WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle());
    StorageFile? destinationFile = await picker.PickSaveFileAsync();
    if (destinationFile is null)
    {
      return false;
    }

    await using FileStream source = new(
      sourcePath,
      FileMode.Open,
      FileAccess.Read,
      FileShare.Read,
      bufferSize: 64 * 1024,
      FileOptions.Asynchronous | FileOptions.SequentialScan);
    await using Stream destination = await destinationFile.OpenStreamForWriteAsync();
    destination.SetLength(0);
    await source.CopyToAsync(destination, cancellationToken);
    await destination.FlushAsync(cancellationToken);
    return true;
  }
}

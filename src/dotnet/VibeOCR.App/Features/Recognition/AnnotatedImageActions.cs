using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace VibeOCR.App.Features.Recognition;

public interface IAnnotatedImagePlatform
{
  Task CopyImageAsync(string sourcePath, CancellationToken cancellationToken);

  Task<bool> SaveImageAsync(string sourcePath, CancellationToken cancellationToken);

  Task CopyTextAsync(string text, CancellationToken cancellationToken);
}

public sealed class AnnotatedImagePlatform(Func<nint> windowHandle) : IAnnotatedImagePlatform
{
  public async Task CopyImageAsync(
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

  public async Task CopyTextAsync(
    string text,
    CancellationToken cancellationToken)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(text);
    var package = new DataPackage
    {
      RequestedOperation = DataPackageOperation.Copy,
    };
    package.SetText(text);
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

  public async Task<bool> SaveImageAsync(
    string sourcePath,
    CancellationToken cancellationToken)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
    var picker = new FileSavePicker
    {
      SuggestedStartLocation = PickerLocationId.PicturesLibrary,
      SuggestedFileName = "vibeocr-annotated",
    };
    bool jpeg = string.Equals(Path.GetExtension(sourcePath), ".jpg", StringComparison.OrdinalIgnoreCase);
    picker.FileTypeChoices.Add(jpeg ? "JPEG 图片" : "PNG 图片", [jpeg ? ".jpg" : ".png"]);
    WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle());
    StorageFile? destinationFile = await picker.PickSaveFileAsync();
    if (destinationFile is null)
    {
      return false;
    }

    string expectedExtension = jpeg ? ".jpg" : ".png";
    if (!string.Equals(Path.GetExtension(destinationFile.Path), expectedExtension, StringComparison.OrdinalIgnoreCase))
      throw new InvalidDataException("保存文件扩展名必须与当前图片格式一致。");

    await WriteImageCopyAsync(sourcePath, destinationFile.Path, cancellationToken);
    return true;
  }

  internal static async Task WriteImageCopyAsync(
    string sourcePath, string destinationPath, CancellationToken cancellationToken)
  {
    string temporaryPath = Path.Combine(Path.GetDirectoryName(destinationPath)!,
      $".vibeocr-{Guid.NewGuid():N}.tmp");
    try
    {
      await using (FileStream source = File.OpenRead(sourcePath))
      await using (FileStream destination = new(temporaryPath, FileMode.CreateNew, FileAccess.Write,
        FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
      {
        await source.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);
      }
      cancellationToken.ThrowIfCancellationRequested();
      File.Move(temporaryPath, destinationPath, overwrite: true);
    }
    finally
    {
      try { File.Delete(temporaryPath); }
      catch (IOException) { }
      catch (UnauthorizedAccessException) { }
    }
  }
}

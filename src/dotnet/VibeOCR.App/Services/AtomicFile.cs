using System.Text;

namespace VibeOCR.App.Services;

internal static class AtomicFile
{
  public static void Write(string path, Action<Stream> write)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    ArgumentNullException.ThrowIfNull(write);

    string target = Path.GetFullPath(path);
    string directory = Path.GetDirectoryName(target) ?? Directory.GetCurrentDirectory();
    Directory.CreateDirectory(directory);
    string temporary = Path.Combine(
      directory,
      $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
    try
    {
      using (var stream = new FileStream(
        temporary,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None))
      {
        write(stream);
        stream.Flush(flushToDisk: true);
      }
      File.Move(temporary, target, overwrite: true);
    }
    catch
    {
      try
      {
        File.Delete(temporary);
      }
      catch (IOException)
      {
        // Preserve the original write/replace failure.
      }
      catch (UnauthorizedAccessException)
      {
        // Preserve the original write/replace failure.
      }
      throw;
    }
  }

  public static void WriteAllText(string path, string content)
  {
    Write(path, stream =>
    {
      using var writer = new StreamWriter(
        stream,
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        leaveOpen: true);
      writer.Write(content);
      writer.Flush();
    });
  }
}

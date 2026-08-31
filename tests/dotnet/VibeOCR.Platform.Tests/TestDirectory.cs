namespace VibeOCR.Platform.Tests;

internal static class TestDirectory
{
  private const int SharingViolationHResult = unchecked((int)0x80070020);
  private const int MaximumAttempts = 20;

  public static void Delete(string path, bool recursive = false)
  {
    for (int attempt = 1; ; attempt++)
    {
      try
      {
        Directory.Delete(path, recursive);
        return;
      }
      catch (IOException error) when (
          error.HResult == SharingViolationHResult && attempt < MaximumAttempts)
      {
        Thread.Sleep(TimeSpan.FromMilliseconds(50));
      }
    }
  }
}

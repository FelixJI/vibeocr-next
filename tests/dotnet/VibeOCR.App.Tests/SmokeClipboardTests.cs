using System.Runtime.InteropServices;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class SmokeClipboardTests
{
  private static COMException Busy() => new("busy", unchecked((int)0x800401D0));

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task BusyReadCanCompleteWithinExistingObservationBudget(bool asynchronous)
  {
    int reads = 0, waits = 0;
    Task<string?> Read()
    {
      if (++reads == 1)
      {
        if (asynchronous) return Task.FromException<string?>(Busy());
        throw Busy();
      }
      return Task.FromResult<string?>(reads == 2 ? "different" : "selected");
    }
    string result = await MainWindow.WaitForSmokeClipboardTextAsync("selected", "fixture", Read,
      () => { waits++; return Task.CompletedTask; });
    Assert.Equal("selected", result);
    Assert.Equal(3, reads);
    Assert.Equal(2, waits);
  }

  [Fact]
  public async Task PermanentlyBusyReadFailsWithoutExtraClipboardAccess()
  {
    int reads = 0, waits = 0;
    var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
      MainWindow.WaitForSmokeClipboardTextAsync("selected", "fixture",
        () => { reads++; throw Busy(); },
        () => { waits++; return Task.CompletedTask; }));
    Assert.Equal(100, reads);
    Assert.Equal(100, waits);
    Assert.IsType<COMException>(error.InnerException);
  }

  [Fact]
  public async Task MismatchDoesNotWriteUnrelatedClipboardTextToFailureArtifacts()
  {
    const string unrelated = "private-clipboard-sentinel";
    var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
      MainWindow.WaitForSmokeClipboardTextAsync("selected", "fixture",
        () => Task.FromResult<string?>(unrelated), () => Task.CompletedTask));
    Assert.DoesNotContain(unrelated, error.ToString());
    Assert.Contains($"actualLength={unrelated.Length}", error.Message);
  }

  [Fact]
  public async Task OtherComFailureIsNotRetried()
  {
    var expected = new COMException("other", unchecked((int)0x80004005));
    int reads = 0, waits = 0;
    var actual = await Assert.ThrowsAsync<COMException>(() =>
      MainWindow.WaitForSmokeClipboardTextAsync("selected", "fixture",
        () => { reads++; throw expected; },
        () => { waits++; return Task.CompletedTask; }));
    Assert.Same(expected, actual);
    Assert.Equal(1, reads);
    Assert.Equal(0, waits);
  }
}

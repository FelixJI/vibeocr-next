using VibeOCR.App.Features.Settings;
using VibeOCR.Contracts.HttpV2;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class RecognitionDeviceSettingsTests
{
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task UnsuccessfulDeviceUpdatePreservesLastAcknowledgedValue(bool cancel)
  {
    var client = new DeviceClient();
    var settings = new SettingsViewModel(client);
    CancellationToken token = TestContext.Current.CancellationToken;
    await settings.SetRecognitionDeviceAsync("cpu", token);
    client.NextError = cancel ? new OperationCanceledException() : new InvalidOperationException("provider unavailable");
    Exception? error = await Record.ExceptionAsync(() => settings.SetRecognitionDeviceAsync("directml", token));
    Assert.Same(client.NextError, error);
    Assert.Equal("cpu", settings.RecognitionDevice);
    Assert.Equal("cpu", client.Current.RecognitionDevice);
    Assert.Equal(1, client.Current.Revision);
  }

  private sealed class DeviceClient : InferenceClientStub
  {
    public SettingsSnapshot Current { get; private set; } = new();
    public Exception? NextError { get; set; }
    public override Task<SettingsSnapshot> GetSettingsAsync(CancellationToken cancellationToken) => Task.FromResult(Current);
    public override Task<SettingsSnapshot> UpdateSettingsAsync(SettingsSnapshot value, CancellationToken cancellationToken)
    {
      Assert.Equal(Current.Revision, value.Revision);
      if (NextError is not null) throw NextError;
      Current = value with { Revision = value.Revision + 1 };
      return Task.FromResult(Current);
    }
  }
}

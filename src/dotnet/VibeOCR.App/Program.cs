using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;
using VibeOCR.App.Features.Update;

namespace VibeOCR.App;

public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        VelopackApp.Build().Run();
        if (VelopackUpdateSelfTest.IsRequested)
        {
            Environment.ExitCode = VelopackUpdateSelfTest.Run();
            return;
        }
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }
}

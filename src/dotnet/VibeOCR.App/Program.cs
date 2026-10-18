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
        CaptureUiSmokeTrace.Stage("program-enter");
        VelopackApp.Build().Run();
        CaptureUiSmokeTrace.Stage("velopack-complete");
        if (VelopackUpdateSelfTest.IsRequested)
        {
            Environment.ExitCode = VelopackUpdateSelfTest.Run();
            return;
        }
        WinRT.ComWrappersSupport.InitializeComWrappers();
        CaptureUiSmokeTrace.Stage("com-wrappers-ready");
        Application.Start(_ =>
        {
            CaptureUiSmokeTrace.Stage("xaml-callback-enter");
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            CaptureUiSmokeTrace.Stage("app-constructor.begin");
            new App();
            CaptureUiSmokeTrace.Stage("app-constructor.end");
        });
    }
}

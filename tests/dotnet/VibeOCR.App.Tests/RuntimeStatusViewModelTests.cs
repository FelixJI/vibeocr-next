using VibeOCR.App.ViewModels;
using Http = VibeOCR.Contracts.HttpV2;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class RuntimeStatusViewModelTests
{
    [Fact]
    public void HttpSnapshotBecomesAuthoritativeAfterSupervisorIsReady()
    {
        var viewModel = new RuntimeStatusViewModel();

        viewModel.ApplySnapshot(new Http.RuntimeStatusSnapshot
        {
            InstanceId = "sup-1",
            ServiceState = Http.RuntimeServiceState.Ready,
            BackendVersion = "0.9.0",
            Profile = new Http.RuntimeProfileStatus
            {
                ProfileId = "win-x64-cpu",
                Accelerator = Http.RuntimeAccelerator.Cpu,
                Components =
                [
                    new Http.RuntimeComponentStatus
                    {
                        ComponentId = "ocr_engine",
                        DisplayName = "OCR engine",
                        State = Http.RuntimeComponentState.Ready,
                        Version = "3.7.0",
                    },
                ],
            },
        });

        Assert.Equal("运行时已就绪", viewModel.Status);
        Assert.Equal("0.9.0", viewModel.BackendVersion);
        Assert.Equal("已就绪", Assert.Single(viewModel.Components).State);
    }

    [Fact]
    public void IdleAndReadyStatesDoNotReportPausedService()
    {
        var model = new RuntimeStatusViewModel();
        model.ApplySnapshot(Ready());
        Assert.Equal("运行时已就绪", model.ServiceStatus);

        model.ReportServiceUnavailable();
        Assert.Equal("运行环境暂不可用，请重新检查或打开诊断与修复。", model.ServiceStatus);

        model.ReportServicePausedForMaintenance();
        Assert.Equal("运行环境维护中，识别服务已暂停。", model.ServiceStatus);
    }

    private static Http.RuntimeStatusSnapshot Ready() => new()
    {
        InstanceId = "sup-test", ServiceState = Http.RuntimeServiceState.Ready, BackendVersion = "0.14.0",
        Profile = new Http.RuntimeProfileStatus
        {
            ProfileId = "win-x64-cpu", Accelerator = Http.RuntimeAccelerator.Cpu, Components = [],
        },
    };
}

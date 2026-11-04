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
        Assert.True(viewModel.IsProgressIndeterminate);
        Assert.NotEqual(100, viewModel.ProgressValue);
    }

    [Fact]
    public void IdleAndReadyStatesDoNotReportAnActiveOperation()
    {
        var model = new RuntimeStatusViewModel();
        Assert.False(model.IsOperationActive);

        model.ApplySnapshot(Ready());
        Assert.False(model.IsOperationActive);

        model.ReportServiceUnavailable();
        Assert.False(model.IsOperationActive);
    }

    [Fact]
    public void SnapshotMaintenanceDrivesActiveState()
    {
        var model = new RuntimeStatusViewModel();
        model.ApplySnapshot(Ready() with
        {
            Maintenance = new Http.RuntimeMaintenanceStatus
            {
                OperationId = "sup-op-1",
                Sequence = 4,
                Operation = Http.RuntimeMaintenanceOperation.Ensure,
                OperationState = Http.RuntimeOperationState.Running,
                Phase = Http.RuntimeMaintenancePhase.PrepareRuntime,
                ProfileId = "win-x64-cpu",
                UpdatedAt = "2026-10-01T00:00:00Z",
            },
        });
        Assert.True(model.IsOperationActive);

        model.ApplySnapshot(Ready() with
        {
            Maintenance = new Http.RuntimeMaintenanceStatus
            {
                OperationId = "sup-op-1",
                Sequence = 5,
                Operation = Http.RuntimeMaintenanceOperation.Ensure,
                OperationState = Http.RuntimeOperationState.Succeeded,
                Phase = Http.RuntimeMaintenancePhase.CommitRuntime,
                ProfileId = "win-x64-cpu",
                UpdatedAt = "2026-10-01T00:00:02Z",
            },
        });
        Assert.False(model.IsOperationActive);
        Assert.Equal("维护操作已完成", model.Status);
    }

    [Fact]
    public void ServiceMaintenanceWithoutLocalOperationStaysActiveUntilReady()
    {
        var model = new RuntimeStatusViewModel();
        model.ApplySnapshot(Ready() with
        {
            ServiceState = Http.RuntimeServiceState.Maintenance,
        });
        Assert.True(model.IsOperationActive);
        Assert.Equal("正在维护运行时", model.Status);

        model.ApplySnapshot(Ready());
        Assert.False(model.IsOperationActive);
    }

    [Fact]
    public void UnknownByteTotalDoesNotBecomeStepCountOrPercentage()
    {
        var model = new RuntimeStatusViewModel();
        model.ApplySnapshot(Ready() with
        {
            Maintenance = new Http.RuntimeMaintenanceStatus
            {
                OperationId = "sup-op-2",
                Sequence = 1,
                Operation = Http.RuntimeMaintenanceOperation.Ensure,
                OperationState = Http.RuntimeOperationState.Running,
                Phase = Http.RuntimeMaintenancePhase.InstallProfile,
                ProfileId = "win-x64-cpu",
                UpdatedAt = "2026-10-01T00:00:00Z",
                Progress = new Http.ProgressSnapshot
                {
                    Unit = Http.ProgressUnit.Bytes,
                    Current = 1024,
                },
            },
        });
        Assert.True(model.IsProgressIndeterminate);
        Assert.Contains("bytes", model.ProgressText);
        Assert.DoesNotContain("步", model.ProgressText);
    }

    [Fact]
    public void RealBytesTotalProjectsDeterminatePercentage()
    {
        var model = new RuntimeStatusViewModel();
        model.ApplySnapshot(Ready() with
        {
            Maintenance = new Http.RuntimeMaintenanceStatus
            {
                OperationId = "sup-op-3",
                Sequence = 2,
                Operation = Http.RuntimeMaintenanceOperation.Ensure,
                OperationState = Http.RuntimeOperationState.Running,
                Phase = Http.RuntimeMaintenancePhase.PrepareRuntime,
                ProfileId = "win-x64-cpu",
                UpdatedAt = "2026-10-01T00:00:01Z",
                Progress = new Http.ProgressSnapshot
                {
                    Unit = Http.ProgressUnit.Bytes,
                    Current = 50,
                    Total = 100,
                },
            },
        });

        Assert.Equal(50, model.ProgressValue);
        Assert.Equal("50 / 100 bytes", model.ProgressText);
        Assert.False(model.IsProgressIndeterminate);
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

using VibeOCR.App.ViewModels;
using Http = VibeOCR.Contracts.HttpV2;
using Host = VibeOCR.Runtime.Contracts.Generated.Host;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class RuntimeStatusViewModelTests
{
    [Fact]
    public void InstallerEventProjectsCurrentComponentAndProgress()
    {
        var viewModel = new RuntimeStatusViewModel();
        viewModel.ApplyProfile(new Host.RuntimeProfileDescriptor
        {
            ProfileId = "win-x64-cpu",
            Accelerator = Host.Accelerator.Cpu,
            Components =
            [
                new Host.RuntimeComponentDescriptor
                {
                    ComponentId = "ocr_engine",
                    DisplayName = "OCR engine",
                    Version = "3.7.0",
                },
            ],
        });

        viewModel.ApplyMaintenance(new Host.RuntimeMaintenanceEvent
        {
            ProtocolVersion = 2,
            EventVersion = 1,
            EventType = Host.RuntimeMaintenanceEventType.Progress,
            Operation = Host.RuntimeHostOperation.Ensure,
            Snapshot = new Host.RuntimeMaintenanceSnapshot
            {
                OperationId = "op-1",
                Sequence = 1,
                Operation = Host.RuntimeHostOperation.Ensure,
                OperationState = Host.RuntimeOperationState.Running,
                Phase = Host.RuntimeMaintenancePhase.InstallProfile,
                ProfileId = "win-x64-cpu",
                ComponentId = "ocr_engine",
                UpdatedAt = "2026-08-05T00:00:00Z",
                Progress = new Host.ProgressSnapshot
                {
                    Unit = Host.ProgressUnit.Steps,
                    Current = 1,
                    Total = 4,
                },
            },
            MessageCode = "runtime.installing",
        });

        Assert.Equal("正在安装", Assert.Single(viewModel.Components).State);
        Assert.Equal("安装重依赖", viewModel.Phase);
        Assert.Equal("1 / 4 步", viewModel.ProgressText);
        Assert.True(viewModel.IsProgressIndeterminate);
    }

    [Fact]
    public void RealBytesTotalProjectsDeterminatePercentage()
    {
        var viewModel = new RuntimeStatusViewModel();

        viewModel.ApplyMaintenance(new Host.RuntimeMaintenanceEvent
        {
            ProtocolVersion = 2,
            EventVersion = 1,
            EventType = Host.RuntimeMaintenanceEventType.Progress,
            Operation = Host.RuntimeHostOperation.Ensure,
            Snapshot = new Host.RuntimeMaintenanceSnapshot
            {
                OperationId = "op-1",
                Sequence = 2,
                Operation = Host.RuntimeHostOperation.Ensure,
                OperationState = Host.RuntimeOperationState.Running,
                Phase = Host.RuntimeMaintenancePhase.PrepareRuntime,
                ProfileId = "win-x64-cpu",
                UpdatedAt = "2026-08-05T00:00:01Z",
                Progress = new Host.ProgressSnapshot
                {
                    Unit = Host.ProgressUnit.Bytes,
                    Current = 50,
                    Total = 100,
                },
            },
            MessageCode = "runtime.extract_python",
        });

        Assert.Equal(50, viewModel.ProgressValue);
        Assert.Equal("50 / 100 bytes", viewModel.ProgressText);
        Assert.False(viewModel.IsProgressIndeterminate);
    }

    [Fact]
    public void SuccessfulStdioOperationDoesNotInventComponentActualState()
    {
        var viewModel = new RuntimeStatusViewModel();
        viewModel.ApplyProfile(new Host.RuntimeProfileDescriptor
        {
            ProfileId = "win-x64-cpu",
            Accelerator = Host.Accelerator.Cpu,
            Components =
            [
                new Host.RuntimeComponentDescriptor
                {
                    ComponentId = "ocr_engine",
                    DisplayName = "OCR engine",
                    Version = "3.7.0",
                },
            ],
        });

        viewModel.ApplyMaintenance(new Host.RuntimeMaintenanceEvent
        {
            ProtocolVersion = 2,
            EventVersion = 1,
            EventType = Host.RuntimeMaintenanceEventType.Snapshot,
            Operation = Host.RuntimeHostOperation.Inspect,
            Snapshot = new Host.RuntimeMaintenanceSnapshot
            {
                OperationId = "inspect-1",
                Sequence = 2,
                Operation = Host.RuntimeHostOperation.Inspect,
                OperationState = Host.RuntimeOperationState.Succeeded,
                Phase = Host.RuntimeMaintenancePhase.CommitRuntime,
                ProfileId = "win-x64-cpu",
                UpdatedAt = "2026-08-05T00:00:02Z",
            },
            MessageCode = "runtime.inspect_complete",
        });

        Assert.Equal("等待检查", Assert.Single(viewModel.Components).State);
        Assert.Equal("维护操作已完成", viewModel.Status);
    }

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
        Assert.Equal(100, viewModel.ProgressValue);
    }
}

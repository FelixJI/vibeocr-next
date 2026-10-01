using VibeOCR.App.ViewModels;
using Http = VibeOCR.Contracts.HttpV2;
using Host = VibeOCR.Runtime.Contracts.Generated.Host;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class RuntimeStatusViewModelTests
{
    [Theory]
    [InlineData(Host.RuntimeOperationState.Failed, "运行时安装失败")]
    [InlineData(Host.RuntimeOperationState.Cancelled, "运行时安装已取消")]
    public void ReadyWithoutMaintenancePreservesTerminalResult(Host.RuntimeOperationState state, string expected)
    {
        var model = new RuntimeStatusViewModel();
        model.ApplyMaintenance(Maintenance(8, state));
        model.ApplySnapshot(Ready());
        Assert.Equal(expected, model.Status);
        Assert.NotEqual(100, model.ProgressValue);
    }

    [Fact]
    public void PausedServiceDoesNotReplaceMaintenanceFailure()
    {
        var model = new RuntimeStatusViewModel();
        model.ApplySnapshot(Ready());
        model.ApplyMaintenance(Maintenance(8, Host.RuntimeOperationState.Failed));
        model.ReportServicePausedForMaintenance();
        Assert.Contains("已暂停", model.ServiceStatus);
        Assert.Equal("运行时安装失败", model.Status);
        model.ApplySnapshot(Ready());
        Assert.Equal("运行时已就绪", model.ServiceStatus);
        Assert.Equal("运行时安装失败", model.Status);
    }

    [Fact]
    public void ReplayedProgressCannotReplaceTerminalResult()
    {
        var model = new RuntimeStatusViewModel();
        model.ApplyMaintenance(Maintenance(8, Host.RuntimeOperationState.Failed));
        model.ApplyMaintenance(Maintenance(7, Host.RuntimeOperationState.Running));
        model.ApplyMaintenance(Maintenance(8, Host.RuntimeOperationState.Running));
        model.ApplyMaintenance(Maintenance(9, Host.RuntimeOperationState.Running));
        Assert.Equal("运行时安装失败", model.Status);
    }

    [Fact]
    public void ProgressDetailsPreservePackageAndStepWithoutRawDiagnostics()
    {
        var model = new RuntimeStatusViewModel();
        model.ApplyMaintenance(Maintenance(1, Host.RuntimeOperationState.Running) with
        {
            MessageArgs = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["package"] = System.Text.Json.JsonSerializer.SerializeToElement("paddlepaddle-gpu"),
                ["step"] = System.Text.Json.JsonSerializer.SerializeToElement("runtime.download_package"),
                ["elapsed_seconds"] = System.Text.Json.JsonSerializer.SerializeToElement("12.5"),
                ["detail"] = System.Text.Json.JsonSerializer.SerializeToElement("https://example.invalid/private"),
            },
        });
        Assert.Contains("paddlepaddle-gpu", model.ProgressDetail);
        Assert.Contains("runtime.download_package", model.ProgressDetail);
        Assert.Contains("12.5", model.ProgressDetail);
        Assert.DoesNotContain("https://", model.ProgressDetail);
    }

    [Fact]
    public void UnknownByteTotalDoesNotBecomeStepCountOrPercentage()
    {
        var model = new RuntimeStatusViewModel();
        var update = Maintenance(1, Host.RuntimeOperationState.Running);
        model.ApplyMaintenance(update with { Snapshot = update.Snapshot with
        {
            Progress = new Host.ProgressSnapshot { Unit = Host.ProgressUnit.Bytes, Current = 1024 },
        }});
        Assert.True(model.IsProgressIndeterminate);
        Assert.Contains("bytes", model.ProgressText);
        Assert.DoesNotContain("步", model.ProgressText);
    }

    private static Host.RuntimeMaintenanceEvent Maintenance(int sequence, Host.RuntimeOperationState state) => new()
    {
        ProtocolVersion = 2, EventVersion = 2, EventType = Host.RuntimeMaintenanceEventType.Progress,
        Operation = Host.RuntimeHostOperation.Ensure, MessageCode = "runtime.installing",
        Snapshot = new Host.RuntimeMaintenanceSnapshot
        {
            OperationId = "ui-test", Sequence = sequence, Operation = Host.RuntimeHostOperation.Ensure,
            OperationState = state, Phase = Host.RuntimeMaintenancePhase.InstallProfile,
            ProfileId = "win-x64-cpu", UpdatedAt = "2026-09-21T00:00:00Z",
        },
    };

    private static Http.RuntimeStatusSnapshot Ready() => new()
    {
        InstanceId = "sup-test", ServiceState = Http.RuntimeServiceState.Ready, BackendVersion = "0.14.0",
        Profile = new Http.RuntimeProfileStatus
        {
            ProfileId = "win-x64-cpu", Accelerator = Http.RuntimeAccelerator.Cpu, Components = [],
        },
    };

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
    public void MaintenanceLifecycleDrivesActiveProgressAndTerminalsExit()
    {
        var model = new RuntimeStatusViewModel();
        model.BeginMaintenance("ui-op-active");
        Assert.True(model.IsOperationActive);

        model.ApplyMaintenance(Maintenance(1, Host.RuntimeOperationState.Running) with
        {
            Snapshot = new Host.RuntimeMaintenanceSnapshot
            {
                OperationId = "ui-op-active",
                Sequence = 1,
                Operation = Host.RuntimeHostOperation.Ensure,
                OperationState = Host.RuntimeOperationState.Running,
                Phase = Host.RuntimeMaintenancePhase.InstallBackend,
                ProfileId = "win-x64-cpu",
                UpdatedAt = "2026-10-01T00:00:00Z",
            },
        });
        Assert.True(model.IsOperationActive);

        model.ApplyMaintenance(Maintenance(2, Host.RuntimeOperationState.Cancelled) with
        {
            Snapshot = new Host.RuntimeMaintenanceSnapshot
            {
                OperationId = "ui-op-active",
                Sequence = 2,
                Operation = Host.RuntimeHostOperation.Ensure,
                OperationState = Host.RuntimeOperationState.Cancelled,
                Phase = Host.RuntimeMaintenancePhase.InstallBackend,
                ProfileId = "win-x64-cpu",
                UpdatedAt = "2026-10-01T00:00:01Z",
            },
        });
        // 取消是终态：退出活动动画，保留准确终态文案。
        Assert.False(model.IsOperationActive);
        Assert.Equal("运行时安装已取消", model.Status);

        model.ApplySnapshot(Ready());
        Assert.False(model.IsOperationActive);
        Assert.Equal("运行时安装已取消", model.Status);
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
    public void InFlightLocalOperationSurvivesUnrelatedSnapshotChurn()
    {
        var model = new RuntimeStatusViewModel();
        model.BeginMaintenance("ui-op-2");
        model.ApplySnapshot(Ready());
        // 本地在途操作未被快照终绪：活动动画保持，等待安装器事件终绪。
        Assert.True(model.IsOperationActive);

        model.CompleteMaintenance("failed");
        Assert.False(model.IsOperationActive);
    }
}

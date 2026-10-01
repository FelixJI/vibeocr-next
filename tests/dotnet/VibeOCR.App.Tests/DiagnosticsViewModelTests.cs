using VibeOCR.App.ViewModels;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using VibeOCR.Platform.Bootstrap;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class DiagnosticsViewModelTests
{
  [Fact]
  public void MilestoneRecordingAndSnapshotReadsAreThreadSafe()
  {
    var viewModel = new DiagnosticsViewModel("test", new PrerequisiteReport([]));

    // 后台连接线程写入里程碑的同时，桥接读者反复取快照；快照隔离下
    // 不得抛出集合并发修改异常，且名称去重语义保持。
    Parallel.For(0, 400, index =>
    {
      viewModel.RecordMilestone($"T{index % 7}", TimeSpan.FromMilliseconds(index));
      Assert.True(viewModel.Milestones.Count is > 0 and <= 7);
    });

    Assert.Equal(7, viewModel.Milestones.Count);
    Assert.Equal(viewModel.Milestones.Count,
      viewModel.Milestones.Select(item => item.Name).Distinct().Count());
  }

  [Fact]
  public void SupervisorSnapshotReflectsLatestHealth()
  {
    var viewModel = new DiagnosticsViewModel("test", new PrerequisiteReport([]));
    Assert.Equal(SupervisorHealthState.NotReady, viewModel.Supervisor.State);

    viewModel.UpdateSupervisor(new SupervisorHealth(
      SupervisorHealthState.Ready, "sup-1", 2, null));
    Assert.Equal(SupervisorHealthState.Ready, viewModel.Supervisor.State);
    Assert.Equal("sup-1", viewModel.Supervisor.InstanceId);
  }

  [Fact]
  public async Task SupervisorHealthChangeBroadcastsExactlyOneDiagnosticsState()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-diag-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      var diagnostics = new DiagnosticsViewModel("test", new PrerequisiteReport([]));
      await using var handler = new DesktopWorkbenchCommandHandler(
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        static () => throw new InvalidOperationException(),
        diagnostics,
        broker,
        root,
        static () => 0,
        annotations);

      var broadcasts = new List<WorkbenchState>();
      handler.StateChanged += state => broadcasts.Add(state);

      // 一次 UpdateSupervisor 触发四个属性通知，但只有代表健康变更的
      // SupervisorStatus 允许广播一次诊断投影。
      diagnostics.UpdateSupervisor(new SupervisorHealth(
        SupervisorHealthState.Ready, "sup-1", 2, null));
      DiagnosticsWorkbenchState ready = Assert.IsType<DiagnosticsWorkbenchState>(
        Assert.Single(broadcasts));
      Assert.Equal("已就绪", ready.SupervisorStatus);
      Assert.True(ready.IsReady);

      broadcasts.Clear();
      // 里程碑写入不产生诊断广播（轨迹仅供导出/后续快照）。
      diagnostics.RecordMilestone("T5", TimeSpan.FromSeconds(1));
      Assert.Empty(broadcasts);

      diagnostics.UpdateSupervisor(new SupervisorHealth(
        SupervisorHealthState.Faulted, null, null, "连接失败"));
      var faulted = Assert.IsType<DiagnosticsWorkbenchState>(Assert.Single(broadcasts));
      Assert.Equal("连接失败", faulted.SupervisorStatus);
      await handler.DisposeAsync();
    }
    finally
    {
      Directory.Delete(root, recursive: true);
    }
  }
}

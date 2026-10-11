using System.ComponentModel;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Features.Shell;
using VibeOCR.App.Features.Update;
using VibeOCR.App.Services;
using Windows.System;

namespace VibeOCR.App.Workbench;

internal sealed class WorkbenchSystemOwner(VibeOCR.App.ViewModels.DiagnosticsViewModel diagnostics, string resourceRoot, Lazy<ShellViewModel> shell, IStructuredClipboardPlatform structuredClipboard, Func<UpdateViewModel> updateFactory, WorkbenchOperations operations)
{
  private Lazy<UpdateViewModel>? updateHolder;
  private Lazy<UpdateViewModel> update => updateHolder ??= new Lazy<UpdateViewModel>(() =>
  {
    UpdateViewModel model = updateFactory(); model.PropertyChanged += OnUpdatePropertyChanged; return model;
  });
  internal bool Handles(WorkbenchCommand command) => command is OpenProjectPageCommand or CheckUpdateCommand or DownloadUpdateCommand or CancelUpdateCommand or CancelRuntimeForUpdateCommand or ExportDiagnosticsCommand or CopyDiagnosticsCommand;

  internal async ValueTask<WorkbenchState?> ExecuteAsync(WorkbenchCommand command, CancellationToken cancellationToken) => command switch
  {
    OpenProjectPageCommand => await OpenProjectPageAsync(cancellationToken),
    CheckUpdateCommand => await CheckUpdateAsync(cancellationToken),
    DownloadUpdateCommand => StartUpdateDownload(cancellationToken),
    CancelUpdateCommand => CancelUpdate(),
    CancelRuntimeForUpdateCommand => await CancelRuntimeForUpdateAsync(cancellationToken),
    ExportDiagnosticsCommand => await ExportDiagnosticsAsync(cancellationToken),
    CopyDiagnosticsCommand => await CopyDiagnosticsAsync(cancellationToken),
    _ => throw new InvalidOperationException("Unsupported System workbench command."),
  };

  private long updateGeneration;
  internal void OnDiagnosticsPropertyChanged(object? sender, PropertyChangedEventArgs args)
  {
    if (!operations.IsDisposed &&
      args.PropertyName is nameof(VibeOCR.App.ViewModels.DiagnosticsViewModel.SupervisorStatus)
        or nameof(VibeOCR.App.ViewModels.DiagnosticsViewModel.DeviceEvidence))
    {
      operations.Publish(DiagnosticsState());
    }
  }

  private async Task<AboutWorkbenchState> OpenProjectPageAsync(
    CancellationToken cancellationToken)
  {
    cancellationToken.ThrowIfCancellationRequested();
    if (!await Launcher.LaunchUriAsync(shell.Value.ProjectUri))
    {
      throw new InvalidOperationException("Windows could not open the project page.");
    }
    return AboutState();
  }

  private async Task<UpdateWorkbenchState> CheckUpdateAsync(
    CancellationToken cancellationToken)
  {
    Interlocked.Increment(ref updateGeneration);
    await update.Value.CheckAsync(cancellationToken);
    return new UpdateWorkbenchState(
      update.Value.IsBusy,
      update.Value.StatusCode,
      update.Value.LatestVersion,
      update.Value.UpdateAvailable);
  }

  private async Task<UpdateWorkbenchState> DownloadUpdateAsync(
    CancellationToken cancellationToken)
  {
    await update.Value.DownloadAndApplyAsync(cancellationToken);
    return UpdateState();
  }

  private UpdateWorkbenchState? StartUpdateDownload(CancellationToken cancellationToken)
  {
    long generation = Interlocked.Increment(ref updateGeneration);
    return operations.Start(UpdateState() with { IsBusy = true },
      () => CompleteUpdateDownloadAsync(generation, cancellationToken));
  }

  private async Task CompleteUpdateDownloadAsync(
    long generation,
    CancellationToken cancellationToken)
  {
    try
    {
      UpdateWorkbenchState state = await DownloadUpdateAsync(cancellationToken);
      if (generation == Volatile.Read(ref updateGeneration))
      {
        operations.PublishTerminal(state);
      }
    }
    catch (OperationCanceledException)
    {
      if (generation == Volatile.Read(ref updateGeneration))
      {
        operations.PublishTerminal(UpdateState());
      }
    }
    catch (Exception error)
    {
      AppLog.Error("Update workbench operation failed", error);
      if (generation == Volatile.Read(ref updateGeneration))
      {
        operations.PublishTerminal(UpdateState());
      }
    }
  }

  private UpdateWorkbenchState CancelUpdate()
  {
    Interlocked.Increment(ref updateGeneration);
    update.Value.Cancel();
    return UpdateState();
  }

  private async Task<UpdateWorkbenchState> CancelRuntimeForUpdateAsync(
    CancellationToken cancellationToken)
  {
    await update.Value.CancelRuntimeMaintenanceAndWaitAsync(cancellationToken);
    return UpdateState();
  }

  private async Task<DiagnosticsWorkbenchState> ExportDiagnosticsAsync(
    CancellationToken cancellationToken)
  {
    string destination = Path.Combine(
      Path.GetDirectoryName(resourceRoot) ?? resourceRoot,
      $"vibeocr-diagnostics-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
    await diagnostics.ExportAsync(destination, cancellationToken);
    return DiagnosticsState();
  }

  /// <summary>
  /// 复制诊断详情：复用导出的同一脱敏文档，经既有平台剪贴板接缝写入；
  /// busy 失败走既有 clipboard_busy 问题反馈，不新增第二条剪贴板路径。
  /// </summary>
  private async Task<DiagnosticsWorkbenchState> CopyDiagnosticsAsync(
    CancellationToken cancellationToken)
  {
    await structuredClipboard.WriteTextAsync(
      diagnostics.BuildRedactedExportJson(),
      cancellationToken);
    return DiagnosticsState();
  }

  private UpdateWorkbenchState UpdateState() => new(
    update.Value.IsBusy,
    update.Value.StatusCode,
    update.Value.LatestVersion,
    update.Value.UpdateAvailable,
    update.Value.CanCancelRuntimeMaintenance);

  internal void OnUpdatePropertyChanged(object? sender, PropertyChangedEventArgs args)
  {
    if (!operations.IsDisposed &&
      args.PropertyName == nameof(UpdateViewModel.CanCancelRuntimeMaintenance))
    {
      operations.Publish(UpdateState());
    }
  }

  internal AboutWorkbenchState AboutState() => new(
    shell.Value.AppVersion,
    shell.Value.License,
    shell.Value.ProjectUri.AbsoluteUri);

  internal DiagnosticsWorkbenchState DiagnosticsState() => new(
    diagnostics.SupervisorStatus,
    diagnostics.ProtocolStatus,
    diagnostics.IsReady,
    diagnostics.Milestones
      .OrderBy(milestone => milestone.Name)
      .Select(milestone => milestone.Name)
      .ToArray(),
    diagnostics.DeviceEvidence);

  internal void Stop()
  {
    Interlocked.Increment(ref updateGeneration);
    if (updateHolder?.IsValueCreated == true)
    {
      update.Value.PropertyChanged -= OnUpdatePropertyChanged;
      update.Value.Cancel(); update.Value.Dispose();
    }
    diagnostics.PropertyChanged -= OnDiagnosticsPropertyChanged;
  }
}

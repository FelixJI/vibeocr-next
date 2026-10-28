using System.ComponentModel;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Features.Shell;
using VibeOCR.App.Features.Update;
using VibeOCR.App.Services;
using Windows.System;

namespace VibeOCR.App.Workbench;

internal sealed class WorkbenchSystemOwner(VibeOCR.App.ViewModels.DiagnosticsViewModel diagnostics, string resourceRoot, RecoverableLazy<ShellViewModel> shell, IStructuredClipboardPlatform structuredClipboard, Func<UpdateViewModel> updateFactory, WorkbenchOperations operations)
{
  private readonly object updateGate = new();
  private UpdateViewModel? updateModel;
  /// <summary>
  /// 可恢复取值（不缓存异常）：工厂抛出（如桌面壳初始化前尚未接线）后，
  /// 接线完成即可创建真实模型；创建与事件订阅在同一临界区。
  /// </summary>
  private UpdateViewModel update
  {
    get
    {
      lock (updateGate)
      {
        if (updateModel is null)
        {
          UpdateViewModel model = updateFactory();
          model.PropertyChanged += OnUpdatePropertyChanged;
          updateModel = model;
        }
        return updateModel;
      }
    }
  }
  internal bool Handles(WorkbenchCommand command) => command is OpenProjectPageCommand or CheckUpdateCommand or DownloadUpdateCommand or CancelUpdateCommand or CancelRuntimeForUpdateCommand or SetUpdateSourceCommand or ExportDiagnosticsCommand or CopyDiagnosticsCommand;

  internal async ValueTask<WorkbenchState?> ExecuteAsync(WorkbenchCommand command, CancellationToken cancellationToken) => command switch
  {
    OpenProjectPageCommand => await OpenProjectPageAsync(cancellationToken),
    CheckUpdateCommand => StartUpdateCheck(cancellationToken),
    DownloadUpdateCommand => StartUpdateDownload(cancellationToken),
    CancelUpdateCommand => CancelUpdate(),
    CancelRuntimeForUpdateCommand => await CancelRuntimeForUpdateAsync(cancellationToken),
    SetUpdateSourceCommand setSource => SetUpdateSource(setSource),
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

  /// <summary>
  /// 检查更新立即发布 busy 快照（update.checking），慢网络期间前端可
  /// 见进行中状态、取消与切源控件可正确联动；终态经后台完成回调发布。
  /// 已在进行中的更新操作不得再启动：忙时直接返回当前状态，不推进
  /// generation（否则首次真实完成会被当作过期被抑制，UI 永久 busy）。
  /// </summary>
  private UpdateWorkbenchState? StartUpdateCheck(CancellationToken cancellationToken)
  {
    UpdateViewModel model = update;
    if (model.IsBusy)
    {
      return UpdateState();
    }
    long generation = Interlocked.Increment(ref updateGeneration);
    return operations.Start(
      UpdateState() with
      {
        IsBusy = true,
        StatusCode = UpdateViewModel.CheckingStatusCode,
      },
      () => CompleteUpdateCheckAsync(generation, cancellationToken));
  }

  private async Task CompleteUpdateCheckAsync(
    long generation,
    CancellationToken cancellationToken)
  {
    try
    {
      await update.CheckAsync(cancellationToken);
      if (generation == Volatile.Read(ref updateGeneration))
      {
        operations.PublishTerminal(UpdateState());
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
      AppLog.Error("Update check workbench operation failed", error);
      if (generation == Volatile.Read(ref updateGeneration))
      {
        operations.PublishTerminal(UpdateState());
      }
    }
  }

  private async Task<UpdateWorkbenchState> DownloadUpdateAsync(
    CancellationToken cancellationToken)
  {
    await update.DownloadAndApplyAsync(cancellationToken);
    return UpdateState();
  }

  private UpdateWorkbenchState? StartUpdateDownload(CancellationToken cancellationToken)
  {
    UpdateViewModel model = update;
    if (model.IsBusy || !model.UpdateAvailable)
    {
      // 下载进行中/无可用更新不启动新任务，也不作废在途完成的发布权。
      return UpdateState();
    }
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
    // 不推进 generation：取消后完成的检查/下载仍需发布“已取消”终态，
    // 否则 UI 永远停在 busy。终态读取的是 VM 当前状态，不会田旧覆新。
    update.Cancel();
    return UpdateState();
  }

  private async Task<UpdateWorkbenchState> CancelRuntimeForUpdateAsync(
    CancellationToken cancellationToken)
  {
    await update.CancelRuntimeMaintenanceAndWaitAsync(cancellationToken);
    return UpdateState();
  }

  private UpdateWorkbenchState SetUpdateSource(SetUpdateSourceCommand command)
  {
    update.SetSource(command.SourceId, command.Kind, command.Url);
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

  internal UpdateWorkbenchState UpdateState() => new(
    update.IsBusy,
    update.StatusCode,
    update.LatestVersion,
    update.UpdateAvailable,
    update.CanCancelRuntimeMaintenance,
    update.StatusMessage,
    update.DetectionEndpoint,
    update.Sources?.Select(option => new UpdateSourceOptionState(
      option.Id, option.Kind, option.DisplayName, option.Endpoint, option.Selected)).ToArray());

  /// <summary>
  /// 初始更新快照：更新 VM 尚未接线（宿主壳初始化前）时仍发布基础
  /// “未检查”状态，不让 about/update 依赖桌面壳初始化时序；源目录随
  /// VM 就绪后的首次状态发布补齐。
  /// </summary>
  internal UpdateWorkbenchState InitialUpdateState()
  {
    try
    {
      return UpdateState();
    }
    catch (InvalidOperationException)
    {
      return new UpdateWorkbenchState(
        false, UpdateViewModel.UncheckedStatusCode, null, false);
    }
  }

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
    UpdateViewModel? model;
    lock (updateGate) model = updateModel;
    if (model is not null)
    {
      model.PropertyChanged -= OnUpdatePropertyChanged;
      model.Cancel(); model.Dispose();
    }
    diagnostics.PropertyChanged -= OnDiagnosticsPropertyChanged;
  }
}

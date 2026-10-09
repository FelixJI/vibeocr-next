using System.Text.Json;
using VibeOCR.App.Features.Pdf;
using VibeOCR.App.ViewModels;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using VibeOCR.Contracts.HttpV2;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;
using Xunit;

namespace VibeOCR.App.Tests;

public sealed class PdfWorkspaceTests
{
  [Fact]
  public async Task SameNamedDocumentsRetainSelectionAndLateSaveAsOwnsOriginalEntry()
  {
    using var fixture = new Fixture();
    await using var handler = fixture.Handler();
    await handler.ExecuteAsync(new OpenDroppedPdfCommand(Path.Combine(fixture.Root, "a", "same.pdf")), CancellationToken.None);
    PdfDocumentEntry first = Assert.Single(handler.PdfDocuments);
    await handler.ExecuteAsync(new PdfBoundCommand(first.Id, new SelectPdfPagesCommand([1])), CancellationToken.None);
    fixture.Client.PendingSave = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task<WorkbenchCommandOutcome> save = handler.ExecuteAsync(new PdfBoundCommand(first.Id, new SavePdfAsCommand()), CancellationToken.None).AsTask();
    await fixture.Client.SaveEntered.Task;
    await handler.ExecuteAsync(new OpenDroppedPdfCommand(Path.Combine(fixture.Root, "b", "same.pdf")), CancellationToken.None);
    PdfDocumentEntry second = handler.PdfDocuments.Last();
    Assert.NotEqual(first.Id, second.Id); Assert.NotEqual(first.Model.SessionId, second.Model.SessionId);
    fixture.Client.PendingSave.SetResult(fixture.SaveTarget);
    await save;
    Assert.Equal(fixture.SaveTarget, first.Model.FilePath);
    Assert.EndsWith(Path.Combine("b", "same.pdf"), second.Model.FilePath!);
    Assert.True(second.Model.IsModified);
    var selected = await handler.ExecuteAsync(new PdfBoundCommand(first.Id, new ActivatePdfDocumentCommand()), CancellationToken.None);
    PdfWorkbenchState state = Assert.IsType<PdfWorkbenchState>(Assert.Single(selected.States));
    Assert.Equal(first.Id, state.DocumentId); Assert.Equal([1], state.SelectedPages!);
    await handler.ExecuteAsync(new PdfBoundCommand(first.Id, new SavePdfCommand()), CancellationToken.None);
    Assert.Equal(fixture.SaveTarget, fixture.Client.Saves.Last().Path);
  }

  [Fact]
  public async Task WindowAndSwitchRevokePublishedResourcesAndDuplicateTargetActivates()
  {
    using var fixture = new Fixture(70);
    await using var handler = fixture.Handler();
    string path = Path.Combine(fixture.Root, "a.pdf");
    await handler.ExecuteAsync(new OpenDroppedPdfCommand(path), CancellationToken.None);
    PdfDocumentEntry first = Assert.Single(handler.PdfDocuments);
    var initial = await handler.ExecuteAsync(new PdfBoundCommand(first.Id, new SetPdfWindowCommand(0)), CancellationToken.None);
    string url = Assert.IsType<PdfWorkbenchState>(Assert.Single(initial.States)).Pages![0].Thumbnail!.Url;
    Assert.Equal(64, Directory.GetFiles(fixture.Root, "*.png", SearchOption.AllDirectories).Length);
    await handler.ExecuteAsync(new PdfBoundCommand(first.Id, new SetPdfWindowCommand(64)), CancellationToken.None);
    Assert.Equal(6, Directory.GetFiles(fixture.Root, "*.png", SearchOption.AllDirectories).Length);
    Assert.Throws<WorkbenchResourceAccessException>(() => fixture.Broker.OpenAsync(new Uri(url), TestContext.Current.CancellationToken).GetAwaiter().GetResult());
    await handler.ExecuteAsync(new OpenDroppedPdfCommand(Path.Combine(fixture.Root, "b.pdf")), CancellationToken.None);
    Assert.Equal(64, Directory.GetFiles(fixture.Root, "*.png", SearchOption.AllDirectories).Length);
    await handler.ExecuteAsync(new OpenDroppedPdfCommand(path), CancellationToken.None);
    Assert.Equal(2, handler.PdfDocuments.Count);
    Assert.Equal(6, Directory.GetFiles(fixture.Root, "*.png", SearchOption.AllDirectories).Length);
  }

  [Fact]
  public async Task CopyBatchRecordsPartialFailureRetryAndCancellationWithoutClearingDirty()
  {
    var client = new Client(); var workspace = new PdfWorkspace();
    for (int i = 0; i < 3; i++)
    { var model = Model(client); await model.OpenPathAsync(Path.Combine(Path.GetTempPath(), $"batch-{i}.pdf"), CancellationToken.None); workspace.Add(model); }
    string directory = Path.Combine(Path.GetTempPath(), $"t4-export-{Guid.NewGuid():N}"); Directory.CreateDirectory(directory);
    client.FailCopy = workspace.Documents[1].Model.SessionId;
    await workspace.ExportAsync(directory, false, false, () => { }, CancellationToken.None);
    Assert.Equal(["saved", "failed", "saved"], workspace.ExportItems.Select(item => item.Status));
    Assert.All(workspace.Documents, entry => Assert.True(entry.Model.IsModified));
    client.FailCopy = null;
    await workspace.ExportAsync(directory, false, true, () => { }, CancellationToken.None);
    Assert.All(workspace.ExportItems, item => Assert.Equal("saved", item.Status));
    client.PendingSave = new(TaskCreationOptions.RunContinuationsAsynchronously);
    client.SaveEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task exporting = workspace.ExportAsync(directory, false, false, () => { }, CancellationToken.None);
    await client.SaveEntered.Task;
    workspace.CancelExport(); client.PendingSave.SetResult(client.Saves.Last().Path);
    await exporting;
    Assert.Equal(["saved", "cancelled", "not_started"], workspace.ExportItems.Select(item => item.Status));
    Assert.All(workspace.Documents, entry => Assert.True(entry.Model.IsModified));
  }

  [Fact]
  public async Task ExitCancelKeepsSessionsAndCloseFailureCanRetry()
  {
    using var fixture = new Fixture();
    await using var handler = fixture.Handler();
    await handler.ExecuteAsync(new OpenDroppedPdfCommand(Path.Combine(fixture.Root, "a.pdf")), CancellationToken.None);
    PdfDocumentEntry entry = Assert.Single(handler.PdfDocuments);
    fixture.Decision = PdfCloseDecision.Cancel;
    Assert.False(await handler.RequestCloseAllPdfAsync()); Assert.Empty(fixture.Client.Closed); Assert.Single(handler.PdfDocuments);
    fixture.Decision = PdfCloseDecision.Discard; fixture.Client.FailClose = true;
    Assert.False(await handler.RequestCloseAllPdfAsync()); Assert.NotNull(entry.CloseError); Assert.Single(handler.PdfDocuments);
    fixture.Client.FailClose = false;
    Assert.True(await handler.RequestCloseAllPdfAsync()); Assert.Empty(handler.PdfDocuments);
  }

  [Fact]
  public async Task SettlementWaitsForNonCancelableSaveAndLimitDoesNotRemoveDocuments()
  {
    var client = new Client { PendingSave = new(TaskCreationOptions.RunContinuationsAsynchronously) };
    PdfViewModel model = Model(client); await model.OpenPathAsync("source.pdf", CancellationToken.None);
    Task<PdfSaveResult> save = model.SaveAsync("target.pdf", CancellationToken.None);
    await client.SaveEntered.Task; Task settled = model.CancelAndSettleAsync();
    Assert.False(settled.IsCompleted);
    client.PendingSave.SetResult(Path.GetFullPath("target.pdf"));
    Assert.True((await save).Saved); await settled; Assert.False(model.IsModified);
    var workspace = new PdfWorkspace();
    for (int i = 0; i < PdfWorkspace.MaxDocuments; i++) workspace.Add(Model(client));
    Assert.Throws<InvalidOperationException>(() => workspace.Add(Model(client)));
    Assert.Equal(16, workspace.Documents.Count);
  }

  [Fact]
  public async Task ExitReviewLateBoundCommandsFailAsReceiptsWithoutThrowing()
  {
    using var fixture = new Fixture();
    await using var handler = fixture.Handler();
    // 经实际 application 边界（桥接层下一跳）验证：失败回执而非异常，
    // 宿主只把异常升级为全局恢复面板，回执不会隐藏工作台。
    await using var application = new WorkbenchApplication(
      ["pdf.open", "pdf.rotate", "pdf.save"], WorkbenchRoute.Pdf, handler);
    await handler.ExecuteAsync(new OpenDroppedPdfCommand(Path.Combine(fixture.Root, "a.pdf")), CancellationToken.None);
    PdfDocumentEntry entry = Assert.Single(handler.PdfDocuments);
    // 退出审阅真实挂起在关闭确认上。
    fixture.DecisionPending = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task<bool> exiting = handler.RequestCloseAllPdfAsync();
    try
    {
      Assert.False(exiting.IsCompleted);
      // 退出确认挂起时迟到的位置更新：失败回执，不写入条目预览。
      WorkbenchCommandReceipt latePosition = await application.ExecuteAsync(
        new WorkbenchCommandEnvelope(Guid.NewGuid(), new PdfBoundCommand(entry.Id,
          new SetPdfPreviewPositionCommand(new PdfPreviewPosition(Draft: "exit-draft", Revision: entry.Model.Revision, Page: 0)))),
        CancellationToken.None);
      Assert.False(latePosition.Ok);
      Assert.NotEqual("exit-draft", entry.Preview.Draft);
      // 已关闭/未知文档 ID：失败回执，不抛。
      WorkbenchCommandReceipt closedId = await application.ExecuteAsync(
        new WorkbenchCommandEnvelope(Guid.NewGuid(), new PdfBoundCommand("00000000000000000000000000000000", new SavePdfCommand())),
        CancellationToken.None);
      Assert.False(closedId.Ok);
      // 被拒命令没有产生远端副作用。
      Assert.Empty(fixture.Client.Saves);
    }
    finally
    {
      // 红断言提前抛出时也放行挂起的关闭确认，避免退出流程悬挂。
      fixture.DecisionPending.TrySetResult(PdfCloseDecision.Cancel);
      await exiting;
    }
    // 用户取消退出后原会话仍可用；旧 revision 的迟到写命令在退出窗口外
    // 命中修订门：失败回执，不产生远端副作用。
    Assert.Same(entry, Assert.Single(handler.PdfDocuments));
    Assert.True(entry.Model.HasSession);
    WorkbenchCommandReceipt staleRevision = await application.ExecuteAsync(
      new WorkbenchCommandEnvelope(Guid.NewGuid(), new PdfBoundCommand(entry.Id,
        new RotatePdfCommand(90), entry.Model.Revision - 1)),
      CancellationToken.None);
    Assert.False(staleRevision.Ok);
    Assert.Empty(fixture.Client.Saves);
    WorkbenchCommandOutcome reactivated = await handler.ExecuteAsync(
      new PdfBoundCommand(entry.Id, new ActivatePdfDocumentCommand()), CancellationToken.None);
    Assert.Null(reactivated.Error);
    Assert.Equal(entry.Id, Assert.IsType<PdfWorkbenchState>(Assert.Single(reactivated.States)).DocumentId);
  }

  [Fact]
  public async Task UnconfirmedExportKeepsTargetAndRetryDoesNotDuplicate()
  {
    var client = new Client(); var workspace = new PdfWorkspace();
    for (int i = 0; i < 3; i++)
    { var model = Model(client); await model.OpenPathAsync(Path.Combine(Path.GetTempPath(), $"unconfirmed-{i}.pdf"), CancellationToken.None); workspace.Add(model); }
    string directory = Path.Combine(Path.GetTempPath(), $"t4-unconfirmed-{Guid.NewGuid():N}"); Directory.CreateDirectory(directory);
    // 中间文档在服务端提交副本后丢失响应（本地合成 target 已存在）；
    // 末尾文档是真正的失败项。
    PdfDocumentEntry unconfirmed = workspace.Documents[1];
    client.ThrowCopyAfterSubmit = unconfirmed.Model.SessionId;
    client.FailCopy = workspace.Documents[2].Model.SessionId;
    await workspace.ExportAsync(directory, false, false, () => { }, CancellationToken.None);
    Assert.Equal(["saved", "unconfirmed", "failed"], workspace.ExportItems.Select(item => item.Status));
    // 未确认项保留真实目标与明确反馈，dirty 不因未确认被清除。
    string target = workspace.ExportItems[1].Output!;
    Assert.Equal(Path.Combine(directory, "unconfirmed-1.pdf"), target);
    Assert.Contains("未确认", workspace.ExportItems[1].Error);
    Assert.True(File.Exists(target));
    Assert.All(workspace.Documents, entry => Assert.True(entry.Model.IsModified));
    // 重试只重发真正失败项：未确认项不第二次提交，也不换目标生成副本。
    client.ThrowCopyAfterSubmit = null; client.FailCopy = null;
    await workspace.ExportAsync(directory, false, true, () => { }, CancellationToken.None);
    Assert.Equal(["saved", "unconfirmed", "saved"], workspace.ExportItems.Select(item => item.Status));
    Assert.Equal(target, workspace.ExportItems[1].Output);
    Assert.Equal(1, client.Saves.Count(save => string.Equals(save.Path, target, StringComparison.OrdinalIgnoreCase)));
    Assert.True(unconfirmed.Model.IsModified);
  }

  [Fact]
  public async Task LateRotationDoesNotRevokeAnotherDocumentsWindowAndExitCancelsBeforeSettlement()
  {
    using var fixture = new Fixture(70);
    await using var handler = fixture.Handler();
    await handler.ExecuteAsync(new OpenDroppedPdfCommand(Path.Combine(fixture.Root, "first.pdf")), CancellationToken.None);
    PdfDocumentEntry first = Assert.Single(handler.PdfDocuments);
    fixture.Client.PendingRotate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task<WorkbenchCommandOutcome> rotating = handler.ExecuteAsync(new PdfBoundCommand(first.Id, new RotatePdfCommand(90)), CancellationToken.None).AsTask();
    await fixture.Client.RotateEntered.Task;
    var opened = await handler.ExecuteAsync(new OpenDroppedPdfCommand(Path.Combine(fixture.Root, "second.pdf")), CancellationToken.None);
    PdfWorkbenchState secondState = Assert.IsType<PdfWorkbenchState>(Assert.Single(opened.States));
    string url = secondState.Pages![0].Thumbnail!.Url;
    fixture.Client.PendingRotate.SetResult(new PdfMutateResult(70));
    PdfWorkbenchState late = Assert.IsType<PdfWorkbenchState>(Assert.Single((await rotating).States));
    Assert.Equal(secondState.DocumentId, late.DocumentId);
    Assert.Equal(url, late.Pages![0].Thumbnail!.Url);
    Assert.Equal(64, Directory.GetFiles(fixture.Root, "*.png", SearchOption.AllDirectories).Length);
    await using (var resource = await fixture.Broker.OpenAsync(new Uri(url), TestContext.Current.CancellationToken)) Assert.True(resource.ContentLength > 0);
    fixture.Client.PendingRotate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.Client.RotateEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task<WorkbenchCommandOutcome> settlingWrite = handler.ExecuteAsync(new PdfBoundCommand(secondState.DocumentId!, new RotatePdfCommand(90)), CancellationToken.None).AsTask();
    await fixture.Client.RotateEntered.Task;
    fixture.Decision = PdfCloseDecision.Cancel;
    Task<bool> exiting = handler.RequestCloseAllPdfAsync();
    Assert.Contains(handler.PdfDocuments.Last().Model.SessionId!, fixture.Client.Cancelled);
    Assert.False(exiting.IsCompleted);
    fixture.Client.PendingRotate.SetResult(new PdfMutateResult(70));
    await settlingWrite;
    Assert.False(await exiting);
    Assert.Equal(2, handler.PdfDocuments.Count);
  }

  [Fact]
  public async Task FailedOpensDoNotConsumeSlotsAndEmptyWorkspaceSettingsAreBound()
  {
    using var fixture = new Fixture();
    await using var handler = fixture.Handler();
    fixture.Client.FailOpen = true;
    PdfWorkbenchState empty = Assert.IsType<PdfWorkbenchState>(Assert.Single((await handler.ExecuteAsync(new OpenDroppedPdfCommand(Path.Combine(fixture.Root, "bad.pdf")), CancellationToken.None)).States));
    Assert.Empty(handler.PdfDocuments); Assert.NotNull(empty.DocumentId);
    var parameters = new PdfProcessingSettings(RenderDpi: 144);
    await handler.ExecuteAsync(new PdfBoundCommand(empty.DocumentId!, new SetPdfProcessingSettingsCommand(parameters), 0), CancellationToken.None);
    Assert.Equal(144, Assert.Single(handler.PdfDocuments).Model.ProcessingSettings.RenderDpi);
    fixture.Client.FailOpen = false;
    await handler.ExecuteAsync(new OpenDroppedPdfCommand(Path.Combine(fixture.Root, "good.pdf")), CancellationToken.None);
    PdfDocumentEntry retained = Assert.Single(handler.PdfDocuments);
    fixture.Client.FailOpen = true;
    for (int attempt = 0; attempt < PdfWorkspace.MaxDocuments + 2; attempt++)
    {
      await handler.ExecuteAsync(new OpenDroppedPdfCommand(Path.Combine(fixture.Root, "bad.pdf")), CancellationToken.None);
      await handler.ExecuteAsync(new PdfBoundCommand(retained.Id, new ActivatePdfDocumentCommand()), CancellationToken.None);
      Assert.Same(retained, Assert.Single(handler.PdfDocuments));
      Assert.True(retained.Model.HasSession);
    }
    Assert.NotEqual(empty.DocumentId, retained.Id);
  }

  [Fact]
  public async Task CancelledLateOpenWithFailedRemoteCloseRemainsVisibleAndRetryable()
  {
    using var fixture = new Fixture();
    await using var handler = fixture.Handler();
    fixture.Client.PendingOpen = new(TaskCreationOptions.RunContinuationsAsynchronously);
    string path = Path.Combine(fixture.Root, "late.pdf");
    Task<WorkbenchCommandOutcome> opening = handler.ExecuteAsync(new OpenDroppedPdfCommand(path), CancellationToken.None).AsTask();
    await fixture.Client.OpenEntered.Task;
    PdfDocumentEntry entry = Assert.Single(handler.PdfDocuments);
    fixture.Client.FailClose = true;
    await handler.ExecuteAsync(new PdfBoundCommand(entry.Id, new CancelPdfCommand()), CancellationToken.None);
    fixture.Client.PendingOpen.SetResult(fixture.Client.OpenResult(path));
    PdfWorkbenchState failed = Assert.IsType<PdfWorkbenchState>(Assert.Single((await opening).States));
    Assert.False(entry.Model.HasSession); Assert.True(entry.Model.HasRemoteSession);
    Assert.Equal(entry.Id, Assert.Single(failed.Documents!).DocumentId);
    Assert.True(Assert.Single(failed.Documents!).CloseFailed);
    // 只剩 _unclosedSession/RequestedPath 的条目：同一路径再次打开激活既有条目，
    // 不创建第二份文档/远端会话，保留可重试关闭入口。
    WorkbenchCommandOutcome reopen = await handler.ExecuteAsync(new OpenDroppedPdfCommand(path), CancellationToken.None);
    Assert.Null(reopen.Error);
    Assert.Same(entry, Assert.Single(handler.PdfDocuments));
    Assert.Equal(1, fixture.Client.Opens);
    fixture.Client.FailClose = false;
    await handler.ExecuteAsync(new PdfBoundCommand(entry.Id, new ClosePdfCommand()), CancellationToken.None);
    Assert.Empty(handler.PdfDocuments); Assert.Single(fixture.Client.Closed);
  }

  [Fact]
  public async Task ExitReviewRejectsOpenEntriesAndLatePickerReturn()
  {
    using var fixture = new Fixture();
    await using var handler = fixture.Handler();
    await handler.ExecuteAsync(new OpenDroppedPdfCommand(Path.Combine(fixture.Root, "a.pdf")), CancellationToken.None);
    PdfDocumentEntry entry = Assert.Single(handler.PdfDocuments);
    // 退出审阅开始前，普通打开入口已经进入 picker 等待。
    var pick = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.Source.PendingPick = pick;
    Task<WorkbenchCommandOutcome> picking = handler.ExecuteAsync(new OpenPdfCommand(), CancellationToken.None).AsTask();
    // 退出审阅真实挂起在关闭确认上。
    var decision = new TaskCompletionSource<PdfCloseDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
    fixture.DecisionPending = decision;
    Task<bool> exiting = handler.RequestCloseAllPdfAsync();
    try
    {
      Assert.False(exiting.IsCompleted);
      // 审阅中：拖入打开被拒绝，不新增条目也不发远端 open。
      WorkbenchCommandOutcome dropped = await handler.ExecuteAsync(new OpenDroppedPdfCommand(Path.Combine(fixture.Root, "late.pdf")), CancellationToken.None);
      Assert.NotNull(dropped.Error);
      Assert.Single(handler.PdfDocuments); Assert.Equal(1, fixture.Client.Opens);
      // 审阅前已打开的 picker 迟到返回：在实际加入入口重查并拒绝。
      pick.SetResult(Path.Combine(fixture.Root, "late.pdf"));
      WorkbenchCommandOutcome late = await picking;
      Assert.NotNull(late.Error);
      Assert.Single(handler.PdfDocuments); Assert.Equal(1, fixture.Client.Opens);
      // 用户取消退出：原会话保持，等待用户决定。
      decision.SetResult(PdfCloseDecision.Cancel);
      Assert.False(await exiting);
      Assert.Same(entry, Assert.Single(handler.PdfDocuments));
      Assert.True(entry.Model.HasSession);
    }
    finally
    {
      // 红断言提前抛出时也排空挂起的 picker/退出流程，避免悬挂。
      pick.TrySetResult(null);
      decision.TrySetResult(PdfCloseDecision.Cancel);
      await picking;
      await exiting;
    }
  }

  [Fact]
  public async Task RequestedPathClaimsOpeningEntryAndSaveAsOldSourceReopensIndependently()
  {
    using var fixture = new Fixture();
    await using var handler = fixture.Handler();
    string path = Path.Combine(fixture.Root, "source.pdf");
    fixture.Client.PendingOpen = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task<WorkbenchCommandOutcome> opening = handler.ExecuteAsync(new OpenDroppedPdfCommand(path), CancellationToken.None).AsTask();
    await fixture.Client.OpenEntered.Task;
    PdfDocumentEntry openingEntry = Assert.Single(handler.PdfDocuments);
    try
    {
      // 正在打开中的条目按 RequestedPath 判重：激活既有条目，不二次远端打开、
      // 不占用第二个文档槽位。先派发重复打开再放行远端结果，两条实现路径都有界。
      Task<WorkbenchCommandOutcome> duplicate = handler.ExecuteAsync(new OpenDroppedPdfCommand(path), CancellationToken.None).AsTask();
      Assert.False(opening.IsCompleted);
      fixture.Client.PendingOpen.SetResult(fixture.Client.OpenResult(path));
      await opening;
      WorkbenchCommandOutcome repeated = await duplicate;
      Assert.Null(repeated.Error);
      Assert.Same(openingEntry, Assert.Single(handler.PdfDocuments));
      Assert.Equal(1, fixture.Client.Opens);
      Assert.True(openingEntry.Model.HasSession);
      // SaveAs 成功后以 Model.FilePath 为当前目标；旧源 RequestedPath 不再抢占，
      // 可以作为独立文档重新打开。
      await handler.ExecuteAsync(new PdfBoundCommand(openingEntry.Id, new SavePdfAsCommand()), CancellationToken.None);
      Assert.Equal(fixture.SaveTarget, openingEntry.Model.FilePath);
      await handler.ExecuteAsync(new OpenDroppedPdfCommand(path), CancellationToken.None);
      Assert.Equal(2, handler.PdfDocuments.Count);
      PdfDocumentEntry reopened = handler.PdfDocuments.Last();
      Assert.NotSame(openingEntry, reopened);
      Assert.Equal(path, reopened.Model.FilePath);
    }
    finally
    {
      // 红断言提前抛出时也放行仍在等待的远端 open，避免悬挂。
      fixture.Client.PendingOpen.TrySetResult(fixture.Client.OpenResult(path));
    }
  }

  [Fact]
  public async Task PreviewPositionStoresPerDocumentSilentlyAndValidatesLateWrites()
  {
    using var fixture = new Fixture();
    await using var handler = fixture.Handler();
    await handler.ExecuteAsync(new OpenDroppedPdfCommand(Path.Combine(fixture.Root, "a.pdf")), CancellationToken.None);
    PdfDocumentEntry first = handler.PdfDocuments[0];
    // 活动文档：保存位置但不回发完整 PDF 状态（避免高频位置更新回声）。
    WorkbenchCommandOutcome active = await handler.ExecuteAsync(new PdfBoundCommand(first.Id,
      new SetPdfPreviewPositionCommand(new PdfPreviewPosition(Left: 5, Top: 6, Draft: "d1", Revision: first.Model.Revision, Page: 0))), CancellationToken.None);
    Assert.Null(active.Error); Assert.Empty(active.States);
    await handler.ExecuteAsync(new OpenDroppedPdfCommand(Path.Combine(fixture.Root, "b.pdf")), CancellationToken.None);
    PdfDocumentEntry second = handler.PdfDocuments[1];
    Assert.Same(second, handler.PdfDocuments.Last());
    // 已切走后迟到的最后位置：允许写入已知非活动条目，仍不发布状态。
    WorkbenchCommandOutcome late = await handler.ExecuteAsync(new PdfBoundCommand(first.Id,
      new SetPdfPreviewPositionCommand(new PdfPreviewPosition(Left: 7, Top: 8, Draft: "d2", Revision: first.Model.Revision, Page: 0))), CancellationToken.None);
    Assert.Null(late.Error); Assert.Empty(late.States);
    // 过期修订/越界页码的迟到值被忽略，不覆盖已知位置。
    await handler.ExecuteAsync(new PdfBoundCommand(first.Id,
      new SetPdfPreviewPositionCommand(new PdfPreviewPosition(Draft: "stale", Revision: first.Model.Revision + 5, Page: 0))), CancellationToken.None);
    await handler.ExecuteAsync(new PdfBoundCommand(first.Id,
      new SetPdfPreviewPositionCommand(new PdfPreviewPosition(Draft: "stale-page", Revision: first.Model.Revision, Page: first.Model.PageCount + 1))), CancellationToken.None);
    // 未知文档 ID 仍按入口门拒绝：预期冲突以失败回执返回（经既有错误边界），
    // 不再以异常穿透桥接层触发全局 WebView 恢复面板。
    WorkbenchCommandOutcome unknown = await handler.ExecuteAsync(new PdfBoundCommand("00000000000000000000000000000000",
      new SetPdfPreviewPositionCommand(new PdfPreviewPosition(Draft: "x", Revision: 0, Page: 0))), CancellationToken.None);
    Assert.NotNull(unknown.Error);
    Assert.Equal("desktop_command_failed", unknown.Error.Code);
    Assert.Equal(WorkbenchProblemCategory.Unavailable, unknown.Error.Category);
    // 切回后恢复的是最后被接受的位置/草稿。
    WorkbenchCommandOutcome back = await handler.ExecuteAsync(new PdfBoundCommand(first.Id, new ActivatePdfDocumentCommand()), CancellationToken.None);
    PdfWorkbenchState state = Assert.IsType<PdfWorkbenchState>(Assert.Single(back.States));
    Assert.Equal(first.Id, state.DocumentId);
    Assert.Equal("d2", state.PreviewPosition!.Draft);
    Assert.Equal(7, state.PreviewPosition.Left); Assert.Equal(8, state.PreviewPosition.Top);
  }

  [Fact]
  public async Task LegacyRuntimeRejectsSaveAsAndCopyBeforeSendingRequests()
  {
    var client = new Client(); var model = new PdfViewModel(client, new Source());
    await model.OpenPathAsync("source.pdf", CancellationToken.None);
    Assert.Equal(PdfSaveDisposition.Rejected, (await model.SaveAsAsync("target.pdf", CancellationToken.None)).Disposition);
    Assert.Equal(PdfSaveDisposition.Rejected, (await model.ExportCopyAsync("copy.pdf", model.Revision, model.ProcessingSettings, CancellationToken.None)).Disposition);
    Assert.Empty(client.Saves); Assert.True(model.IsModified); Assert.Equal("source.pdf", model.FilePath);
  }

  private static PdfViewModel Model(Client client, Source? source = null)
  { var model = new PdfViewModel(client, source ?? new Source()); model.SetInspectionCapabilities(["pdf.copy-export.v1"]); return model; }
  private sealed class Source : IPdfFileSource
  {
    public TaskCompletionSource<string?>? PendingPick { get; set; }
    public Task<string?> PickFileAsync(CancellationToken ct) => PendingPick?.Task ?? Task.FromResult<string?>(null);
  }
  private sealed class Client(int pageCount = 2) : InferenceClientStub
  {
    public List<(string Session, string Path)> Saves { get; } = [];
    public List<string> Closed { get; } = [];
    public List<string> Cancelled { get; } = [];
    public TaskCompletionSource<PdfMutateResult>? PendingRotate { get; set; }
    public TaskCompletionSource RotateEntered { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override Task<PdfMutateResult> RotatePdfPagesAsync(string sessionId, int[] pages, int angle, CancellationToken ct)
    { RotateEntered.TrySetResult(); return PendingRotate?.Task ?? Task.FromResult(new PdfMutateResult(pageCount)); }
    public override Task CancelPdfAsync(string sessionId, CancellationToken ct) { Cancelled.Add(sessionId); return Task.CompletedTask; }
    public TaskCompletionSource<string>? PendingSave { get; set; }
    public TaskCompletionSource SaveEntered { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string? FailCopy { get; set; }
    public string? ThrowCopyAfterSubmit { get; set; }
    public bool FailClose { get; set; }
    public bool FailOpen { get; set; }
    public int Opens { get; private set; }
    public TaskCompletionSource<PdfSessionOpenResult>? PendingOpen { get; set; }
    public TaskCompletionSource OpenEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public PdfSessionOpenResult OpenResult(string path) => new(Guid.NewGuid().ToString("N"), pageCount, path,
      new Wire.PdfDocumentMirror { IsModified = true, Pages = Enumerable.Range(0, pageCount).Select(index => new Wire.PdfPageInfoMirror { PageIndex = index, Rect = [JsonSerializer.SerializeToElement(0), JsonSerializer.SerializeToElement(0), JsonSerializer.SerializeToElement(612), JsonSerializer.SerializeToElement(792)] }).ToArray() });
    public override Task<PdfSessionOpenResult> OpenPdfSessionAsync(string path, string? password, CancellationToken ct)
    {
      Opens++; OpenEntered.TrySetResult();
      if (FailOpen && Path.GetFileName(path) == "bad.pdf") throw new InferenceClientException(HttpV2ErrorCode.ValidationError, "synthetic bad PDF", false);
      return PendingOpen?.Task ?? Task.FromResult(OpenResult(path));
    }
    public override Task<Wire.PdfDocumentMirror> GetPdfModelAsync(string sessionId, CancellationToken ct) => Task.FromResult(new Wire.PdfDocumentMirror { IsModified = true, Pages = Enumerable.Range(0, pageCount).Select(index => new Wire.PdfPageInfoMirror { PageIndex = index, Rect = [JsonSerializer.SerializeToElement(0), JsonSerializer.SerializeToElement(0), JsonSerializer.SerializeToElement(612), JsonSerializer.SerializeToElement(792)] }).ToArray() });
    public override Task<byte[]> RenderPdfPageAsync(string sessionId, int page, int size, CancellationToken ct) => Task.FromResult(new byte[] { 1, 2, 3 });
    public override Task<string> SavePdfOperationAsync(string sessionId, string path, IReadOnlyDictionary<string, JsonElement> settings, bool copyExport, bool rebindTarget, bool overwrite, CancellationToken ct)
    {
      Saves.Add((sessionId, path)); SaveEntered.TrySetResult();
      if (copyExport && ThrowCopyAfterSubmit == sessionId)
      { File.WriteAllBytes(path, [1, 2, 3, 4]); throw new IOException("synthetic response lost after commit"); }
      if (copyExport && FailCopy == sessionId) throw new InferenceClientException(HttpV2ErrorCode.ValidationError, "synthetic target collision", false);
      return PendingSave?.Task ?? Task.FromResult(path);
    }
    public override Task ClosePdfSessionAsync(string sessionId, CancellationToken ct)
    { if (FailClose) throw new IOException("synthetic close failure"); Closed.Add(sessionId); return Task.CompletedTask; }
  }
  private sealed class Fixture : IDisposable
  {
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"vibeocr-t4-{Guid.NewGuid():N}");
    public string SaveTarget => Path.Combine(Root, "saved.pdf");
    public Client Client { get; }
    public WorkbenchResourceBroker Broker { get; }
    private WorkbenchAnnotationStore Annotations { get; }
    public PdfCloseDecision Decision { get; set; } = PdfCloseDecision.Cancel;
    public TaskCompletionSource<PdfCloseDecision>? DecisionPending { get; set; }
    public Source Source { get; } = new();
    public Fixture(int pages = 2)
    { Directory.CreateDirectory(Root); Client = new(pages); Broker = new(Root); Annotations = new(Root); }
    public DesktopWorkbenchCommandHandler Handler() => new(
      () => throw new NotSupportedException(), () => throw new NotSupportedException(), () => throw new NotSupportedException(),
      () => Model(Client, Source), () => throw new NotSupportedException(), () => throw new NotSupportedException(), () => throw new NotSupportedException(),
      new DiagnosticsViewModel("test", new PrerequisiteReport([])), Broker, Root, static () => 0, Annotations,
      confirmPdfClose: _ => DecisionPending?.Task ?? Task.FromResult(Decision), pickPdfSavePath: () => Task.FromResult<string?>(SaveTarget));
    public void Dispose() { Annotations.Dispose(); Broker.Dispose(); }
  }
}

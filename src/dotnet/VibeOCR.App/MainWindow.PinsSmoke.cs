using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using VibeOCR.App.Features.Pins;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace VibeOCR.App;

public sealed partial class MainWindow
{
  private static async Task WaitForPinCheckAsync(Func<Task<bool>> condition, string failure)
  {
    var elapsed = Stopwatch.StartNew();
    while (!await condition())
    {
      if (elapsed.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(failure);
      await Task.Delay(25);
    }
  }

  internal bool SmokeHistoryInitializing => pinHistoryStore?.Initialization.IsCompleted == false;
  internal Task SmokeHistoryInitialization => pinHistoryStore!.Initialization;

  internal async Task VerifyLoadingHistoryQueryAsync(Action release)
  {
    using var stop = new CancellationTokenSource();
    async Task<PinEditWorkbenchState> ObserveAsync()
    {
      await foreach (WorkbenchStateEnvelope state in application.SubscribeAsync(0, stop.Token,
        new HashSet<string> { "pinEdit" }))
        if (state.State is PinEditWorkbenchState { History.Count: > 0 } history) return history;
      throw new InvalidOperationException("History publication ended before initialization.");
    }
    Task<PinEditWorkbenchState> published = ObserveAsync();
    try
    {
      await application.ExecuteAsync(new(Guid.NewGuid(), new ListPinHistoryCommand("no-match-loading-query")), CancellationToken.None);
      release();
      PinEditWorkbenchState loaded = await published.WaitAsync(TimeSpan.FromSeconds(10));
      if (loaded.History!.Entries.Count != 0)
        throw new InvalidOperationException("History initialization cleared the already submitted query.");
      CaptureUiSmokeTrace.Stage("pins.loading-query-preserved");
    }
    finally { stop.Cancel(); }
  }

  internal async Task VerifyPinsSelfCheckAsync()
  {
    CaptureUiSmokeTrace.Stage("pins.main-ready");
    Features.Recognition.PinnedImageWindow.SmokeFilterEvents.Clear();
    Features.Recognition.PinnedImageWindow.SmokeFilterRecording = true;
    await WaitForPinCheckAsync(() => Task.FromResult(webHost.HasSubscription), "Main bridge was not ready.");
    if (commandHandler.InitialStates.OfType<PinEditWorkbenchState>().Single().History is not { Enabled: false })
      throw new InvalidOperationException("History must be available and disabled without an editing session.");
    await application.ExecuteAsync(new(Guid.NewGuid(), new NavigateWorkbenchCommand(WorkbenchRoute.ImageEdit)), CancellationToken.None);
    await WaitForPinCheckAsync(async () => await WorkbenchWebView.ExecuteScriptAsync(
      "!!document.querySelector('[aria-label=\"截图历史\"]')") == "true", "Main history entry was absent.");

    using var encoded = new InMemoryRandomAccessStream();
    BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encoded);
    byte[] pixels = new byte[64 * 32 * 4];
    for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 50; pixels[i + 1] = 140; pixels[i + 2] = 220; pixels[i + 3] = 255; }
    // Two real raster H glyphs: one in the initially excluded left quarter, one visible.
    foreach (int startX in new[] { 4, 44 })
      for (int y = 6; y < 25; y++)
        for (int x = startX; x < startX + 10; x++)
          if (x < startX + 2 || x >= startX + 8 || y is >= 14 and < 17)
          { int at = (y * 64 + x) * 4; pixels[at] = pixels[at + 1] = pixels[at + 2] = 0; }
    encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, 64, 32, 96, 96, pixels);
    await encoder.FlushAsync();
    encoded.Seek(0);
    using var reader = new DataReader(encoded.GetInputStreamAt(0));
    await reader.LoadAsync(checked((uint)encoded.Size));
    byte[] png = new byte[checked((int)encoded.Size)];
    reader.ReadBytes(png);
    string initialImage = Path.Combine(resourceRoot, "pins-smoke-source.png");
    await File.WriteAllBytesAsync(initialImage, png);
    await application.ExecuteAsync(new(Guid.NewGuid(), new OpenDroppedImageEditFileCommand(initialImage)), CancellationToken.None);
    await WaitForPinCheckAsync(async () => await WorkbenchWebView.ExecuteScriptAsync(
      "document.body.textContent.includes('原图 64×32') && !document.querySelector('button[aria-label=\"贴图\"]')?.disabled") == "true",
      "Main image editor did not load the source.");
    await WorkbenchWebView.ExecuteScriptAsync("document.querySelector('button[aria-label=\"屏蔽\"]').click()");
    await WorkbenchWebView.ExecuteScriptAsync("""
      (()=>{const c=document.querySelector('canvas[aria-label="图片检查画布"]');const r=c.getBoundingClientRect();
      const e=(type,x,y,buttons)=>c.dispatchEvent(new PointerEvent(type,{bubbles:true,pointerId:1,pointerType:'mouse',isPrimary:true,button:0,buttons,clientX:r.left+r.width*x/900,clientY:r.top+r.height*y/600}));
      e('pointerdown',1,76,1);e('pointermove',224,524,1);e('pointerup',224,524,0);
      const select=document.querySelector('select[aria-label="输出格式"]');select.value='image/jpeg';select.dispatchEvent(new Event('change',{bubbles:true}));})()
      """);
    string pinRoot = PinSnapshotStore.RootFor(layout.DataRoot);
    int windowCount = CountSmokeAppWindows();
    File.WriteAllText(pinRoot, "owned persistence blocker");
    try
    {
      for (int attempt = 0; attempt < 2; attempt++)
      {
        await WorkbenchWebView.ExecuteScriptAsync("document.querySelector('button[aria-label=\"贴图\"]').click()");
        await WaitForPinCheckAsync(async () => await WorkbenchWebView.ExecuteScriptAsync(
          "document.body.textContent.includes('贴图未创建') && !document.querySelector('button[aria-label=\"贴图\"]')?.disabled") == "true",
          "Failed first pin did not return a recoverable receipt.");
        await WaitForPinCheckAsync(() => Task.FromResult(CountSmokeAppWindows() == windowCount), "Failed pin leaked an unregistered native window.");
        if (pinnedImages.Count != 0) throw new InvalidOperationException("Failed first pin was registered.");
      }
    }
    finally { File.Delete(pinRoot); }
    await WorkbenchWebView.ExecuteScriptAsync("document.querySelector('button[aria-label=\"贴图\"]').click()");
    await WaitForPinCheckAsync(() => Task.FromResult(pinnedImages.Count == 1), "JPEG selection prevented first pin creation.");
    if (pinSnapshotStore!.ReadSnapshotPng(pinnedImages[0].PinId) is not { Width: 64, Height: 32 })
      throw new InvalidOperationException("First pin was not a decodable owned PNG.");
    if (await WorkbenchWebView.ExecuteScriptAsync("document.querySelector('select[aria-label=\"输出格式\"]').value === 'image/jpeg'") != "true")
      throw new InvalidOperationException("First pin changed the ordinary output format.");
    WorkbenchAnnotationLease secondLease = await annotationStore.UploadPngAsync(new MemoryStream(png), CancellationToken.None);
    PinScreenshot(annotationStore.Take(secondLease.ResourceUri), Guid.NewGuid(), 0, null, []);
    PinnedImageWindow first = pinnedImages[0], second = pinnedImages[1];
    await WaitForPinCheckAsync(() => Task.FromResult(first.SmokeDocumentReady && second.SmokeDocumentReady), "Static pins not ready.");
    var observedScopes = new List<string>();
    commandHandler.StateChanged += state => observedScopes.Add(state.Scope);
    try
    {
      byte[] beforeSetupFailure = await File.ReadAllBytesAsync(first.CurrentImagePath);
      first.SmokeEditorSetupStep = () => throw new IOException("smoke editor setup before host initialization");
      try { await EnterPinEditAsync(first); }
      catch (IOException) { }
      byte[] afterSetupFailure = await File.ReadAllBytesAsync(first.CurrentImagePath);
      if (pinEditing is not null || first.Editing || first.SmokeHasEditorOverlay ||
        commandHandler.InitialStates.OfType<PinEditWorkbenchState>().Single().ScreenshotSession is not null ||
        !beforeSetupFailure.SequenceEqual(afterSetupFailure))
        throw new InvalidOperationException("Failed editor setup retained its native slot/UI or changed the image.");
      await EnterPinEditAsync(second);
      await WaitForPinEditorAsync(second);
      await ExitPinEditAsync(second);
      await WaitForPinCheckAsync(() => Task.FromResult(!second.Editing), "Other pin could not leave its editor after setup failure.");
      CaptureUiSmokeTrace.Stage("pins.editor-setup-rollback");
      CaptureUiSmokeTrace.Stage("pins.first-edit");
      var oldTextIdentity = (first.SessionId, first.Revision);
      WorkbenchAnnotationLease sharedLease = await annotationStore.UploadPngAsync(new MemoryStream(png), CancellationToken.None);
      PinScreenshot(annotationStore.Take(sharedLease.ResourceUri), first.SessionId, first.Revision, null, first.CurrentExcludeBoxes);
      PinnedImageWindow shared = pinnedImages.Last();
      await VerifySmokePinTextAsync(first);
      await VerifySmokePinTextAsync(shared);
      if (pinTextTasks.Count != 1) throw new InvalidOperationException("Shared pin text was not cached once.");
      first.SmokeSnapshotDisplayStep = () => Task.FromException(new IOException("Injected static display swap failure."));
      await EnterPinEditAsync(first);
      await WaitForPinEditorAsync(first);
      PinEditSlot failedDisplaySlot = pinEditing!;
      await RotateSmokePinAsync(first);
      CaptureUiSmokeTrace.Stage("pins.switch-save");
      await EnterPinEditAsync(second);
      await WaitForPinEditorAsync(second);
      if (first.Editing || !second.Editing || pinSnapshotStore!.GetSnapshotProjectPath(first.PinId) is null ||
        first.SmokeImageSize != (32, 64)) throw new InvalidOperationException($"Switch did not commit: firstEditing={first.Editing}, secondEditing={second.Editing}, size={first.SmokeImageSize}, project={pinSnapshotStore!.GetSnapshotProjectPath(first.PinId)}, body={await first.SmokeEditorEvaluateAsync("document.body.textContent")}");

      if (failedDisplaySlot.Swap?.IsFaulted == true || !first.SmokeStatus.Contains("显示更新失败") || !pinTextTasks.ContainsKey(oldTextIdentity))
        throw new InvalidOperationException("Committed display failure lost the slot, saved image, or shared OCR task.");
      shared.ForceClose();
      if (pinTextTasks.ContainsKey(oldTextIdentity)) throw new InvalidOperationException("Last shared pin did not release old OCR task.");
      await RotateSmokePinAsync(second);
      string blocker = Path.Combine(PinSnapshotStore.RootFor(layout.DataRoot), second.PinId.ToString("N"), "current.json");
      Directory.CreateDirectory(blocker);
      CaptureUiSmokeTrace.Stage("pins.failed-save-retains-editor");
      await EnterPinEditAsync(first);
      if (!second.Editing || first.Editing || second.SmokeImageSize != (64, 32))
        throw new InvalidOperationException("Failed save discarded the editor or replaced its static image.");
      Directory.Delete(blocker);
      await EnterPinEditAsync(first);
      await WaitForPinEditorAsync(first);
      await WaitForPinCheckAsync(async () => (await first.SmokeEditorEvaluateAsync(
        "document.querySelector('.canvas-operation-message')?.textContent?.includes('工程已恢复') ?? false")) == "true" ||
        (await first.SmokeEditorEvaluateAsync("document.body.textContent.includes('工程已恢复')")) == "true",
        "Saved project did not restore its editable state.");
      CaptureUiSmokeTrace.Stage("pins.restore-editable-project");
      await RotateSmokePinAsync(first);
      if (!await first.SaveEditAsync()) throw new InvalidOperationException("Restored project could not save.");
      if (first.SmokeImageSize != (64, 32)) throw new InvalidOperationException("Restored rotation was lost or flattened.");
      for (int cycle = 0; cycle < 3; cycle++)
      {
        await VerifySmokePinTextAsync(first);
        if (pinTextTasks.Count != 1) throw new InvalidOperationException("OCR cache accumulated before rebind.");
        await EnterPinEditAsync(first); await WaitForPinEditorAsync(first);
        await RotateSmokePinAsync(first); await RotateSmokePinAsync(first);
        if (!await first.SaveEditAsync() || pinEditing is not null || pinTextTasks.Count != 0)
          throw new InvalidOperationException("Repeated text/edit/save leaked its old OCR task or editor slot.");
      }
      await VerifySmokePinTextAsync(first);

      CaptureUiSmokeTrace.Stage("pins.button-commit-unmounted-native-switch");
      await EnterPinEditAsync(first);
      await WaitForPinEditorAsync(first);
      await RotateSmokePinAsync(first);
      var buttonReceiptEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var releaseButtonReceipt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      first.SmokeDelaySnapshotReceipt(() => { buttonReceiptEntered.TrySetResult(); return releaseButtonReceipt.Task; });
      int buttonAppliedBefore = SmokePinSnapshotAppliedCount;
      try
      {
        if (await first.SmokeEditorEvaluateAsync(
          "(()=>{const b=Array.from(document.querySelectorAll('button')).find(b=>b.textContent==='完成贴图');if(!b||b.disabled)return false;b.click();return true})()") != "true")
          throw new InvalidOperationException("Real pin completion button was unavailable.");
        await buttonReceiptEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForPinCheckAsync(async () => await first.SmokeEditorEvaluateAsync(
          "!document.querySelector('.canvas-editor[data-screenshot-session]') && document.body.textContent.includes('等待图片')") == "true",
          "Successful button commit did not unmount its editor before the receipt.");
        Task switching = EnterPinEditAsync(second);
        releaseButtonReceipt.TrySetResult();
        try { await switching.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException error)
        {
          throw new InvalidOperationException("Native switch waited for a new request after the button commit unmounted its listener.", error);
        }
        if (first.Editing || pinEditing?.Window != second || !second.Editing ||
          SmokePinSnapshotAppliedCount - buttonAppliedBefore != 1)
          throw new InvalidOperationException("Joining a committed button save failed to switch exactly once.");
      }
      finally { releaseButtonReceipt.TrySetResult(); }
      await ExitPinEditAsync(second);
      await WaitForPinCheckAsync(() => Task.FromResult(!second.Editing), "Button switch target did not discard.");

      CaptureUiSmokeTrace.Stage("pins.idle-recovery-normal-switch");
      await EnterPinEditAsync(first);
      await WaitForPinEditorAsync(first);
      await first.SmokeResetIdleEditorAsync();
      await WaitForPinEditorAsync(first);
      PinEditSlot recoveredIdleSlot = pinEditing!;
      bool swapFinishedBeforeReceipt = false;
      first.SmokeDelaySnapshotReceipt(async () =>
      {
        await WaitForPinCheckAsync(() => Task.FromResult(recoveredIdleSlot.Swap is not null),
          "Fresh save did not start its committed display swap.");
        await recoveredIdleSlot.Swap!;
        swapFinishedBeforeReceipt = true;
      });
      await RotateSmokePinAsync(first);
      await EnterPinEditAsync(second);
      if (!swapFinishedBeforeReceipt || first.Editing || pinEditing?.Window != second || !second.Editing)
        throw new InvalidOperationException("Idle renderer recovery made a later successful save fail or blocked switching pins.");
      await ExitPinEditAsync(second);
      await WaitForPinCheckAsync(() => Task.FromResult(!second.Editing), "Switch target did not discard.");

      // A13-02：提交后 renderer 重置且回执丢失：已落盘提交为权威结果，槽幂等
      // 收尾不重做保存；同窗重开新代后旧回调不得拆新编辑器；再开新贴图后旧会
      // 迟到确认不得清除新槽；单次提交由真实提交事件计数验证。
      CaptureUiSmokeTrace.Stage("pins.committed-receipt-loss");
      int appliedBefore = SmokePinSnapshotAppliedCount;
      SmokeCommittedResetProbe = win => win.SmokeResetEditorDocument();
      await EnterPinEditAsync(first);
      await WaitForPinEditorAsync(first);
      Func<Task> oldConfirmed = first.SmokeCaptureSnapshotConfirmation();
      await RotateSmokePinAsync(first);
      await first.SaveEditAsync(); // 回执丢失下返回值不可靠；权威提交已落盘
      SmokeCommittedResetProbe = null;
      await WaitForPinCheckAsync(() => Task.FromResult(!first.Editing && pinEditing is null),
        "Committed pin with lost receipt left the edit slot open.");
      if (SmokePinSnapshotAppliedCount - appliedBefore != 1)
        throw new InvalidOperationException($"Committed pin was written {SmokePinSnapshotAppliedCount - appliedBefore} times instead of once.");
      if (pinSnapshotStore!.GetSnapshotPngPath(first.PinId) is null)
        throw new InvalidOperationException("Committed snapshot was not persisted.");
      // 同窗重开新代：旧窗口迟到确认回调不得拆除新编辑器。
      await EnterPinEditAsync(first);
      await WaitForPinEditorAsync(first);
      if (pinEditing is not { } reentered || reentered.Window != first)
        throw new InvalidOperationException("Same-window re-entry did not open a new slot.");
      await oldConfirmed();
      if (!first.Editing || pinEditing is not { } intact || !ReferenceEquals(intact, reentered))
        throw new InvalidOperationException("Late same-window confirmation tore down the new edit generation.");
      var displayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var releaseDisplay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      first.SmokeSnapshotDisplayStep = () => { displayEntered.TrySetResult(); return releaseDisplay.Task; };
      await RotateSmokePinAsync(first);
      Task<bool> newSave = first.SaveEditAsync();
      await displayEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
      try
      {
        Task lateOldConfirmation = oldConfirmed();
        if (!lateOldConfirmation.IsCompleted)
          throw new InvalidOperationException("Old host acknowledgement waited for the new generation's committed display swap.");
        await lateOldConfirmation;
        if (!first.Editing || !ReferenceEquals(pinEditing, reentered))
          throw new InvalidOperationException("Old host acknowledgement ended the new committed generation.");
      }
      finally { releaseDisplay.TrySetResult(); }
      if (!await newSave) throw new InvalidOperationException("Current generation could not finish after a stale callback.");
      await EnterPinEditAsync(first);
      await WaitForPinEditorAsync(first);
      // Native apply 已验证贡献且即将进入真实 writer：在安全 await 卡住后重置旧文档。
      await RotateSmokePinAsync(first);
      var applyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      var releaseCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      VibpProjectManifest? acceptedManifest = null;
      byte[]? acceptedPng = null;
      int preparedCommits = 0;
      commandHandler.Image.SmokeBeforePinSnapshotCommit = (manifest, finalPng) =>
      {
        preparedCommits++;
        acceptedManifest = manifest;
        acceptedPng = finalPng.ToArray();
        applyEntered.TrySetResult();
        return releaseCommit.Task;
      };
      appliedBefore = SmokePinSnapshotAppliedCount;
      Task<bool> resetSave = first.SaveEditAsync();
      try
      {
        await applyEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (SmokePinSnapshotAppliedCount != appliedBefore)
          throw new InvalidOperationException("Native save committed before the controlled renderer reset.");
        first.SmokeResetEditorDocument();
      }
      finally { releaseCommit.TrySetResult(); commandHandler.Image.SmokeBeforePinSnapshotCommit = null; }
      await resetSave;
      await WaitForPinCheckAsync(() => Task.FromResult(!first.Editing && pinEditing is null),
        "Committed pin after an in-flight renderer reset retained its edit slot.");
      if (preparedCommits != 1 || SmokePinSnapshotAppliedCount - appliedBefore != 1 || acceptedManifest is null)
        throw new InvalidOperationException("In-flight renderer reset did not produce exactly one real native commit.");
      using (var archive = System.IO.Compression.ZipFile.OpenRead(pinSnapshotStore.GetSnapshotProjectPath(first.PinId)!))
      using (Stream savedManifest = archive.GetEntry(VibpProjectLimits.ManifestEntryName)!.Open())
      {
        VibpProjectManifest saved = JsonSerializer.Deserialize<VibpProjectManifest>(savedManifest, VibpProjectRules.ManifestJson)!;
        if (JsonSerializer.Serialize(saved.State, VibpProjectRules.ManifestJson) != JsonSerializer.Serialize(acceptedManifest.State, VibpProjectRules.ManifestJson) ||
          pinSnapshotStore.ReadSnapshotPng(first.PinId) is not { } committedImage ||
          (committedImage.Width, committedImage.Height) != first.SmokeImageSize ||
          acceptedPng is null || !committedImage.Png.SequenceEqual(acceptedPng))
          throw new InvalidOperationException("In-flight reset changed the committed project or its final image.");
      }
      if (!await first.PrepareEditExitAsync() || !await PreparePinsExitAsync())
        throw new InvalidOperationException("Recovered commit blocked window close or application exit.");
      pinsExitReview = false;
      CaptureUiSmokeTrace.Stage("pins.native-inflight-reset-committed-once");
      // 新贴图新槽：旧会话迟到确认不得清除新槽。
      await EnterPinEditAsync(second);
      await WaitForPinEditorAsync(second);
      await oldConfirmed();
      if (pinEditing?.Window != second)
        throw new InvalidOperationException("Late old-session confirmation cleared the new edit slot.");
      await ExitPinEditAsync(second);
      await WaitForPinCheckAsync(() => Task.FromResult(!second.Editing), "New slot did not exit after discard.");
      await VerifySmokePinTextAsync(first);
      if (observedScopes.Any(scope => scope is "recognition" or "imageEdit"))
        throw new InvalidOperationException("Pin editing polluted a main image channel.");

      TogglePinGroup(first); TogglePinGroup(second);
      var firstBefore = first.SmokePosition; var secondBefore = second.SmokePosition;
      MovePinGroup(first, 30, -20);
      if (first.SmokePosition != firstBefore || second.SmokePosition.X != secondBefore.X + 30 ||
        second.SmokePosition.Y != secondBefore.Y - 20) throw new InvalidOperationException("Group movement did not use physical deltas.");
      first.SmokeInvokeMaximize();
      first.SetPassThrough(true);
      ShowPinManagerFromTray();
      if (pinManager is null) throw new InvalidOperationException("Tray did not open the manager.");
      for (nint above = GetPinSmokeWindow(pinManager.Handle, 3); above != 0; above = GetPinSmokeWindow(above, 3))
        if (above == first.Handle) throw new InvalidOperationException("Pass-through pin covers its recovery manager in native z-order.");
      if (!pinManager.SmokeInvokeRestoreClickability(first.PinId))
        throw new InvalidOperationException("Manager restore action was unavailable.");
      await WaitForPinCheckAsync(() => Task.FromResult(!first.PassThrough &&
        ((long)PaddleSmokeNative.GetWindowLongPtr(first.Handle, -20) & 0x20) == 0), "Manager did not restore native clickability.");
      first.SmokeInvokeMaximize();
      CaptureUiSmokeTrace.Stage("pins.manager-above-pass-through");
      first.SetCollapsed(true); first.SetCollapsed(false);

      CaptureUiSmokeTrace.Stage("pins.formula-render");
      var formula = CreateTypedPin(TypedPinWindow.ContentKind.Formula);
      formula.SetText("\\frac{x^2+1}{2}");
      SmokeFailFormulaInitialization = true;
      await formula.RenderFormulaAsync();
      if (SmokeFailedFormulaHost is not { IsDisposed: true } || formula.HasFormulaImage ||
        !formula.RecoveryState().Content.Contains("x^2+1"))
        throw new InvalidOperationException("Failed formula initialization leaked its host or lost its source.");
      CaptureUiSmokeTrace.Stage("pins.formula-initialization-rollback");
      bool failedHostNavigated = false;
      SmokeFailedFormulaHost.StateChanged += state =>
      {
        if (state.StartsWith("navigation-", StringComparison.Ordinal)) failedHostNavigated = true;
      };
      // 真实并发/迟到回归（闸门注入）：挂起创建 + 第二次调用只创建一次，
      // 挂起期间修改源文本 → 迟到结果不套用且无旧内容展示，随后重试成功。
      SmokeFormulaHostGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
      Task suspendedRender = formula.RenderFormulaAsync();
      await WaitForPinCheckAsync(() => Task.FromResult(SmokeFormulaHosts.Count == 1), "Formula host creation did not start.");
      await formula.RenderFormulaAsync();
      if (SmokeFormulaHosts.Count != 1) throw new InvalidOperationException("Concurrent render created a second formula host.");
      formula.SetText("\\frac{x^2+5}{9}");
      SmokeFormulaHostGate.TrySetResult();
      await suspendedRender;
      if (formula.HasFormulaImage) throw new InvalidOperationException("Stale formula render installed an old image.");
      if (!formula.StatusText.Contains("未套用")) throw new InvalidOperationException($"Stale render did not surface discard guidance: {formula.StatusText}");
      // 重试（无闸门，复用同一宿主）：新源文本渲染成功且源保留。
      await formula.RenderFormulaAsync();
      if (!formula.HasFormulaImage || !formula.RecoveryState().Content.Contains("x^2+5"))
        throw new InvalidOperationException($"Formula retry after discard failed: {formula.StatusText}");
      if (SmokeFormulaHosts.Count != 1) throw new InvalidOperationException("Formula retry created another host instead of reusing.");
      if (failedHostNavigated) throw new InvalidOperationException("Failed formula host retained a native navigation handler.");
      formula.SetText("\\unknownCommand{");
      await formula.RenderFormulaAsync();
      if (formula.HasFormulaImage || !formula.RecoveryState().Content.Contains("unknownCommand"))
        throw new InvalidOperationException("Failed formula inserted an image or lost its source.");
      // 失败后重试成功：换新有效公式重新渲染，图片恢复且新源保留。
      formula.SetText("\\sqrt{a^2+b^2}");
      await formula.RenderFormulaAsync();
      if (!formula.HasFormulaImage || !formula.RecoveryState().Content.Contains("sqrt"))
        throw new InvalidOperationException($"Formula retry after failure did not recover: {formula.StatusText}");
      formula.ForceClose();
      // 创建期间关闭：迟到宿主必须释放，不得接管字段泄漏。
      var closing = CreateTypedPin(TypedPinWindow.ContentKind.Formula);
      SmokeFormulaHostGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
      Task lateRender = closing.RenderFormulaAsync();
      await WaitForPinCheckAsync(() => Task.FromResult(SmokeFormulaHosts.Count == 2), "Late formula host creation did not start.");
      closing.ForceClose();
      SmokeFormulaHostGate.TrySetResult();
      await lateRender;
      if (!SmokeFormulaHosts[1].IsDisposed) throw new InvalidOperationException("Late formula host after close was leaked.");
      SmokeFormulaHostGate = null;

      CaptureUiSmokeTrace.Stage("pins.close-cancel-discard");
      if (!SmokeRecoveryInjectionConsumed) throw new InvalidOperationException("Injected recovery root failure never ran at startup.");
      await EnterPinEditAsync(second);
      await WaitForPinEditorAsync(second);
      await RotateSmokePinAsync(second);
      second.Close();
      await WaitForPinCheckAsync(() => Task.FromResult(second.SmokeInvokeEditDecision("取消")), "Close decision was absent.");
      if (second.Disposed || !second.Editing) throw new InvalidOperationException("Cancel closed a dirty pin.");
      await WaitForPinCheckAsync(() => Task.FromResult(!second.SmokeEditDecisionPending), "Cancel decision did not finish.");
      second.Close();
      await WaitForPinCheckAsync(() => Task.FromResult(second.SmokeInvokeEditDecision("放弃更改")), "Discard decision was absent.");
      await WaitForPinCheckAsync(() => Task.FromResult(second.Disposed), "Discard did not close the pin.");
      if (pinSnapshotStore!.GetSnapshotPngPath(second.PinId) is not null)
        throw new InvalidOperationException("Closing a single pin did not delete its owned recovery record.");

      CaptureUiSmokeTrace.Stage("pins.exit-recovery");
      var expanded = first.SmokeClientSize;
      first.SetCollapsed(true);
      if (!await PreparePinsExitAsync()) throw new InvalidOperationException("Exit recovery could not persist pins.");
      var recovered = pinSnapshotStore.LoadRecovery();
      if (recovered.Count != 1 || !recovered.Any(item => item.Id == first.PinId && item.X == first.SmokePosition.X))
        throw new InvalidOperationException("Pin recovery did not retain content and physical position.");
      // Simulate the same shutdown disposal and fresh recovery path, preserving owned records.
      shuttingDown = true;
      first.ForceClose();
      shuttingDown = false;
      RestorePins();
      first = pinnedImages.Single();
      await WaitForPinCheckAsync(() => Task.FromResult(first.SmokeDocumentReady), "Recovered static pin not ready.");
      if (!first.Collapsed) throw new InvalidOperationException("Recovery lost the collapsed state.");
      first.SetCollapsed(false);
      if (first.SmokeClientSize.Width != expanded.Width || first.SmokeClientSize.Height != expanded.Height)
        throw new InvalidOperationException($"Recovery lost expanded size: expected={expanded.Width}x{expanded.Height}, actual={first.SmokeClientSize.Width}x{first.SmokeClientSize.Height}, record={JsonSerializer.Serialize(recovered.Single())}");
      await VerifySmokePinTextAsync(first);

      // 启动显示瞬时失败≠用户关闭：失败窗口释放但恢复快照保留，重试恢复成功。
      CaptureUiSmokeTrace.Stage("pins.show-failure-retain");
      shuttingDown = true;
      first.ForceClose();
      shuttingDown = false;
      Features.Recognition.PinnedImageWindow.SmokeFailNextShow = true;
      RestorePins();
      await WaitForPinCheckAsync(() => Task.FromResult(pinnedImages.Count == 0), "Failed show window was not released.");
      if (pinSnapshotStore!.LoadRecovery().Count != 1)
        throw new InvalidOperationException("Startup show failure deleted the recovery snapshot.");
      RestorePins();
      first = pinnedImages.Single();
      await WaitForPinCheckAsync(() => Task.FromResult(first.SmokeDocumentReady), "Retry after startup failure did not restore.");
      await VerifySmokePinTextAsync(first);

      // 拓扑变更回归：自身窗口的屏外恢复记录，用当前工作区验证 image 与
      // typed 贴图至少顶部可拖 band 可见，且位置不再停在屏外记录上。
      CaptureUiSmokeTrace.Stage("pins.offscreen-recovery");
      var typed = CreateTypedPin(Features.Pins.TypedPinWindow.ContentKind.Text);
      typed.SetText("offscreen-restore");
      var workAreas = Features.Pins.PinSnapshotStore.CurrentWorkAreas();
      int offscreenX = checked((int)(workAreas.Max(area => (long)area.X + area.Width) + 4000));
      int offscreenY = checked((int)(workAreas.Max(area => (long)area.Y + area.Height) + 4000));
      pinSnapshotStore!.SaveRecovery(first.RecoveryState() with { X = offscreenX, Y = offscreenY });
      pinSnapshotStore.SaveRecovery(typed.RecoveryState() with { X = offscreenX, Y = offscreenY });
      shuttingDown = true;
      first.ForceClose();
      typed.ForceClose();
      shuttingDown = false;
      RestorePins();
      var recoveredImage = pinnedImages.Single(pin => pin.RecoveryState().X != offscreenX);
      var recoveredTyped = typedPins.Single(pin => pin.RecoveryState().X != offscreenX);
      if (!BandVisibleOnCurrentDesktop(recoveredImage.SmokePosition.X, recoveredImage.SmokePosition.Y,
          recoveredImage.RecoveryState().Width) ||
        !BandVisibleOnCurrentDesktop(recoveredTyped.SmokePosition.X, recoveredTyped.SmokePosition.Y,
          recoveredTyped.RecoveryState().Width))
        throw new InvalidOperationException($"Offscreen recovery did not keep a draggable band visible: image=({recoveredImage.SmokePosition.X},{recoveredImage.SmokePosition.Y}), typed=({recoveredTyped.SmokePosition.X},{recoveredTyped.SmokePosition.Y}), areas={string.Join(";", Features.Pins.PinSnapshotStore.CurrentWorkAreas())}");
      first = recoveredImage;
      first.Close(); second.Close(); recoveredTyped.ForceClose();
      if (pinSnapshotStore.LoadRecovery().Count != 0) throw new InvalidOperationException("Closing a pin kept its owned recovery entry.");

      // 图片过滤注册真实调用配对（按 WebView2 实例分组）：每实例每 URI
      // add−remove ≤1（多次换装不累积），移除的 URI 必曾被注册。
      foreach (var group in Features.Recognition.PinnedImageWindow.SmokeFilterEvents
        .GroupBy(item => item.Instance))
      {
        Dictionary<string, int> adds = [];
        Dictionary<string, int> removes = [];
        foreach ((_, string uri, bool added) in group)
        {
          if (added) adds[uri] = adds.GetValueOrDefault(uri) + 1;
          else removes[uri] = removes.GetValueOrDefault(uri) + 1;
        }
        if (adds.Keys.Concat(removes.Keys).Any(uri =>
            adds.GetValueOrDefault(uri) - removes.GetValueOrDefault(uri) > 1) ||
          removes.Keys.Any(uri => adds.GetValueOrDefault(uri) == 0))
          throw new InvalidOperationException($"Image filter registrations accumulated or removed an unregistered URI (instance {group.Key}): {string.Join("; ", group.Select(item => $"{(item.Added ? "add" : "remove")}({item.Uri})"))}");
      }
    }
    finally
    {
      if (pinEditing is { } slot) await ExitPinEditAsync(slot.Window);
      foreach (var pin in pinnedImages.ToArray()) pin.ForceClose();
      foreach (var pin in typedPins.ToArray()) pin.ForceClose();
      pinManager?.ForceClose();
    }
  }

  private static bool BandVisibleOnCurrentDesktop(int x, int y, int width) =>
    Features.Pins.PinSnapshotStore.CurrentWorkAreas().Any(area =>
      Math.Min((long)x + width, (long)area.X + area.Width) - Math.Max(x, area.X) >= 16 &&
      Math.Min((long)y + 36, (long)area.Y + area.Height) - Math.Max(y, area.Y) >= 16);

  private static async Task VerifySmokePinTextAsync(PinnedImageWindow pin)
  {
    if (pin.CurrentExcludeBoxes.Count == 0) throw new InvalidOperationException("Pin lost its OCR exclusions.");
    await pin.SmokePrepareTextAsync();
    if (pin.SmokeLayerTexts.Count != 1 || pin.SmokeLayerTexts[0] != "visible")
      throw new InvalidOperationException("Pin text layer resurrected an excluded line.");
  }

  internal async Task<RecognitionTextLayerState?> VerifyMaskedPinOcrInputAsync(
    Guid session, long revision, string input, CancellationToken token)
  {
    PinnedImageWindow pin = pinnedImages.First(item => item.SessionId == session);
    if (input == pin.CurrentImagePath) throw new InvalidOperationException("OCR received the ordinary unmasked pin.");
    using var memory = new MemoryStream(await File.ReadAllBytesAsync(input, token));
    using var random = memory.AsRandomAccessStream();
    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(random);
    byte[] pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
      new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
    WorkbenchExclusionBox box = pin.CurrentExcludeBoxes[0];
    double centerX = box.X + box.Width / 2, centerY = box.Y + box.Height / 2;
    int x = (int)(centerX / 1000 * decoder.PixelWidth), y = (int)(centerY / 1000 * decoder.PixelHeight);
    int offset = (y * (int)decoder.PixelWidth + x) * 4;
    if (!pixels.AsSpan(offset, 4).SequenceEqual(new byte[] { 255, 255, 255, 255 }))
      throw new InvalidOperationException("Excluded glyph pixels reached OCR.");
    using var ordinary = new MemoryStream(await File.ReadAllBytesAsync(pin.CurrentImagePath, token));
    using var ordinaryRandom = ordinary.AsRandomAccessStream();
    BitmapDecoder ordinaryDecoder = await BitmapDecoder.CreateAsync(ordinaryRandom);
    byte[] ordinaryPixels = (await ordinaryDecoder.GetPixelDataAsync()).DetachPixelData();
    if (ordinaryPixels.AsSpan(offset, 4).SequenceEqual(new byte[] { 255, 255, 255, 255 }))
      throw new InvalidOperationException("OCR masking modified the ordinary display/copy/save image.");
    double safeX = centerX < 500 ? 800 : 100, safeY = centerY < 500 ? 800 : 100;
    return new RecognitionTextLayerState("textlayer.ready", null, new(session.ToString("N"), revision),
      "rapid_text", "pins-smoke", null,
      [new("hidden", centerX - 1, centerY - 1, centerX + 1, centerY + 1),
       new("visible", safeX, safeY, safeX + 5, safeY + 5)]);
  }

  private int CountSmokeAppWindows()
  {
    int count = 0;
    string windowClass = PaddleSmokeNative.GetWindowClassName(WinRT.Interop.WindowNative.GetWindowHandle(this));
    var probe = new PaddleSmokeNative.EnumWindowsProc((hwnd, _) =>
    {
      PaddleSmokeNative.GetWindowThreadProcessId(hwnd, out uint processId);
      if (processId == Environment.ProcessId && PaddleSmokeNative.GetWindowClassName(hwnd) == windowClass) count++;
      return true;
    });
    PaddleSmokeNative.EnumWindows(probe, nint.Zero);
    GC.KeepAlive(probe);
    return count;
  }

  [DllImport("user32.dll", EntryPoint = "GetWindow")]
  private static extern nint GetPinSmokeWindow(nint window, uint command);

  private static Task WaitForPinEditorAsync(PinnedImageWindow pin) => WaitForPinCheckAsync(
    async () => (await pin.SmokeEditorEvaluateAsync(
      "!!document.querySelector('.canvas-editor[data-screenshot-session]') && !!document.querySelector('canvas[aria-label=\"图片检查画布\"]') && /原图 [0-9]+×[0-9]+/.test(document.body.textContent) && !Array.from(document.querySelectorAll('button')).find(b=>b.getAttribute('aria-label')==='旋转 90°')?.disabled")) == "true",
    "Pin editor did not decode its input.");

  private static async Task RotateSmokePinAsync(PinnedImageWindow pin)
  {
    string result = await pin.SmokeEditorEvaluateAsync(
      "(()=>{const b=Array.from(document.querySelectorAll('button')).find(b=>b.getAttribute('aria-label')==='旋转 90°');if(!b||b.disabled)return false;b.click();return true})()");
    if (result != "true") throw new InvalidOperationException("Pin rotation entry is unavailable.");
    await Task.Delay(50);
  }
}

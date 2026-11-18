using System.Diagnostics;
using System.Text.Json;
using VibeOCR.App.Inference;
using VibeOCR.App.Workbench;
using VibeOCR.Contracts.HttpV2;
using VibeOCR.Platform.Inference;

namespace VibeOCR.App;

internal static class PdfPipelinePhaseStats
{
  internal static PdfSmokeRequestProbe.RequestEvent[] Window(
    IReadOnlyList<PdfSmokeRequestProbe.RequestEvent> events, double start, double end) =>
    events.Where(request => request.StartedAtMs >= start && request.StartedAtMs <= end).ToArray();

  internal static PdfSmokeRequestProbe.RequestEvent[] BusinessRequests(
    IReadOnlyList<PdfSmokeRequestProbe.RequestEvent> window, string session)
  {
    var jobs = window.Where(request => request.Kind == "job_submit" && request.Detail is not null)
      .Select(request => request.Detail!).ToHashSet(StringComparer.Ordinal);
    return window.Where(request =>
      request.Kind == "job_submit" ||
      request.Kind == "job_observe" && jobs.Contains(request.Session) ||
      request.Session == session && (request.Purpose is "business_presence" or "business_render" ||
        request.Kind == "add_text_layers")).ToArray();
  }

  internal static bool CoversPages(IEnumerable<int> actual, IReadOnlyList<int> expected) =>
    actual.Order().SequenceEqual(expected);

  internal static object ByKind(IEnumerable<PdfSmokeRequestProbe.RequestEvent> requests) =>
    requests.GroupBy(request => request.Kind).ToDictionary(group => group.Key, group =>
    {
      var completed = group.Where(request => request.Outcome == "completed").ToArray();
      var polls = completed.Select(request => request.Polls).OfType<PdfSmokeRequestProbe.PollStats>().ToArray();
      double[] durations = completed.Where(request => request.DurationMs is not null)
        .Select(request => request.DurationMs!.Value).Order().ToArray();
      int compacted = polls.Sum(poll => poll.Count);
      bool measured = durations.Length > 0 || polls.Length > 0;
      return new
      {
        count = group.Count() - polls.Length + compacted,
        completed = completed.Length - polls.Length + compacted,
        compacted_poll_count = compacted,
        duration_sum_ms = measured ? (double?)(durations.Sum() + polls.Sum(poll => poll.SumMs)) : null,
        duration_min_ms = measured ? (double?)durations.Concat(polls.Select(poll => poll.MinMs)).Min() : null,
        duration_median_ms = polls.Length == 0 && durations.Length > 0
          ? (double?)(durations[(durations.Length - 1) / 2] + durations[durations.Length / 2]) / 2 : null,
        duration_max_ms = measured ? (double?)durations.Concat(polls.Select(poll => poll.MaxMs)).Max() : null,
        bytes = completed.Any(request => request.Bytes is not null)
          ? (long?)completed.Sum(request => request.Bytes) : null,
        pixels = completed.Any(request => request.Pixels is not null)
          ? (long?)completed.Sum(request => request.Pixels) : null,
      };
    });
}

public sealed partial class MainWindow
{
  private static double PdfPipelineProbeTime(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

  private sealed record PdfPipelineBatch(
    PdfSmokeRequestProbe.JobFact Job, PdfSmokeRequestProbe.PaddleFact Paddle,
    IReadOnlyList<PdfSmokeRequestProbe.UploadFact> Inputs, long InputBytes, long InputPixels);

  private static PdfPipelineBatch[] ValidatePdfPipelineBatches(
    IReadOnlyList<PdfSmokeRequestProbe.RequestEvent> business, int[] targets,
    string instance, string preset)
  {
    if (business.Any(request => request.Outcome != "completed"))
      throw new InvalidOperationException("An OCR business request did not complete.");

    var renders = business.Where(request => request.Purpose == "business_render").ToArray();
    var writes = business.Where(request => request.Kind == "add_text_layers").ToArray();
    if (!PdfPipelinePhaseStats.CoversPages(renders.Select(render => render.Page), targets) ||
        !PdfPipelinePhaseStats.CoversPages(writes.SelectMany(write => write.ConfirmedPages ?? []), targets) ||
        writes.Any(write => write.Purpose != "dirty_write" ||
          write.Pages is null || write.ConfirmedPages is null || !write.Pages.SequenceEqual(write.ConfirmedPages)))
      throw new InvalidOperationException("Expected each OCR page to have one business render and one confirmed dirty write.");

    string provider = PaddleSmokeDevice == "directml" ? "DmlExecutionProvider" : "CPUExecutionProvider";
    var batches = new List<PdfPipelineBatch>();
    foreach (var submit in business.Where(request => request.Kind == "job_submit"))
    {
      string id = submit.Detail ?? throw new InvalidOperationException("OCR submit did not identify the job.");
      var observations = business.Where(request => request.Kind == "job_observe" && request.Session == id).ToArray();
      var terminalEvent = observations.LastOrDefault(request => request.Job?.Terminal is not null);
      var terminal = terminalEvent?.Job?.Terminal;
      var paddle = observations.Select(request => request.Job?.Paddle).OfType<PdfSmokeRequestProbe.PaddleFact>().SingleOrDefault();
      var inputs = submit.Uploads;
      if (terminal is null || inputs is null || inputs.Count == 0 || inputs.Count != submit.Param ||
          terminal.JobId != id || terminal.InstanceId != instance || terminal.State != JobState.Completed ||
          terminal.Pipeline != "OCR" || terminal.Engine != OcrEngine.PaddleOcr ||
          terminal.Summary.Total != inputs.Count || terminal.Summary.Succeeded != inputs.Count ||
          terminal.Summary.Cancelled != 0 || terminal.Summary.Failed != 0 ||
          terminal.Items.Any(item => item.State != ItemState.Succeeded) ||
          terminal.Items.Select(item => item.ItemId).Distinct().Count() != inputs.Count ||
          !terminal.Items.Select(item => item.ClientItemKey).Order().SequenceEqual(
            inputs.Select(input => input.ClientItemKey).Order()))
        throw new InvalidOperationException($"Missing or inconsistent successful OCR job evidence for {id}.");
      if (paddle is null || paddle.Pipeline != "OCR" || paddle.Device != PaddleSmokeDevice ||
          paddle.Provider != provider || paddle.AutomaticFallback != false ||
          paddle.Preset != preset || paddle.Models.Length == 0 || string.IsNullOrEmpty(paddle.Source) ||
          paddle.Selections.ValueKind != JsonValueKind.Object ||
          !paddle.Selections.TryGetProperty("use_textline_orientation", out JsonElement orientation) ||
          orientation.ValueKind != JsonValueKind.True)
        throw new InvalidOperationException($"Unexpected Paddle execution snapshot for {id}.");

      long pixels = 0;
      foreach (var input in inputs)
      {
        var render = renders.Single(request => $"page-{request.Page}" == input.ClientItemKey);
        var write = writes.Single(request => request.ConfirmedPages!.Contains(render.Page));
        if (input.Bytes <= 0 || render.Bytes != input.Bytes || render.Pixels is not > 0 ||
            render.EndedAtMs is not double rendered || rendered > submit.StartedAtMs ||
            terminalEvent?.EndedAtMs is not double observed || observed > write.StartedAtMs)
          throw new InvalidOperationException($"Missing PNG facts or out-of-order job/write for {id} input {input.ClientItemKey}.");
        pixels += render.Pixels.Value;
      }
      long bytes = inputs.Sum(input => input.Bytes);
      if (submit.Bytes != bytes)
        throw new InvalidOperationException($"Job {id} contains unattributed upload bytes.");
      batches.Add(new(terminal, paddle, inputs, bytes, pixels));
    }

    if (batches.Select(batch => batch.Job.JobId).Distinct().Count() != batches.Count ||
        !batches.SelectMany(batch => batch.Inputs).Select(input => input.ClientItemKey).Order().SequenceEqual(
          targets.Select(page => $"page-{page}").Order()))
      throw new InvalidOperationException("OCR job inputs are missing, repeated or include a skipped page.");
    return batches.ToArray();
  }

  private async Task<object> RunPaddlePdfPipelineAsync(
    string mode, string fixture, ProductRuntimeSession session, int timeoutMinutes)
  {
    if (mode != "paddle_text")
      throw new InvalidOperationException("pdf_pipeline requires the Paddle text OCR mode.");
    string preset = PaddleSmokeEnv("VIBEOCR_PADDLE_SMOKE_PRESET") ?? "small";
    bool foreground = PaddleSmokeEnv("VIBEOCR_T3_PIPELINE_FOREGROUND") == "1";
    var probe = new PdfSmokeRequestProbe(capturePipeline: true);
    var passes = new List<object>(2);
    var original = new FileInfo(fixture);
    long originalBytes = original.Length;
    DateTime originalWriteTime = original.LastWriteTimeUtc;
    DeferredInferenceClient.SetPdfSmokeProbe(probe);
    try
    {
      await NavigateSmokeAsync("PDF", "button");
      string installed = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(PdfBaselineObserverScript);
      if (installed != "\"installed\"")
        throw new InvalidOperationException($"PDF baseline observer install failed: {installed}");

      passes.Add(await RunPdfPipelinePassAsync("cold", mode, fixture, preset, foreground, session, timeoutMinutes, probe));
      passes.Add(await RunPdfPipelinePassAsync("steady", mode, fixture, preset, foreground, session, timeoutMinutes, probe));
      var current = new FileInfo(fixture);
      if (current.Length != originalBytes || current.LastWriteTimeUtc != originalWriteTime)
        throw new InvalidOperationException("Source fixture changed during OCR and save-as.");
      var (events, maxInflight, overflow) = probe.Snapshot();
      if (overflow)
        throw new InvalidOperationException("PDF request event buffer overflowed.");
      if (session.InstanceId != smokeManagedSession?.Invoke()?.InstanceId)
        throw new InvalidOperationException("Supervisor changed during the PDF baseline.");

      RecordPaddleSmokeStage("pdf pipeline: measurements complete");
      paddleSmokeOutcome = "passed";
      return new
      {
        input_kind = "pdf_pipeline",
        fixture = new { path = fixture, bytes = originalBytes, last_write_utc = originalWriteTime },
        device = PaddleSmokeDevice, model_preset = preset, supervisor = session.InstanceId,
        supervisor_pid = session.Process.Ready.Pid, process_pid = Environment.ProcessId,
        interaction = foreground ? "controlled_foreground" : "unattended_ocr",
        passes,
        requests = new { events, max_inflight = maxInflight },
        clocks = "Browser durations use performance.now; host request durations use Stopwatch. UTC windows only attribute external process samples and logs.",
        limitations = new
        {
          provider = "Job snapshots identify frozen execution settings; worker diagnostics confirm instantiated session providers.",
          observe = "Host-to-Supervisor polling round-trips. Unchanged polls retain count, sum, minimum and maximum; the exact median is absent when compacted. Native CPU/device wait is NOT_MEASURED.",
          inputs = "Encoded uploads and IHDR pixels by job. Byte totals are not copy cost or allocated memory.",
          retention = "Terminal job/item facts do not measure their retained memory. Results and _outcomes can reference the same payload.",
          staging = "Post-run staging inventory, when present, excludes transient/native peaks.",
        },
      };
    }
    finally
    {
      var (events, maxInflight, overflow) = probe.Snapshot();
      paddleSmokePartialEvidence = new { passes, events, max_inflight = maxInflight, overflow };
      DeferredInferenceClient.SetPdfSmokeProbe(null);
      await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync("window.__t2PdfBaseline?.dispose() === true");
    }
  }

  private async Task<object> RunPdfPipelinePassAsync(
    string label, string mode, string fixture, string preset, bool foreground,
    ProductRuntimeSession session, int timeoutMinutes, PdfSmokeRequestProbe probe)
  {
    RecordPaddleSmokeStage($"pdf pipeline {label}: open");
    await SetPdfBaselinePhaseAsync($"{label}:open");
    string pick = label + "_pick";
    long pickAt = 0;
    long pickUtc = 0;
    await ClickWorkbenchSmokeButtonAsync("打开 PDF");
    await CompletePaddleOpenPickerAsync(fixture, onBeforeConfirm: async () =>
    {
      if (!await MarkPdfBaselineBrowserAsync(pick))
        throw new InvalidOperationException("PDF picker confirmation marker missing.");
      pickAt = PdfBaselineNow();
      pickUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    });
    PdfBaselineImageWait first = await WaitForPdfBaselineImageAsync(
      label, 0, pick, label + "_first", TimeSpan.FromMinutes(2), requireNew: true);
    PdfBaselineBrowserImage firstWitness = first.NewWitness ??
      throw new InvalidOperationException("Missing newly decoded first PDF image.");
    PdfWorkbenchState opened = await WaitForPaddlePdfAsync(
      state => !state.IsBusy && state.PageCount > 0 && state.PageInspectStatusCode == "pdf.inspect.ready",
      TimeSpan.FromMinutes(2));
    long readyAt = PdfBaselineNow();
    long readyUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    ValidatePdfBaselineResourceOwnership(label, opened, first, first.Snapshot, 0);
    if (!opened.CanDemandPages || opened.SessionId is null || opened.DocumentId is null ||
        opened.ProcessingSettings is not { RenderDpi: > 0, MaxPixels: > 0 } settings)
      throw new InvalidOperationException("The candidate did not expose the PDF demand path and render settings.");
    (int pageCount, int[] targets, int[] skips) = Path.GetFileName(fixture) switch
    {
      "scan_short.pdf" => (6, Enumerable.Range(0, 6).ToArray(), []),
      "scan_long.pdf" => (120, Enumerable.Range(0, 120).ToArray(), []),
      "mixed.pdf" => (30, Enumerable.Range(0, 9).ToArray(), Enumerable.Range(10, 10).ToArray()),
      _ => throw new InvalidOperationException("Unknown PDF pipeline fixture."),
    };
    if (opened.PageCount != pageCount)
      throw new InvalidOperationException("PDF fixture page count does not match the baseline plan.");
    int[] requested = [.. targets, .. skips];
    await WaitForPaddleModeAsync("#pdf-task-engine", mode);
    await SelectSmokeValueAsync("#pdf-task-engine", mode);
    await ClickWorkbenchSmokeButtonAsync("全选页面");
    await WaitForPaddlePdfAsync(state => state.SelectedPages?.Count == pageCount, TimeSpan.FromSeconds(30));
    foreach (int page in Enumerable.Range(0, pageCount).Except(requested))
    {
      string selector = JsonSerializer.Serialize($"input[aria-label='选择第 {page + 1} 页']");
      string clicked = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
        $"(() => {{ const e = document.querySelector({selector}); if (!e?.checked) return false; e.click(); return true; }})()");
      if (clicked != "true")
        throw new InvalidOperationException($"Unable to deselect PDF page {page + 1}.");
      await WaitForPaddlePdfAsync(state => state.SelectedPages?.Contains(page) == false, TimeSpan.FromSeconds(30));
    }

    PdfWorkbenchState selected = await WaitForPaddlePdfAsync(
      state => !state.IsBusy && state.TaskEngine == mode && state.CanAddTextLayer &&
        state.SelectedPages?.SequenceEqual(requested) == true,
      TimeSpan.FromSeconds(30));
    RecordPaddleSmokeStage($"pdf pipeline {label}: OCR");
    await SetPdfBaselinePhaseAsync($"{label}:ocr");
    long ocrStartUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    long ocrAt = PdfBaselineNow();
    await ClickWorkbenchSmokeButtonAsync("添加选中页文字层");
    var navigation = new List<PdfPipelinePageTurn>(3);
    if (foreground)
    {
      await WaitForPaddlePdfAsync(state => state.IsBusy, TimeSpan.FromSeconds(30));
      foreach (int page in new[] { 1, 2, 0 })
        navigation.Add(await MeasurePdfPipelinePageTurnAsync(label, opened, page));
    }
    PdfWorkbenchState terminal = await WaitForPaddlePdfAsync(
      state => !state.IsBusy && state.Summary.StartsWith("已添加", StringComparison.Ordinal),
      TimeSpan.FromMinutes(timeoutMinutes));
    long terminalAt = PdfBaselineNow();
    long ocrEndUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    string expectedSummary = $"已添加 {targets.Length} 页、跳过 {skips.Length} 页、失败 0 页";
    if (terminal.SessionId != opened.SessionId || terminal.AddedCount != targets.Length ||
        !terminal.IsModified || !terminal.Summary.StartsWith(expectedSummary, StringComparison.Ordinal))
      throw new InvalidOperationException($"OCR result differs from requested pages: {terminal.Summary}");

    await SetPdfBaselinePhaseAsync($"{label}:save");
    long saveStartUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    long saveAt = PdfBaselineNow();
    object saved = await RunPaddlePdfSaveAsync($"pipeline-{label}");
    long savedAt = PdfBaselineNow();
    long saveEndUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    PdfWorkbenchState savedState = await WaitForPaddlePdfAsync(
      state => !state.IsBusy && !state.IsModified, TimeSpan.FromMinutes(2));
    PdfBaselineBrowserSnapshot browser = await PdfBaselineSnapshotAsync();
    if (!browser.StateMessageListenerAvailable || browser.StateMessageMeasurementErrors != 0)
      throw new InvalidOperationException("PDF browser state measurement failed.");

    var (events, _, _) = probe.Snapshot();
    var business = PdfPipelinePhaseStats.BusinessRequests(PdfPipelinePhaseStats.Window(events,
        PdfPipelineProbeTime(ocrAt), PdfPipelineProbeTime(terminalAt)), opened.SessionId);
    PdfPipelineBatch[] batches = ValidatePdfPipelineBatches(business, targets, session.InstanceId, preset);
    var saveWire = PdfPipelinePhaseStats.Window(events, PdfPipelineProbeTime(saveAt), PdfPipelineProbeTime(savedAt))
      .Where(request => request.Session == opened.SessionId && request.Kind == "save_operation").ToArray();
    if (saveWire is not [{ Outcome: "completed", Detail: not null }] || (saveWire[0].Param & 3) != 2)
      throw new InvalidOperationException("Expected one confirmed save-as operation for the PDF session.");

    RecordPaddleSmokeStage($"pdf pipeline {label}: close");
    await ClickWorkbenchSmokeButtonAsync("关闭文档");
    await WaitForPaddlePdfAsync(
      state => state.PageCount == 0 && (state.Documents?.Count ?? 0) == 0, TimeSpan.FromSeconds(30));

    browser.AllKnownFirst.TryGetValue(opened.SessionId, out var allKnown);
    return new
    {
      label, session = opened.SessionId, document_id = opened.DocumentId,
      host_ms = new
      {
        pick_confirm_to_ready = PdfBaselineElapsedMs(pickAt, readyAt),
        ocr_to_terminal = PdfBaselineElapsedMs(ocrAt, terminalAt),
        save_as = PdfBaselineElapsedMs(saveAt, savedAt),
      },
      utc_windows = new
      {
        open = new { start = pickUtc, end = readyUtc },
        ocr = new { start = ocrStartUtc, end = ocrEndUtc },
        save = new { start = saveStartUtc, end = saveEndUtc },
      },
      settings = new { settings.RenderDpi, settings.MaxPixels },
      detection = new
      {
        page_count = pageCount, selected_pages = requested, ocr_pages = targets, skipped_native_pages = skips,
        at_ocr_start = selected.DetectedCount, at_ocr_end = terminal.DetectedCount,
        pick_to_all_known_browser_ms = allKnown is null ? (double?)null : allKnown.At - PdfBaselineMark(browser, pick),
        load_end = PdfBaselineLoadEndReference(events, opened.SessionId, pickAt),
      },
      phases = PdfPipelinePhaseStats.ByKind(business),
      batches, batch_count = batches.Length,
      largest_job_input_bytes = batches.Max(batch => batch.InputBytes),
      largest_job_input_pixels = batches.Max(batch => batch.InputPixels),
      document = new
      {
        revision_before = selected.Revision, revision_after = terminal.Revision,
        is_modified_after_ocr = terminal.IsModified, added_count = terminal.AddedCount,
        saved = new { saved, save_wire = saveWire[0], is_modified_after_save = savedState.IsModified },
      },
      browser = new
      {
        pick_to_first_decoded_ms = firstWitness.DecodedAt!.Value - PdfBaselineMark(first.Snapshot, pick),
        pick_to_first_frame_ms = firstWitness.FrameOpportunityAt!.Value - PdfBaselineMark(first.Snapshot, pick),
        visible_at_first_frame = firstWitness.VisibilityAtFrame == "visible",
        focused_at_first_frame = firstWitness.HasFocusAtFrame == true,
        state_messages_bytes_estimate = browser.StateMessagePhases
          .Where(phase => phase.Key.StartsWith(label + ":", StringComparison.Ordinal)),
        page_turns = navigation,
        page_turn_median_ms = navigation.Count == 3 ? (double?)navigation.Select(turn => turn.Ms).Order().ElementAt(1) : null,
        page_turn_max_ms = navigation.Count > 0 ? (double?)navigation.Max(turn => turn.Ms) : null,
      },
      performance_eligible = firstWitness.VisibilityAtFrame == "visible" && firstWitness.HasFocusAtFrame == true,
      performance_ineligible_reason = firstWitness.VisibilityAtFrame != "visible"
        ? "first_frame_not_visible" : firstWitness.HasFocusAtFrame != true ? "first_frame_not_focused" : null,
    };
  }

  private sealed record PdfPipelinePageTurn(int Page, double Ms, bool NewImage, object Clarity);

  private async Task<PdfPipelinePageTurn> MeasurePdfPipelinePageTurnAsync(string label, PdfWorkbenchState opened, int page)
  {
    string tag = $"{label}_ocr_page_{page}";
    string selector = JsonSerializer.Serialize($"button[aria-label='检查第 {page + 1} 页']");
    string mark = JsonSerializer.Serialize(tag);
    string clicked = await WorkbenchWebView.CoreWebView2.ExecuteScriptAsync(
      $"(() => {{ const button = document.querySelector({selector}); if (!button || document.visibilityState !== 'visible' || !document.hasFocus()) return false; button.scrollIntoView({{ block: 'nearest' }}); if (!window.__t2PdfBaseline.mark({mark})) return false; button.click(); return true; }})()");
    if (clicked != "true")
      throw new InvalidOperationException("Controlled page turn requires a visible and focused workbench.");
    PdfBaselineImageWait wait = await WaitForPdfBaselineImageAsync(
      tag, page, tag, tag, TimeSpan.FromMinutes(2), requireNew: false, requireClarity: true);
    var state = await WaitForPaddlePdfAsync(state => state.SelectedPage == page, TimeSpan.FromSeconds(30));
    if (!state.IsBusy || state.SessionId != opened.SessionId || state.DocumentId != opened.DocumentId ||
        wait.Pulse.VisibilityState != "visible" || !wait.Pulse.HasFocus || wait.Snapshot.PublicationsOverflow ||
        !wait.Snapshot.Publications.Any(publication =>
          publication.SessionId == opened.SessionId && publication.DocumentId == opened.DocumentId &&
          publication.Page == page && publication.PreviewUrl == wait.Witness.FullSrc &&
          publication.At <= wait.Pulse.At))
      throw new InvalidOperationException("Page-turn sample lacked a visible, matching, decoded frame during active OCR.");
    return new(page, wait.Pulse.At - PdfBaselineMark(wait.Snapshot, tag), wait.NewImageObserved,
      PdfBaselineClarity(tag, wait.Witness, wait.Pulse, wait.Snapshot, wait.Snapshot.Dpr).Detail);
  }
}

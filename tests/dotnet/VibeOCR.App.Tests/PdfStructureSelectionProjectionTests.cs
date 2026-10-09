using System.Text.Json;
using VibeOCR.App.Features.Pdf;
using VibeOCR.App.ViewModels;
using VibeOCR.App.Web;
using VibeOCR.App.Workbench;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Inference;
using Wire = VibeOCR.Runtime.Contracts.Generated.Wire;
using Xunit;

namespace VibeOCR.App.Tests;

/// <summary>
/// 宿主级回归：结构变更（重排/插入/删除）后的任何非 busy 新修订发布都必须携带按
/// 页 identity 重映射后的 SelectedPages，不允许发布"新页面结构 + 旧选择集"的混合
/// 快照。缩略图等资源读取在修正发布前被门控，使中间窗口可被确定性观察。
/// </summary>
public sealed class PdfStructureSelectionProjectionTests
{
  [Fact]
  public async Task ReorderNeverPublishesNewRevisionWithStaleSelection()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-pdf-selection-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
      var client = new Client(3);
      var model = new PdfViewModel(client, new Source());
      await model.OpenPathAsync("synthetic.pdf", CancellationToken.None);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      await using var handler = Handler(() => model, root, broker, annotations);
      await AssertSelectionFollowsIdentityAsync(client, handler, model, select: [1],
        revision => handler.ExecuteAsync(new MovePdfPageCommand(1, 2, revision), CancellationToken.None).AsTask(),
        expectedSelection: [2], expectedPageCount: 3);
    }
    finally { Directory.Delete(root, recursive: true); }
  }

  [Fact]
  public async Task InsertBlankNeverPublishesNewRevisionWithStaleSelection()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-pdf-selection-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
      var client = new Client(3);
      var model = new PdfViewModel(client, new Source());
      await model.OpenPathAsync("synthetic.pdf", CancellationToken.None);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      await using var handler = Handler(() => model, root, broker, annotations);
      await AssertSelectionFollowsIdentityAsync(client, handler, model, select: [1],
        revision => handler.ExecuteAsync(new InsertPdfBlankCommand(-1, 640, 480, revision), CancellationToken.None).AsTask(),
        expectedSelection: [2], expectedPageCount: 4);
    }
    finally { Directory.Delete(root, recursive: true); }
  }

  [Fact]
  public async Task DeleteNeverPublishesNewRevisionWithStaleSelection()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-pdf-selection-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
      var client = new Client(3);
      var model = new PdfViewModel(client, new Source());
      await model.OpenPathAsync("synthetic.pdf", CancellationToken.None);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      await using var handler = Handler(() => model, root, broker, annotations);
      await AssertSelectionFollowsIdentityAsync(client, handler, model, select: [0],
        revision => handler.ExecuteAsync(new DeletePdfPagesCommand(), CancellationToken.None).AsTask(),
        expectedSelection: [], expectedPageCount: 2);
    }
    finally { Directory.Delete(root, recursive: true); }
  }

  [Fact]
  public async Task StructureRemapLeavesSiblingDocumentSelectionIntact()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-pdf-selection-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
      var client = new Client(3);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      // 每个文档独立 view model：工厂随 CreatePdfViewModel 逐次创建。
      await using var handler = Handler(() => new PdfViewModel(client, new Source()), root, broker, annotations);
      await handler.ExecuteAsync(new OpenDroppedPdfCommand(Path.Combine(root, "a.pdf")), CancellationToken.None);
      PdfDocumentEntry a = Assert.Single(handler.PdfDocuments);
      await handler.ExecuteAsync(new SelectPdfPagesCommand([1]), CancellationToken.None);
      await handler.ExecuteAsync(new OpenDroppedPdfCommand(Path.Combine(root, "b.pdf")), CancellationToken.None);
      PdfDocumentEntry b = handler.PdfDocuments.Last();
      Assert.NotSame(a.Model, b.Model);
      await AssertSelectionFollowsIdentityAsync(client, handler, b.Model, select: [1],
        revision => handler.ExecuteAsync(new MovePdfPageCommand(1, 2, revision), CancellationToken.None).AsTask(),
        expectedSelection: [2], expectedPageCount: 3);
      // 重映射仅作用于变更文档：兄弟文档选择集保持 identity 原选择。
      WorkbenchCommandOutcome back = await handler.ExecuteAsync(new PdfBoundCommand(a.Id, new ActivatePdfDocumentCommand()), CancellationToken.None);
      Assert.Equal([1], State(back).SelectedPages);
    }
    finally { Directory.Delete(root, recursive: true); }
  }

  [Fact]
  public async Task LateStructureCompletionOutsideActiveCommandKeepsActiveWindow()
  {
    string root = Path.Combine(Path.GetTempPath(), $"vibeocr-pdf-selection-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
      var client = new Client(3);
      using var broker = new WorkbenchResourceBroker(root);
      using var annotations = new WorkbenchAnnotationStore(root);
      await using var handler = Handler(() => new PdfViewModel(client, new Source()), root, broker, annotations);
      await handler.ExecuteAsync(new OpenDroppedPdfCommand(Path.Combine(root, "a.pdf")), CancellationToken.None);
      PdfDocumentEntry a = Assert.Single(handler.PdfDocuments);
      await handler.ExecuteAsync(new SelectPdfPagesCommand([1]), CancellationToken.None);
      await handler.ExecuteAsync(new OpenDroppedPdfCommand(Path.Combine(root, "b.pdf")), CancellationToken.None);
      PdfDocumentEntry b = handler.PdfDocuments.Last();
      // 活跃文档 B 的窗口缩略图已就绪；随后 A 的结构收尾发生在命令上下文之外
      //（迟到续体，无 AsyncLocal 归属）：不得回收 B 的窗口，也不得改写 B 的选择。
      int renders = client.RenderCount;
      await a.Model.ReorderAsync([0, 2, 1], CancellationToken.None);
      WorkbenchCommandOutcome active = await handler.ExecuteAsync(new PdfBoundCommand(b.Id, new ActivatePdfDocumentCommand()), CancellationToken.None);
      Assert.Equal(renders, client.RenderCount);
      Assert.NotNull(State(active).Pages![0].Thumbnail);
      // A 的选择集仍按 identity 重映射到新索引。
      PdfWorkbenchState back = State(await handler.ExecuteAsync(new PdfBoundCommand(a.Id, new ActivatePdfDocumentCommand()), CancellationToken.None));
      Assert.Equal([2], back.SelectedPages);
    }
    finally { Directory.Delete(root, recursive: true); }
  }

  private static async Task AssertSelectionFollowsIdentityAsync(
    Client client, DesktopWorkbenchCommandHandler handler, PdfViewModel model, int[] select,
    Func<long, Task<WorkbenchCommandOutcome>> mutation, int[] expectedSelection, int expectedPageCount)
  {
    PdfWorkbenchState selected = State(await handler.ExecuteAsync(new SelectPdfPagesCommand(select), CancellationToken.None));
    Assert.Equal(select, selected.SelectedPages);
    long revision = model.Revision;
    var published = new List<PdfWorkbenchState>();
    void Observe(WorkbenchState state)
    {
      if (state is PdfWorkbenchState current && current.Revision == revision + 1) published.Add(current);
    }
    handler.StateChanged += Observe;
    // 门控结构变更后的缩略图重渲染：修正发布（宿主重映射完成后的 PdfStateAsync）
    // 被确定性阻塞，混合中间快照必然先于其发布并可被观察。
    client.ThumbnailGate = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
    Task<WorkbenchCommandOutcome> pending = mutation(revision);
    try
    {
      await client.ThumbnailEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
      PdfWorkbenchState[] beforeCorrection = published.Where(state => !state.IsBusy).ToArray();
      client.ThumbnailGate.TrySetResult([1, 2, 3]);
      WorkbenchCommandOutcome outcome = await pending;
      Assert.NotEmpty(beforeCorrection);
      foreach (PdfWorkbenchState state in beforeCorrection)
        Assert.True(expectedSelection.SequenceEqual(state.SelectedPages ?? []),
          $"non-busy revision {state.Revision} publish carried stale selection [{string.Join(",", state.SelectedPages ?? [])}] instead of [{string.Join(",", expectedSelection)}]");
      PdfWorkbenchState final = State(outcome);
      Assert.Equal(expectedSelection, final.SelectedPages);
      Assert.Equal(expectedPageCount, final.PageCount);
      Assert.Equal(revision + 1, model.Revision);
    }
    finally
    {
      // 前置等待异常时同样释放门控并退订，不遗留悬挂渲染与残留订阅。
      client.ThumbnailGate.TrySetResult([1, 2, 3]);
      handler.StateChanged -= Observe;
    }
  }

  private static PdfWorkbenchState State(WorkbenchCommandOutcome outcome) => Assert.IsType<PdfWorkbenchState>(Assert.Single(outcome.States));
  private static DesktopWorkbenchCommandHandler Handler(Func<PdfViewModel> factory, string root, WorkbenchResourceBroker broker, WorkbenchAnnotationStore annotations) => new(
    static () => throw new InvalidOperationException(), static () => throw new InvalidOperationException(), static () => throw new InvalidOperationException(),
    factory, static () => throw new InvalidOperationException(), static () => throw new InvalidOperationException(), static () => throw new InvalidOperationException(),
    new DiagnosticsViewModel("test", new PrerequisiteReport([])), broker, root, static () => 0, annotations);
  private static Wire.PdfPageInfoMirror Page(int index, double width = 300, double height = 500) =>
    new() { PageIndex = index, Rect = new[] { 0d, 0d, width, height }.Select(value => JsonSerializer.SerializeToElement(value)).ToArray(), HasTextLayer = false };
  private sealed class Source : IPdfFileSource { public Task<string?> PickFileAsync(CancellationToken ct) => Task.FromResult<string?>(null); }
  private sealed class Client(int pageCount) : InferenceClientStub
  {
    public List<Wire.PdfPageInfoMirror> Models { get; } = Enumerable.Range(0, pageCount).Select(index => Page(index)).ToList();
    public TaskCompletionSource<byte[]>? ThumbnailGate { get; set; }
    public TaskCompletionSource ThumbnailEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int RenderCount { get; private set; }
    private Wire.PdfDocumentMirror Model() => new() { Pages = Models.ToArray(), IsModified = true };
    private PdfMutateResult Mutation() => new(Models.Count, new Wire.ModelDiff { FullModel = Model() });
    public int Opens { get; private set; }
    public override Task<PdfSessionOpenResult> OpenPdfSessionAsync(string path, string? password, CancellationToken ct) => Task.FromResult(new PdfSessionOpenResult($"session-{++Opens}", Models.Count, path, Model()));
    public override Task<Wire.PdfDocumentMirror> GetPdfModelAsync(string sessionId, CancellationToken ct) => Task.FromResult(Model());
    public override Task<byte[]> RenderPdfPageAsync(string sessionId, int page, int size, CancellationToken ct)
    { RenderCount++; if (ThumbnailGate is { } gate) { ThumbnailEntered.TrySetResult(); return gate.Task; } return Task.FromResult(new byte[] { 1, 2, 3 }); }
    public override Task<PdfMutateResult> ReorderPdfAsync(string sessionId, int[] newOrder, CancellationToken ct)
    { var old = Models.ToArray(); Models.Clear(); Models.AddRange(newOrder.Select(index => old[index])); Reindex(); return Task.FromResult(Mutation()); }
    public override Task<PdfMutateResult> InsertPdfBlankAsync(string sessionId, int afterIndex, double width, double height, CancellationToken ct)
    { Models.Insert(afterIndex + 1, Page(afterIndex + 1, width, height)); Reindex(); return Task.FromResult(Mutation()); }
    public override Task<PdfMutateResult> DeletePdfPagesAsync(string sessionId, int[] pages, CancellationToken ct)
    { foreach (int index in pages.Order().Reverse()) Models.RemoveAt(index); Reindex(); return Task.FromResult(Mutation()); }
    private void Reindex() { for (int index = 0; index < Models.Count; index++) { var page = Models[index]; Models[index] = Page(index, page.Rect![2].GetDouble(), page.Rect![3].GetDouble()); } }
  }
}

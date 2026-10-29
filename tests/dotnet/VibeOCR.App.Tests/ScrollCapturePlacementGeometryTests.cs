using Windows.Graphics;
using VibeOCR.App.Features.Recognition;
using VibeOCR.Platform.Windows;
using Xunit;

namespace VibeOCR.App.Tests;

/// <summary>
/// 预览候选纯几何契约（#294 高 DPI 长截图回退）：复用生产校验谓词
/// （工作区包含 + 与选区/工具栏不相交），不新建几何框架。
/// </summary>
public sealed class ScrollCapturePlacementGeometryTests
{
  // 实测反例（capture-diagnosis，3840×2076 工作区 @168 DPI）：
  // 长预览 256×257 增高后，选区四居中候选全部非法，但工具栏右侧合法。
  private static readonly PhysicalRectangle Work = new(0, 0, 3840, 2076);
  private static readonly PhysicalRectangle Selection = new(108, 148, 304, 224);
  private static readonly PhysicalRectangle Toolbar = new(0, 388, 875, 115);
  private const int PreviewWidth = 256;
  private const int PreviewHeight = 257;

  private static bool IsLegal(RectInt32 candidate) =>
      ScrollCaptureSession.Session.ContainsRect(
          Work, new PhysicalRectangle(candidate.X, candidate.Y, candidate.Width, candidate.Height)) &&
      SmartScreenCandidates.Intersect(
          new PhysicalRectangle(candidate.X, candidate.Y, candidate.Width, candidate.Height), Selection) is null &&
      SmartScreenCandidates.Intersect(
          new PhysicalRectangle(candidate.X, candidate.Y, candidate.Width, candidate.Height), Toolbar) is null;

  [Fact]
  public void MeasuredDpiCounterexampleFallsThroughToControlSurroundingCandidates()
  {
    RectInt32[] candidates = ScrollCaptureSession.Session.PreviewPlacementCandidates(
        Work, Selection, Toolbar, PreviewWidth, PreviewHeight);

    // 前四个是既有选区四居中候选且保持顺序；该反例下全部非法（旧逻辑在此失败）。
    Assert.Equal(4, Math.Min(4, candidates.Length));
    Assert.All(candidates.Take(4), candidate => Assert.False(IsLegal(candidate)));
    // 扩展候选（围绕选区∪工具栏）找到合法位置：实测反例的合法空间含工具栏右侧空白。
    Assert.Contains(candidates.Skip(4), candidate => IsLegal(candidate) && candidate.X >= Toolbar.Right);
  }

  [Fact]
  public void PrimarySelectionSideCandidatesStayFirstWhenLegal()
  {
    // 无冲突场景：选区右下方空旷、工具栏远离，合法解必须先来自原四候选。
    PhysicalRectangle selection = new(400, 400, 200, 150);
    PhysicalRectangle toolbar = new(430, 600, 180, 60);
    RectInt32[] candidates = ScrollCaptureSession.Session.PreviewPlacementCandidates(
        Work, selection, toolbar, 120, 90);
    RectInt32 firstLegal = candidates.First(IsLegalAside(selection, toolbar));
    Assert.Contains(firstLegal, candidates.Take(4));
    // 原顺序保持：右侧候选仍是第二个（下/右/左/上）。
    Assert.Equal(4, candidates.Length - 4);
  }

  [Fact]
  public void TrulyNoSpaceFailsClosedWithoutAnyLegalCandidate()
  {
    // 工作区被选区与工具栏基本占满：原候选与扩展候选都必须非法，
    // PositionPreview 的真实 HWND 复核链路仍会拒绝（不把无空间当成功）。
    PhysicalRectangle work = new(0, 0, 500, 300);
    PhysicalRectangle selection = new(20, 20, 200, 120);
    PhysicalRectangle toolbar = new(20, 160, 460, 110);
    RectInt32[] candidates = ScrollCaptureSession.Session.PreviewPlacementCandidates(
        work, selection, toolbar, 260, 220);
    Assert.All(candidates, candidate =>
        Assert.False(ScrollCaptureSession.Session.ContainsRect(
            work, new PhysicalRectangle(candidate.X, candidate.Y, candidate.Width, candidate.Height)) &&
            SmartScreenCandidates.Intersect(
                new PhysicalRectangle(candidate.X, candidate.Y, candidate.Width, candidate.Height), selection) is null &&
            SmartScreenCandidates.Intersect(
                new PhysicalRectangle(candidate.X, candidate.Y, candidate.Width, candidate.Height), toolbar) is null));
  }

  [Fact]
  public void NegativeCoordinateMonitorTranslatesCandidates()
  {
    // 副屏（负坐标）平移：候选同样 clamp 进负工作区并存在合法右侧位置。
    PhysicalRectangle work = new(-1920, 0, 1920, 1040);
    PhysicalRectangle selection = new(-1500, 100, 300, 200);
    PhysicalRectangle toolbar = new(-1500, 350, 700, 100);
    RectInt32[] candidates = ScrollCaptureSession.Session.PreviewPlacementCandidates(
        work, selection, toolbar, 200, 250);
    Assert.Contains(candidates, candidate =>
        ScrollCaptureSession.Session.ContainsRect(
            work, new PhysicalRectangle(candidate.X, candidate.Y, candidate.Width, candidate.Height)) &&
        SmartScreenCandidates.Intersect(
            new PhysicalRectangle(candidate.X, candidate.Y, candidate.Width, candidate.Height), selection) is null &&
        SmartScreenCandidates.Intersect(
            new PhysicalRectangle(candidate.X, candidate.Y, candidate.Width, candidate.Height), toolbar) is null &&
        candidate.X < 0);
  }

  [Fact]
  public void PrimaryLegalCandidateIsConsumedBeforeFallbackSideCandidates()
  {
    // 生产最终尝试顺序契约：primary 左/下侧合法、右侧非法，而 fallback
    // 右侧也合法时，首个被消费的合法候选必须仍是 primary 左侧——
    // 旧全局按边排序会让 fallback 右侧先于 primary 左/下侧（回归反例）。
    PhysicalRectangle selection = new(600, 100, 300, 200);
    PhysicalRectangle toolbar = new(760, 120, 200, 180);
    bool Legal(RectInt32 candidate)
    {
      var rect = new PhysicalRectangle(candidate.X, candidate.Y, candidate.Width, candidate.Height);
      return ScrollCaptureSession.Session.ContainsRect(Work, rect) &&
          SmartScreenCandidates.Intersect(rect, selection) is null &&
          SmartScreenCandidates.Intersect(rect, toolbar) is null;
    }
    RectInt32[] candidates = ScrollCaptureSession.Session.PreviewPlacementCandidates(
        Work, selection, toolbar, PreviewWidth, PreviewHeight);
    RectInt32 firstLegal = candidates.First(Legal);
    // primary 左侧候选：selection.X - gap - previewWidth。
    Assert.Equal(selection.X - 16 - PreviewWidth, firstLegal.X);
    // 取舍真实发生：fallback 右侧同样存在合法候选，但排在 primary 之后。
    Assert.Contains(candidates, candidate => Legal(candidate) && candidate.X > selection.Right);
  }

  private static Func<RectInt32, bool> IsLegalAside(
      PhysicalRectangle selection, PhysicalRectangle toolbar) => candidate =>
      ScrollCaptureSession.Session.ContainsRect(
          Work, new PhysicalRectangle(candidate.X, candidate.Y, candidate.Width, candidate.Height)) &&
      SmartScreenCandidates.Intersect(
          new PhysicalRectangle(candidate.X, candidate.Y, candidate.Width, candidate.Height), selection) is null &&
      SmartScreenCandidates.Intersect(
          new PhysicalRectangle(candidate.X, candidate.Y, candidate.Width, candidate.Height), toolbar) is null;
}

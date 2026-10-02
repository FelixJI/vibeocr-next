using VibeOCR.Platform.Windows;

namespace VibeOCR.App.Features.Recognition;

/// <summary>Physical-pixel selection history, independent of the overlay and OCR submission.</summary>
internal sealed class ScreenSelectionSession(int width, int height)
{
  private static readonly int[] HandleEdges = [5, 4, 6, 1, 2, 9, 8, 10];
  private readonly List<PhysicalRectangle?> _undo = [];
  private readonly List<PhysicalRectangle?> _redo = [];
  private PhysicalRectangle? _beforeDrag;
  private PhysicalRectangle? _pressedSelection;
  private PhysicalPoint _origin;
  private LogicalPoint _dipOrigin;
  private double _pixelsPerDipX = 1, _pixelsPerDipY = 1;
  private int _handle = -1;
  private bool _inside, _moving;
  private IReadOnlyList<PhysicalRectangle> _preview = [];
  private int _previewIndex;
  public PhysicalRectangle? Selection { get; private set; }
  public PhysicalRectangle? ActiveSelection => Selection ??
    (_preview.Count == 0 ? null : _preview[_previewIndex]);
  public int PreviewIndex => _previewIndex;
  public bool ManualOnly { get; private set; }
  public bool IsPointerActive { get; private set; }
  public bool IsDragging { get; private set; }
  public bool CanConfirm => !IsPointerActive && ActiveSelection is not null;
  public bool CanUndo => !IsPointerActive && _undo.Count > 0;
  public bool CanRedo => !IsPointerActive && _redo.Count > 0;
  public int ActiveHandle => IsPointerActive ? _handle : -1;

  public static (double X, double Y)[] HandlePoints(PhysicalRectangle rect) =>
    [(rect.X, rect.Y), (rect.X + rect.Width / 2.0, rect.Y), (rect.Right, rect.Y),
     (rect.X, rect.Y + rect.Height / 2.0), (rect.Right, rect.Y + rect.Height / 2.0),
     (rect.X, rect.Bottom), (rect.X + rect.Width / 2.0, rect.Bottom), (rect.Right, rect.Bottom)];

  public int HitHandle(LogicalPoint point, double pixelsPerDipX = 1, double pixelsPerDipY = 1)
  {
    if (ActiveSelection is not { } rect) return -1;
    int hit = -1;
    double nearest = 6 * 6;
    (double X, double Y)[] points = HandlePoints(rect);
    for (int i = 0; i < points.Length; i++)
    {
      double dx = point.X - points[i].X / pixelsPerDipX;
      double dy = point.Y - points[i].Y / pixelsPerDipY;
      double distance = dx * dx + dy * dy;
      if (distance <= 6 * 6 && (hit < 0 || distance < nearest))
      {
        hit = i;
        nearest = distance;
      }
    }
    return hit;
  }

  public PhysicalPoint SamplePoint(PhysicalPoint pointer, int hoveredHandle)
  {
    int handle = IsPointerActive ? _handle : hoveredHandle;
    if (handle >= 0 && ActiveSelection is { } rect)
    {
      (double x, double y) = HandlePoints(rect)[handle];
      // Right/Bottom are exclusive geometry boundaries; sample the last included pixel.
      int edges = HandleEdges[handle];
      pointer = new((int)Math.Floor(x) - ((edges & 2) != 0 ? 1 : 0),
                    (int)Math.Floor(y) - ((edges & 8) != 0 ? 1 : 0));
    }
    return new(Math.Clamp(pointer.X, 0, width - 1), Math.Clamp(pointer.Y, 0, height - 1));
  }

  public void Begin(PhysicalPoint point, LogicalPoint? dipPoint = null,
    double pixelsPerDipX = 1, double pixelsPerDipY = 1)
  {
    if (IsPointerActive) CancelDrag();
    _pixelsPerDipX = pixelsPerDipX;
    _pixelsPerDipY = pixelsPerDipY;
    _dipOrigin = dipPoint ?? ToDip(point);
    _origin = Clamp(point);
    _beforeDrag = Selection;
    _pressedSelection = ActiveSelection;
    _handle = HitHandle(_dipOrigin, pixelsPerDipX, pixelsPerDipY);
    _inside = _pressedSelection is { } rect && Contains(rect, point);
    _moving = Selection is not null && _inside && _handle < 0;
    IsPointerActive = true;
    IsDragging = false;
  }

  public void Move(PhysicalPoint point, LogicalPoint? dipPoint = null)
  {
    if (!IsPointerActive) return;
    if (!IsDragging)
    {
      LogicalPoint dip = dipPoint ?? ToDip(point);
      double dxDip = dip.X - _dipOrigin.X, dyDip = dip.Y - _dipOrigin.Y;
      // Fractional DPI coordinates can add rounding noise at exactly four DIP.
      if (dxDip * dxDip + dyDip * dyDip <= 4 * 4 + 1e-9) return;
      IsDragging = true;
      ClearPreview();
    }
    point = Clamp(point);
    int dx = point.X - _origin.X;
    int dy = point.Y - _origin.Y;
    if (_beforeDrag is { } rect && _moving)
    {
      Selection = rect with
      {
        X = Math.Clamp(rect.X + dx, 0, width - rect.Width),
        Y = Math.Clamp(rect.Y + dy, 0, height - rect.Height)
      };
    }
    else if (_pressedSelection is { } previous && _handle >= 0)
    {
      int edges = HandleEdges[_handle];
      int left = (edges & 1) != 0 ? Math.Clamp(previous.X + dx, 0, previous.Right - 1) : previous.X;
      int right = (edges & 2) != 0 ? Math.Clamp(previous.Right + dx, previous.X + 1, width) : previous.Right;
      int top = (edges & 4) != 0 ? Math.Clamp(previous.Y + dy, 0, previous.Bottom - 1) : previous.Y;
      int bottom = (edges & 8) != 0 ? Math.Clamp(previous.Bottom + dy, previous.Y + 1, height) : previous.Bottom;
      Selection = new(left, top, right - left, bottom - top);
    }
    else
    {
      int w = Math.Abs(point.X - _origin.X);
      int h = Math.Abs(point.Y - _origin.Y);
      Selection = w > 0 && h > 0 ? new(Math.Min(point.X, _origin.X), Math.Min(point.Y, _origin.Y), w, h) : null;
    }
  }

  // A click requests the overlay's existing confirmation/revalidation path.
  public bool End(PhysicalPoint point, LogicalPoint? dipPoint = null)
  {
    if (!IsPointerActive) return false;
    Move(point, dipPoint);
    bool confirm = !IsDragging && _handle < 0 && _inside &&
      ActiveSelection == _pressedSelection && _pressedSelection is { } rect && Contains(rect, point);
    if (IsDragging && Selection != _beforeDrag) Remember(_beforeDrag);
    IsPointerActive = false;
    IsDragging = false;
    return confirm;
  }

  public void CancelDrag()
  {
    if (!IsPointerActive) return;
    Selection = _beforeDrag;
    IsPointerActive = false;
    IsDragging = false;
  }

  // Returns true only when the caller should exit the screenshot session.
  public bool Back()
  {
    if (IsPointerActive) { CancelDrag(); return false; }
    if (_preview.Count > 0) { ReturnToManual(); return false; }
    if (Selection is null) return true;
    Remember(Selection);
    Selection = null;
    return false;
  }

  public void Adjust(int dx, int dy, bool resize)
  {
    if (!CanConfirm || Selection is not { } rect) return;
    PhysicalRectangle next = resize
      ? rect with { Width = Math.Clamp(rect.Width + dx, 1, width - rect.X), Height = Math.Clamp(rect.Height + dy, 1, height - rect.Y) }
      : rect with { X = Math.Clamp(rect.X + dx, 0, width - rect.Width), Y = Math.Clamp(rect.Y + dy, 0, height - rect.Height) };
    if (next == rect) return;
    Remember(rect);
    Selection = next;
  }

  public void Undo()
  {
    if (!CanUndo) return;
    _redo.Add(Selection);
    Selection = _undo[^1];
    _undo.RemoveAt(_undo.Count - 1);
  }

  public void Redo()
  {
    if (!CanRedo) return;
    _undo.Add(Selection);
    Selection = _redo[^1];
    _redo.RemoveAt(_redo.Count - 1);
  }

  public void SetPreview(IReadOnlyList<PhysicalRectangle> candidates)
  {
    if (ManualOnly || IsPointerActive || Selection is not null) return;
    _preview = candidates.ToArray();
    _previewIndex = Math.Min(_previewIndex, Math.Max(0, _preview.Count - 1));
  }

  public void CyclePreview()
  {
    if (IsPointerActive || Selection is not null || _preview.Count == 0) return;
    _previewIndex = (_previewIndex + 1) % _preview.Count;
  }

  public void ClearPreview()
  {
    _preview = [];
    _previewIndex = 0;
  }

  public void ReturnToManual()
  {
    ManualOnly = true;
    ClearPreview();
  }

  private void Remember(PhysicalRectangle? previous)
  {
    if (_undo.Count == 64) _undo.RemoveAt(0);
    _undo.Add(previous);
    _redo.Clear();
  }

  private static bool Contains(PhysicalRectangle rect, PhysicalPoint point) =>
    point.X >= rect.X && point.X < rect.Right && point.Y >= rect.Y && point.Y < rect.Bottom;
  private LogicalPoint ToDip(PhysicalPoint point) => new(point.X / _pixelsPerDipX, point.Y / _pixelsPerDipY);
  private PhysicalPoint Clamp(PhysicalPoint point) => new(Math.Clamp(point.X, 0, width), Math.Clamp(point.Y, 0, height));
}

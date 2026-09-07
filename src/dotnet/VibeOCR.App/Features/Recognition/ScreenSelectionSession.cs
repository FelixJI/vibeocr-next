using VibeOCR.Platform.Windows;

namespace VibeOCR.App.Features.Recognition;

/// <summary>Physical-pixel selection history, independent of the overlay and OCR submission.</summary>
internal sealed class ScreenSelectionSession(int width, int height)
{
  private readonly List<PhysicalRectangle?> _undo = [];
  private readonly List<PhysicalRectangle?> _redo = [];
  private PhysicalRectangle? _beforeDrag;
  private PhysicalPoint _origin;
  private int _edges;
  private bool _moving;
  public PhysicalRectangle? Selection { get; private set; }
  public bool IsDragging { get; private set; }
  public bool CanConfirm => !IsDragging && Selection is not null;
  public bool CanUndo => !IsDragging && _undo.Count > 0;
  public bool CanRedo => !IsDragging && _redo.Count > 0;

  public void Begin(PhysicalPoint point, int tolerance)
  {
    if (IsDragging) CancelDrag();
    _origin = Clamp(point);
    _beforeDrag = Selection;
    _edges = 0;
    _moving = false;
    if (Selection is { } rect && point.X >= rect.X - tolerance && point.X <= rect.Right + tolerance &&
        point.Y >= rect.Y - tolerance && point.Y <= rect.Bottom + tolerance)
    {
      if (Math.Abs(point.X - rect.X) <= tolerance) _edges |= 1;
      else if (Math.Abs(point.X - rect.Right) <= tolerance) _edges |= 2;
      if (Math.Abs(point.Y - rect.Y) <= tolerance) _edges |= 4;
      else if (Math.Abs(point.Y - rect.Bottom) <= tolerance) _edges |= 8;
      _moving = _edges == 0;
    }
    else Selection = null;
    IsDragging = true;
  }

  public void Move(PhysicalPoint point)
  {
    if (!IsDragging) return;
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
    else if (_beforeDrag is { } previous && _edges != 0)
    {
      int left = (_edges & 1) != 0 ? Math.Clamp(previous.X + dx, 0, previous.Right - 1) : previous.X;
      int right = (_edges & 2) != 0 ? Math.Clamp(previous.Right + dx, previous.X + 1, width) : previous.Right;
      int top = (_edges & 4) != 0 ? Math.Clamp(previous.Y + dy, 0, previous.Bottom - 1) : previous.Y;
      int bottom = (_edges & 8) != 0 ? Math.Clamp(previous.Bottom + dy, previous.Y + 1, height) : previous.Bottom;
      Selection = new(left, top, right - left, bottom - top);
    }
    else
    {
      int w = Math.Abs(point.X - _origin.X);
      int h = Math.Abs(point.Y - _origin.Y);
      Selection = w > 0 && h > 0 ? new(Math.Min(point.X, _origin.X), Math.Min(point.Y, _origin.Y), w, h) : null;
    }
  }

  public void End(PhysicalPoint point)
  {
    if (!IsDragging) return;
    Move(point);
    IsDragging = false;
    if (Selection != _beforeDrag) Remember(_beforeDrag);
  }

  public void CancelDrag()
  {
    if (!IsDragging) return;
    Selection = _beforeDrag;
    IsDragging = false;
  }

  // Returns true only when the caller should exit the screenshot session.
  public bool Back()
  {
    if (IsDragging) { CancelDrag(); return false; }
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

  private void Remember(PhysicalRectangle? previous)
  {
    if (_undo.Count == 64) _undo.RemoveAt(0);
    _undo.Add(previous);
    _redo.Clear();
  }

  private PhysicalPoint Clamp(PhysicalPoint point) => new(Math.Clamp(point.X, 0, width), Math.Clamp(point.Y, 0, height));
}

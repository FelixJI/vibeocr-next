using System.Runtime.InteropServices;
using VibeOCR.Platform.Windows;

namespace VibeOCR.App.Features.Recognition;

/// <summary>
/// 滚动输入边界策略：XAML 输入管线不保证处理直接 SendMessage 的 WM_MOUSEWHEEL，
/// 统一使用系统鼠标输入。只在定位前和定位后均通过归属/按键检查时发送，
/// 且不覆盖用户在此期间的光标移动。从采集会话状态机分离出的纯输入决策，
/// 不持有会话/终态/线程状态，可用合成输入独立自检。
/// </summary>
internal static class ScrollCaptureScrollInput
{
  private const uint GaRoot = 2;

  internal static bool TrySend(
      IScrollInput input, Point center, nint source, ScrollCaptureDirection direction, Func<bool>? stillRequired = null)
  {
    bool CanSend() => input.IsSourceAt(center, source) &&
        !new[] { 0x01, 0x02, 0x04, 0x05, 0x06, 0x10, 0x11, 0x12 }.Any(input.IsKeyDown) &&
        stillRequired?.Invoke() != false;
    if (!CanSend() || !input.TryGetCursor(out Point original)) return false;
    try
    {
      if (!input.TrySetCursor(center) || !input.TryGetCursor(out Point positioned) ||
          positioned.X != center.X || positioned.Y != center.Y || !CanSend()) return false;
      bool horizontal = direction is ScrollCaptureDirection.Left or ScrollCaptureDirection.Right;
      int delta = direction is ScrollCaptureDirection.Down or ScrollCaptureDirection.Left ? -40 : 40;
      return input.SendWheel(horizontal, delta);
    }
    finally
    {
      if (input.TryGetCursor(out Point current) && current.X == center.X && current.Y == center.Y)
        input.TrySetCursor(original);
    }
  }

  internal interface IScrollInput
  {
    bool IsSourceAt(Point center, nint source);
    bool IsKeyDown(int key);
    bool TryGetCursor(out Point point);
    bool TrySetCursor(Point point);
    bool SendWheel(bool horizontal, int delta);
  }

  internal sealed class NativeScrollInput : IScrollInput
  {
    public bool IsSourceAt(Point center, nint source)
    {
      nint target = WindowFromPoint(center);
      return target != 0 && GetAncestor(target, GaRoot) == source && GetForegroundWindow() == source;
    }
    public bool IsKeyDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    public bool TryGetCursor(out Point point) => GetCursorPos(out point);
    public bool TrySetCursor(Point point) => SetCursorPos(point.X, point.Y);
    public bool SendWheel(bool horizontal, int delta)
    {
      var input = new MouseInput
      {
        Data = unchecked((uint)delta),
        Flags = horizontal ? 0x1000u : 0x0800u, // MOUSEEVENTF_HWHEEL / MOUSEEVENTF_WHEEL
      };
      return SendInput(1, [new Input { Mouse = input }], Marshal.SizeOf<Input>()) == 1;
    }
  }

  [StructLayout(LayoutKind.Sequential)]
  internal struct Input
  {
    public uint Type; // INPUT_MOUSE = 0; Mouse 是 INPUT 联合体中最大的成员。
    public MouseInput Mouse;
  }

  [StructLayout(LayoutKind.Sequential)]
  internal struct MouseInput
  {
    public int X, Y;
    public uint Data, Flags, Time;
    public nuint ExtraInfo;
  }

  // 不触碰真实桌面的输入边界自检（合成输入夹具），与生产共用同一条 TrySend 路径。
  internal static void VerifyInputBoundaries()
  {
    static void Check(bool condition, string message)
    {
      if (!condition) throw new InvalidOperationException(message);
    }
    var center = new Point { X = -80, Y = 120 };
    foreach (ScrollCaptureDirection direction in Enum.GetValues<ScrollCaptureDirection>())
    {
      var input = new SyntheticScrollInput();
      Check(TrySend(input, center, 1, direction), "Allowed system wheel must be sent.");
      Check(input.Horizontal == (direction is ScrollCaptureDirection.Left or ScrollCaptureDirection.Right) &&
          input.Delta == (direction is ScrollCaptureDirection.Down or ScrollCaptureDirection.Left ? -40 : 40),
          "Wheel flags and signs must follow the selected direction.");
      Check(input.Position.X == 12 && input.Position.Y == 34, "Cursor must restore after accepted input.");
    }
    foreach (int key in new[] { 0x01, 0x02, 0x04, 0x05, 0x06, 0x10, 0x11, 0x12 })
    {
      var input = new SyntheticScrollInput { PressedKey = key };
      Check(!TrySend(input, center, 1, ScrollCaptureDirection.Down) && input.Sends == 0 && input.Moves == 0,
          "Mouse buttons and wheel modifiers must reject input before moving the cursor.");
    }
    var refused = new SyntheticScrollInput { AcceptWheel = false };
    Check(!TrySend(refused, center, 1, ScrollCaptureDirection.Down) && refused.Sends == 1 &&
        refused.Position.X == 12 && refused.Position.Y == 34, "Rejected injection must restore the cursor.");
    var switched = new SyntheticScrollInput { RejectAfterMove = true };
    Check(!TrySend(switched, center, 1, ScrollCaptureDirection.Down) && switched.Sends == 0 &&
        switched.Position.X == 12, "Source change after positioning must prevent input and restore the cursor.");
    var clipped = new SyntheticScrollInput { ClipCursor = true };
    Check(!TrySend(clipped, center, 1, ScrollCaptureDirection.Down) && clipped.Sends == 0,
        "Clipped cursor positioning must never send wheel input at the wrong location.");
    var moved = new SyntheticScrollInput { UserMoves = true };
    Check(TrySend(moved, center, 1, ScrollCaptureDirection.Down) &&
        moved.Position.X == 56 && moved.Position.Y == 78, "User cursor movement must never be overwritten.");
  }

  internal sealed class SyntheticScrollInput : IScrollInput
  {
    public Point Position = new() { X = 12, Y = 34 };
    public int PressedKey, Moves, Sends, Delta;
    public bool Horizontal, RejectAfterMove, UserMoves, ClipCursor;
    public bool AcceptWheel = true;
    public bool IsSourceAt(Point center, nint source) => !RejectAfterMove || Moves == 0;
    public bool IsKeyDown(int key) => key == PressedKey;
    public bool TryGetCursor(out Point point) { point = Position; return true; }
    public bool TrySetCursor(Point point)
    {
      Position = ClipCursor ? new Point { X = point.X + 1, Y = point.Y } : point;
      Moves++;
      return true;
    }
    public bool SendWheel(bool horizontal, int delta)
    {
      Sends++; Horizontal = horizontal; Delta = delta;
      if (UserMoves) Position = new Point { X = 56, Y = 78 };
      return AcceptWheel;
    }
  }

  [StructLayout(LayoutKind.Sequential)]
  internal struct Point
  {
    public int X, Y;
  }

  [DllImport("user32.dll")]
  private static extern nint WindowFromPoint(Point point);

  [DllImport("user32.dll")]
  private static extern nint GetAncestor(nint window, uint flags);

  [DllImport("user32.dll", SetLastError = true)]
  private static extern uint SendInput(uint count, Input[] inputs, int size);

  [DllImport("user32.dll")]
  private static extern short GetAsyncKeyState(int key);

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool GetCursorPos(out Point point);

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool SetCursorPos(int x, int y);

  [DllImport("user32.dll")]
  private static extern nint GetForegroundWindow();
}

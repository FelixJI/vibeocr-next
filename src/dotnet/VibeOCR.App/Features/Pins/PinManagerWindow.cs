using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VibeOCR.App.Features.Recognition;

namespace VibeOCR.App.Features.Pins;

/// <summary>
/// #238 贴图管理窗（原生辅助入口）：穿透中的贴图无法被点击，此处是
/// 始终可达的恢复/管理面——恢复可点击、展开、进入编辑、退出编组与关闭。
/// 列表内容按需刷新（打开时与每次操作后）。
/// </summary>
internal sealed class PinManagerWindow
{
  private readonly Window window = new();
  private readonly ScrollViewer scroller = new();
  private readonly Func<IReadOnlyList<PinnedImageWindow>> pins;
  private readonly Func<IReadOnlyList<TypedPinWindow>> typedPins;
  private readonly Action<PinManagerWindow> refreshRequested;
  private readonly Action<TypedPinWindow.ContentKind> create;
  private readonly Action<PinnedImageWindow> leaveGroup;

  internal nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(window);

  public PinManagerWindow(
    Func<IReadOnlyList<PinnedImageWindow>> pins,
    Action<PinManagerWindow> refreshRequested, Action<TypedPinWindow.ContentKind> create, Action<PinnedImageWindow> leaveGroup,
    Func<IReadOnlyList<TypedPinWindow>> typedPins)
  {
    this.pins = pins;
    this.typedPins = typedPins;
    this.create = create;
    this.leaveGroup = leaveGroup;
    this.refreshRequested = refreshRequested;
    window.Title = "VibeOCR 贴图管理";
    window.AppWindow.Resize(new Windows.Graphics.SizeInt32(420, 360));
    window.Content = scroller;
    window.Closed += (_, _) => Closed?.Invoke(this);
  }

  public event Action<PinManagerWindow>? Closed;

  public void Show()
  {
    Render();
    window.Activate();
  }

  public void ForceClose() => window.Close();

  public void Refresh()
  {
    if (window.Content is null) return;
    Render();
  }

  private void Render()
  {
    var panel = new StackPanel { Spacing = 6, Padding = new Thickness(12) };
    panel.Children.Add(new TextBlock
    {
      Text = "穿透中的贴图可在此恢复可点击；管理操作不改变贴图内容。",
      TextWrapping = TextWrapping.Wrap,
      Opacity = 0.8,
      FontSize = 12,
    });
    var creations = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
    AddAction(creations, "文字", true, () => create(TypedPinWindow.ContentKind.Text));
    AddAction(creations, "颜色", true, () => create(TypedPinWindow.ContentKind.Color));
    AddAction(creations, "公式", true, () => create(TypedPinWindow.ContentKind.Formula));
    AddAction(creations, "文件", true, () => create(TypedPinWindow.ContentKind.FilePreview));
    panel.Children.Add(creations);
    IReadOnlyList<PinnedImageWindow> current = pins();
    if (current.Count == 0 && typedPins().Count == 0)
    {
      panel.Children.Add(new TextBlock { Text = "当前没有打开的贴图。", Opacity = 0.6 });
    }
    foreach (PinnedImageWindow pin in current)
    {
      panel.Children.Add(BuildRow(pin));
    }
    foreach (TypedPinWindow typed in typedPins())
    {
      var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
      row.Children.Add(new TextBlock { Text = typed.Kind switch
      { TypedPinWindow.ContentKind.Text => "文字贴图", TypedPinWindow.ContentKind.Color => "颜色贴图",
        TypedPinWindow.ContentKind.Formula => "公式贴图", _ => "文件预览贴图" },
        VerticalAlignment = VerticalAlignment.Center });
      AddAction(row, "激活", true, typed.Show);
      AddAction(row, "关闭", true, typed.ForceClose);
      panel.Children.Add(row);
    }
    scroller.Content = panel;
  }

  private Border BuildRow(PinnedImageWindow pin)
  {
    var row = new StackPanel
    {
      Orientation = Orientation.Vertical,
      Spacing = 6,
      Tag = pin,
    };
    var info = new TextBlock
    {
      Text = $"贴图 {pin.PinId.ToString("N")[..8]} · " +
        (pin.Editing ? "编辑中" : pin.Collapsed ? "已折叠" : "展开") +
        (pin.PassThrough ? " · 穿透" : "") +
        (pin.Grouped ? " · 编组" : ""),
      VerticalAlignment = VerticalAlignment.Center,
      Margin = new Thickness(0, 0, 8, 0),
    };
    row.Children.Add(info);
    var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
    row.Children.Add(actions);
    AddAction(actions, "恢复可点击", pin.PassThrough, () => pin.SetPassThrough(false));
    AddAction(actions, pin.Collapsed ? "展开" : "折叠", true, () =>
      pin.SetCollapsed(!pin.Collapsed));
    AddAction(actions, "退出编组", pin.Grouped, () => leaveGroup(pin));
    AddAction(actions, "关闭", true, () => pin.Close());
    return new Border
    {
      Child = row,
      Padding = new Thickness(0, 4, 0, 4),
      BorderThickness = new Thickness(0, 0, 0, 1),
      BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
        Microsoft.UI.Colors.LightGray),
    };
  }

  private void AddAction(StackPanel row, string label, bool enabled, Action action)
  {
    var button = new Button
    {
      Content = label,
      IsEnabled = enabled,
      FontSize = 12,
    };
    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
    button.Click += (_, _) =>
    {
      try
      {
        action();
      }
      finally
      {
        refreshRequested(this);
      }
    };
    row.Children.Add(button);
  }
}

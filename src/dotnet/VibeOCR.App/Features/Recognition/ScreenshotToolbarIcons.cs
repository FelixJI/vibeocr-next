using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using IconPath = Microsoft.UI.Xaml.Shapes.Path;

namespace VibeOCR.App.Features.Recognition;

// 与 Web 共用路径、线宽及效果图层，固定 24px 画布。
internal static partial class ScreenshotToolbarIcons
{
  internal const string DefaultKey = "textRecognition";

  internal static IEnumerable<string> Keys => Paths.Keys;

  internal static string? TryGetPath(string key) =>
    Paths.TryGetValue(key, out string? value) ? value : null;

  internal static bool HasIcon(string key) => Paths.ContainsKey(key);

  internal static ContentControl CreateIcon(string key)
  {
    string path = Paths.TryGetValue(key, out string? value) ? value : Paths[DefaultKey];
    var canvas = new Canvas { Width = IconSize, Height = IconSize };
    var icon = new ContentControl
    {
      Width = IconSize,
      Height = IconSize,
      IsTabStop = false,
      IsHitTestVisible = false,
      Padding = new Thickness(0),
      Content = canvas,
    };
    if (EffectLayers.TryGetValue(key, out IconLayer[]? layers))
    {
      foreach (IconLayer layer in layers)
      {
        IconPath geometry = CreatePath(layer.Path);
        geometry.Opacity = layer.Opacity;
        if (layer.Stroke)
        {
          geometry.StrokeThickness = StrokeWidth;
          geometry.StrokeStartLineCap = PenLineCap.Round;
          geometry.StrokeEndLineCap = PenLineCap.Round;
          geometry.StrokeLineJoin = PenLineJoin.Round;
          if (layer.Dashed)
          {
            geometry.StrokeDashCap = PenLineCap.Round;
            geometry.StrokeDashArray = new DoubleCollection { 0.5 / StrokeWidth, 2.5 / StrokeWidth };
          }
        }
        geometry.SetBinding(layer.Stroke ? IconPath.StrokeProperty : IconPath.FillProperty,
          new Binding { Source = icon, Path = new PropertyPath(nameof(icon.Foreground)) });
        canvas.Children.Add(geometry);
      }
    }
    else
    {
      IconPath geometry = CreatePath(path);
      geometry.StrokeThickness = StrokeWidth;
      geometry.StrokeStartLineCap = PenLineCap.Round;
      geometry.StrokeEndLineCap = PenLineCap.Round;
      geometry.StrokeLineJoin = PenLineJoin.Round;
      geometry.SetBinding(IconPath.StrokeProperty,
        new Binding { Source = icon, Path = new PropertyPath(nameof(icon.Foreground)) });
      canvas.Children.Add(geometry);
    }
    return icon;
  }

  private static IconPath CreatePath(string path) => (IconPath)XamlReader.Load(
    $"<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data='{path}'/>");
}

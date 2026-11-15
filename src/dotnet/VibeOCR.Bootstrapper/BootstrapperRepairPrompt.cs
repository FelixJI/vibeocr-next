using System;
using System.Runtime.InteropServices;

namespace VibeOCR.Bootstrapper;

internal static class BootstrapperRepairPrompt
{
  internal static string? CreateMessage(
      string reason, bool selfTest, bool artifactSmoke, bool userInteractive) =>
      selfTest || artifactSmoke || !userInteractive
          ? null
          : $"VibeOCR 无法启动，需要修复产品文件。\n\n原因：{reason}\n\n" +
            "请通过应用内更新修复，或重新下载完整便携包。";

  internal static void Show(string message)
  {
    const uint errorInForeground = 0x00000010 | 0x00010000;
    _ = MessageBoxW(IntPtr.Zero, message, "VibeOCR 启动失败", errorInForeground);
  }

  [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
  private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
}

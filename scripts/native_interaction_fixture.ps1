# Task #217 bounded native interaction fixture. This script is parse-checked
# by tests/node/smoke_windows_interaction.test.mjs (PowerShell AST only) and
# executed only by the main agent on an explicitly authorized interactive
# Windows desktop, together with scripts/smoke_windows_interaction.mjs.
# Boundaries (fail-closed, never swallowed):
#   - Read-only system/IME/TIP observation (language list is only read, no
#     user registry writes, no SPI_SETHIGHCONTRAST set action, no default-input overrides).
#   - Thread-local IME switching only: WM_INPUTLANGCHANGEREQUEST posted to the
#     app-owned focused window plus a poll of the foreground thread's active
#     HKL. No global/user-default state is modified. Verified boundary:
#     ITfInputProcessorProfileMgr.ActivateProfile is never used here - its
#     TF_PM_FORPROCESS flag only affects the calling (fixture) process, its
#     TF_PM_FORSESSION flag changes the whole desktop session and
#     TF_PM_ENABLEPROFILE writes the user registry; all out of scope. If the
#     app thread ignores the posted request, activation is reported as
#     evidence_missing and returned to the main agent - never simulated.
#   - Every SendInput action first re-verifies the foreground is the app PID
#     and that the GA_ROOT top-level window of BOTH the real keyboard focus
#     and the probe point belongs to the app (a cross-process WebView child
#     HWND is proven exactly through this ancestry; no caller-supplied PID is
#     trusted as ownership evidence).
#   - No TIP brand attribution: a LANGID/HKL does not identify which IME (the
#     system one or a third-party one) is active; multiple IME layouts for a
#     LANGID stay evidence_missing and only real composition observations
#     decide coverage.
#   - No process kills, no directory deletion, no evidence mutation here.
param(
    [Parameter(Mandatory = $true)][ValidateSet('abi-selfcheck', 'system-state', 'focus-state', 'ime-activate', 'ime-restore', 'app-hotkey', 'type-keys', 'uia-focused', 'uia-point')][string]$Action,
    [int]$AppPid = 0,
    [long]$Hkl = 0,
    [uint32]$LangId = 2052,
    [uint32]$Key = 0,
    [long]$FocusHandle = 0,
    [uint32]$ThreadId = 0,
    [switch]$Ctrl,
    [switch]$Shift,
    [switch]$Alt,
    [int]$X = 0,
    [int]$Y = 0,
    [string]$Text = ''
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public static class InteractionNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        // Documented GUITHREADINFO: cbSize, flags, six HWNDs, rcCaret. There
        // is no trailing hklActive field (x64 size = 72); the active input
        // locale comes from GetKeyboardLayout(threadId) instead.
        public uint Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public Rect CaretRect;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrast { public int Size, Flags; public IntPtr DefaultScheme; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        // Same layout as scripts/native_actions_fixture.ps1: MOUSEINPUT is the
        // largest union member, so its presence keeps INPUT at 40 bytes on
        // x64 even though this fixture only sends key events.
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyInput Key;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public UIntPtr Extra;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyInput
    {
        public ushort Key, Scan;
        public uint Flags, Time;
        public UIntPtr Extra;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyW(uint code, uint type);
    [DllImport("user32.dll")] private static extern int GetKeyboardLayoutList(int count, IntPtr[] layouts);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint size, ref HighContrast data, uint flags);
    [DllImport("imm32.dll")] private static extern bool ImmIsIME(IntPtr layout);

    public const uint WmInputLangChangeRequest = 0x0050;

    // Non-GUI ABI self-check: pure Add-Type + Marshal.SizeOf, safe to run in
    // contract tests. The shipped product is win-x64, so the exact struct
    // sizes are pinned (GUITHREADINFO 72, INPUT 40 on x64).
    public static object AbiSelfCheck()
    {
        if (IntPtr.Size != 8)
            throw new InvalidOperationException($"Expected a 64-bit process, got {IntPtr.Size * 8}-bit.");
        int guiThreadInfoSize = Marshal.SizeOf<GuiThreadInfo>();
        int inputSize = Marshal.SizeOf<Input>();
        if (guiThreadInfoSize != 72)
            throw new InvalidOperationException($"GUITHREADINFO marshals to {guiThreadInfoSize} bytes instead of 72 (x64 ABI drift).");
        if (inputSize != 40)
            throw new InvalidOperationException($"INPUT marshals to {inputSize} bytes instead of 40 (x64 ABI drift; InputUnion must contain MOUSEINPUT).");
        return new { pointerBytes = IntPtr.Size, guiThreadInfoSize, inputSize };
    }

    // Read-only high contrast query (SPI_GETHIGHCONTRAST only; this fixture
    // never contains the corresponding SPI set action).
    public static bool HighContrastEnabled()
    {
        var data = new HighContrast { Size = Marshal.SizeOf<HighContrast>() };
        if (!SystemParametersInfo(0x0042, (uint)data.Size, ref data, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "High contrast observation failed.");
        return (data.Flags & 0x1) != 0;
    }

    public sealed class LayoutInfo
    {
        public long Hkl { get; set; }
        public string Klid { get; set; }
        public uint LangId { get; set; }
        public bool IsIme { get; set; }
    }

    public static LayoutInfo[] Layouts()
    {
        int count = GetKeyboardLayoutList(0, null);
        if (count < 1) throw new InvalidOperationException("No keyboard layouts are loaded.");
        var handles = new IntPtr[count];
        if (GetKeyboardLayoutList(count, handles) != count)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Keyboard layout observation failed.");
        var list = new List<LayoutInfo>();
        // There is no per-HKL GetKeyboardLayoutName export (the public one
        // reports only the calling thread's active layout, and resolving via
        // ActivateKeyboardLayout would be a state change). The KLID is
        // therefore derived from the documented HKL layout: low word =
        // language id, high word = device id (e.g. HKL 0x08040804 ->
        // "00000804"), and stays null when the word is absent. The KLID is
        // recorded without any TIP brand attribution.
        foreach (var handle in handles)
        {
            uint device = (uint)((((ulong)handle) >> 16) & 0xFFFF);
            list.Add(new LayoutInfo
            {
                Hkl = handle.ToInt64(),
                Klid = device == 0 ? null : $"0000{device:X4}",
                LangId = (uint)(((ulong)handle) & 0xFFFF),
                IsIme = ImmIsIME(handle),
            });
        }
        return list.ToArray();
    }

    public sealed class FocusSnapshot
    {
        public long FocusHandle { get; set; }
        public uint FocusThreadId { get; set; }
        public long FocusRoot { get; set; }
        public uint FocusRootPid { get; set; }
        public long PointRoot { get; set; }
        public uint PointRootPid { get; set; }
        public long ActiveHkl { get; set; }
        public uint ActiveLangId { get; set; }
        public long ForegroundHandle { get; set; }
        public uint ForegroundPid { get; set; }
    }

    // Ownership proof through the window ancestry: any HWND whose GA_ROOT
    // top-level window belongs to appPid is inside the app's owned window
    // tree (a cross-process WebView child HWND is accepted exactly this way;
    // no caller-supplied PID is ever trusted as evidence on its own).
    public static bool HandleRootOwnedByApp(long handle, int appPid)
    {
        if (appPid <= 0) return false;
        IntPtr window = new IntPtr(handle);
        if (window == IntPtr.Zero) return false;
        GetWindowThreadProcessId(GetAncestor(window, 2), out uint rootPid);
        return rootPid == (uint)appPid;
    }

    // Read-only guarded snapshot. Throws fail-closed when the foreground, or
    // the GA_ROOT of either the real keyboard focus or the probe point, is
    // outside this smoke run; foreign windows are never reported as owned.
    public static FocusSnapshot FocusState(int appPid, int x, int y)
    {
        if (appPid <= 0) throw new InvalidOperationException("AppPid ownership is required.");
        var snapshot = new FocusSnapshot();
        snapshot.ForegroundHandle = GetForegroundWindow().ToInt64();
        GetWindowThreadProcessId(new IntPtr(snapshot.ForegroundHandle), out uint foregroundPid);
        snapshot.ForegroundPid = foregroundPid;
        if (foregroundPid != (uint)appPid)
            throw new InvalidOperationException("Foreground window is outside this smoke run.");
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(0, ref info) || info.Focus == IntPtr.Zero)
            throw new InvalidOperationException("Keyboard focus is unavailable on the foreground thread.");
        snapshot.FocusHandle = info.Focus.ToInt64();
        uint focusThreadId = GetWindowThreadProcessId(info.Focus, out _);
        snapshot.FocusThreadId = focusThreadId;
        // The documented GUITHREADINFO has no hklActive member; the active
        // input locale of the focused window's thread comes from GetKeyboardLayout.
        snapshot.ActiveHkl = GetKeyboardLayout(focusThreadId).ToInt64();
        snapshot.ActiveLangId = (uint)(((ulong)snapshot.ActiveHkl) & 0xFFFF);
        IntPtr focusRoot = GetAncestor(info.Focus, 2);
        GetWindowThreadProcessId(focusRoot, out uint focusRootPid);
        snapshot.FocusRoot = focusRoot.ToInt64();
        snapshot.FocusRootPid = focusRootPid;
        IntPtr pointRoot = GetAncestor(WindowFromPoint(new Point { X = x, Y = y }), 2);
        GetWindowThreadProcessId(pointRoot, out uint pointRootPid);
        snapshot.PointRoot = pointRoot.ToInt64();
        snapshot.PointRootPid = pointRootPid;
        if (focusRootPid != (uint)appPid)
            throw new InvalidOperationException("Keyboard focus is not on the app or its owned WebView.");
        if (pointRootPid != (uint)appPid)
            throw new InvalidOperationException($"Guard point ({x},{y}) root={pointRoot.ToInt64()} pid={pointRootPid} is not app={appPid}.");
        return snapshot;
    }

    // Thread-local input locale request. Only the owned focused window (and
    // its owned root) receives WM_INPUTLANGCHANGEREQUEST; the user's default
    // input profile is never modified. Returns the observed activation.
    public static object ImeActivate(int appPid, int x, int y, uint langId)
    {
        var before = FocusState(appPid, x, y);
        if (before.ActiveHkl != 0 && ((uint)((ulong)before.ActiveHkl & 0xFFFF)) == langId)
            return new { status = "already_active",
                reason = (string)null,
                note = "Target input locale is already the foreground thread's active HKL; real SendInput/composition observation may proceed without switching.",
                before = before.ActiveHkl };
        LayoutInfo candidate = null;
        var observed = new List<string>();
        foreach (var layout in Layouts())
        {
            if (layout.LangId != langId || !layout.IsIme) continue;
            observed.Add($"{layout.Hkl:X}:{layout.Klid ?? "unknown"}");
            candidate = layout;
        }
        // No brand attribution: a LANGID (e.g. 0x0804) does not identify which
        // TIP (e.g. the system Chinese IME or a third-party one) a KLID maps
        // to; the real composition evidence decides coverage, not names.
        if (observed.Count == 0)
            return new { status = "evidence_missing",
                reason = $"No loaded IME keyboard layout for LANGID {langId}; loaded layouts: {Layouts().Length}.",
                candidates = observed.ToArray(), before = before.ActiveHkl };
        if (observed.Count > 1)
            return new { status = "evidence_missing",
                reason = "Multiple IME keyboard layouts are loaded for the target LANGID and the active TIP cannot be attributed from the HKL alone: " +
                    string.Join(",", observed),
                candidates = observed.ToArray(), before = before.ActiveHkl };
        IntPtr target = new IntPtr(candidate.Hkl);
        if (!PostMessage(new IntPtr(before.FocusHandle), WmInputLangChangeRequest, IntPtr.Zero, target) &&
            !PostMessage(new IntPtr(before.FocusRoot), WmInputLangChangeRequest, IntPtr.Zero, target))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Owned input locale request could not be posted.");
        var until = DateTime.UtcNow.AddSeconds(4);
        long after = before.ActiveHkl;
        while (DateTime.UtcNow < until)
        {
            System.Threading.Thread.Sleep(50);
            var current = FocusState(appPid, x, y);
            after = current.ActiveHkl;
            if (((uint)((ulong)after & 0xFFFF)) == langId) break;
        }
        bool activated = ((uint)((ulong)after & 0xFFFF)) == langId;
        return new {
            status = activated ? "activated" : "evidence_missing",
            reason = activated ? (string)null :
                "WM_INPUTLANGCHANGEREQUEST to the owned focused window did not change the foreground thread's active HKL (WebView2/WinUI may ignore it). Verified MS docs boundary: ITfInputProcessorProfileMgr.ActivateProfile TF_PM_FORPROCESS only affects the calling fixture process, TF_PM_FORSESSION changes the whole desktop session and TF_PM_ENABLEPROFILE writes the user registry - none is usable from this fixture. Return to the main agent for an in-process TSF seam or an authorized session-level switch.",
            before = before.ActiveHkl, after, requestedHkl = candidate.Hkl, requestedKlid = candidate.Klid,
        };
    }

    // Restore bound to the ORIGINAL focused window/thread captured at
    // activation time. A missing window does not prove thread termination.
    // No external default or session-wide setting is touched.
    public static object ImeRestore(int appPid, int x, int y, long originalHkl,
        long originalFocusHandle, uint originalThreadId)
    {
        if (originalHkl == 0)
            return new { restored = false, terminated = false,
                reason = "No original HKL was captured." };
        IntPtr window = new IntPtr(originalFocusHandle);
        if (originalThreadId == 0 || window == IntPtr.Zero)
            return new { restored = false, terminated = false,
                reason = "No original focus window/thread was captured." };
        if (!IsWindow(window))
            return new { restored = false, terminated = false,
                reason = "Original focus window is gone; restoring the original thread locale is unproven." };
        uint thread = GetWindowThreadProcessId(window, out _);
        if (!HandleRootOwnedByApp(originalFocusHandle, appPid) || thread != originalThreadId)
            return new { restored = false, terminated = false,
                reason = "Original window/thread ownership changed; per-thread restoration is unproven." };
        long current = GetKeyboardLayout(thread).ToInt64();
        if (current == originalHkl)
            return new { restored = true, terminated = false, after = current };
        PostMessage(window, WmInputLangChangeRequest, IntPtr.Zero, new IntPtr(originalHkl));
        var until = DateTime.UtcNow.AddSeconds(3);
        long after = current;
        while (DateTime.UtcNow < until)
        {
            System.Threading.Thread.Sleep(50);
            after = GetKeyboardLayout(thread).ToInt64();
            if (after == originalHkl) break;
        }
        return new { restored = after == originalHkl, terminated = false, after };
    }

    private static void Send(Input[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Synthetic input was not fully delivered.");
    }

    private static Input Key(ushort key, bool up) => new Input
    {
        Type = 1,
        Data = new InputUnion { Key = new KeyInput
        {
            Scan = (ushort)MapVirtualKeyW(key, 0),
            Flags = 0x0008u | (up ? 2u : 0u),
        } },
    };

    // Real guarded SendInput hotkey. The focus-ownership guard runs first and
    // throws; guard errors are never swallowed into a missing-evidence state.
    public static void AppHotkey(int appPid, int x, int y, uint key, bool ctrl, bool shift, bool alt)
    {
        FocusState(appPid, x, y);
        if (key < 0x08 || key > 0xFE) throw new ArgumentOutOfRangeException(nameof(key));
        var order = new List<ushort>();
        if (ctrl) order.Add(0x11);
        if (shift) order.Add(0x10);
        if (alt) order.Add(0x12);
        var down = new List<Input>();
        foreach (var modifier in order) down.Add(Key(modifier, false));
        down.Add(Key((ushort)key, false));
        try { Send(down.ToArray()); }
        finally
        {
            var up = new List<Input> { Key((ushort)key, true) };
            for (int index = order.Count - 1; index >= 0; index--) up.Add(Key(order[index], true));
            Send(up.ToArray());
        }
    }

    // Real guarded SendInput keystrokes for IME pinyin input. The focus
    // ownership guard re-runs before EVERY keydown (focus can be stolen
    // between characters); each release always runs in finally. Only plain
    // lowercase/digit/space characters are allowed so every key travels the
    // real OS keyboard path through the active IME (unicode injection would
    // bypass the IME and is never used here).
    public static void TypeKeys(int appPid, int x, int y, string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 64)
            throw new InvalidOperationException("Type text must be 1..64 characters.");
        foreach (var letter in text)
        {
            ushort key;
            if (letter >= 'a' && letter <= 'z') key = (ushort)(0x41 + (letter - 'a'));
            else if (letter >= '0' && letter <= '9') key = (ushort)(0x30 + (letter - '0'));
            else if (letter == ' ') key = 0x20;
            else throw new InvalidOperationException($"Character '{letter}' is outside the allowed pinyin set.");
            FocusState(appPid, x, y); // per-keydown ownership re-verification
            try { Send(new[] { Key(key, false) }); }
            finally { Send(new[] { Key(key, true) }); }
            System.Threading.Thread.Sleep(30);
        }
    }
}
'@

function Convert-ToObject($value) {
    # Minimal projection so anonymous C# objects serialize predictably as
    # camelCase JSON keys consumed by scripts/smoke_windows_interaction.mjs.
    $out = [ordered]@{}
    foreach ($property in $value.GetType().GetProperties()) {
        $key = $property.Name.Substring(0, 1).ToLower() + $property.Name.Substring(1)
        $out[$key] = $property.GetValue($value)
    }
    $out
}

# Fail fast on any x64 ABI drift before any action touches the real system.
[void][InteractionNative]::AbiSelfCheck()

# Ownership proof for UIA elements: walk the control-view ancestry until an
# element with a native HWND is found, then require that HWND's GA_ROOT to be
# an app-owned top-level window. A raw ProcessId equality is never trusted:
# a Chromium accessibility element may report the webview child process.
function Assert-UiaElementOwned($element, $appPid) {
    $node = $element
    for ($depth = 0; $depth -lt 16 -and $null -ne $node; $depth++) {
        $handle = $node.Current.NativeWindowHandle
        if ($handle -ne 0) {
            if (-not [InteractionNative]::HandleRootOwnedByApp([long]$handle, $appPid)) {
                throw "UIA element window root is outside this smoke run (app=$appPid)."
            }
            return
        }
        $node = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParentElement($node)
    }
    throw "UIA element ownership could not be proven through an app-owned window root (app=$appPid)."
}

# Coordinates are physical pixels, matching native_actions_fixture.ps1.
$oldDpi = [InteractionNative]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
if ($oldDpi -eq [IntPtr]::Zero) { throw 'Per Monitor V2 native control context unavailable' }
try {
switch ($Action) {
    'abi-selfcheck' {
        $result = [InteractionNative]::AbiSelfCheck()
        Convert-ToObject $result | ConvertTo-Json -Compress
    }
    'system-state' {
        # Read-only system observation; nothing is applied or changed.
        $layouts = @([InteractionNative]::Layouts() | ForEach-Object {
            [ordered]@{ hkl = $_.Hkl; klid = $_.Klid; langId = $_.LangId; isIme = $_.IsIme }
        })
        $personalize = Get-ItemProperty -Path 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize' -ErrorAction SilentlyContinue
        $languageList = Get-WinUserLanguageList -ErrorAction SilentlyContinue
        $tips = @()
        if ($languageList) {
            foreach ($language in $languageList) {
                foreach ($tip in $language.InputMethodTips) {
                    $tips += [ordered]@{ tag = $language.LanguageTag; tip = $tip }
                }
            }
        }
        @{
            highContrast = [InteractionNative]::HighContrastEnabled()
            appsUseLightTheme = $personalize.AppsUseLightTheme
            systemUsesLightTheme = $personalize.SystemUsesLightTheme
            layouts = $layouts
            inputMethodTips = $tips
            tipsReadError = if ($languageList) { $null } else { 'Get-WinUserLanguageList unavailable' }
        } | ConvertTo-Json -Compress -Depth 4
    }
    'focus-state' {
        $result = [InteractionNative]::FocusState($AppPid, $X, $Y)
        Convert-ToObject $result | ConvertTo-Json -Compress -Depth 3
    }
    'ime-activate' {
        $result = [InteractionNative]::ImeActivate($AppPid, $X, $Y, $LangId)
        Convert-ToObject $result | ConvertTo-Json -Compress -Depth 3
    }
    'ime-restore' {
        $result = [InteractionNative]::ImeRestore($AppPid, $X, $Y, $Hkl, $FocusHandle, $ThreadId)
        Convert-ToObject $result | ConvertTo-Json -Compress -Depth 3
    }
    'app-hotkey' {
        [InteractionNative]::AppHotkey($AppPid, $X, $Y, $Key,
            $Ctrl.IsPresent, $Shift.IsPresent, $Alt.IsPresent)
    }
    'type-keys' { [InteractionNative]::TypeKeys($AppPid, $X, $Y, $Text) }
    'uia-focused' {
        Add-Type -AssemblyName UIAutomationClient
        Add-Type -AssemblyName UIAutomationTypes
        $null = [InteractionNative]::FocusState($AppPid, $X, $Y)
        $element = [System.Windows.Automation.AutomationElement]::FocusedElement
        if ($null -eq $element) { throw 'UIA focused element is unavailable.' }
        Assert-UiaElementOwned $element $AppPid
        @{
            name = $element.Current.Name
            automationId = $element.Current.AutomationId
            className = $element.Current.ClassName
            controlType = $element.Current.ControlType.ProgrammaticName
            processId = $element.Current.ProcessId
            hasKeyboardFocus = $element.Current.HasKeyboardFocus
            isKeyboardFocusable = $element.Current.IsKeyboardFocusable
        } | ConvertTo-Json -Compress -Depth 3
    }
    'uia-point' {
        Add-Type -AssemblyName UIAutomationClient
        Add-Type -AssemblyName UIAutomationTypes
        $element = [System.Windows.Automation.AutomationElement]::FromPoint(
            [System.Windows.Point]::new($X, $Y))
        if ($null -eq $element) { throw 'UIA point element is unavailable.' }
        Assert-UiaElementOwned $element $AppPid
        @{
            name = $element.Current.Name
            automationId = $element.Current.AutomationId
            className = $element.Current.ClassName
            controlType = $element.Current.ControlType.ProgrammaticName
            processId = $element.Current.ProcessId
            isOffscreen = $element.Current.IsOffscreen
        } | ConvertTo-Json -Compress -Depth 3
    }
}

} finally {
    [void][InteractionNative]::SetThreadDpiAwarenessContext($oldDpi)
}

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using VibeOCR.App.Features.Recognition;
using VibeOCR.App.Features.Batch;
using VibeOCR.App.Features.FloatingToolbar;
using VibeOCR.App.Features.Pdf;
using VibeOCR.App.Features.QrCode;
using VibeOCR.App.Features.Settings;
using VibeOCR.App.Features.Maintenance;
using VibeOCR.App.Inference;
using VibeOCR.App.Features.Shell;
using VibeOCR.App.Features.Update;
using VibeOCR.App.Services;
using VibeOCR.App.ViewModels;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Migration;
using VibeOCR.Platform.Inference;
using VibeOCR.Platform.Windows;

namespace VibeOCR.App;

public sealed partial class App : Application
{
    private readonly Stopwatch _startup = Stopwatch.StartNew();
    /// <summary>
    /// v2 supervisor client (deferred until the supervisor process is started).
    /// Attached after the Supervisor process reports a v2 ready envelope.
    /// </summary>
    private readonly DeferredInferenceClient _inferenceGateway;
    private readonly DeferredQrCodeClient _qrCodeGateway;
    private readonly SemaphoreSlim _supervisorLifecycle = new(1, 1);
    private readonly CancellationTokenSource _applicationShutdown = new();
    private readonly Dictionary<string, double> _startupMilestones = [];
    private readonly RuntimeStatusViewModel _runtimeStatus = new();
    private readonly ProductMaintenanceCoordinator _productMaintenance = new();
    private MainWindow? _window;
    private WindowLayoutStore? _windowLayoutStore;
    private SingleInstanceService? _singleInstance;
    private InferenceSupervisorProcess? _supervisorProcess;
    private string? _supervisorInstanceId;
    private IInferenceClient? _activeInferenceClient;
    private IQrCodeClient? _activeQrCodeClient;
    private PortableLayout? _supervisorLayout;
    private DiagnosticsViewModel? _supervisorDiagnostics;
    private IRuntimeInstallerClient? _runtimeInstaller;
    private IManagedEnvironmentClient? _managedEnvironments;
    private ManagedEnvironmentSettings? _environmentSettings;
    private ManagedEnvironmentSession? _managedSession;
    private SyntheticScreenRegionPicker? _screenshotSmokePicker;
    private int _startupRuntimeEnsureAttempts = 0;
    private int _managedEnvironmentInstallAttempts;
    private FrontendExclusiveLock? _exclusiveLock;
    private WindowMessageService? _windowMessages;
    private TrayMenuOwnerWindow? _trayOwner;
    private TrayIconService? _trayIcon;
    private WindowsHotkeyRegistrar? _hotkeyRegistrar;
    private ShellActionDispatcher? _actionDispatcher;
    private PortableLayout? _shellLayout;
    private FloatingToolbarShell? _floatingToolbar;
    private ShellViewModel? _shellViewModel;
    private UpdateViewModel? _updateViewModel;
    private bool _shutdownStarted;
    private bool _soakCrashRequested;
    private bool _soakCrashInjected;
    private int _supervisorRecoveryScheduled;
    private int _runtimeMaintenanceActive;

    private const uint HotkeyMessage = 0x0312;
    private const uint TrayMessage = 0x8001;

    /// <summary>连接/恢复等待生命周期门的有界预算（覆盖一次 90 秒激活）。</summary>
    private static readonly TimeSpan SupervisorGateWaitTimeout = TimeSpan.FromSeconds(120);

    /// <summary>supervisor 健康轨迹写锁；仅显式设置 VIBEOCR_SUPERVISOR_HEALTH_TRACE 时启用。</summary>
    private readonly object _supervisorHealthTraceLock = new();

    /// <summary>soak 崩溃注入时观测到的崩溃前 supervisor 实例 id。</summary>
    private string? _soakCrashedInstanceId;

    // 托盘菜单仅本任务实际动作：打开工作台/截图识别/截图编辑/剪贴板识别/
    // 悬浮栏开关 + 退出；复用既有托盘回调消息，不新增常驻钩子。lParam
    // 事件分派统一经 Platform 的 TrayIconCallback（v0 键盘与右键同一路径）。
    private const uint MenuFlagString = 0x0000;
    private const uint MenuFlagSeparator = 0x0800;
    private const int TrayMenuCommandBase = 1000;
    private const int TrayMenuToggleToolbar = 1004;
    private const int TrayMenuQuit = 1005;

    public App()
    {
        UnhandledException += OnUnhandledException;
        InitializeComponent();
        // 网关等待 Supervisor 启动时以应用关停令牌兜底，避免退出时挂起。
        _inferenceGateway = new DeferredInferenceClient(_applicationShutdown.Token);
        _qrCodeGateway = new DeferredQrCodeClient(_applicationShutdown.Token);
    }

    private void OnUnhandledException(
        object sender,
        Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        _actionDispatcher?.EndHotkeyRecording();
        AppLog.Error("Unhandled WinUI exception", args.Exception);
        WriteSoakResult(
            Environment.GetEnvironmentVariable("VIBEOCR_SOAK_INJECT_CRASH") == "1",
            recovered: false,
            args.Exception.ToString());
        FlushStartupTrace();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppLaunchOptions options = AppLaunchOptions.Parse(Environment.GetCommandLineArgs()[1..]);
        SelfTestInstanceScope instanceScope = SelfTestInstanceScope.Resolve(
            options.Profile,
            Environment.GetEnvironmentVariable("VIBEOCR_SELF_TEST_SMOKE"),
            Environment.GetEnvironmentVariable("VIBEOCR_SELF_TEST_INSTANCE"));
        _singleInstance = new SingleInstanceService(
            instanceScope.SingleInstanceName,
            arguments =>
            {
                _window?.DispatcherQueue.TryEnqueue(() =>
                {
                    // Parse the forwarded arguments so a secondary launch can
                    // carry intent (e.g. --goto pdf). Unknown flags are ignored
                    // by Parse, so a plain re-activation still just shows the
                    // window on its current tab.
                    string? destination = null;
                    try
                    {
                        destination = AppLaunchOptions.Parse(arguments).Goto;
                    }
                    catch (ArgumentException error)
                    {
                        AppLog.Warn($"Forwarded activation had invalid arguments: {error.Message}");
                    }
                    _window!.ShowAndNavigate(destination);
                });
                return Task.CompletedTask;
            });
        if (!_singleInstance.IsPrimary)
        {
            _ = ForwardActivationAndExitAsync(Environment.GetCommandLineArgs()[1..]);
            return;
        }

        // 跨产品互斥：同一登录会话内 PySide Classic 与 WinUI Next 不同时运行。
        // 在同产品单实例通过后、Supervisor 启动前获取；失败时提示退出，不启动
        // 第二个 Supervisor。Mutex 由 OS 在前端崩溃时自动释放（ADR §6）。
        _exclusiveLock = new FrontendExclusiveLock(instanceScope.ExclusiveMutexName);
        if (!_exclusiveLock.IsAcquired)
        {
            FrontendExclusiveLock.ShowAnotherProductRunningPrompt();
            Exit();
            return;
        }
        string executable = Environment.ProcessPath ?? AppContext.BaseDirectory;
        PortableLayout layout = PortableLayout.Resolve(
            executable,
            options.Profile,
            Environment.GetEnvironmentVariable("VIBEOCR_PORTABLE_LAYOUT"),
            options.InstallRoot,
            productRootOverride: options.ProductRoot);
        // 便携状态根就绪探针:不可写时 fail closed,不回退用户目录。
        layout.EnsurePortableState();
        // WebView2 user-data/cache/cookies 固定在便携 state 内;通过 Loader
        // 官方环境变量指定(WinRT 投影无带目录的 CreateAsync 重载)。外部
        // 显式覆盖(测试/隔离)优先。
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER")))
        {
            Environment.SetEnvironmentVariable(
                "WEBVIEW2_USER_DATA_FOLDER",
                layout.WebView2Root);
        }
        _runtimeInstaller = new RuntimeInstallerClient(
            RuntimeInstallerConfiguration.ForNext(layout));
        _managedEnvironments = (IManagedEnvironmentClient)_runtimeInstaller;
        _runtimeStatus.ApplyProfile(_runtimeInstaller.ReadProfileDescriptor());
        AppLog.Initialize(Path.Combine(layout.DataRoot, "logs"));
        AppLog.Info($"OnLaunched: profile={options.Profile} shellOnly={options.ShellOnly}");
        if (layout.Profile == "production" && File.Exists(layout.ConfigFile))
        {
            MigrationResult migration = ProfileMigrationClient.MigrateConfig(layout);
            if (migration.Status == "skipped")
            {
                throw new InvalidDataException(
                    $"Production profile migration failed: {migration.Message}");
            }
        }
        PrerequisiteReport prerequisites = new PrerequisiteDetector().Detect(layout);
        var diagnostics = new DiagnosticsViewModel(
            options.Profile,
            prerequisites,
            static async (item, _) =>
            {
                if (Uri.TryCreate(item.RepairUri, UriKind.Absolute, out Uri? uri) && uri.Scheme != "repair")
                {
                    await Windows.System.Launcher.LaunchUriAsync(uri);
                }
            },
            _runtimeStatus,
            () => _supervisorProcess is { } process
                ? (process.Ready.InstanceId, process.LogLines)
                : (null, Array.Empty<string>()));
        // 可选取证轨迹（默认关闭）：仅显式设置环境变量时才订阅，不引入新配置系统。
        if (!string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("VIBEOCR_SUPERVISOR_HEALTH_TRACE")))
        {
            diagnostics.PropertyChanged += OnSupervisorHealthTraceChanged;
        }
        _supervisorLayout = layout;
        _supervisorDiagnostics = diagnostics;
        _soakCrashRequested =
            Environment.GetEnvironmentVariable("VIBEOCR_SOAK_INJECT_CRASH") == "1";
        RecordMilestone(diagnostics, "T0", TimeSpan.Zero);
        RecordMilestone(diagnostics, "T1", _startup.Elapsed);

        _windowLayoutStore = new WindowLayoutStore(
            Path.Combine(layout.DataRoot, "winui-layout.json"));
        if (Environment.GetEnvironmentVariable("VIBEOCR_SELF_TEST_SMOKE") is
            "screenshot-e2e" or "text-selection-e2e" or "managed-environment-e2e")
        {
            _screenshotSmokePicker = new SyntheticScreenRegionPicker(
                Environment.GetEnvironmentVariable("VIBEOCR_SELF_TEST_SMOKE") ==
                "text-selection-e2e" ? "VibeOCR 123\r\n中文 文本 456" : "VibeOCR 123");
        }

        // 统一动作分派器先于主窗创建：热键/托盘/悬浮栏/主窗四入口共用同一
        // 动作目录，登记器与悬浮栏在桌面壳初始化时经委托解析。
        _actionDispatcher = new ShellActionDispatcher(
            () => _hotkeyRegistrar,
            new Dictionary<string, Func<Task>>(StringComparer.Ordinal)
            {
                [HotkeyActionCatalog.ScreenshotEdit] =
                    () => _window!.CaptureScreenshotForEditAsync(),
                // 直接等待主窗入口：早先的无条件 finally 显示会在选区/遮罩
                // 尚未结束时提前抢焦点；终态显示由命令层在真正完成后经
                // ShowWorkbench 动作触发（拒绝重入时不显示）。
                [HotkeyActionCatalog.ScreenshotRecognize] =
                    () => _window!.RecognizeScreenshotAsync(),
                [HotkeyActionCatalog.ClipboardRecognize] =
                    ShowWorkbenchThenRecognizeClipboardAsync,
                [HotkeyActionCatalog.ToggleToolbar] = ToggleFloatingToolbarAsync,
                [HotkeyActionCatalog.ShowWorkbench] = ShowWorkbenchFromShellActionAsync,
            },
            CurrentFloatingToolbarSettings,
            () => _floatingToolbar?.Visibility ?? FloatingToolbarVisibility.Disabled,
            TryApplyFloatingToolbarSettings,
            TryShowFloatingToolbar,
            TryHideFloatingToolbar,
            () => _floatingToolbar?.TrySuspendForCapture());

        _window = new MainWindow(
          diagnostics,
          layout,
          () => new RecognitionViewModel(
            _inferenceGateway,
            new InputService(
              () => WinRT.Interop.WindowNative.GetWindowHandle(_window!),
              Environment.GetEnvironmentVariable("VIBEOCR_SELF_TEST_SMOKE") == "paddle-modes-e2e"
                ? new MainWindow.SyntheticFixtureRegionPicker()
                : _screenshotSmokePicker)),
          () => new BatchViewModel(
            _inferenceGateway,
            new BatchFileSource(() => WinRT.Interop.WindowNative.GetWindowHandle(_window!))),
          () =>
          {
            nint handle = WinRT.Interop.WindowNative.GetWindowHandle(_window!);
            return new QrCodeViewModel(
              _qrCodeGateway,
              new QrCodeInputService(() => handle));
          },
          () =>
          {
            nint handle = WinRT.Interop.WindowNative.GetWindowHandle(_window!);
            return new PdfViewModel(
              _inferenceGateway,
              new PdfFileSource(() => handle));
          },
          () => new SettingsViewModel(
            _inferenceGateway,
            _runtimeStatus,
            () => _runtimeInstaller
              ?? throw new InvalidOperationException("Runtime installer is unavailable."),
            _productMaintenance, StopForMaintenanceAsync, RestoreAfterMaintenanceAsync,
            Path.Combine(layout.DataRoot, "runtime-maintenance.json"),
            _environmentSettings ??= new ManagedEnvironmentSettings(
              _managedEnvironments ?? throw new InvalidOperationException("Runtime manager is unavailable."),
              SwitchManagedEnvironmentAsync,
              () => _managedSession is { } session
                ? (session.EnvironmentId, session.Revision) : null,
              _productMaintenance,
              () => _managedSession,
              () => Interlocked.Increment(ref _managedEnvironmentInstallAttempts))),
          () => _shellViewModel ??
            throw new InvalidOperationException("Desktop shell is unavailable."),
          () => _updateViewModel ??
            throw new InvalidOperationException("Update service is unavailable."),
          _windowLayoutStore,
          () => _inferenceGateway.IsAttached,
          () => _supervisorInstanceId,
          _screenshotSmokePicker,
          () => _inferenceGateway.SubmitAttempts,
          () => _inferenceGateway.LastSubmittedJobId,
          () => Volatile.Read(ref _startupRuntimeEnsureAttempts),
          () => _environmentSettings?.Snapshot,
          () => _managedSession,
          () => Volatile.Read(ref _managedEnvironmentInstallAttempts),
          shellActions: _actionDispatcher);
        _window.AppWindow.Closing += OnAppWindowClosing;
        _window.Closed += OnWindowClosedFallback;
        _window.Activate();
        InitializeDesktopShell(layout);
        RecordMilestone(diagnostics, "T2", _startup.Elapsed);

        // --shell-only: run the UI shell without launching the Supervisor. Useful
        // for inspecting layout / XAML without paying the dev cold-import cost.
        // Without args the default is to bring the backend up automatically.
        if (options.ShellOnly)
        {
            diagnostics.UpdateSupervisor(new SupervisorHealth(
                SupervisorHealthState.NotReady,
                null,
                null,
                "外壳模式：未拉起后端（--shell-only）。"));
            RecordMilestone(diagnostics, "T6", _startup.Elapsed);
        }
        else
        {
            // Phase 8 atomic switch: start the v2 inference supervisor after the
            // first window is up. This spawns the supervisor subprocess, reads
            // the ready envelope, and Attach()es the real InferenceHttpClient /
            // QrCodeHttpClient into the deferred gateways so every ViewModel's
            // v2 calls stop throwing. Fire-and-forget: the window is already
            // interactive; the diagnostics panel reflects Connecting → Ready.
            // 启动尝试期间网关调用等待 Attach 而不是立即失败，窗口早于后端
            // 就绪期间触发的命令得以完成。
            _inferenceGateway.MarkStartupPending();
            _qrCodeGateway.MarkStartupPending();
            _ = ConnectSupervisorAfterFirstWindowAsync(layout, diagnostics);
        }

        // Perf-gate smoke mode: exit shortly after first window so cold-start
        // timing can be measured without the supervisor handshake. Production runs
        // never set this env var.
        if (Environment.GetEnvironmentVariable("VIBEOCR_SELF_TEST_SMOKE") is "1" or "t3")
        {
            _ = SmokeExitAsync();
        }
    }

    private async Task ForwardActivationAndExitAsync(IReadOnlyList<string> arguments)
    {
        SingleInstanceService instance = _singleInstance
            ?? throw new InvalidOperationException("Single-instance service is unavailable.");
        try
        {
            await instance.ForwardAsync(arguments, CancellationToken.None);
        }
        finally
        {
            await instance.DisposeAsync();
            _singleInstance = null;
            Exit();
        }
    }

    private void InitializeDesktopShell(PortableLayout layout)
    {
        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(_window!);
        _windowMessages = new WindowMessageService(handle);
        _windowMessages.MessageReceived += OnWindowMessage;
        // 托盘回调、TaskbarCreated 与菜单 owner 独立于主窗：右键/键盘菜单
        // 绝不激活主窗；owner 是真实隐藏顶层 ToolWindow（非 HWND_MESSAGE）。
        _trayOwner = new TrayMenuOwnerWindow();
        _trayOwner.MessageReceived += OnWindowMessage;
        _trayOwner.TaskbarCreated += OnTaskbarCreated;
        _trayIcon = new TrayIconService(Path.Combine(layout.WebAssetsRoot, "vibeocr.ico"));
        _trayIcon.Show(_trayOwner.Handle, TrayMessage, "VibeOCR");
        _shellLayout = layout;

        _hotkeyRegistrar = new WindowsHotkeyRegistrar(
            new GlobalHotkeyService(windowHandle: handle),
            layout);
        // 多动作登记：仅注册目录内已交付动作；单条失败记入该动作状态，
        // 供设置页展示与重试，不拋出。
        _hotkeyRegistrar.InitializeActions();

        string? configuredHotkey = _hotkeyRegistrar.GetActionStatuses()
            .FirstOrDefault(status => status.ActionId == HotkeyActionCatalog.ScreenshotRecognize)?
            .ConfiguredHotkey;
        _shellViewModel = new ShellViewModel(
            _hotkeyRegistrar,
            new WindowsStartupRegistrar(layout.ProductEntry),
            () => _window!.AppWindow.Hide(),
            () => _window!.Close(),
            configuredHotkey ?? string.Empty);
        // 旧单键路径兼容：同键为无操作成功；启动时被占用则重试并回显冲突。
        _shellViewModel.InitializeHotkey();
        _updateViewModel = new UpdateViewModel(
            VelopackUpdateCoordinator.Create(layout.ConfigFile, layout.ProbeWritableStateRoot),
            () => _window!.Close(),
            _productMaintenance);

        // 悬浮工具栏默认关闭；自检模式强制启用并在稳定后注入交互验证。
        bool floatingToolbarSelfTest =
            Environment.GetEnvironmentVariable("VIBEOCR_FLOATING_TOOLBAR_SELF_TEST") == "1";
        _floatingToolbar = FloatingToolbarShell.TryCreate(
            layout,
            _actionDispatcher!.TryDispatch,
            () => _window!.ShowAndNavigate("settings"),
            forceEnabled: floatingToolbarSelfTest);
        if (floatingToolbarSelfTest)
        {
            _ = RunFloatingToolbarSelfTestAsync();
        }
    }

    private async Task RunFloatingToolbarSelfTestAsync()
    {
        try
        {
            // 等待首窗稳定后再注入交互，避免与启动期的窗口几何恢复竞争。
            await Task.Delay(1500);
            bool passed = _floatingToolbar?.RunInteractionSelfTest() == true;
            AppLog.Info($"Floating toolbar self-test: {(passed ? "passed" : "failed")}");
            FlushStartupTrace();
            Environment.Exit(passed ? 0 : 1);
        }
        catch (Exception error)
        {
            AppLog.Error("Floating toolbar self-test crashed", error);
            Environment.Exit(1);
        }
    }

    private void OnWindowMessage(object? sender, WindowMessage message)
    {
        if (message.Id == HotkeyMessage)
        {
            // 按实际注册 ID 解析动作：被替换/未知 ID 不触发任何动作，
            // 与热键、托盘、悬浮栏、主窗入口同一分派器。
            if (_hotkeyRegistrar is { IsRecording: false } &&
                _hotkeyRegistrar.TryResolveAction((int)message.WParam, out string? action))
            {
                _actionDispatcher?.TryDispatch(action);
            }

            return;
        }

        if (message.Id == TrayMessage)
        {
            // v0 lParam 回调约定：按官方文档，右键与键盘菜单键同样发送
            // WM_RBUTTONUP，故同一入口弹菜单；WM_CONTEXTMENU 仅 v4 发送，
            // 容错保留；左键/双击才是明确打开主窗的动作。
            uint callback = (uint)message.LParam;
            if (TrayIconCallback.IsContextMenuRequest(callback))
            {
                ShowTrayContextMenu();
            }
            else if (callback is TrayIconCallback.LeftButtonUp or
                TrayIconCallback.LeftDoubleClick)
            {
                ShowMainWindow();
            }
        }
    }

    /// <summary>
    /// Explorer/任务栏重建：同 GUID 重挂托盘图标；失败如实记入 AppLog，
    /// 不让异常炸掉消息分发线程。
    /// </summary>
    private void OnTaskbarCreated(object? sender, EventArgs args)
    {
        try
        {
            _trayIcon?.Reattach();
            AppLog.Info("Tray icon reattached after taskbar recreation.");
        }
        catch (Exception error)
        {
            AppLog.Error("Tray icon reattach after taskbar recreation failed", error);
        }
    }

    /// <summary>
    /// 托盘上下文菜单：菜单宿主是隐藏的 TrayMenuOwnerWindow（SetForeground +
    /// TrackPopupMenu + 结束 WM_NULL 全部只针对 owner，绝不激活主窗）；
    /// TrackPopupMenu 直接返回选中项，失败/取消时静默保留原状态。
    /// </summary>
    private void ShowTrayContextMenu()
    {
        if (_trayOwner is not { } owner)
        {
            return;
        }

        nint menu = CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        try
        {
            string[] trayActions =
            [
                HotkeyActionCatalog.ShowWorkbench,
                HotkeyActionCatalog.ScreenshotRecognize,
                HotkeyActionCatalog.ScreenshotEdit,
                HotkeyActionCatalog.ClipboardRecognize,
            ];
            for (int index = 0; index < trayActions.Length; index++)
            {
                AppendMenuW(
                    menu,
                    MenuFlagString,
                    (nuint)(TrayMenuCommandBase + index),
                    HotkeyActionCatalog.DisplayName(trayActions[index]));
            }

            AppendMenuW(menu, MenuFlagSeparator, 0, null);
            AppendMenuW(
                menu,
                MenuFlagString,
                (nuint)TrayMenuToggleToolbar,
                ToolbarToggleMenuLabel());
            AppendMenuW(menu, MenuFlagSeparator, 0, null);
            AppendMenuW(menu, MenuFlagString, (nuint)TrayMenuQuit, "退出 VibeOCR");

            if (!GetCursorPos(out PointL cursor))
            {
                return;
            }

            // 握手全部落在 owner：前台、模态菜单、结束 WM_NULL。
            int selected = owner.TrackContextMenu(menu, cursor.X, cursor.Y);
            if (selected is 0)
            {
                return;
            }

            if (selected == TrayMenuQuit)
            {
                _window?.Close();
                return;
            }

            if (selected == TrayMenuToggleToolbar)
            {
                _actionDispatcher?.TryDispatch(HotkeyActionCatalog.ToggleToolbar);
                return;
            }

            if (selected >= TrayMenuCommandBase &&
                selected < TrayMenuCommandBase + trayActions.Length)
            {
                _actionDispatcher?.TryDispatch(trayActions[selected - TrayMenuCommandBase]);
            }
        }
        catch (Win32Exception error)
        {
            AppLog.Error("Tray context menu failed", error);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private string ToolbarToggleMenuLabel() =>
        (_floatingToolbar?.Visibility ?? FloatingToolbarVisibility.Disabled) switch
        {
            FloatingToolbarVisibility.Disabled => "启用悬浮工具栏",
            FloatingToolbarVisibility.Visible => "隐藏悬浮工具栏",
            _ => "显示悬浮工具栏",
        };

    private void ShowMainWindow()
    {
        if (_window is not { } window)
        {
            return;
        }

        window.AppWindow.Show();
        window.Activate();
        // 仅明确打开主窗的用户动作执行；托盘菜单展示/取消不经过此入口。
        // SetForegroundWindow 不承诺 last error，拒绝时只记录实际结果。
        if (!SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(window)))
        {
            AppLog.Warn("Main window foreground request was denied.");
        }
    }

    private Task ShowWorkbenchFromShellActionAsync()
    {
        ShowMainWindow();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 剪贴板识别：非截图动作，从隐藏窗/托盘/热键入口触发时先显式显示
    /// 工作台（结果呈现位置）再执行，不借用带截图终态语义的包装。
    /// </summary>
    private async Task ShowWorkbenchThenRecognizeClipboardAsync()
    {
        ShowMainWindow();
        await _window!.RecognizeClipboardAsync();
    }

    private FloatingToolbarSettings CurrentFloatingToolbarSettings() =>
        _floatingToolbar is { } toolbar
            ? toolbar.Settings
            : _shellLayout is { } layout
                ? FloatingToolbarSettings.Load(layout)
                : FloatingToolbarSettings.Default;

    /// <summary>
    /// 实时应用并持久化悬浮工具栏设置：保存失败保留原状态并返回错误；
    /// 启用但实例未能创建时返回错误，可见档位仍以实际运行实例为准。
    /// </summary>
    private string? TryApplyFloatingToolbarSettings(FloatingToolbarSettings settings)
    {
        if (_shellLayout is not { } layout)
        {
            return "桌面壳尚未完成初始化，请稍后重试。";
        }

        try
        {
            FloatingToolbarSettings.Save(layout, settings);
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn($"Failed to persist floating toolbar settings: {error.Message}");
            return $"无法保存悬浮工具栏设置，原设置已保留：{error.Message}";
        }

        if (!settings.Enabled)
        {
            _floatingToolbar?.Dispose();
            _floatingToolbar = null;
            return null;
        }

        if (_floatingToolbar is { } existing)
        {
            existing.ApplySettings(settings);
            return null;
        }

        // 保存成功但实例创建失败：不把“已启用”当作实际运行成功，如实报错。
        FloatingToolbarShell? created = FloatingToolbarShell.TryCreate(
            layout,
            _actionDispatcher!.TryDispatch,
            () => _window!.ShowAndNavigate("settings"));
        if (created is null)
        {
            return "悬浮工具栏设置已保存，但本次未能创建窗口；将随下次启动生效。";
        }

        _floatingToolbar = created;
        return null;
    }

    private string? TryShowFloatingToolbar()
    {
        if (_floatingToolbar is not { } toolbar)
        {
            return "悬浮工具栏已关闭，请先在设置中启用。";
        }

        try
        {
            toolbar.Show();
            return null;
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn($"Failed to show floating toolbar: {error.Message}");
            return $"无法保存悬浮工具栏显示设置，原状态已保留：{error.Message}";
        }
    }

    private string? TryHideFloatingToolbar()
    {
        if (_floatingToolbar is not { } toolbar)
        {
            return "悬浮工具栏未在运行，无需隐藏。";
        }

        try
        {
            toolbar.Hide();
            return null;
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warn($"Failed to hide floating toolbar: {error.Message}");
            return $"无法保存悬浮工具栏隐藏设置，原状态已保留：{error.Message}";
        }
    }

    private async Task ToggleFloatingToolbarAsync()
    {
        if (_floatingToolbar is { } toolbar)
        {
            toolbar.Toggle();
            return;
        }

        // 已关闭时明确启用并找回：偏好持久化，重启后保持启用。
        // 保存/创建失败只记录，热键入口不弹 UI。
        string? error = TryApplyFloatingToolbarSettings(
            CurrentFloatingToolbarSettings() with { Enabled = true, HiddenByUser = false });
        if (error is not null)
        {
            AppLog.Warn($"Toggle toolbar failed: {error}");
            return;
        }

        _floatingToolbar?.Show();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(
        nint menu,
        uint flags,
        nuint idNewItem,
        string? newItem);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out PointL point);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointL
    {
        public int X;
        public int Y;
    }

    private async Task SmokeExitAsync()
    {
        await Task.Delay(150);  // allow first-window render
        FlushStartupTrace();
        Environment.Exit(0);
    }

    /// <summary>
    /// 等待 Supervisor 生命周期门：可被调用方令牌取消且有界超时。界取
    /// 一次激活的 90 秒引擎启动预算加余量；运行环境安装不持门，只在拆
    /// 线/提交时短暂占用。关停路径保持无令牌等待：所有持门方均已
    /// 有界/可被关停令牌取消，销毁共享资源前不与在途写入者并发。
    /// </summary>
    private async Task WaitSupervisorLifecycleGateAsync(CancellationToken cancellationToken)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(SupervisorGateWaitTimeout);
        try
        {
            await _supervisorLifecycle.WaitAsync(bound.Token);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"等待运行环境生命周期门超时（{(int)SupervisorGateWaitTimeout.TotalSeconds} 秒），连接尝试未能开始。",
                error);
        }
    }

    private async Task<bool> ConnectSupervisorAfterFirstWindowAsync(
        PortableLayout layout,
        DiagnosticsViewModel diagnostics,
        bool isRecovery = false)
    {
        bool connected = await ConnectSupervisorCoreAsync(layout, diagnostics, isRecovery);
        if (connected && _managedSession is not null)
        {
            await RefreshEnvironmentSettingsAfterActivationAsync();
            if (_window is not null)
            {
                try
                {
                    await _window.RefreshRecognitionCatalogAsync(_applicationShutdown.Token);
                }
                catch (OperationCanceledException) when (_applicationShutdown.IsCancellationRequested) { }
                catch (Exception error)
                {
                    AppLog.Warn($"Recognition catalog refresh after attach failed: {error.Message}");
                }
            }
        }
        return connected;
    }

    private async Task<bool> ConnectSupervisorCoreAsync(
        PortableLayout layout,
        DiagnosticsViewModel diagnostics,
        bool isRecovery)
    {
        // 门等待可取消且有界：占用方（另一次启动/切换/维护拆线）异常滞留
        // 时，等待方以准确终态退出，不得无限悬挂。等待期间不发布
        // Connecting，避免占用方把“正在连接”滞留成永久状态。
        try
        {
            await WaitSupervisorLifecycleGateAsync(_applicationShutdown.Token);
        }
        catch (OperationCanceledException) when (_applicationShutdown.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception error)
        {
            _inferenceGateway.MarkStartupFailed(error);
            _qrCodeGateway.MarkStartupFailed(error);
            _runtimeStatus.ReportServiceUnavailable();
            diagnostics.UpdateSupervisor(new SupervisorHealth(
                SupervisorHealthState.Faulted, null, null, error.Message));
            return false;
        }
        try
        {
            bool injectedMaintenanceMutex = MaintenanceMutexEarlyExitSelfTestRequested(isRecovery) &&
                Volatile.Read(ref _runtimeMaintenanceActive) == 0;
            if (Volatile.Read(ref _runtimeMaintenanceActive) != 0 || injectedMaintenanceMutex)
            {
                // 维护互斥早退必须发布终态：识别服务因维护暂停，等待中的
                // 网关调用携带原因退出；恢复仍由维护结束后的 Restore 路径
                // 完成。
                var maintenance = new InvalidOperationException("运行环境维护尚未结束，识别服务保持暂停。");
                _inferenceGateway.MarkStartupFailed(maintenance);
                _qrCodeGateway.MarkStartupFailed(maintenance);
                _runtimeStatus.ReportServicePausedForMaintenance();
                diagnostics.UpdateSupervisor(new SupervisorHealth(
                    SupervisorHealthState.NotReady, null, null, "运行环境维护尚未结束。"));
                if (injectedMaintenanceMutex)
                {
                    // selftest-only 注入路径同样以 t6 终态自退，产出可验证的
                    // NotReady+维护原因证据；真实维护早退不得中断在途安装。
                    RecordMilestone(diagnostics, "T6", _startup.Elapsed);
                    ExitStartupSmokeT6();
                }
                return false;
            }
            // 拿到门且确认开始后才发布 Connecting。
            diagnostics.UpdateSupervisor(new SupervisorHealth(
                SupervisorHealthState.Connecting, null, null, null));
            RecordMilestone(diagnostics, "T3", _startup.Elapsed);
            // Recovery reuses this entry point; re-announce the attempt so
            // calls crossing the detach gap wait for this reconnect.
            _inferenceGateway.MarkStartupPending();
            _qrCodeGateway.MarkStartupPending();
            IManagedEnvironmentClient manager = _managedEnvironments
                ?? throw new InvalidOperationException("Runtime manager is unavailable.");
            ManagedEnvironmentList environments = await manager.ListEnvironmentsAsync(
                _applicationShutdown.Token);
            RecordMilestone(diagnostics, "T4", _startup.Elapsed);
            if (environments.ActiveId is null)
            {
                var unavailable = new InvalidOperationException(
                    "尚未选择运行环境。可以在设置中创建空环境或安装依赖。");
                _inferenceGateway.MarkStartupFailed(unavailable);
                _qrCodeGateway.MarkStartupFailed(unavailable);
                _runtimeStatus.ReportServiceUnavailable();
                diagnostics.UpdateSupervisor(new SupervisorHealth(
                    SupervisorHealthState.NotReady, null, null, unavailable.Message));
                RecordMilestone(diagnostics, "T6", _startup.Elapsed);
                // 未建/无活动环境是稳定终态：t6 冒烟同样自退并产出完整
                // T0–T6 轨迹，而不是悬挂到外部超时。
                ExitStartupSmokeT6();
                return false;
            }
            bool injectSoakCrash = _soakCrashRequested && !_soakCrashInjected && !isRecovery &&
                Environment.GetEnvironmentVariable("VIBEOCR_SOAK_EXTERNAL_CRASH") != "1";
            if (injectSoakCrash) _soakCrashInjected = true;
            await ActivateManagedEnvironmentCoreAsync(
                environments.ActiveId, layout, _applicationShutdown.Token, injectSoakCrash);
            RecordMilestone(diagnostics, "T5", _startup.Elapsed);
            if (_managedSession is { } session)
            {
                SupervisorReadyEnvelope ready = session.Process.Ready;
                diagnostics.UpdateSupervisor(new SupervisorHealth(
                    SupervisorHealthState.Ready, ready.InstanceId, ready.ProtocolVersion, null));
                AppLog.Info($"Supervisor ready: instance={ready.InstanceId} port={ready.Port}");
            }
            else
            {
                diagnostics.UpdateSupervisor(new SupervisorHealth(
                    SupervisorHealthState.NotReady, null, null,
                    "当前为空环境，未安装服务依赖。"));
            }
            RecordMilestone(diagnostics, "T6", _startup.Elapsed);
            bool soakCycleComplete = !_soakCrashRequested || isRecovery;
            if (soakCycleComplete)
            {
                WriteSoakResult(_soakCrashRequested, recovered: true);
            }
            if (Environment.GetEnvironmentVariable("VIBEOCR_SELF_TEST_SMOKE") == "t6"
                && soakCycleComplete)
            {
                FlushStartupTrace();
                Environment.Exit(0);
            }
            return true;
        }
        catch (Exception error)
        {
            // 释放等待本次启动尝试的网关调用，使其携带启动失败原因返回。
            _inferenceGateway.MarkStartupFailed(error);
            _qrCodeGateway.MarkStartupFailed(error);
            AppLog.Error("Supervisor connection failed", error);
            _runtimeStatus.ReportServiceUnavailable();
            await DisconnectSupervisorResourcesAsync();
            diagnostics.UpdateSupervisor(new SupervisorHealth(
                SupervisorHealthState.Faulted, null, null, error.Message));
            FailSoakRun(error.Message);
            return false;
        }
        finally
        {
            _supervisorLifecycle.Release();
        }
    }

    private async Task SwitchManagedEnvironmentAsync(
        string environmentId, CancellationToken cancellationToken)
    {
        // 同一有界门等待：排队在异常滞留的启动/拆线之后时以准确失败
        // 终态退出，而不是无限悬挂。
        await WaitSupervisorLifecycleGateAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref _runtimeMaintenanceActive) != 0)
                throw new InvalidOperationException("运行环境维护尚未结束。");
            PortableLayout layout = _supervisorLayout
                ?? throw new InvalidOperationException("Runtime layout is unavailable.");
            await ActivateManagedEnvironmentCoreAsync(environmentId, layout, cancellationToken);
            if (_managedSession is { } session)
            {
                SupervisorReadyEnvelope ready = session.Process.Ready;
                _supervisorDiagnostics?.UpdateSupervisor(new SupervisorHealth(
                    SupervisorHealthState.Ready, ready.InstanceId, ready.ProtocolVersion, null));
            }
            else
            {
                _supervisorDiagnostics?.UpdateSupervisor(new SupervisorHealth(
                    SupervisorHealthState.NotReady, null, null,
                    "当前为空环境，未启动识别服务。"));
            }
        }
        finally { _supervisorLifecycle.Release(); }
    }

    // 在 supervisor 门释放后刷新；环境切换持环境门等待 supervisor 门。
    private Task RefreshEnvironmentSettingsAfterActivationAsync() =>
        RefreshEnvironmentSettingsAfterActivationAsync(
            _environmentSettings, _applicationShutdown.Token);

    internal static async Task RefreshEnvironmentSettingsAfterActivationAsync(
        ManagedEnvironmentSettings? settings, CancellationToken shutdown)
    {
        if (settings is null) return;
        try
        {
            await settings.RefreshAsync(shutdown);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // 应用关停取消了刷新；无需任何处理。
        }
        catch (Exception error)
        {
            AppLog.Warn($"Managed environment refresh after activation failed: {error.Message}");
        }
    }

    private async Task ActivateManagedEnvironmentCoreAsync(
        string environmentId, PortableLayout layout, CancellationToken cancellationToken,
        bool injectSoakCrash = false)
    {
        IManagedEnvironmentClient manager = _managedEnvironments
            ?? throw new InvalidOperationException("Runtime manager is unavailable.");
        var coordinator = new ManagedEnvironmentSwitchCoordinator(manager);
        // 真实 Paddle 导入探针允许 60 秒；启动预算还需覆盖解释器和装配开销。
        ManagedEnvironmentSession? session = await coordinator.SwitchAsync(
            environmentId,
            _managedSession,
            PublishManagedEnvironmentSession,
            Path.Combine(layout.DataRoot, "supervisor.log"),
            TimeSpan.FromSeconds(90),
            RuntimeCapabilityRequirements.Read(layout.ComponentLock),
            cancellationToken,
            injectSoakCrash);
        if (session is null)
        {
            _runtimeStatus.ReportServiceUnavailable();
            return;
        }
        _runtimeStatus.ApplySnapshot(session.Status);
    }

    private void PublishManagedEnvironmentSession(ManagedEnvironmentSession? next)
    {
        _supervisorInstanceId = next?.Process.Ready.InstanceId;
        _window?.InvalidatePinnedTextLayers();
        if (_managedSession is { } previous)
        {
            previous.Process.UnexpectedExit -= OnSupervisorUnexpectedExit;
            previous.Process.LogReceived -= OnSupervisorLogReceived;
            _inferenceGateway.Detach(previous.Client);
            _qrCodeGateway.Detach(previous.QrClient);
        }
        _managedSession = next;
        _supervisorProcess = next?.Process;
        _activeInferenceClient = next?.Client;
        _activeQrCodeClient = next?.QrClient;
        if (next is null)
        {
            var unavailable = new InvalidOperationException(
                "当前为空环境，未安装识别服务依赖。");
            _inferenceGateway.MarkStartupFailed(unavailable);
            _qrCodeGateway.MarkStartupFailed(unavailable);
            return;
        }
        next.Process.UnexpectedExit += OnSupervisorUnexpectedExit;
        next.Process.LogReceived += OnSupervisorLogReceived;
        _inferenceGateway.Attach(next.Client);
        _qrCodeGateway.Attach(next.QrClient);
    }

    private void WriteSoakResult(bool requested, bool recovered, string? error = null)
    {
        string? resultPath = Environment.GetEnvironmentVariable("VIBEOCR_SOAK_RESULT");
        if (string.IsNullOrWhiteSpace(resultPath)) return;
        string fullPath = Path.GetFullPath(resultPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        // 实例证据：崩溃前与恢复后的 supervisor 实例 id，证明是新实例
        // 重连而非仅置位 recovered 标志。
        File.WriteAllText(fullPath, JsonSerializer.Serialize(new
        {
            crash_requested = requested,
            recovered,
            error,
            instance_before = _soakCrashedInstanceId,
            instance_after = _supervisorInstanceId,
        }));
    }

    private void FailSoakRun(string error)
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("VIBEOCR_SOAK_RESULT")))
        {
            return;
        }

        WriteSoakResult(_soakCrashRequested, recovered: false, error);
        if (Environment.GetEnvironmentVariable("VIBEOCR_SELF_TEST_SMOKE") == "t6")
        {
            FlushStartupTrace();
            Environment.Exit(1);
        }
    }

    private void RecordMilestone(DiagnosticsViewModel diagnostics, string name, TimeSpan elapsed)
    {
        diagnostics.RecordMilestone(name, elapsed);
        _startupMilestones.TryAdd(name, elapsed.TotalSeconds);
    }

    private void FlushStartupTrace()
    {
        string? tracePath = Environment.GetEnvironmentVariable("VIBEOCR_STARTUP_TRACE");
        if (string.IsNullOrWhiteSpace(tracePath)) return;
        string fullPath = Path.GetFullPath(tracePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.AppendAllText(fullPath, JsonSerializer.Serialize(_startupMilestones) + Environment.NewLine);
    }

    /// <summary>selftest-only：仅在 t6 冒烟且显式设置环境变量时注入维护互斥早退。</summary>
    private static bool MaintenanceMutexEarlyExitSelfTestRequested(bool isRecovery) =>
        !isRecovery &&
        Environment.GetEnvironmentVariable("VIBEOCR_SELF_TEST_SMOKE") == "t6" &&
        Environment.GetEnvironmentVariable("VIBEOCR_SELF_TEST_MAINTENANCE_EARLY_EXIT") == "1";

    /// <summary>稳定终态（无环境/注入的维护互斥）下 t6 冒烟自退并落盘轨迹。</summary>
    private void ExitStartupSmokeT6()
    {
        if (Environment.GetEnvironmentVariable("VIBEOCR_SELF_TEST_SMOKE") != "t6") return;
        FlushStartupTrace();
        Environment.Exit(0);
    }

    /// <summary>
    /// 可选 supervisor 健康轨迹（默认关闭）：仅在显式设置
    /// VIBEOCR_SUPERVISOR_HEALTH_TRACE 时，把每次连接健康变更逐行追加
    /// 到指定 JSONL 文件供隔离自测取证；写入失败静默，不影响状态机。
    /// </summary>
    private void OnSupervisorHealthTraceChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(DiagnosticsViewModel.SupervisorStatus)) return;
        try
        {
            string? path = Environment.GetEnvironmentVariable("VIBEOCR_SUPERVISOR_HEALTH_TRACE");
            if (string.IsNullOrWhiteSpace(path) || _supervisorDiagnostics is not { } source) return;
            SupervisorHealth health = source.Supervisor;
            string line = JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.UtcNow.ToString("O"),
                state = health.State.ToString(),
                instance_id = health.InstanceId,
                protocol_version = health.ProtocolVersion,
                detail = health.Detail,
                process_id = _supervisorProcess?.ProcessId,
            });
            lock (_supervisorHealthTraceLock)
            {
                File.AppendAllText(Path.GetFullPath(path), line + Environment.NewLine);
            }
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // 取证轨迹尽力而为；不得影响连接状态机本身。
        }
    }

    private void OnSupervisorLogReceived(object? sender, string line)
    {
        if (!ReferenceEquals(sender, _supervisorProcess) ||
            !line.Contains("[Paddle worker]", StringComparison.Ordinal) ||
            !(line.Contains("[推理设备]", StringComparison.Ordinal) ||
              line.Contains("[GPU]", StringComparison.Ordinal))) return;
        void Notify()
        {
            if (ReferenceEquals(sender, _supervisorProcess))
                _supervisorDiagnostics?.NotifyDeviceEvidenceChanged();
        }
        if (_window?.DispatcherQueue.TryEnqueue(Notify) != true) Notify();
    }

    private void OnSupervisorUnexpectedExit(
        object? sender,
        SupervisorUnexpectedExitEventArgs eventArgs)
    {
        if (_applicationShutdown.IsCancellationRequested
            || Volatile.Read(ref _runtimeMaintenanceActive) != 0
            || !ReferenceEquals(sender, _supervisorProcess)
            || Interlocked.Exchange(ref _supervisorRecoveryScheduled, 1) != 0)
        {
            return;
        }

        AppLog.Warn(
            $"Supervisor exited unexpectedly (code={eventArgs.ExitCode?.ToString() ?? "unknown"}); "
            + "scheduling one reconnect attempt.");
        _soakCrashedInstanceId = _supervisorInstanceId;
        void ScheduleRecovery()
        {
            // 服务已消失：恢复期间不得残留“运行时已就绪”投影。
            _runtimeStatus.ReportServiceUnavailable();
            _supervisorDiagnostics?.UpdateSupervisor(new SupervisorHealth(
                SupervisorHealthState.Faulted,
                null,
                null,
                "Supervisor 异常退出，正在自动恢复。"));
            _ = RecoverSupervisorAfterExitAsync();
        }

        if (_window?.DispatcherQueue.TryEnqueue(ScheduleRecovery) != true)
        {
            _ = Task.Run(ScheduleRecovery);
        }
    }

    private async Task RecoverSupervisorAfterExitAsync()
    {
        try
        {
            await WaitSupervisorLifecycleGateAsync(_applicationShutdown.Token);
            try
            {
                await DisconnectSupervisorResourcesAsync();
            }
            finally
            {
                _supervisorLifecycle.Release();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), _applicationShutdown.Token);
            if (_supervisorLayout is null || _supervisorDiagnostics is null)
            {
                throw new InvalidOperationException("Supervisor recovery context is unavailable.");
            }

            await ConnectSupervisorAfterFirstWindowAsync(
                _supervisorLayout,
                _supervisorDiagnostics,
                isRecovery: true);
        }
        catch (OperationCanceledException) when (_applicationShutdown.IsCancellationRequested)
        {
            // Application shutdown won the race; no recovery is needed.
        }
        catch (Exception error)
        {
            AppLog.Error("Supervisor recovery failed", error);
            // 恢复失败（含门等待超时）必须同步退出“运行时已就绪”残留，
            // 与连接路径的故障终态保持一致。
            _runtimeStatus.ReportServiceUnavailable();
            _supervisorDiagnostics?.UpdateSupervisor(new SupervisorHealth(
                SupervisorHealthState.Faulted, null, null, error.Message));
            FailSoakRun(error.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _supervisorRecoveryScheduled, 0);
        }
    }

    private async Task StopForMaintenanceAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _runtimeMaintenanceActive, 1);
        // 与连接/恢复/切换同一有界门等待：占用方异常滞留时以失败终态
        // 退出（ConfirmAsync 会把安装置为 failed 并经 Restore 重连）。
        await WaitSupervisorLifecycleGateAsync(cancellationToken);
        try
        {
            await DisconnectSupervisorResourcesAsync();
            _runtimeStatus.ReportServicePausedForMaintenance();
        }
        finally { _supervisorLifecycle.Release(); }
    }

    private async Task RestoreAfterMaintenanceAsync()
    {
        Interlocked.Exchange(ref _runtimeMaintenanceActive, 0);
        if (_applicationShutdown.IsCancellationRequested || _supervisorProcess is not null) return;
        if (_supervisorLayout is not null && _supervisorDiagnostics is not null)
            await ConnectSupervisorAfterFirstWindowAsync(_supervisorLayout, _supervisorDiagnostics, isRecovery: true);
    }

    private async Task StopSupervisorAsync()
    {
        // Phase 8: stop the v2 inference supervisor subprocess. The supervisor
        // owns MinerU/PDF children via a Job Object, so disposing the process
        // handle tears the whole tree down. Best-effort: shutdown must not hang
        // the UI even if the child is unresponsive.
        await DisconnectSupervisorResourcesAsync();
    }

    private async Task DisconnectSupervisorResourcesAsync()
    {
        _supervisorInstanceId = null;
        _window?.InvalidatePinnedTextLayers();
        if (_managedSession is { } managed)
        {
            _managedSession = null;
            _supervisorProcess = null;
            _activeInferenceClient = null;
            _activeQrCodeClient = null;
            managed.Process.UnexpectedExit -= OnSupervisorUnexpectedExit;
            managed.Process.LogReceived -= OnSupervisorLogReceived;
            _inferenceGateway.Detach(managed.Client);
            _qrCodeGateway.Detach(managed.QrClient);
            await managed.DisposeAsync();
            return;
        }
        IInferenceClient? inferenceClient = _activeInferenceClient;
        _activeInferenceClient = null;
        if (inferenceClient is not null)
        {
            _inferenceGateway.Detach(inferenceClient);
            await inferenceClient.DisposeAsync();
        }

        IQrCodeClient? qrCodeClient = _activeQrCodeClient;
        _activeQrCodeClient = null;
        if (qrCodeClient is not null)
        {
            _qrCodeGateway.Detach(qrCodeClient);
            await qrCodeClient.DisposeAsync();
        }

        InferenceSupervisorProcess? process = _supervisorProcess;
        _supervisorProcess = null;
        if (process is null)
        {
            return;
        }
        process.UnexpectedExit -= OnSupervisorUnexpectedExit;
        process.LogReceived -= OnSupervisorLogReceived;
        try
        {
            process.Dispose();
        }
        catch (Exception error)
        {
            AppLog.Warn($"Supervisor shutdown error: {error.Message}");
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_shutdownStarted)
        {
            return;
        }

        args.Cancel = true;
        _actionDispatcher?.EndHotkeyRecording();
        _shutdownStarted = true;
        _applicationShutdown.Cancel();
        _ = ShutdownAndExitAsync(sender);
    }

    private async Task ShutdownAndExitAsync(AppWindow appWindow)
    {
        if (_environmentSettings is not null)
            await _environmentSettings.CancelAndWaitForInstallAsync();
        await _supervisorLifecycle.WaitAsync();
        try
        {
            if (_window is not null && _windowLayoutStore is not null && _window.CaptureGeometry() is { } geometry)
            {
                _windowLayoutStore.Save(geometry);
            }
            await StopSupervisorAsync();
            await _inferenceGateway.DisposeAsync();
            await _qrCodeGateway.DisposeAsync();
            await DisposeDesktopShellAsync();
        }
        finally
        {
            _supervisorLifecycle.Release();
            _applicationShutdown.Dispose();
            appWindow.Closing -= OnAppWindowClosing;
            _window?.Close();
            Exit();
        }
    }

    private async Task DisposeDesktopShellAsync()
    {
        _floatingToolbar?.Dispose();
        _floatingToolbar = null;
        _hotkeyRegistrar?.Dispose();
        _hotkeyRegistrar = null;
        // 先删托盘图标，再拆 owner 事件并销毁：各释放一次，图标不残留。
        _trayIcon?.Dispose();
        _trayIcon = null;
        if (_trayOwner is not null)
        {
            _trayOwner.MessageReceived -= OnWindowMessage;
            _trayOwner.TaskbarCreated -= OnTaskbarCreated;
            _trayOwner.Dispose();
            _trayOwner = null;
        }
        if (_windowMessages is not null)
        {
            _windowMessages.MessageReceived -= OnWindowMessage;
            _windowMessages.Dispose();
            _windowMessages = null;
        }
        if (_singleInstance is not null)
        {
            await _singleInstance.DisposeAsync();
            _singleInstance = null;
        }
        _exclusiveLock?.Dispose();
        _exclusiveLock = null;
    }

    private void OnWindowClosedFallback(object sender, WindowEventArgs args)
    {
        if (!_shutdownStarted)
        {
            _shutdownStarted = true;
            _applicationShutdown.Cancel();
        }

        Environment.Exit(0);
    }
}

public sealed record AppLaunchOptions(
    string Profile,
    bool ShellOnly,
    string? Goto = null,
    string? InstallRoot = null,
    string? ProductRoot = null)
{
    public static AppLaunchOptions Parse(IReadOnlyList<string> args)
    {
        string profile = AppBuildDefaults.Profile;
        bool shellOnly = false;
        string? gotoDestination = null;
        string? installRoot = null;
        string? productRoot = null;
        for (int index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index], "--profile", StringComparison.Ordinal))
            {
                if (index + 1 >= args.Count)
                {
                    throw new ArgumentException("--profile requires a value.", nameof(args));
                }
                profile = args[++index];
            }
            else if (string.Equals(args[index], "--shell-only", StringComparison.Ordinal))
            {
                shellOnly = true;
            }
            else if (string.Equals(args[index], "--install-root", StringComparison.Ordinal))
            {
                if (index + 1 >= args.Count)
                {
                    throw new ArgumentException("--install-root requires a value.", nameof(args));
                }
                installRoot = Path.GetFullPath(args[++index]);
            }
            else if (string.Equals(args[index], "--product-root", StringComparison.Ordinal))
            {
                if (index + 1 >= args.Count)
                {
                    throw new ArgumentException("--product-root requires a value.", nameof(args));
                }
                productRoot = Path.GetFullPath(args[++index]);
            }
            else if (string.Equals(args[index], "--goto", StringComparison.Ordinal))
            {
                if (index + 1 >= args.Count)
                {
                    throw new ArgumentException("--goto requires a value.", nameof(args));
                }
                string destination = args[++index];
                if (!ShellNavigation.Destinations.Contains(destination))
                {
                    throw new ArgumentException(
                        $"Unsupported --goto destination: {destination}.", nameof(args));
                }
                gotoDestination = destination;
            }
        }

        if (profile is not ("production" or "winui-dev"))
        {
            throw new ArgumentException($"Unsupported profile: {profile}.", nameof(args));
        }

        return new AppLaunchOptions(
            profile,
            shellOnly,
            gotoDestination,
            installRoot,
            productRoot);
    }
}

public static class AppBuildDefaults
{
#if DEBUG
    public const string Profile = "winui-dev";
#else
    public const string Profile = "production";
#endif
}

public static class ShellNavigation
{
    public static IReadOnlyList<string> Destinations { get; } =
        ["home", "recognition", "batch", "qrcode", "pdf", "settings", "about", "diagnostics"];
}

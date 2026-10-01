using System.Text.Json;
using VibeOCR.App.Features.Shell;
using VibeOCR.Platform.Bootstrap;
using VibeOCR.Platform.Windows;
using Xunit;

namespace VibeOCR.App.Tests;

/// <summary>
/// 多动作全局快捷键登记契约：旧键迁移、实际注册 ID 分派、换键先注册后
/// 持久化再释放旧键、冲突/重复/非法/保存失败保旧注册、禁用/恢复默认与
/// 状态读取，全部用 fake native 驱动，不注册真实系统热键。
/// </summary>
public sealed class HotkeyActionRegistrarTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"vibeocr-hotkey-actions-{Guid.NewGuid():N}");

    private readonly RecordingHotkeyNative _native = new();

    private (WindowsHotkeyRegistrar Registrar, PortableLayout Layout) CreateRegistrar()
    {
        Directory.CreateDirectory(_root);
        PortableLayout layout = PortableLayout.Resolve(
            Path.Combine(_root, "VibeOCR.Next.exe"),
            "production");
        layout.EnsurePortableState();
        var registrar = new WindowsHotkeyRegistrar(
            new GlobalHotkeyService(_native),
            layout);
        return (registrar, layout);
    }

    private void WriteConfig(PortableLayout layout, string json) =>
        File.WriteAllText(layout.ConfigFile, json);

    private static HotkeyActionStatus StatusOf(
        WindowsHotkeyRegistrar registrar, string actionId) =>
        Assert.Single(registrar.GetActionStatuses(), status => status.ActionId == actionId);

    [Fact]
    public void InitializeActionsRegistersOnlyDefaultBoundActionWithNoRepeat()
    {
        using (WindowsHotkeyRegistrar registrar = CreateRegistrar().Registrar)
        {
            registrar.InitializeActions();

            HotkeyActionStatus recognize = StatusOf(
                registrar, HotkeyActionCatalog.ScreenshotRecognize);
            Assert.Equal("Ctrl+Alt+Q", recognize.ConfiguredHotkey);
            Assert.Equal("Ctrl+Alt+Q", recognize.RegisteredHotkey);
            Assert.Null(recognize.Error);

            foreach (string actionId in HotkeyActionCatalog.Actions)
            {
                if (actionId == HotkeyActionCatalog.ScreenshotRecognize)
                {
                    continue;
                }

                HotkeyActionStatus status = StatusOf(registrar, actionId);
                Assert.Null(status.ConfiguredHotkey);
                Assert.Null(status.RegisteredHotkey);
            }

            (int id, HotkeyModifiers modifiers, uint virtualKey) registration =
                Assert.Single(_native.Registrations);
            Assert.Equal(
                HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.NoRepeat,
                registration.modifiers);
            Assert.Equal('Q', registration.virtualKey);
            Assert.True(registrar.TryResolveAction(registration.id, out string? action));
            Assert.Equal(HotkeyActionCatalog.ScreenshotRecognize, action);
            Assert.False(registrar.TryResolveAction(registration.id + 100, out _));
        }

        Assert.Empty(_native.ActiveIds);
    }

    [Fact]
    public void LegacyGlobalScreenshotMigratesWithoutChangingSemantics()
    {
        (WindowsHotkeyRegistrar registrar, PortableLayout layout) = CreateRegistrar();
        using (registrar)
        {
            WriteConfig(
                layout,
                "{\"hotkeys\": {\"global_screenshot\": \"Ctrl+Alt+X\"}}");
            registrar.InitializeActions();

            HotkeyActionStatus recognize = StatusOf(
                registrar, HotkeyActionCatalog.ScreenshotRecognize);
            Assert.Equal("Ctrl+Alt+X", recognize.ConfiguredHotkey);
            Assert.Equal("Ctrl+Alt+X", recognize.RegisteredHotkey);
            int id = Assert.Single(_native.ActiveIds);
            Assert.True(registrar.TryResolveAction(id, out string? action));
            Assert.Equal(HotkeyActionCatalog.ScreenshotRecognize, action);
        }
    }

    [Fact]
    public void ExplicitlyDisabledActionStaysUnboundEvenWithLegacyField()
    {
        (WindowsHotkeyRegistrar registrar, PortableLayout layout) = CreateRegistrar();
        using (registrar)
        {
            WriteConfig(
                layout,
                """
                {
                  "hotkeys": {
                    "global_screenshot": "Ctrl+Alt+Q",
                    "actions": { "screenshot_recognize": "" }
                  }
                }
                """);
            registrar.InitializeActions();

            HotkeyActionStatus recognize = StatusOf(
                registrar, HotkeyActionCatalog.ScreenshotRecognize);
            Assert.Null(recognize.ConfiguredHotkey);
            Assert.Null(recognize.RegisteredHotkey);
            Assert.Empty(_native.ActiveIds);
        }
    }

    [Fact]
    public void StartupRegistrationFailureIsReportedAndRetrySucceeds()
    {
        _native.CanRegister = false;
        (WindowsHotkeyRegistrar registrar, PortableLayout layout) = CreateRegistrar();
        using (registrar)
        {
            registrar.InitializeActions();

            HotkeyActionStatus recognize = StatusOf(
                registrar, HotkeyActionCatalog.ScreenshotRecognize);
            Assert.Equal("Ctrl+Alt+Q", recognize.ConfiguredHotkey);
            Assert.Null(recognize.RegisteredHotkey);
            Assert.NotNull(recognize.Error);
            Assert.Contains("占用", recognize.Error);

            _native.CanRegister = true;
            Assert.True(registrar.SetActionHotkey(
                HotkeyActionCatalog.ScreenshotRecognize, "Ctrl+Alt+Q", out string? error));
            Assert.Null(error);
            HotkeyActionStatus retried = StatusOf(
                registrar, HotkeyActionCatalog.ScreenshotRecognize);
            Assert.Equal("Ctrl+Alt+Q", retried.RegisteredHotkey);
            Assert.Null(retried.Error);
        }
    }

    [Fact]
    public void ReplaceRegistersPersistsThenReleasesOldKeyOnlyAfterSuccess()
    {
        (WindowsHotkeyRegistrar registrar, PortableLayout layout) = CreateRegistrar();
        using (registrar)
        {
            WriteConfig(
                layout,
                "{\"hotkeys\": {\"global_screenshot\": \"Ctrl+Alt+Q\"}, \"other\": 7}");
            registrar.InitializeActions();
            int oldId = Assert.Single(_native.ActiveIds);

            Assert.True(registrar.SetActionHotkey(
                HotkeyActionCatalog.ScreenshotRecognize, "Ctrl+Shift+P", out string? error));
            Assert.Null(error);

            int newId = Assert.Single(_native.ActiveIds);
            Assert.NotEqual(oldId, newId);
            Assert.False(registrar.TryResolveAction(oldId, out _));
            Assert.True(registrar.TryResolveAction(newId, out string? action));
            Assert.Equal(HotkeyActionCatalog.ScreenshotRecognize, action);

            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(layout.ConfigFile));
            JsonElement hotkeys = document.RootElement.GetProperty("hotkeys");
            Assert.Equal(
                "Ctrl+Shift+P",
                hotkeys.GetProperty("actions").GetProperty("screenshot_recognize").GetString());
            Assert.Equal(
                "Ctrl+Shift+P",
                hotkeys.GetProperty(HotkeyActionCatalog.LegacyGlobalScreenshotField).GetString());
            Assert.Equal(
                7,
                document.RootElement.GetProperty("other").GetInt32());
            foreach (string actionId in HotkeyActionCatalog.Actions)
            {
                if (actionId != HotkeyActionCatalog.ScreenshotRecognize)
                {
                    Assert.Equal(
                        string.Empty,
                        hotkeys.GetProperty("actions").GetProperty(actionId).GetString());
                }
            }
        }
    }

    [Fact]
    public void OccupiedCombinationKeepsOldRegistrationAndConfig()
    {
        (WindowsHotkeyRegistrar registrar, PortableLayout layout) = CreateRegistrar();
        using (registrar)
        {
            const string initialConfig =
                "{\"hotkeys\": {\"global_screenshot\": \"Ctrl+Alt+Q\"}}";
            WriteConfig(layout, initialConfig);
            registrar.InitializeActions();
            int oldId = Assert.Single(_native.ActiveIds);

            _native.CanRegister = false;
            Assert.False(registrar.SetActionHotkey(
                HotkeyActionCatalog.ScreenshotRecognize, "Ctrl+Shift+P", out string? error));
            Assert.Contains("占用", error);

            _native.CanRegister = true;
            Assert.Equal([oldId], _native.ActiveIds);
            Assert.True(registrar.TryResolveAction(oldId, out string? action));
            Assert.Equal(HotkeyActionCatalog.ScreenshotRecognize, action);
            Assert.Equal(initialConfig, File.ReadAllText(layout.ConfigFile));
        }
    }

    [Fact]
    public void CorruptConfigKeepsOldRegistrationAndPreservesFile()
    {
        (WindowsHotkeyRegistrar registrar, PortableLayout layout) = CreateRegistrar();
        using (registrar)
        {
            registrar.InitializeActions();
            int oldId = Assert.Single(_native.ActiveIds);
            const string corruptConfig = "{\"hotkeys\": not-json";
            File.WriteAllText(layout.ConfigFile, corruptConfig);

            Assert.False(registrar.SetActionHotkey(
                HotkeyActionCatalog.ScreenshotRecognize, "Ctrl+Shift+P", out string? error));
            Assert.Contains("配置文件已损坏", error);
            Assert.Equal([oldId], _native.ActiveIds);
            Assert.True(registrar.TryResolveAction(oldId, out _));
            Assert.Equal(corruptConfig, File.ReadAllText(layout.ConfigFile));
        }
    }

    [Fact]
    public void EquivalentCombinationIsNoOpForSameAction()
    {
        (WindowsHotkeyRegistrar registrar, PortableLayout layout) = CreateRegistrar();
        using (registrar)
        {
            WriteConfig(
                layout,
                "{\"hotkeys\": {\"global_screenshot\": \"Ctrl+Alt+Q\"}}");
            registrar.InitializeActions();
            int id = Assert.Single(_native.ActiveIds);

            // 次序/大小写/别名差异解析为同一 native 组合：无操作成功，
            // 不重复注册自身组合后误报占用。
            Assert.True(registrar.SetActionHotkey(
                HotkeyActionCatalog.ScreenshotRecognize, "ALT+CONTROL+q", out string? error));
            Assert.Null(error);
            Assert.Equal([id], _native.ActiveIds);
            Assert.True(registrar.TryResolveAction(id, out string? action));
            Assert.Equal(HotkeyActionCatalog.ScreenshotRecognize, action);
        }
    }

    [Fact]
    public void EquivalentCombinationAcrossActionsIsDuplicate()
    {
        (WindowsHotkeyRegistrar registrar, PortableLayout layout) = CreateRegistrar();
        using (registrar)
        {
            WriteConfig(
                layout,
                "{\"hotkeys\": {\"global_screenshot\": \"Ctrl+Alt+Q\"}}");
            registrar.InitializeActions();
            int recognizeId = Assert.Single(_native.ActiveIds);

            Assert.False(registrar.SetActionHotkey(
                HotkeyActionCatalog.ClipboardRecognize, "Alt+Ctrl+Q", out string? error));
            Assert.Contains("快捷截图识别", error);
            Assert.Equal([recognizeId], _native.ActiveIds);
            Assert.Null(StatusOf(registrar, HotkeyActionCatalog.ClipboardRecognize).RegisteredHotkey);
        }
    }

    [Fact]
    public void CorruptConfigAtStartupSurfacesErrorAndRetryAfterRepairWorks()
    {
        (WindowsHotkeyRegistrar registrar, PortableLayout layout) = CreateRegistrar();
        using (registrar)
        {
            const string corruptConfig = "{\"hotkeys\": not-json";
            WriteConfig(layout, corruptConfig);

            registrar.InitializeActions();

            Assert.Empty(_native.ActiveIds);
            foreach (HotkeyActionStatus status in registrar.GetActionStatuses())
            {
                Assert.Null(status.ConfiguredHotkey);
                Assert.Null(status.RegisteredHotkey);
                Assert.Contains("配置文件已损坏", status.Error);
            }

            // 坏文件保留，设置尝试也以读取错误拒绝而不是假成功。
            Assert.False(registrar.SetActionHotkey(
                HotkeyActionCatalog.ScreenshotRecognize, "Ctrl+Alt+Q", out string? setError));
            Assert.Contains("配置文件已损坏", setError);
            Assert.Equal(corruptConfig, File.ReadAllText(layout.ConfigFile));

            // 修复后重试：正常读取并注册。
            WriteConfig(
                layout,
                "{\"hotkeys\": {\"global_screenshot\": \"Ctrl+Alt+X\"}}");
            registrar.InitializeActions();
            int id = Assert.Single(_native.ActiveIds);
            Assert.True(registrar.TryResolveAction(id, out string? action));
            Assert.Equal(HotkeyActionCatalog.ScreenshotRecognize, action);
            HotkeyActionStatus recognize = StatusOf(
                registrar, HotkeyActionCatalog.ScreenshotRecognize);
            Assert.Equal("Ctrl+Alt+X", recognize.RegisteredHotkey);
            Assert.Null(recognize.Error);
        }
    }

    [Fact]
    public void PartiallyBadConfigDoesNotHalfApplyBindings()
    {
        (WindowsHotkeyRegistrar registrar, PortableLayout layout) = CreateRegistrar();
        using (registrar)
        {
            WriteConfig(
                layout,
                """
                {
                  "hotkeys": {
                    "global_screenshot": "Ctrl+Alt+Q",
                    "actions": {
                      "screenshot_edit": "Ctrl+Alt+P",
                      "screenshot_recognize": 5
                    }
                  }
                }
                """);

            registrar.InitializeActions();

            // 任一字段坏类型：整体读取失败，不能只应用前几个动作。
            Assert.Empty(_native.ActiveIds);
            foreach (HotkeyActionStatus status in registrar.GetActionStatuses())
            {
                Assert.Contains("配置文件已损坏", status.Error);
                Assert.Null(status.ConfiguredHotkey);
                Assert.Null(status.RegisteredHotkey);
            }

            Assert.False(registrar.SetActionHotkey(
                HotkeyActionCatalog.ScreenshotEdit, "Ctrl+Alt+P", out string? error));
            Assert.Contains("配置文件已损坏", error);
        }
    }

    [Fact]
    public void LockedConfigFileAtFirstUseReturnsErrorWithoutThrowing()
    {
        (WindowsHotkeyRegistrar registrar, PortableLayout layout) = CreateRegistrar();
        using (registrar)
        {
            WriteConfig(
                layout,
                "{\"hotkeys\": {\"global_screenshot\": \"Ctrl+Alt+Q\"}}");

            using (FileStream lockStream = File.Open(
                layout.ConfigFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None))
            {
                // 读取阶段的 IO 失败不得从设置调用逃逸。
                Assert.False(registrar.SetActionHotkey(
                    HotkeyActionCatalog.ScreenshotRecognize,
                    "Ctrl+Alt+Q",
                    out string? error));
                Assert.Contains("无法读取配置文件", error);
                Assert.Empty(_native.ActiveIds);

                string? conflict = null;
                var ex = Record.Exception(() => registrar.Register("Ctrl+Alt+Q", out conflict));
                Assert.Null(ex);
                Assert.False(string.IsNullOrEmpty(conflict));
                Assert.Contains("无法读取配置文件", conflict);
            }
        }
    }

    [Fact]
    public void LockedConfigFileKeepsOldRegistration()
    {
        (WindowsHotkeyRegistrar registrar, PortableLayout layout) = CreateRegistrar();
        using (registrar)
        {
            WriteConfig(
                layout,
                "{\"hotkeys\": {\"global_screenshot\": \"Ctrl+Alt+Q\"}}");
            registrar.InitializeActions();
            int oldId = Assert.Single(_native.ActiveIds);

            using (FileStream lockStream = File.Open(
                layout.ConfigFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None))
            {
                Assert.False(registrar.SetActionHotkey(
                    HotkeyActionCatalog.ScreenshotRecognize,
                    "Ctrl+Shift+P",
                    out string? error));
                Assert.Contains("配置文件", error);
                Assert.Equal([oldId], _native.ActiveIds);
                Assert.True(registrar.TryResolveAction(oldId, out _));
            }
        }
    }

    [Fact]
    public void DuplicateBindingAcrossActionsIsRejectedWithOwnerAction()
    {
        (WindowsHotkeyRegistrar registrar, PortableLayout layout) = CreateRegistrar();
        using (registrar)
        {
            registrar.InitializeActions();
            int recognizeId = Assert.Single(_native.ActiveIds);

            Assert.False(registrar.SetActionHotkey(
                HotkeyActionCatalog.ClipboardRecognize, "Ctrl+Alt+Q", out string? error));
            Assert.Contains("快捷截图识别", error);
            Assert.Equal([recognizeId], _native.ActiveIds);
            Assert.Null(StatusOf(registrar, HotkeyActionCatalog.ClipboardRecognize).RegisteredHotkey);
        }
    }

    [Theory]
    [InlineData("Q")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+XYZ")]
    [InlineData("Meta+Q")]
    [InlineData("Ctrl+中")]
    [InlineData("Ctrl+É")]
    [InlineData("Shift+A")]
    [InlineData("Shift+1")]
    public void InvalidCombinationsKeepOldBinding(string invalidHotkey)
    {
        (WindowsHotkeyRegistrar registrar, PortableLayout layout) = CreateRegistrar();
        using (registrar)
        {
            registrar.InitializeActions();
            int oldId = Assert.Single(_native.ActiveIds);

            Assert.False(registrar.SetActionHotkey(
                HotkeyActionCatalog.ClipboardRecognize, invalidHotkey, out string? error));
            Assert.False(string.IsNullOrEmpty(error));

            Assert.Equal([oldId], _native.ActiveIds);
            HotkeyActionStatus clipboard = StatusOf(
                registrar, HotkeyActionCatalog.ClipboardRecognize);
            Assert.Null(clipboard.ConfiguredHotkey);
            Assert.Null(clipboard.RegisteredHotkey);
        }
    }

    [Fact]
    public void DisablePersistsUnboundAndReleaseThenResetRestoresDefault()
    {
        (WindowsHotkeyRegistrar registrar, PortableLayout layout) = CreateRegistrar();
        using (registrar)
        {
            registrar.InitializeActions();
            int firstId = Assert.Single(_native.ActiveIds);

            Assert.True(registrar.SetActionHotkey(
                HotkeyActionCatalog.ScreenshotRecognize, null, out string? disableError));
            Assert.Null(disableError);
            Assert.Empty(_native.ActiveIds);
            Assert.False(registrar.TryResolveAction(firstId, out _));
            HotkeyActionStatus disabled = StatusOf(
                registrar, HotkeyActionCatalog.ScreenshotRecognize);
            Assert.Null(disabled.ConfiguredHotkey);
            Assert.Null(disabled.RegisteredHotkey);

            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(layout.ConfigFile));
            JsonElement hotkeys = document.RootElement.GetProperty("hotkeys");
            Assert.Equal(
                string.Empty,
                hotkeys.GetProperty("actions").GetProperty("screenshot_recognize").GetString());
            Assert.Equal(
                string.Empty,
                hotkeys.GetProperty(HotkeyActionCatalog.LegacyGlobalScreenshotField).GetString());

            Assert.True(registrar.ResetActionToDefault(
                HotkeyActionCatalog.ScreenshotRecognize, out string? resetError));
            Assert.Null(resetError);
            int secondId = Assert.Single(_native.ActiveIds);
            Assert.NotEqual(firstId, secondId);
            Assert.False(registrar.TryResolveAction(firstId, out _));
            Assert.True(registrar.TryResolveAction(secondId, out string? action));
            Assert.Equal(HotkeyActionCatalog.ScreenshotRecognize, action);
        }
    }

    [Fact]
    public void DisableFailureKeepsOldRegistration()
    {
        (WindowsHotkeyRegistrar registrar, PortableLayout layout) = CreateRegistrar();
        using (registrar)
        {
            registrar.InitializeActions();
            int oldId = Assert.Single(_native.ActiveIds);
            File.WriteAllText(layout.ConfigFile, "{\"hotkeys\": not-json");

            Assert.False(registrar.SetActionHotkey(
                HotkeyActionCatalog.ScreenshotRecognize, null, out string? error));
            Assert.Contains("配置文件已损坏", error);
            Assert.Equal([oldId], _native.ActiveIds);
            Assert.True(registrar.TryResolveAction(oldId, out _));
            HotkeyActionStatus recognize = StatusOf(
                registrar, HotkeyActionCatalog.ScreenshotRecognize);
            Assert.Equal("Ctrl+Alt+Q", recognize.ConfiguredHotkey);
        }
    }

    [Fact]
    public void UnknownActionIsRejected()
    {
        using WindowsHotkeyRegistrar registrar = CreateRegistrar().Registrar;

        Assert.False(registrar.SetActionHotkey(
            "text_extract", "Ctrl+Shift+T", out string? error));
        Assert.Contains("未知快捷键动作", error);
        Assert.Empty(_native.ActiveIds);
    }

    [Fact]
    public void LegacyRegisterMapsToRecognizeActionAndCoexistsWithActions()
    {
        (WindowsHotkeyRegistrar registrar, PortableLayout layout) = CreateRegistrar();
        using (registrar)
        {
            Assert.True(registrar.Register("Ctrl+Alt+Q", out string? conflict));
            Assert.Null(conflict);
            int id = Assert.Single(_native.ActiveIds);
            Assert.True(registrar.TryResolveAction(id, out string? action));
            Assert.Equal(HotkeyActionCatalog.ScreenshotRecognize, action);

            // 同键重注册：无操作成功，不产生第二条系统注册。
            Assert.True(registrar.Register("Ctrl+Alt+Q", out conflict));
            Assert.Null(conflict);
            Assert.Equal([id], _native.ActiveIds);

            // 换键后旧 ID 不再触发，也不映射到其他动作。
            Assert.True(registrar.Register("Ctrl+Alt+R", out conflict));
            Assert.Null(conflict);
            int newId = Assert.Single(_native.ActiveIds);
            Assert.False(registrar.TryResolveAction(id, out _));
            Assert.True(registrar.TryResolveAction(newId, out action));
            Assert.Equal(HotkeyActionCatalog.ScreenshotRecognize, action);

            // 旧字段随动作绑定同步，回滚版本语义不变。
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(layout.ConfigFile));
            Assert.Equal(
                "Ctrl+Alt+R",
                document.RootElement.GetProperty("hotkeys")
                    .GetProperty(HotkeyActionCatalog.LegacyGlobalScreenshotField)
                    .GetString());

            // 旧 Unregister 只释放该动作，不动配置。
            registrar.Unregister();
            Assert.Empty(_native.ActiveIds);
            Assert.False(registrar.TryResolveAction(newId, out _));
            using JsonDocument after = JsonDocument.Parse(
                File.ReadAllText(layout.ConfigFile));
            Assert.Equal(
                "Ctrl+Alt+R",
                after.RootElement.GetProperty("hotkeys")
                    .GetProperty(HotkeyActionCatalog.LegacyGlobalScreenshotField)
                    .GetString());
        }
    }

    [Fact]
    public void MultipleActionsRegisterDistinctIdsAndResolveIndependently()
    {
        (WindowsHotkeyRegistrar registrar, PortableLayout layout) = CreateRegistrar();
        using (registrar)
        {
            registrar.InitializeActions();
            Assert.True(registrar.SetActionHotkey(
                HotkeyActionCatalog.ClipboardRecognize, "Ctrl+Alt+C", out string? error));
            Assert.Null(error);
            Assert.True(registrar.SetActionHotkey(
                HotkeyActionCatalog.ToggleToolbar, "Ctrl+Alt+B", out error));
            Assert.Null(error);
            Assert.True(registrar.SetActionHotkey(
                HotkeyActionCatalog.ShowWorkbench, "Ctrl+Alt+W", out error));
            Assert.Null(error);

            Assert.Equal(4, _native.ActiveIds.Count);
            foreach (HotkeyActionStatus status in registrar.GetActionStatuses())
            {
                Assert.Equal(status.ConfiguredHotkey, status.RegisteredHotkey);
                if (status.ActionId == HotkeyActionCatalog.ScreenshotEdit)
                {
                    Assert.Null(status.RegisteredHotkey);
                }
            }

            foreach (int id in _native.ActiveIds)
            {
                Assert.True(registrar.TryResolveAction(id, out string? action));
                Assert.Contains(action, HotkeyActionCatalog.Actions);
            }
        }
    }

    [Fact]
    public void BeginRecordingSuspendsRegistrationsWithoutTouchingConfig()
    {
        (WindowsHotkeyRegistrar registrar, PortableLayout layout) = CreateRegistrar();
        using (registrar)
        {
            WriteConfig(
                layout,
                "{\"hotkeys\": {\"global_screenshot\": \"Ctrl+Alt+Q\"}}");
            registrar.InitializeActions();
            Assert.True(registrar.SetActionHotkey(
                HotkeyActionCatalog.ClipboardRecognize, "Ctrl+Alt+C", out string? error));
            Assert.Null(error);
            int[] oldIds = _native.ActiveIds.ToArray();
            string configAtBegin = File.ReadAllText(layout.ConfigFile);

            registrar.BeginRecording();

            // 挂起：本应用全部注册与 ID 映射释放，旧 ID 不再触发任何动作。
            Assert.True(registrar.IsRecording);
            Assert.Empty(_native.ActiveIds);
            foreach (int id in oldIds)
            {
                Assert.False(registrar.TryResolveAction(id, out _));
            }

            // Configured 与配置文件均不被录入挂起修改。
            HotkeyActionStatus recognize = StatusOf(
                registrar, HotkeyActionCatalog.ScreenshotRecognize);
            HotkeyActionStatus clipboard = StatusOf(
                registrar, HotkeyActionCatalog.ClipboardRecognize);
            Assert.Equal("Ctrl+Alt+Q", recognize.ConfiguredHotkey);
            Assert.Null(recognize.RegisteredHotkey);
            Assert.Equal("Ctrl+Alt+C", clipboard.ConfiguredHotkey);
            Assert.Null(clipboard.RegisteredHotkey);
            Assert.Equal(configAtBegin, File.ReadAllText(layout.ConfigFile));

            // 幂等：重复 Begin 不产生新状态或新注册。
            registrar.BeginRecording();
            Assert.True(registrar.IsRecording);
            Assert.Empty(_native.ActiveIds);
            Assert.Equal(configAtBegin, File.ReadAllText(layout.ConfigFile));
        }
    }

    [Fact]
    public void EndRecordingRestoresConfiguredWithFreshMonotonicIds()
    {
        (WindowsHotkeyRegistrar registrar, PortableLayout layout) = CreateRegistrar();
        using (registrar)
        {
            WriteConfig(
                layout,
                "{\"hotkeys\": {\"global_screenshot\": \"Ctrl+Alt+Q\"}}");
            registrar.InitializeActions();
            Assert.True(registrar.SetActionHotkey(
                HotkeyActionCatalog.ClipboardRecognize, "Ctrl+Alt+C", out _));
            int[] oldIds = _native.ActiveIds.ToArray();
            string configAtBegin = File.ReadAllText(layout.ConfigFile);

            registrar.BeginRecording();
            registrar.EndRecording();

            // 恢复：新 ID 与旧 ID 不重叠，各自解析回原动作，配置未写入。
            Assert.False(registrar.IsRecording);
            int[] newIds = _native.ActiveIds.ToArray();
            Assert.Equal(2, newIds.Length);
            Assert.All(newIds, id => Assert.DoesNotContain(id, oldIds));
            foreach (int id in newIds)
            {
                Assert.True(registrar.TryResolveAction(id, out string? action));
                Assert.Contains(action, HotkeyActionCatalog.Actions);
            }

            foreach (HotkeyActionStatus status in registrar.GetActionStatuses())
            {
                Assert.Equal(status.ConfiguredHotkey, status.RegisteredHotkey);
                Assert.Null(status.Error);
            }

            Assert.Equal(configAtBegin, File.ReadAllText(layout.ConfigFile));

            // 幂等：未挂起时 End 是无操作。
            registrar.EndRecording();
            Assert.False(registrar.IsRecording);
            Assert.Equal(newIds, _native.ActiveIds.ToArray());
        }
    }

    [Fact]
    public void EndRecordingReportsOccupiedComboHonestlyAndRetryRestores()
    {
        (WindowsHotkeyRegistrar registrar, PortableLayout layout) = CreateRegistrar();
        using (registrar)
        {
            WriteConfig(
                layout,
                "{\"hotkeys\": {\"global_screenshot\": \"Ctrl+Alt+Q\"}}");
            registrar.InitializeActions();
            string configAtBegin = File.ReadAllText(layout.ConfigFile);

            registrar.BeginRecording();
            _native.CanRegister = false;
            registrar.EndRecording();

            // 恢复失败：Registered 如实为空并携带错误，Configured 保留，
            // 不把配置伪装成有效注册，也不写配置。
            HotkeyActionStatus recognize = StatusOf(
                registrar, HotkeyActionCatalog.ScreenshotRecognize);
            Assert.Equal("Ctrl+Alt+Q", recognize.ConfiguredHotkey);
            Assert.Null(recognize.RegisteredHotkey);
            Assert.Contains("占用", recognize.Error);
            Assert.Equal(configAtBegin, File.ReadAllText(layout.ConfigFile));

            // 占用解除后重试同一键位：恢复成功且状态清错。
            _native.CanRegister = true;
            Assert.True(registrar.SetActionHotkey(
                HotkeyActionCatalog.ScreenshotRecognize, "Ctrl+Alt+Q", out string? error));
            Assert.Null(error);
            HotkeyActionStatus restored = StatusOf(
                registrar, HotkeyActionCatalog.ScreenshotRecognize);
            Assert.Equal("Ctrl+Alt+Q", restored.RegisteredHotkey);
            Assert.Null(restored.Error);
        }
    }

    [Fact]
    public void SetActionDuringRecordingEndsRecordingThenAppliesRegularTransaction()
    {
        (WindowsHotkeyRegistrar registrar, PortableLayout layout) = CreateRegistrar();
        using (registrar)
        {
            WriteConfig(
                layout,
                "{\"hotkeys\": {\"global_screenshot\": \"Ctrl+Alt+Q\"}}");
            registrar.InitializeActions();
            int oldId = Assert.Single(_native.ActiveIds);

            registrar.BeginRecording();
            Assert.True(registrar.SetActionHotkey(
                HotkeyActionCatalog.ScreenshotRecognize, "Ctrl+Shift+P", out string? error));
            Assert.Null(error);

            // 保存事务本身结束了录入：换键成功、持久化完成、新 ID 生效。
            Assert.False(registrar.IsRecording);
            int newId = Assert.Single(_native.ActiveIds);
            Assert.NotEqual(oldId, newId);
            Assert.True(registrar.TryResolveAction(newId, out string? action));
            Assert.Equal(HotkeyActionCatalog.ScreenshotRecognize, action);
            HotkeyActionStatus recognize = StatusOf(
                registrar, HotkeyActionCatalog.ScreenshotRecognize);
            Assert.Equal("Ctrl+Shift+P", recognize.ConfiguredHotkey);
            Assert.Equal("Ctrl+Shift+P", recognize.RegisteredHotkey);

            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(layout.ConfigFile));
            Assert.Equal(
                "Ctrl+Shift+P",
                document.RootElement.GetProperty("hotkeys")
                    .GetProperty("actions")
                    .GetProperty("screenshot_recognize")
                    .GetString());
        }
    }

    [Fact]
    public void SetActionFailureDuringRecordingKeepsOldBindingEffective()
    {
        (WindowsHotkeyRegistrar registrar, PortableLayout layout) = CreateRegistrar();
        using (registrar)
        {
            WriteConfig(
                layout,
                "{\"hotkeys\": {\"global_screenshot\": \"Ctrl+Alt+Q\"}}");
            registrar.InitializeActions();
            string configAtBegin = File.ReadAllText(layout.ConfigFile);

            registrar.BeginRecording();

            // 仅新组合被其他应用占用：先按 Configured 恢复旧键，再换键失败；
            // 旧绑定继续有效（已重新注册），配置与错误如实。
            _native.RefusedCombos.Add((HotkeyModifiers.Control | HotkeyModifiers.Shift, 'P'));
            Assert.False(registrar.SetActionHotkey(
                HotkeyActionCatalog.ScreenshotRecognize, "Ctrl+Shift+P", out string? error));
            Assert.Contains("占用", error);
            Assert.False(registrar.IsRecording);
            HotkeyActionStatus recognize = StatusOf(
                registrar, HotkeyActionCatalog.ScreenshotRecognize);
            Assert.Equal("Ctrl+Alt+Q", recognize.ConfiguredHotkey);
            Assert.Equal("Ctrl+Alt+Q", recognize.RegisteredHotkey);
            Assert.Equal(configAtBegin, File.ReadAllText(layout.ConfigFile));

            _native.CanRegister = true;
            int restoredId = Assert.Single(_native.ActiveIds);
            Assert.True(registrar.TryResolveAction(restoredId, out string? action));
            Assert.Equal(HotkeyActionCatalog.ScreenshotRecognize, action);
        }
    }

    [Fact]
    public void SaveFailureDuringRecordingKeepsOldBindingRegistered()
    {
        (WindowsHotkeyRegistrar registrar, PortableLayout layout) = CreateRegistrar();
        using (registrar)
        {
            WriteConfig(
                layout,
                "{\"hotkeys\": {\"global_screenshot\": \"Ctrl+Alt+Q\"}}");
            registrar.InitializeActions();

            registrar.BeginRecording();
            File.WriteAllText(layout.ConfigFile, "{\"hotkeys\": not-json");

            // 保存异常：录入先被结束并恢复旧键，换键在持久化时失败；旧
            // 绑定（已恢复的注册与 Configured）继续有效。
            Assert.False(registrar.SetActionHotkey(
                HotkeyActionCatalog.ScreenshotRecognize, "Ctrl+Shift+P", out string? error));
            Assert.Contains("配置文件已损坏", error);
            Assert.False(registrar.IsRecording);
            int restoredId = Assert.Single(_native.ActiveIds);
            Assert.True(registrar.TryResolveAction(restoredId, out string? action));
            Assert.Equal(HotkeyActionCatalog.ScreenshotRecognize, action);
            HotkeyActionStatus recognize = StatusOf(
                registrar, HotkeyActionCatalog.ScreenshotRecognize);
            Assert.Equal("Ctrl+Alt+Q", recognize.ConfiguredHotkey);
            Assert.Equal("Ctrl+Alt+Q", recognize.RegisteredHotkey);
            Assert.Equal("{\"hotkeys\": not-json", File.ReadAllText(layout.ConfigFile));
        }
    }

    [Fact]
    public void DisposeDuringRecordingReleasesWithoutLeakingRegistrations()
    {
        (WindowsHotkeyRegistrar registrar, PortableLayout layout) = CreateRegistrar();
        WriteConfig(
            layout,
            "{\"hotkeys\": {\"global_screenshot\": \"Ctrl+Alt+Q\"}}");
        registrar.InitializeActions();
        registrar.BeginRecording();

        // 挂起中 Dispose：注册已释放，不抛出、不残留。
        registrar.Dispose();
        Assert.Empty(_native.ActiveIds);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结果。
        }
    }

    private sealed class RecordingHotkeyNative : IHotkeyNativeMethods
    {
        private readonly HashSet<int> _activeIds = [];

        public bool CanRegister { get; set; } = true;

        /// <summary>模拟被其他应用占用的具体组合（忽略 NoRepeat 位）。</summary>
        public HashSet<(HotkeyModifiers Modifiers, uint VirtualKey)> RefusedCombos { get; } = [];

        public IReadOnlyCollection<int> ActiveIds => _activeIds.Order().ToArray();

        public List<(int Id, HotkeyModifiers Modifiers, uint VirtualKey)> Registrations { get; } = [];

        public bool Register(nint windowHandle, int id, HotkeyModifiers modifiers, uint virtualKey)
        {
            if (!CanRegister
                || RefusedCombos.Contains((modifiers & ~HotkeyModifiers.NoRepeat, virtualKey)))
            {
                return false;
            }

            Registrations.Add((id, modifiers, virtualKey));
            return _activeIds.Add(id);
        }

        public bool Unregister(nint windowHandle, int id) => _activeIds.Remove(id);
    }
}

# 桌面悬浮工具栏与贴边隐藏执行计划

> 状态:已实施(Platform 感应条机制 + App 窗口/状态机/配置/自检,默认不启用)。设置页 UI 开关为后续独立 PR;当前通过 `app_settings.json` 的 `floating_toolbar.enabled` 启用。悬浮工具栏为独立置顶小窗,支持贴边自动隐藏;停靠隐藏时靠屏幕边缘的不可见感应条揭示,全程事件驱动、空闲零轮询。

## 1. 目标与非目标

### 1.1 目标

- 常驻桌面的小型置顶工具栏(截图识别、显示主窗口、设置等入口),不进任务栏与 Alt+Tab。
- 支持拖动;拖近某条屏幕边缘(阈值内)松手即吸附该边。
- 贴边吸附后自动隐藏;鼠标移到该边缘 2~3px 感应区即揭示;鼠标离开工具栏短暂停留后自动收回。
- 揭示/隐藏完全事件驱动:空闲时不消耗定时器、钩子或 CPU。
- 揭示与悬停不抢占前台焦点(用户正在打字不受干扰)。
- 多显示器与 Per-Monitor v2 DPI 下几何正确;显示器热插拔后重算。
- 全屏应用(游戏、视频)期间不误揭示、不遮挡。

### 1.2 非目标(v1 明确不做)

- 异形/局部透明点击穿透(per-pixel alpha 窗口)。
- 缩放/折叠为"露出一个把手"的半隐藏态。
- 多套皮肤、动画过渡曲线精细调优(仅直接显隐,v2 可加滑入)。
- 工具栏内嵌输入框(需要键盘焦点,与不激活策略冲突)。

## 2. 参考路线调研结论

参考路线:"停靠隐藏时沿边缘放一条 2~3px 的透明 Tool 窗口,靠 enterEvent 揭示;WM_NCHITTEST 返回 HTTRANSPARENT 实现悬停可感知但不吃点击"。

结论:**路线主体成立(边缘感应条 + 事件驱动 + 零轮询),但 HTTRANSPARENT 的用法需要修正**,这是本方案与参考路线唯一的实质分歧。

### 2.1 HTTRANSPARENT 的两个事实

1. `WM_NCHITTEST` 返回 `HTTRANSPARENT` 的窗口在命中测试中被**整体跳过**:它收不到任何鼠标消息(包括 hover/enter),鼠标事件直接落到下层窗口。因此感应条**不能**返回 HTTRANSPARENT——那等于让感应条既"不吃点击"也"不可感知",揭示机制失效。该路线中"悬停可感知"与"HTTRANSPARENT"互斥。
2. 官方语义中 HTTRANSPARENT 的穿透只保证发生在**同线程窗口之间**("the message is sent to underlying windows in the same thread")。悬浮工具栏是浮在其它进程窗口之上的顶层窗口,跨进程穿透不可靠,不能作为点击穿透手段。

### 2.2 正确的 Win32 工具组合

| 需求 | 工具 |
| --- | --- |
| 感应条视觉为零但可感知 | `WS_EX_LAYERED` + `SetLayeredWindowAttributes(LWA_ALPHA, 1)`:alpha=1/255 人眼不可见,但窗口仍可命中、可收 `WM_MOUSEMOVE`。注意 alpha=0 会被系统视为完全透明而**穿透命中**,1 是关键下限 |
| 感应条不抢焦点、不在任务栏出现 | `WS_EX_NOACTIVATE` + `WS_EX_TOOLWINDOW` |
| 感应条不吃点击 | 不存在完美手段(可命中即占用命中区);靠 2~3px 极窄 + `WM_MOUSEACTIVATE` 返回 `MA_NOACTIVATE`(点击落在条上时保持前台不变、不弹窗)+ 揭示即隐藏自身,把影响压到可忽略 |
| 工具栏点击不激活 | 子类化 `WM_MOUSEACTIVATE` 返回 `MA_NOACTIVATE`;显示用 `ShowWindow(SW_SHOWNOACTIVATE)` |
| 进程内窗口间穿透(如感应条残留时事件转发到工具栏) | 此场景才可用 HTTRANSPARENT(同进程/同线程) |
| 跨进程穿透(异形窗口) | 仅 per-pixel alpha(`UpdateLayeredWindow`)可靠;WinUI 3 窗口不支持,v1 规避 |

### 2.3 候选路线对比

| 路线 | 空闲成本 | 主要问题 | 结论 |
| --- | --- | --- | --- |
| A. 边缘感应条(参考路线,修正后) | 零(纯窗口消息) | 占用边缘 2~3px 命中区;需处理全屏、任务栏同边、多屏 | **采用** |
| B. Shell AppBar(`ABM_NEW` + `ABS_AUTOHIDE`) | 零 | 与任务栏(本身常是同边 autohide appbar)及其它 appbar 冲突;注册边会参与 workspace 保留,副作用大;第三方 autohide 集成脆弱 | 否决 |
| C. `WH_MOUSE_LL` 低级鼠标钩子 | 零但全局 | 挂全局钩子影响整机输入延迟,超时会被系统静默摘除,安全软件易误报 | 否决 |
| D. `GetCursorPos` 定时轮询 | 持续轮询 | 违背零轮询要求;`DispatcherQueueTimer` 常驻 | 否决 |

## 3. 总体设计

### 3.1 窗口拓扑

```
App UI 线程(DispatcherQueue,一个消息泵)
├── MainWindow(HWND,现有)
├── FloatingToolbarWindow(WinUI Window,代码构建,常驻仅隐藏)
│     无边框 · AlwaysOnTop · 不进 switchers · WS_EX_TOOLWINDOW
│     WM_MOUSEACTIVATE -> MA_NOACTIVATE;显示走 SW_SHOWNOACTIVATE
└── EdgeSensorWindow(纯 Win32 HWND,每停靠边一个)
      WS_POPUP · WS_EX_LAYERED(alpha=1) · WS_EX_TOOLWINDOW · WS_EX_NOACTIVATE
      HWND_TOPMOST;宽/高 2~3 physical px,贴停靠边
```

三个窗口同线程,消息由现有 XAML 消息泵分发,无需新线程。

### 3.2 状态机

```
             鼠标进入感应条(WM_MOUSEMOVE)          鼠标离开工具栏 + linger 超时
  Hidden ───────────────────────────────▶ Revealed ────────────────────────────▶ Hidden
     ▲  (感应条 TopMost 挂边)                (感应条隐藏;工具栏 SW_SHOWNOACTIVATE;
     │                                       PointerExited 启动单次 linger 计时)
     │ 拖动释放时距边 ≤ 阈值                    拖动空白区(PointerPressed + Capture)
     └──────────── Dragging ◀──────────────── Revealed
                   (松手吸附→Hidden;否则自由浮动=Pinned 常显,无感应条)
```

- **Hidden**:工具栏 `ShowWindow(SW_HIDE)`;感应条 `SetWindowPos(HWND_TOPMOST)` 贴边。
- **Revealed**:先隐藏感应条再显示工具栏;`PointerEntered` 取消未到期的 linger 计时;`PointerExited` 启动 `DispatcherQueueTimer` 单次计时(默认 600ms)。计时器只在交互窗口期存在,空闲不存在——满足零轮询。
- **Dragging**:拖动期间感应条销毁;松手时若工具栏中心距任一可停靠边 ≤ 24 physical px 则吸附并进入 Hidden,否则 Pinned 常显。
- **Pinned**(`auto_hide=false` 或拖离边缘):常显,无感应条。
- 全屏抑制:感应条 `WM_MOUSEMOVE` 处理器内同步检测前台窗口是否覆盖其所在显示器(`GetForegroundWindow` rect == monitor rect);全屏则忽略该事件。零额外开销。

### 3.3 模块落位

| 位置 | 新增内容 | 职责 |
| --- | --- | --- |
| `VibeOCR.Platform/Windows/` | `ScreenEdgeGeometry.cs` | 纯函数:monitor rect + 边 + DPI → 感应条矩形/工具栏停靠矩形;吸附边判定;任务栏同边判定(`ABM_GETTASKBARPOS`) |
| `VibeOCR.Platform/Windows/` | `EdgeSensorWindow.cs` + `IEdgeSensorNativeMethods` | 感应条 HWND 创建/贴边/隐藏/销毁;`PointerEntered` 事件;P/Invoke 走接口 seam(仿 `TrayIconService`) |
| `VibeOCR.App/Features/FloatingToolbar/` | `FloatingToolbarWindow.cs` | WinUI 代码构建窗口(仿 `ScreenRegionPicker`:borderless、`IsAlwaysOnTop`、`IsShownInSwitchers=false`),XAML 按钮行 |
| `VibeOCR.App/Features/FloatingToolbar/` | `FloatingToolbarController.cs` | 状态机 + linger 计时 + 拖动/吸附 + 持久化;构造注入 `Func<TimeSpan>` 时钟与动作接口以便单测 |
| `VibeOCR.App` | `App.xaml.cs` 装配 | `InitializeDesktopShell` 创建控制器;`DisposeDesktopShellAsync` 注销;主窗口关闭即全部销毁 |
| 配置 | `app_settings.json` 新节点 | `floating_toolbar: { enabled, edge, auto_hide, linger_ms }`;读写走 `AppSettingsStore`,字段级容错(缺失/非法回退默认) |

停靠位置为贴边居中(不持久化偏移);停靠显示器由工具栏当前所在显示器运行时推导,不做 monitor id 持久化,显示器消失时回落最近显示器。

按钮动作直接复用现有入口:截图识别走 `MainWindow.RecognizeScreenshotAsync`(与热键同路径,触发前先隐藏工具栏自身,避免被截进选区背景);显示主窗口走 `ShowMainWindow`;设置走 `ShowAndNavigate("settings")`。

### 3.4 持久化与生命周期

- 停靠边、显示器、横向偏移、`auto_hide`、`linger_ms` 写入 `app_settings.json`(原子写,复用现有 store);位置恢复按 monitor 设备 ID 匹配,显示器不存在时回退主显示器顶边。
- 工具栏窗口在功能首次启用时创建并常驻(Hide/ShowWindow 切换),避免 WinUI 窗口冷创建延迟进入揭示路径。
- 退出路径挂入 `DisposeDesktopShellAsync`;`WM_DISPLAYCHANGE`/`WM_DPICHANGED` 触发几何重算(经 `WindowMessageService` 或感应条自身 WndProc,事件驱动)。

## 4. 风险与缓解

| 风险 | 缓解 |
| --- | --- |
| 感应条占用边缘 2~3px,最大化窗口在该区域点击落到条上 | `MA_NOACTIVATE` 保证不夺前台;点击后立即揭示工具栏(行为可预期);全屏应用整段退避;宽度取 2px 下限 |
| 工具栏停靠边与任务栏同边,悬停任务栏误触发/顶层级竞争 | `ScreenEdgeGeometry` 查询任务栏所在边并从可吸附边剔除;文档说明限非任务栏边 |
| `WS_EX_NOACTIVATE`/`MA_NOACTIVATE` 下 XAML 按钮点击可用性(项目无先例) | 实施前 spike 验证(见 §6);fallback:仅用 `MA_NOACTIVATE` 子类化、不用扩展样式;再 fallback:允许点击时激活(体验降级,记录已知问题) |
| alpha=1 layered 窗口被个别安全软件标记 | 极窄、无内容、随主程序签名发布;提供设置一键禁用 |
| 感应条被其它置顶工具盖住导致揭示失灵 | 每次贴边(re-arm)时重设 `HWND_TOPMOST`;托盘双击与热键始终可用作兜底入口 |
| 多显示器/热插拔/缩放变化后几何漂移 | 全部几何用 physical px 计算(仿 `PhysicalRectangle`);`WM_DISPLAYCHANGE` 重挂 |

## 5. 测试与验证

- **Platform 单测**(`ScreenEdgeGeometry`、`EdgeSensorWindow` 状态序列):几何纯函数全覆盖(四边、任务栏边剔除、多屏、DPI 换算);感应条经 `IEdgeSensorNativeMethods` fake 验证贴边/隐藏/重挂调用序;另有真实 Win32 集成测试,`SendMessage` 注入 `WM_MOUSEMOVE` 验证揭示事件与 `WS_EX_NOACTIVATE` 样式。
- **App 单测**(`FloatingToolbarController`、`FloatingToolbarSettings`):注入假时钟与动作记录器,覆盖事件序列——进入感应条→揭示→离开→linger 超时→隐藏重挂;离开后 linger 内返回→取消;拖动吸附四边与任务栏边剔除;全屏抑制;`Suspend/Resume`;`ApplySettings` 停用;显示器热插拔重挂;配置字段级容错与 roundtrip。
- **App 交互自检门**:`VIBEOCR_FLOATING_TOOLBAR_SELF_TEST=1` 启动后强制启用工具栏,向真实感应条注入 `WM_MOUSEMOVE` 驱动完整揭示路径,断言工具栏可见、Dismiss 后恢复隐藏,以进程退出码报告结果。
- **真实交互验证记录**(4K 200% 缩放实机):物理鼠标移入顶边 2px → 工具栏于计算位置揭示(实测与 `GetDockedToolbarRectangle` 差 ≤1px);`MA_NOACTIVATE` 下 XAML 按钮 Click 正常(设置按钮成功打开设置页);点击主窗口类命令后主窗口激活触发 `WM_MOUSELEAVE` → linger 收回(合理让位行为);收回后再次贴边可循环揭示。
- **手动清单**(PR 描述附截图):双屏不同 DPI、任务栏四边、全屏视频/游戏退避、打字时揭示不夺焦点、系统睡眠恢复。

质量入口按仓库标准执行:`uv run python scripts/check_quality.py`、Platform tests、`scripts/test_app_ci.ps1`、`scripts/build-release.ps1` + `uv run python scripts/release_smoke.py`。

## 6. 实施前 spike(已全部验证通过)

1. alpha=1 感应条能收到 `WM_MOUSEMOVE` 且视觉不可见(Platform 真实 Win32 集成测试 + 实机物理鼠标注入均通过)。
2. `MA_NOACTIVATE` + `SW_SHOWNOACTIVATE` 下 WinUI 3 XAML `Button.Click` 正常触发(实机点击工具栏设置按钮成功导航设置页)。
3. WinUI Window 用原生 `ShowWindow(SW_HIDE/SW_SHOWNOACTIVATE)` 往复,XAML 渲染恢复正常(自检门 exit=0)。

## 7. 里程碑切分

1. **PR1 `feat(platform)`**:感应条窗口 + 几何纯函数 + 单测(无 UI 接入,零行为变化)。✅
2. **PR2 `feat(app)`**:工具栏窗口、状态机、揭示/隐藏、设置读写与自检门(默认 `enabled=false`)。✅(拖动吸附、多屏重挂、全屏退避一并交付)
3. **PR3 `feat(app)`**(待做):设置页开关接入(WebAssets Settings)与托盘右键菜单入口。

## 8. 决策(已定)

- 默认开关:`enabled=false` 首发,当前通过 `app_settings.json` 启用;设置页 UI 开关为后续 PR。
- 按钮集合:v1 固定为 截图识别 / 显示主窗口 / 设置 / 收回到边缘;粘贴识别待确认 `RecognitionViewModel` 剪贴板入口后追加。
- linger 默认时长:600ms(可配,100~5000ms 钳制)。

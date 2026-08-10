# VibeOCR Next Web 工作台：发现与决策

## Requirements

- 按“Web 工作台 + 原生深宿主”架构完整实施，而非只输出方案。
- 使用适当 Web 框架，统一页面风格并提升现代感和操作效率。
- 使用子代理组织独立工作包，并补充、调整审查与测试。
- 最终达到功能、可靠性、无障碍、CI 和真实发布候选验收标准。

## Repository Findings

- 仓库是 Windows-only .NET 10 WinUI 前端，WebAssets 已使用 WebView2、锁定 Node/TypeScript。
- 当前主窗口使用 WinUI `NavigationView` 加七个 XAML 页面；单次识别页内部嵌入 WebView2。
- 当前 WebAssets 已包含图片 Canvas 编辑器、结果渲染、严格 CSP 和 Node 测试。
- preview-only seam 已有约 439 行 C# host/router，说明扩展时必须避免逐按钮消息类型膨胀。
- 截图、文件选择、剪贴板、托盘、全局快捷键、单实例、更新器和 runtime 安装天然留在 C#。
- 当前 WebAssets `package.json` 只有 `node --test`，项目文件直接复制原始 `index.html/src/*.js/*.css`；尚无生产 bundler。
- `.ci/project.json` 已在 bootstrap 中执行 `npm ci`，后续可在现有质量/发布入口增加 typecheck、test、build。
- 当前 checkout 已同步到 `origin/main` 的 `06ca090`，并创建 `codex/web-workbench` 分支。
- `.git/hooks` 只有 sample 文件，本 clone 没有已安装的本地 hook。
- 变更前 `scripts/check_quality.py` 基线通过：Ruff、40 个 runtime tests、14 个 Web tests 全部通过。
- Codex 工作区依赖只提供 Node/Python/Git，没有 .NET SDK；完成 App/Platform/release 验收需要另行提供仓库锁定的 .NET 10.0.302。
- 已通过微软 `dotnet-install.ps1` 将锁定 SDK 10.0.302 安装到 Git 忽略的 `build/tools/dotnet`；后续显式使用该路径，不修改系统安装。
- locked restore 后变更前 .NET 基线通过：83 个 Platform tests、75 个 App tests，均 0 fail/0 skip。
- bridge 深模块最终采用全局 revision + session 语义；WebView 恢复后忽略旧 session/旧 revision，副作用命令不自动重放。
- 动态资源优先使用同源 `https://app.vibeocr/__resource/{opaqueLease}` 并由 WebResourceRequested 只读拦截，避免跨 host CORS 与路径泄露。
- npm 已锁定 React 19.2.8、React Router 8.3.0、Fluent UI React 9.74.5 和 Fluent icons 2.0.335。
- Fluent UI React v9 使用 Griffel 运行时样式；现有 CSP `style-src 'self'` 不能直接假定兼容，必须在首个真实 WebView tracer bullet 验证 nonce/renderer 策略，禁止用 `unsafe-inline` 草率放宽。
- 视觉复核确认采用“精密、安静、暖橙信号色的 OCR 检查工作台”，迁移时不展示当前为空 handler 或长期禁用的伪能力，应由 capability gate 控制。
- 测试复核确认当前 `.ts` 只是转发手工 `.js`，存在测试/源码漂移假绿；迁移后 TypeScript 必须成为唯一真源。
- 当前 release build/csproj 复制源码 WebAssets，没有 Vite 产物闭包校验；真实候选 smoke 也尚未等待 Web ready handshake。
- `scripts/test_app_ci.ps1` 用固定通过数量作为门禁，新增测试时脆弱；应改为 `total > 0`、全部执行且全部通过，并对真实 WebView smoke 使用独立明确门禁。
- 子代理曾将 PreviewHost 判断为未接入产品；主线程复核显示 `RecognitionPage` 实际创建并初始化 PreviewHost，因此正确缺口是“无真实打包 WebView handshake 测试”，不是“产品未接入”。
- React 展示包已实现七个 hash 路由、统一 Fluent 壳、亮/暗/system 主题、900px 紧凑布局、forced-colors/reduced-motion 和 capability gate；演示状态明确标注未接宿主，不伪造操作成功。
- 固定本地 CSP nonce `vibeocr-style` 已同时配置到 index 和 Griffel RendererProvider，构建产物不含 `unsafe-inline`；仍需真实 WebView2 tracer 验证运行时样式注入。
- 初始 Vite bundle 原始约 597 KiB、gzip 约 173 KiB；功能正确但后续应按页面动态导入拆包，避免单入口继续增长。
- 主线程代码复核确认当前 `AppViewState` 仅含 route/theme/capabilities/runtimeLabel，演示 MessageBar 也固定显示；接入 bridge 时必须改为 bootstrap + revisioned feature state 投影，连接成功后不显示演示态。
- 当前 index CSP 暂时允许 `img-src data:`；最终资源 broker 接入后应收紧回 `img-src 'self' blob:`，二维码/缩略图不使用 data/base64 URL。
- `tsc -b` 生成的 `tsconfig.*.tsbuildinfo` 当前未被忽略；应把 tsBuildInfoFile 定位到已忽略缓存目录并清理工作树副作用。
- 首轮聚合 Web 验证中 typecheck、14 个 legacy tests、3 个 Vitest tests 和 Vite build 已通过；ESLint 暴露主题 state 双写和两个未使用图标，Prettier 还错误覆盖了待迁移伪 TS 范围，需先修正再把门禁视为通过。
- 测试门禁现已按 `format:check → lint → typecheck → test → build → dist 离线闭包` 排列；App TRX 默认验收改为 `total > 0` 且无失败/跳过，避免新增测试时被固定 75 数量阻断。
- 离线 WebAssets 验证器会拒绝外链、目录逃逸、缺失资源、source map、TS 源码、`node_modules`、`unsafe-inline` 与 `unsafe-eval`；release smoke 仍需显式复用这一验证器。

## Architecture Decisions

| Decision | Rationale |
|----------|-----------|
| Web 主界面使用 React/TypeScript/Vite/Fluent UI React v9 | Fluent 2 与 WinUI 视觉接近，React 生态适合复杂工作台与无障碍交互 |
| 使用 hash 路由 | 本地虚拟 host 不需要处理 SPA history fallback，刷新和激活更稳定 |
| C# 输出语义化状态码/快照，Web 负责本地化文案 | 避免把 XAML ViewModel 的中文状态字符串固化进 bridge |
| bridge 固定 envelope，命令使用 discriminated union | 保持 interface 小，同时让两端有静态类型和白名单验证 |
| 每个 feature 状态包含 revision | Web 可忽略异步到达的陈旧快照，不镜像 `PropertyChanged` 顺序 |
| 原生恢复页不参与常规导航 | 它只负责 WebView2/WebAssets 致命故障，避免常规 UI 双实现 |

## Testing Decisions

- 已确认 seam：Web `HostBridge`、C# `WorkbenchApplication`、业务工作流公开命令/状态、生产 WebAssets、真实 WinUI/WebView2 启动。
- TDD 按纵向 tracer bullet 执行；不先批量写完所有测试。
- Web 纯逻辑和页面行为使用 Vitest/Testing Library；生产 bridge 使用 fake transport 测试。
- .NET 测试观察 dispatcher/工作流的公开结果，不 mock 私有内部协作者。
- WebView2 进程失败依赖 `ProcessFailed`/`BrowserProcessExited` 信号，最多一次受控重载/重建，然后显示原生恢复页。
- 不用脆弱截图快照替代行为验收；UI 截图用于 review 证据。

## Official References

- Fluent UI React v9: https://fluent2.microsoft.design/get-started/develop
- Vite: https://v8.vite.dev/guide/
- React Router HashRouter: https://reactrouter.com/api/declarative-routers/HashRouter
- Vitest: https://vitest.dev/guide/
- React Testing Library: https://testing-library.com/docs/react-testing-library/intro/
- WebView2 process recovery: https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/process-related-events

## Issues Encountered

| Issue | Resolution |
|-------|------------|
| catchup 命令意外生成根级 `uv.lock` | 已删除，并记录为禁止重复的执行方式 |
| 当前 shell 未发现任何 .NET SDK，`global.json` 要求 10.0.302 | Platform/App 基线在测试启动前失败；需要定位捆绑 SDK 或安装锁定 SDK 后再验证 |
| 子代理测试命令再次生成根级 `uv.lock` | 子代理完成后已删除；后续 `uv run --no-sync` 验证未再生成根锁 |

## Release Integration Findings

- App 项目原先把原始 `index.html/src/*.js/*.css` 复制到输出，Vite build 即使成功也不会进入产品；现在契约要求只复制已验证的 `WebAssets/dist`。
- `build-release.ps1` 是权威真实候选入口，Web locked install、production build 和离线闭包验证必须发生在 `dotnet publish` 前，失败即终止。

## Visual Direction

- 定位为“高效桌面工作台”，不是营销网站或通用 SaaS 仪表盘。
- 使用 Fluent semantic tokens、克制的暖橙强调色、紧凑工具栏和清晰输入/输出层次。
- 保留现有 Canvas 工作区的编辑器辨识度，但统一按钮、状态、间距、字体层级、空状态和错误状态。
- 优先键盘路径、焦点可见性、中文输入法、forced-colors 与 reduced-motion。
- 响应式以 900px 为现有最小宽度；1180x760 是主要桌面基线。小尺寸使用紧凑导航和面板切换，不把专业工作台简单堆叠成长页面。

## Implementation Findings（2026-08-10）

- 主窗口已切换为单个长期存活 WebView2，并保留独立于 Web 的原生恢复面板；旧七页 XAML/code-behind 已删除，`DesktopWorkbenchCommandHandler` 直接组合现有 ViewModel 与平台适配器。
- bridge 已实现严格 64 KiB UTF-8 边界、封闭命令解析、bootstrap/receipt/state 校验、session/revision 陈旧事件拒绝；资源 broker 使用 opaque lease URL，Web 不接触本地路径，图片和长文本不进入 JSON。
- React 工作台已接入七路由、Fluent v9、统一 tokens、能力门控、原生 route 同步和真实 `window.chrome.webview` transport；普通浏览器明确为 demo，不伪装宿主成功。
- production bundle 已按 app/react/fluent 分组，所有 chunk 小于 500 KiB；CSP 保持无 `unsafe-inline`/`unsafe-eval`，`img-src` 收紧为 `'self' blob:`。
- 功能等价缺口已补齐：React Canvas 支持选择/移动、矩形、椭圆、箭头、文字、马赛克、模糊、裁剪、旋转和撤销/重做；批量/PDF 用有界可翻页窗口覆盖全部项目；QR 只允许打开当前已解码且通过 http/https+无 userinfo 校验的 URL；About 展示宿主版本、许可和固定项目链接。
- 长任务改为“立即 busy 回执 + 后台完成状态事件”，识别、批量、PDF OCR、QR 和更新各自用 generation 抑制 stale completion；取消命令不再被等待中的命令阻塞。
- bootstrap 现在包含 shell 之外的七域初始快照；Settings 的 hotkey editor 以 host 值作为 keyed 初态，宿主变更会重建草稿而不会永久停留在首次渲染值。
- 旧七页 XAML、code-behind、PreviewHost、WebMessageRouter 和对应实现耦合测试已删除；命令处理器直接组合 ViewModel/平台 adapter，不再实例化隐藏页面。
- 批量/PDF/QR 状态使用有界窗口与字段截断，最坏 Unicode 序列化测试证明仍小于 64 KiB；Batch/PDF 通过类型化窗口命令访问全部 item/page。
- 每次 bootstrap 都生成新 session，同时保持 revision 单调；重载前已排队的状态即使晚到，也会因旧 session 被新 renderer 忽略。
- 浏览器 DOM 验证覆盖 900x600 与 1180x760、七个直接 hash 路由、活动导航名称及无横向溢出；代码/行为门禁覆盖中文输入、可访问名称、focus-visible、forced-colors 与 reduced-motion。浏览器截图后端返回空白且完整键盘自动化不稳定，因此缩放视觉、完整键盘路径和高对比度视觉仍需人工复核，未记为自动化通过。
- 本地 WinUI Release build 为 0 warning/0 error；完整质量门禁为 64 Python、33 Web（14 legacy + 19 Vitest）、100 App、83 Platform 全通过。
- 已解析最新正式 Backend 及其绑定 Protocol/SDK identity；完整 release build 生成 `VibeOCR-Next-v0.2.0-win64.zip`、sidecar 与 SPDX SBOM，产物验证和解压后的 packaged bridge-ready release smoke 均通过。
- 构建前 smoke 最初污染产品目录的 `winui-dev` profile，修复为临时产品副本 + production profile + 独立 WebView2 user-data；真实失败还暴露 WebView2 子进程句柄生命周期，现按精确 GUID user-data 命令行清理，不放宽 `no dev profile` 产物门禁。
- `computer-use` 因 Codex 安装目录 `EPERM lstat` 无法初始化；系统截图又无法捕获 WebView2 composition。故视觉证据由真实 ready/window responding、浏览器 DOM 尺寸/七路由/无横向溢出和行为测试共同组成，不能声称完整键盘截图自动化已执行。

## Release main CI Failure（2026-08-10）

- 发布 PR #19 合并后的 main CI run `31349358391` 在 Platform 83 项中的 `SuccessfulStartIsOneShotAndDisposeClearsReady` 失败；产品质量、Web 门禁和前 82 项均通过，CD 因 main CI 失败而按设计跳过。
- 失败发生于 `proc.Dispose()` 返回后的 `Directory.Delete(root, recursive: true)`：`ping.exe` 后代仍将临时目录作为工作目录，Windows 报目录被另一进程占用。
- 当前 `Terminate()` 调用 `process.Kill(entireProcessTree: true)`，随后仅等待父 `cmd.exe`；Microsoft 明确说明父进程 `WaitForExit`/`HasExited` 不反映后代退出状态。
- `WindowsJobObject.Dispose()` 关闭带 `KILL_ON_JOB_CLOSE` 的 handle 会发起整组终止，但实现没有等待 Job 中所有进程退出，因此 `Dispose()` 的“整树已终止”所有权契约未闭合。
- 日志使用短生命周期 `File.AppendAllText`，没有长期文件流；本地精确单测 50 轮通过，说明这是 runner 调度放大的低概率退出竞态，而非版本文件或发布构建差异。
- 官方契约：https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.kill 与 https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects 。
- 修复以 `TerminateJobObject` 终止 Job，并通过 `QueryInformationJobObject(JobObjectBasicAccountingInformation)` 的 `ActiveProcesses` 有界等待归零；父进程 `Process.Kill` 仅保留为 Job API 失败/超时的 fallback。
- 初始 64 后代压力测试在旧实现上仍通过，不能可靠区分修复，已撤销；最终回归 seam 直接向同一 Job 分配两个长生命周期进程，旧实现缺少整组等待契约而确定性 red，新实现返回时两个进程均已退出。
- 本地完整 release build 在 publish 后的 bridge-ready smoke 超时；GitHub PR #20 run `31353677789` 也在同一 smoke 超时，但此前的 Platform 原始失败已转绿（84/84），证明这是第二个独立门禁问题。
- 临时进程探针显示超时时父进程存活且没有对应 WebView2 子进程；可见启动后的 Win32 窗口类为 `#32770`，而非 WinUI 主窗口，且 `AppLog.Initialize` 前没有日志。证据闭合到 production 跨产品互斥提示框：smoke 虽隔离了产品副本和 WebView2 user-data，却仍复用真实 `Local\\VibeOCR.Frontend.Exclusive.v2` 与 production 单实例名，自动化运行会被当前用户会话或 runner 残留命名对象阻塞。
- 正确修复方向不是延长 timeout 或跳过互斥，而是给 `VIBEOCR_SELF_TEST_SMOKE=web-ready` 增加每次随机且严格校验的实例 scope；同一 `SingleInstanceService`/`FrontendExclusiveLock` 生产代码仍被执行，只将 named object 名称隔离，符合 smoke 已有的临时产品副本与唯一 WebView2 user-data 语义。
- 修复后的真实验证发生在 Classic 仍持有 production 跨产品锁的同一用户会话：重新 publish 的 WinUI 包到达 `bridge-ready`；随后权威 `build-release.ps1` 完整生成 ZIP/sidecar/SBOM，解压后的独立 `release_smoke.py` 再次到达 `bridge-ready`。这直接覆盖了原阻塞条件，不依赖延长 30 秒 timeout。
- 发布恢复不能只合并代码修复：`_finalize_main_candidate` 要求 main 的 HEAD commit 修改 `.release/plan.json`，否则写入 `plan-unchanged` sentinel；而直接再次执行 `minor` 会从当前 0.3.0 推到错误的 0.4.0。
- 安全恢复是机械回退尚未发布的 PR #19（恢复已发布 0.2.0 版本/plan 基线），先让生命周期修复进入 main，再通过 CD 的权威 `release prepare --bump minor` 重新生成 0.3.0；不手改生成版本源或 plan。
- 修复分支已推送并创建 ready PR #20；首轮云端 quality、App、Platform 和 CodeQL 均通过，`required` 仅剩 packaged WebView2 smoke 隔离失败。
- PR #20 第二轮 required（run `31355011152`）7分28秒通过，四语言 CodeQL 全绿，并 squash merge 为 main `35a0668`。
- 合并后 main CI run `31355464161` 又在同一 lifecycle fixture 失败，说明首个修复只关闭了“Job 已终止但尚未退出”的等待竞态，没有关闭启动 enrollment 竞态：`Process.Start()` 与 `AssignProcessToJobObject` 之间，`cmd.exe` 已能创建 `ping.exe`；把父进程加入 Job 不会追溯包含既有后代，故 `ActiveProcesses == 0` 仍可能遗漏逃逸子进程。
- 第二层修复用 Toolhelp 进程快照计算 root 的后代闭包：先分配 root，使后续新子进程自动继承 Job；再重复吸收快照中尚未入 Job 的既有后代，直到稳定，最后仍由 `TerminateJobObject + ActiveProcesses` 有界等待。确定性测试显式让 PowerShell 在分配前启动 `ping.exe` 并输出 PID，旧实现 CS1061 red，新实现验证 parent/child 都退出且临时目录可立即删除。

## Productization Requirements（2026-08-10）

- 用户确认一次性交付一个超大 PR；PR 内允许多个逻辑 commit，最终 squash merge，不在功能 PR 中直接修改版本。
- 发布根公开面固定为唯一 `VibeOCR.exe`、`LICENSE`、`CHANGELOG.md`、`app/` 与 `runtime/`；实现依赖、XBF/PRI、WebAssets、机器清单和组件运行时不得继续平铺。
- 新版本是无旧包袱的开发基线：不支持旧布局应用内升级，不迁移、不读取，也不自动删除旧开发版数据；只验证新布局之间的更新和回滚。
- 用户设置、日志、缓存和临时资源默认进入 `%LOCALAPPDATA%\VibeOCR`，用户导出由显式选择位置；更新事务不能覆盖或回滚用户数据。
- Classic 是功能行为契约而不是 Qt 布局模板；单图/截图/剪贴板、识别管道、复制与 Word/Excel 导出、批量、PDF、二维码、Runtime/模型/缓存/更新都进入矩阵，缺少 capability 时阻断而非占位。
- UI 保留 React 19、Fluent UI 控件和原生 Windows 标题栏，功能图标完全切换到 `lucide-react`；原创 SVG 品牌资产确定性派生 ICO/PNG。
- 视觉采用 VibeTable 的冷中性、蓝色主状态、紧凑尺度、轻边框、完整交互状态和无障碍语言，但不引用 VibeTable 源码或复制其数据库布局。
- 1280x800 为主要设计尺寸，1024x720 必须完整可用；覆盖浅/深/系统主题、125%/150%/200% 缩放、forced-colors、reduced-motion 和键盘工作流。
- 不新增永久识别历史；跨路由保留任务，取消只在底层确认停止后结束；重启不自动恢复未完成 OCR。

## Product Layout Findings（2026-08-10）

- 根目录零散文件不是单点 copy bug：`build-release.ps1` 把 WinUI publish、Bootstrapper publish、PyInstaller updater 和文档输出到同一 `$product`；`package_product_release.py` 又在同一根写 component lock、backend、runtime installer 和 product manifest。
- 运行时消费者同样硬编码旧布局：Bootstrapper 默认同根启动 `VibeOCR.WinUI.exe` 并查根 lock/backend/installer；`GitHubUpdateSource` 固定 updater entry 为 `VibeOCR.Bootstrapper.exe`；PowerShell verifier 固定根 required file 列表。
- `PortableLayout` 当前把 production `config/data/output` 放入安装根；`UpdateArtifactCleaner` 甚至要求 DataRoot 必须在 InstallRoot 内。这两处都必须改为 LocalAppData 策略和显式 deployment root。
- WinUI csproj 的 XBF/PRI 回填与 WebAssets `dist` 复制是可靠 publish adapter，应整体发布到 `app/`，不能拆散或移除。
- `product-release-manifest.json` 已承担完整文件 closure 与 hash；新 layout descriptor 只应承担相对路径和布局 schema，不复制 release identity/hash 职责。
- 三个独立 ProductLayout 方案都收敛到版本化 JSON descriptor + 语言内 adapters；分歧主要在跨语言实现形状、legacy 迁移和根 `.exe.config`。用户已明确拒绝 legacy 迁移，因此所有 legacy reader/migration 建议从最终方案删除。
- 推荐的 seam：声明式 `app/metadata/product-layout.json` 是跨语言 interface；Python stage/verify 是构建 implementation，C# net472/net10 是运行时 adapters，Python updater 是替换 adapter，PowerShell 仅编排稳定 CLI。
- `component-identities.json` 既是正式独立 Release identity asset，也必须嵌入 `app/metadata/` 并进入 product release manifest closure；布局重构不能弱化现有 identity 契约。
- 用户侧品牌去除 `Next` 还要求正式 ZIP 从 `VibeOCR-Next-v*-win64.zip` 改为 `VibeOCR-v*-win64.zip`，并同步 `.ci` required assets、更新选择器、checksum/SBOM 与 smoke。
- 依赖分类：schema/path 是 in-process；目录树/ZIP/LocalAppData/进程健康是 local-substitutable；GitHub 下载是 true external，保留现有 HttpClient adapter；Backend/Protocol release input 继续由现有 resolver adapter 负责。

## UI and Test Findings（2026-08-10）

- Web 现有 `@fluentui/react-icons` 只集中覆盖导航和少量工具，业务页大量动作仍只有文字；切换 Lucide 需要删除旧依赖、更新 Vite vendor grouping、集中 IconButton/导航 adapter，并用 lint/源码断言阻止回流。
- 当前橙色主题和 CSS 以 900px 最小宽度、224px rail、大量通用 `work-panel` 为主；新方向应以语义 token 统一蓝色状态、4px spacing、4/6/8px radius、120/200ms motion，并避免每段内容都悬浮成卡片。
- Vitest/jsdom 已覆盖路由、capability gate、QR payload、批量窗口、PDF 选择、Canvas 撤销、busy/cancel 与 About；没有 Playwright、浏览器截图或视觉回归。
- 现有真实 E2E 是 packaged WinUI/WebView2 bridge-ready smoke，不产生截图。新增 Playwright 应使用 mock bridge 只验证稳定壳层/关键状态，真实候选仍由 WebView2 smoke 和 Windows GUI 证据兜底。
- `.ci/project.json` 已有 quality/e2e/release_build/release_smoke 权威入口；新增 Playwright 与 ProductLayout contract 必须进入项目 adapter/quality，而不是在 workflow 重复命令。

## Productization Plan Review（2026-08-10）

- Standards review：2 项硬问题——`CONTRIBUTING.md` 要求大改先建 Issue；计划遗漏 embedded component identities。1 项判断——“layout v1 → v1”命名含混。
- Spec review：当前只有计划、没有实现属于 Phase 9 的预期状态；`findings.md` 的根 `metadata/` seam 与 `app/metadata/` 目标冲突。Scope creep 为 0。
- 主审额外发现：正式资产名仍含 `Next` 会违反对外产品名决策，已加入 `.ci`、更新器和发布 smoke 的同步改名工作。
- 优化结果：Issue 变为实施前置；identity 同时作为独立 Release 资产与 ZIP 内 metadata；所有 seam 统一为 `app/metadata/product-layout.json`；更新契约改称 `schema_version=1` 新树到同 schema。

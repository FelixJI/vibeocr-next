# VibeOCR 产品化实施计划

## Goal

在不削弱 Windows 原生能力、Backend 运行时治理和发布门禁的前提下，一次性交付 VibeOCR 新产品基线：收拢 Windows 发布目录、建立可更新回滚的新布局、按 Classic 行为完成全功能对齐、以 VibeTable 设计语言重构 React/WinUI 界面并全面迁移 Lucide、补齐品牌资产、自动化与视觉测试，最终创建一个合规 Draft PR。

## Current Phase

Phase 15：PR #26 CI 测试失败诊断与修复

## Confirmed Test Seams

用户已批准“Web 工作台 + 原生深宿主”方案，以下 seam 作为 TDD 的已确认测试面：

1. Web `HostBridge`：`bootstrap`、`execute`、`subscribe` 的用户可观察行为。
2. C# `WorkbenchApplication`：领域命令、状态快照、错误与取消语义。
3. 各业务工作流的公开命令与状态快照，不测试内部协作者或私有方法。
4. WebAssets 生产构建与打包 interface：`dist`、CSP、离线资产集合。
5. 真实 WinUI/WebView2 启动 interface：ready handshake、路由激活、进程失败恢复和原生恢复页。
6. `ProductLayout`：安装根、公开入口、内部应用/运行时/元数据、用户数据位置和布局版本的不变量。
7. 更新事务：新布局版本间的 staging、切换、健康确认和失败回滚，不覆盖 `%LOCALAPPDATA%\VibeOCR`。
8. Web 设计系统：语义 token、页面状态模型和 Lucide 图标 adapter 的用户可观察行为。
9. 品牌资产：随仓维护的 ICO/PNG 文件及其应用、发布包引用。

## Phases

### Phase 1：基线、契约与实施拆分

- [x] 建立持续目标和独立 `codex/web-workbench` 分支
- [x] 核对 Git、远端、hooks、AGENTS.md 与现有架构
- [x] 运行变更前最小质量基线并记录结果（Python/Web 通过；.NET 因本机 SDK 缺失待补环境）
- [x] 固定 bridge envelope、状态所有权、路由与资源语义
- [x] 让子代理完成只读架构/测试/视觉拆分复核
- **Status:** complete

### Phase 2：Web 基础设施与契约 tracer bullet

- [x] 引入 React、TypeScript、Vite、Fluent UI React v9、Vitest/Testing Library
- [x] 建立 `HostBridge` production/fake adapters 和首个 red-green 契约测试
- [x] 迁移现有编辑器与结果渲染模块，保持现有行为（React Canvas 支持选择/移动、矩形、椭圆、箭头、文字、马赛克、模糊、裁剪、旋转、撤销/重做；结果文本使用 broker）
- [x] 建立统一主题、布局 tokens 与页面模板
- [x] 让 `npm run typecheck/test/build` 进入质量入口
- **Status:** complete

### Phase 3：C# 深宿主与 WebView2 恢复

- [x] 实现 `WorkbenchApplication`、dispatcher、状态快照和结构化错误
- [x] 用 `WebWorkbenchHost` 替换 preview-only host/router
- [x] 实现只读动态资源 broker，禁止二进制进入 bridge JSON
- [x] 实现 ready handshake、单次恢复、ProcessFailed 处理和原生恢复页
- [x] 补充 .NET interface 测试与真实 WebView2 smoke
- **Status:** complete

### Phase 4：Web Shell 与单次识别纵向切片

- [x] 实现 Web 导航、统一工作台 shell、主题和路由激活
- [x] 迁移单次识别全部操作、状态、原生拖放、编辑和结果动作
- [x] 保持全局快捷键、托盘、单实例和 `--goto recognition`
- [x] 验证 900x600/常规尺寸的 DOM 无横向溢出、中文输入、可访问名称、焦点样式及 forced-colors/reduced-motion 规则；真实 packaged 窗口 bridge-ready（WebView2 composition 截图与完整键盘自动化受工具权限限制，见 findings）
- **Status:** complete-with-tool-limitation

### Phase 5：批量、二维码与 PDF 迁移

- [x] 迁移批量队列、重排、并发、取消与导出；投影仅含无路径、有界 item window
- [x] 迁移二维码生成、识别、保存与仅限已解码 http/https URL 原生打开
- [x] 迁移 PDF 会话、有界缩略图、多选、旋转、删除、OCR 与保存
- [x] 补充 bridge 成功、失败、取消、陈旧 session/revision 与资源边界测试
- **Status:** complete

### Phase 6：设置、关于、诊断与正式切换

- [x] 迁移设置、更新与常规诊断展示
- [x] 保留原生最小恢复页并提供重载、导出诊断、退出
- [x] 默认启用 Web 工作台并删除临时迁移开关
- [x] 删除被替代的 XAML 页面、code-behind、PreviewHost/WebMessageRouter 和实现耦合测试
- [x] 执行删除测试，App 在删除后保持 0 warning/0 error 且全量测试通过
- **Status:** complete

### Phase 7：审查、完整验证与交付

- [x] 独立执行 standards/spec 双轴代码审查并处理发现
- [x] 运行 formatter/lint/typecheck/Web tests/.NET tests
- [x] 运行真实 WinUI publish 与 WebView2 bridge-ready smoke
- [x] 验证 WebAssets 离线/CSP/资产集合和 Web ready 启动里程碑
- [x] 运行包含正式上游 release input 的完整 release build/release smoke
- [x] 更新用户文档、findings.md 与 progress.md
- [x] 所有可自动化验收标准完成；视觉/完整键盘人工复核限制已如实记录
- **Status:** complete-with-documented-visual-limitation

### Phase 8：发布后 main CI 生命周期竞态修复

- [x] 定位失败 run、SHA、job 和首个错误；确认 CD 因 main CI 失败而正确跳过
- [x] 用失败测试连续运行建立反馈环，并记录本地 50 轮未复现的低概率特征
- [x] 核对 `Process.Kill(entireProcessTree)`、父进程等待和 Job Object 生命周期契约
- [x] 经确认后实现 Job Object 整树终止等待和确定性回归测试
- [x] 运行 Platform 全量与 quality；release build 编译/publish 通过，smoke 因本机环境故障转交 GitHub runner
- [x] 在修复 PR 中机械回退未发布的 #19 计划并创建 PR #20；原 supervisor 失败在云端 84/84 通过
- [x] 修复 PR #20 新暴露的 packaged WebView2 smoke 启动隔离问题；本地真实 publish、完整 release build 与解压后 release smoke 均通过
- [x] 合并 PR #20；PR required 与 CodeQL 全绿
- [x] 修复合并后 main CI 暴露的“父进程入 Job 前已启动后代”enrollment 竞态，并重新通过 PR/main CI
- [x] 通过 CD `minor` 重新生成 0.3.0 release PR，并跟踪 main CI、CD 与正式 Release
- **Status:** complete

### Phase 9：产品化方案、基线与独立审查

- [x] 通过多轮需求拷问锁定发布、UI、功能、任务状态、兼容性与交付边界
- [x] 从最新 `origin/main` 创建独立 `codex/vibeocr-productization` worktree
- [x] 核对 AGENTS.md、Git、远端、hooks、现有 PR、v0.3.0 Release 与 main CI
- [x] 盘点发布布局、路径消费者、Classic 功能矩阵、Web 状态与测试缺口
- [x] 写入完整实施方案和验收矩阵
- [x] 对 `ProductLayout` interface 执行三方案独立评审并优化方案
- [x] 依据 CONTRIBUTING.md 创建并关联大改 Issue #23
- [x] 运行变更前最小相关基线，记录环境与现有失败
- **Status:** complete

### Phase 10：产品布局、数据边界与更新事务

- [x] 先为布局 allowlist、路径投影、数据根和更新回滚补充旧实现失败的契约测试
- [x] 建立单一版本化产品布局事实源及跨 C#/Python/PowerShell adapters
- [x] 将公开根收敛为 `VibeOCR.exe`、`LICENSE`、`CHANGELOG.md`、`app/`、`runtime/`
- [x] 将 WinUI/XBF/PRI/WebAssets 与 `app/metadata`、Backend/Installer/Runtime 放入声明目录
- [x] 将 component identities 嵌入 metadata 并保留独立 Release identity asset
- [x] 将正式 ZIP/selector/CI 契约从 `VibeOCR-Next-*` 收敛为 `VibeOCR-*`
- [x] 将设置、日志、缓存和 Web 临时资源迁入 `%LOCALAPPDATA%\VibeOCR`
- [x] 改造 Bootstrapper、更新器、打包器、验证器和 smoke 共同消费布局 interface
- [x] 实现新布局版本间原子替换、健康确认与失败回滚；不兼容、不迁移、不删除旧开发布局
- **Status:** complete

### Phase 11：品牌、设计系统、Lucide 与应用壳

- [x] 建立 Next 自有语义 token，统一浅色/深色/系统主题、状态色、密度、焦点与 motion
- [x] 完全移除 Fluent 功能图标依赖，保留 Fluent UI 控件并统一改用 `lucide-react`
- [x] 创建并直接维护原创 VibeOCR ICO/PNG 品牌资产
- [x] 将品牌应用到 EXE、窗口/任务栏、托盘、Web 壳、启动/恢复页、关于页和更新器
- [x] 重构任务式导航、全局 Runtime/任务状态、通知层级和 1024x720 响应式应用壳
- [x] 保留原生 Windows 标题栏，补齐键盘、forced-colors、reduced-motion 和无障碍语义
- **Status:** complete

### Phase 12：Classic 功能矩阵与工作流体验

- [x] 建立 Classic → Next 行为矩阵，逐项以代码/测试事实判定等价、缺口或需补充能力
- [ ] 重构单图/截图/剪贴板的输入→配置→结果工作区与高级参数渐进披露
- [x] 对齐复制文本/Markdown、Word/Excel 导出；保留既有编辑、撤销/重做和 dirty-state
- [ ] 对齐批量队列、真实取消、部分失败继续、当前/全部导出和跨路由状态保留
- [ ] 对齐 PDF 页面编辑/文字层/OCR、二维码生成/识别及设置/Runtime/缓存/更新能力
- [ ] 保持可配置全局截图快捷键、托盘后台行为、选区编辑和返回主工作区
- [ ] 为各页面覆盖空闲、待开始、加载、运行、取消、空结果、部分成功、可恢复/阻断错误与 Runtime 不可用状态
- **Status:** partial-upstream-blocked；严格等价缺口已记录在 `docs/classic-behavior-matrix.md`

### Phase 13：测试、视觉证据与真实发布验收

- [x] 为新增 interface 补充 C#/Python/PowerShell/TypeScript 定向契约与回归测试
- [x] 增加 mock bridge 驱动的 Playwright，覆盖关键页面、主题、尺寸和状态的稳定截图
- [x] 运行 formatter、lint、typecheck、Web tests、App tests、Platform tests 和相关质量入口
- [x] 执行真实 release build、artifact verifier、ZIP tree/allowlist、更新/回滚和 WebView2 smoke
- [ ] 在 Windows 1024x720/1280x800 与 125%/150%/200% 缩放下记录 GUI 视觉与键盘证据
- [x] 独立执行 Standards/Spec 双轴代码审查并修复所有可操作问题
- **Status:** complete-with-documented-manual-gui-limit；真实高 DPI/完整键盘路径留作 PR 人工复核

### Phase 14：提交、推送与 Draft PR

- [x] 复核最终 diff、生成物、lock、敏感信息、文档与验证记录
- [x] 按完整意图形成多个中文 Conventional Commit，不绕过 hooks
- [x] 推送 `codex/vibeocr-productization` 并创建中文 Draft PR #25
- [x] PR 正文包含根因、变更、影响风险、Classic 矩阵、精确验证结果与 UI 截图
- [x] 确认 GitHub CI/CodeQL 已触发且保持 pending；不擅自合并
- **Status:** complete-delivery；PR 保持 Draft，等待云端门禁和上游 capability

### Phase 15：PR #26 CI 测试失败诊断与修复

- [x] 核对仓库指令、Git 状态、远端、hooks、现有 worktree 与 GitHub CLI 认证
- [x] 确认仅 PR #26 的 `required` 失败，PR #24 全部检查通过
- [x] fetch 远端并 fast-forward 到 PR 当前 head `97c32f2`
- [x] 获取失败 job 日志并建立能命中同一症状的最小红灯反馈环
- [x] 给出 3–5 个可证伪假设、确认根因并提出聚焦修复方案
- [x] 删除 SVG→PNG/ICO 生成链、Edge quality/release 门禁与对应测试
- [x] 将现有 PNG/ICO 作为直接维护的品牌资产，并修正文档事实
- [x] 运行定向门禁、完整 quality 与更新代理测试
- [ ] 提交、推送，复查 PR #26 云端检查与剩余风险
- **Status:** in_progress

## Key Decisions

| Decision | Rationale |
|----------|-----------|
| React + TypeScript + Vite + Fluent UI React v9 | 兼顾现代桌面交互、Fluent 一致性、成熟无障碍能力和快速开发反馈 |
| 单个长期存活 WebView2 | 避免多 renderer、跨页销毁和重复 bridge 生命周期 |
| Web 只拥有展示状态，C# 拥有任务与平台状态 | 防止双向镜像和竞态，保持原生能力集中 |
| 小型 `bootstrap/execute/subscribe` interface | 将复杂工作流藏在深模块后，避免逐按钮 RPC |
| 动态资源使用受控 URL | 保持 64 KiB 消息限制，不传输图片/PDF base64 |
| 原生恢复页永久保留 | WebView2 或 WebAssets 失败时应用仍可诊断、重载和退出 |
| 纵向 red-green 切片 | 每次用一个可观察行为驱动最小实现，避免一次性水平重写 |
| 单个超大 PR、多个逻辑 commit | 用户要求一次性交付；开发和复核仍按完整意图分 commit，最终 squash merge |
| 唯一公开 `VibeOCR.exe` + 内部 `app/`/`runtime/` | 将用户入口与实现文件分离，根目录严格 allowlist |
| 用户数据默认 `%LOCALAPPDATA%\VibeOCR` | 让程序目录可整体替换并避免更新事务覆盖用户数据 |
| 不兼容旧开发布局 | 当前仍处开发阶段，不承担旧数据迁移；同时不自动删除任何旧输出 |
| 保留 Fluent UI 控件、图标完全切换 Lucide | 复用成熟无障碍基础设施，同时统一图标语义和视觉 |
| Next 独立语义 token，对齐 VibeTable 设计语言 | 保持产品族一致但不形成跨仓运行时或源码依赖 |
| 原创 ICO/PNG 品牌资产 | 功能图标与品牌身份分离，图像文件直接随仓维护并接受普通 diff/打包评审 |
| Classic 是行为契约而非 Qt 布局模板 | 保留用户任务、错误和取消语义，重新设计信息架构与响应布局 |
| 不新增永久识别历史 | 避免在未定义隐私、容量与清理契约前持久化敏感识别内容 |

## Acceptance Criteria

- 七个现有目的地与 Classic 已确认行为等价，缺少 Backend/Protocol capability 时阻断完成而非占位或静默隐藏。
- 发布根目录只包含 `VibeOCR.exe`、`LICENSE`、`CHANGELOG.md`、`app/` 与 `runtime/`，且用户 ZIP 不含 PDB 或开发文件。
- Bootstrapper、WinUI、构建、打包、验证和更新共同消费 `app/metadata/product-layout.json`；XBF/PRI/WebAssets、Runtime 与 component identities 均由真实候选验证。
- 用户可见产品目录、入口、界面文案和正式 ZIP 名均使用 `VibeOCR`，不继续暴露 `Next`。
- 新布局版本间更新成功；健康确认失败会回滚完整程序目录；`%LOCALAPPDATA%\VibeOCR` 不参与替换或回滚。
- 旧开发布局不迁移、不读取、不自动删除，也不进入升级兼容测试。
- 全局快捷键、托盘、单实例转发和 `--goto` 正确驱动 Web 路由。
- Web 不直接访问 Backend、任意文件系统或外网。
- 图片、二维码和 PDF 缩略图不通过 bridge JSON 传输。
- WebView2 renderer/browser 失败不会留下不可操作白屏。
- UI 使用 Next 自有语义 token、冷中性表面和蓝色主状态；功能图标只来自 Lucide，品牌只来自原创资产管线。
- 1280x800 为主要设计尺寸，1024x720 必须完整可用；验证 Windows 125%/150%/200% 缩放、键盘焦点、中文输入、forced-colors 和 reduced-motion。
- 页面状态显式覆盖空闲、输入待识别、加载、运行、取消、空结果、部分成功、可恢复/阻断错误与 Runtime 不可用。
- 关键壳层和页面状态具有稳定 Playwright 视觉回归；真实 WinUI 截图与人工键盘路径作为 PR 证据，如环境受限则明确标记未验证。
- formatter、lint、typecheck、Web tests、App tests、Platform tests 全部通过。
- release build 与真实 release smoke 验证打包后的 WebAssets 和 ready handshake。

## Errors Encountered

| Error | Attempt | Resolution |
|-------|---------|------------|
| `planning-with-files` catchup 通过 `uv run` 在无根项目锁的仓库生成了未跟踪 `uv.lock` | 1 | 立即删除；后续仅调用仓库既有环境或封装脚本，避免根级锁副作用 |
| 并行只读基线命令因 `core.hooksPath`/关键词检索无结果返回 exit 1，导致其他输出未汇总 | 1 | 将可选探测改为显式容忍“未配置/未命中”，后续分别读取必要结果，不原样重试 |
| 基线 `dotnet test` 无法启动：`PATH` 中没有 SDK，仓库要求 10.0.302 | 1 | 停止重复运行，先定位工作区捆绑 SDK 或请求安装权限；该结果不计为代码测试失败 |
| 更新 findings 的 patch 上下文不匹配 | 1 | 读取实际文本后缩小上下文重新应用 |
| npm 开发依赖命令将 `@types\\node` 解析为本地路径并报 ENOENT | 1 | 改用引号保护 scoped package 的正斜杠名称 |
| 首次新 TypeScript typecheck 发现旧 `.ts` 同名 JS 循环转发，且 Vite 配置缺 Vitest 类型 | 1 | 暂时从新编译图排除伪 TS，后续逐模块迁移删除；配置改用 `vitest/config` |
| 本地 SDK 就绪后 `--no-restore` 测试缺少 `obj/project.assets.json` | 1 | 先按仓库锁执行 `dotnet restore --locked-mode`，再重跑测试 |
| 首个 Workbench 测试遗漏显式 `using Xunit` | 1 | 补齐测试程序集既有约定后重新确认只因实现缺失而 red |
| Workbench codec 用 collection expression 初始化不可构造的 `IReadOnlySet` | 1 | 改用静态 `HashSet<string>`；忽略随编译失败产生的级联 XAML error |
| 视觉复核 findings patch 上下文有一字差异 | 1 | 用 rg 找到准确行后重试更窄 patch |
| tsbuildinfo 与 ESLint 两次 apply_patch section 结构无效 | 1 | 用包含完整上下文的独立 update/add/delete sections 后成功应用 |
| 前端整体验证发现 ESLint 2 处错误与 Prettier 范围包含待迁移伪 TS | 1 | UI 子代理修复主题单一事实源和未使用图标；主线程收紧格式化范围并重新执行全部 Web 门禁 |
| Windows 沙箱启动 PowerShell 返回错误 1920 | 1 | 经用户批准仅对只读检查使用沙箱外 PowerShell；所有源码写入继续使用 apply_patch |
| 默认 `dotnet` 命中 x86 host，报告没有 SDK | 1 | 改用已安装的 `C:\Program Files\dotnet\dotnet.exe` 10.0.302，不修改 `global.json` |
| 完整 release build 的构建前 WebView2 smoke 在 30 秒内未 bridge-ready | 1 | 云端同样失败；窗口类探针确认本机停在 production 跨产品互斥提示框（`#32770`）。补严格 GUID self-test 实例 scope，隔离单实例/跨产品 named object；当前有 Classic 占锁的同一会话中，真实 publish smoke、完整 release build 和 ZIP release smoke 均转绿 |
| 单独合并代码修复会使 pending 0.3.0 plan 命中 `plan-unchanged`，无法发布 | 1 | 回退未发布 #19 计划到已发布 0.2.0 基线；修复合并后用权威 CD `minor` 重新生成 0.3.0 |
| PR #20 全绿但合并后 main CI 原生命周期用例再次失败 | 2 | `TerminateAndWait` 只等待已入 Job 进程；`cmd.exe` 可在父进程分配前创建 `ping.exe`，而 Windows 不追溯吸收既有后代。新增“既有后代”确定性 red→green，分配根进程后用 Toolhelp 快照闭包吸收启动竞态中的后代，再等待 Job 归零 |

## Notes

- 每完成一个 phase 立即更新本文件和 progress.md。
- 每两次浏览/大范围读取后将关键发现写入 findings.md。
- 不手改 npm/NuGet lock 或生成物；只通过仓库命令更新。
- 不并行修改同一文件；子代理写入范围必须互斥，主代理复核所有产出。
- 任一验收项未达到时保持明确的 partial/pending，不用门禁通过替代功能等价结论。

# VibeOCR Next Web 工作台实施计划

## Goal

在不削弱 Windows 原生能力、Backend 运行时治理和发布门禁的前提下，将 VibeOCR Next 的可见主界面迁移为统一的 React/TypeScript/Fluent Web 工作台，以小而稳定的类型化 interface 连接 C# 原生深宿主，并完成测试、真实打包与故障恢复验收。

## Current Phase

Phase 8：发布后 main CI 生命周期竞态修复（进行中）

## Confirmed Test Seams

用户已批准“Web 工作台 + 原生深宿主”方案，以下 seam 作为 TDD 的已确认测试面：

1. Web `HostBridge`：`bootstrap`、`execute`、`subscribe` 的用户可观察行为。
2. C# `WorkbenchApplication`：领域命令、状态快照、错误与取消语义。
3. 各业务工作流的公开命令与状态快照，不测试内部协作者或私有方法。
4. WebAssets 生产构建与打包 interface：`dist`、CSP、离线资产集合。
5. 真实 WinUI/WebView2 启动 interface：ready handshake、路由激活、进程失败恢复和原生恢复页。

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
- [ ] 合并 PR #20 并跟踪 main CI
- [ ] 通过 CD `minor` 重新生成 0.3.0 release PR，并跟踪 main CI、CD 与正式 Release
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

## Acceptance Criteria

- 七个现有目的地功能等价并使用统一 Fluent 风格。
- 全局快捷键、托盘、单实例转发和 `--goto` 正确驱动 Web 路由。
- Web 不直接访问 Backend、任意文件系统或外网。
- 图片、二维码和 PDF 缩略图不通过 bridge JSON 传输。
- WebView2 renderer/browser 失败不会留下不可操作白屏。
- 已实现 900x600/常规窗口响应式、DPI 无关布局、键盘焦点、中文输入与 forced-colors；DOM/行为/CSS 契约已验证，缩放视觉、完整键盘路径与高对比度视觉因 WebView2 composition 工具限制保留人工复核，不记为自动化通过。
- formatter、lint、typecheck、Web tests、App tests、Platform tests 全部通过。
- release build 与真实 release smoke 验证打包后的 WebAssets 和 ready handshake。

## Errors Encountered

| Error | Attempt | Resolution |
|-------|---------|------------|
| `planning-with-files` catchup 通过 `uv run` 在无根项目锁的仓库生成了未跟踪 `uv.lock` | 1 | 立即删除；后续仅调用仓库既有环境或封装脚本，避免根级锁副作用 |
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

## Notes

- 每完成一个 phase 立即更新本文件和 progress.md。
- 每两次浏览/大范围读取后将关键发现写入 findings.md。
- 不手改 npm/NuGet lock 或生成物；只通过仓库命令更新。
- 不并行修改同一文件；子代理写入范围必须互斥，主代理复核所有产出。
- 任一验收项未达到时保持明确的 partial/pending，不用门禁通过替代功能等价结论。

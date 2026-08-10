# VibeOCR Next Web 工作台进度

## Session: 2026-08-09

### Phase 1–2：基线、契约与 Web tracer bullet

- **Status:** complete-with-documented-visual-limitation
- **Started:** 2026-08-09
- Actions taken:
  - 建立持续目标：完成 Web 工作台、C# 深宿主、测试与真实发布验收。
  - 读取 planning-with-files、route-subagents、codebase-design、frontend-design、tdd 技能。
  - 核对 AGENTS.md、Git 状态、远端、分支、最近提交和实际 hooks。
  - 同步最新 `origin/main`，创建 `codex/web-workbench` 分支。
  - 固定已确认的测试 seam、实施阶段和验收标准。
  - 三个只读子代理分别开始 bridge/C#、测试发布、React/Fluent 复核；视觉和测试复核已返回并由主线程校正冲突。
  - 通过 `npm install --save-exact` 安装并锁定 React、React DOM、React Router、Fluent UI React v9 与 Fluent icons。
  - 完成首个 HostBridge red-green：`bootstrap` 发送 v2 版本化请求并关联响应。
  - 在 `build/tools/dotnet` 安装仓库锁定 .NET SDK 10.0.302，解除本地 .NET 验收环境阻塞。
  - 子代理完成 React/Fluent UI 展示层：七路由、统一壳、主题、capability gate、CSP nonce 和 UI 行为测试。
  - 完成 C# WorkbenchApplication 与 typed navigate codec 两个 red-green tracer bullet。
- Files created/modified:
  - `task_plan.md`（created）
  - `findings.md`（created）
  - `progress.md`（created）
  - `src/dotnet/VibeOCR.App/WebAssets/package.json`（npm generated update）
  - `src/dotnet/VibeOCR.App/WebAssets/package-lock.json`（npm generated update）

## Test Results

| Test | Command | Expected | Actual | Status |
|------|---------|----------|--------|--------|
| Git baseline | `git status -sb` | 独立最新分支、仅计划文件变更 | `codex/web-workbench` 基于 `origin/main`，仅三份计划文件未跟踪 | passed |
| Python/Web quality baseline | `uv run --no-sync python scripts/check_quality.py` | Ruff/runtime/Web tests 通过 | Ruff 通过；40 runtime + 14 Web tests 通过 | passed |
| Platform baseline | `dotnet test ... --no-restore` | 启动锁定 .NET 测试 | 系统未发现 .NET SDK 10.0.302，测试未启动 | blocked-env |
| App baseline | `scripts/test_app_ci.ps1` | 启动 fail-closed App tests | 同一 .NET SDK 缺失，测试未启动 | blocked-env |
| HostBridge red | `npm run test:unit -- src/bridge/client.test.ts` | 实现不存在导致失败 | Vite 无法解析 `./client` | expected-red |
| HostBridge green | 同上 | bootstrap request/response 通过 | 1 test passed | passed |
| 新 TS typecheck | `npm run typecheck` | 新编译图通过 | 发现旧伪 TS 循环转发和 Vite/Vitest 配置类型问题 | failed-known |
| 锁定 SDK 检查 | `build/tools/dotnet/dotnet --info` | 使用 10.0.302 | SDK 10.0.302 / runtime 10.0.10 | passed |
| .NET baseline retry | Platform/App `--no-restore` | 启动测试 | 缺少 project.assets.json，需 locked restore | blocked-env |
| Locked restore | 两个 test csproj `restore --locked-mode` | 不改 lock，生成 assets | restore succeeded | passed |
| Platform baseline | local SDK `dotnet test ... --no-restore` | 全部通过 | 83 passed, 0 failed, 0 skipped | passed |
| App baseline | `scripts/test_app_ci.ps1` | fail-closed 全部通过 | 75/75 passed, Completed | passed |
| WorkbenchApplication initial red | filtered App test | 仅因 Workbench 实现不存在而失败 | 同时发现测试缺少显式 Xunit using | invalid-red |
| WorkbenchApplication red/green | filtered App test | 导航发布 revisioned shell state | red: namespace missing；green: 1 passed | passed |
| Workbench codec red | filtered App test | typed navigate 尚未实现 | codec symbol missing | expected-red |
| Workbench codec green attempt | filtered App test | typed navigate 通过 | `IReadOnlySet` collection expression 编译失败 | failed-known |
| Workbench codec green | filtered App test | typed navigate 解析通过 | 1 passed | passed |
| React UI package | `npm test`, `typecheck`, `build` | 路由/能力展示与生产构建通过 | 14 legacy + 3 Vitest；typecheck/build passed | passed-agent |
| Web 聚合门禁首轮 | `format:check/lint/typecheck/test/build` | 全部通过 | typecheck、17 tests、build 通过；format 有 12 个警告，lint 有 2 个错误 | failed-known |
| 测试门禁子包 | 定向 pytest + Ruff | 新质量序列、离线 dist、动态 App TRX 契约通过 | 27 pytest passed；Ruff 通过；PowerShell 语法通过 | passed-agent |
| release dist 契约 red | 定向 `test_next_ci_adapter.py` | 旧源码复制被测试拒绝 | 1 failed, 15 passed，失败点为 csproj 未包含 dist | expected-red |
| release dist 契约 green | 定向 runtime pytest + Ruff | 只打包 verified dist，build 顺序正确 | 28 passed；Ruff 通过 | passed |

## Error Log

| Timestamp | Error | Attempt | Resolution |
|-----------|-------|---------|------------|
| 2026-08-09 | `uv run` 为 catchup 生成未跟踪根级 `uv.lock` | 1 | 立即删除，后续避免该调用方式 |
| 2026-08-09 | `dotnet test` 报告没有已安装 SDK，要求 10.0.302 | 1 | 停止重复测试，改为定位工作区运行时 |
| 2026-08-09 | 第一次更新 findings 的 patch 上下文与实际文本不匹配 | 1 | 读取准确内容后使用更窄上下文成功更新，未重复原 patch |
| 2026-08-09 | npm 将 `@types\\node` 当作工作区本地路径，报 package.json ENOENT | 1 | 使用 `'@types/node'` 等带引号 scoped package 名重新执行 |
| 2026-08-09 | `tsc -b` 报旧 TS 循环 alias 与 Vite `test` 配置无类型 | 1 | 排除待迁移的伪 TS，改用 `vitest/config`，保留 strict |
| 2026-08-09 | .NET 测试缺少 `obj/project.assets.json` | 1 | 计划执行 locked restore，不修改 lock |
| 2026-08-09 | 新 C# 测试遗漏 `using Xunit` | 1 | 补齐后重新运行 red |
| 2026-08-09 | CS9174：collection expression 不能构造 `IReadOnlySet` | 1 | 提取静态 HashSet 后重跑；XAML error 判定为级联 |
| 2026-08-09 | 子代理运行 Python 门禁后根级 `uv.lock` 再次出现 | 1 | 已通知子代理停止该调用；完成后统一清理 |
| 2026-08-09 | 更新视觉复核 findings 时 patch 文本多写一个“约”导致上下文不匹配 | 1 | 用 rg 定位准确行后缩小 patch |
| 2026-08-09 | tsbuildinfo/ESLint patch 两次缺少完整 hunk 上下文 | 1 | 改为完整 context 并分离 sections 后成功 |
| 2026-08-09 | Web 聚合门禁发现 `set-state-in-effect` 与两个未使用 imports，Prettier 扫入旧伪 TS | 1 | 将主题状态改回单一宿主 state，删除未使用 imports，调整格式化边界后重跑 |
| 2026-08-09 | Windows 沙箱 PowerShell CreateProcessAsUserW 失败（错误 1920） | 1 | 只读诊断获批使用沙箱外执行；源码仍通过 apply_patch 修改 |

## 5-Question Reboot Check

| Question | Answer |
|----------|--------|
| Where am I? | Phase 2：Web 基础设施与契约 tracer bullet |
| Where am I going? | Web 基础设施 → C# 深宿主 → 业务迁移 → 切换 → 完整验收 |
| What's the goal? | 完整实现统一 Web 工作台与可靠原生深宿主 |
| What have I learned? | 见 `findings.md` |
| What have I done? | 建立目标、最新分支、计划文件和已确认测试 seam |

## Session: 2026-08-10

### Phase 3–7：深宿主、Web 工作台、发布闭包与验收

- **Status:** in_progress
- Actions taken:
  - 完成 `WorkbenchApplication`、封闭命令契约、全局 revision/session 状态流和结构化错误。
  - 完成 `WebWorkbenchHost`、严格 bridge codec、opaque lease 资源 broker、一次自动恢复与原生恢复页。
  - 将可见主窗口切换为单 WebView2；原生文件选择、剪贴板、截图、拖放、保存、启动项、快捷键、更新和诊断继续由 C# 深宿主处理。
  - 完成 React 19 + TypeScript + Vite + Fluent UI v9 七路由工作台、主题、能力门控、响应式壳和 WebView2 production transport。
  - 将 Web format/lint/typecheck/test/build/离线闭包纳入质量入口；发布只复制 `dist`，拒绝 TS/TSX/map/node_modules 与宽松 CSP。
  - 新增 packaged WebView2 bridge-ready smoke，并在本地 Release build 上真实通过。
  - 浏览器验证七路由、900x600/1180x760 无横向溢出和紧凑导航可访问名称；记录截图/键盘自动化的工具限制。
  - 最终双轴复审后补齐 Canvas 全工具、Batch/PDF 分页、QR busy/cancel、About 元数据、每次 bootstrap 新 session，并删除旧 XAML；所有功能缺口已有行为测试。

## Latest Test Results

| Test | Actual | Status |
|------|--------|--------|
| Web format/lint/typecheck/test/build | 14 legacy + 19 Vitest；构建分组无 >500 KiB chunk | passed |
| Workbench .NET targeted | 27 passed | passed |
| Resource broker targeted | 11 passed | passed |
| Runtime targeted | 29 passed | passed |
| WinUI Release build | 0 warning, 0 error | passed |
| Packaged WebView2 smoke | bridge-ready health signal reached | passed |
| 全量 quality | 64 Python；14 legacy + 19 Vitest；format/lint/typecheck/build 全通过 | passed |
| App / Platform | 100 App + 83 Platform，0 failed/0 skipped；App build 0 warning/0 error | passed |
| 完整 release candidate build | 正式组件绑定、ZIP、sidecar、SBOM、artifact verify 全通过 | passed |
| ZIP release smoke | 安全解压后 packaged WebView2 到达 bridge-ready | passed |

### Review remediation

- Standards hard finding：发布测试由源码字符串断言改为实际脚本行为 seam；定向 25 passed。
- Standards judgement findings：删除旧 PreviewHost/WebMessageRouter；工作台包重命名；资源响应复制到内存并确定释放源流；旧 XAML 页面依赖解除。
- Spec P0：长任务立即发布 busy，后台完成通过 state source 推送，四域 generation 丢弃陈旧完成。
- Spec P1/P2：补 bootstrap 七域初态、hotkey 同步、Batch/PDF/QR/Canvas 功能闭环并删除旧 XAML。
- Release smoke 在真实候选上暴露并修复 dev profile 污染和 WebView2 user-data 句柄残留，未降低产物验证标准。
- 最终 spec 复审指出 session 重用、Canvas 工具缺口、QR 无 busy/cancel、有界列表不可翻页和 About 元数据缺失；已逐项补齐并新增回归测试。
- 最终 standards 复审指出构建目录忽略、C# 缩进、后台释放竞态、主题状态覆盖和 smoke 进程竞态；均已修正，处理器新增直接异步取消/释放测试。
- 修后复审：Spec blocker/actionable 0；Standards blocker/actionable 0。最终权威 App CI TRX 为 100/100，QR stale-success 修复后的正式候选已重建并通过 artifact verify 与两次 packaged bridge-ready smoke。

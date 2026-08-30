# VibeOCR Next Web 工作台进度

## Session: 2026-08-30

### Phase 16：Bootstrapper WebView2 修复与截图标注验收

- **Status:** complete-with-documented-local-admin-limitation
- **Started:** 2026-08-30 20:03（用户报告时间）
- Actions taken:
  - 创建持续目标并读取 diagnosing-bugs、frontend-design、planning-with-files、route-subagents、git-pr-workflow 技能。
  - 运行会话恢复；未发现未同步上下文。
  - 核对原工作树 Git 状态、远端、分支和 worktree，保留全部用户未跟踪文件。
  - fetch 最新 `origin/main`，创建 `codex/fix-bootstrapper-annotation` 独立 worktree。
  - 建立 Windows PowerShell 反射反馈环，连续两次复现最终产品入口引用 WebView2 Core 1.0.4129.50 但 DLL 缺失的精确症状。
  - 检查 Bootstrapper 调用点、项目引用、release build 与 product layout staging，形成 5 个排名可证伪假设。
  - 移除 Bootstrapper 对 WebView2 托管程序集的编译期引用，改为通过产品 `app/WebView2Loader.dll` 导出检测 Evergreen Runtime；公开入口新增无启动副作用的 `--self-test-prerequisites`。
  - 修正 Windows App Runtime 2.2 的实际 `Microsoft.WindowsAppRuntime.CBS.2` 包身份兼容检测，并在 Platform/Bootstrapper 两处保持一致。
  - release smoke 现在先执行真实 ZIP 解包后的公开 `VibeOCR.exe` 先决条件自检，再执行内部 WinUI WebView2 bridge-ready smoke。
  - 对标 Classic 后，将马赛克、模糊、裁剪、旋转接入真实 Canvas 导出；补充标注图复制/保存、撤销重做/Delete/Escape 快捷键、失败/取消/宿主错误提示和明确的“不重新识别”边界说明。
  - 完成 1280×800 视觉复核，消除重复截图入口和工具栏截断；三位子代理分别审计打包根因、Classic 行为矩阵和前端接线。
  - 独立复核发现浏览器下载被宿主取消、Clipboard 权限被拒绝；将标注 PNG 改为同源受限 POST→opaque URI→闭集 HostBridge 命令→原生 Clipboard/FileSavePicker，只有实际成功后才提示成功，取消与失败可见。
  - 标注导出改为原图分辨率离屏渲染，不包含选中/裁剪辅助虚线；旋转会映射已有标注。Playwright 证明 80×40 原图导出仍为 80×40、选中与未选中 PNG 字节一致、旋转后为 40×80，并验证双击只产生一次上传。
  - 标注上传使用独立 session store：64 MiB 单文件、8 条/128 MiB 会话配额、5 分钟过期回收、one-shot 消费；解析 PNG chunk、IHDR/IEND、单边 32768 与总像素 1 亿边界，失败路径清理，不改变只读 ResourceBroker/下载权限策略。并发 staging 字节也在每次写盘前原子计入 128 MiB 配额，取消、失败和窗口释放均归还预留并清理临时文件。
  - 完整 quality 通过：Ruff、100 个 runtime pytest、Prettier、ESLint、TypeScript、14 个 legacy Web 测试、27 个 Vitest、5 个 Playwright 用例及 Vite build。
  - Platform 139/139、App 192/192 通过；重新解析并验证最新正式组件后，完整 `build-release.ps1` 和最终 `release_smoke.py` 均通过。
  - 最终 staging 公开入口 `VibeOCR.exe --self-test-prerequisites` 返回 0，反射引用表中的托管 WebView2 引用数为 0。
  - 本机执行聚合 `automation.py ci --phase full` 时，bootstrap 的 `Get-AppxPackage -AllUsers` 因当前会话无管理员权限被系统拒绝；没有弱化脚本，改由同一权威子入口逐项完成验证。
- Files created/modified:
  - `task_plan.md`（追加 Phase 16）
  - `findings.md`（追加本次需求与工作区事实）
  - `progress.md`（追加本次会话日志）

- **Completed:** 2026-08-30
- **Result:** 修复真实发布入口启动崩溃；标注像素输出、原生复制/保存、提示、错误投影、资源配额和视觉基线完成，真实候选构建/解包冒烟通过。


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
| 最新 Release Bootstrapper 依赖闭包 | Windows PowerShell 读取 `Documents\VibeOCR.Next\current\VibeOCR.exe` 引用并核对同目录 DLL | 缺依赖时非零退出并包含精确程序集身份 | 连续两次检测到 `Microsoft.Web.WebView2.Core 1.0.4129.50` 引用且 DLL 缺失 | expected-red |
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

### Phase 8：发布后 main CI 故障诊断

- **Status:** in_progress
- 失败事实：main SHA `52c4574` 的 CI run `31349358391` 在 Platform lifecycle 单测清理临时目录时失败；CD workflow_run 随后正确 skipped。
- 复现反馈环：显式使用 x64 .NET 10.0.302，精确单测连续 50 轮均通过，确认本地基础复现率低；GitHub runner 日志提供原始 red 证据。
- 根因证据：生产 `Terminate()` 杀整树后只等待父进程；.NET 官方契约明确父进程退出不代表后代已退出，测试后代 `ping.exe` 持有工作目录。
- 待执行：以 Job Object 为进程树真源，增加有界终止等待和确定性回归，再走 PR、main CI、CD 全链复验。
- 已实现 `WindowsJobObject.TerminateAndWait`：调用 `TerminateJobObject`，查询 `ActiveProcesses`，最多等待 5 秒；`InferenceSupervisorProcess` 仅在 Job 路径失败时回退 `Process.Kill(entireProcessTree: true)`。
- 回归测试 red：`WindowsJobObject` 不含 `TerminateAndWait`，CS1061；green：两个已分配长生命周期进程均在方法返回前退出，1/1 passed。
- 相关生命周期测试 22/22；原始失败用例修复后连续 50/50；完整 Platform 84/84。
- quality 通过：64 Python、14 legacy Web、19 Vitest、Prettier/ESLint/typecheck/build；App 100/100。
- 发布状态机要求候选 main HEAD 本身变更 plan；单独修复会 `plan-unchanged`，直接再跑 `minor` 则会错误生成 0.4.0。恢复方案为回退未发布 #19 后，通过 CD 正式入口重新生成 0.3.0。
- 已创建并推送 PR #20；云端 Platform 84/84 证明 supervisor 整树等待修复有效，CodeQL 与其余质量门禁通过。
- PR #20 的 `required` 新失败位于 packaged WebView2 smoke。探针确认进程停在 `AppLog`/WebView2 之前，窗口类 `#32770` 对应跨产品互斥提示框；当前正在以随机 self-test instance scope 隔离 named Mutex/pipe，并补脚本与 C# 回归契约。
- self-test instance scope 已完成 red→green：C# 5/5 覆盖生产默认、隔离名称和错误输入；脚本行为测试覆盖随机 32 位 scope 与调用方环境恢复。在 Classic 仍占生产锁时，真实 packaged smoke、完整 release build、ZIP release smoke 全部通过。
- PR #20 云端 required 与四语言 CodeQL 全绿后已 squash merge；main SHA `35a0668` 的 CI run `31355464161` 再次暴露原生命周期用例，首个错误仍是 `Directory.Delete` 被 `ping.exe` 占用。
- 新证据表明 `cmd.exe` 可在父进程入 Job 前创建后代，Job enrollment 不会追溯既有后代。已新增确定性“分配前既有 child”契约：旧实现 CS1061 red；Toolhelp 后代闭包 enrollment 后 1/1 green，原 GitHub 失败用例 50/50，Platform 全量 85/85。

| Test | Actual | Status |
|------|--------|--------|
| main CI run `31349358391` | Platform 82/83；目录被后代进程占用 | failed-confirmed |
| 精确 lifecycle 单测 x50 | 50/50 passed，本机未复现低概率竞态 | passed-low-repro |
| Job Object 确定性契约 | red CS1061 → green 1/1，两个活动进程均退出 | passed |
| supervisor/Job Object 相关测试 | 22/22 passed | passed |
| Platform 全量 | 84/84 passed，0 skip | passed |
| quality | 64 Python + 14 legacy Web + 19 Vitest，格式/lint/type/build 全通过 | passed |
| App CI | 100/100，Completed TRX | passed |
| PR #20 required（上一轮） | quality/App/Platform 通过；packaged WebView2 smoke 超时 | failed-confirmed |
| self-test instance scope | C# 5/5；PowerShell 行为 1/1 | passed |
| quality / App / Platform | 64 Python + 33 Web；105 App；84 Platform | passed |
| release build / release smoke | ZIP、sidecar、SBOM、artifact verify；两次 packaged bridge-ready | passed |
| main CI `31355464161` | PR 全绿后仍有启动 enrollment 竞态；83/84 Platform | failed-confirmed |
| 既有后代 enrollment 契约 | red CS1061 → green 1/1；parent/child 均退出 | passed |
| 原 lifecycle 用例复跑 | 50/50 | passed |
| Platform 全量（第二层修复） | 85/85 | passed |

| Timestamp | Error | Attempt | Resolution |
|-----------|-------|---------|------------|
| 2026-08-10 | 默认 `dotnet` 命中 x86 host，未发现 SDK 10.0.302 | 1 | 显式使用 `C:\Program Files\dotnet\dotnet.exe` 后反馈环正常运行 |
| 2026-08-10 | 64 后代压力测试在旧实现上仍通过，无法形成可靠红灯 | 1 | 撤销脆弱测试，改为 Job Object 多活动进程的确定性接口契约 |
| 2026-08-10 | release build 在 build/publish 成功后，构建前 WebView2 smoke 30 秒超时 | 1 | 作为独立本地启动信号调查；下一探针延长观测并区分进程退出/存活/health，不原样重试 |
| 2026-08-10 | 已 publish 产品的 60 秒 smoke 仍超时 | 2 | 用此前验证通过的 0.2.0 候选作差分探针 |
| 2026-08-10 | 0.2.0 旧候选在当前机器同样超时 | 3 | 后续云端同样超时，撤销“仅本机环境”判断并继续根因分析 |
| 2026-08-10 | PR #20 packaged smoke 云端超时 | 4 | 窗口类探针确认互斥提示框；改为隔离 self-test named object，而非延长 timeout |

## Session: 2026-08-10（产品化改造）

### Phase 9：产品化方案、基线与独立审查

- **Status:** in_progress
- Actions taken:
  - 通过七轮需求拷问锁定发布目录、数据边界、Classic 对齐、VibeTable 设计语言、Lucide、品牌、任务生命周期、测试与单 PR 交付边界。
  - 建立持续目标，从最新 `origin/main@6f172f6` 创建 `codex/vibeocr-productization` 独立 worktree。
  - 核实根 AGENTS.md、无启用 hooks、GitHub 认证、无开放 PR；v0.3.0 正式 Release、main CI/CodeQL/CD 均成功，故收口旧 Phase 8。
  - 读取并应用 planning-with-files、codebase-design、frontend-design 与 git-pr-delivery；启动 ProductLayout 三方案独立设计评审。
  - 盘点 build/package/verifier/Bootstrapper/updater/PortableLayout、Web 主题/壳/测试与 CI 入口，更新 task_plan.md/findings.md。
  - 完成 ProductLayout 三方案比较与计划 Standards/Spec 双轴审查；修复 Issue 前置、identity 闭包、metadata seam、schema 命名和对外 ZIP 命名缺口。
  - 创建 GitHub Issue #23，固定目标、非目标、关键风险和验收，并关联 `docs/productization-plan.md`。
  - 完成实施前基线：quality 全绿（64 Python + 33 Web + format/lint/type/build），App 105/105；Platform 84/85，唯一失败为修改前已有 Job Object 退出竞态。
- Files created/modified:
  - `task_plan.md`（追加 Phase 9–14、决策、验收与错误）
  - `findings.md`（追加产品化需求、布局、UI 与测试事实）
  - `progress.md`（本节）

| Timestamp | Error | Attempt | Resolution |
|-----------|-------|---------|------------|
| 2026-08-10 | 通过 `uv run` 执行 planning catchup 生成未跟踪根 `uv.lock` | 1 | 依据创建时间确认是本次副作用并精确删除；后续只用仓库既有 `uv run --no-sync`/封装入口 |
| 2026-08-10 | 并行可选 Git 探测因无 hooks 配置/无关键词命中返回 exit 1 | 1 | 改为可选探测显式 `exit 0` 并分别读取必要结果，不重复原命令 |
| 2026-08-10 | 批量读取包含不存在的 Bootstrapper `App.config` | 1 | 记录为待核实事实；转查 csproj/生成配置与发布产物，不假设源码中存在 App.config |
| 2026-08-10 | Platform 基线 `TerminateAndWaitReturnsAfterEveryAssignedProcessExits` 临时目录仍被后代占用 | 1 | 记录为修改前 existing baseline failure；不原样重跑粉饰，后续若触及相关 lifecycle 再以定向契约诊断 |

| Test | Actual | Status |
|------|--------|--------|
| npm locked install | 327 packages，0 vulnerability | passed |
| quality | 64 Python + 14 legacy Web + 19 Vitest；format/lint/type/build | passed |
| App Release tests | 105/105 | passed |
| Platform Release tests | 84/85；Job Object 退出竞态 1 failed | failed-confirmed-baseline |

### Phase 10–13：产品布局、UI 与验证实现

- `ProductLayout schema_version=1` 已成为 build、Bootstrapper、WinUI、packager、verifier 和 updater 的共同路径契约；公开根严格收敛为五项。
- production 用户数据已移出安装目录，固定为 `%LOCALAPPDATA%\VibeOCR`；更新器只替换五个部署项并在健康超时后恢复旧部署。
- 发布资产收敛为 `VibeOCR-v*-win64.zip`；metadata 保留 component lock、component identities 和完整 release manifest。
- Web 采用 VibeTable 风格蓝色/冷中性语义 token、1024×720 响应式壳和 Lucide 功能图标；原创 SVG 派生 ICO 与 7 档 PNG。
- 单图和批量结果新增 Markdown/Word/Excel 导出；Playwright 增加三个固定主题/尺寸/状态截图。
- 新增 Classic 行为矩阵后确认 PDF 文字层/插页重排、QR 样式/Logo、Runtime mutation 受当前 Backend/Protocol capability 阻断；Draft PR 不把这些占位能力声明为完成。
- 既有 Job Object 全量波动以诊断反馈环收口：修复前 Platform 全套 9/10 失败；测试清理显式等待并关闭进程句柄后 0/10 失败。
- 自审关闭跨卷更新缺口：LocalAppData 候选验证后复制到安装盘同级 stage，stage/rollback 与安装根只进行同卷 rename；新增回归证明用户数据不进入任何原子 move。

| Test | Actual | Status |
|------|--------|--------|
| ProductLayout/package/updater/CI adapters | 32/32 | passed |
| App Release tests | 108/108 | passed |
| Platform Release tests stress | 87/87 × 10，0 failure | passed |
| Bootstrapper Release build | 0 warning / 0 error | passed |
| Web format/lint/type/tests/build | 14 legacy + 19 Vitest；全部通过 | passed |
| Playwright visual | 3/3，1280×800 light recognition/PDF + 1024×720 dark batch | passed |

### Phase 13 最终候选验收

- 最终审查发现并关闭三项发布风险：updater 在触碰安装根前验证 release manifest、完整文件 closure 与 component binding；品牌生成移除未锁定 Pillow 并进入 quality/release 门禁；托盘改用随包发布的原创 ICO。
- Python 与 C# 对 `schema_version=1` 的 canonical path 保持一致；Python 明确区分打包前 `stage`、完整树 `inspect` 与发布 closure `verify`，避免用暂存语义放宽已安装产品。
- 真实候选 ZIP 根精确为 `VibeOCR.exe`、`LICENSE`、`CHANGELOG.md`、`app/`、`runtime/`，PDB 与 `.exe.config` 均为 0。
- 高 DPI 与完整键盘 GUI 路径未在本机自动化；PR 以 Playwright 三张固定 Windows 基线、真实 WebView2 ready、forced-colors/reduced-motion 代码契约和明确人工复核项交付，不伪造通过。

| Test | Actual | Status |
|------|--------|--------|
| Python quality | 74/74；Ruff format/check | passed |
| Web quality | 14 legacy + 19 Vitest + 3 Playwright；Prettier/ESLint/typecheck/build | passed |
| App CI TRX | 108/108，Completed | passed |
| Platform Release | 87/87 | passed |
| Brand assets | 7 个 PNG + ICO 的应用与发布引用 | passed |
| Release build | WinUI/Bootstrapper/PyInstaller updater/layout/closure/ZIP/checksum/SBOM | passed |
| Release smoke | artifact verifier + 解压候选 WebView2 bridge-ready | passed |

### Phase 14：Draft PR 交付

- 三个中文 Conventional Commit 已推送到 `codex/vibeocr-productization`，未绕过 hooks，分支基于最新 `origin/main`（ahead 3 / behind 0）。
- Draft PR #25 已创建：`https://github.com/FelixJI/vibeocr-next/pull/25`；正文包含 Issue #23、根因、布局/更新/UI 变更、Classic 阻断矩阵、精确验证命令与三张视觉基线。
- GitHub CI `plan` 与四语言 CodeQL 已触发并处于 `IN_PROGRESS`；pending 未写成 passed，PR 未合并。

## Session: 2026-08-11（PR #26 CI 修复）

### Phase 15：失败检查定位

- **Status:** in_progress
- Actions taken:
  - 读取并应用 `github:gh-fix-ci`、`diagnosing-bugs`、`git-pr-delivery` 与 `planning-with-files`。
  - 核对 Next 仓库 Git、远端、hooks、worktree 与 GitHub CLI；clone 未启用实际 hooks。
  - 同步 `origin`，确认 PR #26 的 `required` 失败、CodeQL 通过；PR #24 全绿。
  - 将对应 worktree fast-forward 到 PR head `97c32f2`，未覆盖任何未提交改动。

| Test | Actual | Status |
|------|--------|--------|
| `gh pr checks 24` | `required` 与 CodeQL 全绿 | passed |
| `gh pr checks 26` | `required` failed；CodeQL passed | failed-confirmed |

| Timestamp | Error | Attempt | Resolution |
|-----------|-------|---------|------------|
| 2026-08-11 | Windows 沙箱启动 PowerShell 返回错误 1920 | 1 | 经批准对只读 Git/文件检查使用沙箱外 PowerShell；源码写入仍使用 apply_patch |
| 2026-08-11 | 官方 `inspect_pr_checks.py` 在 Windows 按 GBK 解码 UTF-8 gh 日志而崩溃 | 1 | 以 `PYTHONUTF8=1`、`PYTHONIOENCODING=utf-8` 重跑后成功提取失败日志 |
| 2026-08-11 | 首个删除链补丁对 `build-release.ps1` 使用了错误上下文 | 1 | 确认补丁未部分应用，读取实际片段后拆分为精确小补丁 |
| 2026-08-11 | Ruff format check 要求重排两个受影响 Python 文件 | 1 | Ruff lint 已通过；按仓库 formatter 格式化后重跑 lint/format 与 pytest |
| 2026-08-11 | adapter 全文件测试有 3 个 release command 序列仍期待已删除的品牌 `uv` 调用 | 1 | 保留 Web fail-closed 语义，仅从三个期望序列删除首个品牌命令后重跑 |
| 2026-08-11 | 完整 quality 在 82 个 Python 测试通过后找不到 Prettier | 1 | 对应 worktree 尚无锁定 `node_modules`；先执行 `npm ci` bootstrap，再重跑完整 quality |
| 2026-08-11 | 精确 Platform 测试无法启动：缺少 `project.assets.json` | 1 | 先对 Platform tests 执行 locked restore，再运行同一测试反馈环 |

- 失败日志已闭合到 brand-assets：24px Edge headless screenshot 在 30 秒内未产生完整 PNG。
- GitHub connector 证实 PR 实际 diff 仅含更新下载代理与对应测试；brand generator 失败来自新 base，不是该 diff 引入。
- 建立快速红灯：`uv run --no-project python -c "...patch _generate...; module.main()"` 在 1 秒内以 `AssertionError: quality check launched Edge` 失败；该 seam 精确覆盖 quality `--check` 对外部 Edge 的依赖。
- 本地真实 brand check 10.38 秒通过；相邻 main run 分别在 brand 24px 与 Playwright batch screenshot 超时，根因收敛到 CI 中实时浏览器渲染的不确定性，而非 PR 更新代理代码或 24px 资产内容。
- 用户选择最小方案：PNG/ICO 直接随仓维护，不保留 SVG 生成链、manifest 或额外哈希校验。
- quality 定向契约 red→green：删除前 1 failed，删除脚本与调用后 1 passed。
- CI adapter 全文件测试在同步 release command 序列后 31/31 通过；Ruff lint 与 format check 通过。
- 完整 quality 的 Ruff 与 82/82 Python 测试已通过；Web 阶段因本地未安装锁定依赖暂未启动。
- `npm ci` 按锁安装 331 packages、0 vulnerability；完整 quality 随后通过：82 Python、14 legacy Web、19 Vitest、3 Playwright 及 format/lint/typecheck/build。
- 更新代理定向 .NET 测试 5/6，通过的唯一失败为 `DownloadVerifyFallsBackAcrossBadSources` 返回 `false`；该测试在 PR 合入产品化 main 后首次运行到实际 selector 路径。
- 根因确认：测试 fixture 仍发布旧 `VibeOCR-Next-v*`，而产品化 selector 只接受 `VibeOCR-v*`；同步 JSON 与 checksum 文件名，不修改生产 selector。
- 更新代理 fixture 修复后定向 App tests 6/6 通过。
- 推送后 CodeQL 全绿、quality 云端通过；required 在 Platform 86/87 失败：`SuccessfulStartIsOneShotAndDisposeClearsReady` 的 Job enrollment 读取后代 `Process.SafeHandle` 时抛 `Win32Exception: Access is denied`。
- locked restore 后本地精确测试稳定重现目录占用；改用 Job API 所需最小权限显式打开快照后代句柄后，同一测试转为 1/1 passed。
- 完整 Platform suite 连续运行 10 轮并在格式整理后再运行 1 轮，11 轮全部 87/87 passed，未再出现句柄拒绝或 Dispose 后目录占用。

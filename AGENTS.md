# AGENTS.md

本文件适用于仓库根目录及其全部子目录；更深层的 `AGENTS.md` 只能补充更严格、范围更窄的规则。

<!-- BEGIN UNIFIED SIX-REPOSITORY PRACTICES -->
## 统一工程与交付规则

### 语言、事实来源与协作

- 与用户、Issue、PR、review 和交付说明使用简体中文；代码标识符、协议字段、CLI 参数和行业缩写保持原文。代码注释遵循所在模块既有语言，不为翻译而改名。
- 事实优先级依次为：可执行配置/锁文件与代码、`.ci/project.json`、项目脚本、测试、当前文档。文档与实现冲突时先核实实现并在同一 PR 修正文档，不凭记忆扩写。
- 大改先说明影响的模块、接口、风险与验证；优先把复杂实现藏在小而稳定的接口后。`scripts/automation.py` 是自动化稳定接口，项目差异通过声明式配置、项目适配器和必要的 workflow 编排表达。
- 评审 rubric、验收清单和风险分级只保留能区分结果、支撑决策的条目；不要机械枚举所有组合，也不要把普通工程工作包装成安全攻防论文。

### 修改范围与安全

- 开始工作前读取 `git status -sb`、远端、当前分支、最近的仓库指令和实际 hooks。保留用户未完成工作；禁止擅自 stash、reset、checkout 覆盖、递归删除或绕过 hook。
- 在最新远端 `main` 的独立 `codex/<slug>` 分支/worktree 中工作。只暂存本任务文件，不提交密钥、凭据、本地路径、缓存、数据库、模型、构建包或编辑器状态。
- 生成文件、版本派生文件和 lock 必须由仓库脚本更新；不得手改生成物后跳过生成/一致性检查。会删除或重建目录的脚本只可作用于仓库声明的固定输出目录。
- 不通过降低覆盖率、跳过与变更相关的 E2E、吞掉错误、添加无依据重试、删除有明确边界契约的校验或禁用安全检查来使 CI 变绿。修复针对根因；存在稳定且合适的测试 seam 时补充能在旧实现上失败的回归契约，不为勾选条目制造脆弱测试。
- Python 环境统一由 `uv` 管理：使用仓库锁定配置通过 `uv sync --frozen ...`（或项目明确声明的 `uv venv`）创建/更新仓库内 `.venv`，所有 Python 入口通过 `uv run python ...` 或仓库封装脚本调用。禁止直接用系统 `python`/`pip` 安装项目依赖，禁止把依赖散装到全局或用户 `site-packages`。
- 依赖解析和工具链版本服从项目声明的配置、lock 与生成脚本。可复用本机可信下载缓存及已配置镜像，但不得为适配本机网络擅自修改仓库级 registry/index、lock 中的来源、Git remote 或 CI 配置。
- 下载或远端访问失败时先做最小诊断，不盲目重试，也不自行使用未经授权的第三方代理。确需改变仓库依赖来源时，作为独立变更说明其对 CI、lock 与供应链一致性的影响。
- 普通依赖及 lock 始终以仓库声明为准，不因本机已有更高版本而升级。SDK 或工具链缺少仓库固定版本、但本机已有更高稳定版本时，先检查项目的 roll-forward、lock、CI 与兼容性契约：允许兼容前滚的可直接复用；要求精确版本的，不静默修改 pin，而是向用户说明安装固定版本与独立升级 SDK 两种方案。升级须使用项目声明方式走普通 PR，并运行完整质量入口。
- SHA-256、hash 或 identity 比对不是普通正确性或安全校验的默认手段，只在发布资产、外部下载、更新包、跨系统资产交接或故障取证等存在明确字节完整性契约的边界使用。新增前必须能指出权威摘要的生产者、校验消费者、失败处理及其防止的可复现故障；缺少任一项则不新增。
- 不存在上述契约时，不对本地源码、生成文件、配置、目录、临时文件、缓存、日志、数据库或同一流水线内已受 Git/构建步骤约束的数据，为“多一层保险”重复计算 SHA-256，也不把手工 hash 当作默认 review 或交付证据。优先使用语义校验、解析/契约测试、Git tree/diff、精确资产清单或既有单一 checksum；已有多层 hash/identity 若没有独立消费者或边界，应简化而非继续叠加。

### CI/CD 架构保护

- 活跃仓（本仓、`file-toolbox`、`vibetable`）默认只保留 `.github/workflows/ci.yml` 与 `.github/workflows/cd.yml`；公共自动化深模块文件清单为 `scripts/automation.py`、`scripts/automation_core.py` 及 `scripts/automation_{common,ci,candidate,prepare,publish}.py`，相关变更必须在活跃仓之间协调并保持每个对应文件提交后的 Git blob/字节一致；已归档仓中的同名文件是冻结历史副本，不需要也不再要求同步维护。workflow 共享稳定 CLI、`required` 门禁、候选交接和发布状态机等不变量，但不要求字节一致；VibeTable 可按其多栈构建和 E2E 瓶颈调整 job、lane、缓存及产物交接。
- 项目专属命令、测试集合和构建语义优先写在 `.ci/project.json` 及项目脚本中。workflow 可表达项目所需的 runner、job 拓扑、缓存和产物交接，但不重复实现项目命令；需要新依赖或平台步骤时优先扩展 bootstrap/adapter。
- CI 在 PR 和 `main` push 上完成 `.ci/project.json` 声明的 `bootstrap`、`quality`、`e2e`、`release_build` 与 `release_smoke`，按项目真实依赖串并行编排并 fail closed。PR 必须执行适用的完整 release build/smoke；只有 `main` push 会整理并上传正式候选。只有同一 PR 的陈旧运行可取消，`main` 运行不可互相取消。
- PR CI 是合并门禁；squash merge 后的 `main` CI 验证合并结果，并额外上传固定名 `release-candidate`。CD 的 publish job 只下载触发它的那次 `main` CI、同一 source SHA 的候选，不重新运行完整 CI，也禁止在 CD 重建、替换或人工上传资产。
- 手动运行 CD 只允许选择 `patch`/`minor`/`major`，作用是创建或刷新唯一 `automation/release` changelog/version PR。该 PR 合并后依次运行 `main` CI、provenance/SBOM attestation、正式非草稿 Release 和镜像同步；不再设置人工发布确认。

### 版本、changelog 与 Release 不变量

- 版本更新只能走 `uv run python scripts/automation.py release prepare --bump <part>` 及 `.ci/project.json` 声明的生成命令；不得直接编辑多个版本源、手打正式 tag 或手建 Release。
- 目标版本基线取当前版本、稳定 `v*` tag 与已发布正式 Release 的最大值；draft/prerelease 不参与。只有 tag、没有正式 Release 的稳定版本也会推进下一目标，不能复用或回退。
- `refs/tags/v*` 不可更新/删除且无 bypass；main 禁止 force-push/删除。发布候选必须绑定 source SHA、版本、项目 identity、精确资产集合、SHA-256 与 SPDX 2.3 SBOM。已有正式 Release 只允许在 tag/source/identity 一致时补齐或修复资产，否则 fail closed。
- Changelog 由 squash 后的 Conventional Commit 生成。`feat`、`fix`、`perf`、`deps`、`revert` 和 breaking change 默认可见；包括 `security`、`build` 在内的其他类型默认隐藏。不要为进入 changelog 伪造 type；确需覆盖时用 `Changelog: include` 或 `Changelog: skip`。
- 正式 Release 完成后，以 CD 成功状态、source SHA、Release 资产清单、checksums 和 attestations 作为交付证据；流水线已完成逐资产校验时，不在本地重复下载全部资产。仅在用户明确要求或排查具体发布故障时，按项目脚本抽检相关资产。

### 代码质量与验证

- 先运行最小相关 formatter/lint/type/test，再运行项目专属质量入口；修改生成器、构建、版本、组件绑定或发布逻辑时必须执行相应 contract/smoke。完整矩阵以 GitHub PR 的 `required` check 为权威。
- Python 使用仓库配置的 Ruff 和类型检查；TypeScript/Vue 使用锁定 Node 与项目脚本；C# 使用锁定 .NET SDK、warnings-as-errors 与 locked restore；Go 必须 `gofmt`/`go vet`/`go test`。不得用宽泛 `Any`、ignore、禁用规则或更新 snapshot 掩盖缺陷。
- 测试与源码相邻或进入仓库既有测试目录，命名、marker 和覆盖率遵循项目章节。修复跨进程、GUI、打包或协议问题时，选择与可复现故障、接口契约或高概率风险直接相关的成功、失败、取消、超时或产物路径；不机械要求每次改动覆盖全部组合。
- 本地 hook 若已安装必须正常执行且不得 `--no-verify`；若 clone 未安装 hook，运行其配置对应命令并在 PR 说明。格式化若会改变公共镜像文件，必须按镜像豁免规则处理。

### Commit、PR 与合并

- Commit 使用 `<type>(<scope>): <简体中文动词短语>`，例如 `fix(ci): 修复候选产物绑定`、`docs(agents): 补充仓库治理规则`。一个 commit 只表达一个完整意图。
- PR 标题采用中文 Conventional Commit；正文至少包含背景与根因、变更内容、影响与风险、精确验证命令及结果。UI 可见改动附截图；未执行项说明原因，pending 不得写成 passed。
- 只允许 squash merge。合并前必须通过严格同步 `main` 的 `required` check，处理所有 review conversation，不使用 admin/bypass 绕过保护。普通 PR 合并后确认 `main` CI 与 CD 哨兵成功且未意外发布；`automation/release` PR 合并后则必须确认 CD 完成正式发布。
- 工具托管的 worktree 使用工具固定位置；手工创建的 worktree 统一放在各仓库共同父目录下的 `.worktrees/<repo>/<slug>`，按仓隔离，不放进仓库工作树、`build/` 或系统临时目录。worktree 只在工作树干净且 PR 已确认 `MERGED` 后移除。由于只允许 squash merge，必须验证 PR 的 `mergeCommit` 可从最新远端 `main` 到达，并用 `git diff --quiet <branch-head> <mergeCommit>` 确认 tree 等价；不能要求分支 HEAD 本身是 `main` 祖先。先使用 `git worktree remove` 移除 worktree，再执行 `git worktree prune` 和安全删除分支；验证失败时保留现场，不递归删除目录或 `.git`。

### Secret 与远端治理

- `RELEASE_TOKEN` 仅用于 release PR prepare；publish 使用 GitHub OIDC/最小权限。镜像凭据只从既有 Secret 注入。不得打印、复制、重命名或探测 Secret 值；Secret 名或权限变化必须在仍共享该自动化面的活跃仓（本仓、`file-toolbox`、`vibetable`）之间协调，已归档仓不再产生新的同步义务。
- `release` Environment 无 reviewer；仓库只允许 squash、自动删除已合并分支、线性历史、严格 `required`、管理员同样受保护。不得在代码变更中私自放宽 branch/ruleset/environment。

<!-- END UNIFIED SIX-REPOSITORY PRACTICES -->

## 项目架构与独特约束

- 本仓包含仅 Windows 的 .NET 10 WinUI 桌面端与 Python Runtime，不是 WPF。主应用、平台层与 net472 Bootstrapper 分别位于 `src/dotnet/VibeOCR.App`、`VibeOCR.Platform`、`VibeOCR.Bootstrapper`；WebView2 WebAssets 使用锁定 Node/TypeScript。Runtime 位于 `src/runtime/vibeocr/runtime`，内部 wire 契约位于 `contracts/runtime/python/vibeocr/runtime_contracts`，C# 契约与 HTTP client 源码位于 `src/dotnet`。
- Runtime 的 host 仅负责启动、鉴权、HTTP 路由与装配；jobs、recognition、documents、codes、environments、processes 分别拥有任务、推理、PDF/导出/表格、二维码、环境与共享进程能力。桌面、Supervisor、Paddle/MinerU/PDF worker 仍为独立进程；跨进程数据由 v2 内部契约约束。根 `pyproject.toml`/`uv.lock` 是单一 Python 开发入口；`vibeocr-next-runtime` 是唯一内部 wheel（版本由 `repository.json` 派生），作为运行环境隔离安装的实现细节随产品分发，不再有独立 Protocol SDK 或 Backend Python 发版面。
- `global.json` 固定 .NET SDK 基线并允许 `latestPatch` 前滚；`Directory.Build.props` 强制 warnings-as-errors、deterministic 与 locked restore。NuGet central versions/packages.lock 禁止手改，只通过 `scripts/update_dotnet_locks.ps1` 更新。
- `.ci/project.json` 的 bootstrap 必须包含 `uv sync --frozen`、WebAssets `npm ci`、Windows App Runtime 安装、`dotnet tool restore` 和各测试工程 locked restore，不解析旧仓组件 Release；遗漏 Windows App Runtime 会使 WinUI testhost 挂起。
- quality=`uv run --frozen python scripts/check_quality.py`，覆盖 Next 原有测试、迁入 Runtime/contract 测试、生成一致性和真实 HTTP 路由契约；E2E 运行 Platform/App 与新增内部 C# Contracts/HTTP Client 测试；随后 `scripts/build-release.ps1` 和 `uv run --frozen python scripts/release_smoke.py` 构建并验证真实候选。
- #87 单产品实施后，Desktop 以 ProjectReference 直接编译本仓 `VibeOCR.Platform`、`VibeOCR.Contracts` 与 `VibeOCR.Runtime.Client`；`Directory.Packages.props` 不再 pin `VibeOCR.Runtime.*`，NuGet 源只剩 nuget.org。构建与发布默认不解析、下载旧 `vibeocr-backend`/`vibeocr-protocol` Release；wire 只保留一个 v2 内部契约，SDK/Backend/Protocol minor 产品矩阵退役，新行为一律按调用点 capability 协商。
- 版本唯一事实源是 `repository.json`，`scripts/sync_version.py` 派生 App csproj。正式资产精确为 Velopack full nupkg、相邻版本可选 delta nupkg、`VibeOCRNext-v{version}-win-x64.zip`、`releases.win.json`、`product-identity.json` 与 SPDX SBOM，项目 smoke 必须拒绝 Setup、sidecar 和任何额外资产。`scripts/build-release.ps1` 经 `scripts/build_internal_runtime.ps1` 从当前源码产出内部 Runtime 闭包（wheel、冻结 installer、固定第三方 offline base pack、runtime manifest），第三方 CPython 归档与 profile 锁保留在 `config/runtime/`；产品内 `runtime/backend/` 与 `app/metadata/component-lock.json`、`app/metadata/component-identities.json` 只是兼容稳定路径，内容绑定单一 Next 产品，不再单独发布独立组件锁。Portable 应用通过 Velopack feed 完成应用内下载、应用与 restart，不走 Setup 或手动下载桥接。
- release publish 必须包含 WinUI `.xbf`/`.pri`、Bootstrapper、Velopack 运行时文件与产品内 metadata 绑定文件；否则可能出现 `XamlParseException` 或更新入口失效。修改 publish layout、WebAssets、runtime installer 参数或 capabilities 时执行真实打包验证。
- 语言职责边界：C#/WinUI 承担宿主、窗口、单实例与跨产品互斥、热键、剪贴板与文件授权、应用权威状态、更新和本地二维码生成（`src/dotnet/VibeOCR.App/Features` 与 `VibeOCR.Platform`）；React/TypeScript 仅承担 WebView2 workbench 的展示与局部编辑（`WebAssets`，经 `App/Web`/`App/Workbench` 的 bridge command 交互，不直连 Runtime）；Python/CPython 承担 OCR/PDF/表格导出、二维码解码及兼容生成路由、模型/引擎运行、任务调度和运行环境安装事务（`src/runtime/vibeocr/runtime` 各模块）。WebView 消息桥与 Desktop↔Runtime 内部 HTTP wire 是两个不同边界，编号相同不等于同一协议；Runtime 安装计划与设备就绪的真值由 Runtime 提供，C#/TS 不复制包依赖推导。PowerShell 保留 Windows 构建/打包适配，Python 保留通用生成/验证/自动化入口，不为语言统一翻译脚本。功能→语言→边界的完整映射见 `docs/source-reading-guide.md`。
- 语言边界优化按 Issue 推进，区分已实施与计划范围：#97 的公开文本 QR 由 C# 本地生成，不等待 Supervisor；PNG/JPEG 保存使用真实编码，解码与兼容生成路由仍在 Runtime；#98 已移除无生产消费者的旧 JS 核心与薄 TS 包装；生产 bridge 和编辑器使用 TypeScript，保持编译期类型契约、CSP 与运行时消息测试。不引入 Python.NET/IronPython、第二套 IPC 或全量原生 UI 重写，不移除 Python OCR/PDF 能力、已证明的 worker 隔离、Runtime 安装事务与内部 wire。
- Python/PowerShell/TOML 用 4 空格，C#/JSON/YAML 用 2 空格；Python Ruff/Node/.NET 版本以配置为准。不在文档中假定某个 clone 是否安装 Git hook，按工作开始时的实际检查执行，未安装时运行配置对应质量脚本。

## 仓库关系：归档历史与活跃协同

- `vibeocr-backend`、`vibeocr-protocol`、`vibeocr-classic` 均已归档为只读历史：本仓的开发、问题反馈、构建和发布不要求它们未来的任何提交、发版或镜像维护，其中的公共自动化/规则副本是冻结历史快照，不再参与字节一致协同。源仓来源 SHA、许可证与既有 Release/资产说明作为历史事实保留（见 README 来源声明与 `MIGRATION.md`），不把旧事实改写成从未发生。
- 本仓已持有 Runtime 与内部契约源码，正式构建直接消费本仓源码，不再把旧 `vibeocr-backend`/`vibeocr-protocol` Release 作为构建输入；其发版不级联触发本仓 CD，本仓 CI/CD 只消费自身源码，CD 只发布本仓同一 CI 候选。`vibeocr-classic` 的历史正式版本仍绑定旧仓组件，这只描述既有发布物，不产生新的跨仓义务；两者互不依赖、不共享发版版本。
- 归档不等于用户已卸载旧 Classic：代码中的跨产品互斥（`VibeOCR.Platform/Windows/FrontendExclusiveLock.cs`）、既有数据迁移（profile 与 legacy state migration）和更新回退保护继续有效，不得仅凭归档状态删除。
- 内部 wire 契约只保留一个 v2 major，随本仓同源演进，不再维护跨仓 SDK minor 产品矩阵。capability 缺失时必须隐藏、禁用或 fallback；进程隔离、已安装运行环境的 manifest 绑定与回滚语义保留。wire major 变更必须显式升级契约 schema、生成物、golden 与客户端实现。
- `file-toolbox`、`vibetable` 是仅存的活跃共享仓：与本仓无运行时依赖，仅共享自动化治理。公共 automation 深模块与 Secret 变更在活跃仓之间协调，保持 Git blob 一致与既有门禁，不因此放宽 squash、required、候选来源、attestation 与独立审阅。

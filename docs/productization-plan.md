# VibeOCR 产品化改造实施方案

关联 Issue：[#23](https://github.com/FelixJI/vibeocr-next/issues/23)

## 1. 目标与交付边界

本方案把当前 VibeOCR Next 开发基线一次性提升为可审查、可打包、可更新的 VibeOCR 产品基线，并通过一个 Draft PR 交付。目标同时覆盖：

1. 收拢 Windows ZIP 根目录，只公开一个产品入口。
2. 用版本化产品布局 interface 统一 build、Bootstrapper、WinUI、updater、packager、verifier 和 smoke 的路径认知。
3. 将用户可变数据与可整体替换的程序目录分离，保证新布局版本间更新失败可回滚。
4. 以 Classic 的用户可观察行为为功能契约，以 VibeTable 的设计语言为视觉与交互参考，重构 Next 的信息架构和页面状态。
5. 保留 Fluent UI React v9 控件基础设施，功能图标完全迁移到 Lucide，创建独立 VibeOCR 品牌资产。
6. 补齐 interface 契约、UI 行为、视觉回归、真实 WinUI/WebView2 和发布候选验证。

明确不做：

- 不迁移、读取或自动删除任何旧开发版数据。
- 不支持旧平铺布局应用内升级到新布局；新布局是后续更新契约的起点。
- 不增加 MSI/MSIX/安装向导。
- 不新增永久识别历史或进程重启后的任务恢复。
- 不直接修改正式版本；版本仍由 release prepare 流程管理。
- 不复制 Classic 的 Qt 布局，也不让 Next 依赖 VibeTable 源码或运行时。
- 不在用户可见产品目录、EXE、ZIP 资产名或界面文案中继续暴露 `Next`；程序集和内部技术标识可在本 PR 内保持兼容命名。

## 2. 已确认事实与根因

### 2.1 发布根目录零散不是单点问题

当前 `scripts/build-release.ps1` 将 WinUI publish、net472 Bootstrapper publish、PyInstaller updater 与文档都写入同一个 `$product`；`scripts/package_product_release.py` 随后继续在该根写入 component lock、Backend、Runtime Installer 与 product manifest。与此同时：

- `VibeOCR.Bootstrapper/Program.cs` 假定 WinUI、component lock、Backend manifest 与 installer 都在旧根路径。
- `GitHubUpdateSource.cs` 固定更新后入口为 `VibeOCR.Bootstrapper.exe`。
- `verify_winui_artifact.ps1` 维护一组旧根 required paths。
- `PortableLayout.cs` 把生产 `config/data/output` 放入安装根。
- `UpdateArtifactCleaner.cs` 强制 DataRoot 位于 InstallRoot 内。

因此仅移动 DLL 会破坏启动、XBF/PRI、WebAssets、Runtime、更新和验证契约；必须建立共享 seam。

### 2.2 当前 UI 已有可复用基础，但没有统一产品语言

Web 工作台已经具备 React 19、TypeScript、Vite、React Router、Fluent UI、浅/深/系统主题、七路由、capability gate、跨路由宿主状态和 Vitest/jsdom 测试。当前问题是：

- 橙色主题、页面尺寸和通用 panel 样式未形成 VibeTable 风格的语义 token。
- Fluent 图标只覆盖导航与少量工具，业务动作大多没有稳定图标语义。
- `Pages.tsx` 聚合过多页面与状态映射，降低局部性和可审查性。
- 只验证 DOM/行为与 packaged bridge-ready，没有 Playwright 视觉回归。
- 没有正式 SVG/ICO/PNG 品牌事实源，EXE/托盘仍使用默认图标或文字 `V`。

## 3. 目标发布与数据布局

```text
VibeOCR/
├─ VibeOCR.exe
├─ LICENSE
├─ CHANGELOG.md
├─ app/
│  ├─ VibeOCR.WinUI.exe
│  ├─ VibeOCR.WinUI.dll
│  ├─ *.dll / *.json / *.xbf / *.pri
│  ├─ WebAssets/
│  ├─ runtimes/                 # dotnet publish 的原生依赖闭包
│  ├─ tools/
│  │  └─ updater.exe
│  └─ metadata/
│     ├─ product-layout.json
│     ├─ product-release-manifest.json
│     ├─ component-lock.json
│     └─ component-identities.json
└─ runtime/
   ├─ backend/
   ├─ installer/
   │  └─ vibeocr-runtime-installer.exe
   └─ components/

%LOCALAPPDATA%/VibeOCR/
├─ config/
├─ logs/
├─ cache/update/
├─ web-resources/
├─ webview2/
└─ window/
```

根目录采用严格 allowlist：`VibeOCR.exe`、`LICENSE`、`CHANGELOG.md`、`app/`、`runtime/`。发布包拒绝 PDB、源码、source map、测试、缓存、`node_modules`、用户数据和其他根级 EXE/DLL/JSON。

net472 SDK 自动产生的 `VibeOCR.Bootstrapper.exe.config` 当前只声明 `supportedRuntime`，没有 binding redirect 或产品设置。组装阶段将其排除，使重命名后的根入口保持单文件；代价通过 Windows 10/11 干净环境中的真实启动 smoke 覆盖。若该 smoke 证明 config-free 不兼容，则必须重新评审根 allowlist，不能把 `.config` 偷偷加回。

## 4. 深模块与稳定 interface

### 4.1 `ProductLayout`：产品布局深模块

跨语言 external seam 是固定位置 `app/metadata/product-layout.json`。它只描述调用者需要的路径与 schema，不复制 `product-release-manifest.json` 的文件 hash/identity 职责。

```json
{
  "schema_version": 1,
  "product_id": "vibeocr",
  "public_entry": "VibeOCR.exe",
  "roots": {
    "app": "app",
    "runtime": "runtime",
    "metadata": "app/metadata"
  },
  "app": {
    "entry": "app/VibeOCR.WinUI.exe",
    "web_assets": "app/WebAssets",
    "updater": "app/tools/updater.exe"
  },
  "runtime": {
    "manifest": "runtime/backend/runtime-manifest.json",
    "installer": "runtime/installer/vibeocr-runtime-installer.exe"
  },
  "metadata": {
    "component_lock": "app/metadata/component-lock.json",
    "component_identities": "app/metadata/component-identities.json",
    "release_manifest": "app/metadata/product-release-manifest.json"
  },
  "user_data": {
    "known_folder": "LocalApplicationData",
    "relative": "VibeOCR"
  }
}
```

Interface 不变量：

- schema 和 product id 必须匹配；未知版本 fail closed。
- 所有产品路径必须是规范化相对路径，不允许绝对路径、盘符、`..`、链接逃逸或根重叠。
- public entry 精确为根 `VibeOCR.exe`，app/runtime/metadata 必须落在声明根内。
- 根目录实际条目必须与 allowlist 完全一致。
- WinUI entry、PRI、XBF、WebAssets index、Runtime manifest、installer、component lock、component identities 和 release manifest 必须存在且非空。
- app publish 闭包整体进入 `app/`；不得把 XBF/PRI 或 dotnet `runtimes/` 拆散。
- 用户数据位置由 known-folder policy 解析，不允许描述符注入绝对数据路径。

稳定错误 code：`layout.unsupported-schema`、`layout.product-mismatch`、`layout.invalid-path`、`layout.missing-entry`、`layout.root-conflict`、`layout.closure-mismatch`。错误包含面向开发者的相对路径，但不泄露用户目录内容。

### 4.2 构建 adapters

- `scripts/product_layout.py` 是布局规划、解析、stage 和 verify 的 Python implementation；测试通过临时目录 adapter 覆盖。
- `scripts/package_product_release.py` 保留发布绑定职责，但改为接收 WinUI publish、Bootstrapper publish、updater 与 release inputs，调用 `ProductLayout.stage()` 生成最终树和 descriptor，再生成 release manifest 与确定性 `VibeOCR-v<version>-win64.zip`。
- `scripts/build-release.ps1` 只编排 Web build、dotnet publish 到独立中间目录、PyInstaller、package CLI 和稳定 verifier/smoke，不再复制平铺文件。
- `scripts/verify_winui_artifact.ps1` 保留稳定脚本入口，内部委托 `product_layout.py verify` 与现有发布 identity/closure 验证，不再维护第二份路径清单。

### 4.3 C# runtime adapters

运行时解析只需要 BCL 能力。为了让根 net472 launcher 仍是单 EXE，不新增会被复制到根的共享 DLL：

- 在 `src/dotnet/VibeOCR.ProductLayout.Shared/` 放置 net472-compatible 共享 C# 源码。
- Bootstrapper 与 Platform csproj 通过 linked compile 引用同一源码；相同 implementation 分别编译进两个现有程序集。
- Bootstrapper 使用 `ProductLayout.Open(installRoot)` 做 preflight，启动 `layout.AppEntry`，并通过显式参数或受控环境变量把 install root 交给 WinUI。
- Platform/App 只从 resolved layout 取得 Runtime/WebAssets/metadata/user-data，不再自行 `Path.Combine(installRoot, ...)`。
- 测试 surface 是 `ProductLayout.Open` 的可观察结果和稳定错误，不测试私有解析步骤。

### 4.4 `UpdateTransaction`：新布局更新深模块

GitHub 下载仍是 true external adapter；下载并校验完成后，更新事务只接收一个 handoff 文件：

```json
{
  "package": "...zip",
  "install_root": "...",
  "user_data_root": "...",
  "health_file": "..."
}
```

执行顺序：

1. 从 ZIP 唯一定位 `app/tools/updater.exe`，复制到 `%LOCALAPPDATA%/VibeOCR/cache/update/<transaction>/` 后启动。
2. 解压候选到 LocalAppData transaction；在触碰当前安装前完成 layout、allowlist、release manifest、component binding 和文件 closure 验证。
3. 将已验证候选复制到安装根同卷、同级且带 transaction id 的 deployment stage，再次验证完整布局；这关闭 LocalAppData 与安装盘不同卷时 rename 必然失败的边界。
4. 将当前五个部署项 `VibeOCR.exe`、`LICENSE`、`CHANGELOG.md`、`app/`、`runtime/` 整体移入同卷 rollback，再把 deployment stage 的同名项移入安装根；不做逐 DLL 混合更新。
5. 启动新 `VibeOCR.exe`；Bootstrapper 启动 WinUI 后由 App 写入 health。
6. health 成功才清理 LocalAppData transaction、同卷 stage/rollback；超时或启动失败则恢复完整旧部署项并重新启动旧 `VibeOCR.exe`。
7. rollback 失败时保留同卷现场并返回稳定终态，不继续删除。

`product-layout.json` schema 1 的新树是唯一支持的输入和更新来源。没有 descriptor、schema 不匹配或旧平铺结构直接返回 `update.unsupported-layout`，不执行迁移或删除。

## 5. UI 设计与交互 architecture

### 5.1 视觉方向

定位为“本地文档识别工作台”：冷静、紧凑、清晰，突出输入到结构化结果的转换。避免营销式大卡片、紫色渐变、夸张圆角、大面积背景动画和持续漂浮装饰。

- 字体：Windows 中文系统字体栈；数字、路径和诊断使用 Cascadia Mono。标题不引入在线字体。
- 色彩：冷中性 surface + 蓝色 `#3370ff` 主状态；成功/警告/错误使用独立语义色。
- 尺度：4px spacing 基线；4/6/8px radius；12/13/14/16/18px 主字阶；轻边框与克制阴影。
- 动效：120ms 快速反馈、200ms 面板变化；只解释状态，不装饰；完整支持 reduced-motion。
- 焦点：2px primary focus ring + 2px offset；forced-colors 下使用系统色。

### 5.2 设计系统落点

- `src/styles/tokens.css`：原始尺度与语义 token、浅/深色映射、motion、focus、density。
- `src/theme/theme.ts`：将 Next token 映射到 Fluent Theme；不让页面直接写品牌 hex。
- `src/components/ui/`：`AppIcon`、`IconButton`、`PageState`、`InlineNotice`、`TaskProgress`、`EmptyState`、`Panel` 等少量深模块。
- `lucide-react` 是唯一功能图标依赖；删除 `@fluentui/react-icons`。图标默认 16/18/20px、统一 stroke，icon-only 必须具有 Tooltip、`aria-label` 和至少 32x32 点击区域。
- 导航、主要动作和危险动作使用图标+文字；只有关闭、返回、更多、复制等通用次要动作允许纯图标。

### 5.3 品牌资产

- `assets/brand/generated/` 中的 PNG 与 ICO 是直接维护、随仓提交的品牌资产，采用“扫描框 + 文档/文字识别”几何语义，与 VibeTable 同族但不复用其标志。
- 品牌修改通过普通文件 diff、UI 视觉验证与真实打包验证评审；CI 不启动浏览器重新生成或逐字节复算图像。
- WinUI、Bootstrapper、PyInstaller updater、托盘、Web 壳、启动/恢复页和关于页使用同一版本资产。

### 5.4 信息架构

主导航按任务组织：

1. 识别：文件、截图、剪贴板输入；配置；编辑；结果。
2. 批量：队列、预览、任务与导出。
3. PDF：页面管理、编辑、文字层、OCR 与保存。
4. 工具：二维码生成/识别；未来只有确有能力时再加入其他工具。
5. 设置：识别默认项、快捷键、外观、Runtime/模型/缓存；诊断放高级区域。
6. 关于：版本、许可、项目与更新。

宽屏允许上下文双/三栏，1024x720 通过 rail 收缩、面板折叠和内部滚动保持主动作可见；不把专业工作区简单堆叠成长页面。

### 5.5 状态与任务语义

页面状态使用判别联合而非散落布尔值：`empty | ready | loading | running | cancelling | success | empty-result | partial | recoverable-error | blocking-error | runtime-unavailable`。每个状态明确图标、文案、主/次动作、导航与取消能力。

- C# `WorkbenchApplication` 继续拥有任务和平台状态；React 只拥有可撤销的局部展示草稿。
- 路由切换不取消任务；全局任务状态可返回对应页面。
- 取消期间显示 cancelling，底层 worker 确认停止后才允许重开；批量保留完成项。
- PDF/图片编辑建立统一 dirty-state；会丢失不可恢复编辑时才确认。
- 页面错误提供可理解恢复动作，技术详情可展开/复制；短确认用 toast，阻断用 Dialog，Runtime 健康用全局状态。
- 应用重启不恢复执行中任务，不持久化识别历史。

## 6. Classic 功能对照与实施矩阵

| Classic 行为域 | Next 当前证据 | 本 PR 的完成标准 | 主要测试 seam |
|---|---|---|---|
| 单图/截图/剪贴板/复制原图 | recognition route、原生 bridge、Canvas | 输入来源、busy gate、最新输入、全局截图、编辑与结果完整 | Workbench command/state + Vitest + packaged smoke |
| 识别管道与高级参数 | capability/state 已存在但需逐项核对 | 方向/扭曲/语言/页范围/表格/公式等按 Runtime capability 渐进披露 | capability fixtures + typed action/state |
| 结果复制与导出 | export capability、结果视图 | 文本/Markdown、富文本表格、Word/Excel 的成功/失败/取消语义等价 | C# export adapter + Web behavior |
| 批量 | queue、分页、重排、busy/cancel | 添加/去重/重排/并发、真实取消、部分失败继续、当前/全部导出 | Workbench + bounded state + UI |
| PDF | page window、选择、旋转、删除、OCR、保存 | 多文件/页面操作、摆正、插入/删除/重排、文字层、OCR、保存/另存/导出 | PDF command/state + App tests + UI |
| 二维码 | generate/decode/open URL | 生成参数、颜色/反色/Logo/标签、保存/复制；粘贴/拖放/清空/安全 URL | QR command/state + Vitest |
| 设置/Runtime/模型/缓存 | settings、diagnostics、update | 预加载/TTL/日志、缓存维护、Runtime profile/绑定、依赖重装、快捷方式与更新 | capability matrix + Platform/App tests |

第一步先生成逐项矩阵，引用 Classic 与 Next 的实现/测试位置。已存在能力不得因新布局静默丢失；Runtime/Protocol 缺少必要 capability 时记录为 blocker，不提交假按钮或静态演示。

## 7. 实施工作包与顺序

### WP1：基线、契约和计划审查

- 依据 `CONTRIBUTING.md` 先创建并关联大改 Issue #23；Issue 已写明目标、非目标、风险、验收和本方案路径。
- 运行 Web、Python、App、Platform 的最小相关基线并记录环境。
- 固化 Classic → Next 矩阵和 UI 状态矩阵。
- 让独立 reviewer 从 Spec 与 Standards 两轴审查本方案，先修方案再改代码。

### WP2：ProductLayout 与发布 stage（测试先行）

- 新增合法/非法 descriptor fixture、root allowlist、path escape、缺 XBF/PRI/WebAssets/Runtime、PDB/开发文件拒绝测试。
- 实现 Python `ProductLayout`，改 package/build/verifier。
- 发布到独立中间目录后组装 `app/` 与 `runtime/`；排除 Bootstrapper `.config` 和 PDB。
- 将 component identities 同时保留为正式独立 Release 资产并嵌入 `app/metadata/`，由 release manifest 绑定同一内容。
- 将对外 ZIP 从 `VibeOCR-Next-v*-win64.zip` 收敛为 `VibeOCR-v*-win64.zip`，同步 `.ci/project.json`、下载选择器、checksum/SBOM 和 release smoke 契约。
- 用真实候选验证 XAML/WebAssets/Runtime closure。

### WP3：运行时路径、LocalAppData 与 Bootstrapper

- 新增 C# descriptor/path/user-data 契约测试。
- linked compile 同一 net472-compatible parser 到 Bootstrapper 与 Platform。
- 改 Bootstrapper preflight/launch、App install root 传递、PortableLayout、WebView2 data、日志/设置/输出和 artifact cleaner。
- 保留 dev/self-test 显式临时数据根；production 默认 LocalAppData。

### WP4：新布局更新与回滚

- 先为 stage validation、成功替换、复制失败、health timeout、rollback failure、用户数据不触碰补 Python/C# 测试。
- 改 updater handoff、提取路径、部署项整体替换、health 与 cleanup。
- 只支持 `product-layout.json schema_version=1` 的新树更新到同一 schema；旧平铺包 fail closed。

### WP5：品牌、设计 token、Lucide 与壳

- 用仓库命令安装固定版本 `lucide-react` 并移除 `@fluentui/react-icons`，由 npm 更新 lock。
- 建立 token、主题映射、图标 adapter、品牌生成与一致性测试。
- 重构 AppShell、导航、全局状态、主题菜单、截图动作和诊断入口。
- 补 UI 壳行为测试和 1024x720/1280x800 视觉基线。

### WP6：页面拆分与 Classic 对齐

- 将 `Pages.tsx` 按 recognition/batch/pdf/tools/settings/about 拆分，公共状态模块只保留真正重复的 interface。
- 按矩阵逐域补 UI、typed actions/state、取消/错误/dirty-state 和回归测试。
- 每完成一域运行对应 Vitest 与 App/Platform 定向测试，不把所有反馈拖到最后。

### WP7：Playwright、真实 GUI 与发布闭包

- 增加 mock bridge Playwright harness，固定动画/时间/数据，生成少量稳定截图。
- 覆盖浅色 1024x720 空识别、深色 1280x800 结果、批量部分失败、PDF 选择、Runtime 阻断错误和设置高级区。
- 运行 Web format/lint/type/test/build、Python Ruff/type/tests、App/Platform tests、真实 release build/release smoke。
- 在 Windows 10/11 x64 对 config-free `VibeOCR.exe`、高 DPI、托盘、快捷键、更新/回滚和 WebView2 进行人工证据复核。

### WP8：独立审查与 PR

- Standards review：仓库规则、接口深度、错误/取消、生成物、lock、发布门禁、可维护性。
- Spec review：本文件、Classic 矩阵、目标目录、状态/视觉/无障碍和用户已确认边界。
- 修复所有可操作发现，重跑受影响验证。
- 多个中文 Conventional Commit 推送到同一分支，创建包含截图和精确验证结果的 Draft PR；不合并。

## 8. 测试与证据矩阵

| 风险 | 最小回归 | 集成/契约 | 真实证据 |
|---|---|---|---|
| layout schema/path 漂移 | Python + C# shared fixtures | package → verifier | ZIP tree/manifest/identity |
| XBF/PRI/WebAssets 移动 | missing-file negative tests | App publish + artifact verify | packaged WebView2 ready |
| 根目录重新变乱 | strict allowlist/PDB deny | deterministic package | 解压目录截图/清单 |
| LocalAppData 路径错误 | C# path tests | App bootstrap/profile isolation | 实际日志/设置路径 |
| 更新半新半旧 | failure injection | updater success/rollback | 新布局间更新 smoke |
| Lucide 回流/混用 | dependency/import deny | Web build | 页面截图 |
| 主题与响应式退化 | token/DOM tests | Playwright fixed viewport | Windows 高 DPI 截图 |
| 取消/陈旧完成竞态 | generation/cancel tests | Workbench state | 长任务人工路径 |
| Classic 功能丢失 | 功能矩阵逐项测试 | Backend capability contract | 代表性真实文件流程 |

测试调整原则：

- 新测试通过稳定 interface 观察行为；已有浅实现测试在新 interface 覆盖后替换而不是叠床架屋。
- 先运行最小定向测试，再跑项目质量入口；最终完整矩阵以 PR `required` 为权威。
- 不通过更新脆弱快照、降低覆盖率、吞错误、添加无依据重试或跳过 E2E 获得绿色结果。
- GUI、Windows Runtime、WebView2 或外部模型环境不可用时明确记录未验证，不伪造 passed。

## 9. 风险与控制

| 风险 | 控制 |
|---|---|
| net472 config-free 在部分 Windows 环境启动诊断变弱 | 根 allowlist 契约 + Windows 10/11 干净环境真实启动；失败则重新评审，不静默放宽 |
| 跨语言 descriptor 解析漂移 | 同一 JSON fixtures 和稳定 error code；PowerShell 不再实现第三套解析 |
| 安装根/LocalAppData 混淆导致误删 | 更新 interface 显式接收两个绝对根并验证互不包含；部署项 allowlist，不递归清理未知路径 |
| `Pages.tsx` 重构与功能对齐同时扩大 diff | 先锁 typed state/action 测试，再按用户任务纵向拆分；每域保持可运行 |
| Playwright snapshot 脆弱 | Windows 固定浏览器/字体/viewport、禁动画、只截稳定壳层和关键状态 |
| 品牌生成链引入不稳定依赖 | 固定版本、确定性输入输出、生成一致性检查；不手改派生 ICO/PNG |
| 超大 PR 难审 | 文档矩阵、多个逻辑 commit、两轴独立 review、PR 正文按工作包组织，最终仍一次性交付 |
| 资产名从 Next 收敛后更新器选错包 | `.ci` required assets、GitHub asset selector 与 release smoke 共同使用 `VibeOCR-v*-win64` 精确模式，不提供通用后缀回退 |

## 10. 计划 commit 结构

1. `docs(plan): 固化产品化实施与验收方案`
2. `refactor(layout): 统一产品发布布局与数据路径`
3. `fix(update): 建立新布局更新回滚事务`
4. `feat(brand): 补齐品牌资产并迁移 Lucide 图标`
5. `feat(ui): 重构产品工作台视觉与交互状态`
6. `feat(workbench): 对齐 Classic 用户工作流`
7. `test(product): 补齐视觉与真实发布回归`

实际 commit 只在每个完整意图通过对应门禁后形成；最终 PR 使用 squash merge。

## 11. 完成定义

只有同时满足以下条件才将任务和目标标记 complete：

- 目标 ZIP 根严格符合 allowlist，唯一入口能在支持的 Windows 10/11 x64 环境启动。
- 用户可见目录、入口、界面和正式 ZIP 名使用 `VibeOCR`，不继续暴露 `Next`。
- XBF/PRI/WebAssets、Backend/Runtime、component lock、component identities、release manifest 与 SBOM 契约全部通过。
- 新布局间更新成功，健康失败会完整回滚；用户数据目录不参与替换、回滚或发布包。
- Classic 功能矩阵没有未解释缺口；阻断 capability 已解决或经用户明确批准调整。
- UI 仅使用 Lucide 功能图标，品牌资产全链一致，浅/深/系统主题和关键页面状态完成。
- 1024x720、1280x800、高 DPI、键盘、forced-colors、reduced-motion 与关键 GUI 路径有真实证据或明确未验证原因。
- 定向门禁、本地质量入口、真实 release build/release smoke 通过；GitHub PR CI 已触发并如实报告状态。
- Draft PR 已创建，包含背景根因、变更、风险、Classic 矩阵、精确验证和截图；未擅自 merge。

## 12. 方案评审后的取舍

三套独立 ProductLayout 设计共同支持版本化 descriptor；最终采用混合方案：

- 采用最小 external interface：固定 descriptor + `Open/Stage/Verify` 行为。
- 采用默认调用最简单的 handoff/update 流程与根 allowlist。
- 保留未来 schema version，但不建立 plugin registry、layout discovery 或尚不存在的多产品 adapter。
- 删除三份方案中的 legacy reader、旧数据迁移和旧版兼容建议，遵守“开发基线无历史包袱”。
- metadata 放入 `app/metadata/` 而非新增根目录，遵守用户已确认的发布树。
- C# 使用共享源码 linked compile 而非额外根级 DLL，保持 root launcher 单文件。

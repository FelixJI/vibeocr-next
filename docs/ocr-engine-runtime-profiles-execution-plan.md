# OCR 引擎、Runtime Profiles 与 Portable 客户端执行计划

> 适配基线：2026-08-17。Protocol `v2.7.1` 已正式发布；Backend `main`
> `c1178ab` 已实现三引擎、可选组件、下载源、持久化 Settings、精确安装 scope 与
> base/full 运行时修复，但这些 Backend 变更尚未进入正式 Release：最新正式 tag 仍是
> 变更前的 `v0.11.2`。Next 当前 `Directory.Packages.props` 与四份 NuGet lock 仍精确
> 锁定 2.5.0，且尚未实现三项选择 UI。本文因此从“等待协议实现”调整为“升级 2.7.1
> 强类型包、等待并严格绑定下一版合格 Backend Release、完成 Platform/App 接入”。

## 0. 当前事实与发布闸门

| 层 | 当前事实 | Next 必须采取的动作 |
|---|---|---|
| Protocol | engine capability 自 2.6.0 引入；component/source 两项能力自 2.7.0 引入；2.7.1 统一空 source selection 的 Python/.NET 序列化 | `VibeOCR.Runtime.Contracts` 与 `VibeOCR.Runtime.Client` 精确锁到 2.7.1 并重生成四份 lock；空 source 在线上必须省略，不能发 `[]` |
| Backend 代码 | 当前 `main` 已由 `OcrEngineResolver`、profile-aware manifest 与 `RuntimeSelectionPolicy` 提供 engine/component/source 契约 | Platform 只消费 catalog、normalized intent 与 requested/effective 回显；App/WebAssets 不复制依赖闭包或探测逻辑 |
| Backend Release | 最新正式 `v0.11.2` 早于六个相关提交；当前 `build_runtime_manifest.py` 的默认 capability 列表仍缺三项新能力 | resolver 把三项能力加入 required capabilities；最新 Release 不合格时 fail closed，不回退 |
| Next | 当前 `RuntimeInstallerClient`、`IInferenceClient` 与 SettingsViewModel 仍是 2.5 surface | 先升级包与 lock，再扩展 Platform facade、Settings、request 和 maintenance；不在 App/WebAssets 手写 2.7 DTO |
| 产品发布 | 当前 `.ci/project.json` 仍要求 Setup、sidecar、Portable、NUPKG/feed | Portable-only 仍是后续工作包，不能写成已完成；迁移时保留 NUPKG/feed 供 Velopack 自更新 |
| 可变状态 | `PortableLayout.Resolve` 与 product descriptor 默认 `%LOCALAPPDATA%/VibeOCR`；Bootstrapper 日志直接写 LocalAppData；WebView2 使用默认 user-data folder | 不能把当前 Portable ZIP 误报为完全便携；生产路径改为 `<portable-root>/state`，同步 descriptor/闭包校验并禁止用户目录 fallback |

Protocol 2.7.1 的 wire schema、Python/.NET DTO 与生成绑定已经支持
`PipelineSelection.engine`。Python stdlib parser 当前仍拒绝该字段，Backend 因此继续保留
`_extract_engine_selection` seam；这不阻塞 Next 使用 2.7.1 C# record 发送 engine，但
只有 Protocol parser 修复并被 Backend 正式绑定后，才可把该 Backend seam 视为可删除。

## 1. 产品目标与架构边界

Next 只承担 Windows 客户端职责：选择与持久化用户偏好、构造强类型请求、展示
Backend catalog/status、编排 Runtime 和 Velopack 更新。OCR/PDF/MinerU 推理、依赖闭包、
source endpoint 解析和安装执行全部属于 Backend。

目标：

- Portable 解压后禁网安装 `base-offline`，RapidOCR 与基础 PDF 首次即用。
- 全局默认 RapidOCR，任务可临时 override；Windows/PaddleOCR 不可用时 fail closed。
- feature/accelerator 选择映射为 Backend component id，full CPU/CUDA 只在用户确认后
  按当前 Backend release hash lock 在线安装。
- 下载源按开放 kind 分组，每个已知 kind 至多选择一个；安装 operation 固化 source
  intent，不受并发 Settings 修改影响。
- model registry 首版只公开 PaddleX 与 MinerU 的共同远端来源 `huggingface`、
  `modelscope`；AIStudio/BOS 是 PaddleX 专属能力，在现有全局 kind 契约下暂不展示。模型
  acquisition 完成后，推理只绑定 portable 本地模型路径。
- UI 只消费既有 Runtime snapshot/events/heartbeat/progress；不建立第二套状态机。
- 所有产品拥有的配置、缓存、日志、Runtime、模型、输出、更新状态、WebView2 profile 和
  临时文件都位于 `<portable-root>/state`；稳定 portable 根不能混同会被更新替换的
  `current`/应用版本目录。
- 用户可见 Release 只提供 `VibeOCRNext-Portable.zip`；NUPKG/feed 留给 Velopack
  自更新，移除 Setup 用户入口。

非目标：WebAssets 不直连 Backend、不反序列化 Protocol DTO、不保存 source endpoint；
Platform 不实现 Python/pip 逻辑；不把 full 依赖塞入应用 NUPKG 或 Portable。“完全便携”
约束产品拥有的文件，不承诺移除系统 WebView2 Runtime、Windows 语言包或命名管道；但
WebView2 的 VibeOCR user-data/cache/cookies 必须在 `state` 内。

## 2. 上游门槛

1. Protocol 门槛已经满足：将 `Directory.Packages.props` 的两个 VibeOCR 包更新为正式
   `[2.7.1]`，并通过 `scripts/update_dotnet_locks.ps1` 重生成四份 lock；不能手改 generated
   record 或 lock。
2. Backend 代码门槛基本满足，Release 门槛尚未满足。下一版正式 Backend Release 必须：
   - 在 runtime manifest 与 health descriptor 中同时声明三项新 capability；
   - 携带同源 base pack、manifest、component lock 与绑定 Protocol；
   - 包含当前 resolver、selection policy、Settings/source 与 base/full 修复；
   - 通过既有 attestation/checksum/SBOM 契约。
3. `scripts/resolve_component_releases.py` 的 required capabilities 加入三项新能力，继续只
   接受最新正式 Backend Release。当前最新 `v0.11.2` 应因缺能力 fail closed，不能回退
   或用 main fixture 代替正式 release smoke。
4. Protocol 2.7.1 的 `SettingsSnapshot` 已把 null/空内存 collection 统一序列化为 omission；
   wire `download_source_ids: []` 非法。`DownloadSourceKind` 保持开放 string。
5. Backend Release 只携带 base pack；full 组件的在线 install scope/lock/catalog 来自同一
   Backend identity，不作为额外 runtime pack 被前端解析。
6. Backend model registry 门槛尚未满足：`model_registry` kind 已定义，但 catalog 还只有
   package index。下一版合格 Backend 必须声明稳定 id `huggingface`/`modelscope`，并通过
   PaddleX/MinerU Adapter 执行真实下载；前端在 capability/catalog 出现前不预造选项。
7. 现有 Protocol 每 kind 至多选一个来源，首版仅用两引擎交集即可保持 2.7.1 不变。未来
   若开放 PaddleX 专属 AIStudio/BOS 或按 consumer 分别选择，必须先发布含 consumer scope
   的新 Protocol minor，不能选择两个同 kind id。
8. Portable 根启动时执行 create/write/rename/delete 探针；不可写时 fail closed 并提示移动
   目录，不请求管理员权限，也不回退 `%LOCALAPPDATA%`、用户 profile 或系统 Temp。

## 3. 契约到客户端模型的映射

| 领域 | Protocol 契约 | Next owner |
|---|---|---|
| 引擎目录 | health `ocr_engine_catalog` | Platform 映射为 UI-neutral model；App 本地化显示 |
| OCR 选择 | `PipelineSelection.engine` | `IInferenceClient`/request builder 强类型发送 |
| feature 目录 | 当前 CPU 为 `document_parsing`；CUDA 为 `document_parsing`/`gpu_runtime`，且 CUDA document parsing 闭包含 GPU runtime | Platform 仍按 `feature_id + accelerator` 映射；不得固化当前 feature id 恰好等于 component id，WebAssets 不处理 component id |
| 安装意图 | ensure/retry `install_component_ids` | `RuntimeInstallerClient` 保留 null 与空 list 的差异 |
| source 目录 | 当前只声明 package-index 的 `tuna-pypi`（默认）与 `pypi`；目标再增加 model-registry 的 `huggingface`/`modelscope` | Platform 保留未知 kind；App 只开放 catalog 实际声明的已知 kind，不预造模型源 |
| 默认 source | Backend Settings `download_source_ids` | `InferenceHttpClient` 强类型读写并作为长期真相；App 不维护第二份持久 source 配置 |
| 本次 source | maintenance `download_source_ids` | installer start/retry 显式传入当前选择 |
| 安装状态 | requested/effective component/source ids + existing events | Platform 投影为 immutable state，Workbench bridge 只传 UI 所需字段 |
| Portable 状态根 | 非 Protocol 字段；`PortableLayout` 是唯一 owner | 固定 `<portable-root>/state`；App/Bootstrapper/WebView 不自行读用户目录 |
| 模型根 | Runtime Installer `RuntimeLaunch.model_root` | 固定 `<portable-root>/state/models` 并传给 Backend；Platform 不理解仓库内部布局 |

省略与空值是 wire 语义，不能在 C# collection default 中被抹平：

- `InstallComponentIds = null`：省略，委托 Backend 默认/重用 retry intent。
- `InstallComponentIds = []`：显式 base only。
- `DownloadSourceIds = null`：线上省略；开始时使用 Backend Settings/default，retry 时复用旧 intent。
- 空内存 collection 同样由 2.7.1 serializer 规范化为 omission；source wire 不允许空数组，
  每种 kind 至多一个，数组顺序不表示优先级。

`DownloadSourceKind` 必须投影为 `string`，不能恢复 closed enum。强类型边界位于 descriptor、
request/status record；known values 由 UI policy 识别，未知 response 值原样保留。

统一 state 布局：

```text
<portable-root>/
  VibeOCR.exe / Update.exe / current/ / packages/          # Velopack 应用与更新载荷
  state/
    config/           # app_settings、Backend/MinerU config
    cache/            # HTTP、缩略图及通用 cache
    logs/             # App 与 Bootstrapper
    runtimes/         # 版本化 Backend runtime environments
    models/           # rapidocr/paddleocr/mineru/download staging
    output/
    update/           # app updater cache/staging/journal
    temp/
    locks/
    webview2/         # WebView2 user-data/cache/cookies
```

`state` 不进入 NUPKG 的 app payload，也不在 apply 时被替换；`packages/` 仍是 Velopack
portable 根的一部分。product layout closure 必须显式允许稳定 `state`，同时继续拒绝
应用版本目录中的未知污染文件。

## 4. 深模块与代码落点

### 4.1 Platform Runtime selection service

在 `src/dotnet/VibeOCR.Platform` 增加 UI-neutral 深模块，隐藏 catalog 校验与映射：

- 输入 health descriptors、用户 engine/feature/accelerator/source preference。
- 验证 source id 全局唯一、每 kind 至多一个、`feature_id + accelerator` 唯一。
- 输出 engine request selection 和 immutable maintenance intent。
- capability 缺失、unknown id、无匹配 variant 时返回可判别错误，不猜默认。

`Bootstrap/RuntimeInstallerClient.cs` 继续拥有 Runtime Host/maintenance transport、cursor、
取消和 retry；优先复用 2.7.1 Runtime Client 的正式 record/parser，并为现有 wrapper 增加
strong overload。ensure/retry 使用 `install_component_ids`，repair 继续使用
`component_ids`；旧调用方在迁移期仍可编译，但两种字段不能混用。wrapper 不接受 endpoint
或 Python package 名，只接受 component/source ids。

### 4.2 Inference/Settings seam

扩展 `src/dotnet/VibeOCR.Platform/Inference/IInferenceClient.cs` 与
`InferenceHttpClient.cs`：

- health/catalog 读取复用 Protocol records。
- Settings 支持 read/update `DownloadSourceIds`，以 Backend Settings 为长期真相；2.7.1
  serializer 保证 null/空内存集合都 omission，未知 kind 不丢失。
- OCR job request 接收 nullable task engine override，只有纯文本 OCR pipeline 写 engine。
- 426/428 与 engine/source/component error code 转为 domain error，App 决定显示文案。

`DeferredInferenceClient` 同步 interface；不能让 App/WebAssets 绕过 facade 直接发 HTTP。

### 4.3 App、Workbench 与 WebAssets

`Features/Settings/SettingsViewModel.cs` 保存全局 RapidOCR 默认、feature/accelerator 和
UI 状态；source preference 写入 Backend Settings，不在 App 复制第二份持久真相。任务模型
单独持有 override。`DesktopWorkbenchCommandHandler` 负责
把用户动作变成 selection service 命令，`WorkbenchContracts.cs`/bridge codec 只投影：

- 本地化 display name、availability、reason/status。
- feature/accelerator 选项和是否需要下载。
- 已知 source kind 的单选项。
- operation requested/effective 集合、progress、可取消/可 retry 状态。

WebAssets 不接触 Protocol 包、不保存 endpoint、不自己推导 component id。未知 source
kind 可显示“当前版本暂不支持配置”，但不得导致整个设置页或 health 失败。当前 Backend
未声明 model registry 时，不显示伪造的模型源选项；声明后显示 Hugging Face /
ModelScope，AIStudio/BOS 暂不显示。

### 4.4 PortableLayout 深模块与缓存收口

以 `src/dotnet/VibeOCR.Platform/Bootstrap/PortableLayout.cs` 为唯一生产路径接口：

- `Resolve` 从稳定 Velopack 根构造 `<portable-root>/state`，不再默认
  `Environment.SpecialFolder.LocalApplicationData`。扩展强类型属性覆盖上述目录；调用方
  不拼接根路径。
- `VibeOCR.ProductLayout.Shared/ProductLayout.cs` 与 descriptor 从
  `LocalApplicationData/VibeOCR` 改为 portable-relative `state`，更新 product-root closure，
  明确只读 `runtime/` 与可变 `state/runtimes/` 的不同 owner。
- `VibeOCR.Bootstrapper/BootstrapperLog.cs` 在主应用 layout 尚未加载时，从 Bootstrapper 自身
  executable 定位稳定根并写 `state/logs`；descriptor 失败日志也不能回退 AppData。
- `WebWorkbenchHost.InitializeAsync` 创建 `CoreWebView2Environment`，把 `userDataFolder` 固定
  为 `state/webview2` 后再 `EnsureCoreWebView2Async(environment)`；WebAssets 本身不获取绝对
  state path。
- `RuntimeInstallerConfiguration.ForNext(layout)` 把 `state/models` 和完整 portable launch
  environment 传给 Runtime Host；在任何 PaddleX/MinerU/Hugging Face/ModelScope import 前
  至少设置 `HF_HOME`/`HF_HUB_CACHE`、`MODELSCOPE_CACHE`、
  `MINERU_TOOLS_CONFIG_JSON`。PaddleX 锁定版本的 cache/model 参数经契约测试确认后绑定
  `state/models/paddleocr`，不猜测变量名。
- 生产只使用 portable layout；测试保留 in-memory/temp layout。路径做 containment、junction/
  symlink 与 `..` 校验。若需要兼容旧测试数据，只提供显式一次性 import，不自动读取或双写
  LocalAppData。

### 4.5 Backend model registry seam

Backend 增加 model acquisition interface，至少有 PaddleX、MinerU 生产 Adapter 与 in-memory
测试 Adapter：

- catalog 声明 `{kind: model_registry, id: huggingface|modelscope, endpoint: ...}`；endpoint
  Release-owned、UI 只读，不允许用户任意 URL。
- Adapter 将 source id 映射为 `PADDLE_PDX_MODEL_SOURCE`/
  `MINERU_MODEL_SOURCE`。下载阶段使用所选远端；成功后记录 source/revision/local path，推理
  显式使用 portable 本地模型，避免自动探测和用户目录写入。
- full `document_parsing` ensure 把 PaddleOCR/MinerU 所需模型纳入同一 durable maintenance
  operation，复用 progress/cancel/retry/requested/effective source snapshot。只有所有声明资产
  就绪才标记 ready，首次业务请求不再承担不可观察的懒下载。
- 下载 staging 位于 `state/models/downloads`，成功原子切换；失败不能破坏 base 或既有模型。
  MinerU 配置固定为 `state/config/mineru.json`，通过 `MINERU_TOOLS_CONFIG_JSON` 指向它；
  本地推理使用 `local` + `models-dir`。

### 4.6 Portable、Runtime 与应用更新

`PortableLayout`/product layout 保持 base pack、component lock、identity 和 Protocol
资产的明确路径。full components 安装到持久 runtime/state 目录，与 Velopack 应用版本
目录隔离。

`scripts/build-release.ps1`、`.ci/project.json`、`release_smoke.py` 分阶段改为：

- `VibeOCRNext-Portable.zip` 是唯一用户下载入口。
- NUPKG 与 `releases.win.json` 仅供 Velopack 更新；删除 Setup/sidecar 的 required asset、
  build 和文档契约。
- 移除 `VelopackUpdateCoordinator` 对 `UpdateManager.IsInstalled == false` 的 Portable 硬拒绝，
  使用 packaged portable locator 执行 check/download/apply/restart；更新前再次验证根可写，
  防止 package cache 回落 LocalAppData。
- Portable 内只含 base pack；full 未选择时 release/build/smoke 证明不存在重依赖资产。
- 应用更新与 runtime maintenance 通过协调服务串行化；更新后兼容组件复用，不兼容时
  明确要求重新准备，不在启动时偷偷下载。

## 5. 分阶段工作包

### N0：Backend model registry 前置包

- 发布 `huggingface`/`modelscope` model-registry catalog、Settings round-trip 与 Adapter。
- 将 PaddleOCR/MinerU model acquisition 纳入 durable maintenance，并把全部 cache/config/model
  路径收口到 portable state；通过真实下载、中断、retry、断网本地复用测试。
- 修正 manifest 的三项 capability 后随下一版正式 Backend Release 交付。

### N1：正式 Protocol 包与强类型契约

- 更新 NuGet pins/locks 到 2.7.1，验证 generated `DownloadSourceKind` 是 string、
  `SettingsSnapshot` 空集合 omission。
- 将三项 capability 加入 resolver 门槛，等待包含当前 Backend main 实现且 runtime manifest
  正确声明能力的正式 Release；验证旧 `v0.11.2` 被拒绝。
- `RuntimeInstallerClient` ensure/retry overload 传 component/source ids，保持 component
  null/empty，并隔离 repair `component_ids`。
- `IInferenceClient`/`DeferredInferenceClient`/mock 同步 Settings 与 engine request seam。

### N2：selection service 与配置迁移

- 实现 catalog 业务键校验和 feature/accelerator 映射。
- 新配置默认 RapidOCR；旧配置无值迁移，未知值要求用户重选。
- source ids 保存到 Backend Settings 而非 App 配置，且不保存 endpoint；只允许 Backend
  catalog 中的 id。

### N3：OCR 请求与设置 UI

- 全局 engine 与任务 override 分离；非 OCR pipeline 不发送 engine。
- SettingsViewModel/Workbench/WebAssets 完成 engine、feature 和 source 选择。
- 对当前 Backend 显示 CPU/CUDA 的 `document_parsing`/`gpu_runtime` 和 TUNA/PyPI，但不把
  这些当前值固化为前端协议枚举。
- unavailable/preparation required、426/428 和稳定错误码提供本地化动作提示，不 fallback。

### N4：maintenance operation

- 用户确认后以显式 component/source intent 启动 ensure；base only 发送空 component list。
- observe/reconnect/cancel/retry 复用现有 RuntimeInstallerClient；retry 省略即复用旧 intent，
  显式重选则创建新 normalized intent。
- requested/effective 回显是安装真相；dependency closure 扩大时 UI 如实展示。

### N5：完全便携状态根

- 切换 `PortableLayout` 与 product descriptor，收口 Bootstrapper 日志、WebView2、Backend
  launch env、更新 cache，并移除 LocalAppData fallback。
- 加入不可写/containment 探针和文件系统写入审计；验证含空格/中文/长路径的可写根成功，
  只读根明确失败。

### N6：Portable-only 与 release

- 删除 Setup 用户交付路径，保留 Velopack update feed；更新精确资产测试和文档。
- clean-machine 禁网 base install + RapidOCR/PDF smoke。
- full online install、失败保持 base、应用更新后复用组件和 frozen WebView UI smoke。

## 6. 测试矩阵

C# 单元/契约：

- Protocol records 对开放 source kind 的 round-trip；未知 kind 不抛反序列化异常。
- null/empty/non-empty component lists 的 JSON；source null/空内存集合都 omission，wire 不出现
  `download_source_ids: []`；非空 source 每 kind 单选。
- repair `component_ids` 与 ensure/retry `install_component_ids` 不交叉。
- catalog duplicate source id、duplicate feature+accelerator、unknown component/source fail closed。
- 全局 RapidOCR、任务 override、非 OCR pipeline、引擎 unavailable/preparation-required。
- Settings 修改与 operation snapshot 竞态；retry reuse 与 explicit replace。
- Workbench bridge round-trip 不泄漏 endpoint/内部依赖信息，未知 kind 有降级显示。

集成/E2E：

- `RuntimeInstallerClient` 对 receipt/status/observe/cursor expiry/cancel/retry 的现有用例扩展
  requested/effective component/source assertions。
- resolver 对当前正式 `v0.11.2` 因缺三项 capability fail closed，对下一版合格 Backend
  Release 才成功，并分别记录 runtime-bound Protocol 与 frontend SDK identity。
- clean Windows 解压 Portable，断网安装 base 并完成 RapidOCR/基础 PDF。
- Windows OCR 有/无语言包；PaddleOCR 未准备→确认下载→完成→OCR。
- full 未选择零下载；CPU/CUDA 只装选定 closure；中断、空间不足、lock 不匹配保持 base。
- 安装期间改变 Settings 不影响当前 sources；下一 operation 使用新偏好。
- Hugging Face/ModelScope 分别准备 PaddleOCR 与 MinerU；完成后断网重启仍只读本地模型，
  source 改动只影响下一次 acquisition。
- 将 `USERPROFILE`/`LOCALAPPDATA`/`TEMP` 指向监控目录后运行设置、WebView2、OCR/PDF、Runtime
  ensure、Bootstrapper 错误与更新检查，断言监控目录无产品新增文件；不可写根 fail closed。
- Velopack 更新不重复下载兼容 full components；不兼容组合 fail closed。
- WebAssets locked build、CSP/offline closure、WinUI/WebView frozen smoke 与 release smoke。

先运行受影响项目的 format/build/test，再运行 `.ci/project.json` quality、E2E、release
build/smoke；完整结果以 GitHub PR `required` 为权威。

## 7. 完成标准与 PR 边界

- [ ] 两个 NuGet 包与四份 lock 已精确绑定 Protocol 2.7.1；下一版正式 Backend Release 的
  runtime manifest/health 同时声明三项能力并被严格绑定，latest 不兼容时不回退。
- [ ] 引擎、feature/accelerator、source 三类选择均由 Backend catalog 驱动。
- [ ] C# 强类型 client 保留 omission/empty/open-kind/snapshot/retry 的精确 wire 语义。
- [ ] base 禁网可用，full 仅显式在线安装，失败不破坏 base。
- [ ] Backend catalog 正式声明 Hugging Face/ModelScope；PaddleOCR/MinerU 经同一 portable
  model store 准备并可断网复用，用户 profile 无 cache/config 写入。
- [ ] Next 配置、日志、缓存、Runtime、模型、输出、临时文件、WebView2 profile 与更新状态
  全部位于 `<portable-root>/state`；不可写时不回退。
- [ ] 用户资产只有 Portable，Velopack 应用更新与 Runtime 组件生命周期正确隔离。
- [ ] clean-machine、WebAssets/WinUI、release build/smoke 和 PR required 全部通过。

建议 PR 顺序：Backend model registry/Release gate → Protocol locks → selection
service/config → inference/UI → maintenance → Portable state → Portable release。每个 PR 只
表达一个可验证意图；不得把 Backend main 状态
写成正式 Release 已就绪，不得在 UI 中复制 Protocol DTO 解析、让 WebAssets 直连 Backend、
将 full pack 加回 Release、回退 `v0.11.2`，或通过忽略未知 kind/错误码使测试表面通过。

## 8. 外部能力依据（实施时重新核对锁定版本）

- PaddleX 模型源：<https://paddlepaddle.github.io/PaddleX/latest/en/module_usage/tutorials/ocr_modules/layout_detection.html>
- MinerU 模型源与本地模型：<https://opendatalab.github.io/MinerU/usage/model_source/>
- MinerU 配置路径：<https://opendatalab.github.io/MinerU/usage/cli_tools/>
- Hugging Face cache 环境变量：<https://huggingface.co/docs/huggingface_hub/en/package_reference/environment_variables>
- ModelScope cache：<https://www.modelscope.cn/docs/sdk/cli>
- Velopack Portable 交付：<https://docs.velopack.io/packaging/overview>

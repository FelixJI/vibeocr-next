# VibeOCR Next：Velopack 更新器迁移实施方案

## 1. 结论

VibeOCR Next 采用 Velopack 1.2.0 C# SDK 与同版本 `vpk` CLI。本次实现先发布兼容旧 ZIP 的
桥接版本，并同时启用 Velopack 原生安装/更新；桥接窗口结束后再由独立清理 PR 删除 Python
`updater.exe`、`update_replacer.py` 和旧 GitHub ZIP 更新链路。

启动后健康检查失败自动回退不是上线门禁。强制保留的是：包校验、更新互斥、应用退出前
优雅停机、应用失败时旧版本仍可启动、真实安装包启动 smoke，以及独立于安装目录的用户数据。

实施顺序建议在三个产品中排第一，因为 C# SDK 暴露自定义 `IUpdateSource`/`IFileDownloader`
Seam，现有生产 `DataRoot` 也已经固定在 `%LocalAppData%\VibeOCR`。

## 2. 固定决策

| 项目 | 决策 |
|---|---|
| Pack ID | `VibeOCRNext` |
| 安装根 | Velopack 默认 `%LocalAppData%\VibeOCRNext` |
| 用户数据 | 保持 `%LocalAppData%\VibeOCR`，不得进入 Velopack `current/` |
| Channel | 首版只用默认 `win`，feed 为 `releases.win.json` |
| Feed | `https://github.com/FelixJI/vibeocr-next/releases/latest/download/` |
| Package | 首版只发布 full nupkg，禁用 delta；稳定两版后再评估 delta |
| 新用户入口 | `VibeOCRNext-Setup.exe`；Portable 只作为手动下载资产 |
| 旧用户入口 | 桥接窗口继续发布 `VibeOCR-v{version}-win64.zip` |
| 代理 | direct、standard HTTP(S) forward proxy、URL-prefix proxy 都要通过测试 |
| 健康回退 | 不要求新版本启动失败后自动退回；不得删除真实启动 smoke |

Velopack 会整体替换 Windows 安装下的 `current/`，持久文件必须在其外；官方资料见
[Integrating Overview](https://docs.velopack.io/integrating/overview) 和
[Preserving Files & Settings](https://docs.velopack.io/integrating/preserved-files)。发布资产及
`releases.win.json` 语义见
[Packaging Overview](https://docs.velopack.io/packaging/overview) 与
[Distributing Overview](https://docs.velopack.io/distributing/overview)。

## 3. 目标 Module 与 Interface

调用方只依赖一个小型 `IUpdateCoordinator` Interface：

```csharp
Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken);
Task<UpdateApplyResult> DownloadAndApplyAsync(
    IProgress<int>? progress,
    CancellationToken cancellationToken);
```

`UpdateCheckResult` 只表达 latest/available/not-installed/error 和版本、发布说明；
`UpdateApplyResult` 只表达 downloaded/apply-started/cancelled/failed。Velopack 的
`UpdateInfo`、feed、nupkg、locator 和重启参数不穿出 Module。

生产 Adapter 为 `VelopackUpdateCoordinator`，调用方测试 Adapter 为内存 fake，网络 Adapter
测试使用 loopback HTTP/TLS origin、URL-prefix server 与 CONNECT proxy。网络是真实外部 Seam：

- direct：`SimpleWebSource` 指向 GitHub `releases/latest/download/`；
- URL-prefix：把整个 base URL 改写为
  `<prefix>/https://github.com/.../releases/latest/download/`，feed 与 nupkg 必须使用同一候选；
- standard forward proxy：自定义 `IFileDownloader` 内的 `HttpClientHandler.Proxy`，不修改 URL；
- 候选失败按用户配置顺序尝试，某候选取得 feed 后，下载必须绑定同一候选，禁止混源。

`UpdateViewModel` 不再拥有“发现→下载 SHA→抽取 updater→ready 文件”状态机，只把
Interface 结果映射为现有 UI 文案。

## 4. 当前实施状态与后续发布步骤

本工作包纵向完成原方案 PR 1～3 的代码：稳定 Interface、三类 transport、legacy
not-installed 迁移、双格式构建、产品闭包与 Release exact-set。根 `VibeOCR.exe`
（Bootstrapper）和其启动的 `current/app/VibeOCR.WinUI.exe` 都在业务初始化前调用
`VelopackApp.Build().Run()`；真实 Portable probe 已观测子进程 `IsInstalled=True`、
`CurrentVersion=0.1.0`。旧 ZIP 仍包含 `app/tools/updater.exe`，旧客户端因此可以先升级到桥接版。

以下 PR 编号保留为原设计和运营顺序；PR 4～5 仍是未来发布/清理工作。

### PR 1：阻断式技术 Spike

不改生产默认行为，只建立可执行证据：

1. 在临时 fixture 打包两个最小版本，固定 `Velopack` NuGet 与 `vpk` 为同一 1.2.0。
2. 用 loopback HTTP server 验证 `releases.win.json` 与 full nupkg 的 direct 更新。
3. 用记录请求路径的 prefix proxy 验证 feed 和 nupkg 都收到完整 GitHub URL 前缀。
4. 用 loopback CONNECT proxy 验证显式 forward proxy，不接受“机器上恰好能联网”作为证据。
5. 验证 Portable 和 Setup 安装的 `CurrentVersion`、`IsInstalled/IsPortable`、下载、应用、重启。
6. 验证主进程和 Supervisor/Bootstrapper 都退出后才 apply；不测试启动健康失败自动回退。

退出条件：三种 transport 均通过；full package 校验失败时当前安装仍可启动；应用目录被占用时
返回可诊断失败而不是强杀未停稳的子进程。任何一项不满足就停止后续 PR。

### PR 2：引入新 Interface，不改变发布格式

文件级变更：

- 新增 `src/dotnet/VibeOCR.App/Features/Update/IUpdateCoordinator.cs`；
- 新增 `VelopackUpdateCoordinator.cs`、`VelopackFeedFactory.cs`、`ProxyFileDownloader.cs`；
- 修改 `UpdateViewModel.cs` 依赖新 Interface；
- 修改 `App.xaml.cs` 组合生产 Adapter；
- 在能早于 WinUI 初始化执行的位置调用 `VelopackApp.Build().Run()`；
- 在 `Directory.Packages.props`、`VibeOCR.App.csproj` 和锁文件中锁定 Velopack；
- 新增代理配置模型，但沿用产品现有配置根，不另建第二套设置真相源。

新运行态已删除 `GitHubUpdateSource`，只保留一个自动检查入口。legacy ZIP 内的 Python
`updater.exe` 仅服务更老客户端升级到桥接版，不再被新 installed/legacy coordinator 调用。

### PR 3：双格式构建与桥接运行态

修改 `scripts/build-release.ps1`：

1. 继续从当前 staged product root 生成组件闭包，Backend/Protocol/runtime 不在客户端重组；
2. 对同一 staged root 运行固定版本 `vpk pack`，入口为根 `VibeOCR.exe`；
3. `vpk` 输出先进入独立 build 目录，只复制声明的正式资产到 artifacts；
4. 同时生成旧格式 ZIP，供未迁移客户端更新到同版本桥接程序；
5. Setup 生成 SHA-256 sidecar，SBOM/identity 覆盖 ZIP、full nupkg、Setup、Portable 和 feed；
6. CD 仍只发布触发它的 main CI 候选，不运行 `vpk pack`、不改 feed。

桥接期精确 Release 资产：

- 现有五项：legacy ZIP、ZIP `.sha256`、`component-lock.json`、
  `component-identities.json`、`SBOM.spdx.json`；
- `VibeOCRNext-{version}-full.nupkg`；
- `VibeOCRNext-Setup.exe` 与 `.sha256`；
- `VibeOCRNext-Portable.zip`；
- `releases.win.json`。

修改 `.ci/project.json`、`scripts/release_smoke.py` 和相邻 contract 测试，使上述集合 fail closed；
现有 `verify_winui_artifact.ps1` 继续验证 legacy ZIP。公共 `scripts/automation_core.py` 未修改。

legacy 布局中运行时，Coordinator 返回 `not-installed`，UI 显示“一次性迁移到新版安装器”。
它下载并校验同版本 Setup，退出并运行 Setup；数据仍在原 `%LocalAppData%\VibeOCR`，无需复制。
迁移成功前保留旧目录；以后误开旧入口时只转发到 Velopack 根 stub，不再次拥有更新状态机。

### PR 4：切换默认与桥接发布

1. 发布首个双格式版本，保留旧更新按钮语义；已安装用户直接走 Velopack，legacy 用户走 Setup 迁移。
2. 发布第二个双格式版本，证明：旧版本→最新 legacy ZIP→同版本 Setup→下一版本 nupkg。
3. 至少维持两个正式版本或 90 天桥接窗口，以较长者为准。
4. 文档明确：超过迁移 floor 的极老版本需手动运行最新 Setup。

### PR 5：桥接窗口结束后的独立清理

桥接窗口结束后一次性删除：

- `GitHubUpdateSource.cs` 与对应 asset 选择测试已由本次 replace-don't-layer 完成；
- `scripts/updater_main.py`、`scripts/update_replacer.py` 及 runtime 测试；
- `app/tools/updater.exe` 的 Python/C# layout 字段、构建步骤和 artifact cleaner 规则；
- legacy ZIP 的发现、SHA 下载、ready/health 文件协议；
- PyInstaller updater 构建依赖。

只保留手动下载所需 Portable/Setup、Velopack 日志入口和稳定数据根。删除应与新 Interface 测试替换
同一 PR 完成，避免长期双轨。

## 5. 必须通过的验收

- direct、prefix、forward proxy 各自完成 feed + full nupkg 更新；代理失败后按配置回退。
- 取消下载不退出应用；损坏 nupkg 报校验错误，当前版本仍能启动。
- 同一 App ID 并发更新只有一个成功取得锁，UI 给出“已有更新任务”。
- 更新前 Supervisor、Backend Runtime 与 WebView2 进程完成产品既有优雅停机。
- Setup 安装、Portable 启动、legacy→Setup 迁移、N→N+1 更新均启动真实冻结入口。
- 更新前后 `%LocalAppData%\VibeOCR` 的配置、runtime 和输出证据保持不变。
- 新版本启动失败不要求自动回退，但安装失败不得破坏旧版本入口。
- PR 和 main CI 都运行完整 release build/smoke；CD 不构建、不替换资产。

精确验证入口沿用仓库声明：

```powershell
uv sync --frozen --group dev --group build
uv run python scripts/check_quality.py
dotnet test tests/dotnet/VibeOCR.Platform.Tests/VibeOCR.Platform.Tests.csproj -c Release
pwsh -File scripts/test_app_ci.ps1
uv run python scripts/automation.py ci --event pull_request --source-sha <HEAD_SHA>
```

最后一条是包含 bootstrap、quality、e2e、release build/smoke 的 canonical 全量入口；需要真实组件
Release 输入和 Windows 构建环境。命令细节以 `.ci/project.json` 为准，不在 workflow 复制项目命令。

## 6. 明确不做

- 首版不做 delta、分阶段灰度、后台静默强制更新或跨产品共享 updater。
- 不把新版本启动健康失败自动回退设为 required check。
- 不让 Velopack 修改 PocketBase/Backend Runtime 的数据 authority。
- 不在 CD 重新打包、重新生成 feed 或下载组件。

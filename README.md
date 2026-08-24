<div align="center">

# VibeOCR Next

**基于 .NET 10、WinUI 3 与 React 的现代 Windows OCR 桌面客户端**

[![CI](https://github.com/FelixJI/vibeocr-next/actions/workflows/ci.yml/badge.svg)](https://github.com/FelixJI/vibeocr-next/actions/workflows/ci.yml)
[![Latest Release](https://img.shields.io/github/v/release/FelixJI/vibeocr-next?display_name=tag)](https://github.com/FelixJI/vibeocr-next/releases/latest)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet)](global.json)
[![Node](https://img.shields.io/badge/Node.js-24-5FA04E?logo=nodedotjs&logoColor=white)](src/dotnet/VibeOCR.App/WebAssets/package.json)
[![Platform](https://img.shields.io/badge/Windows-10%20%2F%2011%20x64-0078D4?logo=windows)](.ci/project.json)
[![License](https://img.shields.io/github/license/FelixJI/vibeocr-next)](LICENSE)

[下载](#下载与使用) · [架构](#架构) · [开发](#开发与验证) · [源码导读](docs/source-reading-guide.md) · [Workbench](docs/web-workbench-architecture.md)

</div>

VibeOCR Next 是 VibeOCR 的 WinUI 3 桌面客户端。它使用 .NET Platform 层管理窗口、运行时与 typed
Protocol client，并通过 WebView2 承载 React/TypeScript 高级 workbench。OCR/PDF 推理由独立 Backend
Release 在本机完成。

![VibeOCR Next 运行时安装进度](docs/runtime-install-progress.png)

> [!IMPORTANT]
> Next 仅支持 Windows 10/11 x64，桌面技术栈是 WinUI 3，不是 WPF。Release 会绑定经过验证的
> Backend 与 Protocol 组件。

## 主要能力

- 原生 WinUI 3 桌面识别工作流；
- 本地 Backend 解析、安装、启动、ready 与 capability 协商；
- typed Protocol client 驱动的 job 提交、观察和控制；
- React/TypeScript WebAssets 与 WebView2 高级 workbench；
- 单实例、运行时前置检查和 Windows 打包交付。

## 下载与使用

1. 从 [Releases](https://github.com/FelixJI/vibeocr-next/releases/latest) 下载
   `VibeOCRNext-v<version>-win-x64.zip`。
2. 校验 Portable ZIP 的 SHA-256：

   ```powershell
   Get-FileHash .\VibeOCRNext-v*-win-x64.zip -Algorithm SHA256
   ```

3. 便携版由 Velopack 在应用内检查、下载并应用更新。首次使用相关能力时，
   应用会按 component lock 安装本地 Backend 运行时。

## 架构

```mermaid
flowchart LR
    WinUI["WinUI App / ViewModels"] --> Platform["VibeOCR.Platform"]
    Platform --> Client["Typed InferenceHttpClient"]
    Client -->|"Protocol v2"| Backend["VibeOCR Backend"]
    WinUI --> WebView["WebView2 Host"]
    WebView --> Bridge["Command codec / handler"]
    Bridge --> React["React Workbench"]
    Bootstrapper[".NET Framework Bootstrapper"] --> WinUI
```

WinUI App 负责用户体验与进程生命周期，Platform 层隔离协议、系统和运行时能力；WebAssets 通过明确的
bridge command 与桌面交互，不直接访问 Backend。

## 仓库地图

```text
src/dotnet/
├── VibeOCR.App/                    # WinUI 3 应用
│   └── WebAssets/                  # React 19 / TypeScript / Vite workbench
├── VibeOCR.Platform/               # Protocol、进程与平台 seam
└── ...Bootstrapper.../             # .NET Framework 4.7.2 启动器
tests/dotnet/
├── VibeOCR.Platform.Tests/         # 平台与 Protocol 测试
└── VibeOCR.App.Tests/              # WinUI app tests
scripts/
├── check_quality.py                # Python/Node/.NET 质量编排
├── install_windows_app_runtime.ps1 # 开发/CI 前置条件
├── resolve_component_releases.py   # Backend/Protocol 组件解析
└── automation.py                   # CI/发布稳定入口
docs/                               # 架构、截图与源码阅读文档
.ci/project.json                    # 构建、测试、资产与发布契约
```

Bootstrapper 的精确工程名与当前目录以 `rg --files -g '*.csproj'` 为准。

## 两条核心链路

### 应用启动

`App.xaml.cs::OnLaunched` 依次处理单实例、前置条件、窗口创建与 Supervisor 生命周期；
`InferenceSupervisorProcess` 读取 ready envelope 和 capabilities，再把可用 client 交给应用层。

### 提交识别

`RecognitionViewModel` 通过 deferred/typed client 调用 Platform 的 `InferenceHttpClient`，由后者按
Protocol v2 与 Backend 通信。ViewModel 不应拼 HTTP 或依赖模型内部类型。

完整路线见 [源码阅读指南](docs/source-reading-guide.md)。Web workbench 的 bridge 设计见
[`docs/web-workbench-architecture.md`](docs/web-workbench-architecture.md)。

## 开发与验证

需要 Windows、仓库锁定的 [.NET SDK](global.json)、[uv](https://docs.astral.sh/uv/) 与 WebAssets
声明的 Node/npm 版本：

```powershell
git clone https://github.com/FelixJI/vibeocr-next.git
cd vibeocr-next
uv venv .venv
uv pip install --python .venv\Scripts\python.exe --group dev
$env:VIRTUAL_ENV = (Resolve-Path .venv).Path
$env:Path = "$env:VIRTUAL_ENV\Scripts;$env:Path"
npm ci --prefix src/dotnet/VibeOCR.App/WebAssets
$env:RUNNER_TEMP = (New-Item -ItemType Directory -Force build/runner-temp).FullName
pwsh -File scripts/install_windows_app_runtime.ps1
uv run --no-sync python scripts/resolve_component_releases.py
dotnet restore tests/dotnet/VibeOCR.Platform.Tests/VibeOCR.Platform.Tests.csproj --locked-mode
dotnet restore tests/dotnet/VibeOCR.App.Tests/VibeOCR.App.Tests.csproj --locked-mode
uv run --no-sync python scripts/check_quality.py
dotnet test tests/dotnet/VibeOCR.Platform.Tests/VibeOCR.Platform.Tests.csproj -c Release --no-restore
pwsh -File scripts/test_app_ci.ps1
```

这些命令与 [`.ci/project.json`](.ci/project.json) 对齐。完整 PR CI 还会执行 release build、smoke、组件
identity 与正式资产检查。

## WebAssets

WebAssets 使用 Node 24.x、npm 11.7、React 19、TypeScript 6 和 Vite 8。修改后至少运行其 package scripts
中的 lint、typecheck、test 与 build。WebView2 bridge 变更必须同步更新桌面 handler/codec、前端 client
与两侧测试。

## 发布资产

正式 Release 精确包含 Velopack full nupkg、Setup 与 SHA-256、Portable、feed、
`component-lock.json`、`component-identities.json` 和 SPDX SBOM。版本与派生文件只由自动化脚本更新。

## 参与贡献

请先阅读 [`CONTRIBUTING.md`](CONTRIBUTING.md)、[源码阅读指南](docs/source-reading-guide.md) 和
[Workbench 架构](docs/web-workbench-architecture.md)。UI 可见改动需要在 PR 附截图；提交使用 Conventional Commit。

## 许可证

本项目基于 [LICENSE](LICENSE) 中的条款发布。

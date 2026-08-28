# VibeOCR Next 产品化架构

关联 Issue：[#23](https://github.com/FelixJI/vibeocr-next/issues/23)

## 产品布局

```text
VibeOCR/
├─ VibeOCR.exe
├─ Velopack.dll
├─ Microsoft.Web.WebView2.Core.dll
├─ WebView2Loader.dll
├─ Newtonsoft.Json.dll
├─ LICENSE
├─ CHANGELOG.md
├─ app/
│  ├─ VibeOCR.WinUI.exe
│  ├─ WebAssets/
│  └─ metadata/
│     ├─ product-layout.json
│     ├─ product-release-manifest.json
│     ├─ component-lock.json
│     └─ component-identities.json
└─ runtime/
   ├─ backend/
   └─ installer/vibeocr-runtime-installer.exe
```

根目录是严格 allowlist；PDB、源码、source map、测试、缓存、用户数据和额外根级文件都会使构建失败。
产品目录只包含可由 Velopack 整体替换的程序闭包。

用户可变数据固定在稳定 Portable 根的 `state/`：配置、日志、Runtime、WebView2 数据与输出不进入
Velopack `current/`，也不参与更新替换。应用绝不回退到 `%LocalAppData%`、用户目录或系统临时目录。

## 稳定接口

`app/metadata/product-layout.json` 是 Python build、Bootstrapper、WinUI 与 smoke 共享的路径事实源。
schema 1 描述入口、app/runtime/metadata 根、Runtime manifest/installer、组件 identity 与用户数据 policy；
所有路径必须为规范相对路径，拒绝绝对路径、`..`、链接逃逸和根目录杂项。

Python `scripts/product_layout.py` 负责 stage/inspect/verify；net472-compatible 共享 C# parser 编译进
Bootstrapper 与 Platform。稳定错误包括 `layout.unsupported-schema`、`layout.product-mismatch`、
`layout.invalid-path`、`layout.missing-entry`、`layout.root-conflict`、`layout.closure-mismatch`。

## 构建与发布

1. WebAssets 和 .NET app/Bootstrapper 分别构建到中间目录；
2. `product_layout.py stage` 组装严格 product root 并嵌入已解析 Backend/Protocol 组件；
3. `finalize_product_release.py` 复核组件 Release 绑定并写入文件 closure manifest；
4. `vpk pack` 直接消费该 product root，生成 full nupkg、Portable 和 feed（N6 起 `--noInst`，Setup 退场）；
5. release smoke 校验精确资产与 component identities；当目标版本高于最新正式版时，release build 还会预置
   上一正式 full base，并用候选代码构建合成旧版本 Portable，执行 Velopack check/download/apply/restart，
   断言请求 delta、替换 `current/`、保留稳定 `state/`。同版本构建保持 full-only，当前解包/Web smoke 不替代
   相邻版本 E2E。首个能力版本由迁移前客户端通过 full 更新；从其下一相邻版本开始，真实上一正式客户端
   才具备请求 delta 的能力；首阶段 E2E 使用真实旧 `current/state` 布局，并由新版本 updated hook 从
   Velopack backup 原子迁入稳定根。合成旧版本 E2E 不作为首阶段现网 delta 采用证明。

发布与应用内更新只使用 Velopack；项目不再拥有独立 updater、ZIP 替换事务或健康文件回滚协议。

## UI 与运行时边界

WinUI App 管理窗口、单实例和 Supervisor 生命周期；Platform 层隔离 Runtime、协议和系统能力；
React WebAssets 通过 typed command bridge 与桌面交互，不直接访问 Backend。PocketBase/Backend Runtime
数据 authority 不受应用更新影响。

UI 使用 Fluent UI React v9、Lucide 功能图标和仓库品牌资产。页面状态、取消、错误、dirty-state 与
capability gate 通过 typed state/action 契约表达；路由切换不取消后台任务。

## 完成定义

- 严格产品布局和跨语言 descriptor 契约通过；
- Backend/Protocol/runtime/component identities 与 closure manifest 绑定一致；
- Portable、WebView2 和两版本 packaged update smoke 通过；
- 配置、Runtime 与输出保持在稳定 Portable 根的 `state/`；
- Web、Python、App、Platform 质量入口及 PR release build/smoke 全绿。

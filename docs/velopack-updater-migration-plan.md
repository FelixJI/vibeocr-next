# VibeOCR Next：Velopack 更新器方案

## 结论

VibeOCR Next 只使用 Velopack 1.2.0 C# SDK 与同版本 `vpk` CLI。安装版通过
`IUpdateCoordinator` 检查、下载并应用 full nupkg;用户交付仅 Portable,Portable 同样
位于 Velopack 布局,经默认 locator 执行 check/download/apply/restart,应用前再次
验证便携根可写。项目不再发布旧 Windows ZIP，不再构建 Python updater，也不再下载并启动 Setup 作为桥接。

启动健康失败自动回退不设为 required；包校验、更新互斥、优雅退出、真实 Setup/Portable 启动 smoke
和安装目录外的用户数据仍是发布门禁。

## 固定契约

| 项目 | 决策 |
|---|---|
| Pack ID | `VibeOCRNext` |
| Channel | `win`，feed 为 `releases.win.json` |
| 安装根 | Velopack 默认 `%LocalAppData%\VibeOCRNext` |
| 用户数据 | `%LocalAppData%\VibeOCR`，不得进入 `current/` |
| Package | full nupkg，禁用 delta |
| 新用户入口 | `VibeOCRNext-Portable.zip`（唯一用户资产；N6 起 Setup 不再构建） |
| Portable | `VibeOCRNext-Portable.zip`，不支持应用内自动更新 |
| 代理 | direct、URL-prefix、standard HTTP(S) forward proxy |

正式 Release 精确包含八项资产：

- `VibeOCRNext-{version}-full.nupkg`；
- `VibeOCRNext-Portable.zip`（N6 起 Setup 退场,full nupkg 与 `releases.win.json` 仅供 Velopack 更新 feed）；
- `VibeOCRNext-Portable.zip`；
- `releases.win.json`；
- `component-lock.json`；
- `component-identities.json`；
- `SBOM.spdx.json`。

## 模块边界

调用方只依赖 `IUpdateCoordinator`：

```csharp
Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken);
Task<UpdateApplyResult> DownloadAndApplyAsync(
    IProgress<int>? progress,
    CancellationToken cancellationToken);
```

Velopack `UpdateInfo`、feed、nupkg 和重启参数不穿出模块。网络实现支持：

- direct：`SimpleWebSource` 指向 GitHub Release；
- URL-prefix：前缀包装完整 GitHub base URL；
- forward proxy：`IFileDownloader` 使用 `HttpClientHandler.Proxy`，不改写 origin URL。

候选取得 feed 后，full nupkg 必须从同一 source 下载，禁止混源。Python/C# 代码均不重新实现版本选择、
包校验、目录替换或 rollback。

## 构建与布局

`scripts/product_layout.py` 组装严格 product root；`scripts/finalize_product_release.py` 校验组件 Release
绑定并写入 closure manifest；`scripts/build-release.ps1` 把同一 product root 直接交给 `vpk pack`。
product root 不包含独立 updater，可由 Velopack 整体替换。

根 `VibeOCR.exe` 和 WinUI 入口在业务初始化前运行 Velopack hook。`%LocalAppData%\VibeOCR` 中的
配置、Runtime 与输出不参与更新替换。

## 验收

- direct、URL-prefix、forward proxy 均覆盖 feed 与 full nupkg；
- Portable 不下载或启动 Setup；更新经 Velopack feed 就地应用,根不可写时返回可诊断失败；
- 取消下载不退出，损坏 nupkg 报校验错误，当前版本仍可启动；
- 并发更新只有一个任务取得锁；
- Setup 与 Portable 启动真实冻结入口；
- Release 只包含八项声明资产，extra fail closed；
- PR CI 运行完整 release build/smoke，CD 不重新构建。

不做 delta、后台静默强制更新、启动健康失败自动回退或跨产品共享 updater。

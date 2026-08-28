# VibeOCR Next：Velopack 更新器方案

## 结论

VibeOCR Next 只使用 Velopack 1.2.0 C# SDK 与同版本 `vpk` CLI。安装版通过
`IUpdateCoordinator` 检查、下载并应用 full 或相邻版本 delta nupkg；用户交付仅 Portable，Portable 同样
位于 Velopack 布局,经默认 locator 执行 check/download/apply/restart,应用前再次
验证便携根可写。项目不再发布旧 Windows ZIP，不再构建 Python updater，也不再下载并启动 Setup 作为桥接。

启动健康失败自动回退不设为 required；包校验、更新与 Runtime maintenance 互斥、优雅退出、真实两版本
Portable 更新/restart smoke 和稳定根 `state/` 保留仍是发布门禁。

## 固定契约

| 项目 | 决策 |
|---|---|
| Pack ID | `VibeOCRNext` |
| Channel | `win`，feed 为 `releases.win.json` |
| 安装根 | Portable 根；Velopack locator 解析其运行时 `current/` 布局 |
| 用户数据 | `<portable-root>/state`，不得进入 `current/` 或任何用户目录 |
| Package | 当前 full + 上一正式版到当前版的单跳 delta；客户端最多使用 1 个 delta，其余情况回退 full |
| 新用户入口 | `VibeOCRNext-Portable.zip`（唯一用户资产；N6 起 Setup 不再构建） |
| Portable | `VibeOCRNext-Portable.zip`，通过同一 Velopack feed 就地更新；不下载或启动 Setup |
| 代理 | direct、URL-prefix、standard HTTP(S) forward proxy |

正式 Release 精确包含七项资产：

- `VibeOCRNext-{version}-full.nupkg`；
- `VibeOCRNext-{version}-delta.nupkg`；
- `VibeOCRNext-Portable.zip`（full/delta nupkg 与 `releases.win.json` 同时供 Velopack 更新 feed）；
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

候选取得 feed 后，full/delta nupkg 必须从同一 source 下载，禁止混源。Python/C# 代码均不重新实现版本选择、
包校验、目录替换或 rollback。

`vpk` 生成 delta 后会在中间 feed 带入历史 full；构建先验证它与计划 base 一致，再把正式 feed
归一化为“当前 full + 当前 delta”。上一正式 full 不在当前 Release 重复发布或引用；真实 E2E 已证明
客户端持有本地 base 时仍会选择并应用 delta。

## 构建与布局

`scripts/product_layout.py` 组装严格 product root；`scripts/finalize_product_release.py` 校验组件 Release
绑定并写入 closure manifest；`scripts/build-release.ps1` 把同一 product root 直接交给 `vpk pack`。
product root 不包含独立 updater，可由 Velopack 整体替换。

根 `VibeOCR.exe` 和 WinUI 入口在业务初始化前运行 Velopack hook。`<portable-root>/state` 中的
配置、Runtime 与输出不参与更新替换。`ProductMaintenanceCoordinator` 在 Runtime operation 终态和
Updater apply 之间持有唯一 owner；更新请求可等待或显式取消 Runtime，但绝不并行 apply。

迁移前版本曾把用户数据写在 `current/state`。首次 full 更新时，Velopack 先把旧 `current` 备份到
`packages/tmp_*` 或 1.2.0 实际使用的 `packages/VelopackTemp/tmp_*`，再运行新版本的
`--veloapp-updated` fast callback。Bootstrapper 要求唯一且无 reparse point 的旧 state：稳定目标不存在时
同卷原子提升；稳定目标已被新壳预建时，先把它原子移到固定 recovery 目录，再把完整旧 state 原子提升为
正式 `state`，最后无覆盖合并 recovery。文件冲突、备份含糊或链接逃逸均在迁移前 fail closed；若末段 I/O
中断，后续启动会继续消费 recovery，避免不可重试的半迁移。后续更新只使用稳定 state，不再触发旧布局迁移。

## 验收

- direct、URL-prefix、forward proxy 均覆盖 feed 与所选 full/delta nupkg；
- Portable 不下载或启动 Setup；更新经 Velopack feed 就地应用,根不可写时返回可诊断失败；
- 取消下载不退出，损坏 nupkg 报校验错误，当前版本仍可启动；
- 并发更新只有一个任务取得锁；
- 预置上一正式 full base 的真实 Portable 经 loopback feed 请求 delta、完成 check/download/apply/restart，`state/` marker 保留且
  `LOCALAPPDATA`、用户目录、`TEMP` 无产品状态写入；E2E 只允许 Velopack 1.2 固定日志和 .NET Framework
  创建的空 known-folder 目录，其他外写入 fail closed；
- full bootstrap 以迁移前 `current/state` 布局写入五类 marker，并验证 updated hook 将其迁入稳定
  `<portable-root>/state` 后再启动新版本；
- Release 只包含七项声明资产，extra fail closed；
- PR CI 运行完整 release build/smoke，CD 不重新构建。

上线分为两个相邻 Release：迁移前已发布客户端的
`MaximumDeltasBeforeFallback=-1`，因此首个包含本实现的能力版本即使发布合法 delta，现网旧客户端也会
选择 full；该版本先建立 `MaximumDeltasBeforeFallback=1` 的客户端基线。再下一版开始，真实上一正式版
客户端才会请求单跳 delta。构建中的合成旧版本 Portable E2E 用于证明候选代码的 full/delta 选择、应用与
重启契约，不冒充迁移前公开版本会请求 delta 的证据；首个能力版本仍必须验证 full fallback。

不做多跳 delta、后台静默强制更新、启动健康失败自动回退或跨产品共享 updater。

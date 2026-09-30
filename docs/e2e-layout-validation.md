> 历史记录（2026-09-07）：本文保留旧工作区的审阅结论和当时记录的验证结果，
> 不代表当前 main 或本次 PR 的验证结论。2026-09-30 整理时将仍有效的 PDF 状态修复、
> 窗口适配和测试迁移到最新 main；SDK 前滚和发布资产文档更正已由 main 覆盖。
> Backend/Protocol 架构及“待实施”条目需按现有源码重新判断；本次实际验证见 PR 正文。

# 端到端验证与窗口布局

2026-09-07，在 `codex/maintainability-audit` 上接续首批可维护性修复。

## 已复现与修复

- 640 像素视口下 document 仍为 1024 像素，右侧内容被裁切。移除根节点硬性最小宽度；
  窄窗口改为纵向分区，PDF 工具栏与结果工具栏换行，侧导航可独立滚动。
- FluentProvider 会把 className 传递到 portal。原 `.fluent-root` 的全高/背景样式使弹出层
  遮盖页面，真实点击被拦截。将应用根布局限定到 `#root > .fluent-root`；不使用强制点击绕过问题。
- 大屏工作区宽度限制为 1920 CSS 像素并居中，避免内容无限拉宽；窄窗口保持内容可滚动。
- 原生最小窗口改为 640×480 DIP；恢复尺寸及 WM_GETMINMAXINFO 的下限均受当前屏幕工作区约束。
  两项回归在旧实现上分别复现 200%/150% DPI 下窗口高于工作区，修复后通过。

## 测试分层及范围

| 层次 | 入口 | 验证内容 |
| --- | --- | --- |
| 浏览器交互 | `WebAssets/e2e/workbench.responsive.spec.ts` | 600×400、640×480、1024×600、2560×1440 下七个路由可导航，无全局/页面横向溢出，尾部控件可滚动到达，大屏宽度受限 |
| 浏览器业务交互 | 同上 | 识别发起→运行中跨页→缩放→返回读取结果→复制/导出；拒绝命令→提示→重试→取消；PDF 选择→删除→页列表更新→保存 |
| 原有视觉基线 | `WebAssets/e2e/workbench.visual.spec.ts` | 识别、标注、批处理、PDF 的既有截图和标注导出；本次未更新基线 |
| 原生几何与 App | `WindowGeometryPolicyTests`、`scripts/test_app_ci.ps1` | 高 DPI、小工作区恢复尺寸及全部 App 契约 |
| 打包 WebView2 | `scripts/smoke_web_workbench.ps1` | 启动真实生产 bundle，请求 640×480、1280×800 两档窗口尺寸，等待 React 内容出现且根高度匹配视口、页面无横向裁切 |
| 发布交付 | `scripts/build-release.ps1`、`scripts/release_smoke.py` | 真实 publish、产品布局、Portable 更新、ZIP 解包后公共入口及 WebView2 尺寸 smoke |

浏览器 fixture `workbench-host.ts` 仅替换原生宿主消息边界，仍使用生产 React、路由、BridgeClient、
WorkbenchWebRuntime 和正常鼠标点击。测试明确发送宿主状态并断言命令参数；PDF checkbox 是宿主控制的
异步状态，先点击、再等待消息与状态回传，不假定点击后同步完成。

这些测试验证用户界面到宿主消息的链路，不验证实际 OCR 引擎的文字准确率、模型下载或真实用户 PDF 文件写入。
真实 WebView2 smoke 使用 shell-only 隔离环境；不能把它称为完整 OCR 推理 E2E。

## 本地验证结果

- 修复前新增浏览器场景复现 1024 固定宽度及 portal 拦截点击；修复后全部通过。
- `uv run --no-sync python scripts/check_quality.py`：通过。Python 108 项、Web legacy 14 项、
  unit 27 项、Playwright 12 项（原有 5 项 + 新增 7 项）、格式/lint/typecheck/build 全部完成。
- 最后补充 smoke 缺失布局证据的反向用例后，
  `uv run --no-sync python -m pytest tests/runtime/test_next_ci_adapter.py -q`：41 项通过；
  对该文件的 Ruff check/format 通过。
- `pwsh -File scripts/test_app_ci.ps1`：205/205 通过，无跳过。
- `pwsh -File scripts/build-release.ps1`：通过，真实 WebView2 两档尺寸 smoke 成功。
  当前与正式版同版本，本次 delta plan 为 `None`，更新 E2E 实际验证 full fallback；
  工具统一输出中的“adjacent-delta”字样不代表本次运行了 delta。
- 设置 `AUTOMATION_ARTIFACTS_DIR=<本工作区>/artifacts`、`AUTOMATION_VERSION=0.5.1` 后运行
  `uv run --no-sync python scripts/release_smoke.py`：通过，解包后的产品再次通过两档尺寸检查。
- `git diff --check`：通过。锁文件、依赖版本、公共自动化模块及 CI 拓扑未修改。

新增布局/交互测试放在现有 Playwright testDir，现有 quality 入口会自动执行；打包 smoke 的 ready 信号
必须包含 `layout_sizes_verified: 2`，缺失时 fail closed，不能只证明桥接连接就放行。

日志保存在本工作区 `build/audit/quality-layout.log`、`app-layout.log`、`smoke-contract.log`、
`release-layout.log`、`release-smoke-layout.log`；小窗口截图为 `build/audit/recognition-compact.png`
和 `build/audit/pdf-compact.png`。截图展示滚动到结果/操作区后的视口，已目视检查。

本次只生成本地验证产物，未推送、合并或发布。新版本仍需通过 PR required 与正式发布流程。

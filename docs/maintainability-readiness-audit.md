> 历史记录（2026-09-07）：本文保留旧工作区的审阅结论和当时记录的验证结果，
> 不代表当前 main 或本次 PR 的验证结论。2026-09-30 整理时将仍有效的 PDF 状态修复、
> 窗口适配和测试迁移到最新 main；SDK 前滚和发布资产文档更正已由 main 覆盖。
> Backend/Protocol 架构及“待实施”条目需按现有源码重新判断；本次实际验证见 PR 正文。

# Next 可维护性与可用阶段审阅

审阅日期：2026-09-07。基线：远端 main `3287214cd7438ea44177fbbeba1c9796259ac02c`。
范围：WinUI 生命周期、Workbench 命令/状态桥、PDF/识别流程、Platform 协议客户端、质量与发布入口。
本报告是重点路径审阅，不声称逐行覆盖全部源码，也不包含原工作区尚未提交的设计稿。

后续窗口适配与端到端验证已实施，包含真实打包与 release smoke；见
[布局验证记录](e2e-layout-validation.md)。下文验证记录保留首批审阅当时的范围。

## 当前阶段

结论：具备正式交付和受控使用基础，尚不足以认定全部工作流达到稳定日用阶段。

- [v0.5.1](https://github.com/FelixJI/vibeocr-next/releases/tag/v0.5.1) 于 2026-09-02 发布。
- 同一基线提交的 [CI 33600560357](https://github.com/FelixJI/vibeocr-next/actions/runs/33600560357)
  与 [CD 33601170704](https://github.com/FelixJI/vibeocr-next/actions/runs/33601170704) 均为 success。
- `.ci/project.json` 定义 Python/Web quality、Platform/App tests、release build/smoke；
  `scripts/release_smoke.py` 对解包产品执行 prerequisites 与 WebView2 smoke。
  这是基线交付证据，不代表本次工作树已通过远端门禁。
- 本次新增回归测试在基线上复现 PDF 取消/关闭仍 busy、删除后页面结果错位、负索引抛异常。
  因此 CI 绿不能推导出交互边界无缺陷。
- 本次没有开展真人 OCR 准确率、长文档内存峰值、断网首次安装、真实旧版本升级和长期运行验收；
  不据此宣称生产稳定或可无条件替代 Classic。

## 结构判断

现有 WinUI → App 功能层 → Platform → 独立 Backend 的分工合理；
WebAssets 通过 typed Workbench bridge 工作，`InferenceJobRunner` 已集中提交/观察/结果映射。
保留这些接口，比重写技术栈更有价值。

主要维护成本集中在两个多职责入口：`WebAssets/src/features/Pages.tsx` 同时承载多页面，
`Workbench/DesktopWorkbenchCommandHandler.cs` 同时负责命令分发、状态投影、资源发布及后台任务。
问题在于一个功能改动需要理解多条生命周期，不以文件行数本身判定必须拆分。

## 措施与实施状态

| 优先级 | 问题与证据 | 措施及验收 | 状态 |
| --- | --- | --- | --- |
| P1 | `PdfViewModel.CancelActiveRun` 推进 generation 后，旧 finally 无法清除 busy；提前 Dispose 可能影响尚未返回的任务 | 取消立即清理本地状态；原任务拥有 CTS 释放；统一 `FinishRun`；晚到结果不得覆盖新会话 | 本次已实现 |
| P1 | `DeletePagesAsync` 只更新 PageCount，旧 Pages/OCR 文本仍按删除前索引存在 | 删除成功后重建连续索引，保留存活页 OCR 结果并修正选择；新会话不接收旧删除结果 | 本次已实现 |
| P1 | `StartOcrAsync` 在 try 前访问负页号，会抛异常并留下 busy | 在启动前验证选择，去重，渲染返回后检查取消；未完成页不永久保持 Processing | 本次已实现 |
| P2 | 保存/修改操作未加入 active-run，取消语义不一致 | 纳入相同取消与收尾流程 | 本次已实现 |
| P2 | AGENTS 的禁止 SDK 前滚、固定六资产描述与 global.json/release_smoke 不符 | 按可执行配置修正文档，不修改 SDK/依赖/发布实现 | 本次已实现 |
| P1 | `RecognitionStatusCode` 仅按 busy/result 映射 running/completed/ready；`PdfState` 仅按页数映射 open/empty，可能隐藏 ViewModel 的失败信息 | 下一批增加 typed 失败/取消状态，贯通 VM → bridge → UI；以 BackendUnavailable、输入失败和重试成功验证用户可见提示 | 待实施 |
| P1 | `CloseSession` 只清理本地状态；`IInferenceClient.ClosePdfSessionAsync` 未在此调用，打开新文档/旧打开晚到时存在远端 session 释放缺口 | 下一批建立异步 session 所有权；以 fake close 次数及真实 Backend 重复打开/关闭后的资源占用验证 | 待实施 |
| P2 | 多页面 JSX 和多功能命令 handler 聚集修改原因 | 在上述状态契约稳定后逐功能迁移，保留 command/bridge 外部接口；按 recognition、PDF、settings 分批，每批运行相关 Web/App tests | 待实施 |
| P2 | `PdfSessionHttpClient` / `DeferredPdfSessionClient` / `IPdfSessionClient` 与实际 IInferenceClient PDF 路径并存；源码搜索未发现前两者的生产构造调用 | 确认外部消费者后删除未使用适配器，将共享 DTO 留在中性契约文件；通过 Platform/App 编译和测试证明无内部依赖 | 待实施 |

本批聚焦可复现的 PDF 状态错误和文档漂移；不把跨功能大拆分与行为修复混在一起，
也不引入通用任务框架或额外依赖。取消修改/保存是停止本地等待及发送取消信号，
不承诺撤销 Backend 已经完成的文件操作。

## 验证记录

- `dotnet restore tests/dotnet/VibeOCR.App.Tests/VibeOCR.App.Tests.csproj --locked-mode`：通过，复用本机缓存。
- `pwsh -File scripts/test_app_ci.ps1 -Filter FullyQualifiedName~PdfViewModelSupervisorTests`：
  修复前 7 项中 4 项失败；首轮修复后 7/7 通过。
- `pwsh -File scripts/test_app_ci.ps1`：203/203 通过（包含最终 9 项 PDF 测试，新增 6 项）。
- `dotnet restore tests/dotnet/VibeOCR.Platform.Tests/VibeOCR.Platform.Tests.csproj --locked-mode`
  及 `dotnet test tests/dotnet/VibeOCR.Platform.Tests/VibeOCR.Platform.Tests.csproj -c Release --no-restore --blame-hang --blame-hang-timeout 2m --blame-hang-dump-type none`：172/172 通过，无跳过。
- `uv run --no-sync python scripts/check_quality.py`：通过；Ruff check/format、108 项 Python 测试、
  Web format/lint/typecheck、14 项 legacy + 27 项 unit + 5 项 Playwright 视觉测试、生产构建与资源校验均完成。
- 两个修改的 C# 文件分别通过对应 csproj 的 `dotnet format whitespace --no-restore --include <file>` 格式化；
  命令报告 workspace 加载警告，后续真实 App/Platform 构建与测试均成功。`git diff --check` 通过。
- 本批未执行新候选 release build/smoke、未创建 PR 或发布；上述历史 CI/CD 仅适用于基线。
  日志保留在本任务工作区忽略目录 `build/audit/`，App 测试结果在 `.test-results/app/app-tests.trx`。
- 本机原 clone 未安装实际 Git hooks（仅 sample）；执行仓库质量入口替代未安装 hook 的验证。
- Python 按 README 的 `uv venv .venv` / `uv pip install --python .venv/Scripts/python.exe --group dev`
  创建环境，使用清华镜像；npm ci 使用本地缓存及 npmmirror，不改 lock 或仓库 registry。

## 稳定日用的下一道验收

1. 合入前通过严格同步 main 的 PR required；本批本地结果不能代替 PR 门禁。
2. 完成 P1 错误展示与 session 释放措施，所有失败/取消路径能恢复到可操作状态。
3. 在打包产品上记录：图片导入/截图 OCR 与复制导出；PDF 打开、OCR、删除、保存后重开核对；
   OCR 运行中关闭/换文档；断网运行时安装失败后恢复；相邻版本更新后 state 保留。
   每项记录版本、输入、预期与实际，失败保留诊断，不只记录“启动成功”。
4. 大 PDF/批处理明确输入规模与机器配置，记录耗时、峰值内存、取消后的资源回落；
   在有实际目标前不编造性能阈值。

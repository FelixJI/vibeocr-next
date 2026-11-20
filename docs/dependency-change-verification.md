# 依赖与闭包变更验证清单

例行依赖更新（uv lock / npm / NuGet）与 Runtime 闭包变更（`config/runtime/`）
的分级验证。原则：例行项由本地 quality + PR CI 兜底；真实计算路径冒烟按
变更面选跑，控制额外成本，不进 CI。

## 例行项（每次必跑）

```powershell
uv lock                      # 或 uv lock --upgrade；闭包变更另跑下面两个脚本
uv run python scripts/update_runtime_host_locks.py [--upgrade]
uv run python scripts/update_runtime_paddle_onnx_locks.py [--upgrade]
uv run --frozen python scripts/check_quality.py
```

- 三处声明一致性由 `tests/runtime/test_runtime_dependency_consistency.py`
  契约把关：改根 `pyproject.toml` 或 `config/runtime/shared/requirements-base.in`
  任一侧后必须同步另一侧，再重建闭包锁。
- `check_quality.py` 已含真实 MinerU SDK 合同（42 项，装配 venv 对真实包
  验证导入面/配置面/设备 env/api_server/真实 docx 解析）——MinerU 升级
  无需额外冒烟。
- WebAssets/NuGet 变更的例行验证见 `docs/source-reading-guide.md` 分层验证。

## 真实计算路径冒烟（按变更面选跑）

前置：先装配候选 `pwsh -File scripts/build-release.ps1 -Version <repository.json 版本>`；
所有脚本要求候选无 `state` 目录、WorkRoot 不嵌套在仓库内。

### 闭包 / 推理组件变更（paddleocr、paddlex、onnxruntime-directml、闭包锁）

真实产品树 + 真实 Supervisor + 真实 OCR 往返（合成图断言文本）：

```powershell
pwsh -File scripts/smoke_runtime_candidate.ps1 `
    -ProductRoot .release-build/VibeOCR `
    -WorkRoot <隔离目录>
```

### Paddle 识别线行为变更

最小模式（真实 UI + 真实识别作业，断言合成图 token）：

```powershell
pwsh -File scripts/smoke_paddle_modes.ps1 -Modes paddle_text -InputKinds file `
    -ProductRoot .release-build/VibeOCR -WorkRoot <隔离目录>
```

表格/结构/PDF 变更扩大到对应 `-Modes` / `-InputKinds`；PDF/导出测量走
`t2_pdf_baseline.ps1`。

### MinerU 版本升级

除 quality 内置合同外，关注 `scripts/prepare_mineru_sdk_tests.py` 的
`DIRECT_DEPENDENCIES`：新版本若新增 api_server 顶层导入链（如 4.2 的
`mineru-vl-utils`），装配 venv 缺包会直接在合同测试失败——补清单即可。

## 说明

- 上述冒烟均不接 CI（每 PR 全量跑真实推理成本过高）；依赖变更 PR 由
  提交者按清单执行并在 PR 正文记录结果。
- 本地联调入口（`scripts/dev_runtime.ps1`）见
  `docs/source-reading-guide.md` 的"本地联调"小节。

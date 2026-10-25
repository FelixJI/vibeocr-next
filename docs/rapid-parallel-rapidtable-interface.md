# RapidTable / RapidOCR 设备与并行：实现与实测说明

## 使用入口

单次识别、批量识别和 PDF 页面选择 RapidOCR 文字或 RapidTable 表格模式后，可在“运行设备”中选择自动、CPU 或 GPU（CUDA）。设备偏好按模式保存；CPU 批量并行由 Runtime 自动控制，无需配置线程数。使用 Rapid GPU 时，在运行环境设置中准备并选用“RapidOCR + RapidTable · NVIDIA CUDA”。

截图编辑工具栏按工具、属性和操作分组折行，背景高度随内容调整；圆角/直角由同一个按钮切换。识别文字和表格使用明确的操作按钮；Paddle 独立公式入口已退役，历史公式结果仍可读。

“文字”添加普通文字标注，“说明”添加带引线的说明；“选择”操作标注对象，“手形”平移画布；“重复匹配”查找与选定样本相似的重复区域。默认准备可选文字，不再单列“取字”工具：只使用已就绪的本地识别服务，不自动安装或启动环境。复杂排版中的副本和背景文字可在既有排版输出预览中选择。

## 接口

新识别模式 `rapid_table`：

* `pipeline_id="RAPID_TABLE"`（新 pipeline，`engine` 字段对其不合法——engine-only-OCR 不变量保留）；`engine=null`。
* `family="specialized"`、`provisioning="base_runtime"`、`lifecycle.kind="unmanaged"`（无 preload/ttl/pin/release）。
* `supported_options=("device",)`；`device` 枚举 `auto|cpu|cuda`，省略即 `auto`（经既有 pipeline option 合同校验，无新增 capability/目录字段）。
* OCR 管道同样新增 `device` 选项，仅 engine 缺省（默认 rapidocr）或显式 `rapidocr` 合法；`windows`/`paddleocr` 携带 `device` → `VALIDATION_ERROR`。
* 结果 payload 与 TABLE_RECOGNITION（paddle）同形：`ocr.v1` + `content_list[]` `type:"table"` 块（table.v1 网格、`table_body` HTML、markdown/html/raw projections、cell bbox 像素坐标）；`pipeline_type` 固定 `"TABLE_RECOGNITION"`，导出/编辑链路零改动。
* 提交样例：`"pipeline": {"pipeline_id": "RAPID_TABLE", "options_version": 1, "options": {"device": "auto"}}`；`paddle_table`（TABLE_RECOGNITION）不变。

## 设备语义（两引擎一致，fail closed）

* `cuda`：仅在 CUDA 真实就绪时放行——官方 CUDA EP 插件注册成功且 `get_ep_devices()` 可见 `CUDAExecutionProvider`；随后引擎按 CPU 构造，再对全部原生 ORT session `set_providers([('CUDAExecutionProvider', {'device_id': '0'}), 'CPUExecutionProvider'])` 并核实每个 session 实际 providers 以 CUDA 为首。设备探针不就绪时返回结构化错误（`OCR_ENGINE_UNAVAILABLE` / `device_provider_unavailable`；engine=`rapidocr`/`rapidtable`）；session 激活失败则抛出异常，交由既有任务恢复边界处理。失败不缓存降级实例，重试会重新构造并激活。绝不静默以 CPU 冒充 GPU（上游 rapid_table 在 provider 缺失时仅 warning 后退，本仓在构造前后双重拦截）。
* `auto`：就绪选 GPU，否则 CPU；`cpu`：强制 CPU。非法值由 parser enum 拒绝，引擎层第二道门同样 fail closed。
* 引擎 `capabilities()` 只推导保守批量能力、不抛错（executor 的 capabilities 调用点只容 AttributeError/TypeError）；确定性设备失败在 `recognize_many` 抛 `OcrEngineError`，经既有 executor fail-fast 边界映射为 per-item 协议错误码，不进入恢复重试。
* GPU 生产方案：CPU onnxruntime（版本不变）+ 官方 CUDA EP 插件固定 DLL（安装布局 `<runtime_root>/providers/cuda/onnxruntime_providers_cuda.dll`，env `VIBEOCR_ORT_CUDA_EP_DLL` 优先）+ nvidia CUDA13 固定运行库（`rapidocr-cuda` 配方/`win-x64-rapid-cu13` lock）；不使用 onnxruntime-gpu、不排除 CPU 发行物（rapid-table 硬依赖 CPU onnxruntime，`uv pip check` 不变）、不升级 ORT。关键加载次序：nvidia DLL 目录前缀进程 PATH + 保留 `add_dll_directory` 句柄 + `ctypes.WinDLL` 显式加载 + `register_execution_provider_library('CUDAExecutionProvider', dll)`（两参签名；`ep_name` 为设备属性名）。

## RapidOCR 批量有界并行

* RapidOCR `__call__` 内部 `update_params` 修改实例 cfg/postprocess，共享实例并行不安全 → CPU 多图任务经有界 `ThreadPoolExecutor`（worker `min(4, max(1, cpu//2))`），每 worker 线程私有独立实例/ORT session，总 intra_op 预算 ≤ min(8, cpu)。输出严格保序；单图异常使整批失败（等待在途 future 收尾后传播原始异常，不吞错）走既有恢复二分；`close/release_idle` 等待在途推理收尾后关闭池，不再复用（下次重建）。GPU 固定单实例（未实测多实例收益不开启）。
* 能力表达：`AdapterCapability(real_batch=False, parallel_compute=True, max_compute_batch=workers)`；BudgetPlanner 对 `parallel_compute` 按 workers 分组计算微批，`real_batch` 语义不变（绝不声称原生多输入 batch）。
* 实测（RapidOCR 3.9.2 / ORT 1.30，12 张 1000×700 英文票据，热身后两轮，总 intra 预算 8）：1×8 中位 5.0177s、2×4 4.0731s、4×2 3.6825s，输出文本与顺序一致；生产引擎路径 4 图并行结果一致保序。不宣称通用加速比。

## RapidTable 模型与准备

* `rapid-table` wheel 不含 onnx 模型（README『SLANETPLUS 随 wheel』已过时）；上游经 `default_models.yaml` 的 modelscope URL+SHA256（权威 checksum 契约）按需下载。
* 生产模型随 base pack 固定资产闭包携带并安装到 `<runtime_root>/models/rapidtable/slanet-plus.onnx`。运行时解析顺序：`VIBEOCR_RAPIDTABLE_MODEL` → 共享模型缓存 `rapidtable-3.0.2/modelscope/RapidAI/RapidTable/slanet-plus.onnx` → pack 固定路径（`VIBEOCR_RUNTIME_ROOT` 由受管启动设置）。皆缺时 mode availability=`preparation_required`、reason=`model_not_prepared`，提交 fail closed；识别期不下载、不写 site-packages。

## 实测结论

* RapidTable 3.0.2 CPU 与 CUDA（生产插件路径）均验证：合成 3×3 表格 → 9 单元格、HTML 文字正确、cell bbox 像素坐标齐全。
* 生产 adapter GPU 全链路（`RapidTableEngine`/`RapidOcrEngine` device=cuda，隔离 profile venv）验证通过：RapidTable 9 cells 正确、RapidOCR 两图输出一致（9 行）、cuda 能力为单实例非并行。
* Review 修复后，两引擎各运行 CPU/CUDA/auto 三组真实推理：RapidOCR 的三个会话、RapidTable 的结构与内嵌 OCR 共四个会话在首次推理前已创建，前后均保持所选主 EP。CUDA 会话仍可将部分形状算子放在 CPU，不代表全部算子均使用 GPU。图像输入按锁定上游契约转为连续 BGR，单元格框使用全部顶点的外接矩形。
* 在隔离 venv 完成 70 包 `pip sync`+`pip check` 与生产 `cuda_provider_ready` 探针、安装兼容回归 17 项通过。

## 协议增量审阅

本次为候选源码变更，尚未发布。相对不可变 `openapi-2.0.0.yaml`，保守兼容检查唯一报告的差异是 `POST /v2/jobs` 请求 `manifest.pipeline.pipeline_id` 增加 `RAPID_TABLE`。

该增量经实现审阅和独立审阅确认为内部 v2 的新增行为：旧请求值、参数含义及结果结构均保留；新客户端仅在 Runtime 模式目录声明 `rapid_table` 后提交该管道。同源桌面与 Runtime 一起构建，不要求旧 Runtime 接受新管道，也不改变 wire major。

历史 baseline、通用比较器和默认 CLI 严格行为不变。正式契约测试要求诊断结果恰好为上述单项，并检查请求枚举恰好等于旧集合加 `RAPID_TABLE`；任何额外差异、删除值或额外新值仍失败。这不是零差异兼容声明，也不把候选文件冒充已发布 baseline。

## 首轮实现验证（2026-10-23，工具栏紧凑化之前）

* Python 全量：3526 项通过；两项 Windows 临时目录重命名出现 `WinError 5`，原断言单独复查均通过。Ruff、生成一致性、OpenAPI 与协议 conformance 检查通过。
* Web：773 项单元测试通过；最后文字化改动的 24 项定向单元测试通过。浏览器全量 242 项通过、1 项预览解码失败，原测试复查通过；最终界面的 46 项截图、贴图、工作台与窄屏布局回归全部通过。未以更新截图基线或放宽断言消除失败。
* C#：Contracts 111 项、HTTP Client 24 项通过，新增模式、设备偏好回显及截图管道定向验证通过。App 全量仍有 6 项原生截图 UI 测试因 Windows 拒绝前台激活而未通过；不将此轮本地检查表述为完整 CI 门禁全绿。
* 已用真实 CPU 和 NVIDIA GPU 验证生产识别 adapter，实测范围与数据见上文。
* 最终 `pwsh -File scripts/build-release.ps1` 与 `uv run --frozen python scripts/release_smoke.py` 通过，包含真实 WebView 启动、Velopack Portable full 更新 E2E、精确资产集与包内 Runtime 验证。候选位于本地 `build/rapid-artifacts/`，不代表正式发布。
## Review 与工具栏整改验证（2026-10-23）

* Runtime 定向回归 58 项通过；几何与颜色回归在旧实现上失败。CPU/CUDA/auto 六组生产 adapter 推理通过。
* 完整质量入口的 Ruff、生成一致性、OpenAPI 与协议检查通过；Python 3539 passed、17 skipped、2 deselected，仅一项临时目录重命名遇到 Windows `WinError 5`。该原测试单独复查通过，完整入口未一次全绿。
* Web 全量格式、Lint、类型检查和 782 项单测通过。浏览器全量 247 passed、两项旧视觉基线失败；核对新旧界面、补齐默认文字层的测试模拟响应后，仅更新两张受影响基线，原测试复查通过。
* 独立审阅发现的对象选框重绘、初始裁剪手柄避让、慢上传后新修订漏准备均补充旧实现失败的回归。裁剪与标注手柄共用 12px 命中边界；最后对象边界增量 13 项单测、此前 2 项真实边界拖动和上述 2 项视觉测试通过；构建及 `verify_web_assets.py` 通过。
* C# 定向 12 项通过，覆盖自动文字层不准备运行环境、显式贴图取字保持准备能力及会话去重。原生 GUI smoke 本轮未重跑，完整打包与桌面 GUI 门禁以本次提交的 PR CI 为准。

## 未验证范围

* RapidTable 多 worker 并行（表格任务多为单图，单实例保序，不开启）。
* CUDA13 插件在非 NVIDIA/无驱动机器上的表现（预期 fail closed，未逐机实测）。

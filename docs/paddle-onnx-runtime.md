# Paddle 保留模式的 ONNX Runtime 路径

`paddle_text` 保留原产品模式、OCRResult、置信度和几何映射。文字路径通过 PaddleOCR 3.7.0 / PaddleX 3.7.2 的官方 `engine="onnxruntime"` 编排，模型不在用户机器转换。ORT 环境同时声明 `paddle_text`、`paddle_table` 和 `paddle_structure`，不回落旧 Paddle worker。结构模式移除专用公式、图表转表格和印章推理；普通公式/图表区域仍保留。

## 环境与设备

| 配方 | 唯一 ORT namespace 分发包 | 附加运行库 |
| --- | --- | --- |
| `paddleocr-ort-cpu` | `onnxruntime==1.30.0` | 无 |
| `paddleocr-ort-cuda` | `onnxruntime==1.30.0` | 与 Rapid 路径同一微软 CUDA13 EP v0.2.0 插件与七个固定 NVIDIA wheel |
| `paddleocr-ort-directml` | `onnxruntime-directml==1.24.4` | Windows DirectML，无 CUDA/Paddle/Torch |

三闭包保持 CPython 3.13 / win-x64，使用 `paddleocr==3.7.0`、`paddlex[ocr]==3.7.2`，不启用 `doc-parser` extra。`scripts/update_runtime_paddle_onnx_locks.py` 约束既有 Paddle CPU lock 中的共同包版本，生成三个 source-neutral lock；根 Python lock 和 Pillow 升级不属于此变更。manifest 将三锁绑定到同一个产品候选，安装仍经 staging、探针、固定路径交换及失败回滚。

Windows 的 `chardet` 原生扩展已在较长 Portable 路径下出现 DLL 导入失败；若提示文件名或扩展名太长，请缩短 Portable 目录后重新预览安装。

`device` 的省略/`auto` 跟随所选环境；显式 `cpu` 可在三闭包运行，`cuda`/`directml` 要求相应闭包。环境安装计划沿用 CPU/CUDA 的宿主依赖 profile；DirectML 的 `target_device` 和任务 `provider` 独立表示图形设备，不将 CPU profile 冒充实际 CPU 执行。DirectML 用 DXGI `EnumAdapters1` 硬件 ordinal；软件适配器排除，ordinal 不与 CUDA id 混用。CUDA 需要 R580 或更新驱动，运行库从固定 wheel 加载，不要求 Toolkit 或 PyTorch。

PaddleX 的 `device="cpu"` 仅选择其编排入口，`engine_config.providers` 与 `provider_options` 才决定 ORT 执行设备。每个启用 runner 都检查实际首 EP 并关闭 ORT 的运行失败自动 fallback。日志区分请求 EP、available EP、各 session 的实际 EP/选项与模型目录。GPU 执行证据使用真实 profile 节点；shape/control 的必要 CPU 分区单独保留，不能据此宣称整图 GPU 或将整图 CPU 标为 GPU。

CUDA 对所有启用模型统一设置官方 [`prefer_nhwc=1`](https://onnxruntime.ai/docs/execution-providers/CUDA-ExecutionProvider.html#prefer_nhwc)，由 ORT 插入等价布局转换，保持原始 FP32 权重、输入尺寸和批量。固定 ORT 1.30 / cuDNN 9.27 在 RTX 4090 的 300dpi 合成 PDF 上，small_rec 的 NCHW 1×7 depthwise 卷积首批六行曾触发 CUDA error 700；独立同张量复现后，NHWC 路径真实 CUDA 节点执行完成。原失败任务保留终态，不重放；CPU、显式 CPU 和 DirectML 配置不受该布局选项影响。

布局等价不表示浮点逐元素一致：同一六行张量与独立 CPU 比较，最大概率绝对差约 0.002355，原诊断 `rtol=1e-3, atol=1e-4` 的 allclose 未通过，CTC argmax 则全部一致。完整 PDF 两侧均为 17 行、方向 0；16 行文本一致，一行出现额外空格，最大归一化坐标差约 0.403（原图约 1px）、行置信度差约 0.0256。保留原 token、有效几何及 score 口径，不宣称输出完全相同或全文无误。

DirectML 禁用 memory pattern、使用 sequential execution，包装层持锁直到 predict generator 消费完毕。worker 的 stdio 请求继续串行。PaddleX 3.7.2 的 ONNX runner `close()` 为空，因此本地 owner 在 TTL、切档和退出时显式清除其全部 session 引用；不调用 Paddle allocator。原生崩溃、OOM 或 EP 失效终止当前任务，不进入通用 batch 重试；普通坏输入仍走既有隔离语义。已确认取消会停止自有 worker，下一新任务才可重建，绝不重放原任务。

## 模型、来源和完整性

| preset | det | rec |
| --- | --- | --- |
| `tiny` / 轻量 | PP-OCRv6_tiny_det | PP-OCRv6_tiny_rec |
| `small` / 均衡（默认） | PP-OCRv6_small_det | PP-OCRv6_small_rec |
| `medium` / 高精度 | PP-OCRv6_medium_det | PP-OCRv6_medium_rec |

模型名称用于官方 API；资源 repo 使用对应 `PaddlePaddle/<name>_onnx`。六主模型各包含 `inference.onnx`、`inference.yml`、`inference.json`。方向模型 PP-LCNet_x1_0_doc_ori、文本行方向 PP-LCNet_x0_25_textline_ori 和 UVDoc 各包含 ONNX/YAML，仅在该功能启用时准备。rec 字典内嵌各自 YAML；opset 为 det 14、rec 11、LCNet 7、UVDoc 16，模型 I/O 为 FP32。逐模型 IR/I/O 和外部权重声明保存在 `paddle_onnx_models.json`，当前清单十八个模型无外部权重文件。

`src/runtime/vibeocr/runtime/environments/paddle_onnx_models.json` 是审阅冻结的外部来源清单：Hugging Face 固定 repo commit，LFS 文件的权威摘要为官方树中的 SHA-256，非 LFS 文件使用官方 Git blob SHA-1，不能称为上游 SHA-256。ModelScope 的权威摘要和每文件 revision 来自官方文件 API；下载逐文件固定 revision，不把 master 当不可变 commit。消费者是既有 `model_cache.prepare_models`：文件不符合权威长度/摘要就不提交私有视图，并保留旧视图。这里校验的是外部模型下载字节，未给本地派生配置另造 checksum。

新模型使用 `paddlex-onnx-3.7.2` namespace。两源独立缓存，显式源失败不偷换到另一站；BOS 未有已冻结 ONNX 资产，明确拒绝并提示选官方 Hugging Face 或 ModelScope。全文件就绪后原子提交 `current.json`；完整缓存可离线复用，损坏或缺文件不得成为 ready。切档只准备所需 det/rec 与启用辅助模型，不重新 uv 安装、不拉取全部档位。

## 设置和任务快照

C# `PaddleModeOptions` 是权威设置，TS 只展示 Runtime `supported_options` 并提交 preset id/provider。保存与重启复用既有设置存储；截图、文件、批量与 PDF 都在原有提交冻结点复制选项。Runtime 在任务准入时另外冻结环境 id/revision、provider/device、模型组合/revision/文件清单与来源，进入内部 worker 请求；后来的设置/环境变化不会重写在途记录。构造缓存签名包含该快照与辅助开关，切换真实权重会释放旧 session 后重建。

## 验证口径

在新结果产生前冻结：同硬件、相同合成输入和相同模型档位比较旧 Paddle 与 ORT；模型预下载后的新进程首个完整 OCR 为冷耗时，同进程五次暖耗时保留原始样本并取 median。记录首失败、依赖 wheel 下载体积、安装体积和进程峰值内存；可可靠获取时另列 GPU 显存。没有产品速度 SLO，不承诺固定加速比，不在看到结果后调整门槛。沿用 install 60 分钟、mode 40 分钟硬超时，超时即失败。

主无辅助合成文本须完整返回 `VibeOCR 12345 TEST`。公开 smoke 保留 `paddle_smoke_fixtures.py` 的 `GOAL103|1120|2244`（既有 `Any`/`some`：命中任一配置 token）/ `1120` token 与 score、bbox/polygon、方向合同；8 页 `document_orientation.pdf` 按既有 `expected_rotate` 判断。启用 UVDoc 的简单合成行已观察到首字偏差，不能据此声称全文无误，辅助路径必须用既有文档 token 和有效几何另行验收。

候选 UI 复用 `scripts/smoke_paddle_modes.ps1 -Provider cpu|cuda|directml -ModelPreset tiny|small|medium`，ORT 阶段支持三个保留模式；`-InputKinds @('file','batch','pdf','pdf_text_layer')` 分别覆盖表格文件/批量、结构 PDF 和文字层入口。默认 `-Provider legacy` 保留历史五模式 smoke。最终仍须完整质量入口、真实候选 build/release_smoke、两源/离线/失败恢复与独立 R2 审阅，不以 import 或 provider 列表代替推理。

单次识别、批量和 PDF 的“本次模型档位”分别继承已保存默认，或临时选择 tiny/small/medium。临时选择不落盘，重启后恢复继承；PDF 沿每个文档已有 TaskEngine 范围隔离。截图与选字沿单次识别的有效设置。提交前冻结有效档位，批量整批沿同一快照；后续切换只影响新的提交。

仅当前有效模式为目录声明 `model_preset` 且 ready 的 `paddle_text` 时，自动选字才额外使用 ORT。它沿 recognition 的有效档位、设备和辅助开关提交独立 `local_models_only=true` 快照，不隐式安装或下载；本地组合不完整时显示 unavailable。选字缓存同时绑定这些选项、来源、服务实例与环境 id/revision；切换会取消旧准备并使旧层失效，迟到结果不能发布到新选择。其他高级模式与旧环境仍沿原轻量引擎限制。受管理 ORT 子进程禁用 PaddleX 导入时的多源连通性探测；实际下载仍由冻结清单的显式来源完成。


## 表格与文档结构的模型迁移

独立表格定位改用官方 `PP-DocLayout_plus-L`（20 类、800 输入），不是旧 `PP-DocLayout-L`（23 类、640 输入）的等价替换。结构模式本来使用 plus-L，并继续使用 `PP-DocBlockLayout`。有线结构保持 `SLANeXt_wired`；独立表格的无线结构依赖为 `SLANeXt_wireless`，PPStructure 为 `SLANet_plus`，不统一替换。表格分类、wired/wireless RT-DETR cell detector、表格方向和嵌套 OCR 均纳入完整准备与 session 验证；嵌套 OCR 的检测阈值为 .4，顶层 OCR 为 .6，不能共享实例。结构行方向使用 x1_0 模型，普通文字路径使用 x0_25。

默认无线结果采用官方 `use_wireless_table_cells_trans_to_html=True`、`use_e2e_wireless_table_rec_model=False`：RT-DETR cell 框和 OCR 生成可编辑网格。上游构造器仍强制创建无线结构 session，但这条默认路径不执行它；不能据其存在宣称模型参与结果。独立表格保留公开 E2E 选项，验收脚本的 `--wireless-e2e` 单独覆盖该路径。原生报告区分默认未消费构造依赖与要求实际目标 EP 节点的角色；条件触发的嵌套 OCR 需要旋转表格输入覆盖。

独立 table_v2 未透传布局 NMS 配置，适配在该 layout 实例启用官方 NMS/union。两模式均先运行原布局后处理；原结果已有 table 时整页保持。仅零 table 时从同一次模型预测提取 table 类，按 .1 阈值、官方 NMS 和仅 table 集合的 large 包含过滤补救，非 table 输出保持。该迁移适配可能改变低置信表格召回，需保留无表负例、混合页和独立多表验证；不能称为旧模型等价输出。

PaddleX 的 cell-to-HTML 使用固定 5px 聚类，去畸变后的边界漂移曾把真实 4 列拆成 5 列。ORT 适配先用单元格局部尺度约束的完整边界簇，再以真实相邻格的面对边证明必要合并；窄格两端不能同簇，中间真实格阻断相邻关系。复用 `TableModelV1` 的无重叠校验并要求完整网格覆盖。无法证明的缺格、冲突或过度漂移抛出 `PaddleOnnxError`，任务明确失败，不裁空列、不补造格子。只将私有边界索引坐标交给原 HTML 生成器，实际检测框、OCR 匹配与返回坐标保持原值。该方法是有界推断，不保证任意倾斜或不齐框都可恢复。

专用印章的构造、准备、预热和合法设置均退出结构模式。历史读取与直接导出在副本上过滤有明确 seal 类型/来源关联的结构块及专用裁图，并重建显示、复制、评分和导出投影；不删除数据库、原文件、混合记录或原始整页图片中的印章像素。无类型历史摘要不能可靠区分文字来源，保持原值；普通正文即使与印章同文也不按字符串猜测删除。

`verify_paddle_structure_native.py` 验证合并单元格、逐格关联、两栏/90°阅读序、混合区域及 HTML/XLSX/DOCX 的真实表格内容与合并语义；`wireless_dispatch.png` 的 5×5、25 格及数字位置合同来自独立预冻结样例。生成器、合同和模型版本随源码可复现，报告记录 source SHA、是否有 tracked diff、冷启动、五次 warm 原值/median、可靠峰值工作集和逐角色 profile。`--local-only` 同时阻断网络连接。质量或实际启用角色 EP 验证失败均不能作为通过。

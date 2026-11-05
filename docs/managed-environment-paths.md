# 旧识别设置与路径

产品从 `runtime/env/runtime-env/python.exe` 启动 Supervisor，Paddle、MinerU 和 PDF 各自使用该解释器启动独立 worker。桌面先校验产品 layout 和 runtime manifest 的绑定。启动错误会指向产品文件，恢复时请重新解压完整产品包。

首次计算时，Runtime 在状态根的 `recognition/supervisor-settings.json` 保存当前识别设置。存在旧 `environments.json` 时仅按 `active_id` 读取选中的记录。旧产品及自定义 venv 的缓存按 `environments/<id>/` 下的已知相对路径复用，历史 legacy 布局沿用状态根的已知缓存，Paddle ONNX 使用共享 `model-cache/`。已有设置在读取时保留原件。

旧 CPU 选择可直接迁入，旧 CUDA 或未知设备按自动选择。无旧配置时使用产品默认值。注册表损坏、路径越界或设置不可读时会提供错误信息并保留源文件。程序不会遍历、执行或删除旧环境。设置保存使用原子替换和版本比较，遇到并发更改时应重新读取后再保存。

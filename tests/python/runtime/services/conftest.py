"""services 目录收集门控：MinerU SDK 合同套件只在装配好的 SDK venv 中收集。

根环境（``uv sync`` 的 dev 环境）不安装 mineru SDK，直接收集
``mineru_sdk_contract`` 会在导入其 conftest 时失败；该套件由
``scripts/check_quality.py`` 调用 ``scripts/prepare_mineru_sdk_tests.py``
装配的专用 venv 以 ``VIBEOCR_MINERU_SDK_CONTRACTS=1`` 显式启用——缺包时
必须 fail 而不是 skip，因此这里不做任何静默降级。
"""

from __future__ import annotations

import os

collect_ignore_glob = (
    []
    if os.environ.get("VIBEOCR_MINERU_SDK_CONTRACTS") == "1"
    else ["mineru_sdk_contract*"]
)

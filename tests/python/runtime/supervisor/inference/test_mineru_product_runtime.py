"""#321 R3-2：产品模式下 MinerU 官方 SDK 缺损是产品损坏，不是待准备。

边界用例（在 a90 实现上失败）：
- 产品环境 + SDK 缺损 → tier/mode unavailable + product_runtime_damaged，
  不携带组件；实际请求 require_ready 以同一稳定 reason 拒绝。
- 开发环境 + SDK 缺损 → 待准备语义原样保留。
- 产品环境 + SDK 存在 + 模型/tier 未准备 → 待准备语义保留。
- 连接无效优先于产品损坏判定（原链不变）。
"""

from __future__ import annotations

import importlib

import pytest

readiness = importlib.import_module("vibeocr.runtime.recognition.mineru_readiness")


@pytest.fixture(name="sdk_installed")
def _sdk_installed(monkeypatch: pytest.MonkeyPatch):
    def install(value: bool):
        monkeypatch.setattr(readiness, "_mineru4_installed", lambda: value)

    return install


def _tier(tier_id: str = "basic") -> dict:
    return next(
        item for item in readiness.catalog_payload()["tiers"] if item["id"] == tier_id
    )


def test_product_sdk_damage_is_unavailable_with_stable_reason(
    monkeypatch: pytest.MonkeyPatch, sdk_installed
) -> None:
    monkeypatch.setenv("VIBEOCR_PRODUCT_RUNTIME", "1")
    sdk_installed(False)

    tier = _tier()
    assert tier["availability"] == "unavailable"
    assert tier["reason_code"] == "product_runtime_damaged"

    from vibeocr.runtime.recognition.mineru_config import MineruConfigError
    from vibeocr.runtime_contracts import ErrorCode, MineruTier

    with pytest.raises(MineruConfigError) as failure:
        readiness.require_ready(MineruTier.BASIC)
    assert failure.value.code is ErrorCode.MINERU_TIER_PREPARATION_REQUIRED
    assert failure.value.reason == "product_runtime_damaged"


def test_product_remote_sdk_damage_is_unavailable_too(
    monkeypatch: pytest.MonkeyPatch, sdk_installed
) -> None:
    """远程连接同样以官方 SDK 可导入为门禁：产品模式 SDK 缺损同判损坏。"""
    monkeypatch.setenv("VIBEOCR_PRODUCT_RUNTIME", "1")
    sdk_installed(False)
    monkeypatch.setattr(
        readiness,
        "_connection",
        readiness.MineruConnection(
            mode=readiness.CONNECTION_REMOTE,
            api_url="https://mineru4.example",
            api_key="k",
        ),
    )

    tier = _tier()
    assert tier["availability"] == "unavailable"
    assert tier["reason_code"] == "product_runtime_damaged"


def test_dev_sdk_damage_keeps_preparation_semantics(
    monkeypatch: pytest.MonkeyPatch, sdk_installed
) -> None:
    monkeypatch.delenv("VIBEOCR_PRODUCT_RUNTIME", raising=False)
    sdk_installed(False)

    tier = _tier()
    assert tier["availability"] == "preparation_required"
    assert tier["reason_code"] == "mineru_dependencies_required"


def test_product_missing_models_keep_preparation_semantics(
    monkeypatch: pytest.MonkeyPatch, sdk_installed
) -> None:
    # SDK 存在、tier 尚未实际准备（无观察记录）：不是软件损坏。
    monkeypatch.setenv("VIBEOCR_PRODUCT_RUNTIME", "1")
    sdk_installed(True)
    monkeypatch.setattr(readiness, "_observations", {})

    tier = _tier()
    assert tier["availability"] == "preparation_required"
    assert tier["reason_code"] == "mineru_execution_preparation_required"


def test_invalid_connection_takes_precedence_over_product_damage(
    monkeypatch: pytest.MonkeyPatch, sdk_installed
) -> None:
    monkeypatch.setenv("VIBEOCR_PRODUCT_RUNTIME", "1")
    sdk_installed(False)
    monkeypatch.setattr(
        readiness,
        "_connection",
        readiness.MineruConnection(mode=readiness.CONNECTION_UNAVAILABLE),
    )

    tier = _tier()
    assert tier["availability"] == "unavailable"
    assert tier["reason_code"] == "mineru_connection_invalid"


def test_product_mode_catalog_marks_mineru_document_damaged(
    monkeypatch: pytest.MonkeyPatch, sdk_installed
) -> None:
    monkeypatch.setenv("VIBEOCR_PRODUCT_RUNTIME", "1")
    sdk_installed(False)

    from vibeocr.runtime.host import composition
    from vibeocr.runtime.recognition.ocr_engines import (
        EngineAvailability,
        EngineDescriptor,
        OcrEngineRegistry,
        OcrEngineResolver,
    )
    from vibeocr.runtime_contracts.dtos import OcrEngine

    class _Engine:
        def __init__(self, engine_id):
            self.engine_id = engine_id

        def descriptor(self):
            return EngineDescriptor(
                engine_id=self.engine_id,
                availability=EngineAvailability.READY,
                included_in_base=True,
            )

    resolver = OcrEngineResolver(
        OcrEngineRegistry([_Engine(OcrEngine.WINDOWS), _Engine(OcrEngine.PADDLEOCR)])
    )
    modes = composition._build_recognition_mode_registry(
        engine_resolver=resolver,
        use_paddle=True,
        use_mineru=True,
    )

    entries = {item["id"]: item for item in modes.catalog_payload()["modes"]}
    mineru = entries["mineru_document"]
    assert mineru["availability"] == "unavailable"
    assert mineru["reason_code"] == "product_runtime_damaged"
    assert mineru["required_component"] is None


def test_dev_mode_catalog_keeps_mineru_preparation(
    monkeypatch: pytest.MonkeyPatch, sdk_installed
) -> None:
    monkeypatch.delenv("VIBEOCR_PRODUCT_RUNTIME", raising=False)
    sdk_installed(False)

    from vibeocr.runtime.host import composition
    from vibeocr.runtime.recognition.ocr_engines import (
        EngineAvailability,
        EngineDescriptor,
        OcrEngineRegistry,
        OcrEngineResolver,
    )
    from vibeocr.runtime_contracts.dtos import OcrEngine

    class _Engine:
        def __init__(self, engine_id):
            self.engine_id = engine_id

        def descriptor(self):
            return EngineDescriptor(
                engine_id=self.engine_id,
                availability=EngineAvailability.READY,
                included_in_base=True,
            )

    resolver = OcrEngineResolver(
        OcrEngineRegistry([_Engine(OcrEngine.WINDOWS), _Engine(OcrEngine.PADDLEOCR)])
    )
    modes = composition._build_recognition_mode_registry(
        engine_resolver=resolver,
        use_paddle=True,
        use_mineru=True,
    )

    entries = {item["id"]: item for item in modes.catalog_payload()["modes"]}
    mineru = entries["mineru_document"]
    assert mineru["availability"] == "preparation_required"
    assert mineru["reason_code"] == "runtime_component_missing"

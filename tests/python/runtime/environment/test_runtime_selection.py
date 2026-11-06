from __future__ import annotations

import pytest
from vibeocr.runtime.environments.runtime_manifest import ManifestError
from vibeocr.runtime.environments.runtime_selection import (
    RuntimeSelectionError,
    default_download_sources,
    download_source_catalog_payload,
    expand_legacy_model_sources,
    normalize_download_source_ids,
)
from vibeocr.runtime_contracts import ErrorCode


def test_download_source_catalog_declares_default_and_unique_business_keys() -> None:
    catalog = download_source_catalog_payload()

    sources = catalog["sources"]
    assert sources, "catalog must declare at least one Backend default source"
    ids = [source["id"] for source in sources]
    assert len(set(ids)) == len(ids)
    # 依赖索引源已随在线安装退役：缺省 = 双引擎模型源（ModelScope）。
    assert [
        source["id"] for source in default_download_sources(include_model_sources=True)
    ] == [
        "paddleocr-modelscope",
        "mineru-modelscope",
    ]
    assert [source["id"] for source in default_download_sources()] == [
        "paddleocr-modelscope",
        "mineru-modelscope",
    ]
    # 历史共享 model_registry 条目保留在目录中供旧计划/意图解析（UI 隐藏）。
    assert {source["id"] for source in sources} == {
        "huggingface",
        "modelscope",
        "paddleocr-huggingface",
        "paddleocr-modelscope",
        "paddleocr-bos",
        "mineru-huggingface",
        "mineru-modelscope",
    }
    assert {source["kind"] for source in sources} == {
        "model_registry",
        "paddleocr_model_registry",
        "mineru_model_registry",
    }
    for source in sources:
        assert set(source) == {"kind", "id", "endpoint"}
        assert source["endpoint"].startswith("https://")


def test_download_source_catalog_rejects_duplicate_ids_and_unknown_model_sources() -> (
    None
):
    duplicate_id = [
        {"kind": "model_registry", "id": "modelscope", "endpoint": "https://a"},
        {"kind": "model_registry", "id": "modelscope", "endpoint": "https://b"},
    ]
    with pytest.raises(RuntimeSelectionError) as excinfo:
        download_source_catalog_payload(duplicate_id)
    assert excinfo.value.code is ErrorCode.VALIDATION_ERROR

    unsupported_model_source = [
        {
            "kind": "model_registry",
            "id": "unknown-registry",
            "endpoint": "https://example.invalid",
        }
    ]
    with pytest.raises(RuntimeSelectionError) as excinfo:
        download_source_catalog_payload(unsupported_model_source)
    assert excinfo.value.code is ErrorCode.VALIDATION_ERROR

    # package_index kind 已随在线依赖安装退役：目录条目 fail closed。
    retired_kind = [
        {"kind": "package_index", "id": "pypi", "endpoint": "https://pypi.org/simple"}
    ]
    with pytest.raises(RuntimeSelectionError) as excinfo:
        download_source_catalog_payload(retired_kind)
    assert excinfo.value.code is ErrorCode.VALIDATION_ERROR

    same_kind_candidates = [
        {
            "kind": "paddleocr_model_registry",
            "id": "paddleocr-modelscope",
            "endpoint": "https://a",
        },
        {
            "kind": "paddleocr_model_registry",
            "id": "paddleocr-huggingface",
            "endpoint": "https://b",
        },
    ]
    assert download_source_catalog_payload(same_kind_candidates) == {
        "sources": same_kind_candidates
    }


def test_component_ids_have_one_meaning_and_legacy_selection_migrates() -> None:
    from vibeocr.runtime.environments.runtime_manifest import (
        migrate_legacy_component_ids,
        runtime_component_binding,
    )

    assert runtime_component_binding("win-x64-base", "runtime_host").import_name == (
        "fastapi"
    )
    for profile in ("win-x64-base", "win-x64-cpu"):
        for retired in ("rapidocr-base", "paddleocr-cpu", "paddleocr-cuda"):
            with pytest.raises(ManifestError, match="binding is missing"):
                runtime_component_binding(profile, retired)
        assert (
            migrate_legacy_component_ids(profile, ("ocr_engine", "rapidocr-base")) == ()
        )
    # 历史档位绑定仅保留读取基准：mineru-cpu 仍按旧 profile 解析到包名。
    assert runtime_component_binding("win-x64-cpu", "mineru-cpu").import_name == (
        "mineru"
    )
    with pytest.raises(ManifestError, match="binding is missing"):
        runtime_component_binding("win-x64-base", "ocr_engine")

    # 统一推理闭包：document_parsing 及其目标组件（mineru-cpu/mineru-cuda）、
    # gpu_runtime 与 2.8 前的功能域组件在所有 profile 下统一折叠为空
    # （MinerU 随 managed paddleocr-ort-directml 环境携带）。
    for profile in ("win-x64-base", "win-x64-cpu"):
        assert (
            migrate_legacy_component_ids(
                profile,
                (
                    "document_parsing",
                    "mineru-cpu",
                    "mineru-cuda",
                    "gpu_runtime",
                    "pdf_document_tools",
                    "image_code_tools",
                ),
            )
            == ()
        )
    assert (
        migrate_legacy_component_ids("win-x64-cpu", ("ocr_engine", "document_parsing"))
        == ()
    )


def test_normalize_download_source_ids_resolves_omission_to_backend_default() -> None:
    model_defaults = ("paddleocr-modelscope", "mineru-modelscope")
    assert normalize_download_source_ids(None) == model_defaults
    assert normalize_download_source_ids(()) == model_defaults
    # 历史 package index id（tuna-pypi/pypi）静默忽略：纯历史请求回默认。
    assert normalize_download_source_ids(["pypi"]) == model_defaults
    assert normalize_download_source_ids(["tuna-pypi", "mineru-modelscope"]) == (
        "mineru-modelscope",
    )


def test_normalize_download_source_ids_fails_closed_on_unknown_or_conflict() -> None:
    with pytest.raises(RuntimeSelectionError) as excinfo:
        normalize_download_source_ids(["tsinghua-mirror"])
    assert excinfo.value.code is ErrorCode.DOWNLOAD_SOURCE_UNKNOWN

    with pytest.raises(RuntimeSelectionError) as excinfo:
        normalize_download_source_ids(["paddleocr-modelscope", "paddleocr-huggingface"])
    assert excinfo.value.code is ErrorCode.VALIDATION_ERROR


def test_legacy_sources_do_not_lose_valid_preference_to_unknown_engine_id() -> None:
    assert expand_legacy_model_sources(("modelscope", "paddleocr-future")) == (
        "paddleocr-future",
        "paddleocr-modelscope",
        "mineru-modelscope",
    )
    assert expand_legacy_model_sources(("modelscope", "paddleocr-bos")) == (
        "paddleocr-bos",
        "mineru-modelscope",
    )
    # 历史 package index id 在展开时被过滤。
    assert expand_legacy_model_sources(("tuna-pypi", "modelscope")) == (
        "paddleocr-modelscope",
        "mineru-modelscope",
    )

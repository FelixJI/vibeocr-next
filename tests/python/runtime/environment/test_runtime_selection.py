from __future__ import annotations

from pathlib import Path

import pytest
from vibeocr.runtime.environments.runtime_manifest import (
    ManifestError,
    RuntimeInstallScope,
    RuntimeProfile,
    default_profile_components,
)
from vibeocr.runtime.environments.runtime_selection import (
    RuntimeSelectionError,
    RuntimeSelectionPolicy,
    component_variant_catalog_payload,
    default_download_sources,
    download_source_catalog_payload,
    expand_legacy_model_sources,
    normalize_download_source_ids,
    normalize_install_component_ids,
    selectable_component_ids,
    selectable_component_ids_across_catalog,
)
from vibeocr.runtime_contracts import ErrorCode


def _profile(name: str, *scopes: RuntimeInstallScope) -> RuntimeProfile:
    return RuntimeProfile(
        name=name,
        lock_path=Path(f"{name}.lock"),
        sha256="0" * 64,
        runtime_pack=(),
        components=default_profile_components(name),
        scopes=scopes,
    )


def _scope(scope_id: str, component_ids: tuple[str, ...]) -> RuntimeInstallScope:
    return RuntimeInstallScope(
        scope_id=scope_id,
        component_ids=component_ids,
        lock_path=Path(f"{scope_id}.lock"),
        sha256="0" * 64,
        runtime_pack=(),
        runtime_pack_sha256=(),
    )


def _selection_profiles() -> dict[str, RuntimeProfile]:
    base_ids = tuple(
        component.component_id
        for component in default_profile_components("win-x64-base")
    )
    cpu_ids = tuple(
        component.component_id
        for component in default_profile_components("win-x64-cpu")
    )
    cuda_ids = tuple(
        component.component_id
        for component in default_profile_components("win-x64-cu126")
    )
    return {
        "win-x64-base": _profile("win-x64-base", _scope("default", base_ids)),
        "win-x64-cpu": _profile(
            "win-x64-cpu",
            _scope("default", cpu_ids),
            _scope("paddle", (*base_ids, "paddleocr-cpu")),
            _scope("mineru", (*base_ids, "mineru-cpu")),
        ),
        "win-x64-cu126": _profile(
            "win-x64-cu126",
            _scope("default", cuda_ids),
            _scope("gpu-runtime", (*base_ids, "gpu_runtime")),
            _scope("paddle", (*base_ids, "paddleocr-cuda")),
            _scope("mineru", (*base_ids, "mineru-cuda", "gpu_runtime")),
            _scope("paddle-gpu-runtime", (*base_ids, "paddleocr-cuda", "gpu_runtime")),
        ),
    }


def _selection_policy() -> RuntimeSelectionPolicy:
    return RuntimeSelectionPolicy(profiles=_selection_profiles())


def test_download_source_catalog_declares_default_and_unique_business_keys() -> None:
    catalog = download_source_catalog_payload()

    sources = catalog["sources"]
    assert sources, "catalog must declare at least one Backend default source"
    ids = [source["id"] for source in sources]
    assert len(set(ids)) == len(ids)
    assert [source["id"] for source in default_download_sources()] == ["tuna-pypi"]
    # 默认：依赖包 TUNA，模型 ModelScope（仅代表安装依赖与下载模型）。
    assert [
        source["id"] for source in default_download_sources(include_model_sources=True)
    ] == [
        "tuna-pypi",
        "paddleocr-modelscope",
        "mineru-modelscope",
    ]
    # 历史共享 model_registry 条目保留在目录中供旧计划/意图解析（UI 隐藏）。
    assert {source["id"] for source in sources} == {
        "tuna-pypi",
        "pypi",
        "huggingface",
        "modelscope",
        "paddleocr-huggingface",
        "paddleocr-modelscope",
        "paddleocr-bos",
        "mineru-huggingface",
        "mineru-modelscope",
    }
    assert {source["kind"] for source in sources} == {
        "package_index",
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
        {"kind": "package_index", "id": "pypi", "endpoint": "https://a"},
        {"kind": "package_index", "id": "pypi", "endpoint": "https://b"},
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

    same_kind_candidates = [
        {"kind": "package_index", "id": "pypi", "endpoint": "https://a"},
        {"kind": "package_index", "id": "mirror", "endpoint": "https://b"},
    ]
    assert download_source_catalog_payload(same_kind_candidates) == {
        "sources": same_kind_candidates
    }


def test_component_variant_catalog_lists_only_selectable_full_components() -> None:
    catalog = component_variant_catalog_payload()

    variants = catalog["variants"]
    assert variants, "catalog must list the optional full-closure components"
    business_keys = {(v["feature_id"], v["accelerator"]) for v in variants}
    assert len(business_keys) == len(variants)
    component_ids_by_accelerator: dict[str, set[str]] = {}
    for variant in variants:
        assert set(variant) == {"feature_id", "accelerator", "component_id"}
        assert variant["accelerator"] in {"cpu", "nvidia_cuda"}
        # 同一 accelerator 内 component_id 不得重复承载两个 feature。
        per_accelerator = component_ids_by_accelerator.setdefault(
            variant["accelerator"], set()
        )
        assert variant["component_id"] not in per_accelerator
        per_accelerator.add(variant["component_id"])
    # base 必备组件不进入可选目录；#270 后退役原生 Paddle 组件同样不进入
    # 新目录（身份仅在 PROFILE_COMPONENTS/迁移表中保留）。
    listed = {variant["component_id"] for variant in variants}
    assert listed == {
        "mineru-cpu",
        "mineru-cuda",
        "gpu_runtime",
    }
    assert selectable_component_ids("cpu") == ("mineru-cpu",)
    assert set(selectable_component_ids("nvidia_cuda")) == {
        "mineru-cuda",
        "gpu_runtime",
    }


def test_component_ids_have_one_meaning_and_legacy_selection_migrates() -> None:
    from vibeocr.runtime.environments.runtime_manifest import (
        migrate_legacy_component_ids,
        runtime_component_binding,
    )

    assert runtime_component_binding("win-x64-base", "runtime_host").import_name == (
        "fastapi"
    )
    for profile in ("win-x64-base", "win-x64-cpu", "win-x64-cu126"):
        for retired in ("rapidocr-base", "paddleocr-cpu", "paddleocr-cuda"):
            with pytest.raises(ManifestError, match="binding is missing"):
                runtime_component_binding(profile, retired)
        assert (
            migrate_legacy_component_ids(profile, ("ocr_engine", "rapidocr-base")) == ()
        )
    assert runtime_component_binding("win-x64-cpu", "mineru-cpu").import_name == (
        "mineru"
    )
    with pytest.raises(ManifestError, match="binding is missing"):
        runtime_component_binding("win-x64-base", "ocr_engine")

    assert migrate_legacy_component_ids(
        "win-x64-cpu", ("ocr_engine", "document_parsing")
    ) == ("mineru-cpu",)
    assert migrate_legacy_component_ids("win-x64-cu126", ("document_parsing",)) == (
        "mineru-cuda",
    )


def test_selection_policy_resolves_exact_cuda_dependency_closure() -> None:
    policy = _selection_policy()

    gpu_only = policy.plan_start(
        accelerator="nvidia_cuda",
        install_component_ids=("gpu_runtime",),
        download_source_ids=("pypi",),
    )
    document_parsing = policy.plan_start(
        accelerator="nvidia_cuda",
        install_component_ids=("document_parsing",),
        download_source_ids=("pypi",),
    )

    assert gpu_only.requested_component_ids == ("gpu_runtime",)
    assert gpu_only.install_scope.scope_id == "gpu-runtime"
    assert gpu_only.effective_component_ids == (
        "runtime_host",
        "gpu_runtime",
    )
    assert document_parsing.install_scope.scope_id == "default"
    assert document_parsing.effective_component_ids == (
        "runtime_host",
        "mineru-cuda",
        "gpu_runtime",
    )


def test_selection_policy_canonicalizes_sources_and_rejects_same_kind() -> None:
    policy = RuntimeSelectionPolicy(
        profiles=_selection_profiles(),
        sources=(
            {
                "kind": "package_index",
                "id": "pypi",
                "endpoint": "https://pypi.org/simple",
            },
            {
                "kind": "package_index",
                "id": "mirror",
                "endpoint": "https://mirror.invalid/simple",
            },
            {
                "kind": "model_registry",
                "id": "huggingface",
                "endpoint": "https://huggingface.co",
            },
            {
                "kind": "model_registry",
                "id": "modelscope",
                "endpoint": "https://www.modelscope.cn",
            },
        ),
        default_download_source_ids=("pypi",),
    )

    resolved = policy.plan_start(
        accelerator="cpu",
        install_component_ids=None,
        download_source_ids=("pypi", "huggingface"),
    )
    assert resolved.requested_download_source_ids == ("pypi", "huggingface")
    assert resolved.effective_download_source_ids == ("pypi", "huggingface")

    with pytest.raises(RuntimeSelectionError) as excinfo:
        policy.plan_start(
            accelerator="cpu",
            install_component_ids=None,
            download_source_ids=("pypi", "huggingface", "modelscope"),
        )
    assert excinfo.value.code is ErrorCode.VALIDATION_ERROR


def test_selection_policy_overlays_explicit_sources_by_kind() -> None:
    policy = RuntimeSelectionPolicy(
        profiles=_selection_profiles(),
        sources=(
            {
                "kind": "package_index",
                "id": "tuna-pypi",
                "endpoint": "https://mirrors.tuna.tsinghua.edu.cn/pypi/web/simple/",
            },
            {
                "kind": "package_index",
                "id": "pypi",
                "endpoint": "https://pypi.org/simple",
            },
            {
                "kind": "model_registry",
                "id": "huggingface",
                "endpoint": "https://huggingface.co",
            },
            {
                "kind": "model_registry",
                "id": "modelscope",
                "endpoint": "https://www.modelscope.cn",
            },
        ),
        default_download_source_ids=("tuna-pypi", "modelscope"),
    )

    model_override = policy.plan_start(
        accelerator="cpu",
        install_component_ids=None,
        download_source_ids=("huggingface",),
    )
    package_override = policy.plan_start(
        accelerator="cpu",
        install_component_ids=None,
        download_source_ids=("pypi",),
    )

    assert model_override.requested_download_source_ids == ("huggingface",)
    assert model_override.effective_download_source_ids == (
        "tuna-pypi",
        "huggingface",
    )
    assert package_override.requested_download_source_ids == ("pypi",)
    assert package_override.effective_download_source_ids == ("pypi", "modelscope")


def test_normalize_download_source_ids_resolves_omission_to_backend_default() -> None:
    assert normalize_download_source_ids(None) == ("tuna-pypi",)
    assert normalize_download_source_ids(()) == ("tuna-pypi",)
    assert normalize_download_source_ids(["pypi"]) == ("pypi",)


def test_normalize_download_source_ids_fails_closed_on_unknown_or_conflict() -> None:
    with pytest.raises(RuntimeSelectionError) as excinfo:
        normalize_download_source_ids(["tsinghua-mirror"])
    assert excinfo.value.code is ErrorCode.DOWNLOAD_SOURCE_UNKNOWN

    with pytest.raises(RuntimeSelectionError) as excinfo:
        normalize_download_source_ids(["pypi", "pypi"])
    assert excinfo.value.code is ErrorCode.VALIDATION_ERROR


def test_normalize_install_component_ids_distinguishes_omission_and_empty() -> None:
    # None（省略）= Backend 缺省完整闭包；[]（显式空集）= base-only。
    assert normalize_install_component_ids(None, accelerator="cpu") is None
    assert normalize_install_component_ids((), accelerator="cpu") == ()
    assert normalize_install_component_ids((), accelerator="base") == ()
    assert normalize_install_component_ids(["document_parsing"], accelerator="cpu") == (
        "mineru-cpu",
    )
    assert normalize_install_component_ids(["ocr_engine"], accelerator="base") == ()


def test_normalize_install_component_ids_rejects_retired_native_paddle() -> None:
    """#270：显式旧组件请求（含混合）可诊断拒绝，不静默降级为 base 成功。"""
    for accelerator, retired in (
        ("cpu", "paddleocr-cpu"),
        ("nvidia_cuda", "paddleocr-cuda"),
    ):
        with pytest.raises(RuntimeSelectionError, match="retired") as excinfo:
            normalize_install_component_ids([retired], accelerator=accelerator)
        assert excinfo.value.code is ErrorCode.VALIDATION_ERROR
    with pytest.raises(RuntimeSelectionError, match="retired"):
        normalize_install_component_ids(
            ["paddleocr-cpu", "mineru-cpu"], accelerator="cpu"
        )


def test_normalize_install_component_ids_fails_closed() -> None:
    with pytest.raises(RuntimeSelectionError) as excinfo:
        normalize_install_component_ids(["not-a-component"], accelerator="cpu")
    assert excinfo.value.code is ErrorCode.RUNTIME_COMPONENT_UNKNOWN

    # gpu_runtime 在目录中，但只能装进 nvidia_cuda 档位：选择不得隐式换档。
    with pytest.raises(RuntimeSelectionError) as excinfo:
        normalize_install_component_ids(["gpu_runtime"], accelerator="cpu")
    assert excinfo.value.code is ErrorCode.VALIDATION_ERROR

    with pytest.raises(RuntimeSelectionError) as excinfo:
        normalize_install_component_ids(["document_parsing"], accelerator="base")
    assert excinfo.value.code is ErrorCode.VALIDATION_ERROR


@pytest.mark.parametrize(
    "accelerator,suffix", [("cpu", "cpu"), ("nvidia_cuda", "cuda")]
)
def test_paddle_selection_never_adds_mineru_or_torch(accelerator, suffix):
    """#270：显式退役原生 Paddle 请求被明确拒绝，不安装也不降级成 base 成功。"""
    with pytest.raises(RuntimeSelectionError, match="retired"):
        _selection_policy().plan_start(
            accelerator=accelerator,
            install_component_ids=(f"paddleocr-{suffix}",),
            download_source_ids=None,
        )


def test_retired_native_paddle_excluded_from_catalog_request_and_default_closure():
    """#270 回归：新目录/缺省闭包排除原生 Paddle；显式旧请求无副作用拒绝。

    旧实现（目录/默认闭包含 paddleocr-*、请求静默装 paddle）上失败。
    """
    policy = _selection_policy()
    assert "paddleocr-cpu" not in selectable_component_ids_across_catalog()
    assert "paddleocr-cuda" not in selectable_component_ids_across_catalog()
    # 缺省（None）闭包 = base + MinerU（+ gpu_runtime），不含 paddleocr-*。
    default = policy.plan_start(
        accelerator="nvidia_cuda",
        install_component_ids=None,
        download_source_ids=None,
    )
    assert set(default.effective_component_ids) == {
        "runtime_host",
        "mineru-cuda",
        "gpu_runtime",
    }
    assert default.install_scope.scope_id == "default"
    # 旧基础 OCR 身份只折叠，不恢复已退役引擎；OCR 由托管 Paddle 环境提供。
    legacy = policy.plan_start(
        accelerator="cpu",
        install_component_ids=("ocr_engine", "rapidocr-base"),
        download_source_ids=None,
    )
    assert legacy.requested_component_ids == ()
    assert legacy.effective_component_ids == ("runtime_host",)
    assert (
        normalize_install_component_ids(
            ("ocr_engine", "rapidocr-base"), accelerator="cpu"
        )
        == ()
    )
    # 混合请求（退役 + 合法组件）整体拒绝，不部分安装。
    with pytest.raises(RuntimeSelectionError, match="retired"):
        policy.plan_start(
            accelerator="cpu",
            install_component_ids=("paddleocr-cpu", "mineru-cpu"),
            download_source_ids=None,
        )


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

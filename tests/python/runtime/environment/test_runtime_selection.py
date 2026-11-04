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
    return {
        "win-x64-base": _profile("win-x64-base", _scope("default", base_ids)),
        "win-x64-cpu": _profile(
            "win-x64-cpu",
            _scope("default", cpu_ids),
            _scope("paddle", (*base_ids, "paddleocr-cpu")),
            _scope("mineru", (*base_ids, "mineru-cpu")),
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


def test_component_variant_catalog_is_empty_after_unified_inference_closure() -> None:
    """统一推理闭包回归：组件档位退役后可选目录不再提供任何变体。

    旧实现（目录列出 mineru-cpu/mineru-cuda/gpu_runtime 供选择，但显式
    请求已被折叠为空——UI 展示静默无作用选项）上失败。
    """
    catalog = component_variant_catalog_payload()

    assert catalog["variants"] == []
    assert selectable_component_ids("cpu") == ()
    assert selectable_component_ids_across_catalog() == frozenset()


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


def test_selection_policy_rejects_retired_cuda_accelerator() -> None:
    """CUDA 档位已整体退役：nvidia_cuda 只作为历史记录值被读取归类，
    新的选择入口必须可诊断拒绝（不裸抛 KeyError、不静默按 base 规划）。
    """
    policy = _selection_policy()
    for accelerator in ("nvidia_cuda", "not-an-accelerator"):
        with pytest.raises(RuntimeSelectionError, match="unsupported accelerator"):
            policy.plan_start(
                accelerator=accelerator,
                install_component_ids=None,
                download_source_ids=None,
            )
        with pytest.raises(RuntimeSelectionError, match="unsupported accelerator"):
            normalize_install_component_ids(
                ("document_parsing",), accelerator=accelerator
            )


def test_selection_policy_folds_retired_cuda_closure_requests_to_base() -> None:
    """统一推理闭包：gpu_runtime/document_parsing 请求不再解析 CUDA 组件闭包。

    CUDA 依赖闭包（mineru-cuda + gpu_runtime）已随分设备配方退役：这些 id
    在选择入口折叠为空，解析结果收敛到 base-only 闭包；推理环境由 managed
    paddleocr-ort-directml 统一闭包承载，不再走组件安装路径。
    """
    policy = _selection_policy()

    for requested in (("gpu_runtime",), ("document_parsing",)):
        resolved = policy.plan_start(
            accelerator="cpu",
            install_component_ids=requested,
            download_source_ids=("paddleocr-modelscope",),
        )
        assert resolved.requested_component_ids == ()
        assert resolved.install_scope.scope_id == "default"
        assert resolved.effective_component_ids == ("runtime_host",)


def test_selection_policy_canonicalizes_sources_and_rejects_same_kind() -> None:
    policy = RuntimeSelectionPolicy(
        profiles=_selection_profiles(),
        sources=(
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
            {
                "kind": "paddleocr_model_registry",
                "id": "paddleocr-huggingface",
                "endpoint": "https://huggingface.co",
            },
            {
                "kind": "paddleocr_model_registry",
                "id": "paddleocr-modelscope",
                "endpoint": "https://www.modelscope.cn",
            },
        ),
        default_download_source_ids=("modelscope",),
    )

    resolved = policy.plan_start(
        accelerator="cpu",
        install_component_ids=None,
        download_source_ids=("modelscope", "paddleocr-huggingface"),
    )
    assert resolved.requested_download_source_ids == (
        "modelscope",
        "paddleocr-huggingface",
    )
    assert resolved.effective_download_source_ids == (
        "modelscope",
        "paddleocr-huggingface",
    )

    with pytest.raises(RuntimeSelectionError) as excinfo:
        policy.plan_start(
            accelerator="cpu",
            install_component_ids=None,
            download_source_ids=("modelscope", "huggingface", "paddleocr-modelscope"),
        )
    assert excinfo.value.code is ErrorCode.VALIDATION_ERROR


def test_selection_policy_ignores_legacy_package_index_ids() -> None:
    """历史 package index id 在读取层静默过滤：显式请求只按剩余模型源解析。"""
    policy = RuntimeSelectionPolicy(
        profiles=_selection_profiles(),
        sources=(
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
            {
                "kind": "paddleocr_model_registry",
                "id": "paddleocr-huggingface",
                "endpoint": "https://huggingface.co",
            },
            {
                "kind": "paddleocr_model_registry",
                "id": "paddleocr-modelscope",
                "endpoint": "https://www.modelscope.cn",
            },
        ),
        default_download_source_ids=("modelscope",),
    )

    resolved = policy.plan_start(
        accelerator="cpu",
        install_component_ids=None,
        download_source_ids=("paddleocr-modelscope", "pypi"),
    )
    assert resolved.requested_download_source_ids == ("paddleocr-modelscope",)
    # 历史 pypi 被过滤；默认共享 model_registry 偏好按 kind 叠加保留。
    assert resolved.effective_download_source_ids == (
        "modelscope",
        "paddleocr-modelscope",
    )


def test_selection_policy_retired_only_request_inherits_defaults() -> None:
    """仅含历史 package index 的请求等效未请求：继承默认来源而不是拒绝。

    与 normalize_download_source_ids 同语义（纯历史请求回默认），历史
    冻结计划只记录纯包源 effective 集合时同样回退；真正未知的 id 仍拒绝。
    """
    policy = RuntimeSelectionPolicy(
        profiles=_selection_profiles(),
        sources=(
            {
                "kind": "model_registry",
                "id": "modelscope",
                "endpoint": "https://www.modelscope.cn",
            },
            {
                "kind": "paddleocr_model_registry",
                "id": "paddleocr-modelscope",
                "endpoint": "https://www.modelscope.cn",
            },
        ),
        default_download_source_ids=("modelscope",),
    )
    retired_only = policy.plan_start(
        accelerator="cpu",
        install_component_ids=None,
        download_source_ids=("tuna-pypi", "pypi"),
    )
    inherited = policy.plan_start(
        accelerator="cpu",
        install_component_ids=None,
        download_source_ids=None,
    )
    assert retired_only.requested_download_source_ids is None
    assert retired_only.effective_download_source_ids == ("modelscope",)
    assert (
        retired_only.effective_download_source_ids
        == inherited.effective_download_source_ids
    )

    with pytest.raises(RuntimeSelectionError) as excinfo:
        policy.plan_start(
            accelerator="cpu",
            install_component_ids=None,
            download_source_ids=("tsinghua-mirror",),
        )
    assert excinfo.value.code is ErrorCode.DOWNLOAD_SOURCE_UNKNOWN


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
        normalize_download_source_ids(["modelscope", "modelscope"])
    assert excinfo.value.code is ErrorCode.VALIDATION_ERROR


def test_normalize_install_component_ids_distinguishes_omission_and_empty() -> None:
    # None（省略）= Backend 缺省完整闭包；[]（显式空集）= base-only。
    assert normalize_install_component_ids(None, accelerator="cpu") is None
    assert normalize_install_component_ids((), accelerator="cpu") == ()
    assert normalize_install_component_ids((), accelerator="base") == ()
    # 统一推理闭包：document_parsing 与 mineru/gpu_runtime 组件请求统一
    # 折叠为空（不拒绝、不安装），OCR/MinerU 由 managed 环境承载。
    assert (
        normalize_install_component_ids(["document_parsing"], accelerator="cpu") == ()
    )
    assert normalize_install_component_ids(["gpu_runtime"], accelerator="cpu") == ()
    assert normalize_install_component_ids(["ocr_engine"], accelerator="base") == ()


def test_normalize_install_component_ids_rejects_retired_native_paddle() -> None:
    """#270：显式旧组件请求（含混合）可诊断拒绝，不静默降级为 base 成功。"""
    for accelerator, retired in (
        ("cpu", "paddleocr-cpu"),
        ("base", "paddleocr-cuda"),
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

    # 可选组件闭包已随统一推理闭包退役折叠，“档位不匹配”分支不再可达；
    # 重复请求仍在迁移前 fail closed。
    with pytest.raises(RuntimeSelectionError) as excinfo:
        normalize_install_component_ids(["document_parsing"] * 2, accelerator="cpu")
    assert excinfo.value.code is ErrorCode.VALIDATION_ERROR


def test_paddle_selection_never_adds_mineru_or_torch():
    """#270：显式退役原生 Paddle 请求被明确拒绝，不安装也不降级成 base 成功。"""
    with pytest.raises(RuntimeSelectionError, match="retired"):
        _selection_policy().plan_start(
            accelerator="cpu",
            install_component_ids=("paddleocr-cpu",),
            download_source_ids=None,
        )


def test_retired_native_paddle_excluded_from_catalog_request_and_default_closure():
    """#270 回归：新目录/缺省闭包排除原生 Paddle；显式旧请求无副作用拒绝。

    旧实现（目录/默认闭包含 paddleocr-*、请求静默装 paddle）上失败。
    """
    policy = _selection_policy()
    assert "paddleocr-cpu" not in selectable_component_ids_across_catalog()
    assert "paddleocr-cuda" not in selectable_component_ids_across_catalog()
    # 统一推理闭包后缺省（None）闭包 = base 完整闭包，不含 paddleocr-*，
    # 也不含已退役的 MinerU/CUDA 组件档位。
    default = policy.plan_start(
        accelerator="cpu",
        install_component_ids=None,
        download_source_ids=None,
    )
    assert set(default.effective_component_ids) == {"runtime_host"}
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
    # 历史 package index id 在展开时被过滤。
    assert expand_legacy_model_sources(("tuna-pypi", "modelscope")) == (
        "paddleocr-modelscope",
        "mineru-modelscope",
    )

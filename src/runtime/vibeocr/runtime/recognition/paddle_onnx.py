"""Managed PaddleOCR ONNX models, request snapshots and owned sessions."""

from __future__ import annotations

import json
import logging
import os
import threading
from collections.abc import Callable
from contextlib import contextmanager
from contextvars import ContextVar
from importlib.resources import files
from pathlib import Path
from typing import Any

from vibeocr.runtime.environments.model_cache import (
    LocalModelsNotPrepared,
    ModelAsset,
    ModelFile,
    prepare_models,
)

logger = logging.getLogger(__name__)
RECIPES = ("paddleocr-ort-cpu", "paddleocr-ort-cuda", "paddleocr-ort-directml")
PROVIDERS = {
    "cpu": "CPUExecutionProvider",
    "cuda": "CUDAExecutionProvider",
    "directml": "DmlExecutionProvider",
}
PRESETS = ("tiny", "small", "medium")
_SNAPSHOT: ContextVar[dict | None] = ContextVar("paddle_onnx_snapshot", default=None)
_CANCELLED: ContextVar[Callable[[], bool]] = ContextVar(
    "paddle_onnx_cancelled", default=lambda: False
)


class PaddleOnnxError(RuntimeError):
    """Deterministic native/model failure; the submitted task must not replay."""


class PaddleOnnxCancelled(PaddleOnnxError):
    """The owning worker was stopped for an acknowledged task cancellation."""


def cancelled() -> bool:
    return _CANCELLED.get()()


def is_ort_environment() -> bool:
    return os.environ.get("VIBEOCR_MANAGED_ENVIRONMENT_RECIPE") in RECIPES


def model_catalog() -> dict:
    return json.loads(
        files("vibeocr.runtime.environments")
        .joinpath("paddle_onnx_models.json")
        .read_text(encoding="utf-8")
    )["models"]


def freeze_request(options: dict, source_ids: tuple[str, ...] = ()) -> dict:
    preset = options.get("model_preset", "small")
    if preset not in PRESETS:
        raise PaddleOnnxError("Unsupported Paddle ONNX model preset")
    recipe = os.environ.get("VIBEOCR_MANAGED_ENVIRONMENT_RECIPE", "")
    default_device = recipe.removeprefix("paddleocr-ort-")
    device = options.get("device", default_device)
    if device == "auto":
        device = default_device
    if recipe not in RECIPES or device not in {"cpu", default_device}:
        raise PaddleOnnxError("Provider requires its matching Paddle ONNX environment")
    source = next(
        (
            entry.removeprefix("paddleocr-")
            for entry in source_ids
            if entry.startswith("paddleocr-")
        ),
        os.environ.get("PADDLE_PDX_MODEL_SOURCE", "modelscope"),
    )
    if source not in {"huggingface", "modelscope"}:
        raise PaddleOnnxError(
            "Paddle ONNX requires official Hugging Face or ModelScope assets"
        )
    names = [f"PP-OCRv6_{preset}_det", f"PP-OCRv6_{preset}_rec"]
    for option, model, default in (
        ("use_doc_orientation_classify", "PP-LCNet_x1_0_doc_ori", True),
        ("use_doc_unwarping", "UVDoc", False),
        ("use_textline_orientation", "PP-LCNet_x0_25_textline_ori", False),
    ):
        if options.get(option, default):
            names.append(model)
    catalog = model_catalog()
    device_id = 0
    adapter = None
    if device == "directml":
        from vibeocr.runtime.recognition.directml_devices import hardware_adapters

        adapters = hardware_adapters()
        if not adapters:
            raise PaddleOnnxError("DirectML hardware adapter unavailable")
        adapter = adapters[0]
        device_id = adapter["device_id"]
    return {
        "environment_id": os.environ.get("VIBEOCR_MANAGED_ENVIRONMENT_ID", ""),
        "environment_revision": os.environ.get(
            "VIBEOCR_MANAGED_ENVIRONMENT_REVISION", ""
        ),
        "recipe": recipe,
        "model_preset": preset,
        "device": device,
        "provider": PROVIDERS[device],
        "device_id": device_id,
        "adapter": adapter,
        "source": source,
        "models": {name: catalog[name]["sources"][source] for name in names},
    }


@contextmanager
def request_snapshot(
    snapshot: dict | None, *, cancelled: Callable[[], bool] | None = None
):
    token = _SNAPSHOT.set(snapshot)
    cancellation = _CANCELLED.set(cancelled or _CANCELLED.get())
    try:
        yield
    finally:
        _SNAPSHOT.reset(token)
        _CANCELLED.reset(cancellation)


def current_snapshot(options: dict | None = None) -> dict:
    snapshot = _SNAPSHOT.get()
    return snapshot if snapshot is not None else freeze_request(options or {})


def constructor_kwargs(options: Any) -> dict:
    if not is_ort_environment():
        return {}
    values = {
        key: getattr(options, key, default)
        for key, default in (
            ("model_preset", "small"),
            ("device", "auto"),
            ("use_doc_orientation_classify", True),
            ("use_doc_unwarping", False),
            ("use_textline_orientation", False),
        )
    }
    snapshot = current_snapshot(values)
    return {
        "_onnx_request": json.dumps(snapshot, sort_keys=True),
        **{
            key: model in snapshot["models"]
            for key, model in (
                ("use_doc_orientation_classify", "PP-LCNet_x1_0_doc_ori"),
                ("use_doc_unwarping", "UVDoc"),
                ("use_textline_orientation", "PP-LCNet_x0_25_textline_ori"),
            )
        },
        "local_models_only": getattr(options, "local_models_only", False),
    }


def _unwrap(pipeline):
    while hasattr(pipeline, "_pipeline"):
        pipeline = pipeline._pipeline
    return pipeline


def owned_runners(pipeline) -> list:
    pipeline = _unwrap(pipeline.paddlex_pipeline)
    nodes = [
        getattr(pipeline, name, None)
        for name in ("text_det_model", "text_rec_model", "textline_orientation_model")
    ]
    preprocessor = getattr(pipeline, "doc_preprocessor_pipeline", None)
    if preprocessor is not None:
        preprocessor = _unwrap(preprocessor)
        nodes.extend(
            getattr(preprocessor, name, None)
            for name in ("doc_ori_classify_model", "doc_unwarping_model")
        )
    return [node.runner for node in nodes if node is not None]


class PaddleOnnxPipeline:
    def __init__(self, pipeline, snapshot: dict):
        self.pipeline = pipeline
        self.snapshot = snapshot
        self._lock = threading.RLock()
        self.sessions = []
        runners = owned_runners(pipeline)
        if len(runners) != len(snapshot["models"]):
            self.close()
            raise PaddleOnnxError("Paddle ONNX session/model count mismatch")
        for runner in runners:
            session = runner.session
            actual = session.get_providers()
            if not actual or actual[0] != snapshot["provider"]:
                self.close()
                raise PaddleOnnxError(f"Requested provider was not activated: {actual}")
            if snapshot["provider"] == "DmlExecutionProvider":
                options = session.get_session_options()
                import onnxruntime as ort

                if (
                    options.enable_mem_pattern
                    or options.execution_mode != ort.ExecutionMode.ORT_SEQUENTIAL
                ):
                    self.close()
                    raise PaddleOnnxError(
                        "DirectML session options require sequential execution without memory patterns"
                    )
            session.disable_fallback()
            self.sessions.append(
                {
                    "model": str(runner.model_dir),
                    "providers": actual,
                    "provider_options": session.get_provider_options(),
                }
            )
        logger.info(
            "Paddle ONNX request=%s available=%s sessions=%s",
            snapshot,
            __import__("onnxruntime").get_available_providers(),
            self.sessions,
        )

    def predict(self, *args, **kwargs):
        # DML disallows concurrent Run on a session; hold through generator consumption.
        with self._lock:
            if self.pipeline is None:
                raise PaddleOnnxError("Paddle ONNX session already released")
            try:
                yield from self.pipeline.predict(*args, **kwargs)
            except Exception as exc:
                if (
                    isinstance(exc, MemoryError)
                    or type(exc).__module__.startswith("onnxruntime")
                    and type(exc).__name__ != "InvalidArgument"
                ):
                    self.close()
                    raise PaddleOnnxError(str(exc)) from exc
                raise

    def close(self):
        with self._lock:
            pipeline, self.pipeline = self.pipeline, None
            if pipeline is not None:
                # PaddleX 3.7.2 ONNXRuntimeRunner.close() is empty; drop our session owners.
                for runner in owned_runners(pipeline):
                    runner.session = None
                pipeline.close()


def create_pipeline(request: str, *, local_models_only: bool = False, **flags):
    snapshot = json.loads(request)
    if any(
        snapshot[key] != os.environ.get(variable, "")
        for key, variable in (
            ("recipe", "VIBEOCR_MANAGED_ENVIRONMENT_RECIPE"),
            ("environment_id", "VIBEOCR_MANAGED_ENVIRONMENT_ID"),
            ("environment_revision", "VIBEOCR_MANAGED_ENVIRONMENT_REVISION"),
        )
    ):
        raise PaddleOnnxError("Paddle ONNX environment snapshot changed")
    for option, model in (
        ("use_doc_orientation_classify", "PP-LCNet_x1_0_doc_ori"),
        ("use_doc_unwarping", "UVDoc"),
        ("use_textline_orientation", "PP-LCNet_x0_25_textline_ori"),
    ):
        if flags.get(option, model in snapshot["models"]) != (
            model in snapshot["models"]
        ):
            raise PaddleOnnxError("Paddle ONNX auxiliary selection snapshot changed")
        flags[option] = model in snapshot["models"]
    catalog = model_catalog()
    source = snapshot["source"]
    assets = []
    for name, listing in snapshot["models"].items():
        if listing != catalog[name]["sources"][source]:
            raise PaddleOnnxError("Paddle ONNX model snapshot changed")
        assets.append(
            ModelAsset(
                "paddlex-onnx-3.7.2",
                source,
                catalog[name]["repo_id"],
                name,
                tuple(entry["path"] for entry in listing["files"]),
                listing["revision"],
                tuple(ModelFile(**entry) for entry in listing["files"]),
            )
        )
    private = (
        Path(os.environ["PADDLE_PDX_CACHE_HOME"])
        / "onnx"
        / snapshot["model_preset"]
        / source
    )
    try:
        prepared = prepare_models(assets, private, local_only=local_models_only)
    except LocalModelsNotPrepared:
        raise
    except Exception as exc:
        from vibeocr.runtime.environments.runtime_maintenance import safe_runtime_detail

        raise PaddleOnnxError(safe_runtime_detail(str(exc))) from exc
    import onnxruntime as ort

    if snapshot["device"] == "cuda":
        from vibeocr.runtime.recognition.rapid_devices import setup_cuda_plugin

        if not setup_cuda_plugin():
            raise PaddleOnnxError(
                "CUDA DLL/plugin unavailable; repair the CUDA environment"
            )
    provider = snapshot["provider"]
    if provider not in ort.get_available_providers():
        raise PaddleOnnxError(f"Requested provider unavailable: {provider}")
    engine_config = {
        "providers": [provider],
        "provider_options": [{"device_id": str(snapshot["device_id"])}]
        if snapshot["device"] != "cpu"
        else [{}],
    }
    if snapshot["device"] == "directml":
        engine_config.update(enable_mem_pattern=False, execution_mode="sequential")
    model_arguments = {}
    for name in snapshot["models"]:
        if name.endswith("_det"):
            prefix = "text_detection"
        elif name.endswith("_rec"):
            prefix = "text_recognition"
        else:
            prefix = {
                "PP-LCNet_x1_0_doc_ori": "doc_orientation_classify",
                "UVDoc": "doc_unwarping",
                "PP-LCNet_x0_25_textline_ori": "textline_orientation",
            }[name]
        model_arguments[prefix + "_model_name"] = name
        model_arguments[prefix + "_model_dir"] = str(prepared.root / name)
    os.environ["PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK"] = "True"
    from paddleocr import PaddleOCR

    try:
        pipeline = PaddleOCR(
            engine="onnxruntime",
            device="cpu",
            engine_config=engine_config,
            **model_arguments,
            **flags,
        )
        return PaddleOnnxPipeline(pipeline, snapshot)
    except PaddleOnnxError:
        raise
    except Exception as exc:
        raise PaddleOnnxError(str(exc)) from exc

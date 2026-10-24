"""Managed PaddleOCR ONNX models, request snapshots and owned sessions."""

from __future__ import annotations

import json
import logging
import os
import threading
from collections import Counter
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
PIPELINES = ("OCR", "TABLE_RECOGNITION", "PP-StructureV3")
_RETIRED = ("use_formula_recognition", "use_seal_recognition", "use_chart_recognition")
_CONSTRUCTION_LOCK = threading.RLock()
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


def freeze_request(
    options: dict, source_ids: tuple[str, ...] = (), *, pipeline_id: str = "OCR"
) -> dict:
    if pipeline_id not in PIPELINES:
        raise PaddleOnnxError("Unsupported Paddle ONNX pipeline")
    if any(options.get(key) for key in _RETIRED):
        raise PaddleOnnxError("Formula, seal and chart recognition are retired")
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
    selections = {
        "use_doc_orientation_classify": options.get(
            "use_doc_orientation_classify", True
        ),
        "use_doc_unwarping": options.get("use_doc_unwarping", False),
        "use_textline_orientation": options.get("use_textline_orientation", False),
        "use_table_recognition": options.get("use_table_recognition", True),
        "use_table_orientation_classify": options.get(
            "use_table_orientation_classify", True
        ),
    }
    names = [f"PP-OCRv6_{preset}_det", f"PP-OCRv6_{preset}_rec"]
    if selections["use_doc_orientation_classify"]:
        names.append("PP-LCNet_x1_0_doc_ori")
    if selections["use_doc_unwarping"]:
        names.append("UVDoc")
    if selections["use_textline_orientation"]:
        names.append(
            "PP-LCNet_x1_0_textline_ori"
            if pipeline_id == "PP-StructureV3"
            else "PP-LCNet_x0_25_textline_ori"
        )
    if pipeline_id != "OCR":
        names.append("PP-DocLayout_plus-L")
        if pipeline_id == "PP-StructureV3":
            names.append("PP-DocBlockLayout")
        if pipeline_id == "TABLE_RECOGNITION" or selections["use_table_recognition"]:
            names.extend(
                (
                    "PP-LCNet_x1_0_table_cls",
                    "SLANeXt_wired",
                    "SLANeXt_wireless"
                    if pipeline_id == "TABLE_RECOGNITION"
                    else "SLANet_plus",
                    "RT-DETR-L_wired_table_cell_det",
                    "RT-DETR-L_wireless_table_cell_det",
                )
            )
            if selections["use_table_orientation_classify"]:
                names.append("PP-LCNet_x1_0_doc_ori")
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
        "pipeline_id": pipeline_id,
        "selections": selections,
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
    if snapshot is not None:
        if options and snapshot.get("pipeline_id", "OCR") != options.get(
            "pipeline", "OCR"
        ):
            raise PaddleOnnxError("Paddle ONNX pipeline snapshot changed")
        return snapshot
    values = options or {}
    return freeze_request(values, pipeline_id=values.get("pipeline", "OCR"))


def constructor_kwargs(options: Any) -> dict:
    if not is_ort_environment():
        return {}
    values = {
        key: getattr(options, key, default)
        for key, default in (
            ("pipeline", "OCR"),
            ("use_table_recognition", True),
            ("use_table_orientation_classify", True),
            ("model_preset", "small"),
            ("device", "auto"),
            ("use_doc_orientation_classify", True),
            ("use_doc_unwarping", False),
            ("use_textline_orientation", False),
        )
    }
    values["pipeline"] = getattr(values["pipeline"], "value", values["pipeline"])
    snapshot = current_snapshot(values)
    return {
        "_onnx_request": json.dumps(snapshot, sort_keys=True),
        **{
            key: snapshot["selections"][key]
            for key in (
                "use_doc_orientation_classify",
                "use_doc_unwarping",
                "use_textline_orientation",
            )
        },
        "local_models_only": getattr(options, "local_models_only", False),
    }


def _unwrap(pipeline):
    while hasattr(pipeline, "_pipeline"):
        pipeline = pipeline._pipeline
    return pipeline


def owned_runners(pipeline) -> list:
    runners = []
    seen = set()

    def visit(node):
        node = _unwrap(node)
        if id(node) in seen:
            return
        seen.add(id(node))
        if getattr(node, "runner", None) is not None:
            runners.append(node.runner)
            return
        for name, child in vars(node).items():
            if hasattr(child, "__dict__") and (
                name.endswith("_model") or name.endswith("_pipeline")
            ):
                visit(child)

    visit(pipeline.paddlex_pipeline)
    return runners


class _MissingTableLayoutPostprocess:
    """Only recover model table candidates when the normal layout has none."""

    def __init__(self, original):
        self.original = original

    def __call__(self, batch_outputs, datas, **kwargs):
        baseline = self.original(batch_outputs, datas, **kwargs)
        missing = [not any(box["cls_id"] == 8 for box in boxes) for boxes in baseline]
        if not any(missing):
            return baseline
        table_outputs = [
            {**output, "boxes": output["boxes"][output["boxes"][:, 0] == 8]}
            for output in batch_outputs
        ]
        tables = self.original(
            table_outputs,
            datas,
            **{
                **kwargs,
                "threshold": 0.1,
                "layout_nms": True,
                # Containment filtering is restricted to the recovery table
                # candidates. Existing layout and all non-table blocks stay intact.
                "layout_merge_bboxes_mode": "large",
            },
        )
        return [
            original + table if recover else original
            for original, table, recover in zip(baseline, tables, missing, strict=True)
        ]


def _install_cell_topology(table_pipeline):
    from vibeocr.runtime.recognition.paddle_table_geometry import (
        TableGeometryError,
        topology_coordinates,
    )

    original = table_pipeline.trans_cells_det_results_to_html

    def convert(cells):
        try:
            logical = topology_coordinates(cells)
        except TableGeometryError as exc:
            raise PaddleOnnxError(str(exc)) from exc
        return original(logical)

    table_pipeline.trans_cells_det_results_to_html = convert


def _materialize_table_nodes(pipeline, snapshot):
    root = _unwrap(pipeline.paddlex_pipeline)
    if snapshot["pipeline_id"] == "OCR":
        return
    if snapshot["pipeline_id"] == "TABLE_RECOGNITION":
        # Unlike PPStructure, table_v2 does not forward layout kwargs from its
        # config. Set the official predictor options on this instance only.
        root.layout_det_model.layout_nms = True
        root.layout_det_model.layout_merge_bboxes_mode = "union"
    root.layout_det_model.post_op = _MissingTableLayoutPostprocess(
        root.layout_det_model.post_op
    )
    if snapshot["pipeline_id"] == "PP-StructureV3":
        root = getattr(root, "table_recognition_pipeline", None)
        if root is None:
            return
        root = _unwrap(root)
    _install_cell_topology(root)
    if snapshot["selections"]["use_table_orientation_classify"]:
        root.table_orientation_classify_model = root.create_model(
            root.table_orientation_classify_config
        )
    if root.general_ocr_pipeline is None:
        # This distinct OCR has box_thresh=.4; sharing the top .6 OCR changes table text.
        root.general_ocr_pipeline = root.create_pipeline(root.general_ocr_config_bak)


def _structure_config(snapshot, prepared):
    from paddlex.inference import load_pipeline_config

    config = load_pipeline_config(
        "table_recognition_v2"
        if snapshot["pipeline_id"] == "TABLE_RECOGNITION"
        else "PP-StructureV3"
    )
    selection = snapshot["selections"]
    expected = []

    def configure(node, *, top=False):
        for flag in _RETIRED:
            node[flag] = False
        modules = node.setdefault("SubModules", {})
        pipelines = node.setdefault("SubPipelines", {})
        modules.pop("ChartRecognition", None)
        pipelines.pop("SealRecognition", None)
        pipelines.pop("FormulaRecognition", None)
        if top:
            node["use_doc_preprocessor"] = (
                selection["use_doc_orientation_classify"]
                or selection["use_doc_unwarping"]
            )
            if not node["use_doc_preprocessor"]:
                pipelines.pop("DocPreprocessor", None)
        if node.get("pipeline_name") == "PP-StructureV3":
            node["use_table_recognition"] = selection["use_table_recognition"]
            if not selection["use_table_recognition"]:
                pipelines.pop("TableRecognition", None)
        if node.get("pipeline_name") == "doc_preprocessor":
            for flag, role in (
                ("use_doc_orientation_classify", "DocOrientationClassify"),
                ("use_doc_unwarping", "DocUnwarping"),
            ):
                node[flag] = selection[flag]
                if not selection[flag]:
                    modules.pop(role, None)
        if node.get("pipeline_name") == "OCR":
            node["use_textline_orientation"] = selection["use_textline_orientation"]
            if not selection["use_textline_orientation"]:
                modules.pop("TextLineOrientation", None)
            elif "TextLineOrientation" not in modules:
                modules["TextLineOrientation"] = {
                    "module_name": "textline_orientation",
                    "model_name": "PP-LCNet_x0_25_textline_ori",
                }
        if not selection["use_table_orientation_classify"]:
            modules.pop("TableOrientationClassify", None)
        for role, model in modules.items():
            if role in ("TextDetection", "TextRecognition"):
                model["model_name"] = (
                    f"PP-OCRv6_{snapshot['model_preset']}_{'det' if role == 'TextDetection' else 'rec'}"
                )
            elif role == "LayoutDetection":
                model["model_name"] = "PP-DocLayout_plus-L"
            name = model["model_name"]
            if name not in snapshot["models"]:
                raise PaddleOnnxError(f"Unprepared model in Paddle ONNX graph: {name}")
            model["model_dir"] = str(prepared / name)
            expected.append(name)
        for child in pipelines.values():
            configure(child)

    configure(config, top=True)
    return config, expected


class PaddleOnnxPipeline:
    def __init__(
        self, pipeline, snapshot: dict, expected_models: list[str] | None = None
    ):
        self.pipeline = pipeline
        self.snapshot = snapshot
        self._lock = threading.RLock()
        self.sessions = []
        runners = owned_runners(pipeline)
        if (
            expected_models is not None
            and Counter(Path(r.model_dir).name for r in runners)
            != Counter(expected_models)
        ) or (expected_models is None and len(runners) != len(snapshot["models"])):
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
                for key, value in self.snapshot.get("selections", {}).items():
                    if (
                        key in kwargs
                        and kwargs[key] is not None
                        and kwargs[key] != value
                    ):
                        raise PaddleOnnxError(
                            "Paddle ONNX prediction selection snapshot changed"
                        )
                if any(kwargs.get(key) for key in _RETIRED):
                    raise PaddleOnnxError(
                        "Formula, seal and chart recognition are retired"
                    )
                if self.snapshot.get("pipeline_id") == "PP-StructureV3":
                    kwargs.update(dict.fromkeys(_RETIRED, False))
                    kwargs.setdefault("use_e2e_wireless_table_rec_model", False)
                    kwargs.setdefault("use_wireless_table_cells_trans_to_html", True)
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
    selections = snapshot["selections"]
    for option in (
        "use_doc_orientation_classify",
        "use_doc_unwarping",
        "use_textline_orientation",
    ):
        if flags.get(option, selections[option]) != selections[option]:
            raise PaddleOnnxError("Paddle ONNX auxiliary selection snapshot changed")
        flags[option] = selections[option]
    if any(flags.get(key) for key in _RETIRED):
        raise PaddleOnnxError("Formula, seal and chart recognition are retired")
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
    provider_options = (
        {"device_id": str(snapshot["device_id"])} if snapshot["device"] != "cpu" else {}
    )
    if snapshot["device"] == "cuda":
        # cuDNN NCHW depthwise 1x7 fails on the frozen small_rec PDF batch; keep FP32 via NHWC.
        provider_options["prefer_nhwc"] = "1"
    engine_config = {"providers": [provider], "provider_options": [provider_options]}
    if snapshot["device"] == "directml":
        engine_config.update(enable_mem_pattern=False, execution_mode="sequential")
    os.environ["PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK"] = "True"
    import paddleocr
    from paddlex.inference.models.runners.onnxruntime_runner import ONNXRuntimeRunner

    expected = None
    if snapshot["pipeline_id"] == "OCR":
        model_arguments = {}
        prefixes = {
            "PP-LCNet_x1_0_doc_ori": "doc_orientation_classify",
            "UVDoc": "doc_unwarping",
            "PP-LCNet_x0_25_textline_ori": "textline_orientation",
        }
        for name in snapshot["models"]:
            prefix = (
                "text_detection"
                if name.endswith("_det")
                else "text_recognition"
                if name.endswith("_rec")
                else prefixes[name]
            )
            model_arguments[prefix + "_model_name"] = name
            model_arguments[prefix + "_model_dir"] = str(prepared.root / name)
        factory = paddleocr.PaddleOCR
        arguments = {**model_arguments, **flags}
    else:
        config, expected = _structure_config(snapshot, prepared.root)
        factory = (
            paddleocr.TableRecognitionPipelineV2
            if snapshot["pipeline_id"] == "TABLE_RECOGNITION"
            else paddleocr.PPStructureV3
        )
        arguments = {"paddlex_config": config}
    # The locked upstream close() is empty, including partially built graphs.
    # Capture each runner before initialization so failed constructors release it too.
    captured = []
    with _CONSTRUCTION_LOCK:
        original = ONNXRuntimeRunner.__init__
        owner_thread = threading.get_ident()

        def initialize(runner, *args, **kwargs):
            if threading.get_ident() == owner_thread:
                captured.append(runner)
            original(runner, *args, **kwargs)

        ONNXRuntimeRunner.__init__ = initialize
        try:
            pipeline = factory(
                engine="onnxruntime",
                device="cpu",
                engine_config=engine_config,
                **arguments,
            )
            _materialize_table_nodes(pipeline, snapshot)
            return PaddleOnnxPipeline(pipeline, snapshot, expected)
        except Exception as exc:
            for runner in captured:
                runner.session = None
            if isinstance(exc, PaddleOnnxError):
                raise
            raise PaddleOnnxError(str(exc)) from exc
        finally:
            ONNXRuntimeRunner.__init__ = original

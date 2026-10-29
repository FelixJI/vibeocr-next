"""Paddle adapter proxy; the dedicated worker owns ORT pipelines and their cv2."""

from __future__ import annotations

import base64
import json
import logging
import os
import queue
import subprocess
import sys
import threading
import time
from dataclasses import asdict
from pathlib import Path

from vibeocr.runtime.environments.model_cache import LocalModelsNotPrepared
from vibeocr.runtime.jobs.budgets import AdapterCapability, InputItem
from vibeocr.runtime.processes.utils.job_object import CREATE_SUSPENDED, JobObjectGuard
from vibeocr.runtime.processes.utils.subprocess_log import SubprocessLogForwarder
from vibeocr.runtime.recognition.ocr_engines import (
    REASON_ENGINE_NOT_INSTALLED,
    EngineAvailability,
    EngineDescriptor,
)
from vibeocr.runtime.recognition.paddle_adapter import PADDLE_REQUIRED_COMPONENT
from vibeocr.runtime.recognition.paddle_onnx import (
    PaddleOnnxCancelled,
    PaddleOnnxError,
    cancelled,
    current_snapshot,
    is_ort_environment,
)
from vibeocr.runtime.recognition.worker_idle import WorkerIdleOwner
from vibeocr.runtime_contracts import (
    PipelineSelection,
    RecognitionResourceKind,
    ResidencyEntry,
    ResidencyKind,
    ResidencyStatus,
    SettingsSnapshot,
    parse_pipeline_spec,
    parse_residency_entry,
)
from vibeocr.runtime_contracts.dtos import OcrEngine

logger = logging.getLogger(__name__)


def paddle_python() -> Path | None:
    """#270：Paddle worker 解释器只在 Paddle ONNX 管理环境内指向当前进程。

    不再发现旧 legacy 安装的 ``engines/paddle`` 隔离解释器，也不再在非
    ORT 环境里落回原生 Paddle；返回 None 表示本进程没有可用 Paddle 后端。
    """
    from vibeocr.runtime.recognition.paddle_onnx import RECIPES

    if os.environ.get("VIBEOCR_MANAGED_ENVIRONMENT_RECIPE", "") in RECIPES:
        return Path(sys.executable)
    return None


class PaddleProcessAdapter:
    engine_id = OcrEngine.PADDLEOCR
    # Paddle CPU/DirectML 随基础运行时离线发布；CUDA 环境与大型模型按需准备。
    included_in_base = True

    def __init__(self, python: Path) -> None:
        self.python = python
        self._process: subprocess.Popen | None = None
        self._lock = threading.RLock()
        self._request_lock = threading.RLock()
        self._responses: queue.Queue = queue.Queue()
        self._guard: JobObjectGuard | None = None
        self._status = ResidencyStatus()
        self._settings = SettingsSnapshot()
        self._settings_pending = False
        self._readers: list[threading.Thread] = []
        self._idle = WorkerIdleOwner("PaddleOCR", self._stop_process, lock=self._lock)

    @staticmethod
    def probe_descriptor() -> EngineDescriptor:
        python = paddle_python()
        available = False
        if python is not None:
            try:
                # #270：探针只验证 Paddle ONNX 编排依赖（paddleocr/paddlex +
                # onnxruntime），不再探测原生 Paddle 导入。
                result = subprocess.run(
                    [
                        str(python),
                        "-I",
                        "-c",
                        "from paddleocr import PaddleOCR; import onnxruntime",
                    ],
                    capture_output=True,
                    timeout=60,
                    creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
                )
                available = result.returncode == 0
            except (OSError, subprocess.TimeoutExpired):
                pass
        return EngineDescriptor(
            engine_id=OcrEngine.PADDLEOCR,
            availability=EngineAvailability.READY
            if available
            else EngineAvailability.PREPARATION_REQUIRED,
            included_in_base=True,
            reason_code=None if available else REASON_ENGINE_NOT_INSTALLED,
            required_component=None if available else PADDLE_REQUIRED_COMPONENT,
        )

    def descriptor(self) -> EngineDescriptor:
        return self.probe_descriptor()

    def _start(self) -> None:
        if self._process is not None and self._process.poll() is None:
            return
        self._stop()
        self._idle.starting()
        responses = queue.Queue()
        self._responses = responses
        env = dict(os.environ, PYTHONNOUSERSITE="1", PYTHONUTF8="1")
        env.pop("PYTHONPATH", None)
        product_code = env.get("VIBEOCR_PRODUCT_CODE_ROOT")
        arguments = (
            [
                "-I",
                "-B",
                "-c",
                "import os,runpy,sys;sys.path.insert(0,os.environ['VIBEOCR_PRODUCT_CODE_ROOT']);runpy.run_module('vibeocr.runtime.recognition.paddle_worker',run_name='__main__')",
            ]
            if product_code
            else ["-I", "-m", "vibeocr.runtime.recognition.paddle_worker"]
        )
        process = subprocess.Popen(
            [str(self.python), *arguments],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
            env=env,
            creationflags=(subprocess.CREATE_NO_WINDOW | CREATE_SUSPENDED)
            if os.name == "nt"
            else 0,
        )
        self._process = process
        self._guard = JobObjectGuard(allow_breakaway=False)
        if os.name == "nt" and not self._guard.assign_from_popen(
            process, suspended=True
        ):
            process.terminate()
            process.wait(timeout=5)
            self._stop()
            raise RuntimeError("Paddle worker ownership could not be established")

        def read_replies() -> None:
            try:
                for line in process.stdout:
                    responses.put(line)
            finally:
                responses.put(None)

        def read_logs() -> None:
            forwarded_logger = logging.getLogger("vibeocr.subprocess.paddle")
            if not forwarded_logger.handlers:
                handler = logging.StreamHandler(sys.stderr)
                handler.setFormatter(logging.Formatter("%(message)s"))
                forwarded_logger.addHandler(handler)
            forwarded_logger.setLevel(logging.INFO)
            forwarded_logger.propagate = False
            forwarder = SubprocessLogForwarder(
                logger_name="vibeocr.subprocess.paddle",
                source_label="[Paddle worker]",
            )
            try:
                for line in process.stderr:
                    for part in forwarder.split_mixed_lines(line):
                        forwarder.forward(part)
            finally:
                forwarder.flush()

        self._readers = [
            threading.Thread(target=read_replies, daemon=True),
            threading.Thread(target=read_logs, daemon=True),
        ]
        for reader in self._readers:
            reader.start()
        # Settings IPC belongs to the serial request path, outside the owner lock.
        self._settings_pending = True
        self._idle.started()

    def _exchange(self, request: dict, *, timeout: float = 3600) -> object:
        with self._lock:
            process = self._process
            responses = self._responses
        if process is None or process.stdin is None:
            raise RuntimeError("Paddle worker is not running")
        try:
            process.stdin.write(json.dumps(request, ensure_ascii=True) + "\n")
            process.stdin.flush()
        except (OSError, ValueError) as exc:
            self._stop()
            if is_ort_environment():
                raise PaddleOnnxError("Paddle ONNX worker request failed") from exc
            raise
        deadline = time.monotonic() + timeout
        try:
            while True:
                if not is_ort_environment():
                    line = responses.get(timeout=timeout)
                    break
                if cancelled():
                    self._stop()
                    raise PaddleOnnxCancelled("Paddle ONNX task cancelled")
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise queue.Empty
                try:
                    line = responses.get(timeout=min(0.1, remaining))
                    break
                except queue.Empty:
                    continue
        except queue.Empty as exc:
            self._stop()
            if is_ort_environment():
                raise PaddleOnnxError("Paddle ONNX worker deadline exceeded") from exc
            raise TimeoutError("Paddle worker deadline exceeded") from exc
        if line is None:
            self._stop()
            if is_ort_environment():
                raise PaddleOnnxError("Paddle ONNX worker exited before replying")
            raise RuntimeError("Paddle worker exited before replying")
        try:
            response = json.loads(line)
            if not isinstance(response, dict) or not (
                {"result", "error"} & response.keys()
            ):
                raise ValueError("Paddle worker response is invalid")
        except (ValueError, TypeError) as exc:
            self._stop()
            if is_ort_environment():
                raise PaddleOnnxError("Paddle ONNX worker response is invalid") from exc
            raise
        if "error" in response:
            if response.get("error_type") == "PaddleOnnxError":
                self._stop()
                raise PaddleOnnxError(response["error"])
            if response.get("error_type") == "LocalModelsNotPrepared":
                raise LocalModelsNotPrepared(response["error"])
            raise RuntimeError(response["error"])
        return response["result"]

    def reserve(self):
        return self._idle.reserve()

    def _request(self, operation: str, **payload: object) -> object:
        release = self.reserve()
        try:
            with self._request_lock:
                try:
                    with self._lock:
                        self._idle.working()
                        self._start()
                        settings = self._settings if self._settings_pending else None
                        self._settings_pending = False
                    if settings is not None:
                        self._exchange(
                            {"operation": "settings", "settings": settings.to_payload()}
                        )
                    return self._exchange({"operation": operation, **payload})
                except Exception as exc:
                    with self._lock:
                        if self._idle.state == "starting":
                            self._idle.stop("start_failed", force=True)
                        self._idle.record_error(exc, "request_failed")
                    raise
        finally:
            release()

    def capabilities(
        self, options: PipelineSelection | None = None
    ) -> AdapterCapability:
        from vibeocr.runtime.recognition.core.pipelines import get_registry

        name = (options or PipelineSelection("OCR")).pipeline_id
        registry = get_registry()
        spec = registry.get(name) if registry.has(name) else None
        real_batch = bool(
            spec is not None and getattr(spec, "recognize_batch", None) is not None
        )
        return AdapterCapability(
            name=name, real_batch=real_batch, max_compute_batch=8 if real_batch else 1
        )

    def recognize_many(
        self,
        items: list[InputItem],
        *,
        options: PipelineSelection | None = None,
        compute_batch: object | None = None,
    ) -> list[dict]:
        del compute_batch  # The Paddle adapter schedules its own batches.
        wire_items = [
            {**asdict(item), "data": base64.b64encode(item.data).decode("ascii")}
            for item in items
        ]
        result = self._request(
            "recognize",
            items=wire_items,
            options=(options or PipelineSelection("OCR")).to_payload(),
            snapshot=current_snapshot(
                {**options.options, "pipeline": options.pipeline_id} if options else {}
            )
            if is_ort_environment()
            else None,
        )
        if not isinstance(result, list):
            raise RuntimeError("Invalid Paddle worker result")
        return result

    def _record_status(self, result: dict) -> ResidencyStatus:
        self._status = ResidencyStatus(
            default_ttl_seconds=result["default_ttl_seconds"],
            entries=tuple(parse_residency_entry(p) for p in result["entries"]),
            pipelines=tuple(
                parse_pipeline_spec(p) for p in result.get("pipelines", [])
            ),
        )
        return self._status

    def residency_status(self) -> ResidencyStatus:
        worker = self._idle.snapshot()
        from vibeocr.runtime.environments.model_cache import paddle_text_models_prepared

        cache_home = os.environ.get("PADDLE_PDX_CACHE_HOME")
        prepared = paddle_text_models_prepared(Path(cache_home)) if cache_home else None
        return ResidencyStatus(
            entries=(
                ResidencyEntry(
                    pipeline="PaddleOCR",
                    models_prepared=prepared,
                    resource_kind=RecognitionResourceKind.PROCESS,
                    resource_id="paddle",
                    kind=ResidencyKind.IDLE
                    if self._idle.running
                    else ResidencyKind.EVICTED,
                    active_leases=worker["active_leases"],
                    remaining_ttl_seconds=worker["remaining_seconds"],
                    worker=worker,
                ),
            )
        )

    def preload(self, pipelines: tuple[str, ...]) -> ResidencyStatus:
        self._request("preload", pipelines=pipelines)
        return self.residency_status()

    def release_idle(self, pipeline: str | None = None) -> ResidencyStatus:
        if pipeline not in (
            None,
            "PaddleOCR",
            "OCR",
            "TABLE_RECOGNITION",
            "PP-StructureV3",
        ):
            return self.residency_status()
        self._idle.stop("explicit_release")
        return self.residency_status()

    def settings_guard(self):
        return self._lock

    def configure_settings(self, snapshot: SettingsSnapshot) -> None:
        with self._lock:
            self._settings = snapshot
            self._settings_pending = True
            self._idle.configure(snapshot.paddle_idle_seconds)

    def _stop(self) -> None:
        self._idle.stop("failure_recovery", force=True)

    def _stop_process(self) -> None:
        process = self._process
        if process is not None:
            if process.poll() is None:
                # Only idle EOF is safe: an active writer may hold TextIO's buffer lock.
                if self._idle.active == 0:
                    if process.stdin is not None:
                        try:
                            process.stdin.close()
                        except OSError:
                            pass
                    try:
                        process.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        if self._guard is not None:
                            self._guard.terminate_and_wait(timeout=5)
                        else:
                            process.terminate()
                        process.wait(timeout=5)
                else:
                    if self._guard is not None:
                        self._guard.terminate_and_wait(timeout=5)
                    else:
                        process.terminate()
                    process.wait(timeout=5)
            if self._guard is not None:
                self._guard.terminate_and_wait(timeout=5)
            for reader in self._readers:
                reader.join(timeout=1)
                if reader.is_alive():
                    raise TimeoutError("Paddle worker pipes did not drain")
            self._readers = []
            for stream in (process.stdin, process.stdout, process.stderr):
                if stream is not None and not stream.closed:
                    try:
                        stream.close()
                    except OSError:
                        pass  # A forced stop intentionally discards the broken input frame.
        if self._guard is not None:
            self._guard.close()
            self._guard = None
        self._process = None
        self._responses = queue.Queue()
        self._status = ResidencyStatus()

    def close(self) -> None:
        self._idle.close()

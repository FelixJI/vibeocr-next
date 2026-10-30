"""MinerU 文档解析服务

通过 mineru-api FastAPI 服务进行文档解析。
自动管理 mineru-api 进程的生命周期。
"""

from __future__ import annotations

import base64
import json
import logging
import os
import re
import socket
import subprocess
import sys
import threading
import time
from collections.abc import Callable
from dataclasses import replace
from pathlib import Path
from typing import TYPE_CHECKING, Any

import httpx
from vibeocr.runtime.documents.tables.blocks import (
    canonicalize_table_block,
    table_model_from_block,
)
from vibeocr.runtime.documents.tables.contracts import TableProvenanceV1
from vibeocr.runtime.documents.tables.projections import table_model_to_plain_text
from vibeocr.runtime.documents.tables.reducer import rebuild_result_projections
from vibeocr.runtime.documents.utils.mime_types import mime_to_extension
from vibeocr.runtime.environments.runtime_maintenance import safe_runtime_detail
from vibeocr.runtime.processes.utils.job_object import CREATE_SUSPENDED, JobObjectGuard
from vibeocr.runtime.processes.utils.subprocess_log import strip_ansi
from vibeocr.runtime.recognition.core.constants import Constants
from vibeocr.runtime.recognition.core.singleton_meta import SingletonMeta
from vibeocr.runtime.recognition.mineru_api import (
    MineruApiClient,
    MineruApiError,
    MineruCancelled,
    MineruDocument,
)
from vibeocr.runtime.recognition.mineru_config import (
    MineruConfigError,
    migrate_legacy_options,
)
from vibeocr.runtime.recognition.mineru_readiness import (
    CONNECTION_REMOTE,
    current_connection,
)
from vibeocr.runtime.recognition.mineru_result import project_document
from vibeocr.runtime.recognition.models.ocr_result import (
    DISCARDED_BLOCK_TYPES,
    OCRResult,
    TextBlock,
    normalize_bbox,
    normalize_content_list,
)
from vibeocr.runtime_contracts import (
    ErrorCode,
    MineruConfig,
    MineruOcrMode,
    MineruTier,
)
from vibeocr.runtime_contracts.utils.http_log import (
    guess_response_size,
    log_http_response,
)

if TYPE_CHECKING:
    from vibeocr.runtime.recognition.models.ocr_options import OCROptions

_logger = logging.getLogger(__name__)


class MinerUService(metaclass=SingletonMeta):
    """MinerU 文档解析服务（单例）

    通过 mineru-api FastAPI 服务进行文档解析。
    自动管理 mineru-api 进程的生命周期。
    """

    _api_process: subprocess.Popen | None = None
    _api_url: str = ""
    _lock = threading.RLock()
    _ownership_lock = threading.RLock()
    _initialized = False
    _server_tier = "basic"
    _process_generation = 0
    _prepare_process: subprocess.Popen | None = None
    _prepare_guard: JobObjectGuard | None = None
    _log_reader: threading.Thread | None = None
    _startup_cancel: threading.Event | None = None
    _api_owner: threading.Event | None = None
    _prepare_owner: threading.Event | None = None
    _job_guard: JobObjectGuard | None = None
    _model_root: str | None = None

    def __init__(self):
        if current_connection().mode == CONNECTION_REMOTE:
            return
        if not self._initialized:
            with self._lock:
                if (
                    not self._initialized
                ):  # pragma: no cover - DCL inner recheck, only races under concurrency
                    # 远程连接不管理本地 mineru-api 子进程；首次真实请求时
                    # 由 _call_api 直接访问自部署服务器。
                    if current_connection().mode != CONNECTION_REMOTE:
                        self._ensure_api_running()
                    with self.__class__._ownership_lock:
                        self._require_owner(self.__class__._startup_cancel)
                        self.__class__._initialized = True

    @classmethod
    def _reset(cls) -> None:
        """重置服务状态（供测试使用）"""
        with cls._lock, cls._ownership_lock:
            cls._stop_preparation()
            cls._stop_api_locked()
            cls._model_root = None
            cls._startup_cancel = None

    @staticmethod
    def _parse_api_log_level(text: str) -> int:
        """从 mineru-api 日志行中提取日志级别"""
        import re

        match = re.search(
            r"\b(DEBUG|INFO|WARNING|ERROR|CRITICAL)\b", text, re.IGNORECASE
        )
        if match:
            return getattr(logging, match.group(1).upper(), logging.DEBUG)
        return logging.DEBUG

    def _start_log_reader(self, process: subprocess.Popen) -> None:
        """启动守护线程读取 mineru-api 子进程的 stderr 并转发到项目日志系统。

        注意：mineru-api 是第三方 FastAPI 服务，其日志格式（uvicorn 风格，
        如 ``INFO:     127.0.0.1:... "POST /v1/parse/jobs HTTP/1.1" 200``）不符合
        SubprocessLogForwarder 的结构化正则（YYYY-MM-DD HH:MM:SS [LEVEL] name: msg），
        因此这里不复用 forwarder 的折叠逻辑（会把 uvicorn 的有用 INFO 全折叠掉），
        而是保留原有的"按级别词匹配 + 原文转发"。仅 logger 名统一到
        vibeocr.subprocess.<name> 规范。
        """
        mineru_logger = logging.getLogger("vibeocr.subprocess.mineru_api")
        stderr = process.stderr
        if stderr is None:
            return

        def _read():
            try:
                # 退出后仍排空管道，保留崩溃日志的最后几行。
                while line := stderr.readline():
                    text = strip_ansi(line.decode("utf-8", errors="replace")).strip()
                    if not text:
                        continue
                    level = self._parse_api_log_level(text)
                    mineru_logger.log(level, text)
            except Exception:
                pass

        t = threading.Thread(target=_read, daemon=True, name="MinerUApiLogReader")
        self.__class__._log_reader = t
        t.start()

    def _check_api_running(self, url: str) -> bool:
        """检查 mineru-api 是否运行"""
        request_url = f"{url}/v1/health"
        started = time.perf_counter()
        try:
            resp = httpx.get(request_url, timeout=3, trust_env=False)
            log_http_response(
                logger=_logger,
                method="GET",
                url=request_url,
                status_code=resp.status_code,
                reason=resp.reason_phrase,
                elapsed_ms=(time.perf_counter() - started) * 1000,
                response_bytes=guess_response_size(dict(resp.headers), resp.content),
            )
            return (
                resp.status_code == 200
                and str(resp.json().get("version", "")).split(".")[0] == "4"
            )
        except Exception as exc:
            _logger.warning(
                "[MinerU] GET %s failed after %.1f ms: %s",
                request_url,
                (time.perf_counter() - started) * 1000,
                exc,
            )
            return False

    def _find_free_port(self) -> int:
        """找一个可用端口"""
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
            s.bind(("127.0.0.1", 0))
            return s.getsockname()[1]

    def _resolve_python_executable(self) -> Path | None:
        """查找可用的 Python 解释器

        查找顺序:
        1. 嵌入式 Python（便携模式）
        2. 当前 Python 解释器（开发模式）
        """
        if os.environ.get("VIBEOCR_MANAGED_ENVIRONMENT_ID"):
            return Path(sys.executable)
        from vibeocr.runtime.environments.env_manager import (
            get_embedded_python,
            get_project_root,
        )

        project_root = get_project_root()
        embedded = get_embedded_python(project_root)
        if embedded.exists():
            return embedded

        return Path(sys.executable)

    @staticmethod
    def _local_environment() -> dict[str, str]:
        env = os.environ.copy()
        env.pop("PYTHONPATH", None)
        # A separate home leaves MinerU 3 config and model paths untouched.
        from vibeocr.runtime.environments.env_manager import get_project_root

        home = Path(
            env.get("MINERU_HOME") or get_project_root() / "data" / "mineru4"
        ).resolve()
        home.mkdir(parents=True, exist_ok=True)
        config_path = Path(env.get("MINERU_CONFIG") or home / "config.yaml").resolve()
        if "MINERU_CONFIG" in env and not config_path.is_file():
            raise ValueError("MINERU_CONFIG must reference an existing config file")
        if not config_path.exists():
            config_path.write_text(
                "model:\n  small_backend: onnx\n  vlm:\n    engine: llama-cpp\n",
                encoding="utf-8",
            )
        env["MINERU_HOME"] = str(home)
        env["MINERU_CONFIG"] = str(config_path)
        # 设备意图：accelerator 权威，缺失时回退 legacy VIBEOCR_USE_GPU。CPU 时
        # 仅在子进程 env 隐藏设备（llama-cpp 默认 n_gpu_layers=99 会抢占 GPU，
        # VlmConfig 无该字段）；GGML 空设备列表须用单空格（空串在 Windows 等于删除）。
        accelerator = env.get("VIBEOCR_RUNTIME_ACCELERATOR")
        gpu_selected = (
            accelerator == "nvidia_cuda"
            if accelerator is not None
            else env.get("VIBEOCR_USE_GPU", "").lower() == "true"
        )
        if not gpu_selected:
            env["CUDA_VISIBLE_DEVICES"] = "-1"
            env["GGML_VK_VISIBLE_DEVICES"] = " "

        return env

    def _start_api(self, *, owner: threading.Event | None = None) -> None:
        """启动 mineru-api 进程。"""
        cancel = owner if owner is not None else self.__class__._startup_cancel
        if cancel is not None and cancel.is_set():
            raise MineruCancelled("MinerU startup canceled")
        python_exe = self._resolve_python_executable()
        if python_exe is None:
            raise RuntimeError("找不到 Python 解释器。请确保已安装 Python 和 MinerU 4")

        port = self._find_free_port()
        url = f"http://127.0.0.1:{port}"

        _logger.info(f"[MinerU] 启动 mineru-api 服务 @ {url}...")

        # MinerU 4 api_server CLI 无 --language（传入即退出码 2），语言不经 CLI 传递。
        cmd = [
            str(python_exe),
            "-m",
            "mineru.parser.api_server",
            "--host",
            "127.0.0.1",
            "--port",
            str(port),
            "--tier",
            self.__class__._server_tier,
        ]

        env = self._local_environment()
        if self.__class__._model_root is not None:
            env["MINERU_MODEL_BASE_DIR"] = self.__class__._model_root
            env["MINERU_MODEL_SOURCE"] = "local"

        with self.__class__._ownership_lock:
            self._require_owner(cancel)
            if (
                self.__class__._api_process is not None
                or self.__class__._job_guard is not None
            ):
                raise RuntimeError("MinerU still owns an unconfirmed process")
            self.__class__._api_process = subprocess.Popen(
                cmd,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.PIPE,
                env=env,
                cwd=Path(env["MINERU_HOME"]),
                creationflags=(subprocess.CREATE_NO_WINDOW | CREATE_SUSPENDED)
                if os.name == "nt"
                else 0,
            )

            # Publish ownership before Job assignment/resume can fail.
            self.__class__._api_owner = cancel
            # 绑定 Windows Job Object：主进程崩溃时内核连带终止 mineru-api
            self.__class__._job_guard = JobObjectGuard(allow_breakaway=False)
            if os.name == "nt" and not self.__class__._job_guard.assign_from_popen(
                self.__class__._api_process, suspended=True
            ):
                self.__class__._api_process.terminate()
                self.__class__._api_process.wait(timeout=5)
                self.shutdown_owned()
                raise RuntimeError("MinerU worker ownership could not be established")

            self.__class__._process_generation += 1
            # 读取 mineru-api 子进程的 stderr 并转发到项目日志系统
            self._start_log_reader(self.__class__._api_process)

            process = self.__class__._api_process
        _logger.debug("[MinerU] 日志输出到项目日志系统")

        # 等待 API 就绪
        for _ in range(int(Constants.Timeout.MINERU_API_START)):
            if cancel is not None and cancel.is_set():
                raise MineruCancelled("MinerU startup canceled")
            if process.poll() is not None:
                raise RuntimeError(f"mineru-api 启动失败，退出码: {process.returncode}")
            if self._check_api_running(url):
                with self.__class__._ownership_lock:
                    self._require_owner(cancel)
                    if self.__class__._api_process is not process:
                        raise MineruCancelled("MinerU process generation changed")
                    self.__class__._api_url = url
                _logger.info(f"[MinerU] mineru-api 服务已就绪 @ {url}")
                return
            if cancel is None:
                time.sleep(1)
            elif cancel.wait(1):
                raise MineruCancelled("MinerU startup canceled")

        raise RuntimeError(
            f"mineru-api 启动超时（{int(Constants.Timeout.MINERU_API_START)}秒）"
        )

    @classmethod
    def bind_owner(cls, owner: threading.Event) -> None:
        with cls._ownership_lock:
            if owner.is_set():
                raise MineruCancelled("MinerU owner canceled")
            if (
                (cls._api_process is not None or cls._job_guard is not None)
                and cls._api_owner is not owner
            ) or (
                (cls._prepare_process is not None or cls._prepare_guard is not None)
                and cls._prepare_owner is not owner
            ):
                raise RuntimeError("MinerU still owns another generation")
            cls._startup_cancel = owner

    @classmethod
    def _require_owner(cls, owner: threading.Event | None) -> None:
        if owner is not None and (owner.is_set() or cls._startup_cancel is not owner):
            raise MineruCancelled("MinerU owner canceled or replaced")

    def _prepare_cached_models(
        self,
        tier: str,
        cancelled: Callable[[], bool],
        *,
        owner: threading.Event | None = None,
    ) -> None:
        if not os.environ.get("VIBEOCR_SHARED_MODEL_CACHE"):
            return
        if cancelled():
            raise MineruCancelled("MinerU job canceled before model preparation")
        python = self._resolve_python_executable()
        if python is None:
            raise MineruApiError("MinerU interpreter is unavailable")
        env = self._local_environment()
        arguments = (
            [
                "-I",
                "-B",
                "-c",
                "import os,runpy,sys;sys.path.insert(0,os.environ['VIBEOCR_PRODUCT_CODE_ROOT']);runpy.run_module('vibeocr.runtime.environments.model_cache',run_name='__main__')",
            ]
            if env.get("VIBEOCR_PRODUCT_CODE_ROOT")
            else ["-m", "vibeocr.runtime.environments.model_cache"]
        )
        with self.__class__._ownership_lock:
            self._require_owner(owner)
            self._stop_preparation()
            process = subprocess.Popen(
                [str(python), *arguments, tier],
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                env=env,
                creationflags=(subprocess.CREATE_NO_WINDOW | CREATE_SUSPENDED)
                if os.name == "nt"
                else 0,
            )
            guard = JobObjectGuard(allow_breakaway=False)
            self.__class__._prepare_process, self.__class__._prepare_guard = (
                process,
                guard,
            )
            self.__class__._prepare_owner = owner
            if os.name == "nt" and not guard.assign_from_popen(process, suspended=True):
                self._stop_preparation(process=process)
                raise RuntimeError(
                    "MinerU preparation ownership could not be established"
                )
        started = time.monotonic()
        try:
            while True:
                if cancelled():
                    raise MineruCancelled(
                        "MinerU job canceled during model preparation"
                    )
                if time.monotonic() - started > Constants.Timeout.MINERU_MODEL_DOWNLOAD:
                    raise MineruApiError("MinerU model preparation timed out")
                try:
                    stdout, stderr = process.communicate(timeout=0.2)
                    break
                except subprocess.TimeoutExpired:
                    continue
            if process.returncode != 0:
                raise MineruApiError(
                    "MinerU model preparation failed: "
                    + safe_runtime_detail(stderr.decode("utf-8", errors="replace"))[
                        -2000:
                    ]
                )
            if cancelled():
                raise MineruCancelled("MinerU job canceled after model preparation")
            try:
                root = json.loads(stdout.decode("utf-8").strip().splitlines()[-1])[
                    "model_root"
                ]
                if not isinstance(root, str) or not Path(root).is_dir():
                    raise ValueError("Model directory is unavailable")
            except (ValueError, KeyError, IndexError, TypeError) as exc:
                raise MineruApiError(
                    "MinerU model preparation returned an invalid result"
                ) from exc
            with self.__class__._ownership_lock:
                self._require_owner(owner)
                if root != self.__class__._model_root:
                    self._stop_api_locked(owner=owner)
                    self.__class__._model_root = root
        finally:
            self._stop_preparation(process=process)

    @classmethod
    def _stop_preparation(
        cls,
        *,
        process: subprocess.Popen | None = None,
        owner: threading.Event | None = None,
    ) -> None:
        with cls._ownership_lock:
            if process is not None and cls._prepare_process is not process:
                return
            if owner is not None and cls._prepare_owner is not owner:
                return
            current, guard = cls._prepare_process, cls._prepare_guard
            if current is None:
                return
            if guard is not None:
                guard.terminate_and_wait(timeout=5)
            if current.poll() is None:
                current.terminate()
            current.communicate(timeout=5)
            if guard is not None:
                guard.close()
            cls._prepare_process = cls._prepare_guard = cls._prepare_owner = None

    def _ensure_api_running(self, *, owner: threading.Event | None = None) -> None:
        cancel = owner if owner is not None else self.__class__._startup_cancel
        with self.__class__._ownership_lock:
            self._require_owner(cancel)
            url, process = self.__class__._api_url, self.__class__._api_process
        if url and self._check_api_running(url):
            with self.__class__._ownership_lock:
                self._require_owner(cancel)
                if process is self.__class__._api_process:
                    return
        with self._lock:
            with self.__class__._ownership_lock:
                self._require_owner(cancel)
                url, process = self.__class__._api_url, self.__class__._api_process
            if url and self._check_api_running(url):
                with self.__class__._ownership_lock:
                    self._require_owner(cancel)
                    if process is self.__class__._api_process:
                        return
            with self.__class__._ownership_lock:
                self._require_owner(cancel)
                self._stop_api_locked(owner=cancel)
            self._start_api(owner=cancel)

    @staticmethod
    def _config(options: OCROptions | MineruConfig | None) -> MineruConfig:
        if isinstance(options, MineruConfig):
            return options
        values = (
            {}
            if options is None
            else {
                name: getattr(options, name)
                for name in (
                    "backend",
                    "effort",
                    "parse_method",
                    "enable_formula",
                    "enable_table",
                    "lang_list",
                    "start_page_id",
                    "end_page_id",
                )
            }
        )
        return migrate_legacy_options(values)

    def _call_api(
        self,
        data: bytes,
        filename: str,
        options: OCROptions | MineruConfig | None = None,
        *,
        files: list[tuple[str, bytes]] | None = None,
        cancelled: Callable[[], bool] = lambda: False,
    ) -> dict[str, MineruDocument | MineruApiError]:
        config = self._config(options)
        # 在途解析固定原端点：入口快照连接，配置中途变更不串用。
        connection = current_connection()
        # 本地能力真值闭环：锁定 MinerU 4 无 language 上行通道，本地可能 OCR
        # 的组合对旧配置非 ch fail-closed，不静默改意图；纯抽取与远端不受限。
        if (
            connection.mode != CONNECTION_REMOTE
            and config.tier != MineruTier.FLASH
            and config.ocr_mode != MineruOcrMode.TXT
            and config.language != "ch"
        ):
            raise MineruConfigError(
                ErrorCode.MINERU_CONFIG_UNAVAILABLE, "unsupported_language"
            )
        owner = (
            self.__class__._startup_cancel
            if connection.mode != CONNECTION_REMOTE
            else None
        )
        user_cancelled = cancelled

        def cancelled() -> bool:
            return user_cancelled() or (owner is not None and owner.is_set())

        # Serialize jobs through shutdown/start and result download; never
        # mutate a live service used by another job.
        with self._lock:
            if cancelled():
                raise MineruCancelled("MinerU request canceled")
            if connection.mode == CONNECTION_REMOTE:
                # 远程自部署服务器：绝不回退本地，失败原样上抛。
                result = MineruApiClient(
                    connection.api_url,
                    timeout=Constants.Timeout.MINERU_HTTP_TOTAL,
                    api_key=connection.api_key or None,
                ).parse(
                    files if files is not None else [(filename, data)],
                    config,
                    cancelled=cancelled,
                )
                if cancelled():
                    raise MineruCancelled(
                        "MinerU remote request canceled after response"
                    )
                return result
            tier = (
                "standard"
                if config.tier in (MineruTier.STANDARD, MineruTier.ADVANCED)
                else "basic"
            )
            if config.tier != MineruTier.FLASH:
                self._prepare_cached_models(tier, cancelled, owner=owner)
            with self.__class__._ownership_lock:
                self._require_owner(owner)
                if self.__class__._server_tier != tier:
                    self._stop_api_locked(owner=owner)
                    self.__class__._server_tier = tier
            self._ensure_api_running(owner=owner)
            result = MineruApiClient(
                self.__class__._api_url, timeout=Constants.Timeout.MINERU_HTTP_TOTAL
            ).parse(
                files if files is not None else [(filename, data)],
                config,
                cancelled=cancelled,
            )
            if cancelled():
                raise MineruCancelled("MinerU request canceled after response")
            return result

    def prepare(self, *, cancelled: Callable[[], bool] = lambda: False) -> None:
        """Explicit preload verifies actual parsing instead of claiming health is ready."""
        import pymupdf as fitz
        from vibeocr.runtime.recognition.mineru_readiness import (
            mark_executed,
            mark_failed,
        )

        with fitz.open() as pdf:
            page = pdf.new_page()
            page.insert_textbox(
                fitz.Rect(72, 150, 520, 500),
                "VibeOCR readiness document. Document parsing preserves readable text.\n"
                "This generated sample verifies the requested parsing tier.",
                fontsize=14,
            )
            data = pdf.tobytes()
        for tier in MineruTier:
            # 逐 tier 快照连接：观察归属“实际服务该次解析的连接”。生产路径
            # 由 adapter 租约保证解析期间连接不被切换；无租约的直接调用下，
            # 迟到的旧结果也只会记在旧连接名下，不计入新连接。
            connection = current_connection()
            try:
                document = self._call_api(
                    data,
                    "readiness.pdf",
                    MineruConfig(
                        tier=tier,
                        ocr_mode=MineruOcrMode.TXT
                        if tier == MineruTier.FLASH
                        else MineruOcrMode.OCR,
                    ),
                    cancelled=cancelled,
                )["readiness.pdf"]
                if isinstance(document, MineruApiError):
                    raise document
                result = project_document(document)
                if not result.raw_text.strip():
                    raise MineruApiError(
                        f"MinerU {tier.value} readiness produced no text"
                    )
            except Exception:
                mark_failed(tier)
                raise
            mark_executed(tier, connection=connection)

    def parse(
        self,
        data: bytes,
        mime_type: str,
        options: OCROptions | MineruConfig | None = None,
    ) -> OCRResult:
        filename = "input" + self._get_extension(mime_type)
        document = self._call_api(data, filename, options)[filename]
        if isinstance(document, MineruApiError):
            raise document
        return project_document(document)

    def file_parse(
        self,
        files: list[tuple[str, bytes]],
        *,
        options: OCROptions | MineruConfig | None = None,
        backend: str | None = None,
        cancelled: Callable[[], bool] = lambda: False,
    ) -> dict[str, dict[str, Any]]:
        if not files:
            return {}
        if backend is not None and backend != "hybrid-engine":
            # Old callers must explicitly reselect an equivalent new tier.
            migrate_legacy_options({"backend": backend})
        documents = self._call_api(
            b"", "multi.bin", options, files=files, cancelled=cancelled
        )
        from vibeocr.runtime.recognition.models import ocr_result_to_payload

        result = {}
        for filename, document in documents.items():
            if isinstance(document, MineruApiError):
                result[Path(filename).stem] = {"mineru_error": str(document)}
                continue
            try:
                projected = project_document(document)
            except MineruCancelled:
                # 取消必须无条件上抛，绝不允许波及为文件错误。
                raise
            except MineruApiError as exc:
                # 投影契约失败对同一结果是确定性的：按文件隔离为
                # mineru_error（与上游 per-file 失败同语义），不进入
                # supervisor 通用恢复路径重发整批解析，同批其他文件继续成功。
                result[Path(filename).stem] = {"mineru_error": str(exc)}
                continue
            result[Path(filename).stem] = ocr_result_to_payload(projected)
        return result

    def _get_extension(self, mime_type: str) -> str:
        return mime_to_extension(mime_type) or ".pdf"

    def _build_ocr_result(
        self,
        api_result: dict[str, Any],
        filename: str,
        data: bytes | None = None,
    ) -> OCRResult:
        """从 API 响应构建 OCRResult"""
        stem = Path(filename).stem
        results = api_result.get("results", {})
        file_result = results.get(stem, {})

        md_content = file_result.get("md_content") or ""

        content_list_raw = file_result.get("content_list")
        content_list_parsed: list = []
        if content_list_raw:
            try:
                content_list_parsed = json.loads(content_list_raw)
            except (json.JSONDecodeError, TypeError):
                content_list_parsed = []

        table_sequence = 0
        page_groups = (
            content_list_parsed
            if content_list_parsed and isinstance(content_list_parsed[0], list)
            else [content_list_parsed]
        )
        for inferred_page_idx, page_blocks in enumerate(page_groups):
            for block_index, block in enumerate(page_blocks):
                if not isinstance(block, dict):
                    continue
                block.setdefault(
                    "block_id",
                    (f"mineru-{stem}-page-{inferred_page_idx}-block-{block_index}"),
                )
                if block.get("type") != "table":
                    continue
                content = block.get("content")
                nested_html = ""
                if isinstance(content, dict):
                    nested_html = str(
                        content.get("table_body")
                        or content.get("table_html")
                        or content.get("html")
                        or ""
                    )
                if nested_html and not (block.get("table_body") or block.get("html")):
                    block["table_body"] = nested_html
                if not (
                    isinstance(block.get("table"), dict)
                    or block.get("table_body")
                    or block.get("html")
                ):
                    block["source_type"] = "table"
                    block["type"] = "table_unparsed"
                    table_content = (
                        content.get("table_content")
                        if isinstance(content, dict)
                        else None
                    )
                    block["text"] = " ".join(
                        str(item.get("content") or "")
                        for item in (
                            table_content if isinstance(table_content, list) else []
                        )
                        if isinstance(item, dict) and item.get("content")
                    )
                    block["projection_warnings"] = [
                        (f"{block['block_id']}:structured-table-unsupported")
                    ]
                    continue
                page_idx = block.get("page_idx", inferred_page_idx)
                table_id = str(
                    block.get("block_id")
                    or block.get("table_id")
                    or (f"mineru-{stem}-page-{page_idx}-table-{table_sequence}")
                )
                source_html = str(block.get("table_body") or block.get("html") or "")
                canonical_input = dict(block)
                if source_html and not re.search(
                    r"<table\b", source_html, flags=re.IGNORECASE
                ):
                    canonical_input["table_body"] = f"<table>{source_html}</table>"
                    source = dict(block.get("source") or {})
                    source.setdefault("source_html", source_html)
                    canonical_input["source"] = source
                canonical_block = canonicalize_table_block(
                    canonical_input,
                    table_id=table_id,
                    pipeline="MinerU",
                )
                table_model = table_model_from_block(canonical_block)
                table_model = replace(
                    table_model,
                    provenance=TableProvenanceV1(
                        pipeline="MinerU",
                        provider_schema="mineru-content-list",
                    ),
                )
                canonical_block["table"] = table_model.to_payload()
                page_blocks[block_index] = canonical_block
                table_sequence += 1

        # 通过正常化层统一格式
        normalized = normalize_content_list(content_list_parsed)
        for normalized_block in normalized:
            if normalized_block.get("type") != "table":
                continue
            raw_block = normalized_block.get("raw")
            if isinstance(raw_block, dict) and isinstance(raw_block.get("table"), dict):
                table_model = table_model_from_block(raw_block)
                normalized_block["text"] = table_model_to_plain_text(table_model)

        flat_content_list: list[dict[str, Any]] = []
        is_v2_content = bool(
            content_list_parsed and isinstance(content_list_parsed[0], list)
        )
        for normalized_block in normalized:
            raw_block = normalized_block.get("raw")
            if not isinstance(raw_block, dict):
                continue
            flat_block = dict(raw_block)
            if is_v2_content:
                # V2 使用 page_header/page_footer 等 provider 类型；投影层只识别
                # 归一化后的 header/footer，因此扁平化时携带统一语义，避免页眉泄漏。
                flat_block["type"] = normalized_block.get(
                    "type", flat_block.get("type")
                )
                flat_block["text"] = normalized_block.get("text", "")
            elif flat_block.get("type") == "text" and isinstance(
                flat_block.get("text_level"), int
            ):
                # MinerU legacy 标题以 text + text_level 表示。
                flat_block["type"] = "title"
                flat_block["level"] = flat_block["text_level"]
            flat_block.setdefault("page_idx", normalized_block.get("page_idx"))
            flat_content_list.append(flat_block)

        images: dict[str, bytes] = {}
        images_dict = file_result.get("images", {})
        for img_name, data_uri in images_dict.items():
            if data_uri and data_uri.startswith("data:"):
                b64_part = data_uri.split(",", 1)[-1]
                images[img_name] = base64.b64decode(b64_part)

        # 从 normalized 提取纯文本
        raw_text = (
            self._extract_plain_text_normalized(normalized)
            if normalized
            else md_content
        )

        # 从 normalized 构建 text_blocks
        text_blocks: list[TextBlock] = []
        for i, block in enumerate(normalized):
            block_type = block.get("type", "")
            if block_type in DISCARDED_BLOCK_TYPES:
                continue
            bbox = block.get("bbox")
            if (not bbox or len(bbox) < 4) and block_type != "table":
                continue
            text = block.get("text", "")
            if not text:
                continue

            text_blocks.append(
                TextBlock(
                    text=text,
                    score=None,  # MineRU content_list 不提供 confidence（真实 unknown，不伪造）
                    bbox=normalize_bbox(bbox[:4]) if bbox else None,
                    page_idx=block.get("page_idx"),
                    content_index=i,
                    content_id=(block.get("raw") or {}).get("block_id"),
                )
            )

        text_with_scores = [(b.text, b.score) for b in text_blocks]
        known_scores = [s for _, s in text_with_scores if s is not None]
        avg_score = sum(known_scores) / len(known_scores) if known_scores else 0.0

        result = OCRResult(
            raw_text=raw_text,
            markdown_text=md_content,
            html_text="",
            text_with_scores=text_with_scores,
            avg_score=avg_score,
            low_confidence_items=[],
            pipeline_type="MinerU",
            images=images,
            content_list=flat_content_list,
            text_blocks=text_blocks,
        )
        rebuild_result_projections(result)
        return result

    @staticmethod
    def _strip_html(html: str) -> str:
        """从 HTML 中提取纯文本，合并多余空白"""
        text = re.sub(r"<[^>]+>", " ", html)
        return re.sub(r"\s+", " ", text).strip()

    @staticmethod
    def _extract_plain_text_normalized(normalized: list[dict]) -> str:
        """从 normalize_content_list 的输出提取纯文本"""
        parts: list[str] = []
        for block in normalized:
            block_type = block.get("type", "")
            if block_type in DISCARDED_BLOCK_TYPES:
                continue
            text = block.get("text", "")
            if text:
                parts.append(text)
        return "\n".join(parts)

    @staticmethod
    def _extract_plain_text(content_list: list[dict]) -> str:
        """从 content_list 提取纯文本"""
        parts: list[str] = []
        for block in content_list:
            block_type = block.get("type", "")
            if block_type in DISCARDED_BLOCK_TYPES:
                continue
            if block_type == "table":
                captions = block.get("table_caption") or []
                if captions:
                    parts.append(" ".join(captions))
                html = block.get("table_body", "")
                parts.append(MinerUService._strip_html(html))
            elif block_type in ("image", "chart"):
                captions = (
                    block.get("image_caption") or block.get("chart_caption") or []
                )
                if captions:
                    parts.append(" ".join(captions))
            elif block_type == "list":
                items = block.get("list_items", [])
                parts.extend(items)
            elif block_type == "code":
                body = block.get("code_body", "")
                parts.append(body)
            else:
                text = block.get("text", "")
                if text:
                    parts.append(text)
        return "\n".join(p for p in parts if p)

    @staticmethod
    def _extract_block_text(block: dict) -> str:
        """从单个 content_list 块提取用于 TextBlock 的文本"""
        block_type = block.get("type", "")
        if block_type == "table":
            captions = block.get("table_caption") or []
            cap_text = " ".join(captions)
            html = block.get("table_body", "")
            body_text = MinerUService._strip_html(html)
            if cap_text and body_text:
                return f"{cap_text}\n{body_text}"
            return cap_text or body_text
        if block_type in ("image", "chart"):
            captions = block.get("image_caption") or block.get("chart_caption") or []
            content = block.get("content", "")
            text = " ".join(captions)
            if content:
                text = f"{text} {content}".strip()
            return text or f"[{block_type}]"
        if block_type == "list":
            items = block.get("list_items", [])
            return "; ".join(items)
        if block_type == "code":
            return block.get("code_body", "")[:200]
        return block.get("text", "")

    @classmethod
    def _stop_api_locked(cls, *, owner: threading.Event | None = None) -> None:
        if owner is not None and cls._api_owner is not owner:
            return
        process, guard, reader = cls._api_process, cls._job_guard, cls._log_reader
        if guard is not None:
            guard.terminate_and_wait(timeout=10)
        if process is not None and process.poll() is None:
            process.terminate()
        if process is not None:
            process.wait(timeout=10)
        if reader is not None:
            reader.join(timeout=1)
            if reader.is_alive():
                raise TimeoutError("MinerU worker logs did not drain")
        if (
            process is not None
            and process.stderr is not None
            and not process.stderr.closed
        ):
            process.stderr.close()
        if guard is not None:
            guard.close()
        cls._api_process = cls._job_guard = cls._log_reader = cls._api_owner = None
        cls._api_url = ""
        cls._initialized = False

    @classmethod
    def shutdown_owned(cls, *, owner: threading.Event | None = None) -> None:
        """Termination owns only a short OS-resource lock, never the long parse lock."""
        cancel = owner if owner is not None else cls._startup_cancel
        if cancel is not None:
            cancel.set()
        with cls._ownership_lock:
            cls._stop_preparation(owner=owner)
            cls._stop_api_locked(owner=owner)

    def shutdown(self) -> None:
        self.shutdown_owned()

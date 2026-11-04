"""Flash 原生文档的真实 API 解析（真实 ``mineru.parser.api_server`` + SDK facade）。

与受控服务器套件互补：这里的服务是随包官方 ``api_server`` 子进程，走完整
上传/轮询/下载 Tasks 协议；flash 档是纯原生抽取，不应触发模型下载。
"""

from __future__ import annotations

import logging
import os
import socket
import subprocess
import sys
import threading
import time
from pathlib import Path
from typing import TYPE_CHECKING

import httpx
import pytest
from vibeocr.runtime.recognition.mineru_api import (
    MineruApiClient,
    MineruApiError,
    MineruDocument,
)
from vibeocr.runtime.recognition.mineru_result import project_document
from vibeocr.runtime_contracts import MineruConfig, MineruOcrMode, MineruTier

if TYPE_CHECKING:
    from collections.abc import Iterator

VERIFIABLE_TEXT = "VibeOCR flash readiness paragraph with verifiable token 0137a."
logger = logging.getLogger(__name__)


def _free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def _wait_healthy(base_url: str, *, deadline: float = 60.0) -> dict:
    started = time.monotonic()
    last_error: Exception | None = None
    while time.monotonic() - started < deadline:
        try:
            response = httpx.get(f"{base_url}/v1/health", timeout=3, trust_env=False)
            if response.status_code == 200:
                payload = response.json()
                if str(payload.get("version", "")).split(".")[0] == "4":
                    return payload
                last_error = RuntimeError(f"unexpected health payload: {payload}")
        except Exception as exc:  # noqa: BLE001 - 启动窗口内的连接失败是常态
            last_error = exc
        time.sleep(0.5)
    raise AssertionError(f"mineru api_server not healthy in {deadline}s: {last_error}")


def _stderr_tail(lines: list[str], limit: int = 30) -> str:
    return "\n".join(lines[-limit:])


@pytest.fixture()
def flash_api_server(
    tmp_path: Path,
) -> Iterator[tuple[str, list[str]]]:
    home = tmp_path / "mineru-home"
    home.mkdir(parents=True, exist_ok=True)
    # 与生产 MinerUService._local_environment 相同的最小 4.1 配置。
    (home / "config.yaml").write_text(
        "model:\n  small_backend: onnx\nvlm:\n  engine: llama-cpp\n", encoding="utf-8"
    )
    env = os.environ.copy()
    env.pop("PYTHONPATH", None)
    env["MINERU_HOME"] = str(home)
    env["MINERU_CONFIG"] = str(home / "config.yaml")
    port = _free_port()
    process = subprocess.Popen(
        [
            sys.executable,
            "-m",
            "mineru.parser.api_server",
            "--host",
            "127.0.0.1",
            "--port",
            str(port),
            "--tier",
            "flash",
        ],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
        env=env,
        # api_server 的 --upload-dir 缺省经 click.Path(resolve_path=True)
        # 解析为子进程 CWD（内容寻址 blobs 与 .mineru-service.lock 会落在
        # 那里）；把工作目录钉在测试 tmp，避免污染仓库根。
        cwd=str(tmp_path),
        creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
    )
    stderr_lines: list[str] = []

    def drain() -> None:
        assert process.stderr is not None
        for raw in iter(process.stderr.readline, b""):
            line = raw.decode("utf-8", errors="replace").rstrip()
            if line:
                stderr_lines.append(line)
                logger.debug("[mineru-api-server] %s", line)

    reader = threading.Thread(target=drain, daemon=True)
    reader.start()
    base_url = f"http://127.0.0.1:{port}"
    try:
        yield base_url, stderr_lines
    finally:
        process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=10)
        reader.join(timeout=2)


class TestFlashNativeApiServer:
    def test_real_docx_parse_through_official_api_server(
        self, flash_api_server, tmp_path
    ):
        from docx import Document

        base_url, stderr_lines = flash_api_server
        _wait_healthy(base_url)

        source = Document()
        source.add_paragraph(VERIFIABLE_TEXT)
        docx_path = tmp_path / "flash-readiness.docx"
        source.save(docx_path)

        result = MineruApiClient(base_url, timeout=180).parse(
            [("flash-readiness.docx", docx_path.read_bytes())],
            MineruConfig(tier=MineruTier.FLASH, ocr_mode=MineruOcrMode.TXT),
        )
        document = result["flash-readiness.docx"]
        assert not isinstance(document, MineruApiError), (
            f"{document}; server stderr:\n{_stderr_tail(stderr_lines)}"
        )
        assert isinstance(document, MineruDocument)
        assert document.middle_json["pages"], _stderr_tail(stderr_lines)
        blocks = [
            block
            for page in document.middle_json["pages"]
            for block in page.get("blocks", [])
        ]
        assert blocks, _stderr_tail(stderr_lines)
        projected = project_document(document)
        assert VERIFIABLE_TEXT in projected.raw_text, _stderr_tail(stderr_lines)

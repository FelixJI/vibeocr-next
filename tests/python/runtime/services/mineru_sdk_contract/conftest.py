"""真实 MinerU 4.1.3 SDK 合同测试夹具。

本 conftest 在模块级直接导入官方 SDK：装配环境（prepare_mineru_sdk_tests
创建的 build/mineru-sdk-contract-venv）缺包时必须 collection error 而非
skip——这些合同只在与 CI 相同的装配里才有意义。根环境直接显式指定本目录
时给出可行动的装配指引（testpaths 收集已被 services/conftest.py 门控）。
"""

from __future__ import annotations

import json
import os
import threading
import zipfile
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from io import BytesIO
from pathlib import Path
from typing import TYPE_CHECKING

try:
    import mineru.parser.api_client  # noqa: F401 - 缺 SDK 必须 fail，不允许 skip
except ImportError:
    if os.environ.get("VIBEOCR_MINERU_SDK_CONTRACTS") != "1":
        raise ImportError(
            "MinerU SDK contract tests require the assembled venv: run "
            "scripts/prepare_mineru_sdk_tests.py (or scripts/check_quality.py) "
            "and execute with build/mineru-sdk-contract-venv and "
            "VIBEOCR_MINERU_SDK_CONTRACTS=1"
        ) from None
    raise

import pytest

if TYPE_CHECKING:
    from collections.abc import Callable, Iterator

FIXTURES = Path(__file__).resolve().parents[1] / "fixtures" / "mineru4"

# 受控服务器默认返回的最小 middle_json（schema docvortex.middle/2.0；
# pdf 布局块的 bbox 是 schema 必填项）。
EMPTY_MIDDLE: dict = {
    "schema": "docvortex.middle",
    "schema_version": "2.0",
    "metadata": {
        "file_suffix": "pdf",
        "producer": {"name": "mineru", "version": "4.1.3"},
    },
    "is_full_document": True,
    "pages": [
        {
            "page_idx": 0,
            "blocks": [
                {
                    "type": "text",
                    "index": 0,
                    "bbox": [0.1, 0.1, 0.9, 0.2],
                    "content": [{"type": "text", "content": "controlled server text"}],
                }
            ],
        }
    ],
}

# 1x1 灰度 JPEG（合法 JFIF 字节，供 zip sidecar 使用）。
TINY_JPEG = bytes.fromhex("ffd8ffe000104a46494600010100000100010000ffdb004300ffd9")


class TasksServer:
    """实现官方 SDK Tasks 协议的受控自部署服务器假件。

    记录全部 (method, path) 调用、创建请求体、上传字节与 Authorization 头；
    通过行为开关构造成功、扣留轮询、单文件失败、创建失败与取消端点等场景。
    每个 SDK 任务只承载一个文件（``parse_async`` 逐文件建任务），任务行为按
    file_name 查表。
    """

    def __init__(self, *, middle: dict | None = None) -> None:
        self.calls: list[tuple[str, str]] = []
        self.create_requests: list[dict] = []
        self.authorization_headers: list[str | None] = []
        self.uploaded: dict[str, bytes] = {}
        self.uploaded_events: dict[str, threading.Event] = {}
        self.cancel_calls: list[str] = []
        # 行为开关
        self.create_status = 200
        self.create_body: dict | None = None
        self.fail_files: dict[str, tuple[str, str]] = {}  # name -> (code, message)
        self.hold_running: set[str] = set()
        self.cancel_status = 200
        self.middle = json.loads(json.dumps(middle or EMPTY_MIDDLE))
        # 结果 zip 的附加素材（路径 -> 字节）；omit_sidecars 时跳过全部素材。
        self.sidecars: dict[str, bytes] = {}
        self.omit_sidecars = False
        # task_id -> {task_id, file_id, file_name, bytes, tier}
        self._tasks: dict[str, dict] = {}
        self._lock = threading.Lock()
        self._server: ThreadingHTTPServer | None = None
        self._thread: threading.Thread | None = None

    # -- 生命周期 -----------------------------------------------------------

    def start(self) -> "TasksServer":
        server = ThreadingHTTPServer(("127.0.0.1", 0), self._handler())
        self._server = server
        self._thread = threading.Thread(target=server.serve_forever, daemon=True)
        self._thread.start()
        return self

    def close(self) -> None:
        if self._server is not None:
            self._server.shutdown()
            self._server.server_close()
        if self._thread is not None:
            self._thread.join(timeout=5)

    @property
    def base_url(self) -> str:
        assert self._server is not None, "server not started"
        return f"http://127.0.0.1:{self._server.server_port}"

    # -- 观测辅助 -----------------------------------------------------------

    def calls_matching(self, fragment: str) -> list[tuple[str, str]]:
        return [call for call in self.calls if fragment in call[1]]

    def wait_for_upload(self, file_name: str, timeout: float = 10.0) -> None:
        event = self.uploaded_events.get(file_name)
        if event is not None:
            assert event.wait(timeout), f"upload of {file_name} never observed"

    def result_zip(self, task: dict) -> bytes:
        buffer = BytesIO()
        with zipfile.ZipFile(buffer, "w") as archive:
            archive.writestr("middle_json.json", json.dumps(self.middle))
            if not self.omit_sidecars:
                for path, payload in self.sidecars.items():
                    archive.writestr(path, payload)
        return buffer.getvalue()

    # -- 协议实现 -----------------------------------------------------------

    def _handler(self) -> type[BaseHTTPRequestHandler]:
        server = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *_args: object) -> None:
                pass

            def _record(self, method: str) -> None:
                with server._lock:
                    server.calls.append((method, self.path))
                    server.authorization_headers.append(
                        self.headers.get("Authorization")
                    )

            def _send_json(self, payload: dict, status: int = 200) -> None:
                body = json.dumps(payload).encode("utf-8")
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def _send_bytes(self, payload: bytes) -> None:
                self.send_response(200)
                self.send_header("Content-Type", "application/zip")
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)

            def _read_body(self) -> bytes:
                length = int(self.headers.get("Content-Length", "0"))
                return self.rfile.read(length) if length else b""

            def do_POST(self) -> None:  # noqa: N802
                path = self.path
                self._record("POST")
                if path == "/v1/tasks":
                    self._create_task()
                elif path.startswith("/v1/tasks/") and path.endswith("/cancel"):
                    with server._lock:
                        server.cancel_calls.append(path)
                    self._cancel(path)
                else:
                    self._send_json({"error": {"code": "not_found"}}, 404)

            def _create_task(self) -> None:
                request = json.loads(self._read_body())
                with server._lock:
                    server.create_requests.append(request)
                if server.create_status != 200:
                    body = server.create_body or {"detail": "Not Found"}
                    self._send_json(body, server.create_status)
                    return
                item = request["files"][0]
                index = len(server._tasks) + 1
                task = {
                    "task_id": f"task-{index}",
                    "file_id": f"file-{index}",
                    "file_name": item["file_name"],
                    "bytes": item["bytes"],
                    "tier": request.get("tier") or "standard",
                }
                with server._lock:
                    server._tasks[task["task_id"]] = task
                    server.uploaded_events.setdefault(
                        task["file_name"], threading.Event()
                    )
                self._send_json(
                    {
                        "task_id": task["task_id"],
                        "status": "waiting-file",
                        "files": [
                            {
                                "file_id": task["file_id"],
                                "file_name": task["file_name"],
                                "status": "waiting-file",
                                "upload": {
                                    "url": (
                                        f"{server.base_url}/upload/{task['file_id']}"
                                    ),
                                    "headers": {},
                                    "expires_at": 9999999999,
                                },
                            }
                        ],
                    }
                )

            def _cancel(self, path: str) -> None:
                task_id = path.removeprefix("/v1/tasks/").removesuffix("/cancel")
                with server._lock:
                    known = task_id in server._tasks
                if not known:
                    self._send_json({"detail": "Not Found"}, 404)
                    return
                if server.cancel_status == 200:
                    task = server._tasks[task_id]
                    self._send_json(
                        {
                            "task_id": task_id,
                            "status": "canceled",
                            "finished_at": "2026-01-01T00:00:00Z",
                            "files": [
                                {
                                    "file_id": task["file_id"],
                                    "file_name": task["file_name"],
                                    "status": "canceled",
                                }
                            ],
                        }
                    )
                    return
                if server.cancel_status == 409:
                    self._send_json({"error": {"code": "task_already_terminal"}}, 409)
                    return
                self._send_json(
                    {"error": {"code": "internal_error", "message": "boom"}},
                    server.cancel_status,
                )

            def do_PUT(self) -> None:  # noqa: N802
                path = self.path
                self._record("PUT")
                if not path.startswith("/upload/"):
                    self._send_json({"error": {"code": "not_found"}}, 404)
                    return
                file_id = path.removeprefix("/upload/")
                body = self._read_body()
                with server._lock:
                    task = next(
                        (
                            item
                            for item in server._tasks.values()
                            if item["file_id"] == file_id
                        ),
                        None,
                    )
                    if task is None:
                        self._send_json({"error": {"code": "not_found"}}, 404)
                        return
                    server.uploaded[file_id] = body
                self._send_json(
                    {
                        "task_id": task["task_id"],
                        "file_id": file_id,
                        "status": "pending",
                    }
                )
                event = server.uploaded_events.get(task["file_name"])
                if event is not None:
                    event.set()

            def do_GET(self) -> None:  # noqa: N802
                path = self.path
                self._record("GET")
                if path.startswith("/v1/tasks/"):
                    self._task_detail(path.removeprefix("/v1/tasks/"))
                elif path.startswith("/result/"):
                    task_id = path.removeprefix("/result/")
                    with server._lock:
                        task = server._tasks.get(task_id)
                    if task is None:
                        self._send_json({"error": {"code": "not_found"}}, 404)
                        return
                    self._send_bytes(server.result_zip(task))
                else:
                    self._send_json({"error": {"code": "not_found"}}, 404)

            def _task_detail(self, task_id: str) -> None:
                with server._lock:
                    task = server._tasks.get(task_id)
                    uploaded = task is not None and task["file_id"] in server.uploaded
                if task is None:
                    self._send_json({"error": {"code": "not_found"}}, 404)
                    return
                if not uploaded:
                    # 上传前详情：SDK 强制要求 waiting-file + upload 链接。
                    self._send_json(
                        {
                            "task_id": task_id,
                            "status": "waiting-file",
                            "created_at": "2026-01-01T00:00:00Z",
                            "output_formats": ["default"],
                            "progress": {"completed": 0, "failed": 0, "total": 1},
                            "files": [
                                {
                                    "file_id": task["file_id"],
                                    "file_name": task["file_name"],
                                    "status": "waiting-file",
                                    "bytes": task["bytes"],
                                    "upload": {
                                        "url": (
                                            f"{server.base_url}/upload/"
                                            f"{task['file_id']}"
                                        ),
                                        "headers": {},
                                        "expires_at": 9999999999,
                                    },
                                }
                            ],
                        }
                    )
                    return
                failure = server.fail_files.get(task["file_name"])
                if failure is not None:
                    code, message = failure
                    self._send_json(
                        {
                            "task_id": task_id,
                            "status": "failed",
                            "created_at": "2026-01-01T00:00:00Z",
                            "output_formats": ["default"],
                            "progress": {"completed": 0, "failed": 1, "total": 1},
                            "files": [
                                {
                                    "file_id": task["file_id"],
                                    "file_name": task["file_name"],
                                    "status": "failed",
                                    "bytes": task["bytes"],
                                    "error": {"code": code, "message": message},
                                }
                            ],
                        }
                    )
                    return
                if task["file_name"] in server.hold_running:
                    self._send_json(
                        {
                            "task_id": task_id,
                            "status": "running",
                            "created_at": "2026-01-01T00:00:00Z",
                            "output_formats": ["default"],
                            "progress": {"completed": 0, "failed": 0, "total": 1},
                            "files": [
                                {
                                    "file_id": task["file_id"],
                                    "file_name": task["file_name"],
                                    "status": "running",
                                    "bytes": task["bytes"],
                                }
                            ],
                        }
                    )
                    return
                self._send_json(
                    {
                        "task_id": task_id,
                        "status": "completed",
                        "created_at": "2026-01-01T00:00:00Z",
                        "output_formats": ["default"],
                        "progress": {"completed": 1, "failed": 0, "total": 1},
                        "files": [
                            {
                                "file_id": task["file_id"],
                                "file_name": task["file_name"],
                                "status": "completed",
                                "bytes": task["bytes"],
                                "parse": {
                                    "tier": task["tier"],
                                    "duration_ms": 10,
                                },
                                "result_url": f"{server.base_url}/result/{task_id}",
                            }
                        ],
                    }
                )

        return Handler


@pytest.fixture()
def tasks_server() -> Iterator[TasksServer]:
    server = TasksServer().start()
    try:
        yield server
    finally:
        server.close()


@pytest.fixture()
def tiny_jpeg() -> bytes:
    return TINY_JPEG


@pytest.fixture()
def image_middle() -> Iterator[Callable[[str], dict]]:
    yield _image_middle


def _image_middle(middle_path: str) -> dict:
    """构造引用 zip sidecar 的 table-image middle_json（真实 schema/2.0）。

    基于 fixtures/mineru4/table-image-middle.json，把内联 image_base64 换成
    ``image_path`` 引用，配合服务器 sidecars/omit_sidecars 覆盖素材存在与
    缺失两条路径。
    """

    middle = json.loads(
        (FIXTURES / "table-image-middle.json").read_text(encoding="utf-8")
    )
    block = middle["pages"][0]["blocks"][3]["content"][0]
    del block["image_base64"]
    block["image_path"] = middle_path
    return middle

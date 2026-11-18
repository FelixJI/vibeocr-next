"""Tests for the supervisor PDF v2 routes (plan §6).

Drives the real FastAPI app over httpx ASGI transport with a fake
``pdf_adapter`` injected via the shared ``pdf_module`` fixture in conftest.
Covers: open/close/model/load stream/render thumbnail+preview/detect/mutate/
text-layer/save/cancel/reset_cancel, plus 503 when no adapter is wired and
400 on bad JSON.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

import httpx
import pytest

# pytest puts conftest.py on sys.path for each test session, so importing the
# shared NullExecutor from it is safe at runtime.
from conftest import NullExecutor
from vibeocr.runtime.host.app import create_app
from vibeocr.runtime.host.bootstrap import new_instance_id
from vibeocr.runtime.jobs.module import SupervisorModule, SupervisorOptions

if TYPE_CHECKING:
    from pathlib import Path

    from conftest import FakePdfAdapter
    from fastapi import FastAPI


def _http(token: str, app) -> httpx.AsyncClient:
    """Build a raw httpx client over ASGI transport with the bearer token."""
    return httpx.AsyncClient(
        transport=httpx.ASGITransport(app=app),
        base_url="http://127.0.0.1",
        headers={"Authorization": f"Bearer {token}"},
    )


# ---------------------------------------------------------------------------
# 503 when no adapter
# ---------------------------------------------------------------------------


async def test_pdf_route_returns_internal_error_when_no_adapter(
    tmp_path: Path, supervisor_token: str
) -> None:
    opts = SupervisorOptions(instance_id=new_instance_id())
    module = SupervisorModule(
        options=opts,
        stager_root=tmp_path / "staging",
        executor=NullExecutor(),
        pdf_adapter=None,
    )
    app = create_app(module, supervisor_token)
    async with _http(supervisor_token, app) as http:
        resp = await http.post("/v2/pdf/sessions/open", json={"path": "x.pdf"})
    assert resp.status_code == 500
    body = resp.json()
    assert body["code"] == "INTERNAL_ERROR"


# ---------------------------------------------------------------------------
# Lifecycle: open / close / model
# ---------------------------------------------------------------------------


async def test_open_returns_session_id_and_model(
    pdf_app: FastAPI, supervisor_token: str, fake_pdf_adapter: FakePdfAdapter
) -> None:
    async with _http(supervisor_token, pdf_app) as http:
        resp = await http.post("/v2/pdf/sessions/open", json={"path": "doc.pdf"})
    assert resp.status_code == 200
    body = resp.json()
    assert body["session_id"] == "sid-1"
    assert body["schema_version"] == 2
    assert body["model"]["file_path"] == "doc.pdf"
    assert fake_pdf_adapter.calls[-1][0] == "open_session"


async def test_close_proxies_and_returns_closed_flag(
    pdf_app: FastAPI, supervisor_token: str
) -> None:
    async with _http(supervisor_token, pdf_app) as http:
        resp = await http.post("/v2/pdf/sessions/sid-1/close")
    assert resp.status_code == 200
    assert resp.json()["closed"] is True


async def test_model_returns_full_mirror(
    pdf_app: FastAPI, supervisor_token: str
) -> None:
    async with _http(supervisor_token, pdf_app) as http:
        resp = await http.post("/v2/pdf/sessions/sid-1/model")
    assert resp.status_code == 200
    assert resp.json()["pages"] == []


# ---------------------------------------------------------------------------
# Streaming load / delete_text_layers
# ---------------------------------------------------------------------------


async def test_load_streams_ndjson_progress_events(
    pdf_app: FastAPI, supervisor_token: str
) -> None:
    async with _http(supervisor_token, pdf_app) as http:
        resp = await http.post("/v2/pdf/sessions/sid-1/load")
    assert resp.status_code == 200
    lines = [ln for ln in resp.text.splitlines() if ln]
    assert len(lines) == 1
    import json

    event = json.loads(lines[0])
    assert event["phase"] == "load"
    assert event["message"] == "done"


async def test_prefetch_presence_rejects_invalid_combinations_before_streaming(
    pdf_app: FastAPI, supervisor_token: str, fake_pdf_adapter: FakePdfAdapter
) -> None:
    path = "/v2/pdf/sessions/sid-1/load"
    background = {"scope": "background", "epoch": 1}
    async with _http(supervisor_token, pdf_app) as http:
        health = await http.get("/v2/health")
        assert "pdf.prefetch-presence.v1" in health.json()["capabilities"]
        for invalid in (
            {"read": background, "prefetch": True},
            {"read": background, "pages": [], "prefetch": True},
            {"read": background, "pages": [0, 1, 2, 3, 4], "prefetch": True},
            {"read": background, "pages": [0], "prefetch": "true"},
            {
                "read": {"scope": "viewport", "epoch": 1},
                "pages": [0],
                "prefetch": True,
            },
        ):
            response = await http.post(path, json=invalid)
            assert response.status_code == 400
            assert response.json()["code"] == "VALIDATION_ERROR"
        assert not fake_pdf_adapter.calls

        for payload in (
            {"read": background, "pages": [0, 4], "prefetch": True},
            {"read": background, "pages": [0, 1, 2, 3, 4], "prefetch": False},
            {"read": background, "pages": [0]},
        ):
            response = await http.post(path, json=payload)
            assert response.status_code == 200
            assert '"message":"done"' in response.text
            assert fake_pdf_adapter.calls[-1] == (
                "load_demand_stream",
                ("sid-1", payload),
                {},
            )


async def test_delete_text_layers_streams_ndjson(
    pdf_app: FastAPI, supervisor_token: str
) -> None:
    async with _http(supervisor_token, pdf_app) as http:
        resp = await http.post(
            "/v2/pdf/sessions/sid-1/delete_text_layers", json={"pages": [0, 1]}
        )
    assert resp.status_code == 200
    lines = [ln for ln in resp.text.splitlines() if ln]
    assert len(lines) == 1
    import json

    event = json.loads(lines[0])
    assert event["phase"] == "delete"


# ---------------------------------------------------------------------------
# Render (binary PNG)
# ---------------------------------------------------------------------------


async def test_render_thumbnail_returns_png_bytes(
    pdf_app: FastAPI, supervisor_token: str, fake_pdf_adapter: FakePdfAdapter
) -> None:
    async with _http(supervisor_token, pdf_app) as http:
        resp = await http.post(
            "/v2/pdf/sessions/sid-1/render_thumbnail",
            json={"page": 0, "size": 128},
        )
    assert resp.status_code == 200
    assert resp.content.startswith(b"\x89PNG")
    name, _args, kwargs = fake_pdf_adapter.calls[-1]
    assert name == "render_thumbnail"
    assert kwargs == {"size": 128}


async def test_render_preview_returns_png_bytes_and_forwards_dpi(
    pdf_app: FastAPI, supervisor_token: str, fake_pdf_adapter: FakePdfAdapter
) -> None:
    async with _http(supervisor_token, pdf_app) as http:
        resp = await http.post(
            "/v2/pdf/sessions/sid-1/render_preview",
            json={"page": 2, "dpi": 300},
        )
    assert resp.status_code == 200
    assert resp.content.startswith(b"\x89PNG")
    name, _args, kwargs = fake_pdf_adapter.calls[-1]
    assert name == "render_preview"
    assert kwargs == {"dpi": 300}


async def test_render_get_route_returns_png_and_reads_query_params(
    pdf_app: FastAPI, supervisor_token: str, fake_pdf_adapter: FakePdfAdapter
) -> None:
    """The .NET client issues GET /render?page=&size= for quick previews."""
    async with _http(supervisor_token, pdf_app) as http:
        resp = await http.get(
            "/v2/pdf/sessions/sid-1/render", params={"page": 3, "size": 96}
        )
    assert resp.status_code == 200
    assert resp.content.startswith(b"\x89PNG")
    name, args, kwargs = fake_pdf_adapter.calls[-1]
    assert name == "render_thumbnail"
    assert args == ("sid-1", 3)
    assert kwargs == {"size": 96}


async def test_save_transactional_returns_path_and_calls_adapter(
    pdf_app: FastAPI, supervisor_token: str, fake_pdf_adapter: FakePdfAdapter
) -> None:
    async with _http(supervisor_token, pdf_app) as http:
        resp = await http.post(
            "/v2/pdf/sessions/sid-1/save_transactional",
            json={"path": "/tmp/out.pdf"},
        )
    assert resp.status_code == 200
    body = resp.json()
    assert body["path"] == "/tmp/out.pdf"
    name, args, _kwargs = fake_pdf_adapter.calls[-1]
    assert name == "save_transactional"
    assert args == ("sid-1", "/tmp/out.pdf")


async def test_save_transactional_rejects_missing_path(
    pdf_app: FastAPI, supervisor_token: str
) -> None:
    async with _http(supervisor_token, pdf_app) as http:
        resp = await http.post("/v2/pdf/sessions/sid-1/save_transactional", json={})
    assert resp.status_code == 400
    assert resp.json()["code"] == "VALIDATION_ERROR"


async def test_rotate_proxies_pages_and_angle(
    pdf_app: FastAPI, supervisor_token: str, fake_pdf_adapter: FakePdfAdapter
) -> None:
    async with _http(supervisor_token, pdf_app) as http:
        resp = await http.post(
            "/v2/pdf/sessions/sid-1/rotate", json={"pages": [0, 1], "angle": 90}
        )
    assert resp.status_code == 200
    name, args, _kwargs = fake_pdf_adapter.calls[-1]
    assert name == "rotate"
    assert args == ("sid-1", [0, 1], 90)


async def test_delete_pages_marks_structural_change(
    pdf_app: FastAPI, supervisor_token: str
) -> None:
    async with _http(supervisor_token, pdf_app) as http:
        resp = await http.post(
            "/v2/pdf/sessions/sid-1/delete_pages", json={"pages": [2]}
        )
    assert resp.status_code == 200
    assert resp.json()["diff"]["structural_change"] is True


@pytest.mark.parametrize(
    "op,body,expected_name",
    [
        (
            "insert_blank",
            {"after_index": 0, "width": 100.0, "height": 200.0},
            "insert_blank",
        ),
        ("insert_from", {"source_path": "src.pdf", "after_index": 0}, "insert_from"),
        ("move_page", {"from_index": 0, "to_index": 1}, "move_page"),
        ("reorder", {"new_order": [1, 0]}, "reorder"),
    ],
)
async def test_structural_mutations_proxy_body(
    pdf_app: FastAPI,
    supervisor_token: str,
    fake_pdf_adapter: FakePdfAdapter,
    op: str,
    body: dict,
    expected_name: str,
) -> None:
    async with _http(supervisor_token, pdf_app) as http:
        resp = await http.post(f"/v2/pdf/sessions/sid-1/{op}", json=body)
    assert resp.status_code == 200
    assert fake_pdf_adapter.calls[-1][0] == expected_name


# ---------------------------------------------------------------------------
# Text layer
# ---------------------------------------------------------------------------


async def test_add_text_layer_batch_returns_saved_extra(
    pdf_app: FastAPI, supervisor_token: str
) -> None:
    async with _http(supervisor_token, pdf_app) as http:
        resp = await http.post(
            "/v2/pdf/sessions/sid-1/add_text_layer_batch",
            json={
                "pages": [{"page": 0, "ocr_result": {"text": "hi"}}],
                "save": True,
            },
        )
    assert resp.status_code == 200
    assert resp.json()["extra"]["saved"] is True


async def test_update_block_text_proxies_text(
    pdf_app: FastAPI, supervisor_token: str, fake_pdf_adapter: FakePdfAdapter
) -> None:
    async with _http(supervisor_token, pdf_app) as http:
        resp = await http.post(
            "/v2/pdf/sessions/sid-1/update_block_text",
            json={
                "page": 0,
                "block_index": 1,
                "new_text": "edited",
                "expected_old_text": "old",
            },
        )
    assert resp.status_code == 200
    name, args, kwargs = fake_pdf_adapter.calls[-1]
    assert name == "update_block_text"
    assert args == ("sid-1", 0, 1, "edited")
    assert kwargs["expected_old_text"] == "old"


async def test_page_inspect_proxies_page(
    pdf_app: FastAPI, supervisor_token: str, fake_pdf_adapter: FakePdfAdapter
) -> None:
    async with _http(supervisor_token, pdf_app) as http:
        resp = await http.post(
            "/v2/pdf/sessions/sid-1/page_inspect",
            json={"page": 2},
        )
    assert resp.status_code == 200
    body = resp.json()
    assert body["page"] == 2
    assert body["schema_version"] == 2
    assert isinstance(body["instance_id"], str) and body["instance_id"]
    name, args, _kwargs = fake_pdf_adapter.calls[-1]
    assert name == "page_inspect"
    assert args == ("sid-1", 2)


# ---------------------------------------------------------------------------
# Save + cancel
# ---------------------------------------------------------------------------


async def test_save_returns_path_and_forwards_rewrite_flag(
    pdf_app: FastAPI, supervisor_token: str, fake_pdf_adapter: FakePdfAdapter
) -> None:
    async with _http(supervisor_token, pdf_app) as http:
        resp = await http.post(
            "/v2/pdf/sessions/sid-1/save",
            json={"path": "out.pdf", "rewrite_text_layers": False},
        )
    assert resp.status_code == 200
    assert resp.json()["path"] == "out.pdf"
    name, _args, kwargs = fake_pdf_adapter.calls[-1]
    assert name == "save"
    assert kwargs == {"rewrite_text_layers": False}


async def test_cancel_and_reset_cancel_toggle_state(
    pdf_app: FastAPI, supervisor_token: str, fake_pdf_adapter: FakePdfAdapter
) -> None:
    async with _http(supervisor_token, pdf_app) as http:
        await http.post("/v2/pdf/sessions/sid-1/cancel")
        await http.post("/v2/pdf/sessions/sid-1/reset_cancel")
    names = [c[0] for c in fake_pdf_adapter.calls]
    assert "cancel" in names
    assert "reset_cancel" in names


# ---------------------------------------------------------------------------
# Bad input
# ---------------------------------------------------------------------------


async def test_open_without_path_returns_validation_error(
    pdf_app: FastAPI, supervisor_token: str
) -> None:
    async with _http(supervisor_token, pdf_app) as http:
        resp = await http.post("/v2/pdf/sessions/open", json={})
    assert resp.status_code == 400
    assert resp.json()["code"] == "VALIDATION_ERROR"


async def test_open_with_invalid_json_returns_validation_error(
    pdf_app: FastAPI, supervisor_token: str
) -> None:
    async with _http(supervisor_token, pdf_app) as http:
        resp = await http.post(
            "/v2/pdf/sessions/open",
            content=b"not json",
            headers={"Authorization": f"Bearer {supervisor_token}"},
        )
    assert resp.status_code == 400


async def test_update_block_text_backend_rejection_maps_validation_error(
    pdf_app: FastAPI, supervisor_token: str, fake_pdf_adapter: FakePdfAdapter
) -> None:
    """后端 4xx（如编辑空文本/越界）映射为 ValidationError：可判定未提交。"""
    from vibeocr.runtime.documents.pdf_backend_client import PdfBackendError

    original = fake_pdf_adapter.update_block_text

    def _reject(*args: object, **kwargs: object) -> object:
        raise PdfBackendError("后端错误 x (422): 新文本不能为空", status=422)

    fake_pdf_adapter.update_block_text = _reject  # type: ignore[method-assign]
    try:
        async with _http(supervisor_token, pdf_app) as http:
            resp = await http.post(
                "/v2/pdf/sessions/sid-1/update_block_text",
                json={"page": 0, "block_index": 0, "new_text": "  "},
            )
    finally:
        fake_pdf_adapter.update_block_text = original  # type: ignore[method-assign]
    assert resp.status_code == 400
    assert resp.json()["code"] == "VALIDATION_ERROR"


@pytest.mark.parametrize(
    "backend_status,expected_http,expected_code,expected_category,detail_key",
    [
        (400, 400, "VALIDATION_ERROR", "validation", "reason"),
        (422, 400, "VALIDATION_ERROR", "validation", "reason"),
        (404, 404, "RESOURCE_NOT_FOUND", "not_found", "reason"),
        # 409 只有 session closing / stale expected text 内部来源，无通用 409
        # registry 码：不伪造 RUNTIME_BUSY/JOB 码，保守 INTERNAL_ERROR。
        (409, 500, "INTERNAL_ERROR", "internal", "reason"),
        (423, 500, "INTERNAL_ERROR", "internal", "reason"),
        (500, 500, "INTERNAL_ERROR", "internal", "error"),
    ],
)
async def test_pdf_error_maps_backend_status_distinctly(
    pdf_app: FastAPI,
    supervisor_token: str,
    fake_pdf_adapter: FakePdfAdapter,
    backend_status: int,
    expected_http: int,
    expected_code: str,
    expected_category: str,
    detail_key: str,
) -> None:
    """后端 4xx/5xx 逐状态映射 v2 错误码：只有真 400/422 才是
    VALIDATION_ERROR；404 会话/资源丢失必须 RESOURCE_NOT_FOUND（后端崩溃
    重启后旧 session 不得被误报为可修正参数错误）；其余保守 INTERNAL_ERROR。
    全部响应携带 v2 错误信封（schema_version/instance_id/code/message/Category）。"""
    from vibeocr.runtime.documents.pdf_backend_client import PdfBackendError

    message = f"后端错误 x ({backend_status})"
    original = fake_pdf_adapter.get_model

    def _reject(*args: object, **kwargs: object) -> object:
        raise PdfBackendError(message, status=backend_status)

    fake_pdf_adapter.get_model = _reject  # type: ignore[method-assign]
    try:
        async with _http(supervisor_token, pdf_app) as http:
            resp = await http.post("/v2/pdf/sessions/sid-1/model")
    finally:
        fake_pdf_adapter.get_model = original  # type: ignore[method-assign]
    assert resp.status_code == expected_http
    body = resp.json()
    assert body["schema_version"] == 2
    assert isinstance(body["instance_id"], str) and body["instance_id"]
    assert body["code"] == expected_code
    assert body["category"] == expected_category
    assert isinstance(body["message"], str) and body["message"]
    assert body["detail"][detail_key] == message


async def test_pdf_error_backend_error_without_status_keeps_internal_error(
    pdf_app: FastAPI,
    supervisor_token: str,
    fake_pdf_adapter: FakePdfAdapter,
) -> None:
    """无 status 的 PdfBackendError（子进程启动/传输失败）保持 INTERNAL_ERROR 兜底。"""
    from vibeocr.runtime.documents.pdf_backend_client import PdfBackendError

    original = fake_pdf_adapter.close_session

    def _reject(*args: object, **kwargs: object) -> object:
        raise PdfBackendError("transport failed")

    fake_pdf_adapter.close_session = _reject  # type: ignore[method-assign]
    try:
        async with _http(supervisor_token, pdf_app) as http:
            resp = await http.post("/v2/pdf/sessions/sid-1/close")
    finally:
        fake_pdf_adapter.close_session = original  # type: ignore[method-assign]
    assert resp.status_code == 500
    assert resp.json()["code"] == "INTERNAL_ERROR"


@pytest.mark.parametrize(
    "fields",
    [
        {"copy_export": True, "overwrite": False, "rebind_target": False},
        {"copy_export": False, "overwrite": True, "rebind_target": True},
    ],
)
async def test_save_forwards_negotiated_workspace_semantics(
    pdf_app, supervisor_token, fake_pdf_adapter, fields
):
    async with _http(supervisor_token, pdf_app) as http:
        response = await http.post(
            "/v2/pdf/sessions/sid-1/save",
            json={"path": "copy.pdf", "rewrite_text_layers": False, **fields},
        )
    assert response.status_code == 200
    assert response.json()["schema_version"] == 2
    assert response.json()["path"] == "copy.pdf"
    _, _, kwargs = fake_pdf_adapter.calls[-1]
    assert kwargs == {"rewrite_text_layers": False, **fields}


@pytest.mark.parametrize(
    "mode,phase",
    [
        ("overwrite", "stage"),
        ("rebind", "stage"),
        ("copy", "stage"),
        ("overwrite", "replace"),
        ("overwrite", "reopen"),
        ("rebind", "reopen"),
        ("overwrite", "lost"),
        ("overwrite", "unavailable"),
    ],
)
async def test_real_worker_client_host_save_outcome_and_retry(
    pdf_app, pdf_module, supervisor_token, tmp_path, monkeypatch, mode, phase
):
    from pathlib import Path

    import pymupdf as fitz
    from fastapi.testclient import TestClient
    from vibeocr.runtime.documents import pdf_backend_process as worker
    from vibeocr.runtime.documents.adapter import PdfProcessAdapter
    from vibeocr.runtime.documents.pdf_backend_client import PdfBackendClient
    from vibeocr.runtime.documents.pdf_service import PdfService

    source = tmp_path / "source.pdf"
    with fitz.open() as seed:
        seed.new_page().insert_text((72, 72), "ORIGINAL")
        seed.save(source)
    doc, model = PdfService.open_doc(str(source))
    doc[0].insert_text((72, 110), "UNSAVED")
    model.is_modified = True
    registry = worker.SessionRegistry()
    session = worker.BackendSession("real", str(source), doc, model)
    registry._sessions["real"] = session
    monkeypatch.setattr(worker, "_get_registry", lambda: registry)
    target = source if mode == "overwrite" else tmp_path / "target.pdf"
    payload = {
        "path": str(target),
        "rewrite_text_layers": False,
        "copy_export": mode == "copy",
        "rebind_target": mode == "rebind",
    }
    committed = phase in ("reopen", "unavailable")
    usable = phase not in ("lost", "unavailable")
    real_save, real_replace, real_open = fitz.Document.save, Path.replace, fitz.open

    def fail_save(self, *args, **kwargs):
        if kwargs.get("garbage") == 3:
            raise OSError("stage failure")
        return real_save(self, *args, **kwargs)

    def fail_replace(self, destination):
        if Path(destination) == target:
            raise OSError("replace failure")
        return real_replace(self, destination)

    def fail_reopen(*args, **kwargs):
        if args and Path(args[0]) == target:
            raise OSError("reopen failure")
        return real_open(*args, **kwargs)

    with TestClient(worker.app) as worker_http:
        child = PdfBackendClient()
        monkeypatch.setattr(child, "_ensure_started", lambda: worker_http)
        pdf_module.pdf_adapter = PdfProcessAdapter(lambda: child, _child=child)
        async with _http(supervisor_token, pdf_app) as http:
            with monkeypatch.context() as fault:
                if phase == "stage":
                    fault.setattr(fitz.Document, "save", fail_save)
                elif phase == "replace":
                    fault.setattr(Path, "replace", fail_replace)
                elif phase in ("lost", "unavailable"):
                    if phase == "lost":
                        fault.setattr(Path, "replace", fail_replace)
                    fault.setattr(
                        fitz,
                        "open",
                        lambda *a, **k: (_ for _ in ()).throw(
                            OSError("recovery unavailable")
                        ),
                    )
                else:
                    fault.setattr(fitz, "open", fail_reopen)
                response = await http.post("/v2/pdf/sessions/real/save", json=payload)
            assert response.status_code == 500
            detail = response.json()["detail"]
            assert detail["reason_code"] == "pdf_save_outcome"
            assert detail["committed"] is committed
            assert detail["session_usable"] is usable
            assert detail["path"] == str(target)
            assert model.is_modified is not committed
            assert model.file_path == str(
                target if committed and mode == "rebind" else source
            )
            if not usable:
                assert session.doc.is_closed
                with real_open(detail["recovery_path"]) as recovered_doc:
                    assert "UNSAVED" in recovered_doc[0].get_text()
                return
            assert "UNSAVED" in session.doc[0].get_text()
            recovered = tmp_path / "retry.pdf"
            response = await http.post(
                "/v2/pdf/sessions/real/save",
                json={
                    "path": str(recovered),
                    "rewrite_text_layers": False,
                    "rebind_target": True,
                },
            )
            assert response.status_code == 200
            with real_open(recovered) as disk:
                assert "UNSAVED" in disk[0].get_text()
        session.doc.close()


@pytest.mark.parametrize(
    "patch",
    [
        {"committed": "false"},
        {"session_usable": 1},
        {"path": "other.pdf"},
        {"recovery_path": None},
        {"reason_code": "unknown"},
    ],
)
async def test_malformed_worker_outcome_is_not_promoted(
    pdf_app, pdf_module, supervisor_token, tmp_path, monkeypatch, patch
):
    from vibeocr.runtime.documents.adapter import PdfProcessAdapter
    from vibeocr.runtime.documents.pdf_backend_client import PdfBackendClient

    target = str(tmp_path / "target.pdf")
    detail = {
        "reason_code": "pdf_save_outcome",
        "committed": False,
        "session_usable": True,
        "path": target,
        **patch,
    }
    if patch.get("path") == "other.pdf":
        detail["path"] = str(tmp_path / "other.pdf")
    with httpx.Client(
        transport=httpx.MockTransport(
            lambda request: httpx.Response(500, json={"detail": detail})
        ),
        base_url="http://127.0.0.1",
    ) as worker_http:
        child = PdfBackendClient()
        monkeypatch.setattr(child, "_ensure_started", lambda: worker_http)
        pdf_module.pdf_adapter = PdfProcessAdapter(lambda: child, _child=child)
        async with _http(supervisor_token, pdf_app) as http:
            response = await http.post(
                "/v2/pdf/sessions/real/save",
                json={"path": target, "rewrite_text_layers": False},
            )
        assert response.status_code == 500
        assert "reason_code" not in response.json()["detail"]


async def test_pdf_demand_round_trip_with_real_worker(
    pdf_app, pdf_module, supervisor_token, tmp_path
):
    import io
    import json

    import pymupdf as fitz
    from PIL import Image
    from vibeocr.runtime.documents.adapter import PdfProcessAdapter
    from vibeocr.runtime.documents.pdf_backend_client import PdfBackendClient

    source = tmp_path / "demand.pdf"
    with fitz.open() as doc:
        page = doc.new_page(width=200, height=400)
        page.insert_text((20, 20), "searchable")
        doc.save(source)
    child = PdfBackendClient()
    pdf_module.pdf_adapter = PdfProcessAdapter(lambda: child, _child=child)
    try:
        async with _http(supervisor_token, pdf_app) as http:
            opened = await http.post(
                "/v2/pdf/sessions/open", json={"path": str(source)}
            )
            assert opened.status_code == 200, opened.text
            path = "/v2/pdf/sessions/" + opened.json()["session_id"]

            read = {"scope": "viewport", "epoch": 2**32 + 1}
            scoped = await http.post(f"{path}/load", json={"read": read, "pages": [0]})
            assert scoped.status_code == 200, scoped.text
            event, terminal = [json.loads(line) for line in scoped.text.splitlines()]
            assert event["page_payload"]["has_text_layer"] is True
            assert event["page_payload"]["scan_checked"] is False
            assert terminal["message"] == "done"

            thumbnail = await http.post(
                f"{path}/render_thumbnail", json={"page": 0, "size": 100, "read": read}
            )
            assert thumbnail.status_code == 200, thumbnail.text
            with Image.open(io.BytesIO(thumbnail.content)) as image:
                assert image.size == (50, 100)

            preview = await http.post(
                f"{path}/render_preview",
                json={"page": 0, "max_edge": 512, "read": read},
            )
            assert preview.status_code == 200, preview.text
            with Image.open(io.BytesIO(preview.content)) as image:
                assert image.size == (256, 512)

            details = await http.post(
                f"{path}/page_inspect", json={"page": 0, "read": read}
            )
            assert details.status_code == 200, details.text
            assert details.json()["native_lines"][0]["text_preview"] == "searchable"

            overflow = await http.post(
                f"{path}/cancel", json={"scope": "viewport", "before_epoch": 2**63}
            )
            assert overflow.status_code == 400
            ack = await http.post(
                f"{path}/cancel",
                json={"scope": "viewport", "before_epoch": read["epoch"] + 1},
            )
            assert ack.status_code == 200 and ack.json()["cancelled"] is True
            stale = await http.post(f"{path}/load", json={"read": read, "pages": [0]})
            assert stale.status_code == 200
            assert json.loads(stale.text)["message"] == "cancelled"

            legacy = await http.post(f"{path}/load")
            assert legacy.status_code == 200, legacy.text
            old_event, _ = [json.loads(line) for line in legacy.text.splitlines()]
            assert old_event["page_payload"]["scan_checked"] is True
            assert (await http.post(f"{path}/close")).status_code == 200
    finally:
        child.stop()


async def test_read_cancel_reaches_worker_while_render_adapter_waits(
    pdf_app, fake_pdf_adapter, supervisor_token, monkeypatch
):
    import asyncio
    import threading

    entered = threading.Event()
    release = threading.Event()
    cancelled = threading.Event()

    def render(session, payload):
        entered.set()
        if not release.wait(5):
            raise RuntimeError("render wait expired")
        return b"PNG"

    def cancel(session, scope, before_epoch):
        assert (session, scope, before_epoch) == ("sid-1", "viewport", 2)
        cancelled.set()

    monkeypatch.setattr(
        fake_pdf_adapter, "render_preview_demand", render, raising=False
    )
    monkeypatch.setattr(fake_pdf_adapter, "cancel_reads", cancel, raising=False)
    async with _http(supervisor_token, pdf_app) as http:
        pending = asyncio.create_task(
            http.post(
                "/v2/pdf/sessions/sid-1/render_preview",
                json={"page": 0, "read": {"scope": "viewport", "epoch": 1}},
            )
        )
        try:
            assert await asyncio.to_thread(entered.wait, 5)
            response = await asyncio.wait_for(
                http.post(
                    "/v2/pdf/sessions/sid-1/cancel",
                    json={"scope": "viewport", "before_epoch": 2},
                ),
                5,
            )
            assert response.status_code == 200 and response.json()["cancelled"] is True
            assert cancelled.is_set()
            assert not pending.done()
        finally:
            release.set()
            result = await pending
        assert result.status_code == 200

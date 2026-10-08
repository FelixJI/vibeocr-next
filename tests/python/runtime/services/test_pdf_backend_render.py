"""当前内存 PDF 的并发请求按文档锁串行栅格化，图像编码在锁外。"""

from __future__ import annotations

import threading
from concurrent.futures import ThreadPoolExecutor
from unittest.mock import MagicMock

import fitz
import pytest
from vibeocr.runtime.documents.pdf_backend_client import PdfBackendClient


@pytest.fixture
def backend_client():
    """启动真实 PDF 后端子进程的客户端。"""
    client = PdfBackendClient()
    yield client
    client.stop()


@pytest.fixture
def heavy_pdf(tmp_path):
    """高 DPI 栅格化耗时的多页 PDF。

    用较大的页面尺寸 + 嵌入图片，让 get_pixmap 有真实工作量（每页 ~50ms+），
    这样串行 vs 并行的耗时可测、差异稳定。
    """
    path = tmp_path / "heavy.pdf"
    doc = fitz.open()
    # 生成一张可压缩的图片（渐变），栅格化时有真实工作量
    import io

    from PIL import Image

    img = Image.new("RGB", (1200, 1600))
    px = img.load()
    assert px is not None
    for y in range(1600):
        for x in range(1200):
            px[x, y] = ((x * y) % 256, (x + y) % 256, (x - y) % 256)
    buf = io.BytesIO()
    img.save(buf, "PNG")
    img_bytes = buf.getvalue()

    for i in range(8):
        page = doc.new_page(width=612, height=792)
        page.insert_image(page.rect, stream=img_bytes)
        page.insert_text((72, 72), f"Page {i + 1}", fontsize=12)
    doc.save(str(path))
    doc.close()
    return path


class TestCurrentDocumentRendering:
    """并发请求不允许同时进入同一个 PyMuPDF 文档。"""

    def test_concurrent_render_preview_all_succeed(self, backend_client, heavy_pdf):
        """8 页并发 render_preview 应全部成功返回有效 PNG。"""
        open_resp = backend_client.open_session(str(heavy_pdf))
        sid = open_resp.session_id
        total = len(open_resp.model.pages)
        assert total == 8

        def render_one(page_idx):
            return backend_client.render_preview(sid, page_idx, dpi=150)

        with ThreadPoolExecutor(max_workers=8) as pool:
            results = list(pool.map(render_one, range(total)))

        assert len(results) == total
        for png in results:
            assert isinstance(png, bytes)
            assert len(png) > 0
            # 验证是有效 PNG
            assert png[:8] == b"\x89PNG\r\n\x1a\n", "应返回 PNG 字节流"

    def test_current_document_rasterization_holds_session_lock(self, monkeypatch):
        from vibeocr.runtime.documents import pdf_backend_process as backend
        from vibeocr.runtime.documents.wire_schemas import RenderPreviewRequest

        session = backend.BackendSession(
            session_id="current-render",
            file_path="unused.pdf",
            doc=MagicMock(),
            pdf_document=MagicMock(),
        )
        registry = MagicMock()
        registry.get.return_value = session
        monkeypatch.setattr(backend, "_get_registry", lambda: registry)
        entered = threading.Event()
        release = threading.Event()
        calls = []

        def rasterize(doc, page, dpi):
            assert doc is session.doc
            calls.append(page)
            if page == 0:
                entered.set()
                assert release.wait(5)
            return b"\x00\x00\x00", 1, 1

        monkeypatch.setattr(backend, "_render_page_pixels", rasterize)
        with ThreadPoolExecutor(max_workers=2) as pool:
            first = pool.submit(
                backend.render_preview,
                session.session_id,
                RenderPreviewRequest(page=0, dpi=150),
            )
            assert entered.wait(5)
            # 首个真实文档读尚未结束；另一线程不能取得该文档锁。
            assert not session.fitz_lock.acquire(blocking=False)
            second = pool.submit(
                backend.render_preview,
                session.session_id,
                RenderPreviewRequest(page=1, dpi=150),
            )
            release.set()
            assert (
                first.result().media_type == second.result().media_type == "image/png"
            )
        assert calls == [0, 1]

    def test_render_preview_invalid_page_returns_400(self, backend_client, heavy_pdf):
        """页索引越界应返回 400（而非 500）。"""
        from vibeocr.runtime.documents.pdf_backend_client import PdfBackendError

        open_resp = backend_client.open_session(str(heavy_pdf))
        sid = open_resp.session_id
        total = len(open_resp.model.pages)

        with pytest.raises(PdfBackendError) as exc_info:
            backend_client.render_preview(sid, total + 100, dpi=72)
        # PdfBackendError 的 message 含 HTTP 状态码
        assert "400" in str(exc_info.value), f"越界应返回 400，实际：{exc_info.value}"

"""PDF 后端子进程 — FastAPI 服务,持有所有 fitz.Document + PdfDocument 规范模型。

主进程通过 localhost HTTP 调用本服务。fitz 调用全部收敛在此单进程单线程内,
彻底规避 PyMuPDF 不支持多线程的隐患。

启动方式:python -m vibeocr.runtime.documents.pdf_backend_process --port <port>
由主进程 PdfBackendClient 托管(端口探测 + JobObjectGuard 孤儿清理 + 崩溃重启)。

设计要点:
- SessionRegistry 按 session_id 持有 (doc, pdf_document, cancel_event)
- 变更操作返回 ModelDiff(增量),主进程 apply 到 mirror
- 长操作(摆正/删除文字层/OCR 写层/保存)走流式进度(chunked line JSON)
- 渲染(缩略图/预览)返回字节流(PNG),主进程构 QPixmap
- 取消:协作式,POST /session/{id}/cancel 设置 event,任务循环检查
"""

from __future__ import annotations

import argparse
import io
import logging
import os
import socket
import sys
import threading
import time
import uuid
from contextlib import asynccontextmanager, contextmanager
from dataclasses import dataclass, field, replace
from typing import TYPE_CHECKING

import pymupdf as fitz
import uvicorn
from fastapi import FastAPI, HTTPException
from fastapi.responses import StreamingResponse
from vibeocr.runtime.documents.models.pdf_document import (
    PdfDocument,
    PdfPageInfo,
    TextLayerInfo,
)
from vibeocr.runtime.documents.pdf_block_editor import PdfBlockEditor
from vibeocr.runtime.documents.pdf_native_gate import (
    PdfNativeGate,
    PdfNativeWaitAborted,
)
from vibeocr.runtime.documents.pdf_service import (
    PdfSaveError,
    PdfService,
    TextLayerBatchError,
)
from vibeocr.runtime.documents.wire_schemas import (
    AddTextLayerRequest,
    BatchAddTextLayerRequest,
    DeletePagesRequest,
    DetectTextLayersRequest,
    DetectTextLayersResponse,
    EmptyOk,
    HealthResponse,
    InsertBlankRequest,
    InsertFromRequest,
    ModelDiff,
    MovePageRequest,
    MutateResponse,
    OpenRequest,
    OpenResponse,
    PageInspectRequest,
    PageInspectResponse,
    PageListRequest,
    PdfDemandLoadRequest,
    PdfDocumentMirror,
    PdfPageInfoMirror,
    PdfReadCancelledOk,
    PdfReadCancelRequest,
    PdfReadContext,
    ProgressEvent,
    ProgressPhase,
    RenderPreviewRequest,
    RenderThumbnailRequest,
    ReorderRequest,
    RewriteTextLayerRequest,
    RotateRequest,
    SaveRequest,
    SaveResponse,
    TextBlockMirror,
    TextLayerInfoMirror,
    UpdateBlockTextRequest,
)
from vibeocr.runtime.recognition.models.ocr_result import OCRResult, TextBlock

if TYPE_CHECKING:
    from collections.abc import Iterator

logger = logging.getLogger(__name__)

# 全局注册表(单进程单例)
_REGISTRY: SessionRegistry | None = None


def _get_registry() -> SessionRegistry:
    global _REGISTRY
    if _REGISTRY is None:
        _REGISTRY = SessionRegistry()
    return _REGISTRY


# ---- 镜像 / diff 构造 ---------------------------------------------------


def _text_layer_to_mirror(tl: TextLayerInfo) -> TextLayerInfoMirror:
    return TextLayerInfoMirror(
        index=tl.index,
        text_preview=tl.text_preview,
        char_count=tl.char_count,
        bbox=tl.bbox,
        color_id=tl.color_id,
    )


def _text_block_to_mirror(b: TextBlock) -> TextBlockMirror:
    return TextBlockMirror(
        text=b.text,
        score=0.0 if b.score is None else b.score,
        score_unknown=b.score is None,
        bbox=b.bbox,
        polygon=b.polygon,
        page_idx=b.page_idx,
        is_manually_edited=b.is_manually_edited,
        label=b.label,
        order=b.order,
    )


def _page_to_mirror(info: PdfPageInfo, *, summary: bool = False) -> PdfPageInfoMirror:
    return PdfPageInfoMirror(
        page_index=info.page_index,
        rotation=info.rotation,
        has_text_layer=info.has_text_layer,
        has_ocr_text_layer=bool(info.ocr_text_blocks),
        text_layers=[_text_layer_to_mirror(t) for t in info.text_layers],
        is_scanned=info.is_scanned,
        rect=info.rect,
        ocr_text_blocks=[]
        if summary
        else [_text_block_to_mirror(b) for b in info.ocr_text_blocks],
        ocr_preproc_angle=info.ocr_preproc_angle,
        deskewed=info.deskewed,
        scan_checked=info.scan_checked,
    )


def _doc_to_mirror(
    doc_model: PdfDocument, *, summary: bool = False
) -> PdfDocumentMirror:
    return PdfDocumentMirror(
        file_path=doc_model.file_path,
        pages=[_page_to_mirror(p, summary=summary) for p in doc_model.pages],
        is_modified=doc_model.is_modified,
        has_structural_change=doc_model.has_structural_change,
        render_dpi=doc_model.render_dpi,
        thumbnail_dpi=doc_model.thumbnail_dpi,
    )


def _diff_full(doc_model: PdfDocument) -> ModelDiff:
    """全量 model diff(打开/结构变更)。"""
    return ModelDiff(
        full_model=_doc_to_mirror(doc_model, summary=True), structural_change=True
    )


def _diff_pages(
    doc_model: PdfDocument,
    pages: list[int],
    *,
    invalidate_thumbnails: list[int] | None = None,
    modified: bool | None = None,
) -> ModelDiff:
    """增量页 diff(内容变更:旋转/加文字层/摆正/编辑块)。"""
    return ModelDiff(
        replaced_pages=[
            _page_to_mirror(doc_model.pages[p])
            for p in pages
            if 0 <= p < len(doc_model.pages)
        ],
        invalidated_thumbnails=invalidate_thumbnails or [],
        modified_flag=modified,
    )


# ---- 渲染并发控制 -------------------------------------------------------
# 渲染（render_preview / render_thumbnail）各自打开一个独立的临时
# PyMuPDF 的当前文档读写按文档锁串行，图像编码在锁外。


def _render_page_pixels(
    doc: fitz.Document,
    page_index: int,
    dpi: float,
    *,
    max_edge: int | None = None,
    max_pixels: int | None = None,
    target_size: int | None = None,
) -> tuple[bytes, int, int]:
    """在调用方持有的文档锁内渲染当前内存修订，分配前限制像素。

    - target_size：直接按目标边长像素栅格化（demand 缩略图，无需大图缩小）；
    - max_edge：目标长边像素需求（demand 预览）；未指定时按 DPI。
    - max_pixels：像素预算，与 16M 硬上限取小。
    """
    import math

    if page_index < 0 or page_index >= doc.page_count:
        raise HTTPException(status_code=400, detail="页索引越界")
    if not math.isfinite(dpi) or not 1 <= dpi <= 1200:
        raise HTTPException(status_code=400, detail="DPI 必须在 1–1200 之间")
    if target_size is not None and not 1 <= target_size <= 8192:
        raise HTTPException(status_code=400, detail="target_size 必须在 1–8192 之间")
    page = doc[page_index]
    width, height = page.rect.width, page.rect.height
    if not (math.isfinite(width) and math.isfinite(height) and min(width, height) > 0):
        raise HTTPException(status_code=400, detail="页面几何退化，无法渲染")

    budget = min(16_000_000, max_pixels) if max_pixels is not None else 16_000_000
    edge = target_size if target_size is not None else max_edge
    scale = (
        dpi / 72
        if edge is None
        else math.nextafter(min(edge, budget) / max(width, height), 0.0)
    )

    def fits(candidate: float) -> bool:
        pixels_w, pixels_h = math.ceil(width * candidate), math.ceil(height * candidate)
        return pixels_w * pixels_h <= budget and (
            edge is None or max(pixels_w, pixels_h) <= edge
        )

    if not fits(scale):
        if edge is None and max_pixels is None:
            raise HTTPException(
                status_code=400, detail=f"页面渲染超过 {budget} 像素预算"
            )
        # 对栅格整数边界求可用比例，含长窄页和 1 像素预算。
        low, high = min(scale, 1 / max(width, height)), scale
        if not fits(low):
            low = math.nextafter(low, 0.0)
        for _ in range(40):
            middle = (low + high) / 2
            if fits(middle):
                low = middle
            else:
                high = middle
        scale = low

    pix = page.get_pixmap(matrix=fitz.Matrix(scale, scale), alpha=False)
    if (
        min(pix.width, pix.height) < 1
        or pix.width * pix.height > budget
        or (edge is not None and max(pix.width, pix.height) > edge)
    ):
        raise HTTPException(status_code=400, detail="页面栅格尺寸超出预算")
    return bytes(pix.samples), pix.width, pix.height


def _png_response(
    session: BackendSession,
    read: PdfReadContext | None,
    pixels: tuple[bytes, int, int],
) -> StreamingResponse:
    if read is not None:
        _reject_obsolete_read(session, read)
    encoded = _encode_png(*pixels)
    if read is not None:
        _reject_obsolete_read(session, read)
    return StreamingResponse(io.BytesIO(encoded), media_type="image/png")


def _encode_png(samples: bytes, width: int, height: int) -> bytes:
    """锁外 PNG 编码（无缩放）；独立 seam 供过期丢弃测试注入。"""
    from PIL import Image

    img = Image.frombytes("RGB", (width, height), samples)
    buf = io.BytesIO()
    img.save(buf, "PNG")
    return buf.getvalue()


# ---- Session 注册表 -----------------------------------------------------


_FITZ_LOCK = threading.Lock()
# 全局原生调度优先门：所有 _FITZ_LOCK / BackendSession.fitz_lock 入场（含
# SessionRegistry.remove 的 close 收尾）都经此排序（前台优先/后台有界公平，
# burst=4 与冻结预算 foreground_native_burst 一致）。门只做入场排序，不是
# 绕过原生锁的假队列；所有 fitz 调用仍在原生锁内执行，PIL 编码保持锁外。
_NATIVE_GATE = PdfNativeGate()


@dataclass
class BackendSession:
    session_id: str
    file_path: str
    doc: fitz.Document
    pdf_document: PdfDocument
    block_editor: PdfBlockEditor | None = None
    cancel_event: threading.Event = field(default_factory=threading.Event)
    # fitz(Document) 非线程安全:并发渲染缩略图时串行化 get_pixmap 等 fitz 调用。
    # 锁粒度仅覆盖 fitz 栅格化,PIL 缩放/PNG 编码在锁外可并行。
    fitz_lock: threading.Lock = field(default_factory=lambda: _FITZ_LOCK)
    # 原生调度优先门（默认绑定全局 _FITZ_LOCK 对应的全局门）。
    native_gate: PdfNativeGate = field(default_factory=lambda: _NATIVE_GATE)
    # 文字层后台逐页检测线程(打开后异步跑,逐页发 progress)
    _load_thread: threading.Thread | None = None
    # 关闭同步：CLOSING 状态拒绝新操作，active_ops 跟踪进行中的 fitz 操作，
    # remove() 等待 active_ops 归零后再在 fitz_lock 内 close doc。
    state: str = "OPEN"  # OPEN / CLOSING / CLOSED
    active_ops: int = 0
    _ops_cond: threading.Condition = field(default_factory=threading.Condition)
    # 一次替换发布两个水位，门内只读快照，不取锁。
    read_watermarks: tuple[int, int] = (0, 0)
    read_changed: threading.Condition = field(default_factory=threading.Condition)


@contextmanager
def _invalidate_block_edits(s: BackendSession, pages: list[int]) -> Iterator[None]:
    if s.block_editor is not None:
        s.block_editor.invalidate(pages)
    try:
        yield
    finally:
        if (
            s.block_editor is not None
            and s.block_editor.doc is s.doc
            and not s.doc.is_closed
        ):
            s.block_editor.prune()


class SessionRegistry:
    """session_id → BackendSession。单线程访问(FastAPI 同步路由在线程池跑,
    但 fitz 操作通过 session 内单例串行;真正的并发隔离靠 doc 单线程持有)。"""

    def __init__(self) -> None:
        self._sessions: dict[str, BackendSession] = {}
        self._lock = threading.Lock()

    def add(self, file_path: str) -> BackendSession:
        sid = uuid.uuid4().hex[:16]
        # fitz.open + 占位 PdfDocument(轻量,不逐页读)。open 是前台 native 单位。
        with _NATIVE_GATE.foreground(), _FITZ_LOCK:
            doc, pdf_document = PdfService.open_doc(file_path)
        session = BackendSession(
            session_id=sid, file_path=file_path, doc=doc, pdf_document=pdf_document
        )
        with self._lock:
            self._sessions[sid] = session
        return session

    def find(self, session_id: str) -> BackendSession | None:
        with self._lock:
            return self._sessions.get(session_id)

    def get(self, session_id: str) -> BackendSession:
        s = self.find(session_id)
        if s is None:
            raise HTTPException(
                status_code=404, detail=f"session not found: {session_id}"
            )
        # CLOSING/CLOSED 状态拒绝新操作，避免 close 与新请求并发
        if s.state != "OPEN":
            raise HTTPException(
                status_code=409, detail=f"session is closing/closed: {session_id}"
            )
        return s

    def remove(self, session_id: str) -> None:
        s = self.find(session_id)
        if s is None:
            return
        # Keep the entry until close is confirmed; a timeout/exception is retryable.
        with s._ops_cond:
            s.state = "CLOSING"
            s.cancel_event.set()
        # 先释放 _ops_cond 再唤醒优先门（防锁反转）：等待中的后台页在门内
        # 重评 CLOSING/cancel_event 即退出，不进入 native，也不占公平配额。
        _wake_read_waiters(s)
        with s._ops_cond:
            deadline = time.monotonic() + 10.0
            while s.active_ops > 0:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise HTTPException(
                        status_code=409, detail="PDF operations have not settled"
                    )
                s._ops_cond.wait(timeout=min(1.0, remaining))
        # close 收尾同样是 native 入场，经优先门 + 原生锁；此刻本会话已无
        # 等待者（后台页已被 CLOSING 唤醒退出，已登记前台租约已归零）。
        with s.native_gate.foreground(), s.fitz_lock:
            if s.doc.is_closed is not True:
                s.doc.close()
            s.block_editor = None
            s.state = "CLOSED"
        with self._lock:
            if self._sessions.get(session_id) is s:
                del self._sessions[session_id]

    def count(self) -> int:
        with self._lock:
            return len(self._sessions)


@contextmanager
def _ops_lease(session: BackendSession):
    """native 活跃租约：登记 active_ops，退出时唤醒 close 等待。

    登记前的 OPEN 检查是临界进入的唯一权威判定（与 remove() 置 CLOSING
    同在 _ops_cond 内互斥，消除 TOCTOU）；优先门许可由外层持有。
    """
    with session._ops_cond:
        if session.state != "OPEN":
            raise HTTPException(status_code=409, detail="session is closing/closed")
        session.active_ops += 1
    try:
        yield
    finally:
        with session._ops_cond:
            session.active_ops -= 1
            if session.active_ops == 0:
                session._ops_cond.notify_all()


@contextmanager
def _fitz_op(session: BackendSession):
    """fitz 前台操作上下文：活跃租约 → 优先门前台许可。

    既有全部 with 调用点默认前台（write/open/model/render 等同一优先级）。
    租约先登记（T1 语义）：已受理的前台/写操作在 close 时会被等待安全
    完成，不会被关闭后拒绝。许可在原生锁释放之后才归还（with 嵌套
    顺序），与调用方 fitz_lock 一起保证恰一 native 持有者。本路径不套用
    读取消；后续 scoped 前台 read 另用明确可撤销读入口。
    """
    with _ops_lease(session), session.native_gate.foreground():
        yield


@contextmanager
def _foreground_native(session: BackendSession) -> Iterator[None]:
    with _fitz_op(session), session.fitz_lock:
        yield


@contextmanager
def _background_page_native(session: BackendSession) -> Iterator[None]:
    """后台逐页 native 单元：优先门后台等待 → 再次 OPEN 检查/租约 → 原生锁。

    等待阶段可被 CLOSING / cancel_event 撤销（未进入 native，不占租约也
    不占公平配额）；门内准入后由 _ops_lease 在 _ops_cond 内做权威 OPEN
    复查（与 remove() 置 CLOSING 互斥，消除 TOCTOU），随后本页 native
    执行不可中断、安全完成。租约只覆盖本页 native 执行，不跨 NDJSON
    yield/网络背压。写路径不受本接口影响（write lease / cancel_event
    语义不变）；后续 viewport/background read epoch 的 highwater 谓词
    可直接组合进 should_abort，不需要新的取消协议。
    """

    def _aborted() -> bool:
        # 免锁读（在门互斥锁内执行，不得获取 _ops_cond）。
        return session.state != "OPEN" or session.cancel_event.is_set()

    try:
        with session.native_gate.background(should_abort=_aborted):
            with _ops_lease(session), session.fitz_lock:
                yield
    except PdfNativeWaitAborted as error:
        raise HTTPException(
            status_code=409, detail="后台页检测在进入原生执行前被取消"
        ) from error


# ---- pdf.demand.v1 scoped 读 -----------------------------------------


def _read_aborted(session: BackendSession, read: PdfReadContext) -> bool:
    cutoff = session.read_watermarks[0 if read.scope == "viewport" else 1]
    return read.epoch < cutoff or session.state != "OPEN"


def _advance_read_watermark(
    session: BackendSession, scope: str, before_epoch: int
) -> None:
    with session.read_changed:
        viewport, background = session.read_watermarks
        session.read_watermarks = (
            max(viewport, before_epoch) if scope != "background" else viewport,
            max(background, before_epoch) if scope != "viewport" else background,
        )


def _wake_read_waiters(session: BackendSession) -> None:
    with session.read_changed:
        session.read_changed.notify_all()
    session.native_gate.wake_all()


def _reject_obsolete_read(session: BackendSession, read: PdfReadContext) -> None:
    if _read_aborted(session, read):
        raise HTTPException(status_code=409, detail="read superseded")


@contextmanager
def _revocable_read(
    session: BackendSession, read: PdfReadContext, *, foreground: bool
) -> Iterator[None]:
    """scoped 读 native 单元：水位校验 → 门（可撤销前台/后台）→ 租约 → 原生锁。

    显式 pages 的定向读与可见视口读走可撤销前台（真撤出等待队列并唤醒公平）；
    自动全档流走后台。等待阶段可被 CLOSING/读水位撤销；进入
    native 后不可中断，安全完成后由调用方在昂贵编码前复查水位。
    """

    def _aborted() -> bool:
        return _read_aborted(session, read)

    entry = (
        session.native_gate.foreground if foreground else session.native_gate.background
    )
    try:
        with entry(should_abort=_aborted), _ops_lease(session), session.fitz_lock:
            _reject_obsolete_read(session, read)
            yield
    except PdfNativeWaitAborted as error:
        raise HTTPException(status_code=409, detail="PDF 读已撤销或会话关闭") from error


def _demand_self_test_delay_seconds() -> float:
    """AC1 受控延迟：仅自测 smoke 环境下的自动 body 流（pages 省略）生效。

    门禁：VIBEOCR_SELF_TEST_SMOKE=paddle-modes-e2e 且
    VIBEOCR_PADDLE_SMOKE_INPUT_KIND=pdf_baseline；幅度
    VIBEOCR_PDF_DEMAND_TEST_DELAY_MS 夹在 0..500ms。其余环境（含显式
    pages/legacy load）恒 0，不影响生产行为。
    """
    if os.environ.get("VIBEOCR_SELF_TEST_SMOKE") != "paddle-modes-e2e":
        return 0.0
    if os.environ.get("VIBEOCR_PADDLE_SMOKE_INPUT_KIND") != "pdf_baseline":
        return 0.0
    raw = os.environ.get("VIBEOCR_PDF_DEMAND_TEST_DELAY_MS", "")
    try:
        milliseconds = int(raw) if raw else 0
    except ValueError:
        return 0.0
    return max(0, min(500, milliseconds)) / 1000.0


# ---- FastAPI 应用 -------------------------------------------------------


@asynccontextmanager
async def _lifespan(app: FastAPI):
    logger.info("[pdf-backend] 服务启动")
    yield
    # 关闭所有 session
    reg = _get_registry()
    for sid in list(reg._sessions.keys()):
        reg.remove(sid)
    logger.info("[pdf-backend] 服务关闭")


app = FastAPI(title="VibeOCR PDF Backend", lifespan=_lifespan)


@app.get("/health", response_model=HealthResponse)
def health() -> HealthResponse:
    import os

    return HealthResponse(
        status="ok", sessions=_get_registry().count(), pid=os.getpid()
    )


@app.post("/session/open", response_model=OpenResponse)
def session_open(req: OpenRequest) -> OpenResponse:
    """打开 PDF,返回 session_id + 占位 model(逐页信息由 /load 异步填充)。"""
    try:
        session = _get_registry().add(req.path)
    except FileNotFoundError as e:
        raise HTTPException(status_code=400, detail=str(e)) from e
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"打开失败: {e}") from e
    return OpenResponse(
        session_id=session.session_id, model=_doc_to_mirror(session.pdf_document)
    )


@app.post("/session/{sid}/close", response_model=EmptyOk)
def session_close(sid: str) -> EmptyOk:
    _get_registry().remove(sid)
    return EmptyOk()


@app.post("/session/{sid}/model", response_model=PdfDocumentMirror)
def session_model(sid: str) -> PdfDocumentMirror:
    """全量刷新 model(主进程定期校准用)。"""
    s = _get_registry().get(sid)
    with _fitz_op(s), s.fitz_lock:
        return _doc_to_mirror(s.pdf_document, summary=True)


# ---- 后台逐页文字层检测(打开后流式)----------------------------------


def _detect_one_page(
    session: BackendSession,
    i: int,
    *,
    read: PdfReadContext | None = None,
    foreground: bool = False,
) -> PdfPageInfo:
    """检测单页并写入 session model。

    准入：无 read 走后台逐页单元（legacy）；有 read 走 scoped 可撤销单元，
    默认显式 pages 为前台优先，自动流和预取为后台。body 形 demand
    load 只做几何+文字存在性，不扫描图像覆盖 is_scanned，并保留
    OCR/preproc/deskewed。文字存在性变化时旧检测结果失效；legacy 完整
    扫描置 scan_checked=True。
    """
    if read is not None:
        admission = _revocable_read(session, read, foreground=foreground)
    else:
        admission = _background_page_native(session)
    with admission:
        if not 0 <= i < session.pdf_document.page_count:
            raise HTTPException(status_code=400, detail="页索引越界")
        rotation = PdfService.page_rotation(session.doc, i)
        page_rect = PdfService.page_rect(session.doc, i)
        has_text_layer = PdfService.page_has_text(session.doc, i)
        previous = session.pdf_document.pages[i]
        info = replace(
            previous,
            rotation=rotation,
            has_text_layer=has_text_layer,
            rect=page_rect,
        )
        if previous.has_text_layer != has_text_layer:
            info.invalidate_text_detection()
        if read is None:
            info.is_scanned = not has_text_layer and PdfService.is_page_scanned(
                session.doc, i
            )
            info.scan_checked = True
        if read is not None:
            _reject_obsolete_read(session, read)
        session.pdf_document.pages[i] = info
        return info


@app.post("/session/{sid}/load")
def session_load(
    sid: str, req: PdfDemandLoadRequest | None = None
) -> StreamingResponse:
    """流式逐页检测，每完成一页推一条 NDJSON ProgressEvent。

    无 body（legacy）：完整检测（含图像覆盖率扫描），逐页后台单元。
    有 body（pdf.demand.v1）：仅几何+文字存在性；显式 pages 默认前台优先，
    prefetch=true 使用后台准入，省略 pages 为唯一自动后台流。
    取消/水位隔栏/失败不伪造全量完成。

    每行 JSON:
        {"phase":"load","current":N,"total":T,"page_index":I,
         "page_payload":{PdfPageInfoMirror...}}
    末行:{"phase":"load","current":检测成功页数,"total":检测目标页数,
          "message":"done"|"cancelled"|"incomplete"}
    """
    import json as _json

    s = _get_registry().get(sid)
    total = s.pdf_document.page_count
    read = req.read if req is not None else None
    targeted = req is not None and req.pages is not None
    foreground = targeted and req is not None and not req.prefetch
    pages = (
        list(dict.fromkeys(req.pages))
        if req is not None and req.pages is not None
        else range(total)
    )
    sentinel_total = len(pages)
    delay_seconds = (
        _demand_self_test_delay_seconds() if read is not None and not targeted else 0.0
    )

    def gen() -> Iterator[bytes]:
        succeeded = 0
        cancelled = False
        for i in pages:
            try:
                if read is not None and delay_seconds:
                    # 等待发生在门与租约之前，scoped cancel 可立即唤醒。
                    with s.read_changed:
                        s.read_changed.wait_for(
                            lambda: _read_aborted(s, read), delay_seconds
                        )
                info = (
                    _detect_one_page(s, i)
                    if read is None
                    else _detect_one_page(s, i, read=read, foreground=foreground)
                )
                if read is not None:
                    _reject_obsolete_read(s, read)
                page_mirror = _page_to_mirror(info)
                ev = ProgressEvent(
                    phase=ProgressPhase.LOAD,
                    current=succeeded + 1,
                    total=sentinel_total,
                    page_index=i,
                    page_payload=page_mirror.model_dump(mode="json"),
                )
                line = (_json.dumps(ev.model_dump(mode="json")) + "\n").encode()
                succeeded += 1
                yield line
            except HTTPException as e:
                if e.status_code == 409:
                    # 未进入 native 或结果已过期，这一页不得计入已知。
                    cancelled = True
                    break
                logger.error("[pdf-backend] load page %d failed: %s", i, e)
            except Exception as e:
                logger.error("[pdf-backend] load page %d failed: %s", i, e)
        # 结束哨兵：current 是真实检测成功页数；取消/失败用显式结局标记。
        if cancelled or (read is not None and _read_aborted(s, read)):
            outcome = "cancelled"
        elif succeeded == sentinel_total:
            outcome = "done"
        else:
            outcome = "incomplete"
        done_ev = ProgressEvent(
            phase=ProgressPhase.LOAD,
            current=succeeded,
            total=sentinel_total,
            message=outcome,
        )
        yield (_json.dumps(done_ev.model_dump(mode="json")) + "\n").encode()

    return StreamingResponse(gen(), media_type="application/x-ndjson")


# ---- 渲染 ---------------------------------------------------------------


@app.post("/session/{sid}/render_thumbnail")
def render_thumbnail(sid: str, req: RenderThumbnailRequest) -> StreamingResponse:
    """渲染缩略图,返回 PNG 字节流。

    携带 read（pdf.demand.v1）：可撤销前台准入 + 直接按目标 size 有界
    栅格化（无需大图缩小）；复制后过期在昂贵编码前丢弃。无 read 保持
    legacy：thumbnail_dpi 渲染后锁外 LANCZOS 缩小。PIL 编码均在锁外。
    """
    s = _get_registry().get(sid)
    try:
        if req.read is not None:
            with _revocable_read(s, req.read, foreground=True):
                samples, width, height = _render_page_pixels(
                    s.doc, req.page, s.pdf_document.thumbnail_dpi, target_size=req.size
                )
            return _png_response(s, req.read, (samples, width, height))
        with _fitz_op(s), s.fitz_lock:
            samples, width, height = _render_page_pixels(
                s.doc, req.page, s.pdf_document.thumbnail_dpi
            )
        # 锁外:PIL 缩放 + PNG 编码(CPU 密集,无 fitz 调用,可并行)
        from PIL import Image

        img = Image.frombytes("RGB", (width, height), samples)
        img.thumbnail((req.size, req.size), Image.LANCZOS)
        buf = io.BytesIO()
        img.save(buf, "PNG")
        return StreamingResponse(io.BytesIO(buf.getvalue()), media_type="image/png")
    except HTTPException:
        raise
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"渲染缩略图失败: {e}") from e


@app.post("/session/{sid}/render_preview")
def render_preview(sid: str, req: RenderPreviewRequest) -> StreamingResponse:
    """渲染预览页,返回 PNG 字节流(直接 fitz Pixmap → PNG)。

    指定 max_edge 时按目标像素长边栅格化，缺省按 DPI。携带 read 时按域
    选择可撤销前台或后台准入，复制后过期在编码前丢弃。PIL/PNG 编码在锁外。
    """
    s = _get_registry().get(sid)
    try:
        with (
            _revocable_read(s, req.read, foreground=req.read.scope == "viewport")
            if req.read is not None
            else _foreground_native(s)
        ):
            if req.max_edge is None and req.max_pixels is None:
                samples, width, height = _render_page_pixels(s.doc, req.page, req.dpi)
            else:
                samples, width, height = _render_page_pixels(
                    s.doc,
                    req.page,
                    req.dpi,
                    max_edge=req.max_edge,
                    max_pixels=req.max_pixels,
                )
        return _png_response(s, req.read, (samples, width, height))
    except HTTPException:
        raise
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"渲染预览失败: {e}") from e


@app.post("/session/{sid}/detect_text_layers", response_model=DetectTextLayersResponse)
def detect_text_layers(
    sid: str, req: DetectTextLayersRequest
) -> DetectTextLayersResponse:
    s = _get_registry().get(sid)
    try:
        with _fitz_op(s), s.fitz_lock:
            layers = PdfService.detect_text_layers(s.doc, req.page)
            if 0 <= req.page < len(s.pdf_document.pages):
                info = s.pdf_document.pages[req.page]
                if info.has_text_layer != bool(layers):
                    info.invalidate_text_detection()
                info.has_text_layer = bool(layers)
                info.text_layers = layers
        return DetectTextLayersResponse(
            text_layers=[_text_layer_to_mirror(t) for t in layers]
        )
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"检测文字层失败: {e}") from e


# ---- 变更操作 -----------------------------------------------------------


@app.post("/session/{sid}/rotate", response_model=MutateResponse)
def rotate_pages(sid: str, req: RotateRequest) -> MutateResponse:
    s = _get_registry().get(sid)
    try:
        with _fitz_op(s), s.fitz_lock:
            PdfService.rotate_pages(s.doc, s.pdf_document, req.pages, req.angle)
        return MutateResponse(
            diff=_diff_pages(
                s.pdf_document,
                req.pages,
                invalidate_thumbnails=req.pages,
                modified=True,
            )
        )
    except ValueError as e:
        raise HTTPException(status_code=400, detail=str(e)) from e
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"旋转失败: {e}") from e


@app.post("/session/{sid}/delete_pages", response_model=MutateResponse)
def delete_pages(sid: str, req: DeletePagesRequest) -> MutateResponse:
    s = _get_registry().get(sid)
    try:
        with _fitz_op(s), s.fitz_lock:
            PdfService.delete_pages(s.doc, s.pdf_document, req.pages)
            if s.block_editor is not None:
                s.block_editor.prune()
        return MutateResponse(diff=_diff_full(s.pdf_document))
    except ValueError as e:
        raise HTTPException(status_code=400, detail=str(e)) from e
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"删除页失败: {e}") from e


@app.post("/session/{sid}/insert_blank", response_model=MutateResponse)
def insert_blank(sid: str, req: InsertBlankRequest) -> MutateResponse:
    s = _get_registry().get(sid)
    try:
        with _fitz_op(s), s.fitz_lock:
            PdfService.insert_blank_page(
                s.doc, s.pdf_document, req.after_index, req.width, req.height
            )
        return MutateResponse(diff=_diff_full(s.pdf_document))
    except ValueError as e:
        raise HTTPException(status_code=400, detail=str(e)) from e
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"插入空白页失败: {e}") from e


@app.post("/session/{sid}/insert_from", response_model=MutateResponse)
def insert_from(sid: str, req: InsertFromRequest) -> MutateResponse:
    s = _get_registry().get(sid)
    try:
        with _fitz_op(s), s.fitz_lock:
            PdfService.insert_pages_from(
                s.doc, s.pdf_document, req.source_path, req.after_index
            )
        return MutateResponse(diff=_diff_full(s.pdf_document))
    except ValueError as e:
        raise HTTPException(status_code=400, detail=str(e)) from e
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"插入页失败: {e}") from e


@app.post("/session/{sid}/move_page", response_model=MutateResponse)
def move_page(sid: str, req: MovePageRequest) -> MutateResponse:
    s = _get_registry().get(sid)
    try:
        with _fitz_op(s), s.fitz_lock:
            PdfService.move_page(s.doc, s.pdf_document, req.from_index, req.to_index)
        return MutateResponse(diff=_diff_full(s.pdf_document))
    except ValueError as e:
        raise HTTPException(status_code=400, detail=str(e)) from e
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"移动页失败: {e}") from e


@app.post("/session/{sid}/reorder", response_model=MutateResponse)
def reorder(sid: str, req: ReorderRequest) -> MutateResponse:
    s = _get_registry().get(sid)
    try:
        with _fitz_op(s), s.fitz_lock:
            PdfService.reorder_pages(s.doc, s.pdf_document, req.new_order)
        return MutateResponse(diff=_diff_full(s.pdf_document))
    except ValueError as e:
        raise HTTPException(status_code=400, detail=str(e)) from e
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"重排失败: {e}") from e


@app.post("/session/{sid}/add_text_layer", response_model=MutateResponse)
def add_text_layer(sid: str, req: AddTextLayerRequest) -> MutateResponse:
    """写入 OCR 文字层。ocr_result 是序列化的 OCRResult dict。

    简化:主进程负责把 OCRResult 序列化(含 text_blocks),后端反序列化后调
    PdfService.add_text_layer。
    """
    s = _get_registry().get(sid)
    try:
        # 反序列化 OCRResult(主进程传 dict)
        ocr_result_data = req.ocr_result
        text_blocks = [
            TextBlock(
                text=b["text"],
                score=b["score"],
                bbox=tuple(b["bbox"]) if b.get("bbox") else None,
                polygon=tuple(b["polygon"]) if b.get("polygon") else None,
                page_idx=b.get("page_idx"),
                is_manually_edited=b.get("is_manually_edited", False),
                label=b.get("label", "text"),
                order=b.get("order", -1),
            )
            for b in ocr_result_data.get("text_blocks", [])
        ]
        ocr_result = OCRResult(
            text_blocks=text_blocks,
            preproc_angle=int(ocr_result_data.get("preproc_angle", 0) or 0),
        )
        with _fitz_op(s), s.fitz_lock, _invalidate_block_edits(s, [req.page]):
            PdfService.add_text_layer(
                s.doc,
                s.pdf_document,
                req.page,
                ocr_result,
                pdf_settings=_settings_from_dict(req.pdf_settings),
                overwrite=req.overwrite,
            )
        return MutateResponse(
            diff=_diff_pages(
                s.pdf_document,
                [req.page],
                invalidate_thumbnails=[req.page],
                modified=True,
            )
        )
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"加文字层失败: {e}") from e


@app.post("/session/{sid}/add_text_layer_batch", response_model=MutateResponse)
def add_text_layer_batch(sid: str, req: BatchAddTextLayerRequest) -> MutateResponse:
    """批量写 OCR 文字层，一批页共享单一聚合子集字体。

    与逐页 add_text_layer 的区别：把本批所有页字符聚合一次解析子集字体，
    全批共享，避免每页一份独立子集字体放大体积。聚合逻辑复用
    PdfService.add_text_layer_batch（参照 save_with_rewrite 的整文档子集模式）。

    save=True 时，写层成功后紧跟一次 incremental save 把本批落盘
    （崩溃只丢最后一批）。extra.saved 标记是否成功落盘（False=回滚，调用方不写 sidecar）。
    """
    s = _get_registry().get(sid)
    pages_data = [{"page": p.page, "ocr_result": p.ocr_result} for p in req.pages]
    # 写层与落盘分离：写层成功后，落盘失败（文件占用/磁盘满/备份失败）不应
    # 导致整批 500（文字层已在内存 doc 中，用户可手动保存）。落盘失败仅记
    # 日志并返回 extra.saved=False，调用方不写 sidecar。
    try:
        with (
            _fitz_op(s),
            s.fitz_lock,
            _invalidate_block_edits(s, [page.page for page in req.pages]),
        ):
            results = PdfService.add_text_layer_batch(
                s.doc,
                s.pdf_document,
                pages_data,
                pdf_settings=_settings_from_dict(req.pdf_settings),
                overwrite=req.overwrite,
                cancel_check=s.cancel_event.is_set,
            )
    except TextLayerBatchError as error:
        logger.error("[pdf-backend] 文字层批次部分提交失败: %s", error)
        results = error.results
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"批量加文字层失败: {e}") from e

    with _fitz_op(s), s.fitz_lock:
        written_pages = sorted(results.keys())
        saved = True
        if req.save and written_pages:
            save_path = s.pdf_document.file_path
            if save_path:
                s.block_editor = None
                try:
                    try:
                        fresh = PdfService.save_incremental(s.doc, save_path)
                    except PdfSaveError:
                        raise
                    except Exception:
                        logger.exception("Incremental checkpoint unavailable")
                        fresh = False
                    if fresh is False:
                        fresh = PdfService._compress_in_place(
                            s.doc, save_path, clean=False
                        )
                    s.doc = fresh
                    saved = True
                except PdfSaveError as error:
                    _recover_save_session(s, error)
                    saved = error.committed
                    logger.exception("PDF checkpoint recovery: %s", error)
                except Exception:
                    saved = False
                    logger.exception("PDF checkpoint failed; edits remain unsaved")
            if saved:
                # 本批文字层已经持久化；保持后端规范模型与磁盘状态一致。后续 OCR
                # 收尾可据此跳过没有必要的整文档重写。
                s.pdf_document.is_modified = False
        return MutateResponse(
            diff=_diff_pages(
                s.pdf_document,
                written_pages,
                invalidate_thumbnails=written_pages,
                modified=s.pdf_document.is_modified,
            ),
            extra={"results": results, **({"saved": saved} if req.save else {})},
        )


@app.post("/session/{sid}/rewrite_text_layer", response_model=MutateResponse)
def rewrite_text_layer(sid: str, req: RewriteTextLayerRequest) -> MutateResponse:
    s = _get_registry().get(sid)
    try:
        blocks = [
            TextBlock(
                text=b.text,
                score=None if b.score_unknown else b.score,
                bbox=b.bbox,
                polygon=b.polygon,
                page_idx=b.page_idx,
                is_manually_edited=b.is_manually_edited,
                label=b.label,
                order=b.order,
            )
            for b in req.text_blocks
        ]
        with _fitz_op(s), s.fitz_lock, _invalidate_block_edits(s, [req.page]):
            written, skipped = PdfService.rewrite_text_layer(
                s.doc,
                s.pdf_document,
                req.page,
                blocks,
                req.preproc_angle,
                pdf_settings=_settings_from_dict(req.pdf_settings),
            )
        if written == 0:
            # 零写入＝整页未提交，明确 422 而非假成功；纯 OCR 部分写入仍沿既有语义成功。
            raise HTTPException(
                status_code=422,
                detail=f"块写入未全部成功，已保留原内容（written=0, skipped={skipped}）",
            )
        return MutateResponse(
            diff=_diff_pages(
                s.pdf_document, [req.page], modified=s.pdf_document.is_modified
            )
        )
    except HTTPException:
        raise
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"重写文字层失败: {e}") from e


@app.post("/session/{sid}/update_block_text", response_model=MutateResponse)
def update_block_text(sid: str, req: UpdateBlockTextRequest) -> MutateResponse:
    """双击编辑文字块：原子写入当前内存 doc 后才更新权威模型。

    事务语义（与 add_text_layer_batch 的 candidate 单页事务一致）：锁内在
    本页块克隆上仅改目标块，经 rewrite_text_layer(require_all=True) 整页
    重写；全部块写入成功才提交 doc 并更新模型/dirty。任一块被跳过、写入
    失败 → 原内容与模型不变（fail-closed）。新旧文本相同 → changed=False
    返回，不推进 dirty。
    """
    import math

    s = _get_registry().get(sid)
    try:
        if not req.new_text or not req.new_text.strip():
            raise HTTPException(status_code=400, detail="新文本不能为空")
        if len(req.new_text) > 2000:
            raise HTTPException(status_code=400, detail="新文本超出单块长度限制")
        with _fitz_op(s), s.fitz_lock:
            # 页索引在锁内校验，避免检查后结构变化
            if req.page < 0 or req.page >= len(s.pdf_document.pages):
                raise HTTPException(status_code=400, detail="页索引越界")
            info = s.pdf_document.pages[req.page]
            blocks = list(info.ocr_text_blocks)
            if not (0 <= req.block_index < len(blocks)):
                raise HTTPException(status_code=400, detail="块索引越界")
            target = blocks[req.block_index]
            if (
                req.expected_old_text is not None
                and target.text != req.expected_old_text
            ):
                raise HTTPException(
                    status_code=409, detail="块内容已被其他修改更新，请刷新后重试"
                )
            if target.text == req.new_text:
                return MutateResponse(
                    diff=_diff_pages(
                        s.pdf_document,
                        [req.page],
                        modified=s.pdf_document.is_modified,
                    ),
                    extra={"changed": False},
                )
            # 与 batch 反序列化同规则预验证全部块：任一块缺文本/几何 → 拒绝，
            # 不允许静默丢弃非目标块。
            for position, block in enumerate(blocks):
                if not (block.text and block.text.strip()):
                    raise HTTPException(
                        status_code=422,
                        detail=f"第 {position} 块文本为空，页面不可整页重写提交",
                    )
                bbox = block.bbox
                if (
                    bbox is None
                    or len(bbox) != 4
                    or not all(
                        isinstance(v, (int, float))
                        and math.isfinite(v)
                        and 0 <= v <= 1000
                        for v in bbox
                    )
                    or bbox[2] <= bbox[0]
                    or bbox[3] <= bbox[1]
                ):
                    raise HTTPException(
                        status_code=422,
                        detail=f"第 {position} 块缺少可写几何，页面不可整页重写提交",
                    )
            cloned = [
                replace(
                    block,
                    text=req.new_text,
                    is_manually_edited=True,
                )
                if position == req.block_index
                else replace(block)
                for position, block in enumerate(blocks)
            ]
            if s.block_editor is None or s.block_editor.doc is not s.doc:
                s.block_editor = PdfBlockEditor(s.doc)
            written, skipped = s.block_editor.rewrite(
                s.pdf_document,
                req.page,
                cloned,
                info.ocr_preproc_angle,
                _settings_from_dict(req.pdf_settings),
            )
            if written != len(cloned) or skipped:
                # require_all 下 batch 已不提交，这里是防御性二次校验。
                raise HTTPException(
                    status_code=422,
                    detail="块写入未全部成功，已保留原内容",
                )
        return MutateResponse(
            diff=_diff_pages(
                s.pdf_document, [req.page], modified=s.pdf_document.is_modified
            ),
            extra={"changed": True},
        )
    except HTTPException:
        raise
    except ValueError as e:
        raise HTTPException(status_code=400, detail=str(e)) from e
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"更新块文字失败: {e}") from e


@app.post("/session/{sid}/page_inspect", response_model=PageInspectResponse)
def page_inspect(sid: str, req: PageInspectRequest) -> PageInspectResponse:
    """当前页检查 payload：OCR 块与原生文字层的显示空间归一化投影。

    携带 read（pdf.demand.v1）走可撤销前台准入；原生文字投影在页内复用
    单个 TextPage 且不解析图像（TEXT_PRESERVE_IMAGES 关闭），复制后过期
    在返回前丢弃。
    """
    s = _get_registry().get(sid)
    try:
        if req.read is not None:
            with _revocable_read(s, req.read, foreground=True):
                response = PdfService.page_inspect(s.doc, s.pdf_document, req.page)
            _reject_obsolete_read(s, req.read)
            return response
        with _fitz_op(s), s.fitz_lock:
            return PdfService.page_inspect(s.doc, s.pdf_document, req.page)
    except HTTPException:
        raise
    except ValueError as e:
        raise HTTPException(status_code=400, detail=str(e)) from e
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"页检查失败: {e}") from e


# ---- 流式进度操作 -------------------------------------------------------


def _stream_generator(gen: Iterator[ProgressEvent]) -> StreamingResponse:
    """把 ProgressEvent 生成器逐条序列化为一行一 JSON（真流式模式）。

    每页处理完即 yield，调用方立即可收到该页的进度事件，不必等全批
    处理完。删除文字层等逐页耗时操作用此路径，避免大文字层页
    apply_redactions 阻塞时进度条长时间不动（此前收集到 list 再一次性
    推送，大文字层页会卡死）。
    """
    import json as _json

    def _gen() -> Iterator[bytes]:
        for ev in gen:
            yield (_json.dumps(ev.model_dump(mode="json")) + "\n").encode()

    return StreamingResponse(_gen(), media_type="application/x-ndjson")


@app.post("/session/{sid}/delete_text_layers")
def delete_text_layers(sid: str, req: PageListRequest) -> StreamingResponse:
    """逐页删除文字层,流式进度。

    真流式：每页处理完即推送事件，避免 apply_redactions 同步阻塞时
    进度条长时间不动（此前收集到 list 再一次性推送，大文字层页会卡死）。
    """
    s = _get_registry().get(sid)
    total = len(req.pages)

    def _gen() -> Iterator[ProgressEvent]:
        residual_pages: list[int] = []
        for n, page in enumerate(req.pages):
            if s.cancel_event.is_set():
                break
            has_text = False
            residual = False
            try:
                # page_has_text + delete_text_layers 是一次"检测+删除"单元,
                # 一起进锁避免被并发渲染线程插入(与持 fitz_lock 的 render_* 互斥)。
                with _fitz_op(s), s.fitz_lock, _invalidate_block_edits(s, [page]):
                    has_text = PdfService.page_has_text(s.doc, page)
                    if not has_text:
                        PdfService.delete_text_layers(s.doc, s.pdf_document, page)
                        payload = (0, 0, False)
                    else:
                        deleted, rounds, residual = PdfService.delete_text_layers(
                            s.doc, s.pdf_document, page
                        )
                        payload = (deleted, rounds, residual)
                # 锁外:纯 Python 汇总
                if has_text and residual:
                    residual_pages.append(page)
                yield ProgressEvent(
                    phase=ProgressPhase.DELETE,
                    current=n + 1,
                    total=total,
                    page_index=page,
                    page_payload=payload,
                )
            except Exception as e:
                logger.error("[pdf-backend] delete layer page %d: %s", page, e)
                yield ProgressEvent(
                    phase=ProgressPhase.DELETE,
                    current=n + 1,
                    total=total,
                    page_index=page,
                    page_payload=None,
                )
        yield ProgressEvent(
            phase=ProgressPhase.DELETE,
            current=total,
            total=total,
            message="done",
            page_payload={"residual_pages": residual_pages},
        )

    return _stream_generator(_gen())


def _recover_save_session(s: BackendSession, error: PdfSaveError) -> None:
    s.block_editor = None
    if error.new_doc is not None:
        s.doc = error.new_doc
    if (
        error.committed
        and s.pdf_document.file_path
        and os.path.normcase(os.path.abspath(error.path))
        == os.path.normcase(os.path.abspath(s.pdf_document.file_path))
    ):
        s.pdf_document.is_modified = False
        s.pdf_document.has_structural_change = False


@app.post("/session/{sid}/save", response_model=SaveResponse)
def save(sid: str, req: SaveRequest) -> SaveResponse:
    """保存(rewrite + 落盘)。doc 可能被 close+reopen 替换。"""
    s = _get_registry().get(sid)
    try:
        with _fitz_op(s), s.fitz_lock:
            # Full saves can renumber xrefs even for copy export; stale editor
            # cleanup must not prune objects in the renumbered live document.
            s.block_editor = None
            try:
                result = PdfService.save_with_rewrite(
                    s.doc,
                    s.pdf_document,
                    path=req.path,
                    pdf_settings=_settings_from_dict(req.pdf_settings),
                    rewrite_text_layers=req.rewrite_text_layers,
                    copy_export=req.copy_export,
                    overwrite=req.overwrite,
                    rebind_target=req.rebind_target,
                )
            except PdfSaveError as error:
                logger.warning("PDF save outcome: %s", error)
                _recover_save_session(s, error)
                if error.committed and not req.copy_export:
                    s.pdf_document.is_modified = False
                    s.pdf_document.has_structural_change = False
                    if req.rebind_target and req.path:
                        s.pdf_document.file_path = os.path.abspath(error.path)
                        s.file_path = s.pdf_document.file_path
                detail = {
                    "reason_code": "pdf_save_outcome",
                    "committed": error.committed,
                    "session_usable": not s.doc.is_closed,
                    "path": os.path.abspath(error.path),
                }
                if error.recovery_path:
                    detail["recovery_path"] = os.path.abspath(error.recovery_path)
                raise HTTPException(status_code=500, detail=detail) from error
            if req.rebind_target and not req.copy_export:
                s.file_path = s.pdf_document.file_path or s.file_path
            # 全量压缩时 doc 被替换。
            # 注意：_compress_in_place 内部已经 close 了传入的 s.doc（释放
            # Windows 文件锁的必要步骤）。这里不能再 close 一次——对已关闭
            # 的 fitz doc 调 close 会触发原生 use-after-free（0xC0000409）。
            new_doc = getattr(result, "new_doc", None)
            if new_doc is not None:
                s.block_editor = None
                s.doc = new_doc
        saved_path = result.path or s.pdf_document.file_path or ""
        # 保存只改变持久化状态，不改变页内容/结构。返回完整文档会把数百页 OCR
        # 块重复序列化进 WorkerHost 控制帧；现场 682 页回包达 15.5 MiB，超过
        # 8 MiB 帧上限并在保存已经成功后误报连接失败。
        return SaveResponse(
            path=saved_path,
            diff=ModelDiff(
                modified_flag=s.pdf_document.is_modified if req.copy_export else False,
                structural_flag=s.pdf_document.has_structural_change
                if req.copy_export
                else False,
            ),
        )
    except HTTPException:
        raise
    except (FileExistsError, ValueError) as e:
        raise HTTPException(status_code=422, detail=f"保存拒绝: {e}") from e
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"保存失败: {e}") from e


@app.post("/session/{sid}/deskew", response_model=MutateResponse)
def deskew(sid: str, req: PageListRequest) -> MutateResponse:
    """自动摆正(简化:不在此处跑 OCR 方向检测,需主进程先识别后调 rotate)。

    完整摆正(渲染+OCR方向检测+旋转)需 OCR 服务,放主进程编排:
    主进程 → /render_preview 取图 → OCR 识别得 angle → /rotate 纠正。
    本路由保留供未来后端内部跑 OCR 时扩展。
    """
    raise HTTPException(
        status_code=501, detail="deskew 由主进程编排(渲染+OCR+旋转三步)"
    )


@app.post("/session/{sid}/cancel")
def cancel(
    sid: str, req: PdfReadCancelRequest | None = None
) -> EmptyOk | PdfReadCancelledOk:
    """取消：无 body 保持 T1 写 cancel_event；有 body 只推进读水位。

    读取消单调推进 per-scope 水位（all_reads 原子推进两者）并唤醒门内
    等待读，绝不触碰写 cancel_event，也不污染写事件。ACK cancelled=true，
    客户端必须校验。
    """
    registry = _get_registry()
    if req is None:
        s = registry.get(sid)
        s.cancel_event.set()
        # 唤醒优先门中等待的后台页：已取消的待入 native 读立即在唤醒后退出。
        # 只影响可撤销读；cancel_event/T1 写路径语义不变。
        _wake_read_waiters(s)
        return EmptyOk()
    # 关闭重试仍需确认两域已隔栏。CLOSING 不可逆；已移除会话无可准入读。
    s = registry.find(sid) if req.scope == "all_reads" else registry.get(sid)
    if s is not None:
        _advance_read_watermark(s, req.scope.value, req.before_epoch)
        _wake_read_waiters(s)
    return PdfReadCancelledOk()


@app.post("/session/{sid}/reset_cancel", response_model=EmptyOk)
def reset_cancel(sid: str) -> EmptyOk:
    # 只清写 cancel_event；读水位只增不减（reset 绝不降低水位）。
    s = _get_registry().get(sid)
    s.cancel_event.clear()
    return EmptyOk()


# ---- 辅助 ---------------------------------------------------------------


def _settings_from_dict(d: dict | None):
    """dict → PdfGlobalSettings。"""
    if d is None:
        return None
    from vibeocr.runtime.documents.models.pdf_ocr_options import PdfGlobalSettings

    if hasattr(PdfGlobalSettings, "from_dict"):
        return PdfGlobalSettings.from_dict(d)
    return PdfGlobalSettings(**d)


# ---- 入口 ---------------------------------------------------------------


def _find_free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def main() -> None:
    parser = argparse.ArgumentParser(description="VibeOCR PDF Backend")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=0, help="0 = 自动选空闲端口")
    parser.add_argument("--log-level", default="info")
    args = parser.parse_args()

    logging.basicConfig(
        level=args.log_level.upper(),
        # 格式与 OCR worker / 整个子进程统一：YYYY-MM-DD HH:MM:SS [LEVEL] name: msg
        # 主进程 SubprocessLogForwarder 按此格式正则解析、级别还原、转发。
        format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
        stream=sys.stdout,  # 主进程读 stdout 转发日志
    )

    port = args.port or _find_free_port()
    # 把选定的端口打到 stdout 第一行,主进程读取
    print(f"VIBEOCR_PDF_BACKEND_PORT={port}", flush=True)
    uvicorn.run(app, host=args.host, port=port, log_level=args.log_level)


if (
    __name__ == "__main__"
):  # pragma: no cover - 入口守卫，仅脚本直接执行时触发，单元测试无法覆盖
    main()

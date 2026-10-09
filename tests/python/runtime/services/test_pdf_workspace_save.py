from pathlib import Path
from unittest.mock import MagicMock

import fitz
import pytest
from vibeocr.runtime.documents.models.pdf_document import PdfDocument
from vibeocr.runtime.documents.pdf_service import PdfService


def opened(tmp_path):
    source = tmp_path / "source.pdf"
    doc = fitz.open()
    doc.new_page().insert_text((72, 72), "original")
    doc.save(source)
    doc.close()
    doc = fitz.open(source)
    model = PdfDocument(
        file_path=str(source), is_modified=True, has_structural_change=True
    )
    doc[0].insert_text((72, 100), "edited TAIL202")
    return source, doc, model


def test_copy_preserves_model_and_refuses_commit_collision(tmp_path, monkeypatch):
    source, doc, model = opened(tmp_path)
    target = tmp_path / "copy.pdf"
    PdfService.save_with_rewrite(
        doc,
        model,
        str(target),
        rewrite_text_layers=False,
        copy_export=True,
        overwrite=False,
    )
    assert model.is_modified and model.has_structural_change
    assert model.file_path == str(source)
    with fitz.open(target) as check:
        assert "edited TAIL202" in check[0].get_text()
    target.unlink()
    # Create a competitor immediately before final commit, after the temp was written.
    import os

    operation = "rename" if os.name == "nt" else "link"
    commit = getattr(os, operation)

    def racing_commit(temporary, destination):
        Path(destination).write_bytes(b"competitor")
        return commit(temporary, destination)

    monkeypatch.setattr(os, operation, racing_commit)
    with pytest.raises(FileExistsError):
        PdfService.save_with_rewrite(
            doc,
            model,
            str(target),
            rewrite_text_layers=False,
            copy_export=True,
            overwrite=False,
        )
    assert target.read_bytes() == b"competitor"
    assert model.is_modified and model.has_structural_change
    assert not list(tmp_path.glob(".*.tmp"))
    doc.close()


def test_save_as_releases_old_source_and_subsequent_save_keeps_new_target(tmp_path):
    source, doc, model = opened(tmp_path)
    target = tmp_path / "new.pdf"
    result = PdfService.save_with_rewrite(
        doc, model, str(target), rewrite_text_layers=False, rebind_target=True
    )
    assert doc.is_closed
    assert model.file_path == str(target)
    assert not model.is_modified and not model.has_structural_change
    current = result.new_doc
    assert current is not None
    # A second live entry can now edit and atomically save the original on Windows.
    other = fitz.open(source)
    other_model = PdfDocument(file_path=str(source), is_modified=True)
    other[0].insert_text((72, 120), "other document")
    other_result = PdfService.save_with_rewrite(
        other, other_model, str(source), rewrite_text_layers=False
    )
    if other_result.new_doc:
        other_result.new_doc.close()
    current[0].insert_text((72, 140), "new target TAIL202")
    model.is_modified = True
    saved = PdfService.save_with_rewrite(
        current, model, str(target), rewrite_text_layers=False
    )
    if saved.new_doc:
        saved.new_doc.close()
    with fitz.open(source) as original:
        assert "other document" in original[0].get_text()
        assert "new target TAIL202" not in original[0].get_text()
    with fitz.open(target) as reopened:
        assert "edited TAIL202" in reopened[0].get_text()
        assert "new target TAIL202" in reopened[0].get_text()


def test_failed_close_keeps_session_and_closed_after_error_can_retry():
    from vibeocr.runtime.documents.pdf_backend_process import (
        BackendSession,
        SessionRegistry,
    )

    registry = SessionRegistry()
    doc = MagicMock(is_closed=False)
    session = BackendSession("retry", "source.pdf", doc, MagicMock())
    registry._sessions[session.session_id] = session
    doc.close.side_effect = OSError("close failed")
    with pytest.raises(OSError):
        registry.remove("retry")
    assert registry.count() == 1
    assert session.state == "CLOSING"
    doc.is_closed = True
    registry.remove("retry")
    assert registry.count() == 0
    doc.close.assert_called_once()


def test_rebind_close_failure_releases_opened_target_and_keeps_dirty(
    tmp_path, monkeypatch
):
    source, doc, model = opened(tmp_path)
    proxy = MagicMock()
    proxy.name = str(source)
    proxy.save.side_effect = doc.save
    proxy.close.side_effect = OSError("old source close failed")
    target_docs = []
    real_open = fitz.open

    def recorded_open(path):
        candidate = real_open(path)
        target_docs.append(candidate)
        return candidate

    monkeypatch.setattr(
        "vibeocr.runtime.documents.pdf_service.fitz.open", recorded_open
    )
    with pytest.raises(OSError, match="old source"):
        PdfService.save_with_rewrite(
            proxy,
            model,
            str(tmp_path / "new.pdf"),
            rewrite_text_layers=False,
            rebind_target=True,
        )
    assert len(target_docs) == 1 and target_docs[0].is_closed
    assert (
        model.file_path == str(source)
        and model.is_modified
        and model.has_structural_change
    )
    doc.close()


def test_close_timeout_keeps_registration_until_actual_settlement(monkeypatch):
    from fastapi import HTTPException
    from vibeocr.runtime.documents import pdf_backend_process as backend

    registry = backend.SessionRegistry()
    doc = MagicMock(is_closed=False)
    session = backend.BackendSession("timeout", "source.pdf", doc, MagicMock())
    session.active_ops = 1
    registry._sessions[session.session_id] = session
    with monkeypatch.context() as patch:
        ticks = iter((0.0, 11.0))
        patch.setattr(backend.time, "monotonic", lambda: next(ticks))
        with pytest.raises(HTTPException) as failure:
            registry.remove("timeout")
        assert failure.value.status_code == 409
    assert registry.count() == 1 and session.state == "CLOSING"
    doc.close.assert_not_called()
    session.active_ops = 0
    registry.remove("timeout")
    assert registry.count() == 0


def test_backend_copy_response_preserves_flags_and_target(tmp_path, monkeypatch):
    from vibeocr.runtime.documents import pdf_backend_process as backend
    from vibeocr.runtime.documents.wire_schemas import SaveRequest

    source, doc, model = opened(tmp_path)
    session = backend.BackendSession("copy", str(source), doc, model)
    registry = MagicMock()
    registry.get.return_value = session
    monkeypatch.setattr(backend, "_get_registry", lambda: registry)
    result = backend.save(
        "copy",
        SaveRequest(
            path=str(tmp_path / "copy.pdf"),
            rewrite_text_layers=False,
            copy_export=True,
            overwrite=False,
        ),
    )
    assert result.diff.modified_flag is True and result.diff.structural_flag is True
    assert session.file_path == str(source) and model.file_path == str(source)
    doc.close()


def test_process_adapter_preserves_workspace_fields_and_legacy_request():
    from vibeocr.runtime.documents.adapter import PdfProcessAdapter

    child = MagicMock()
    adapter = PdfProcessAdapter(lambda: child)
    adapter.save(
        "session",
        "copy.pdf",
        {},
        rewrite_text_layers=False,
        copy_export=True,
        overwrite=False,
    )
    child.save.assert_called_once_with(
        "session",
        "copy.pdf",
        {},
        rewrite_text_layers=False,
        copy_export=True,
        overwrite=False,
        rebind_target=False,
    )
    child.save.reset_mock()
    adapter.save("session", "legacy.pdf", {}, rewrite_text_layers=False)
    child.save.assert_called_once_with(
        "session", "legacy.pdf", {}, rewrite_text_layers=False
    )

"""Real-output regressions for save bookkeeping and commit recovery."""

from pathlib import Path
from unittest.mock import MagicMock

import pymupdf as fitz
import pytest
from fastapi import HTTPException
from vibeocr.runtime.documents import pdf_backend_process as backend
from vibeocr.runtime.documents.models.pdf_document import PdfPageInfo
from vibeocr.runtime.documents.models.pdf_ocr_options import PdfGlobalSettings
from vibeocr.runtime.documents.pdf_block_editor import PdfBlockEditor
from vibeocr.runtime.documents.pdf_service import PdfSaveError, PdfService
from vibeocr.runtime.documents.wire_schemas import SaveRequest
from vibeocr.runtime.recognition.models.ocr_result import TextBlock


def opened(tmp_path):
    path = tmp_path / "source.pdf"
    with fitz.open() as seed:
        seed.new_page().insert_text((72, 72), "ORIGINAL")
        seed.save(path)
    doc, model = PdfService.open_doc(str(path))
    doc[0].insert_text((72, 110), "PRECOPY")
    model.is_modified = True
    return path, doc, model


def save(doc, model, **kwargs):
    result = PdfService.save_with_rewrite(
        doc, model, rewrite_text_layers=False, **kwargs
    )
    return result.new_doc if result.new_doc is not None else doc


@pytest.mark.parametrize("export", [False, True])
def test_consecutive_uncompressed_saves_keep_every_revision(tmp_path, export):
    source, doc, model = opened(tmp_path)
    original = source.read_bytes()
    settings = PdfGlobalSettings(compress_on_save=False)
    if export:
        doc = save(doc, model, path=str(tmp_path / "copy.pdf"), copy_export=True)
        assert source.read_bytes() == original and model.is_modified
    doc[0].insert_text((72, 140), "POSTCOPY")
    for word, y in (("FIRST", 170), ("SECOND", 200)):
        doc[0].insert_text((72, y), word)
        model.is_modified = True
        previous = doc
        doc = save(doc, model, pdf_settings=settings)
        assert previous.is_closed
        assert not model.is_modified
        with fitz.open(source) as disk:
            text = disk[0].get_text()
            assert all(
                token in text for token in ("ORIGINAL", "PRECOPY", "POSTCOPY", "FIRST")
            )
            assert word in text
    doc.close()


@pytest.mark.parametrize("phase", ["stage", "replace", "reopen"])
def test_overwrite_failure_has_commit_state_and_editable_retry(
    tmp_path, monkeypatch, phase
):
    source, doc, model = opened(tmp_path)
    original = source.read_bytes()
    registry = MagicMock()
    session = backend.BackendSession("failure", str(source), doc, model)
    registry.get.return_value = session
    monkeypatch.setattr(backend, "_get_registry", lambda: registry)
    real_save, real_replace, real_open = fitz.Document.save, Path.replace, fitz.open

    def fail_save(self, *args, **kwargs):
        if kwargs.get("garbage") == 3:
            raise OSError("stage failure")
        return real_save(self, *args, **kwargs)

    def fail_replace(self, target):
        if Path(target) == source:
            raise OSError("replace failure")
        return real_replace(self, target)

    def fail_reopen(*args, **kwargs):
        if args and Path(args[0]) == source:
            raise OSError("reopen failure")
        return real_open(*args, **kwargs)

    with monkeypatch.context() as patch:
        if phase == "stage":
            patch.setattr(fitz.Document, "save", fail_save)
        elif phase == "replace":
            patch.setattr(Path, "replace", fail_replace)
        else:
            patch.setattr(fitz, "open", fail_reopen)
        with pytest.raises(HTTPException) as failure:
            backend.save("failure", SaveRequest(rewrite_text_layers=False))
        assert phase in str(failure.value.detail)
        assert not session.doc.is_closed
        assert "PRECOPY" in session.doc[0].get_text()
        assert model.is_modified is (phase != "reopen")
        if phase == "reopen":
            assert "saved to disk" in failure.value.detail
        else:
            assert source.read_bytes() == original
    backend.save("failure", SaveRequest(rewrite_text_layers=False))
    with fitz.open(source) as disk:
        assert "PRECOPY" in disk[0].get_text()
    assert not list(tmp_path.glob(".*.tmp"))
    session.doc.close()


def test_incremental_reopen_failure_never_truncates_commit(tmp_path, monkeypatch):
    source, doc, model = opened(tmp_path)
    real_open = fitz.open
    with monkeypatch.context() as patch:
        patch.setattr(
            fitz, "open", lambda *a, **k: (_ for _ in ()).throw(OSError("reopen"))
        )
        with pytest.raises(PdfSaveError) as failure:
            PdfService.save_incremental(doc, str(source))
        assert failure.value.committed and failure.value.new_doc is doc
        assert not Path(str(source) + PdfService._INCREMENTAL_MARKER_SUFFIX).exists()
    with real_open(source) as disk:
        assert "PRECOPY" in disk[0].get_text()
    doc[0].insert_text((72, 140), "RETRY")
    doc = save(doc, model, pdf_settings=PdfGlobalSettings(compress_on_save=False))
    assert "PRECOPY" in doc[0].get_text() and "RETRY" in doc[0].get_text()
    doc.close()


def test_rebind_reopen_failure_keeps_original_binding_and_reports_saved_target(
    tmp_path, monkeypatch
):
    source, doc, model = opened(tmp_path)
    target = tmp_path / "new.pdf"
    real_open = fitz.open
    with monkeypatch.context() as patch:
        patch.setattr(
            fitz, "open", lambda *a, **k: (_ for _ in ()).throw(OSError("reopen"))
        )
        with pytest.raises(PdfSaveError) as failure:
            save(doc, model, path=str(target), rebind_target=True)
        assert failure.value.committed and failure.value.path == str(target)
        assert model.file_path == str(source) and model.is_modified
        assert "PRECOPY" in doc[0].get_text()
    with real_open(target) as disk:
        assert "PRECOPY" in disk[0].get_text()
    doc = save(doc, model, path=str(target), rebind_target=True)
    assert model.file_path == str(target) and not model.is_modified
    doc.close()


def test_failed_stage_recovery_retains_file_with_unsaved_edits(tmp_path, monkeypatch):
    source, doc, model = opened(tmp_path)
    original = source.read_bytes()
    with monkeypatch.context() as patch:
        patch.setattr(
            Path, "replace", lambda *a: (_ for _ in ()).throw(OSError("replace"))
        )
        patch.setattr(
            fitz, "open", lambda *a, **k: (_ for _ in ()).throw(OSError("recover"))
        )
        with pytest.raises(PdfSaveError) as failure:
            save(doc, model)
    error = failure.value
    assert not error.committed and error.new_doc is None
    assert source.read_bytes() == original and model.is_modified
    stage = Path(error.recovery_path)
    with fitz.open(stage) as recovered:
        assert "PRECOPY" in recovered[0].get_text()
    stage.unlink()


def test_copy_discards_editor_xrefs_before_full_save_and_next_correction(
    tmp_path, monkeypatch
):
    source, doc, model = opened(tmp_path)
    model.pages = [PdfPageInfo(page_index=0, has_text_layer=True)]
    settings = PdfGlobalSettings()
    editor = PdfBlockEditor(doc)
    blocks = [
        TextBlock(
            text="CORRECTED",
            score=1,
            bbox=(100, 100, 800, 200),
            is_manually_edited=True,
        )
    ]
    assert editor.rewrite(model, 0, blocks, 0, settings) == (1, 0)
    session = backend.BackendSession(
        "copy", str(source), doc, model, block_editor=editor
    )
    registry = MagicMock()
    registry.get.return_value = session
    monkeypatch.setattr(backend, "_get_registry", lambda: registry)
    backend.save(
        "copy",
        SaveRequest(
            path=str(tmp_path / "copy.pdf"), copy_export=True, rewrite_text_layers=False
        ),
    )
    assert session.block_editor is None and model.is_modified
    assert "CORRECTED" in doc[0].get_text()
    replacement = PdfBlockEditor(doc)
    blocks[0].text = "AFTERCOPY"
    assert replacement.rewrite(model, 0, blocks, 0, settings) == (1, 0)
    doc = save(doc, model, pdf_settings=PdfGlobalSettings(compress_on_save=False))
    assert "AFTERCOPY" in doc[0].get_text() and "CORRECTED" not in doc[0].get_text()
    doc.close()


def test_legacy_batch_checkpoints_adopt_fresh_handle(tmp_path, monkeypatch):
    from vibeocr.runtime.documents.wire_schemas import BatchAddTextLayerRequest

    source = tmp_path / "batch.pdf"
    with fitz.open() as seed:
        seed.new_page()
        seed.new_page()
        seed.save(source)
    doc, model = PdfService.open_doc(str(source))
    session = backend.BackendSession("batch", str(source), doc, model)
    registry = MagicMock()
    registry.get.return_value = session
    monkeypatch.setattr(backend, "_get_registry", lambda: registry)
    for index, text in enumerate(("FIRST", "SECOND")):
        response = backend.add_text_layer_batch(
            "batch",
            BatchAddTextLayerRequest.model_validate(
                {
                    "save": True,
                    "pages": [
                        {
                            "page": index,
                            "ocr_result": {
                                "preproc_angle": 0,
                                "text_blocks": [
                                    {
                                        "text": text,
                                        "score": 1,
                                        "bbox": [100, 150, 620, 230],
                                        "polygon": [
                                            100,
                                            150,
                                            620,
                                            150,
                                            620,
                                            230,
                                            100,
                                            230,
                                        ],
                                    }
                                ],
                            },
                        }
                    ],
                }
            ),
        )
        assert response.extra["saved"] is True
        assert not model.is_modified
    with fitz.open(source) as disk:
        assert "FIRST" in disk[0].get_text()
        assert "SECOND" in disk[1].get_text()
    session.doc.close()

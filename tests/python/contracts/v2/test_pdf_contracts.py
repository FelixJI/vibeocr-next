"""PDF HTTP v2 DTO 的公开解析与序列化契约。"""

from __future__ import annotations

import operator

import pytest
from jsonschema import validate
from vibeocr.runtime_contracts import (
    ContractError,
)
from vibeocr.runtime_contracts import (
    PdfDocumentMirror as PublicPdfDocumentMirror,
)
from vibeocr.runtime_contracts.generated import RESPONSE_JSON_SCHEMAS
from vibeocr.runtime_contracts.pdf import (
    PdfDetectResult,
    PdfMutationResult,
    PdfOpenResult,
    PdfProgressEvent,
    PdfProgressPhase,
    PdfSaveResult,
)


def test_pdf_document_mirror_is_exported_from_public_package() -> None:
    assert PublicPdfDocumentMirror is not None


@pytest.mark.parametrize(
    "payload",
    [
        {"instance_id": "runtime-1", "session_id": "pdf-1", "model": {}},
        {
            "schema_version": 3,
            "instance_id": "runtime-1",
            "session_id": "pdf-1",
            "model": {},
        },
        {"schema_version": 2, "session_id": "pdf-1", "model": {}},
        {
            "schema_version": 2,
            "instance_id": "",
            "session_id": "pdf-1",
            "model": {},
        },
    ],
)
def test_pdf_response_requires_v2_envelope(payload: dict[str, object]) -> None:
    with pytest.raises(ContractError):
        PdfOpenResult.from_payload(payload)


def test_pdf_open_result_parses_nested_mirror_and_retains_response_extensions() -> None:
    payload = {
        "schema_version": 2,
        "instance_id": "runtime-1",
        "session_id": "pdf-1",
        "model": {
            "file_path": "C:/docs/a.pdf",
            "pages": [
                {
                    "page_index": 0,
                    "rect": [0, 0, 612, 792],
                    "text_layers": [
                        {
                            "index": 1,
                            "text_preview": "hello",
                            "char_count": 5,
                            "bbox": [1, 2, 3, 4],
                            "color_id": 7,
                        }
                    ],
                    "ocr_text_blocks": [
                        {
                            "text": "hello",
                            "score": 0.9,
                            "bbox": [10, 20, 30, 40],
                            "polygon": [10, 20, 30, 20, 30, 40, 10, 40],
                        }
                    ],
                    "future_page_hint": "kept",
                }
            ],
            "future_model_hint": {"revision": 3},
        },
        "future_response_hint": True,
    }

    result = PdfOpenResult.from_payload(payload)

    assert result.session_id == "pdf-1"
    assert result.model.pages[0].rect == (0.0, 0.0, 612.0, 792.0)
    assert result.model.pages[0].text_layers[0].bbox == (1.0, 2.0, 3.0, 4.0)
    assert result.model.pages[0].ocr_text_blocks[0].polygon == (
        10.0,
        20.0,
        30.0,
        20.0,
        30.0,
        40.0,
        10.0,
        40.0,
    )
    assert result.extra == {"future_response_hint": True}
    assert result.model.extra == {"future_model_hint": {"revision": 3}}
    assert result.model.pages[0].extra == {"future_page_hint": "kept"}
    with pytest.raises(TypeError):
        operator.setitem(result.extra, "new", "forbidden")
    canonical = result.to_payload()
    assert canonical["future_response_hint"] is True
    assert canonical["model"]["render_dpi"] == 300
    assert canonical["model"]["thumbnail_dpi"] == 96
    assert canonical["model"]["pages"][0]["rotation"] == 0
    assert canonical["model"]["pages"][0]["ocr_text_blocks"][0]["bbox"] == [
        10.0,
        20.0,
        30.0,
        40.0,
    ]


def test_pdf_mutation_result_normalizes_model_diff_and_operation_extra() -> None:
    payload = {
        "schema_version": 2,
        "instance_id": "runtime-1",
        "diff": {
            "replaced_pages": [{"page_index": 2, "has_text_layer": True}],
            "structural_change": False,
            "modified_flag": True,
            "structural_flag": None,
            "invalidated_thumbnails": [2, 4],
            "future_diff_hint": "kept",
        },
        "extra": {"saved": True},
        "future_response_hint": 9,
    }

    result = PdfMutationResult.from_payload(payload)

    assert result.diff.replaced_pages[0].page_index == 2
    assert result.diff.replaced_pages[0].has_text_layer is True
    assert result.diff.invalidated_thumbnails == (2, 4)
    assert result.diff.extra == {"future_diff_hint": "kept"}
    assert result.operation_extra == {"saved": True}
    assert result.extra == {"future_response_hint": 9}
    assert result.to_payload()["diff"]["full_model"] is None
    assert result.to_payload()["extra"] == {"saved": True}


def test_pdf_progress_event_parses_json_with_defaults_and_extension() -> None:
    event = PdfProgressEvent.from_json(
        b'{"phase":"render","page_index":3,"future_progress_hint":"fast"}'
    )

    assert event.phase is PdfProgressPhase.RENDER
    assert event.current == 0
    assert event.total == 0
    assert event.message is None
    assert event.page_index == 3
    assert event.page_payload is None
    assert event.extra == {"future_progress_hint": "fast"}
    assert event.to_payload() == {
        "phase": "render",
        "current": 0,
        "total": 0,
        "message": None,
        "page_index": 3,
        "page_payload": None,
        "future_progress_hint": "fast",
    }


def test_pdf_save_result_parses_path_and_full_model_diff() -> None:
    result = PdfSaveResult.from_payload(
        {
            "schema_version": 2,
            "instance_id": "runtime-1",
            "path": "C:/docs/saved.pdf",
            "diff": {
                "structural_change": True,
                "full_model": {"file_path": "C:/docs/saved.pdf", "pages": []},
            },
        }
    )

    assert result.path == "C:/docs/saved.pdf"
    assert result.diff.structural_change is True
    assert result.diff.full_model is not None
    assert result.diff.full_model.file_path == "C:/docs/saved.pdf"


def test_pdf_detect_result_parses_text_layer_mirrors() -> None:
    result = PdfDetectResult.from_payload(
        {
            "schema_version": 2,
            "instance_id": "runtime-1",
            "text_layers": [
                {
                    "index": 0,
                    "text_preview": "abc",
                    "char_count": 3,
                    "bbox": [0, 1, 2, 3],
                    "color_id": 4,
                }
            ],
        }
    )

    assert result.text_layers[0].text_preview == "abc"
    assert result.to_payload()["text_layers"][0]["bbox"] == [0.0, 1.0, 2.0, 3.0]


@pytest.mark.parametrize(
    ("operation_id", "payload"),
    [
        (
            "openPdfSession",
            PdfOpenResult.from_payload(
                {
                    "schema_version": 2,
                    "instance_id": "runtime-1",
                    "session_id": "pdf-1",
                    "model": {"pages": []},
                }
            ).to_payload(),
        ),
        (
            "rotatePdfPages",
            PdfMutationResult.from_payload(
                {
                    "schema_version": 2,
                    "instance_id": "runtime-1",
                    "diff": {},
                }
            ).to_payload(),
        ),
        (
            "savePdfSession",
            PdfSaveResult.from_payload(
                {
                    "schema_version": 2,
                    "instance_id": "runtime-1",
                    "path": "C:/docs/saved.pdf",
                    "diff": {},
                }
            ).to_payload(),
        ),
        (
            "detectPdfTextLayers",
            PdfDetectResult.from_payload(
                {
                    "schema_version": 2,
                    "instance_id": "runtime-1",
                    "text_layers": [],
                }
            ).to_payload(),
        ),
    ],
)
def test_pdf_response_payloads_match_generated_schema(
    operation_id: str, payload: dict[str, object]
) -> None:
    validate(instance=payload, schema=RESPONSE_JSON_SCHEMAS[operation_id])

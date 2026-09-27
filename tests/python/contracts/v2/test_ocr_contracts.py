"""OCR ``ocr.v1`` payload 的公开 DTO 契约。"""

from __future__ import annotations

import operator

import pytest
from vibeocr.runtime_contracts import (
    OCR_RESULT_PAYLOAD_TYPE,
    OcrResultV1,
    OcrTextBlockV1,
    parse_ocr_result_payload,
)


def test_ocr_result_parses_complete_payload_and_roundtrips_json_shape() -> None:
    payload = {
        "raw_text": "第一行\n第二行",
        "markdown_text": "第一行  \n第二行",
        "html_text": "<p>第一行</p><p>第二行</p>",
        "avg_score": 0.925,
        "pipeline_type": "OCR",
        "preproc_angle": 90,
        "content_list": [{"type": "text", "text": "第一行"}],
        "text_with_scores": [["第一行", 0.95], ["第二行", 0.9]],
        "low_confidence_items": [["第二行", 0.9]],
        "text_blocks": [
            {
                "text": "第一行",
                "score": 0.95,
                "bbox": [10, 20, 300, 80],
                "polygon": [10, 20, 300, 20, 300, 80, 10, 80],
                "page_idx": 0,
                "is_manually_edited": True,
                "content_index": 0,
                "content_id": "block-0",
                "label": "text",
                "order": 3,
            }
        ],
        "images": {"figure-1": {"present": True, "size": 128}},
        "image_width": 1920,
        "image_height": 1080,
        "future_hint": {"mode": "new"},
    }

    result = parse_ocr_result_payload(OCR_RESULT_PAYLOAD_TYPE, payload)

    assert isinstance(result, OcrResultV1)
    assert isinstance(result.text_blocks[0], OcrTextBlockV1)
    assert result.text_blocks[0].bbox == (10.0, 20.0, 300.0, 80.0)
    assert result.text_blocks[0].polygon == (
        10.0,
        20.0,
        300.0,
        20.0,
        300.0,
        80.0,
        10.0,
        80.0,
    )
    assert result.text_with_scores == (("第一行", 0.95), ("第二行", 0.9))
    assert result.low_confidence_items == (("第二行", 0.9),)
    assert result.extra == {"future_hint": {"mode": "new"}}
    with pytest.raises(TypeError):
        operator.setitem(result.extra, "another", True)
    assert result.to_payload() == payload


def test_ocr_result_rejects_invalid_canonical_table_model() -> None:
    invalid_table = {
        "schema_version": 2,
        "table_id": "table-0",
        "row_count": 1,
        "column_count": 1,
        "coordinate_space": "normalized_1000",
        "cells": [],
        "provenance": None,
    }

    with pytest.raises(ValueError, match="table schema_version"):
        OcrResultV1.from_payload(
            {
                "content_list": [
                    {
                        "type": "table",
                        "table": invalid_table,
                        "table_body": "<table><tr><td>兼容内容</td></tr></table>",
                    }
                ]
            }
        )


def test_ocr_result_rejects_non_object_canonical_table_field() -> None:
    with pytest.raises(ValueError, match="canonical table must be an object"):
        OcrResultV1.from_payload(
            {"content_list": [{"type": "table", "table": ["not-an-object"]}]}
        )


def test_ocr_text_block_rejects_polygon_without_four_coordinate_pairs() -> None:
    with pytest.raises(ValueError, match="polygon"):
        OcrTextBlockV1.from_payload(
            {"text": "broken", "score": 0.5, "polygon": [0, 1, 2, 3, 4, 5]}
        )


def test_ocr_result_uses_legacy_text_as_raw_text_with_stable_defaults() -> None:
    result = OcrResultV1.from_payload({"text": "兼容纯文本"})

    assert result.raw_text == "兼容纯文本"
    assert result.markdown_text == ""
    assert result.html_text == ""
    assert result.avg_score == 0.0
    assert result.pipeline_type == "OCR"
    assert result.preproc_angle == 0
    assert result.content_list == ()
    assert result.text_with_scores == ()
    assert result.low_confidence_items == ()
    assert result.text_blocks == ()
    assert result.images == {}
    assert result.image_width == 0
    assert result.image_height == 0
    assert "text" not in result.extra
    assert result.to_payload()["raw_text"] == "兼容纯文本"


def test_ocr_result_accepts_valid_canonical_and_legacy_html_tables() -> None:
    canonical_table = {
        "schema_version": 1,
        "table_id": "table-0",
        "row_count": 1,
        "column_count": 1,
        "coordinate_space": "normalized_1000",
        "cells": [
            {
                "cell_id": "r0c0",
                "row": 0,
                "column": 0,
                "rowspan": 1,
                "colspan": 1,
                "text": "值",
                "is_header": False,
                "bbox": None,
                "confidence": 0.98,
                "source_refs": [],
            }
        ],
        "provenance": None,
    }
    content_list = [
        {"type": "table", "table": canonical_table},
        {
            "type": "table",
            "table": None,
            "table_body": "<table><tr><td>legacy</td></tr></table>",
        },
    ]

    result = OcrResultV1.from_payload({"content_list": content_list})

    assert result.to_payload()["content_list"] == content_list


def test_parse_ocr_result_rejects_wrong_payload_type() -> None:
    with pytest.raises(ValueError, match="payload_type"):
        parse_ocr_result_payload("ocr.v2", {})


@pytest.mark.parametrize(
    ("payload", "message"),
    [
        ([], "must be an object"),
        ({"raw_text": None}, "raw_text must be a string"),
        ({"markdown_text": 1}, "markdown_text must be a string"),
        ({"html_text": []}, "html_text must be a string"),
        ({"avg_score": True}, "avg_score must be a finite number"),
        ({"pipeline_type": 1}, "pipeline_type must be a string"),
        ({"preproc_angle": 1.5}, "preproc_angle must be an integer"),
        ({"content_list": "blocks"}, "content_list must be an array"),
        ({"content_list": ["block"]}, "content_list entry must be an object"),
        (
            {"text_with_scores": [["text"]]},
            "text_with_scores entries must contain text and score",
        ),
        (
            {"low_confidence_items": [["text", float("nan")]]},
            "low_confidence_items must be a finite number",
        ),
        ({"text_blocks": {}}, "text_blocks must be an array"),
        ({"text_blocks": [None]}, "OcrTextBlockV1 must be an object"),
        ({"images": []}, "images must be an object"),
        ({"image_width": True}, "image_width must be an integer"),
        ({"image_height": 1.5}, "image_height must be an integer"),
    ],
)
def test_ocr_result_rejects_invalid_typed_fields(payload: object, message: str) -> None:
    with pytest.raises(ValueError, match=message):
        OcrResultV1.from_payload(payload)


@pytest.mark.parametrize(
    ("payload", "message"),
    [
        ({"score": 0.5}, "requires text and score"),
        ({"text": "x"}, "requires text and score"),
        ({"text": 1, "score": 0.5}, "text must be a string"),
        ({"text": "x", "score": True}, "score must be a finite number"),
        ({"text": "x", "score": 0.5, "bbox": [1, 2, 3]}, "bbox"),
        ({"text": "x", "score": 0.5, "page_idx": False}, "page_idx"),
        (
            {"text": "x", "score": 0.5, "is_manually_edited": 1},
            "is_manually_edited",
        ),
        ({"text": "x", "score": 0.5, "content_index": 1.5}, "content_index"),
        ({"text": "x", "score": 0.5, "content_id": 7}, "content_id"),
        ({"text": "x", "score": 0.5, "label": None}, "label"),
        ({"text": "x", "score": 0.5, "order": True}, "order"),
    ],
)
def test_ocr_text_block_rejects_invalid_typed_fields(
    payload: dict[str, object], message: str
) -> None:
    with pytest.raises(ValueError, match=message):
        OcrTextBlockV1.from_payload(payload)

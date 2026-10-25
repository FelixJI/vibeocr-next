from __future__ import annotations

import copy
import json

import pytest
from PIL import Image

from scripts.paddle_smoke_fixtures import draw_structure_pdfs
from scripts.verify_paddle_pdf_structure import verify


def _evidence(tmp_path, monkeypatch):
    for name in (
        "table_merged_zh_en.png",
        "wireless_dispatch.png",
        "document_mixed.png",
        "document_columns_90.png",
    ):
        Image.new("RGB", (30, 21), "white").save(tmp_path / name)
    draw_structure_pdfs(tmp_path)
    checked = []
    monkeypatch.setattr(
        "scripts.verify_paddle_pdf_structure._check_result",
        lambda name, result, w, h: checked.append((name, result.content_list, w, h)),
    )
    blocks = [[{"type": "text", "text": str(index)}] for index in range(2)]
    job = {
        "task_id": "job",
        "Items": [{"ClientItemKey": f"page-{i}", "ItemId": str(i)} for i in range(2)],
        "outcomes": [
            {"ItemId": str(i), "Payload": {"content_list": blocks[i]}} for i in range(2)
        ],
    }
    health = {
        "state": "passed",
        "evidence": {
            "mode": "paddle_table",
            "input_kind": "pdf",
            "fixture_path": str(tmp_path / "document_table_pages.pdf"),
            "job": job,
            "pages": [
                {
                    "page_index": i,
                    "ClientItemKey": f"page-{i}",
                    "ItemId": str(i),
                    "content_blocks": copy.deepcopy(blocks[i]),
                    "structured_inspected": True,
                }
                for i in range(2)
            ],
        },
    }
    return health, checked


def test_public_pdf_checks_both_frozen_contracts(tmp_path, monkeypatch):
    health, checked = _evidence(tmp_path, monkeypatch)
    path = tmp_path / "health.json"
    path.write_text(json.dumps(health), encoding="utf-8")
    assert verify(tmp_path, path)["passed"]
    assert [(name, w, h) for name, _, w, h in checked] == [
        ("table_merged_zh_en.png", 1000, 1000),
        ("wireless_dispatch.png", 1000, 1000),
    ]


@pytest.mark.parametrize("corruption", ["page", "field", "duplicate"])
def test_public_pdf_rejects_cross_page_or_mutated_evidence(
    tmp_path, monkeypatch, corruption
):
    health, _ = _evidence(tmp_path, monkeypatch)
    proof = health["evidence"]
    if corruption == "page":
        proof["pages"][1]["ItemId"] = "0"
    elif corruption == "field":
        proof["pages"][1]["content_blocks"][0]["text"] = "changed"
    else:
        proof["job"]["outcomes"][1]["Payload"] = copy.deepcopy(
            proof["job"]["outcomes"][0]["Payload"]
        )
        proof["pages"][1]["content_blocks"] = copy.deepcopy(
            proof["pages"][0]["content_blocks"]
        )
    path = tmp_path / "health.json"
    path.write_text(json.dumps(health), encoding="utf-8")
    with pytest.raises(AssertionError):
        verify(tmp_path, path)

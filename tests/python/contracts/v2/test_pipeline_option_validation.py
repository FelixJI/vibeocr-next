"""wire v1 pipeline options 的严格类型/范围/路径根校验合同。

回归锚点（旧实现会失败）：旧 parser 只校验选项名，不校验每项类型/范围；
``formula_recognition_model_dir`` 曾接受任意路径。
"""

from __future__ import annotations

import pytest
from vibeocr.runtime_contracts.parser import (
    ContractError,
    parse_pipeline_selection,
)


def _selection(pipeline_id: str, options: dict) -> dict:
    return {
        "pipeline_id": pipeline_id,
        "options_version": 1,
        "options": options,
    }


# ---------------------------------------------------------------------------
# 布尔选项：不接受 int 0/1、字符串
# ---------------------------------------------------------------------------


@pytest.mark.parametrize(
    ("pipeline_id", "option"),
    [
        ("OCR", "use_doc_orientation_classify"),
        ("OCR", "use_doc_unwarping"),
        ("OCR", "use_textline_orientation"),
        ("PP-StructureV3", "use_seal_recognition"),
        ("PP-StructureV3", "use_chart_recognition"),
        ("PaddleOCR-VL", "vl_use_layout_detection"),
        ("PaddleOCR-VL", "use_ocr_for_image_block"),
        ("TABLE_RECOGNITION", "use_table_orientation_classify"),
        ("TABLE_RECOGNITION", "use_ocr_results_with_table_cells"),
        ("FORMULA_RECOGNITION", "use_doc_orientation_classify"),
    ],
)
def test_bool_options_reject_int_and_string(pipeline_id: str, option: str) -> None:
    for bad in (0, 1, "true", None):
        with pytest.raises(ContractError):
            parse_pipeline_selection(_selection(pipeline_id, {option: bad}))


def test_bool_options_accept_real_booleans() -> None:
    selection = parse_pipeline_selection(
        _selection("OCR", {"use_doc_orientation_classify": False})
    )
    assert selection.options == {"use_doc_orientation_classify": False}


# ---------------------------------------------------------------------------
# 公式构造参数：批量大小与模型名
# ---------------------------------------------------------------------------


def test_formula_batch_size_rejects_bool_float_and_out_of_range() -> None:
    for bad in (True, 1.0, 0, -1, 65, "8"):
        with pytest.raises(ContractError):
            parse_pipeline_selection(
                _selection(
                    "FORMULA_RECOGNITION", {"formula_recognition_batch_size": bad}
                )
            )


def test_formula_batch_size_accepts_documented_range() -> None:
    for good in (1, 8, 64):
        selection = parse_pipeline_selection(
            _selection("FORMULA_RECOGNITION", {"formula_recognition_batch_size": good})
        )
        assert selection.options["formula_recognition_batch_size"] == good


def test_formula_model_name_rejects_unknown_model() -> None:
    with pytest.raises(ContractError, match="formula_recognition_model_name"):
        parse_pipeline_selection(
            _selection(
                "FORMULA_RECOGNITION",
                {"formula_recognition_model_name": "Not-A-Real-Model"},
            )
        )


def test_formula_model_name_accepts_locked_model_and_null() -> None:
    selection = parse_pipeline_selection(
        _selection(
            "FORMULA_RECOGNITION",
            {"formula_recognition_model_name": "PP-FormulaNet_plus-S"},
        )
    )
    assert selection.options["formula_recognition_model_name"] == "PP-FormulaNet_plus-S"
    selection = parse_pipeline_selection(
        _selection("FORMULA_RECOGNITION", {"formula_recognition_model_name": None})
    )
    assert selection.options["formula_recognition_model_name"] is None


# ---------------------------------------------------------------------------
# 模型目录：只接受授权模型根（PADDLE_PDX_CACHE_HOME）内的真实目录
# ---------------------------------------------------------------------------


def test_model_dir_rejected_without_authorized_root(monkeypatch) -> None:
    monkeypatch.delenv("PADDLE_PDX_CACHE_HOME", raising=False)
    with pytest.raises(ContractError, match="PADDLE_PDX_CACHE_HOME"):
        parse_pipeline_selection(
            _selection(
                "FORMULA_RECOGNITION", {"formula_recognition_model_dir": "C:/models/f"}
            )
        )


def test_model_dir_rejected_outside_authorized_root(monkeypatch, tmp_path) -> None:
    root = tmp_path / "paddlex-cache"
    root.mkdir()
    inside = root / "PP-FormulaNet_plus-M"
    inside.mkdir()
    monkeypatch.setenv("PADDLE_PDX_CACHE_HOME", str(root))
    outside = tmp_path / "outside"
    outside.mkdir()
    with pytest.raises(ContractError, match="authorized model root"):
        parse_pipeline_selection(
            _selection(
                "FORMULA_RECOGNITION",
                {"formula_recognition_model_dir": str(outside)},
            )
        )


def test_model_dir_rejected_when_not_a_directory(monkeypatch, tmp_path) -> None:
    root = tmp_path / "paddlex-cache"
    root.mkdir()
    monkeypatch.setenv("PADDLE_PDX_CACHE_HOME", str(root))
    with pytest.raises(ContractError, match="existing model directory"):
        parse_pipeline_selection(
            _selection(
                "FORMULA_RECOGNITION",
                {"formula_recognition_model_dir": str(root / "missing")},
            )
        )


def test_model_dir_accepts_directory_inside_authorized_root(
    monkeypatch, tmp_path
) -> None:
    root = tmp_path / "paddlex-cache"
    inside = root / "PP-FormulaNet_plus-M"
    inside.mkdir(parents=True)
    monkeypatch.setenv("PADDLE_PDX_CACHE_HOME", str(root))
    selection = parse_pipeline_selection(
        _selection(
            "FORMULA_RECOGNITION",
            {"formula_recognition_model_dir": str(inside)},
        )
    )
    assert selection.options["formula_recognition_model_dir"] == str(inside)


def test_model_dir_accepts_null(monkeypatch) -> None:
    monkeypatch.delenv("PADDLE_PDX_CACHE_HOME", raising=False)
    selection = parse_pipeline_selection(
        _selection("FORMULA_RECOGNITION", {"formula_recognition_model_dir": None})
    )
    assert selection.options["formula_recognition_model_dir"] is None


# ---------------------------------------------------------------------------
# MinerU 遗留选项类型/范围
# ---------------------------------------------------------------------------


def test_mineru_enum_options_reject_unknown_values() -> None:
    for option, bad in (
        ("parse_method", "fast"),
        ("backend", "turbo-engine"),
        ("effort", "low"),
    ):
        with pytest.raises(ContractError):
            parse_pipeline_selection(_selection("MinerU", {option: bad}))


def test_mineru_page_ids_reject_negative_and_bool() -> None:
    for option, bad in (
        ("start_page_id", -1),
        ("start_page_id", True),
        ("end_page_id", -2),
    ):
        with pytest.raises(ContractError):
            parse_pipeline_selection(_selection("MinerU", {option: bad}))


def test_mineru_lang_list_rejects_non_string_entries() -> None:
    for bad in (["ch", 3], "ch", [""], [None]):
        with pytest.raises(ContractError):
            parse_pipeline_selection(_selection("MinerU", {"lang_list": bad}))


def test_mineru_options_accept_valid_values() -> None:
    selection = parse_pipeline_selection(
        _selection(
            "MinerU",
            {
                "parse_method": "ocr",
                "backend": "pipeline",
                "effort": "high",
                "enable_formula": False,
                "enable_table": True,
                "lang_list": ["ch", "en"],
                "start_page_id": 2,
                "end_page_id": None,
            },
        )
    )
    assert selection.options["lang_list"] == ["ch", "en"]
    assert selection.options["end_page_id"] is None

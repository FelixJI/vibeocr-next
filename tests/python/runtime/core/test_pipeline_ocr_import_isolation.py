from __future__ import annotations

import builtins

import pytest
from vibeocr.runtime.recognition.core.pipelines.pipeline_ocr import _create_ocr_pipeline
from vibeocr.runtime.recognition.core.pipelines.pipeline_pp_structure import (
    _create_pp_structure_pipeline,
)
from vibeocr.runtime.recognition.core.pipelines.pipeline_table import (
    _create_table_pipeline,
)
from vibeocr.runtime.recognition.paddle_onnx import PaddleOnnxError, constructor_kwargs


@pytest.mark.parametrize(
    "factory",
    [_create_ocr_pipeline, _create_pp_structure_pipeline, _create_table_pipeline],
)
def test_native_factory_request_rejected_before_import(factory, monkeypatch):
    real_import = builtins.__import__
    imported = []

    def record(name, *args, **kwargs):
        if name in {"paddle", "paddleocr"}:
            imported.append(name)
            raise AssertionError("native framework import")
        return real_import(name, *args, **kwargs)

    monkeypatch.setattr(builtins, "__import__", record)
    with pytest.raises(PaddleOnnxError, match="ONNX"):
        factory("cpu")
    assert imported == []


def test_native_constructor_options_rejected_without_snapshot(monkeypatch):
    monkeypatch.delenv("VIBEOCR_PADDLE_ORT_DEVICE", raising=False)
    monkeypatch.delenv("VIBEOCR_PADDLE_ONNX", raising=False)
    monkeypatch.setattr(
        "vibeocr.runtime.recognition.paddle_onnx.is_ort_environment", lambda: False
    )
    with pytest.raises(PaddleOnnxError, match="Paddle ONNX environment"):
        constructor_kwargs(object())

"""Windows OCR 原生依赖装载顺序契约。"""

from __future__ import annotations

import sys
import types

from vibeocr.runtime.recognition.native_runtime import (
    prepare_windows_ocr_native_runtime,
)


def test_prepares_onnxruntime_without_initializing_engine_models(
    monkeypatch,
) -> None:
    imported: list[str] = []
    onnxruntime = types.ModuleType("onnxruntime")
    paddleocr = types.ModuleType("paddleocr")

    class UnexpectedPaddleOCR:
        def __init__(self) -> None:
            raise AssertionError("native preparation must not initialize models")

    paddleocr.PaddleOCR = UnexpectedPaddleOCR  # type: ignore[attr-defined]
    monkeypatch.setattr(sys, "platform", "win32")
    monkeypatch.setitem(sys.modules, "onnxruntime", onnxruntime)
    monkeypatch.setitem(sys.modules, "paddleocr", paddleocr)

    prepare_windows_ocr_native_runtime(imported.append)

    assert imported == ["onnxruntime"]


def test_native_preparation_is_noop_off_windows(monkeypatch) -> None:
    imported: list[str] = []
    monkeypatch.setattr(sys, "platform", "linux")

    prepare_windows_ocr_native_runtime(imported.append)

    assert imported == []

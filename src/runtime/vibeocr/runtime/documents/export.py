"""OCR 导出用例；HTTP host 仅映射请求和错误。"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Any

from vibeocr.runtime.recognition.models.ocr_result import OCRResult

from .export_service import ExportService
from .tables.blocks import validate_table_blocks


@dataclass(frozen=True, slots=True)
class OcrExportRequest:
    raw_text: str
    markdown_text: str
    html_text: str
    raw_blocks: list[Any]
    output_path: Path
    format: str
    overwrite: bool = False


def parse_export_request(body: dict[str, Any]) -> OcrExportRequest:
    raw_blocks = list(body.get("raw_blocks", []))
    validate_table_blocks(raw_blocks)
    return OcrExportRequest(
        raw_text=str(body.get("raw_text", "")),
        markdown_text=str(body.get("markdown_text", "")),
        html_text=str(body.get("html_text", "")),
        raw_blocks=raw_blocks,
        output_path=Path(str(body.get("output_path", ""))),
        format=str(body.get("format", "")),
        overwrite=bool(body.get("overwrite", False)),
    )


def write_export(request: OcrExportRequest) -> int | None:
    result = OCRResult(
        raw_text=request.raw_text,
        markdown_text=request.markdown_text,
        html_text=request.html_text,
        content_list=request.raw_blocks,
    )
    if not ExportService.export(result, request.output_path, request.format):
        return None
    return request.output_path.stat().st_size if request.output_path.exists() else 0

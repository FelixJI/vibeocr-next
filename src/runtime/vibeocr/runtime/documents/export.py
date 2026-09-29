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


def iter_image_refs(raw_blocks: list[Any]):
    """迭代 raw_blocks 中所有图像资产引用（含不可用标记）。"""

    for block in raw_blocks:
        if isinstance(block, dict):
            image = block.get("image")
            if isinstance(image, dict):
                yield image


def resolve_export_images(
    raw_blocks: list[Any],
    resolver,
) -> tuple[dict[str, bytes], int]:
    """解析 raw_blocks 的图像资产引用为导出可用的 images 字节表。

    ``resolver`` 形如 ``SupervisorModule.resolve_result_asset``（返回
    ``(bytes, media_type) | None``）。返回 ``(images, missing)``：
    ``images`` 以引用的 ``name``（缺省 asset_id.png）为键，供既有
    markdown/html 导出图片代码直接内嵌/落盘；``missing`` 为无法解析的
    引用数（含上游标记 available=false 的块）——调用方必须向客户端
    报告 incomplete，不得当作完整成功。
    """

    from vibeocr.runtime.recognition.result_assets import sanitize_asset_name

    images: dict[str, bytes] = {}
    missing = 0
    for ref in iter_image_refs(raw_blocks):
        if ref.get("available") is False:
            missing += 1
            continue
        job_id = ref.get("job_id")
        item_id = ref.get("item_id")
        asset_id = ref.get("asset_id")
        if not all(
            isinstance(value, str) and value for value in (job_id, item_id, asset_id)
        ):
            missing += 1
            continue
        from vibeocr.runtime.jobs.registry import JobNotFoundError

        try:
            resolved = resolver(job_id, item_id, asset_id)
        except JobNotFoundError:
            resolved = None
        if resolved is None:
            missing += 1
            continue
        data, _media_type = resolved
        key = sanitize_asset_name(ref.get("name"), fallback=f"{asset_id}.png")
        images[key] = data
    return images, missing


def write_export(
    request: OcrExportRequest,
    *,
    images: dict[str, bytes] | None = None,
) -> int | None:
    blocks = [
        dict(block) if isinstance(block, dict) else block
        for block in request.raw_blocks
    ]
    for block in blocks:
        if isinstance(block, dict) and isinstance(block.get("image"), dict):
            name = block["image"].get("name")
            if isinstance(name, str) and name in (images or {}):
                block["img_path"] = name
    result = OCRResult(
        raw_text=request.raw_text,
        markdown_text=request.markdown_text,
        html_text=request.html_text,
        content_list=blocks,
        images=images or {},
    )
    if not ExportService.export(result, request.output_path, request.format):
        return None
    return request.output_path.stat().st_size if request.output_path.exists() else 0

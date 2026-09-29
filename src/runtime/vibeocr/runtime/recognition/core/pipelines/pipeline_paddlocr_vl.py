# src/vibeocr/core/pipelines/pipeline_paddlocr_vl.py
"""PaddleOCR-VL 管道选项与规格

定义 PaddleOCR-VL 管道的选项类和 PipelineSpec，
支持图片/PDF 文档解析，提取文本、表格、公式、图表等。
"""

from __future__ import annotations

import html
import logging
from dataclasses import dataclass, replace
from typing import Any

from vibeocr.runtime.documents.tables.blocks import (
    canonicalize_table_block,
    table_model_from_block,
)
from vibeocr.runtime.documents.tables.contracts import TableProvenanceV1
from vibeocr.runtime.documents.tables.projections import (
    table_model_to_markdown,
    table_model_to_plain_text,
)
from vibeocr.runtime.documents.tables.reducer import rebuild_result_projections
from vibeocr.runtime.recognition.core.pipelines.base_options import BasePipelineOptions
from vibeocr.runtime.recognition.core.pipelines.registry import PipelineSpec

_logger = logging.getLogger(__name__)

#: 本仓 paddle_document_vl 模式的语义固定为 VL-1.5（发行包同时提供
#: PaddleOCR-VL-1.6 更新默认）。不跟随上游新默认静默换模型；升级须走
#: 显式模式合同评审。
PADDLEOCR_VL_PIPELINE_VERSION = "v1.5"


def _table_block_key(block: dict[str, Any]) -> tuple[Any, ...]:
    """Build a provider-neutral semantic key without relying on source order."""

    table = table_model_from_block(block)
    cell_key = tuple(
        (
            cell.row,
            cell.column,
            cell.rowspan,
            cell.colspan,
            cell.text,
            cell.is_header,
        )
        for cell in table.cells
    )
    return (table.row_count, table.column_count, cell_key)


def _bbox_key(block: dict[str, Any]) -> tuple[float, float, float, float] | None:
    bbox = block.get("bbox")
    return (
        (float(bbox[0]), float(bbox[1]), float(bbox[2]), float(bbox[3]))
        if isinstance(bbox, (list, tuple)) and len(bbox) >= 4
        else None
    )


def _canonicalize_vl_table(block: dict[str, Any], *, table_id: str) -> dict[str, Any]:
    canonical_block = canonicalize_table_block(
        block,
        table_id=table_id,
        pipeline="PaddleOCR-VL",
    )
    table_model = table_model_from_block(canonical_block)
    table_model = replace(
        table_model,
        provenance=TableProvenanceV1(
            pipeline="PaddleOCR-VL",
            provider_schema="paddlex-paddlocr-vl",
        ),
    )
    canonical_block["table"] = table_model.to_payload()
    return canonical_block


def _project_canonical_table(block: dict[str, Any]) -> tuple[str, str]:
    table_model = table_model_from_block(block)
    plain_text = table_model_to_plain_text(table_model)
    markdown_projection = table_model_to_markdown(table_model)
    if markdown_projection.warnings:
        provenance = block["table"].get("provenance")
        if isinstance(provenance, dict):
            warnings = list(provenance.get("warnings") or [])
            for warning in markdown_projection.warnings:
                if warning not in warnings:
                    warnings.append(warning)
            provenance["warnings"] = warnings
    return plain_text, markdown_projection.text


def _extract_bbox_from_rec_boxes(
    rec_boxes, index: int
) -> tuple[float, float, float, float] | None:
    """从 rec_boxes 提取第 index 个文本框的 bbox"""
    try:
        box = rec_boxes[index]
        if hasattr(box, "tolist"):
            box = box.tolist()
        if len(box) == 4:
            if isinstance(box[0], (int, float)):
                return (float(box[0]), float(box[1]), float(box[2]), float(box[3]))
            xs = [p[0] for p in box]
            ys = [p[1] for p in box]
            return (min(xs), min(ys), max(xs), max(ys))
        if len(box) == 2 and len(box[0]) == 2 and len(box[1]) == 2:
            return (
                float(box[0][0]),
                float(box[0][1]),
                float(box[1][0]),
                float(box[1][1]),
            )
    except (IndexError, TypeError, ValueError):
        pass
    return None


def _extract_block_bbox(
    block_bbox: list | tuple | None,
) -> tuple[float, float, float, float] | None:
    """从 parsing_res_list 的 block_bbox 提取坐标"""
    if not block_bbox:
        return None
    try:
        if len(block_bbox) == 4 and all(
            isinstance(v, (int, float)) for v in block_bbox
        ):
            return (
                float(block_bbox[0]),
                float(block_bbox[1]),
                float(block_bbox[2]),
                float(block_bbox[3]),
            )
        if len(block_bbox) >= 2:
            xs = [p[0] for p in block_bbox]
            ys = [p[1] for p in block_bbox]
            return (min(xs), min(ys), max(xs), max(ys))
    except (TypeError, IndexError, ValueError):
        pass
    return None


def _get_block_score(res, block: dict) -> float | None:
    """从 parsing_res_list 结果中获取 block 的真实置信度。

    布局检测框携带真实 score；无布局框或框内无 score 时返回 None
    （unknown），不伪造默认分。
    """
    if hasattr(res, "layout_det_res") and hasattr(res.layout_det_res, "boxes"):
        boxes = res.layout_det_res.boxes
        order = block.get("block_order", -1)
        if 0 <= order < len(boxes):
            score = boxes[order].get("score")
            return float(score) if score is not None else None
    return None


def _build_ocr_result(
    raw_text: str,
    markdown_text: str = "",
    html_text: str = "",
    text_with_scores: list[tuple[str, float]] | None = None,
    pipeline_type: str = "PaddleOCR-VL",
    images: dict[str, Any] | None = None,
    text_blocks: list | None = None,
    content_list: list[dict[str, Any]] | None = None,
) -> Any:
    """构建 OCRResult 对象"""
    from vibeocr.runtime.recognition.models.ocr_result import OCRResult

    if text_with_scores is None:
        text_with_scores = []

    # 上游无置信度（None）不参与均值/低置信度统计，也不伪造数值。
    known_scores = [s for _, s in text_with_scores if s is not None]
    avg_score = 0.0
    if known_scores:
        avg_score = sum(known_scores) / len(known_scores)

    low_confidence_items = [
        (text, score)
        for text, score in text_with_scores
        if score is not None and score < 0.80
    ]

    final_html = html_text or raw_text

    return OCRResult(
        raw_text=raw_text,
        markdown_text=markdown_text or raw_text,
        html_text=final_html,
        text_with_scores=text_with_scores,
        avg_score=avg_score,
        low_confidence_items=low_confidence_items,
        pipeline_type=pipeline_type,
        images=images or {},
        text_blocks=text_blocks or [],
        content_list=content_list or [],
    )


@dataclass
class PaddleOCRVLOptions(BasePipelineOptions):
    """PaddleOCR-VL 管道选项

    使用 PaddleOCR-VL-1.5 解析文档，支持图片/PDF，提取文本、表格、公式、
    图表等。``use_doc_orientation_classify``/``use_doc_unwarping``/
    ``vl_use_layout_detection`` 门控构造期子模块初始化（参与构造签名）；
    chart/seal/ocr_for_image_block 是 predict 参数（锁定版本中不门控
    模型加载）。
    """

    pipeline: str = "PaddleOCR-VL"
    use_doc_orientation_classify: bool = True
    use_doc_unwarping: bool = False
    vl_use_layout_detection: bool = True
    vl_use_chart_recognition: bool = False
    vl_use_seal_recognition: bool = False
    use_ocr_for_image_block: bool = False


def _paddlocr_vl_constructor_kwargs(options: Any) -> dict[str, Any]:
    """把公开选项映射为 PaddleOCRVL 构造参数（仅模型门控项）。

    语义固定 ``pipeline_version="v1.5"``，不随上游默认漂移。
    """
    return {
        "use_doc_orientation_classify": options.use_doc_orientation_classify,
        "use_doc_unwarping": options.use_doc_unwarping,
        "use_layout_detection": options.vl_use_layout_detection,
    }


def _create_paddlocr_vl_pipeline(device: str, **kwargs: Any) -> Any:
    """创建 PaddleOCR-VL 管道实例

    额外 kwargs 透传给 PaddleOCRVL（例如 enable_mkldnn）。
    """
    from paddleocr import PaddleOCRVL

    pipeline = PaddleOCRVL(
        device=device,
        pipeline_version=PADDLEOCR_VL_PIPELINE_VERSION,
        **kwargs,
    )
    _logger.info(
        "[Paddle consumed] construct PaddleOCR-VL use_layout_detection=%s",
        kwargs.get("use_layout_detection"),
    )
    return pipeline


def _recognize_paddlocr_vl(
    service: Any,
    image: Any,
    options: PaddleOCRVLOptions,
    asset_sink: Any | None = None,
) -> Any:
    """PaddleOCR-VL 文档解析

    从 OCRService._recognize_paddlocr_vl 迁移而来。块级图像
    （image/seal/chart，布局对象携带 PIL 图）经 asset_sink 写入 job
    受控目录，content_list 携带 opaque 资产引用（不含本地路径）。
    """
    from vibeocr.runtime.recognition.models.ocr_result import TextBlock
    from vibeocr.runtime.recognition.result_assets import (
        REASON_SOURCE_UNAVAILABLE,
        REASON_STORE_UNAVAILABLE,
        unavailable_asset,
    )

    pipeline = service.get_or_create_pipeline("PaddleOCR-VL", options=options)

    predict_kwargs: dict[str, Any] = {}
    predict_kwargs["use_doc_orientation_classify"] = (
        options.use_doc_orientation_classify
    )
    predict_kwargs["use_doc_unwarping"] = options.use_doc_unwarping
    predict_kwargs["use_layout_detection"] = options.vl_use_layout_detection
    predict_kwargs["use_chart_recognition"] = options.vl_use_chart_recognition
    predict_kwargs["use_seal_recognition"] = options.vl_use_seal_recognition
    predict_kwargs["use_ocr_for_image_block"] = options.use_ocr_for_image_block

    output = pipeline.predict(input=image, **predict_kwargs)
    output_list = list(output)
    _logger.info(
        "[Paddle consumed] predict PaddleOCR-VL use_layout_detection=%s "
        "use_chart_recognition=%s",
        predict_kwargs["use_layout_detection"],
        predict_kwargs["use_chart_recognition"],
    )

    markdown_text = ""
    text_blocks: list[TextBlock] = []
    # 上游无置信度时以 None 并行占位（内部索引对齐），serializer 跳过。
    text_with_scores: list[tuple[str, float | None]] = []
    content_list: list[dict[str, Any]] = []
    images: dict[str, Any] = {}
    table_sequence = 0
    table_markdown_parts: list[str] = []
    projected_table_ids: set[str] = set()

    for res in output_list:
        result_content_start = len(content_list)
        result_text_start = len(text_blocks)
        # PaddleX 结果是 dict 子类，content_list/images/parsing_res_list 是
        # dict key（非属性），必须用下标取值；hasattr 对 dict 恒为 False 会导致
        # 整个解析被跳过。markdown 在 MarkdownMixin 中是 property，可作兜底。
        if hasattr(res, "get"):
            md = res.get("markdown")
            if md:
                markdown_text = md or markdown_text
        elif hasattr(res, "markdown"):
            markdown_text = getattr(res, "markdown", "") or markdown_text

        cl = res.get("content_list") if hasattr(res, "get") else None
        if not cl and hasattr(res, "content_list"):
            cl = getattr(res, "content_list", None)
        if cl:
            for source_block in cl:
                block = dict(source_block)
                # 禁止本地路径出 wire：provider 的 img_path 不进入结果。
                block.pop("img_path", None)
                block.pop("image_path", None)
                block.pop("src", None)
                block.pop("image", None)
                if block.get("type") == "table":
                    table_id = str(
                        block.get("block_id")
                        or block.get("table_id")
                        or f"paddlocr-vl-table-{table_sequence}"
                    )
                    table_sequence += 1
                    block = _canonicalize_vl_table(block, table_id=table_id)
                else:
                    block.setdefault(
                        "block_id", f"paddlocr-vl-block-{len(content_list)}"
                    )
                content_list.append(block)

        imgs = res.get("images") if hasattr(res, "get") else None
        if imgs is None and hasattr(res, "images"):
            imgs = getattr(res, "images", None)
        if imgs and isinstance(imgs, dict):
            images.update(imgs)

        # PaddleOCR-VL 3.x: parsing_res_list with block-level localization
        parsing_res_list: list[Any] = []
        if hasattr(res, "__getitem__"):
            parsing_res_list = (
                res["parsing_res_list"]
                if "parsing_res_list" in (res.keys() if hasattr(res, "keys") else [])
                else []
            )
        if not parsing_res_list and hasattr(res, "parsing_res_list"):
            parsing_res_list = res.parsing_res_list
        for block in parsing_res_list:
            # PaddleOCRVLBlock 是普通对象（非 dict），属性为 content/label/bbox/
            # global_block_id；同时兼容 dict 形态（block_content 等键）。
            text = (
                getattr(block, "content", None)
                if not isinstance(block, dict)
                else block.get("block_content") or block.get("content", "")
            )
            label = (
                getattr(block, "label", "text")
                if not isinstance(block, dict)
                else block.get("block_label") or block.get("label", "text")
            )
            raw_bbox = (
                getattr(block, "bbox", None)
                if not isinstance(block, dict)
                else block.get("block_bbox") or block.get("bbox")
            )
            raw_order = (
                getattr(block, "global_block_id", -1)
                if not isinstance(block, dict)
                else block.get("block_order", -1)
            )
            provider_image_entry = (
                getattr(block, "image", None)
                if not isinstance(block, dict)
                else block.get("image") or block.get("block_image")
            )
            raw_block_image = (
                provider_image_entry.get("img")
                if isinstance(provider_image_entry, dict)
                else None
            )
            raw_block_image_name = (
                provider_image_entry.get("path")
                if isinstance(provider_image_entry, dict)
                else None
            )
            try:
                order = int(raw_order) if raw_order is not None else -1
            except (TypeError, ValueError):
                order = -1
            bbox = _extract_block_bbox(raw_bbox)
            score = _get_block_score(res, block)

            if text or label in ("image", "chart", "seal"):
                cl_idx = len(content_list)
                block_id = f"paddlocr-vl-block-{cl_idx}"
                projected_text = text
                if label == "table":
                    table_id = f"paddlocr-vl-table-{table_sequence}"
                    candidate = _canonicalize_vl_table(
                        {
                            "type": "table",
                            "table_body": text,
                            "bbox": bbox,
                        },
                        table_id=table_id,
                    )
                    candidate_key = _table_block_key(candidate)
                    candidate_bbox = _bbox_key(candidate)
                    provider_id_matches = [
                        index
                        for index in range(result_content_start, len(content_list))
                        if content_list[index].get("type") == "table"
                        and order not in (-1, None)
                        and str(
                            content_list[index].get(
                                "global_block_id",
                                content_list[index].get("block_order", ""),
                            )
                        )
                        == str(order)
                    ]
                    matching_indices = provider_id_matches or [
                        index
                        for index in range(result_content_start, len(content_list))
                        if content_list[index].get("type") == "table"
                        and _table_block_key(content_list[index]) == candidate_key
                        and (
                            _bbox_key(content_list[index]) is None
                            or candidate_bbox is None
                            or _bbox_key(content_list[index]) == candidate_bbox
                        )
                    ]
                    cl_idx = matching_indices[0] if len(matching_indices) == 1 else -1
                    if cl_idx < 0:
                        cl_idx = len(content_list)
                        content_list.append(candidate)
                        table_sequence += 1
                    elif (
                        _bbox_key(content_list[cl_idx]) is None
                        and candidate_bbox is not None
                    ):
                        content_list[cl_idx]["bbox"] = bbox
                    canonical_table_block = content_list[cl_idx]
                    projected_text, table_markdown = _project_canonical_table(
                        canonical_table_block
                    )
                    block_id = str(canonical_table_block["block_id"])
                    if block_id not in projected_table_ids:
                        if table_markdown:
                            table_markdown_parts.append(table_markdown)
                        projected_table_ids.add(block_id)
                text_blocks.append(
                    TextBlock(
                        text=projected_text,
                        score=score,
                        bbox=bbox,
                        label=label,
                        order=order,
                        content_index=cl_idx,
                        content_id=(
                            content_list[cl_idx].get("block_id")
                            if label == "table"
                            else block_id
                        ),
                    )
                )
                text_with_scores.append((projected_text, score))
                if label != "table":
                    content_entry = {
                        "type": label,
                        "text": text,
                        "bbox": bbox,
                        "block_id": block_id,
                    }
                    if label in ("image", "seal", "chart"):
                        provider_image = raw_block_image
                        if asset_sink is not None and provider_image is not None:
                            content_entry["image"] = asset_sink.save(
                                provider_image,
                                name=raw_block_image_name,
                            )
                        elif asset_sink is None:
                            content_entry["image"] = unavailable_asset(
                                REASON_STORE_UNAVAILABLE
                            )
                        else:
                            content_entry["image"] = unavailable_asset(
                                REASON_SOURCE_UNAVAILABLE
                            )
                    content_list.append(content_entry)

        referenced_indices = {
            text_block.content_index
            for text_block in text_blocks[result_text_start:]
            if text_block.content_index is not None
        }
        for content_index in range(result_content_start, len(content_list)):
            source_block = content_list[content_index]
            if content_index in referenced_indices:
                continue
            block_id = str(source_block["block_id"])
            if source_block.get("type") in (
                "image",
                "seal",
                "chart",
            ) and not isinstance(source_block.get("image"), dict):
                # 布局对象未携带 PIL 的图像块（如 content_list 独有来源）：
                # 显式标记不可用，不伪造资产，也不静默丢失。
                source_block["image"] = unavailable_asset(REASON_SOURCE_UNAVAILABLE)
            if source_block.get("type") != "table":
                projected_text = str(
                    source_block.get("text") or source_block.get("content") or ""
                )
                if not projected_text:
                    continue
                text_blocks.append(
                    TextBlock(
                        text=projected_text,
                        score=None,
                        bbox=_bbox_key(source_block),
                        label=str(source_block.get("type") or "text"),
                        content_index=content_index,
                        content_id=block_id,
                    )
                )
                text_with_scores.append((projected_text, None))
                continue
            projected_text, table_markdown = _project_canonical_table(source_block)
            if block_id not in projected_table_ids:
                if table_markdown:
                    table_markdown_parts.append(table_markdown)
                projected_table_ids.add(block_id)
            text_blocks.append(
                TextBlock(
                    text=projected_text,
                    score=None,
                    bbox=_bbox_key(source_block),
                    label="table",
                    content_index=content_index,
                    content_id=block_id,
                )
            )
            text_with_scores.append((projected_text, None))

        reading_indices: list[int] = []
        for text_block in text_blocks[result_text_start:]:
            content_index = text_block.content_index
            if (
                content_index is not None
                and content_index >= result_content_start
                and content_index not in reading_indices
            ):
                reading_indices.append(content_index)
        reading_indices.extend(
            index
            for index in range(result_content_start, len(content_list))
            if index not in reading_indices
        )
        if reading_indices:
            original_slice = {index: content_list[index] for index in reading_indices}
            content_list[result_content_start:] = [
                original_slice[index] for index in reading_indices
            ]
            remapped = {
                old_index: result_content_start + offset
                for offset, old_index in enumerate(reading_indices)
            }
            for text_block in text_blocks[result_text_start:]:
                if text_block.content_index in remapped:
                    text_block.content_index = remapped[text_block.content_index]

        if (
            not parsing_res_list
            and hasattr(res, "rec_texts")
            and hasattr(res, "rec_scores")
        ):
            # Fallback: legacy output format
            rec_boxes = getattr(res, "rec_boxes", None)
            for i, (text, score) in enumerate(
                zip(res.rec_texts, res.rec_scores, strict=False)
            ):
                if text:
                    fs = float(score)
                    text_with_scores.append((text, fs))
                    bbox = (
                        _extract_bbox_from_rec_boxes(rec_boxes, i)
                        if rec_boxes is not None
                        else None
                    )
                    cl_idx = len(content_list)
                    block_id = f"paddlocr-vl-block-{cl_idx}"
                    text_blocks.append(
                        TextBlock(
                            text=text,
                            score=fs,
                            bbox=bbox,
                            content_index=cl_idx,
                            content_id=block_id,
                        )
                    )
                    content_list.append(
                        {
                            "type": "text",
                            "text": text,
                            "bbox": bbox,
                            "block_id": block_id,
                        }
                    )

    raw_text = "\n".join(b.text for b in text_blocks)
    if table_markdown_parts:
        markdown_text = "\n\n".join(
            part for part in (markdown_text, *table_markdown_parts) if part
        )
    if not raw_text and markdown_text:
        raw_text = markdown_text

    from vibeocr.runtime.documents.utils.markdown_converter import markdown_to_html

    html_parts = []
    for block in content_list:
        if block.get("type") == "table":
            html_parts.append(str(block.get("table_body") or ""))
        else:
            text = block.get("text")
            if isinstance(text, str) and text:
                html_parts.append(f"<p>{html.escape(text)}</p>")

    result = _build_ocr_result(
        raw_text=raw_text,
        markdown_text=markdown_text or raw_text,
        html_text=(
            "\n".join(part for part in html_parts if part)
            or (markdown_to_html(markdown_text) if markdown_text else raw_text)
        ),
        text_with_scores=text_with_scores,
        pipeline_type="PaddleOCR-VL",
        images=images if images and asset_sink is None else None,
        text_blocks=text_blocks,
        content_list=content_list,
    )
    rebuild_result_projections(result)
    return result


PADDLEOCR_VL_SPEC = PipelineSpec(
    name="PaddleOCR-VL",
    display_name="文档P（PaddleOCR-VL）",
    description="使用 PaddleOCR-VL-1.5 解析文档，支持图片/PDF，提取文本、表格、公式、图表等",
    options_class=PaddleOCRVLOptions,
    create_pipeline=_create_paddlocr_vl_pipeline,
    recognize=_recognize_paddlocr_vl,
    constructor_kwargs=_paddlocr_vl_constructor_kwargs,
)

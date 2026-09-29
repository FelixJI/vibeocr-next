# src/vibeocr/core/pipelines/pipeline_formula.py
"""公式识别管道选项与规格

基于锁定 PaddleOCR 3.7.0 的 ``FormulaRecognitionPipeline``（独立公式识别
管线，默认模型 PP-FormulaNet_plus-M；另有单模型 ``FormulaRecognition``
类，二者不同）。构造参数：模型名/模型目录/批量大小与文档预处理开关；
predict 参数：方向分类/去畸变。结果从 ``formula_res_list[].rec_formula``
与 ``dt_polys`` 区域几何解析，按上游输出顺序作为阅读顺序。

上游不提供置信度：TextBlock.score 置 None（真实 unknown），任何下游
展示不得把它当作 0% 或 100%。
"""

from __future__ import annotations

import io
import logging
from dataclasses import dataclass
from typing import Any

from vibeocr.runtime.recognition.core.pipelines.base_options import BasePipelineOptions
from vibeocr.runtime.recognition.core.pipelines.registry import PipelineSpec
from vibeocr.runtime.recognition.pipeline_contracts import (
    FORMULA_RECOGNITION_MODEL_NAMES,
)

_logger = logging.getLogger(__name__)


@dataclass
class FormulaRecognitionOptions(BasePipelineOptions):
    """公式识别管道选项。

    ``use_doc_orientation_classify``/``use_doc_unwarping`` 同时作为构造与
    predict 参数（决定 DocPreprocessor 子管线是否初始化）；模型名/目录/
    批量大小仅构造时消费（变更会触发管道重建，不复用旧实例）。
    """

    pipeline: str = "FORMULA_RECOGNITION"
    use_doc_orientation_classify: bool = True
    use_doc_unwarping: bool = False
    formula_recognition_model_name: str | None = None
    formula_recognition_model_dir: str | None = None
    formula_recognition_batch_size: int = 1


def _formula_constructor_kwargs(options: Any) -> dict[str, Any]:
    """把公开选项映射为 FormulaRecognitionPipeline 构造参数。

    None 值保持透传（底层 create_config_from_structure 会丢弃 None 并
    回退发行包默认）；非 None 值参与构造签名，变更即重建管道。
    """
    return {
        "use_doc_orientation_classify": options.use_doc_orientation_classify,
        "use_doc_unwarping": options.use_doc_unwarping,
        "formula_recognition_model_name": options.formula_recognition_model_name,
        "formula_recognition_model_dir": options.formula_recognition_model_dir,
        "formula_recognition_batch_size": options.formula_recognition_batch_size,
    }


def _create_formula_pipeline(device: str, **kwargs: Any) -> Any:
    """创建公式识别管道实例

    额外 kwargs 透传给 FormulaRecognitionPipeline（例如 enable_mkldnn）。
    """
    from paddleocr import FormulaRecognitionPipeline

    pipeline = FormulaRecognitionPipeline(device=device, **kwargs)
    model_name = kwargs.get("formula_recognition_model_name")
    if model_name is None or model_name in FORMULA_RECOGNITION_MODEL_NAMES:
        _logger.info(
            "[Paddle consumed] construct FORMULA_RECOGNITION formula_recognition_model_name=%s",
            model_name,
        )
    return pipeline


def _parse_dt_polys(dt_polys: Any) -> tuple[float, float, float, float] | None:
    """把 formula_res_list[].dt_polys（x1,y1,x2,y2）解析为 bbox 元组。"""
    if dt_polys is None:
        return None
    try:
        if hasattr(dt_polys, "tolist"):
            dt_polys = dt_polys.tolist()
        if not isinstance(dt_polys, (list, tuple)) or len(dt_polys) < 4:
            return None
        values = [float(dt_polys[i]) for i in range(4)]
        x1, y1, x2, y2 = values
        if x2 < x1:
            x1, x2 = x2, x1
        if y2 < y1:
            y1, y2 = y2, y1
        return (x1, y1, x2, y2)
    except (TypeError, ValueError, IndexError):
        return None


def _recognize_formula(
    service: Any,
    image: Any,
    options: FormulaRecognitionOptions,
    asset_sink: Any | None = None,
) -> Any:
    """执行公式识别并返回 OCRResult

    结果键（PaddleX 3.7.2 formula_recognition 管线）：``formula_res_list``
    每项含 ``rec_formula``（LaTeX）、``formula_region_id``、可选
    ``dt_polys``；``doc_preprocessor_res`` 含 ``angle``/``output_img``。
    无置信度字段——score 置 None，不伪造。
    """
    from vibeocr.runtime.recognition.models.ocr_result import OCRResult, TextBlock

    pipeline_name = options.pipeline
    pipeline = service.get_or_create_pipeline(pipeline_name, options=options)

    predict_kwargs: dict[str, Any] = {
        "use_doc_orientation_classify": options.use_doc_orientation_classify,
        "use_doc_unwarping": options.use_doc_unwarping,
    }
    output = pipeline.predict(input=image, **predict_kwargs)
    output_list = list(output)
    _logger.info(
        "[Paddle consumed] predict FORMULA_RECOGNITION use_doc_orientation_classify=%s",
        predict_kwargs["use_doc_orientation_classify"],
    )

    preproc_angle = 0
    preprocessed_png: bytes | None = None
    preproc_w = preproc_h = 0
    if output_list:
        res = output_list[0]
        dp_res = res.get("doc_preprocessor_res") if hasattr(res, "get") else None
        if dp_res is not None:
            preproc_angle = dp_res.get("angle", 0)
            out_arr = dp_res.get("output_img")
            if out_arr is not None:
                from PIL import Image as _PILImage

                # output_img 已是 RGB，不可做 [::-1] 翻转（否则 R/B 对调）
                rgb = out_arr.copy()
                pil_img = _PILImage.fromarray(rgb)
                preproc_w, preproc_h = pil_img.size
                buf = io.BytesIO()
                pil_img.save(buf, format="PNG")
                preprocessed_png = buf.getvalue()

    text_blocks: list[TextBlock] = []
    # 上游无置信度：text_with_scores 以 None 并行占位（内部索引对齐），
    # serializer 会跳过 None 项，不会伪造数值。
    text_with_scores: list[tuple[str, float | None]] = []
    markdown_parts: list[str] = []
    content_list: list[dict[str, Any]] = []

    for res in output_list:
        if isinstance(res, dict) and "error" in res:
            # check_model_settings_valid 失败等上游错误必须显式失败，
            # 不能静默返回空结果冒充成功。
            raise RuntimeError(
                f"公式识别管线返回错误: {res['error']!r}；"
                "请检查构造参数（方向分类/去畸变/模型配置）是否一致"
            )
        formula_res_list: list[Any] = []
        if hasattr(res, "__getitem__"):
            formula_res_list = (
                res["formula_res_list"]
                if "formula_res_list" in (res.keys() if hasattr(res, "keys") else [])
                else []
            )
        if not formula_res_list and hasattr(res, "formula_res_list"):
            formula_res_list = res.formula_res_list
        for region in formula_res_list:
            entry = region if isinstance(region, dict) else {}
            content = str(entry.get("rec_formula") or "")
            if not content:
                continue
            region_id = entry.get("formula_region_id")
            try:
                order = int(region_id) if region_id is not None else -1
            except (TypeError, ValueError):
                order = -1
            bbox = _parse_dt_polys(entry.get("dt_polys"))

            cl_idx = len(content_list)
            block_id = f"formula-recognition-block-{cl_idx}"
            formula_md = f"$${content}$$"
            markdown_parts.append(formula_md)
            text_blocks.append(
                TextBlock(
                    text=content,
                    score=None,
                    bbox=bbox,
                    label="formula",
                    order=order,
                    content_index=cl_idx,
                    content_id=block_id,
                )
            )
            text_with_scores.append((content, None))
            content_list.append(
                {
                    "type": "formula",
                    "text": content,
                    "bbox": list(bbox) if bbox else None,
                    "block_id": block_id,
                }
            )

    raw_text = "\n".join(block.text for block in text_blocks)
    markdown_text = "\n\n".join(markdown_parts) if markdown_parts else raw_text

    from vibeocr.runtime.documents.utils.markdown_converter import markdown_to_html

    known_scores = [score for _, score in text_with_scores if score is not None]
    result = OCRResult(
        raw_text=raw_text,
        markdown_text=markdown_text,
        html_text=markdown_to_html(markdown_text) if markdown_text else "",
        text_with_scores=text_with_scores,
        avg_score=(sum(known_scores) / len(known_scores) if known_scores else 0.0),
        low_confidence_items=[],
        pipeline_type="FORMULA_RECOGNITION",
        text_blocks=text_blocks,
        content_list=content_list,
    )
    result.preproc_angle = preproc_angle
    result.preprocessed_image = preprocessed_png
    result.preproc_img_w = preproc_w
    result.preproc_img_h = preproc_h
    return result


FORMULA_RECOGNITION_SPEC = PipelineSpec(
    name="FORMULA_RECOGNITION",
    display_name="公式识别",
    description="独立数学公式识别（LaTeX 输出）",
    options_class=FormulaRecognitionOptions,
    create_pipeline=_create_formula_pipeline,
    recognize=_recognize_formula,
    constructor_kwargs=_formula_constructor_kwargs,
)

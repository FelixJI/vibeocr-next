"""纯文本 OCR 的 ``reference_image`` 发布契约。

Paddle bbox 在预处理图（旋转/去畸变后）坐标空间归一化，桌面必须拿真实
预处理像素才能对齐；这里验证 adapter 在获得 ``OCRResult.preprocessed_image``
时经既有 ``ResultAssetSink`` 发布顶层 ``reference_image``（形状与
``content_list[].image`` 相同），且缺失/预算失败/无 sink 场景显式
``{available: false, reason}``，不做原图 fallback。
"""

from __future__ import annotations

import io
from pathlib import Path
from typing import Any

import pytest
from PIL import Image
from vibeocr.runtime.jobs.budgets import InputItem
from vibeocr.runtime.recognition.models.ocr_result import OCRResult, TextBlock
from vibeocr.runtime.recognition.paddle_adapter import PaddlePipelineAdapter
from vibeocr.runtime_contracts import PipelineSelection


def _png(color: tuple[int, int, int], size: tuple[int, int] = (6, 3)) -> bytes:
    buf = io.BytesIO()
    Image.new("RGB", size, color).save(buf, format="PNG")
    return buf.getvalue()


def _encode(image: Image.Image) -> bytes:
    buf = io.BytesIO()
    image.save(buf, format="PNG")
    return buf.getvalue()


def _pixels(data: bytes) -> tuple[tuple[int, int], bytes]:
    with Image.open(io.BytesIO(data)) as image:
        rgb = image.convert("RGB")
        return rgb.size, rgb.tobytes()


def _pattern_source(size: tuple[int, int] = (6, 3)) -> Image.Image:
    """坐标相关的非均匀像素：任何几何重排都能被逐点断言发现。"""
    image = Image.new("RGB", size)
    image.putdata(
        [
            ((x * 40) % 256, (y * 80) % 256, ((x + y) * 25) % 256)
            for y in range(size[1])
            for x in range(size[0])
        ]
    )
    return image


class _ResultService:
    """每个输入回放同一个预构造 OCRResult（模拟已归一化的真实结果）。"""

    def __init__(self, result: OCRResult) -> None:
        self._result = result

    def recognize_batch(
        self, images: list[Any], options: Any | None = None, *, asset_sinks: Any = None
    ) -> list[OCRResult]:
        return [self._result for _ in images]


def _item(*, asset_dir: str | None = None) -> InputItem:
    raw = _png((255, 255, 255))
    return InputItem(
        item_id="it-0000",
        data=raw,
        encoded_bytes=len(raw),
        decoded_pixels=18,
        estimated_pages=1,
        asset_dir=asset_dir or "",
    )


def _ocr_result(
    *,
    preprocessed: bytes | None,
    angle: int = 90,
    preproc_w: int = 3,
    preproc_h: int = 6,
    with_bbox: bool = True,
    content_list: list[dict[str, Any]] | None = None,
) -> OCRResult:
    return OCRResult(
        raw_text="旋转文本",
        pipeline_type="OCR",
        text_blocks=(
            [TextBlock(text="旋转文本", score=0.9, bbox=(1.0, 2.0, 3.0, 4.0))]
            if with_bbox
            else []
        ),
        content_list=content_list or [],
        preproc_angle=angle,
        preprocessed_image=preprocessed,
        preproc_img_w=preproc_w,
        preproc_img_h=preproc_h,
    )


def _recognize(
    service: _ResultService, item: InputItem, *, unwarping: bool = False
) -> dict[str, Any]:
    adapter = PaddlePipelineAdapter(service=service, pipeline_name="OCR")
    payloads = adapter.recognize_many(
        [item],
        options=(
            PipelineSelection("OCR", options={"use_doc_unwarping": True})
            if unwarping
            else None
        ),
    )
    assert len(payloads) == 1
    return payloads[0]


def _delivered_reference(
    tmp_path: Path,
    *,
    preprocessed_image: Image.Image,
    source: Image.Image,
    result: OCRResult,
    unwarping: bool = False,
) -> dict[str, Any]:
    """公共闭环：发布 → 受控目录单文件 → 引用与像素均等于真实预处理图。"""
    results_dir = tmp_path / "job" / "results"
    payload = _recognize(
        _ResultService(result), _item(asset_dir=str(results_dir)), unwarping=unwarping
    )

    reference = payload.get("reference_image")
    assert isinstance(reference, dict) and reference.get("available") is not False
    assert set(reference) >= {
        "item_id",
        "asset_id",
        "media_type",
        "width",
        "height",
        "size_bytes",
        "name",
    }
    assert reference["media_type"] == "image/png"
    # 引用尺寸 = 真实预处理图尺寸（直接交付，非按 angle/原图推导）。
    assert (reference["width"], reference["height"]) == preprocessed_image.size

    # wire 只携带 opaque 引用，禁止 base64/本地路径。
    serialized = repr(payload)
    assert str(tmp_path) not in serialized
    assert "base64" not in serialized

    files = list(results_dir.iterdir())
    assert len(files) == 1
    assert files[0].name == f"it-0000-{reference['asset_id']}.png"
    delivered = _pixels(files[0].read_bytes())
    assert delivered == (preprocessed_image.size, preprocessed_image.tobytes())
    assert delivered != (source.size, source.tobytes())
    return reference


@pytest.mark.parametrize("rotation", [90, 270])
def test_rotated_preprocessed_png_pixels_delivered_verbatim(
    tmp_path: Path, rotation: int
) -> None:
    # 6x3 非均匀横图经真实 PIL 旋转 90°/270° 后为 3x6 竖图：尺寸互换、
    # 像素重排，预处理像素才是 bbox 坐标空间。
    source = _pattern_source()
    preprocessed_image = source.transpose(
        {
            90: Image.Transpose.ROTATE_90,
            270: Image.Transpose.ROTATE_270,
        }[rotation]
    )
    assert source.size == (6, 3)
    assert preprocessed_image.size == (3, 6)
    assert preprocessed_image.tobytes() != source.tobytes()

    _delivered_reference(
        tmp_path,
        preprocessed_image=preprocessed_image,
        source=source,
        result=_ocr_result(
            preprocessed=_encode(preprocessed_image),
            angle=rotation,
            preproc_w=preprocessed_image.width,
            preproc_h=preprocessed_image.height,
        ),
    )


def test_unwarped_preprocessed_png_delivered_verbatim_at_angle0(
    tmp_path: Path,
) -> None:
    """angle=0 的非刚性去畸变输出同样直接交付：同尺寸但像素重排。"""
    source = _pattern_source()
    unwarped = source.transform(
        source.size,
        Image.Transform.QUAD,
        (0.5, 0.0, 6.5, 0.5, 6.0, 3.0, 0.0, 2.5),
        Image.Resampling.BILINEAR,
    )
    assert unwarped.size == source.size
    assert unwarped.tobytes() != source.tobytes()

    _delivered_reference(
        tmp_path,
        preprocessed_image=unwarped,
        source=source,
        result=_ocr_result(
            preprocessed=_encode(unwarped),
            angle=0,
            preproc_w=unwarped.width,
            preproc_h=unwarped.height,
        ),
        unwarping=True,
    )


def test_missing_preprocessed_image_is_unavailable_not_original_fallback(
    tmp_path: Path,
) -> None:
    service = _ResultService(_ocr_result(preprocessed=None, angle=180))
    payload = _recognize(service, _item(asset_dir=str(tmp_path / "r")))
    assert payload["reference_image"] == {
        "available": False,
        "reason": "asset_source_unavailable",
    }
    assert not list((tmp_path / "r").glob("*.png"))


def test_unwarping_without_preprocessed_image_is_unavailable(tmp_path: Path) -> None:
    """去畸变在角度 0 时也改变几何：图缺失必须显式 unavailable，不回退原图。"""
    result = _ocr_result(preprocessed=None, angle=0, preproc_w=0, preproc_h=0)
    service = _ResultService(result)
    payload = _recognize(service, _item(asset_dir=str(tmp_path / "r")), unwarping=True)
    assert payload["reference_image"] == {
        "available": False,
        "reason": "asset_source_unavailable",
    }
    # 同一结果在未开启去畸变时不要求参考图（普通无预处理省略字段）。
    payload_plain = _recognize(service, _item(asset_dir=str(tmp_path / "r")))
    assert "reference_image" not in payload_plain


def test_job_budget_exhaustion_is_unavailable(tmp_path: Path) -> None:
    from vibeocr.runtime.recognition.result_assets import MAX_JOB_ASSET_BYTES

    results_dir = tmp_path / "job" / "results"
    results_dir.mkdir(parents=True)
    # 预算扫描计入既有文件：逻辑尺寸占满 job 级预算（NTFS/ext4 均不落盘）。
    with (results_dir / "it-0000-existing.png").open("wb") as existing:
        existing.truncate(MAX_JOB_ASSET_BYTES)
    service = _ResultService(_ocr_result(preprocessed=_png((1, 2, 3))))
    payload = _recognize(service, _item(asset_dir=str(results_dir)))
    assert payload["reference_image"] == {
        "available": False,
        "reason": "asset_job_budget",
    }
    assert [entry.name for entry in results_dir.iterdir()] == ["it-0000-existing.png"]


def test_without_sink_preprocessed_result_reports_store_unavailable() -> None:
    service = _ResultService(_ocr_result(preprocessed=_png((9, 9, 9))))
    payload = _recognize(service, _item())
    assert payload["reference_image"] == {
        "available": False,
        "reason": "asset_store_unavailable",
    }


def test_plain_result_without_preprocessing_omits_field(tmp_path: Path) -> None:
    result = _ocr_result(preprocessed=None, angle=0, preproc_w=0, preproc_h=0)
    service = _ResultService(result)
    payload = _recognize(service, _item(asset_dir=str(tmp_path / "r")))
    assert "reference_image" not in payload


def test_content_list_result_not_published(tmp_path: Path) -> None:
    result = _ocr_result(
        preprocessed=_png((5, 5, 5)),
        content_list=[{"type": "text", "text": "结构块", "bbox": [1, 2, 3, 4]}],
    )
    service = _ResultService(result)
    payload = _recognize(service, _item(asset_dir=str(tmp_path / "r")))
    assert "reference_image" not in payload


def test_result_without_valid_bbox_not_published(tmp_path: Path) -> None:
    result = _ocr_result(preprocessed=_png((5, 5, 5)), with_bbox=False)
    service = _ResultService(result)
    payload = _recognize(service, _item(asset_dir=str(tmp_path / "r")))
    assert "reference_image" not in payload


@pytest.mark.parametrize(
    "bbox",
    [
        [1.0, 2.0, 1.0, 4.0],  # 零面积（x1 == x0）
        [1.0, 2.0, 0.5, 4.0],  # 负面积
        [1.0, float("nan"), 3.0, 4.0],  # NaN
        [1.0, float("inf"), 3.0, 4.0],  # 非有限
        [1.0, 2.0, 3.0],  # 非 4 元
        [1.0, 2.0, 3.0, "4"],  # 非数值
    ],
)
def test_degenerate_bbox_not_published(bbox: list[Any]) -> None:
    """无面积/非有限 bbox 无法 overlay：不发布（即使预处理需要图）。"""
    result = _ocr_result(preprocessed=_png((5, 5, 5)), angle=90)
    payload = {
        "raw_text": "文本",
        "content_list": [],
        "text_blocks": [{"text": "退化块", "score": 0.9, "bbox": bbox}],
    }
    # unwarping=True + sink=None：若 bbox 可用会得到 store_unavailable 标记，
    # 字段缺失证明是 bbox 门禁拦下，而非预处理不需要。
    PaddlePipelineAdapter._publish_reference_image(
        payload, result, None, unwarping=True
    )
    assert "reference_image" not in payload


def test_block_without_text_not_published(tmp_path: Path) -> None:
    result = _ocr_result(preprocessed=_png((5, 5, 5)), angle=90)
    result.text_blocks = [TextBlock(text="", score=0.9, bbox=(1.0, 2.0, 3.0, 4.0))]
    service = _ResultService(result)
    payload = _recognize(service, _item(asset_dir=str(tmp_path / "r")))
    assert "reference_image" not in payload


def test_unavailable_marker_when_preprocessing_needed_but_decode_fails(
    tmp_path: Path,
) -> None:
    class _BrokenBytes(bytes):
        pass

    service = _ResultService(_ocr_result(preprocessed=_BrokenBytes(b"not png")))
    payload = _recognize(service, _item(asset_dir=str(tmp_path / "r")))
    assert payload["reference_image"] == {
        "available": False,
        "reason": "asset_encode_failed",
    }

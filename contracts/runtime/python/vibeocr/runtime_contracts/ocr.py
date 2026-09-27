"""Stdlib-only DTOs for the typed ``ocr.v1`` result payload."""

from __future__ import annotations

import math
from copy import deepcopy
from dataclasses import dataclass, field
from types import MappingProxyType
from typing import Any, ClassVar, Mapping, Self

from vibeocr.runtime.documents.tables.contracts import TableModelV1

from .parser import ContractError

OCR_RESULT_PAYLOAD_TYPE = "ocr.v1"

JsonObject = dict[str, Any]


def _object(value: object, label: str) -> JsonObject:
    if not isinstance(value, dict):
        raise ContractError(f"{label} must be an object")
    return value


def _string(value: object, label: str) -> str:
    if not isinstance(value, str):
        raise ContractError(f"{label} must be a string")
    return value


def _integer(value: object, label: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int):
        raise ContractError(f"{label} must be an integer")
    return value


def _number(value: object, label: str) -> float:
    if (
        isinstance(value, bool)
        or not isinstance(value, int | float)
        or not math.isfinite(float(value))
    ):
        raise ContractError(f"{label} must be a finite number")
    return float(value)


def _optional_integer(value: object, label: str) -> int | None:
    return None if value is None else _integer(value, label)


def _optional_string(value: object, label: str) -> str | None:
    return None if value is None else _string(value, label)


def _numbers(
    value: object, label: str, *, length: int | None = None
) -> tuple[float, ...]:
    if not isinstance(value, list | tuple):
        raise ContractError(f"{label} must be an array")
    if length is not None and len(value) != length:
        raise ContractError(f"{label} must contain {length} numbers")
    return tuple(_number(item, label) for item in value)


def _extra(payload: JsonObject, known: frozenset[str]) -> Mapping[str, Any]:
    return MappingProxyType(
        {key: deepcopy(value) for key, value in payload.items() if key not in known}
    )


def _merge(payload: JsonObject, extra: Mapping[str, Any]) -> JsonObject:
    return {**deepcopy(dict(extra)), **payload}


def _pairs(value: object, label: str) -> tuple[tuple[str, float], ...]:
    if not isinstance(value, list | tuple):
        raise ContractError(f"{label} must be an array")
    result: list[tuple[str, float]] = []
    for item in value:
        if not isinstance(item, list | tuple) or len(item) != 2:
            raise ContractError(f"{label} entries must contain text and score")
        result.append((_string(item[0], f"{label}.text"), _number(item[1], label)))
    return tuple(result)


def _content_blocks(value: object) -> tuple[JsonObject, ...]:
    if not isinstance(value, list | tuple):
        raise ContractError("content_list must be an array")
    result: list[JsonObject] = []
    for item in value:
        block = _object(item, "content_list entry")
        canonical_table = block.get("table")
        if (
            block.get("type") == "table"
            and "table" in block
            and canonical_table is not None
        ):
            if not isinstance(canonical_table, dict):
                raise ContractError("canonical table must be an object")
            try:
                TableModelV1.from_payload(canonical_table)
            except (TypeError, ValueError) as exc:
                raise ContractError(f"invalid canonical table: {exc}") from exc
        result.append(deepcopy(block))
    return tuple(result)


@dataclass(frozen=True, slots=True)
class OcrTextBlockV1:
    """One text block carried by an ``ocr.v1`` result."""

    text: str
    score: float
    bbox: tuple[float, float, float, float] | None = None
    polygon: tuple[float, ...] | None = None
    page_idx: int | None = None
    is_manually_edited: bool = False
    content_index: int | None = None
    content_id: str | None = None
    label: str = "text"
    order: int = -1
    extra: Mapping[str, Any] = field(default_factory=lambda: MappingProxyType({}))

    _label: ClassVar[str] = "OcrTextBlockV1"
    _known: ClassVar[frozenset[str]] = frozenset(
        {
            "text",
            "score",
            "bbox",
            "polygon",
            "page_idx",
            "is_manually_edited",
            "content_index",
            "content_id",
            "label",
            "order",
        }
    )

    @classmethod
    def from_payload(cls, value: object) -> Self:
        payload = _object(value, cls._label)
        if "text" not in payload or "score" not in payload:
            raise ContractError("OcrTextBlockV1 requires text and score")
        raw_bbox = payload.get("bbox")
        bbox = None if raw_bbox is None else _numbers(raw_bbox, "bbox", length=4)
        raw_polygon = payload.get("polygon")
        polygon = None if raw_polygon is None else _numbers(raw_polygon, "polygon")
        if polygon is not None and (len(polygon) < 8 or len(polygon) % 2 != 0):
            raise ContractError("polygon must contain at least four coordinate pairs")
        edited = payload.get("is_manually_edited", False)
        if not isinstance(edited, bool):
            raise ContractError("is_manually_edited must be a boolean")
        return cls(
            text=_string(payload["text"], "text"),
            score=_number(payload["score"], "score"),
            bbox=None if bbox is None else (bbox[0], bbox[1], bbox[2], bbox[3]),
            polygon=polygon,
            page_idx=_optional_integer(payload.get("page_idx"), "page_idx"),
            is_manually_edited=edited,
            content_index=_optional_integer(
                payload.get("content_index"), "content_index"
            ),
            content_id=_optional_string(payload.get("content_id"), "content_id"),
            label=_string(payload.get("label", "text"), "label"),
            order=_integer(payload.get("order", -1), "order"),
            extra=_extra(payload, cls._known),
        )

    def to_payload(self) -> JsonObject:
        return _merge(
            {
                "text": self.text,
                "score": self.score,
                "bbox": list(self.bbox) if self.bbox is not None else None,
                "polygon": list(self.polygon) if self.polygon is not None else None,
                "page_idx": self.page_idx,
                "is_manually_edited": self.is_manually_edited,
                "content_index": self.content_index,
                "content_id": self.content_id,
                "label": self.label,
                "order": self.order,
            },
            self.extra,
        )


@dataclass(frozen=True, slots=True)
class OcrResultV1:
    """Typed view of the opaque result carried by a successful OCR outcome."""

    raw_text: str = ""
    markdown_text: str = ""
    html_text: str = ""
    avg_score: float = 0.0
    pipeline_type: str = "OCR"
    preproc_angle: int = 0
    content_list: tuple[JsonObject, ...] = ()
    text_with_scores: tuple[tuple[str, float], ...] = ()
    low_confidence_items: tuple[tuple[str, float], ...] = ()
    text_blocks: tuple[OcrTextBlockV1, ...] = ()
    images: Mapping[str, Any] = field(default_factory=lambda: MappingProxyType({}))
    image_width: int = 0
    image_height: int = 0
    extra: Mapping[str, Any] = field(default_factory=lambda: MappingProxyType({}))

    _label: ClassVar[str] = "OcrResultV1"
    _known: ClassVar[frozenset[str]] = frozenset(
        {
            "raw_text",
            "text",
            "markdown_text",
            "html_text",
            "avg_score",
            "pipeline_type",
            "preproc_angle",
            "content_list",
            "text_with_scores",
            "low_confidence_items",
            "text_blocks",
            "images",
            "image_width",
            "image_height",
        }
    )

    @classmethod
    def from_payload(cls, value: object) -> Self:
        payload = _object(value, cls._label)
        raw_content = payload.get("content_list", [])
        raw_blocks = payload.get("text_blocks", [])
        raw_images = payload.get("images", {})
        if not isinstance(raw_blocks, list | tuple):
            raise ContractError("text_blocks must be an array")
        if not isinstance(raw_images, dict):
            raise ContractError("images must be an object")
        return cls(
            raw_text=_string(
                payload.get("raw_text", payload.get("text", "")), "raw_text"
            ),
            markdown_text=_string(payload.get("markdown_text", ""), "markdown_text"),
            html_text=_string(payload.get("html_text", ""), "html_text"),
            avg_score=_number(payload.get("avg_score", 0.0), "avg_score"),
            pipeline_type=_string(payload.get("pipeline_type", "OCR"), "pipeline_type"),
            preproc_angle=_integer(payload.get("preproc_angle", 0), "preproc_angle"),
            content_list=_content_blocks(raw_content),
            text_with_scores=_pairs(
                payload.get("text_with_scores", []), "text_with_scores"
            ),
            low_confidence_items=_pairs(
                payload.get("low_confidence_items", []), "low_confidence_items"
            ),
            text_blocks=tuple(OcrTextBlockV1.from_payload(item) for item in raw_blocks),
            images=MappingProxyType(deepcopy(raw_images)),
            image_width=_integer(payload.get("image_width", 0), "image_width"),
            image_height=_integer(payload.get("image_height", 0), "image_height"),
            extra=_extra(payload, cls._known),
        )

    def to_payload(self) -> JsonObject:
        return _merge(
            {
                "raw_text": self.raw_text,
                "markdown_text": self.markdown_text,
                "html_text": self.html_text,
                "avg_score": self.avg_score,
                "pipeline_type": self.pipeline_type,
                "preproc_angle": self.preproc_angle,
                "content_list": deepcopy(list(self.content_list)),
                "text_with_scores": [list(item) for item in self.text_with_scores],
                "low_confidence_items": [
                    list(item) for item in self.low_confidence_items
                ],
                "text_blocks": [block.to_payload() for block in self.text_blocks],
                "images": deepcopy(dict(self.images)),
                "image_width": self.image_width,
                "image_height": self.image_height,
            },
            self.extra,
        )


def parse_ocr_result_payload(payload_type: str, payload: object) -> OcrResultV1:
    """Parse one successful outcome carrying an ``ocr.v1`` result."""

    if payload_type != OCR_RESULT_PAYLOAD_TYPE:
        raise ContractError(
            "unsupported OCR payload_type: "
            f"expected {OCR_RESULT_PAYLOAD_TYPE!r}, got {payload_type!r}"
        )
    return OcrResultV1.from_payload(payload)


__all__ = [
    "OCR_RESULT_PAYLOAD_TYPE",
    "OcrResultV1",
    "OcrTextBlockV1",
    "parse_ocr_result_payload",
]

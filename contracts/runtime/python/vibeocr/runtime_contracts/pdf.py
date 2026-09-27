"""Stdlib-only DTOs for the PDF HTTP v2 response boundary.

The generated OpenAPI bindings remain the wire source of truth.  These
dataclasses provide the runtime parsing surface needed by Python frontends
without exposing Backend-owned Pydantic models.
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from enum import StrEnum
from types import MappingProxyType
from typing import Any, ClassVar, Mapping, Self

from .dtos import SCHEMA_VERSION
from .parser import ContractError

JsonObject = dict[str, Any]


def _object(payload: object, label: str) -> JsonObject:
    if not isinstance(payload, dict):
        raise ContractError(f"{label} must be an object")
    return payload


def _required(payload: JsonObject, name: str, label: str) -> Any:
    if name not in payload or payload[name] is None:
        raise ContractError(f"{label} missing required field: {name}")
    return payload[name]


def _integer(value: object, label: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int):
        raise ContractError(f"{label} must be an integer")
    return value


def _number(value: object, label: str) -> float:
    if isinstance(value, bool) or not isinstance(value, int | float):
        raise ContractError(f"{label} must be a number")
    return float(value)


def _string(value: object, label: str) -> str:
    if not isinstance(value, str):
        raise ContractError(f"{label} must be a string")
    return value


def _boolean(value: object, label: str) -> bool:
    if not isinstance(value, bool):
        raise ContractError(f"{label} must be a boolean")
    return value


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
        {key: value for key, value in payload.items() if key not in known}
    )


def _merge(payload: JsonObject, extra: Mapping[str, Any]) -> JsonObject:
    return {**extra, **payload}


def _response_envelope(payload: JsonObject, label: str) -> tuple[int, str]:
    schema_version = _integer(
        _required(payload, "schema_version", label), "schema_version"
    )
    if schema_version != SCHEMA_VERSION:
        raise ContractError(
            f"schema_version mismatch: expected {SCHEMA_VERSION}, got {schema_version}"
        )
    instance_id = _string(_required(payload, "instance_id", label), "instance_id")
    if not instance_id:
        raise ContractError("instance_id must not be empty")
    return schema_version, instance_id


class _JsonResponse:
    _label: ClassVar[str]

    @classmethod
    def from_json(cls, value: str | bytes | bytearray) -> Self:
        try:
            payload = json.loads(value)
        except (TypeError, ValueError) as exc:
            raise ContractError(f"{cls._label} must be valid JSON") from exc
        return cls.from_payload(_object(payload, cls._label))


class PdfProgressPhase(StrEnum):
    LOAD = "load"
    RENDER = "render"
    OCR = "ocr"
    WRITE = "write"
    DETECT = "detect"
    CORRECT = "correct"
    DELETE = "delete"
    SAVE = "save"
    EXPORT = "export"
    COMPRESS = "compress"


@dataclass(frozen=True, slots=True)
class PdfProgressEvent(_JsonResponse):
    phase: PdfProgressPhase
    current: int = 0
    total: int = 0
    message: str | None = None
    page_index: int | None = None
    page_payload: Any | None = None
    extra: Mapping[str, Any] = field(default_factory=lambda: MappingProxyType({}))

    _label: ClassVar[str] = "PdfProgressEvent"
    _known: ClassVar[frozenset[str]] = frozenset(
        {"phase", "current", "total", "message", "page_index", "page_payload"}
    )

    @classmethod
    def from_payload(cls, value: object) -> Self:
        payload = _object(value, cls._label)
        raw_phase = _string(_required(payload, "phase", cls._label), "phase")
        try:
            phase = PdfProgressPhase(raw_phase)
        except ValueError as exc:
            raise ContractError(f"unknown PDF progress phase: {raw_phase!r}") from exc
        raw_message = payload.get("message")
        raw_page_index = payload.get("page_index")
        if raw_message is not None:
            raw_message = _string(raw_message, "message")
        return cls(
            phase=phase,
            current=_integer(payload.get("current", 0), "current"),
            total=_integer(payload.get("total", 0), "total"),
            message=raw_message,
            page_index=(
                None
                if raw_page_index is None
                else _integer(raw_page_index, "page_index")
            ),
            page_payload=payload.get("page_payload"),
            extra=_extra(payload, cls._known),
        )

    def to_payload(self) -> JsonObject:
        return _merge(
            {
                "phase": self.phase.value,
                "current": self.current,
                "total": self.total,
                "message": self.message,
                "page_index": self.page_index,
                "page_payload": self.page_payload,
            },
            self.extra,
        )


@dataclass(frozen=True, slots=True)
class TextLayerInfoMirror(_JsonResponse):
    index: int
    text_preview: str
    char_count: int
    bbox: tuple[float, float, float, float]
    color_id: int
    extra: Mapping[str, Any] = field(default_factory=lambda: MappingProxyType({}))

    _label: ClassVar[str] = "TextLayerInfoMirror"
    _known: ClassVar[frozenset[str]] = frozenset(
        {"index", "text_preview", "char_count", "bbox", "color_id"}
    )

    @classmethod
    def from_payload(cls, value: object) -> Self:
        payload = _object(value, cls._label)
        bbox = _numbers(_required(payload, "bbox", cls._label), "bbox", length=4)
        return cls(
            index=_integer(_required(payload, "index", cls._label), "index"),
            text_preview=_string(
                _required(payload, "text_preview", cls._label), "text_preview"
            ),
            char_count=_integer(
                _required(payload, "char_count", cls._label), "char_count"
            ),
            bbox=(bbox[0], bbox[1], bbox[2], bbox[3]),
            color_id=_integer(_required(payload, "color_id", cls._label), "color_id"),
            extra=_extra(payload, cls._known),
        )

    def to_payload(self) -> JsonObject:
        return _merge(
            {
                "index": self.index,
                "text_preview": self.text_preview,
                "char_count": self.char_count,
                "bbox": list(self.bbox),
                "color_id": self.color_id,
            },
            self.extra,
        )


@dataclass(frozen=True, slots=True)
class TextBlockMirror(_JsonResponse):
    text: str
    score: float
    bbox: tuple[float, float, float, float] | None = None
    polygon: tuple[float, ...] | None = None
    page_idx: int | None = None
    is_manually_edited: bool = False
    label: str = "text"
    order: int = -1
    extra: Mapping[str, Any] = field(default_factory=lambda: MappingProxyType({}))

    _label: ClassVar[str] = "TextBlockMirror"
    _known: ClassVar[frozenset[str]] = frozenset(
        {
            "text",
            "score",
            "bbox",
            "polygon",
            "page_idx",
            "is_manually_edited",
            "label",
            "order",
        }
    )

    @classmethod
    def from_payload(cls, value: object) -> Self:
        payload = _object(value, cls._label)
        raw_bbox = payload.get("bbox")
        bbox = None if raw_bbox is None else _numbers(raw_bbox, "bbox", length=4)
        raw_polygon = payload.get("polygon")
        polygon = None if raw_polygon is None else _numbers(raw_polygon, "polygon")
        raw_page_idx = payload.get("page_idx")
        return cls(
            text=_string(_required(payload, "text", cls._label), "text"),
            score=_number(_required(payload, "score", cls._label), "score"),
            bbox=None if bbox is None else (bbox[0], bbox[1], bbox[2], bbox[3]),
            polygon=polygon,
            page_idx=(
                None if raw_page_idx is None else _integer(raw_page_idx, "page_idx")
            ),
            is_manually_edited=_boolean(
                payload.get("is_manually_edited", False), "is_manually_edited"
            ),
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
                "label": self.label,
                "order": self.order,
            },
            self.extra,
        )


@dataclass(frozen=True, slots=True)
class PdfPageInfoMirror(_JsonResponse):
    page_index: int
    rotation: int = 0
    has_text_layer: bool = False
    text_layers: tuple[TextLayerInfoMirror, ...] = ()
    is_scanned: bool = False
    rect: tuple[float, float, float, float] = (0.0, 0.0, 0.0, 0.0)
    ocr_text_blocks: tuple[TextBlockMirror, ...] = ()
    ocr_preproc_angle: int = 0
    deskewed: bool = False
    extra: Mapping[str, Any] = field(default_factory=lambda: MappingProxyType({}))

    _label: ClassVar[str] = "PdfPageInfoMirror"
    _known: ClassVar[frozenset[str]] = frozenset(
        {
            "page_index",
            "rotation",
            "has_text_layer",
            "text_layers",
            "is_scanned",
            "rect",
            "ocr_text_blocks",
            "ocr_preproc_angle",
            "deskewed",
        }
    )

    @classmethod
    def from_payload(cls, value: object) -> Self:
        payload = _object(value, cls._label)
        raw_rect = _numbers(payload.get("rect", [0, 0, 0, 0]), "rect", length=4)
        text_layers = payload.get("text_layers", [])
        blocks = payload.get("ocr_text_blocks", [])
        if not isinstance(text_layers, list | tuple):
            raise ContractError("text_layers must be an array")
        if not isinstance(blocks, list | tuple):
            raise ContractError("ocr_text_blocks must be an array")
        return cls(
            page_index=_integer(
                _required(payload, "page_index", cls._label), "page_index"
            ),
            rotation=_integer(payload.get("rotation", 0), "rotation"),
            has_text_layer=_boolean(
                payload.get("has_text_layer", False), "has_text_layer"
            ),
            text_layers=tuple(TextLayerInfoMirror.from_payload(v) for v in text_layers),
            is_scanned=_boolean(payload.get("is_scanned", False), "is_scanned"),
            rect=(raw_rect[0], raw_rect[1], raw_rect[2], raw_rect[3]),
            ocr_text_blocks=tuple(TextBlockMirror.from_payload(v) for v in blocks),
            ocr_preproc_angle=_integer(
                payload.get("ocr_preproc_angle", 0), "ocr_preproc_angle"
            ),
            deskewed=_boolean(payload.get("deskewed", False), "deskewed"),
            extra=_extra(payload, cls._known),
        )

    def to_payload(self) -> JsonObject:
        return _merge(
            {
                "page_index": self.page_index,
                "rotation": self.rotation,
                "has_text_layer": self.has_text_layer,
                "text_layers": [item.to_payload() for item in self.text_layers],
                "is_scanned": self.is_scanned,
                "rect": list(self.rect),
                "ocr_text_blocks": [item.to_payload() for item in self.ocr_text_blocks],
                "ocr_preproc_angle": self.ocr_preproc_angle,
                "deskewed": self.deskewed,
            },
            self.extra,
        )


@dataclass(frozen=True, slots=True)
class PdfDocumentMirror(_JsonResponse):
    file_path: str | None = None
    pages: tuple[PdfPageInfoMirror, ...] = ()
    is_modified: bool = False
    has_structural_change: bool = False
    render_dpi: int = 300
    thumbnail_dpi: int = 96
    extra: Mapping[str, Any] = field(default_factory=lambda: MappingProxyType({}))

    _label: ClassVar[str] = "PdfDocumentMirror"
    _known: ClassVar[frozenset[str]] = frozenset(
        {
            "file_path",
            "pages",
            "is_modified",
            "has_structural_change",
            "render_dpi",
            "thumbnail_dpi",
        }
    )

    @classmethod
    def from_payload(cls, value: object) -> Self:
        payload = _object(value, cls._label)
        raw_path = payload.get("file_path")
        pages = payload.get("pages", [])
        if raw_path is not None:
            raw_path = _string(raw_path, "file_path")
        if not isinstance(pages, list | tuple):
            raise ContractError("pages must be an array")
        return cls(
            file_path=raw_path,
            pages=tuple(PdfPageInfoMirror.from_payload(v) for v in pages),
            is_modified=_boolean(payload.get("is_modified", False), "is_modified"),
            has_structural_change=_boolean(
                payload.get("has_structural_change", False),
                "has_structural_change",
            ),
            render_dpi=_integer(payload.get("render_dpi", 300), "render_dpi"),
            thumbnail_dpi=_integer(payload.get("thumbnail_dpi", 96), "thumbnail_dpi"),
            extra=_extra(payload, cls._known),
        )

    def to_payload(self) -> JsonObject:
        return _merge(
            {
                "file_path": self.file_path,
                "pages": [page.to_payload() for page in self.pages],
                "is_modified": self.is_modified,
                "has_structural_change": self.has_structural_change,
                "render_dpi": self.render_dpi,
                "thumbnail_dpi": self.thumbnail_dpi,
            },
            self.extra,
        )


@dataclass(frozen=True, slots=True)
class PdfModelDiff(_JsonResponse):
    replaced_pages: tuple[PdfPageInfoMirror, ...] = ()
    structural_change: bool = False
    full_model: PdfDocumentMirror | None = None
    modified_flag: bool | None = None
    structural_flag: bool | None = None
    invalidated_thumbnails: tuple[int, ...] = ()
    extra: Mapping[str, Any] = field(default_factory=lambda: MappingProxyType({}))

    _label: ClassVar[str] = "PdfModelDiff"
    _known: ClassVar[frozenset[str]] = frozenset(
        {
            "replaced_pages",
            "structural_change",
            "full_model",
            "modified_flag",
            "structural_flag",
            "invalidated_thumbnails",
        }
    )

    @classmethod
    def from_payload(cls, value: object) -> Self:
        payload = _object(value, cls._label)
        pages = payload.get("replaced_pages", [])
        invalidated = payload.get("invalidated_thumbnails", [])
        if not isinstance(pages, list | tuple):
            raise ContractError("replaced_pages must be an array")
        if not isinstance(invalidated, list | tuple):
            raise ContractError("invalidated_thumbnails must be an array")
        raw_full_model = payload.get("full_model")
        raw_modified = payload.get("modified_flag")
        raw_structural = payload.get("structural_flag")
        return cls(
            replaced_pages=tuple(PdfPageInfoMirror.from_payload(v) for v in pages),
            structural_change=_boolean(
                payload.get("structural_change", False), "structural_change"
            ),
            full_model=(
                None
                if raw_full_model is None
                else PdfDocumentMirror.from_payload(raw_full_model)
            ),
            modified_flag=(
                None
                if raw_modified is None
                else _boolean(raw_modified, "modified_flag")
            ),
            structural_flag=(
                None
                if raw_structural is None
                else _boolean(raw_structural, "structural_flag")
            ),
            invalidated_thumbnails=tuple(
                _integer(item, "invalidated_thumbnails") for item in invalidated
            ),
            extra=_extra(payload, cls._known),
        )

    def to_payload(self) -> JsonObject:
        return _merge(
            {
                "replaced_pages": [page.to_payload() for page in self.replaced_pages],
                "structural_change": self.structural_change,
                "full_model": (
                    self.full_model.to_payload()
                    if self.full_model is not None
                    else None
                ),
                "modified_flag": self.modified_flag,
                "structural_flag": self.structural_flag,
                "invalidated_thumbnails": list(self.invalidated_thumbnails),
            },
            self.extra,
        )


@dataclass(frozen=True, slots=True)
class PdfOpenResult(_JsonResponse):
    session_id: str
    model: PdfDocumentMirror
    instance_id: str
    schema_version: int = SCHEMA_VERSION
    extra: Mapping[str, Any] = field(default_factory=lambda: MappingProxyType({}))

    _label: ClassVar[str] = "PdfOpenResult"
    _known: ClassVar[frozenset[str]] = frozenset(
        {"schema_version", "instance_id", "session_id", "model"}
    )

    @classmethod
    def from_payload(cls, value: object) -> Self:
        payload = _object(value, cls._label)
        schema_version, instance_id = _response_envelope(payload, cls._label)
        return cls(
            session_id=_string(
                _required(payload, "session_id", cls._label), "session_id"
            ),
            model=PdfDocumentMirror.from_payload(
                _required(payload, "model", cls._label)
            ),
            schema_version=schema_version,
            instance_id=instance_id,
            extra=_extra(payload, cls._known),
        )

    def to_payload(self) -> JsonObject:
        return _merge(
            {
                "schema_version": self.schema_version,
                "instance_id": self.instance_id,
                "session_id": self.session_id,
                "model": self.model.to_payload(),
            },
            self.extra,
        )


@dataclass(frozen=True, slots=True)
class PdfMutationResult(_JsonResponse):
    diff: PdfModelDiff
    instance_id: str
    operation_extra: Mapping[str, Any] | None = None
    schema_version: int = SCHEMA_VERSION
    extra: Mapping[str, Any] = field(default_factory=lambda: MappingProxyType({}))

    _label: ClassVar[str] = "PdfMutationResult"
    _known: ClassVar[frozenset[str]] = frozenset(
        {"schema_version", "instance_id", "diff", "extra"}
    )

    @classmethod
    def from_payload(cls, value: object) -> Self:
        payload = _object(value, cls._label)
        schema_version, instance_id = _response_envelope(payload, cls._label)
        raw_operation_extra = payload.get("extra")
        if raw_operation_extra is not None:
            raw_operation_extra = MappingProxyType(
                dict(_object(raw_operation_extra, "extra"))
            )
        return cls(
            diff=PdfModelDiff.from_payload(_required(payload, "diff", cls._label)),
            instance_id=instance_id,
            operation_extra=raw_operation_extra,
            schema_version=schema_version,
            extra=_extra(payload, cls._known),
        )

    def to_payload(self) -> JsonObject:
        return _merge(
            {
                "schema_version": self.schema_version,
                "instance_id": self.instance_id,
                "diff": self.diff.to_payload(),
                "extra": (
                    dict(self.operation_extra)
                    if self.operation_extra is not None
                    else None
                ),
            },
            self.extra,
        )


@dataclass(frozen=True, slots=True)
class PdfSaveResult(_JsonResponse):
    path: str
    diff: PdfModelDiff
    instance_id: str
    schema_version: int = SCHEMA_VERSION
    extra: Mapping[str, Any] = field(default_factory=lambda: MappingProxyType({}))

    _label: ClassVar[str] = "PdfSaveResult"
    _known: ClassVar[frozenset[str]] = frozenset(
        {"schema_version", "instance_id", "path", "diff"}
    )

    @classmethod
    def from_payload(cls, value: object) -> Self:
        payload = _object(value, cls._label)
        schema_version, instance_id = _response_envelope(payload, cls._label)
        return cls(
            path=_string(_required(payload, "path", cls._label), "path"),
            diff=PdfModelDiff.from_payload(_required(payload, "diff", cls._label)),
            schema_version=schema_version,
            instance_id=instance_id,
            extra=_extra(payload, cls._known),
        )

    def to_payload(self) -> JsonObject:
        return _merge(
            {
                "schema_version": self.schema_version,
                "instance_id": self.instance_id,
                "path": self.path,
                "diff": self.diff.to_payload(),
            },
            self.extra,
        )


@dataclass(frozen=True, slots=True)
class PdfDetectResult(_JsonResponse):
    text_layers: tuple[TextLayerInfoMirror, ...]
    instance_id: str
    schema_version: int = SCHEMA_VERSION
    extra: Mapping[str, Any] = field(default_factory=lambda: MappingProxyType({}))

    _label: ClassVar[str] = "PdfDetectResult"
    _known: ClassVar[frozenset[str]] = frozenset(
        {"schema_version", "instance_id", "text_layers"}
    )

    @classmethod
    def from_payload(cls, value: object) -> Self:
        payload = _object(value, cls._label)
        raw_layers = _required(payload, "text_layers", cls._label)
        schema_version, instance_id = _response_envelope(payload, cls._label)
        if not isinstance(raw_layers, list | tuple):
            raise ContractError("text_layers must be an array")
        return cls(
            text_layers=tuple(
                TextLayerInfoMirror.from_payload(item) for item in raw_layers
            ),
            schema_version=schema_version,
            instance_id=instance_id,
            extra=_extra(payload, cls._known),
        )

    def to_payload(self) -> JsonObject:
        return _merge(
            {
                "schema_version": self.schema_version,
                "instance_id": self.instance_id,
                "text_layers": [layer.to_payload() for layer in self.text_layers],
            },
            self.extra,
        )


__all__ = [
    "PdfDetectResult",
    "PdfDocumentMirror",
    "PdfModelDiff",
    "PdfMutationResult",
    "PdfOpenResult",
    "PdfPageInfoMirror",
    "PdfProgressEvent",
    "PdfProgressPhase",
    "PdfSaveResult",
    "TextBlockMirror",
    "TextLayerInfoMirror",
]

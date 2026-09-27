"""Strict parser/validator for the HTTP v2 protocol payloads.

This module is the single entry point for converting wire JSON back into the
DTO dataclasses. It intentionally rejects anything that would create *fake
compatibility*:

* unknown top-level required fields,
* unknown enum values (JobState/ItemState/JobKind/etc.),
* unknown error codes,
* illegal job/item state transitions when the caller asks for transition
  validation.

It does **not** try to be a general JSON validator — it only enforces the
shape we actually put on the wire.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from datetime import datetime
from typing import Any

from vibeocr.runtime.recognition.pipeline_contracts import (
    OCRPipeline,
    get_pipeline_supported_options,
)

from .dtos import (
    MINERU_PAGE_RANGE_PATTERN,
    SCHEMA_VERSION,
    TERMINAL_ITEM_STATES,
    TERMINAL_JOB_STATES,
    CancelMode,
    EvictionReason,
    ItemOutcome,
    ItemState,
    JobCommand,
    JobCommandKind,
    JobItem,
    JobKind,
    JobPriority,
    JobRef,
    JobSnapshot,
    JobState,
    JobSummary,
    JobUpdate,
    MineruConfig,
    MineruOcrMode,
    MineruTier,
    OcrEngine,
    PipelineSelection,
    PipelineSpec,
    ProgressSnapshot,
    ProgressUnit,
    RecognitionResourceKind,
    ResidencyEntry,
    ResidencyKind,
    RuntimeAccelerator,
    RuntimeComponentActualState,
    RuntimeComponentDesiredState,
    RuntimeComponentState,
    RuntimeComponentStatus,
    RuntimeDriftReason,
    RuntimeInstallPlan,
    RuntimeInstallPlanAction,
    RuntimeInstallPlanBlocker,
    RuntimeInstallPlanComponent,
    RuntimeInstallPlanCost,
    RuntimeInstallPlanDependencyState,
    RuntimeInstallPlanRequest,
    RuntimeInstallPlanResponse,
    RuntimeMaintenanceEvent,
    RuntimeMaintenanceEventType,
    RuntimeMaintenanceOperation,
    RuntimeMaintenancePhase,
    RuntimeMaintenanceReceipt,
    RuntimeMaintenanceStatus,
    RuntimeMaintenanceUpdate,
    RuntimeOperationState,
    RuntimeProfileStatus,
    RuntimeServiceState,
    RuntimeSourceIdentity,
    RuntimeStatusSnapshot,
    StageEvent,
    SubmitItem,
    SubmitRequest,
)
from .errors import ErrorCode, ErrorPayload, error_registry


class ContractError(ValueError):
    """Raised when a wire payload violates the v2 contract."""


class JobStateTransitionError(ContractError):
    """Raised when an observed transition is illegal per the state machine."""


# ---------------------------------------------------------------------------
# Allowed transitions
# ---------------------------------------------------------------------------

_JOB_TRANSITIONS: dict[JobState, frozenset[JobState]] = {
    JobState.ACCEPTED: frozenset(
        {JobState.QUEUED, JobState.FAILED, JobState.CANCELLED}
    ),
    JobState.QUEUED: frozenset(
        {
            JobState.RUNNING,
            JobState.CANCELLED,
            JobState.FAILED,
            JobState.CANCEL_REQUESTED,
        }
    ),
    JobState.RUNNING: frozenset(
        {
            JobState.COMPLETED,
            JobState.COMPLETED_WITH_ERRORS,
            JobState.CANCEL_REQUESTED,
            JobState.FAILED,
        }
    ),
    JobState.CANCEL_REQUESTED: frozenset({JobState.CANCELLED, JobState.FAILED}),
    # Terminal states never transition out.
    JobState.COMPLETED: frozenset(),
    JobState.COMPLETED_WITH_ERRORS: frozenset(),
    JobState.CANCELLED: frozenset(),
    JobState.FAILED: frozenset(),
}


_ITEM_TRANSITIONS: dict[ItemState, frozenset[ItemState]] = {
    ItemState.QUEUED: frozenset(
        {ItemState.RUNNING, ItemState.CANCELLED, ItemState.FAILED}
    ),
    ItemState.RUNNING: frozenset(
        {ItemState.SUCCEEDED, ItemState.FAILED, ItemState.CANCELLED}
    ),
    ItemState.SUCCEEDED: frozenset(),
    ItemState.FAILED: frozenset(),
    ItemState.CANCELLED: frozenset(),
}


def assert_job_transition(current: JobState, target: JobState) -> None:
    if target == current:
        return
    allowed = _JOB_TRANSITIONS.get(current)
    if allowed is None or target not in allowed:
        raise JobStateTransitionError(
            f"illegal job state transition: {current.value} -> {target.value}"
        )


def assert_item_transition(current: ItemState, target: ItemState) -> None:
    if target == current:
        return
    allowed = _ITEM_TRANSITIONS.get(current)
    if allowed is None or target not in allowed:
        raise JobStateTransitionError(
            f"illegal item state transition: {current.value} -> {target.value}"
        )


def is_terminal_job(state: JobState) -> bool:
    return state in TERMINAL_JOB_STATES


def is_terminal_item(state: ItemState) -> bool:
    return state in TERMINAL_ITEM_STATES


# ---------------------------------------------------------------------------
# Enum parsing helpers
# ---------------------------------------------------------------------------


def _require_enum(enum_cls: type, raw: Any, label: str) -> Any:
    if not isinstance(raw, str):
        raise ContractError(f"{label} must be a string, got {type(raw).__name__}")
    try:
        return enum_cls(raw)
    except ValueError as exc:
        raise ContractError(f"unknown {label}: {raw!r}") from exc


# ---------------------------------------------------------------------------
# Payload parsing
# ---------------------------------------------------------------------------


def _require_fields(
    payload: dict[str, Any], fields: tuple[str, ...], label: str
) -> None:
    missing = [f for f in fields if f not in payload or payload[f] is None]
    if missing:
        raise ContractError(f"{label} missing required field(s): {', '.join(missing)}")


def _require_present_fields(
    payload: dict[str, Any], fields: tuple[str, ...], label: str
) -> None:
    missing = [field for field in fields if field not in payload]
    if missing:
        raise ContractError(f"{label} missing required field(s): {', '.join(missing)}")


def _reject_unknown_fields(
    payload: dict[str, Any], allowed: frozenset[str], label: str
) -> None:
    unknown = sorted(set(payload).difference(allowed))
    if unknown:
        raise ContractError(f"{label} has unknown field(s): {', '.join(unknown)}")


def _parse_mineru_config(payload: Any) -> MineruConfig:
    if not isinstance(payload, dict):
        raise ContractError("mineru config must be a JSON object")
    _reject_unknown_fields(
        payload,
        frozenset({"tier", "ocr_mode", "page_range", "language"}),
        "mineru config",
    )
    _require_fields(payload, ("tier",), "mineru config")
    tier = _require_enum(MineruTier, payload["tier"], "mineru tier")
    ocr_mode = MineruOcrMode.AUTO
    if "ocr_mode" in payload:
        ocr_mode = _require_enum(MineruOcrMode, payload["ocr_mode"], "mineru ocr_mode")
    page_range = payload.get("page_range", "all")
    if not isinstance(page_range, str) or not re.fullmatch(
        MINERU_PAGE_RANGE_PATTERN, page_range
    ):
        raise ContractError(
            "mineru page_range must be 'all' or a comma-separated list of "
            "positive/reverse page indexes and closed ranges"
        )
    language = payload.get("language", "ch")
    if not isinstance(language, str) or not language or language != language.strip():
        raise ContractError(
            "mineru language must be a non-empty language id without "
            "surrounding whitespace"
        )
    return MineruConfig(
        tier=tier, ocr_mode=ocr_mode, page_range=page_range, language=language
    )


def parse_pipeline_selection(payload: dict[str, Any]) -> PipelineSelection:
    if not isinstance(payload, dict):
        raise ContractError("pipeline selection must be a JSON object")
    _reject_unknown_fields(
        payload,
        frozenset({"pipeline_id", "options_version", "options", "engine", "mineru"}),
        "pipeline selection",
    )
    _require_fields(
        payload, ("pipeline_id", "options_version", "options"), "pipeline selection"
    )
    pipeline_id = payload["pipeline_id"]
    if not isinstance(pipeline_id, str):
        raise ContractError("pipeline_id must be a string")
    try:
        pipeline = OCRPipeline(pipeline_id)
    except ValueError as exc:
        raise ContractError(f"unknown pipeline_id: {pipeline_id!r}") from exc
    version = payload["options_version"]
    if version != 1:
        raise ContractError(f"unsupported options_version: {version!r}")
    options = payload["options"]
    if not isinstance(options, dict):
        raise ContractError("pipeline options must be a JSON object")
    allowed_options = set(get_pipeline_supported_options(pipeline))
    unknown_options = sorted(set(options).difference(allowed_options))
    if unknown_options:
        raise ContractError(
            f"unsupported option(s) for {pipeline.value}: {', '.join(unknown_options)}"
        )
    if "mineru" in payload and "engine" in payload:
        raise ContractError(
            "mineru config cannot be combined with the engine field; "
            "reject with VALIDATION_ERROR semantics"
        )
    engine = None
    if "engine" in payload:
        engine = _require_enum(OcrEngine, payload["engine"], "ocr engine")
        if pipeline is not OCRPipeline.OCR:
            raise ContractError(
                "engine is only valid for the plain-text OCR pipeline; use "
                "OCR_ENGINE_NOT_VALID_FOR_PIPELINE semantics"
            )
    mineru = None
    if "mineru" in payload:
        if payload["mineru"] is None:
            raise ContractError(
                "mineru config must be a JSON object, not an explicit null"
            )
        if pipeline is not OCRPipeline.DOCUMENT_PARSING:
            raise ContractError("mineru config requires the MinerU pipeline")
        if options:
            raise ContractError(
                "mineru config cannot be combined with legacy pipeline options; "
                "reject with VALIDATION_ERROR semantics"
            )
        mineru = _parse_mineru_config(payload["mineru"])
    return PipelineSelection(
        pipeline_id=pipeline.value,
        options_version=version,
        options=dict(options),
        engine=engine,
        mineru=mineru,
    )


def _parse_submit_item(payload: Any) -> SubmitItem:
    if not isinstance(payload, dict):
        raise ContractError("submit item must be a JSON object")
    _reject_unknown_fields(
        payload,
        frozenset({"client_item_key", "ordinal", "display_name", "source"}),
        "submit item",
    )
    _require_fields(
        payload,
        ("client_item_key", "ordinal", "display_name", "source"),
        "submit item",
    )
    source = payload["source"]
    if not isinstance(source, dict):
        raise ContractError("submit item source must be a JSON object")
    source_type = source.get("type")
    if source_type == "upload.v1":
        _reject_unknown_fields(
            source, frozenset({"type", "attachment"}), "upload source"
        )
        _require_fields(source, ("type", "attachment"), "upload source")
    elif source_type == "pdf_page.v1":
        _reject_unknown_fields(
            source,
            frozenset({"type", "session_id", "session_revision", "page_index"}),
            "pdf page source",
        )
        _require_fields(
            source,
            ("type", "session_id", "session_revision", "page_index"),
            "pdf page source",
        )
        if not isinstance(source["page_index"], int) or source["page_index"] < 0:
            raise ContractError("pdf page_index must be a non-negative integer")
    else:
        raise ContractError(f"unknown submit source type: {source_type!r}")
    ordinal = payload["ordinal"]
    if not isinstance(ordinal, int) or ordinal < 0:
        raise ContractError("submit item ordinal must be a non-negative integer")
    client_key = payload["client_item_key"]
    if not isinstance(client_key, str) or not client_key:
        raise ContractError("client_item_key must be a non-empty string")
    display_name = payload["display_name"]
    if not isinstance(display_name, str):
        raise ContractError("display_name must be a string")
    return SubmitItem(
        client_item_key=client_key,
        ordinal=ordinal,
        display_name=display_name,
        source=dict(source),
    )


def parse_submit_request(payload: dict[str, Any]) -> SubmitRequest:
    if not isinstance(payload, dict):
        raise ContractError("submit request must be a JSON object")
    _reject_unknown_fields(
        payload,
        frozenset(
            {
                "schema_version",
                "request_id",
                "kind",
                "priority",
                "pipeline",
                "items",
                "parameters",
            }
        ),
        "submit request",
    )
    _require_fields(
        payload,
        ("schema_version", "request_id", "kind", "priority", "pipeline", "items"),
        "submit request",
    )
    if payload["schema_version"] != SCHEMA_VERSION:
        raise ContractError(
            f"schema_version mismatch: expected {SCHEMA_VERSION}, "
            f"got {payload['schema_version']}"
        )
    request_id = payload["request_id"]
    if not isinstance(request_id, str) or not request_id:
        raise ContractError("request_id must be a non-empty string")
    kind = _require_enum(JobKind, payload["kind"], "job kind")
    if kind not in {JobKind.RECOGNITION, JobKind.MINERU_PARSE, JobKind.PDF_OCR}:
        raise ContractError(f"job kind is not submittable: {kind.value}")
    priority = _require_enum(JobPriority, payload["priority"], "job priority")
    pipeline = parse_pipeline_selection(payload["pipeline"])
    if (
        kind is JobKind.MINERU_PARSE
        and pipeline.pipeline_id != OCRPipeline.DOCUMENT_PARSING.value
    ):
        raise ContractError("mineru_parse requires the MinerU pipeline")
    if (
        kind is JobKind.RECOGNITION
        and pipeline.pipeline_id == OCRPipeline.DOCUMENT_PARSING.value
    ):
        raise ContractError("MinerU requires kind=mineru_parse")
    if pipeline.mineru is not None and kind is not JobKind.MINERU_PARSE:
        raise ContractError("mineru config requires kind=mineru_parse")
    items_raw = payload["items"]
    if not isinstance(items_raw, list) or not items_raw:
        raise ContractError("submit request items must be a non-empty list")
    items = tuple(_parse_submit_item(item) for item in items_raw)
    keys = [item.client_item_key for item in items]
    if len(keys) != len(set(keys)):
        raise ContractError("client_item_key must be unique within a job")
    ordinals = [item.ordinal for item in items]
    if sorted(ordinals) != list(range(len(items))):
        raise ContractError(
            "submit item ordinals must be unique and contiguous from zero"
        )
    parameters = payload.get("parameters", {})
    if not isinstance(parameters, dict):
        raise ContractError("submit request parameters must be a JSON object")
    return SubmitRequest(
        request_id=request_id,
        kind=kind,
        priority=priority,
        pipeline=pipeline,
        items=items,
        parameters=dict(parameters),
    )


def parse_job_ref(payload: dict[str, Any]) -> JobRef:
    if not isinstance(payload, dict):
        raise ContractError("job ref must be a JSON object")
    _require_fields(payload, ("job_id", "schema_version", "state"), "job ref")
    if payload["schema_version"] != SCHEMA_VERSION:
        raise ContractError(
            f"schema_version mismatch: expected {SCHEMA_VERSION}, "
            f"got {payload['schema_version']}"
        )
    items_raw = payload.get("items", [])
    if not isinstance(items_raw, list):
        raise ContractError("job ref items must be a list")
    return JobRef(
        job_id=payload["job_id"],
        schema_version=payload["schema_version"],
        instance_id=payload.get("instance_id"),
        state=_require_enum(JobState, payload["state"], "job state"),
        items=tuple(_parse_job_item(item) for item in items_raw),
    )


def parse_job_snapshot(payload: dict[str, Any]) -> JobSnapshot:
    if not isinstance(payload, dict):
        raise ContractError("job snapshot must be a JSON object")
    _require_fields(
        payload,
        ("job_id", "kind", "priority", "state", "schema_version"),
        "job snapshot",
    )
    sv = payload["schema_version"]
    if sv != SCHEMA_VERSION:
        raise ContractError(
            f"schema_version mismatch: expected {SCHEMA_VERSION}, got {sv}"
        )
    items_raw = payload.get("items", [])
    if not isinstance(items_raw, list):
        raise ContractError("items must be a list")
    items = tuple(_parse_job_item(it) for it in items_raw)
    summary_raw = payload.get("summary", {})
    summary = _parse_summary(summary_raw)
    state = _require_enum(JobState, payload["state"], "job state")
    cancel_mode_raw = payload.get("cancel_mode")
    cancel_mode = (
        _require_enum(CancelMode, cancel_mode_raw, "cancel_mode")
        if cancel_mode_raw is not None
        else None
    )
    return JobSnapshot(
        job_id=payload["job_id"],
        kind=_require_enum(JobKind, payload["kind"], "job kind"),
        priority=_require_enum(JobPriority, payload["priority"], "job priority"),
        state=state,
        schema_version=int(sv),
        instance_id=payload.get("instance_id"),
        created_at=payload["created_at"],
        started_at=payload.get("started_at"),
        finished_at=payload.get("finished_at"),
        stage=payload.get("stage"),
        progress_current=int(payload.get("progress_current", 0)),
        progress_total=int(payload.get("progress_total", 0)),
        items=items,
        summary=summary,
        cancel_requested_at=payload.get("cancel_requested_at"),
        cancel_mode=cancel_mode,
        degraded=bool(payload.get("degraded", False)),
        event_sequence=int(payload.get("event_sequence", 0)),
        result_available=bool(payload.get("result_available", False)),
        request_id=payload.get("request_id"),
        source_job_id=payload.get("source_job_id"),
        pipeline=(
            parse_pipeline_selection(payload["pipeline"])
            if payload.get("pipeline") is not None
            else None
        ),
        progress=(
            _parse_progress_snapshot(payload["progress"])
            if payload.get("progress") is not None
            else None
        ),
    )


def _parse_job_item(payload: Any) -> JobItem:
    if not isinstance(payload, dict):
        raise ContractError("job item must be a JSON object")
    _require_fields(payload, ("item_id", "display_name", "state"), "job item")
    return JobItem(
        item_id=payload["item_id"],
        display_name=payload["display_name"],
        state=_require_enum(ItemState, payload["state"], "item state"),
        attempt=int(payload.get("attempt", 0)),
        error=payload.get("error"),
        client_item_key=payload.get("client_item_key"),
        ordinal=int(payload.get("ordinal", 0)),
        source_item_id=payload.get("source_item_id"),
    )


def _parse_stage_event(payload: Any) -> StageEvent:
    if not isinstance(payload, dict):
        raise ContractError("stage event must be a JSON object")
    _require_fields(payload, ("sequence", "stage", "timestamp"), "stage event")
    detail = payload.get("detail", {})
    if not isinstance(detail, dict):
        raise ContractError("stage event detail must be a JSON object")
    return StageEvent(
        sequence=int(payload["sequence"]),
        stage=payload["stage"],
        item_id=payload.get("item_id"),
        timestamp=payload["timestamp"],
        detail=detail,
        progress=(
            _parse_progress_snapshot(payload["progress"])
            if payload.get("progress") is not None
            else None
        ),
        message_code=payload.get("message_code"),
    )


def _parse_progress_snapshot(payload: Any) -> ProgressSnapshot:
    if not isinstance(payload, dict):
        raise ContractError("progress must be a JSON object")
    _require_fields(payload, ("unit", "current"), "progress")
    current = payload["current"]
    total = payload.get("total")
    estimated_remaining_seconds = payload.get("estimated_remaining_seconds")
    if not isinstance(current, int) or current < 0:
        raise ContractError("progress current must be a non-negative integer")
    if total is not None and (not isinstance(total, int) or total < 0):
        raise ContractError("progress total must be a non-negative integer")
    unit = _require_enum(ProgressUnit, payload["unit"], "progress unit")
    if estimated_remaining_seconds is not None:
        if (
            isinstance(estimated_remaining_seconds, bool)
            or not isinstance(estimated_remaining_seconds, int | float)
            or estimated_remaining_seconds < 0
        ):
            raise ContractError("progress ETA must be a non-negative number")
        if unit not in {ProgressUnit.ITEMS, ProgressUnit.BYTES}:
            raise ContractError("progress ETA requires items or bytes")
        if total is None or total <= 0:
            raise ContractError("progress ETA requires a positive real total")
    return ProgressSnapshot(
        unit=unit,
        current=current,
        total=total,
        estimated_remaining_seconds=(
            float(estimated_remaining_seconds)
            if estimated_remaining_seconds is not None
            else None
        ),
    )


def _parse_item_outcome(payload: Any) -> ItemOutcome:
    if not isinstance(payload, dict):
        raise ContractError("item outcome must be a JSON object")
    _require_fields(payload, ("item_id", "state", "attempt"), "item outcome")
    state = _require_enum(ItemState, payload["state"], "item state")
    if state not in TERMINAL_ITEM_STATES:
        raise ContractError("item outcome state must be terminal")
    result = payload.get("payload")
    error_code = payload.get("error_code")
    payload_type = payload.get("payload_type")
    if state is ItemState.SUCCEEDED:
        if not isinstance(result, dict) or not payload_type or error_code is not None:
            raise ContractError(
                "succeeded item outcome requires payload_type/payload and no error"
            )
    elif result is not None or payload_type is not None or not error_code:
        raise ContractError(
            "failed/cancelled item outcome requires error and no result payload"
        )
    error_detail = payload.get("error_detail", {})
    if not isinstance(error_detail, dict):
        raise ContractError("item outcome error_detail must be a JSON object")
    return ItemOutcome(
        item_id=payload["item_id"],
        state=state,
        attempt=int(payload["attempt"]),
        payload_type=payload_type,
        payload=result,
        error_code=error_code,
        error_detail=error_detail,
    )


def parse_job_update(payload: dict[str, Any]) -> JobUpdate:
    if not isinstance(payload, dict):
        raise ContractError("job update must be a JSON object")
    _require_fields(
        payload,
        (
            "schema_version",
            "snapshot",
            "events",
            "outcomes",
            "through_sequence",
            "more",
        ),
        "job update",
    )
    if payload["schema_version"] != SCHEMA_VERSION:
        raise ContractError(
            f"schema_version mismatch: expected {SCHEMA_VERSION}, "
            f"got {payload['schema_version']}"
        )
    events_raw = payload["events"]
    outcomes_raw = payload["outcomes"]
    if not isinstance(events_raw, list) or not isinstance(outcomes_raw, list):
        raise ContractError("job update events/outcomes must be lists")
    events = tuple(_parse_stage_event(event) for event in events_raw)
    outcomes = tuple(_parse_item_outcome(outcome) for outcome in outcomes_raw)
    through = int(payload["through_sequence"])
    if through < 0 or any(event.sequence > through for event in events):
        raise ContractError("job update through_sequence is inconsistent")
    return JobUpdate(
        snapshot=parse_job_snapshot(payload["snapshot"]),
        events=events,
        outcomes=outcomes,
        through_sequence=through,
        more=bool(payload["more"]),
    )


def parse_job_command(payload: dict[str, Any]) -> JobCommand:
    if not isinstance(payload, dict):
        raise ContractError("job command must be a JSON object")
    _reject_unknown_fields(
        payload,
        frozenset({"command_id", "kind", "job_id", "item_ids", "priority_override"}),
        "job command",
    )
    _require_fields(payload, ("command_id", "kind", "job_id"), "job command")
    item_ids = payload.get("item_ids", [])
    if not isinstance(item_ids, list) or any(
        not isinstance(item, str) for item in item_ids
    ):
        raise ContractError("job command item_ids must be a list of strings")
    priority_raw = payload.get("priority_override")
    return JobCommand(
        command_id=payload["command_id"],
        kind=_require_enum(JobCommandKind, payload["kind"], "job command kind"),
        job_id=payload["job_id"],
        item_ids=tuple(item_ids),
        priority_override=(
            _require_enum(JobPriority, priority_raw, "job priority")
            if priority_raw is not None
            else None
        ),
    )


def _parse_summary(payload: Any) -> JobSummary:
    if payload is None:
        return JobSummary()
    if not isinstance(payload, dict):
        raise ContractError("summary must be a JSON object")
    return JobSummary(
        succeeded=int(payload.get("succeeded", 0)),
        failed=int(payload.get("failed", 0)),
        cancelled=int(payload.get("cancelled", 0)),
        total=int(payload.get("total", 0)),
    )


def parse_error_payload(payload: dict[str, Any]) -> ErrorPayload:
    if not isinstance(payload, dict):
        raise ContractError("error payload must be a JSON object")
    _require_present_fields(
        payload,
        (
            "schema_version",
            "instance_id",
            "code",
            "message",
            "category",
            "retryable",
            "detail",
            "job_id",
        ),
        "error payload",
    )
    if payload["schema_version"] != SCHEMA_VERSION:
        raise ContractError(
            f"schema_version mismatch: expected {SCHEMA_VERSION}, "
            f"got {payload['schema_version']}"
        )
    code_raw = payload["code"]
    try:
        code = code_raw if isinstance(code_raw, ErrorCode) else ErrorCode(code_raw)
    except ValueError as exc:
        raise ContractError(f"unknown error code: {code_raw!r}") from exc
    # Cross-check against the registry so a code only valid in one place is
    # rejected. ``error_registry`` is loaded from errors.json and is total over
    # the ErrorCode enum (every member is registered), so a value that survived
    # the ErrorCode() construction above is always present here; the guard is
    # retained as a defensive invariant for future registry edits.
    if (
        code not in error_registry
    ):  # pragma: no cover - registry is total over ErrorCode
        raise ContractError(f"error code not in registry: {code.value}")
    registry_entry = error_registry[code]
    category_raw = payload["category"]
    if category_raw != registry_entry.category.value:
        raise ContractError(
            f"category mismatch for {code.value}: payload={category_raw!r} "
            f"registry={registry_entry.category.value}"
        )
    retryable_raw = payload["retryable"]
    if not isinstance(retryable_raw, bool) or retryable_raw != registry_entry.retryable:
        raise ContractError(
            f"retryable mismatch for {code.value}: payload={retryable_raw!r} "
            f"registry={registry_entry.retryable!r}"
        )
    retry_after = payload.get("retry_after")
    if retry_after is not None:
        if type(retry_after) is not int or retry_after < 0:
            raise ContractError("retry_after must be null or a non-negative integer")
        if not retryable_raw:
            raise ContractError("retry_after requires a retryable error")
    instance_id = payload["instance_id"]
    if instance_id is not None and not isinstance(instance_id, str):
        raise ContractError("instance_id must be null or a string")
    message = payload["message"]
    if not isinstance(message, str):
        raise ContractError("message must be a string")
    detail = payload["detail"]
    if not isinstance(detail, dict):
        raise ContractError("detail must be a JSON object")
    job_id = payload["job_id"]
    if job_id is not None and not isinstance(job_id, str):
        raise ContractError("job_id must be null or a string")
    return ErrorPayload(
        schema_version=int(payload["schema_version"]),
        instance_id=instance_id,
        code=code,
        message=message,
        category=registry_entry.category,
        retryable=retryable_raw,
        retry_after=retry_after,
        detail=dict(detail),
        job_id=job_id,
    )


def parse_residency_entry(payload: dict[str, Any]) -> ResidencyEntry:
    if not isinstance(payload, dict):
        raise ContractError("residency entry must be a JSON object")
    _require_fields(payload, ("pipeline", "kind"), "residency entry")
    kind = _require_enum(ResidencyKind, payload["kind"], "residency kind")
    reason = EvictionReason.NONE
    raw_reason = payload.get("eviction_reason")
    if raw_reason is not None:
        reason = _require_enum(EvictionReason, raw_reason, "eviction reason")
    resource_kind = None
    raw_resource_kind = payload.get("resource_kind")
    if raw_resource_kind is not None:
        resource_kind = _require_enum(
            RecognitionResourceKind,
            raw_resource_kind,
            "recognition resource kind",
        )
    return ResidencyEntry(
        pipeline=payload["pipeline"],
        kind=kind,
        recognition_mode=payload.get("recognition_mode"),
        resource_kind=resource_kind,
        resource_id=payload.get("resource_id"),
        active_leases=int(payload.get("active_leases", 0)),
        remaining_ttl_seconds=payload.get("remaining_ttl_seconds"),
        estimated_vram_mb=payload.get("estimated_vram_mb"),
        eviction_reason=reason,
    )


def parse_pipeline_spec(payload: dict[str, Any]) -> PipelineSpec:
    if not isinstance(payload, dict):
        raise ContractError("pipeline spec must be a JSON object")
    _require_fields(payload, ("name",), "pipeline spec")
    ttl = payload.get("ttl_seconds")
    if ttl is not None and (not isinstance(ttl, int) or ttl < 0):
        raise ContractError(
            f"ttl_seconds must be null or non-negative int, got {ttl!r}"
        )
    return PipelineSpec(
        name=payload["name"],
        ttl_seconds=ttl,
        pinned=bool(payload.get("pinned", False)),
        recognition_mode=payload.get("recognition_mode"),
    )


def _parse_runtime_source_identity(payload: Any) -> RuntimeSourceIdentity:
    if not isinstance(payload, dict):
        raise ContractError("runtime source identity must be a JSON object")
    fields = (
        "backend_version",
        "backend_source_sha",
        "runtime_manifest_sha256",
    )
    _require_fields(payload, fields, "runtime source identity")
    if set(payload) != set(fields):
        raise ContractError("runtime source identity contains unsupported fields")
    if any(not isinstance(payload[field], str) for field in fields):
        raise ContractError("runtime source identity fields must be strings")
    if len(payload["backend_source_sha"]) != 40 or any(
        char not in "0123456789abcdef" for char in payload["backend_source_sha"]
    ):
        raise ContractError("backend_source_sha must be a full lowercase Git SHA")
    for field in ("runtime_manifest_sha256",):
        value = payload[field]
        if len(value) != 64 or any(char not in "0123456789abcdef" for char in value):
            raise ContractError(f"{field} must be a lowercase SHA-256 digest")
    return RuntimeSourceIdentity(**{field: payload[field] for field in fields})


def _parse_component_ids(payload: Any, field: str) -> tuple[str, ...]:
    if payload is None:
        return ()
    if (
        not isinstance(payload, list)
        or any(not isinstance(item, str) or not item for item in payload)
        or len(set(payload)) != len(payload)
    ):
        raise ContractError(f"{field} must contain unique non-empty strings")
    return tuple(payload)


def _parse_runtime_maintenance_status(payload: Any) -> RuntimeMaintenanceStatus:
    if not isinstance(payload, dict):
        raise ContractError("runtime maintenance must be a JSON object")
    _require_fields(
        payload,
        (
            "operation_id",
            "sequence",
            "operation",
            "operation_state",
            "phase",
            "profile_id",
            "updated_at",
        ),
        "runtime maintenance",
    )
    sequence = payload["sequence"]
    if type(sequence) is not int or sequence < 1:
        raise ContractError("runtime maintenance sequence must be a positive integer")
    return RuntimeMaintenanceStatus(
        operation_id=payload["operation_id"],
        source_operation_id=payload.get("source_operation_id"),
        sequence=sequence,
        operation=_require_enum(
            RuntimeMaintenanceOperation,
            payload["operation"],
            "runtime maintenance operation",
        ),
        operation_state=_require_enum(
            RuntimeOperationState,
            payload["operation_state"],
            "runtime operation state",
        ),
        phase=_require_enum(
            RuntimeMaintenancePhase,
            payload["phase"],
            "runtime maintenance phase",
        ),
        profile_id=payload["profile_id"],
        component_id=payload.get("component_id"),
        updated_at=payload["updated_at"],
        progress=(
            _parse_progress_snapshot(payload["progress"])
            if payload.get("progress") is not None
            else None
        ),
        message_code=payload.get("message_code"),
        requested_component_ids=_parse_component_ids(
            payload.get("requested_component_ids"),
            "requested_component_ids",
        ),
        effective_component_ids=_parse_component_ids(
            payload.get("effective_component_ids"),
            "effective_component_ids",
        ),
        requested_download_source_ids=_parse_component_ids(
            payload.get("requested_download_source_ids"),
            "requested_download_source_ids",
        ),
        effective_download_source_ids=_parse_component_ids(
            payload.get("effective_download_source_ids"),
            "effective_download_source_ids",
        ),
        source=(
            _parse_runtime_source_identity(payload["source"])
            if payload.get("source") is not None
            else None
        ),
        plan_id=payload.get("plan_id"),
    )


def parse_runtime_status(payload: dict[str, Any]) -> RuntimeStatusSnapshot:
    if not isinstance(payload, dict):
        raise ContractError("runtime status must be a JSON object")
    _require_fields(
        payload,
        (
            "schema_version",
            "instance_id",
            "service_state",
            "backend_version",
            "profile",
            "maintenance",
        ),
        "runtime status",
    )
    if payload["schema_version"] != SCHEMA_VERSION:
        raise ContractError(
            "schema_version mismatch: "
            f"expected {SCHEMA_VERSION}, got {payload['schema_version']}"
        )
    profile = payload["profile"]
    if not isinstance(profile, dict):
        raise ContractError("runtime profile must be a JSON object")
    _require_fields(
        profile, ("profile_id", "accelerator", "components"), "runtime profile"
    )
    components = profile["components"]
    if not isinstance(components, list):
        raise ContractError("runtime profile components must be a list")
    parsed_components: list[RuntimeComponentStatus] = []
    for component in components:
        if not isinstance(component, dict):
            raise ContractError("runtime component must be a JSON object")
        _require_fields(
            component,
            ("component_id", "display_name", "state"),
            "runtime component",
        )
        parsed_components.append(
            RuntimeComponentStatus(
                component_id=component["component_id"],
                display_name=component["display_name"],
                state=_require_enum(
                    RuntimeComponentState,
                    component["state"],
                    "runtime component state",
                ),
                version=component.get("version"),
                desired_state=(
                    _require_enum(
                        RuntimeComponentDesiredState,
                        component["desired_state"],
                        "runtime component desired state",
                    )
                    if component.get("desired_state") is not None
                    else None
                ),
                desired_version=component.get("desired_version"),
                actual_state=(
                    _require_enum(
                        RuntimeComponentActualState,
                        component["actual_state"],
                        "runtime component actual state",
                    )
                    if component.get("actual_state") is not None
                    else None
                ),
                actual_version=component.get("actual_version"),
                drift_reason=(
                    _require_enum(
                        RuntimeDriftReason,
                        component["drift_reason"],
                        "runtime component drift reason",
                    )
                    if component.get("drift_reason") is not None
                    else None
                ),
                repairable=(
                    component["repairable"] if "repairable" in component else None
                ),
            )
        )
        if "repairable" in component and not isinstance(component["repairable"], bool):
            raise ContractError("runtime component repairable must be a boolean")
    maintenance_raw = payload["maintenance"]
    maintenance = None
    if maintenance_raw is not None:
        maintenance = _parse_runtime_maintenance_status(maintenance_raw)
    return RuntimeStatusSnapshot(
        schema_version=SCHEMA_VERSION,
        instance_id=payload["instance_id"],
        service_state=_require_enum(
            RuntimeServiceState,
            payload["service_state"],
            "runtime service state",
        ),
        backend_version=payload["backend_version"],
        profile=RuntimeProfileStatus(
            profile_id=profile["profile_id"],
            accelerator=_require_enum(
                RuntimeAccelerator,
                profile["accelerator"],
                "runtime accelerator",
            ),
            components=tuple(parsed_components),
        ),
        maintenance=maintenance,
        source=(
            _parse_runtime_source_identity(payload["source"])
            if payload.get("source") is not None
            else None
        ),
    )


def parse_runtime_maintenance_receipt(
    payload: dict[str, Any],
) -> RuntimeMaintenanceReceipt:
    if not isinstance(payload, dict):
        raise ContractError("runtime maintenance receipt must be a JSON object")
    _require_fields(
        payload,
        ("schema_version", "operation_id", "snapshot", "negotiated_capabilities"),
        "runtime maintenance receipt",
    )
    if payload["schema_version"] != SCHEMA_VERSION:
        raise ContractError("runtime maintenance receipt schema_version mismatch")
    negotiated = _parse_component_ids(
        payload["negotiated_capabilities"], "negotiated_capabilities"
    )
    snapshot = _parse_runtime_maintenance_status(payload["snapshot"])
    if snapshot.operation_id != payload["operation_id"]:
        raise ContractError("runtime maintenance receipt operation_id mismatch")
    return RuntimeMaintenanceReceipt(
        operation_id=payload["operation_id"],
        snapshot=snapshot,
        negotiated_capabilities=negotiated,
    )


def parse_runtime_maintenance_event(payload: dict[str, Any]) -> RuntimeMaintenanceEvent:
    if not isinstance(payload, dict):
        raise ContractError("runtime maintenance event must be a JSON object")
    _require_fields(
        payload,
        (
            "schema_version",
            "event_type",
            "operation",
            "snapshot",
            "message_code",
        ),
        "runtime maintenance event",
    )
    if payload["schema_version"] != SCHEMA_VERSION:
        raise ContractError("runtime maintenance event schema_version mismatch")
    snapshot = _parse_runtime_maintenance_status(payload["snapshot"])
    sequence = payload.get("sequence", snapshot.sequence)
    if type(sequence) is not int or sequence < 1 or snapshot.sequence != sequence:
        raise ContractError("runtime maintenance event sequence mismatch")
    operation = _require_enum(
        RuntimeMaintenanceOperation, payload["operation"], "runtime operation"
    )
    if operation != snapshot.operation:
        raise ContractError("runtime maintenance event operation mismatch")
    message_args = payload.get("message_args", {})
    if not isinstance(message_args, dict) or any(
        not isinstance(key, str) or not isinstance(value, str)
        for key, value in message_args.items()
    ):
        raise ContractError("runtime maintenance event message_args must be strings")
    return RuntimeMaintenanceEvent(
        sequence=sequence,
        event_type=_require_enum(
            RuntimeMaintenanceEventType, payload["event_type"], "runtime event type"
        ),
        operation=operation,
        snapshot=snapshot,
        message_code=payload["message_code"],
        message_args=dict(message_args),
        fallback_message=payload.get("fallback_message"),
    )


def parse_runtime_maintenance_update(
    payload: dict[str, Any],
) -> RuntimeMaintenanceUpdate:
    if not isinstance(payload, dict):
        raise ContractError("runtime maintenance update must be a JSON object")
    _require_present_fields(
        payload,
        (
            "schema_version",
            "operation_id",
            "snapshot",
            "events",
            "oldest_sequence",
            "through_sequence",
            "more",
            "replay_expires_at",
        ),
        "runtime maintenance update",
    )
    if payload["schema_version"] != SCHEMA_VERSION:
        raise ContractError("runtime maintenance update schema_version mismatch")
    if not isinstance(payload["events"], list):
        raise ContractError("runtime maintenance update events must be a list")
    events = tuple(parse_runtime_maintenance_event(item) for item in payload["events"])
    sequences = [event.sequence for event in events]
    if any(right != left + 1 for left, right in zip(sequences, sequences[1:])):
        raise ContractError("runtime maintenance event sequence gap")
    through = payload["through_sequence"]
    if (
        type(through) is not int
        or through < 0
        or (sequences and sequences[-1] != through)
    ):
        raise ContractError("runtime maintenance through_sequence mismatch")
    snapshot = _parse_runtime_maintenance_status(payload["snapshot"])
    oldest = payload["oldest_sequence"]
    if type(oldest) is not int or oldest < 1:
        raise ContractError("runtime maintenance oldest_sequence mismatch")
    if oldest > snapshot.sequence or (sequences and sequences[0] < oldest):
        raise ContractError("runtime maintenance oldest_sequence mismatch")
    if snapshot.sequence < through:
        raise ContractError("runtime maintenance snapshot precedes cursor")
    if snapshot.operation_id != payload["operation_id"]:
        raise ContractError("runtime maintenance update operation_id mismatch")
    if any(event.snapshot.operation_id != payload["operation_id"] for event in events):
        raise ContractError("runtime maintenance update event operation_id mismatch")
    more = payload["more"]
    if type(more) is not bool:
        raise ContractError("runtime maintenance update more must be a boolean")
    replay_expires_at = payload["replay_expires_at"]
    if replay_expires_at is not None:
        if not isinstance(replay_expires_at, str):
            raise ContractError("runtime maintenance replay_expires_at is invalid")
        try:
            expires_at = datetime.fromisoformat(
                replay_expires_at.replace("Z", "+00:00")
            )
        except ValueError as exc:
            raise ContractError(
                "runtime maintenance replay_expires_at is invalid"
            ) from exc
        if expires_at.tzinfo is None:
            raise ContractError("runtime maintenance replay_expires_at is invalid")
    return RuntimeMaintenanceUpdate(
        operation_id=payload["operation_id"],
        snapshot=snapshot,
        events=events,
        oldest_sequence=oldest,
        through_sequence=through,
        more=more,
        replay_expires_at=replay_expires_at,
    )


def _parse_date_time(value: Any, label: str) -> str:
    if not isinstance(value, str):
        raise ContractError(f"{label} must be an ISO-8601 date-time string")
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as exc:
        raise ContractError(f"{label} must be an ISO-8601 date-time string") from exc
    if parsed.tzinfo is None:
        raise ContractError(f"{label} must carry a timezone offset")
    return value


def _parse_nullable_component_ids(
    payload: Any, field: str, *, allow_empty: bool
) -> tuple[str, ...] | None:
    if payload is None:
        return None
    parsed = _parse_component_ids(payload, field)
    if not allow_empty and not parsed:
        raise ContractError(f"{field} must be non-empty when not null")
    return parsed


def _require_plan_text(value: Any, field: str) -> str:
    if not isinstance(value, str) or not value:
        raise ContractError(f"{field} must be a non-empty string")
    return value


def _parse_runtime_install_plan_component(payload: Any) -> RuntimeInstallPlanComponent:
    if not isinstance(payload, dict):
        raise ContractError("install plan component must be a JSON object")
    _require_fields(
        payload,
        ("component_id", "action", "dependency_state", "reason_codes"),
        "install plan component",
    )
    reason_codes = _parse_component_ids(payload["reason_codes"], "reason_codes")
    return RuntimeInstallPlanComponent(
        component_id=_require_plan_text(payload["component_id"], "component_id"),
        action=_require_enum(
            RuntimeInstallPlanAction, payload["action"], "install plan action"
        ),
        dependency_state=_require_enum(
            RuntimeInstallPlanDependencyState,
            payload["dependency_state"],
            "install plan dependency state",
        ),
        reason_codes=reason_codes,
    )


def _parse_runtime_install_plan_blocker(payload: Any) -> RuntimeInstallPlanBlocker:
    if not isinstance(payload, dict):
        raise ContractError("install plan blocker must be a JSON object")
    _require_fields(payload, ("code", "next_action"), "install plan blocker")
    component_id = payload.get("component_id")
    if component_id is not None and (
        not isinstance(component_id, str) or not component_id
    ):
        raise ContractError(
            "install plan blocker component_id must be a non-empty string"
        )
    return RuntimeInstallPlanBlocker(
        code=_require_plan_text(payload["code"], "blocker code"),
        next_action=_require_plan_text(payload["next_action"], "next_action"),
        component_id=component_id,
    )


def _parse_runtime_install_plan_cost(payload: Any) -> RuntimeInstallPlanCost:
    if not isinstance(payload, dict):
        raise ContractError("install plan cost must be a JSON object")
    # Totals are required but explicitly nullable.
    _require_present_fields(
        payload,
        ("download_bytes", "additional_disk_bytes", "unknown_reason_codes"),
        "install plan cost",
    )
    _require_fields(payload, ("unknown_reason_codes",), "install plan cost")
    totals: dict[str, int | None] = {}
    for field in ("download_bytes", "additional_disk_bytes"):
        value = payload[field]
        if value is not None and (type(value) is not int or value < 0):
            raise ContractError(
                f"install plan cost {field} must be a non-negative integer or null"
            )
        totals[field] = value
    unknown_reason_codes = _parse_component_ids(
        payload["unknown_reason_codes"], "unknown_reason_codes"
    )
    if (
        totals["download_bytes"] is None or totals["additional_disk_bytes"] is None
    ) and not unknown_reason_codes:
        raise ContractError(
            "install plan cost unknown totals require unknown_reason_codes"
        )
    return RuntimeInstallPlanCost(
        download_bytes=totals["download_bytes"],
        additional_disk_bytes=totals["additional_disk_bytes"],
        unknown_reason_codes=unknown_reason_codes,
    )


def _parse_runtime_install_plan(payload: Any) -> RuntimeInstallPlan:
    if not isinstance(payload, dict):
        raise ContractError("install plan must be a JSON object")
    # requested_* echo fields are required but explicitly nullable, so only
    # presence is checked here; the per-field parses reject other holes.
    _require_present_fields(
        payload,
        (
            "plan_id",
            "expires_at",
            "accelerator",
            "profile_id",
            "requested_component_ids",
            "effective_component_ids",
            "requested_download_source_ids",
            "effective_download_source_ids",
            "source",
            "components",
            "blockers",
            "cost",
        ),
        "install plan",
    )
    _require_fields(
        payload,
        (
            "plan_id",
            "profile_id",
            "expires_at",
            "accelerator",
            "source",
            "cost",
            "components",
            "blockers",
            "effective_component_ids",
            "effective_download_source_ids",
        ),
        "install plan",
    )
    if not isinstance(payload["components"], list):
        raise ContractError("install plan components must be a list")
    if not isinstance(payload["blockers"], list):
        raise ContractError("install plan blockers must be a list")
    components = tuple(
        _parse_runtime_install_plan_component(item) for item in payload["components"]
    )
    if len({item.component_id for item in components}) != len(components):
        raise ContractError("install plan component_id values must be unique")
    cost = _parse_runtime_install_plan_cost(payload["cost"])
    return RuntimeInstallPlan(
        plan_id=_require_plan_text(payload["plan_id"], "plan_id"),
        expires_at=_parse_date_time(payload["expires_at"], "install plan expires_at"),
        accelerator=_require_enum(
            RuntimeAccelerator, payload["accelerator"], "install plan accelerator"
        ),
        profile_id=_require_plan_text(payload["profile_id"], "profile_id"),
        requested_component_ids=_parse_nullable_component_ids(
            payload["requested_component_ids"],
            "requested_component_ids",
            allow_empty=True,
        ),
        effective_component_ids=_parse_component_ids(
            payload["effective_component_ids"], "effective_component_ids"
        ),
        requested_download_source_ids=_parse_nullable_component_ids(
            payload["requested_download_source_ids"],
            "requested_download_source_ids",
            allow_empty=False,
        ),
        effective_download_source_ids=_parse_component_ids(
            payload["effective_download_source_ids"], "effective_download_source_ids"
        ),
        source=_parse_runtime_source_identity(payload["source"]),
        components=components,
        blockers=tuple(
            _parse_runtime_install_plan_blocker(item) for item in payload["blockers"]
        ),
        cost=cost,
    )


def parse_runtime_install_plan_response(
    payload: dict[str, Any],
) -> RuntimeInstallPlanResponse:
    if not isinstance(payload, dict):
        raise ContractError("install plan response must be a JSON object")
    _require_fields(
        payload,
        ("schema_version", "plan", "negotiated_capabilities"),
        "install plan response",
    )
    if payload["schema_version"] != SCHEMA_VERSION:
        raise ContractError("install plan response schema_version mismatch")
    negotiated = _parse_component_ids(
        payload["negotiated_capabilities"], "negotiated_capabilities"
    )
    return RuntimeInstallPlanResponse(
        plan=_parse_runtime_install_plan(payload["plan"]),
        negotiated_capabilities=negotiated,
    )


def parse_runtime_install_plan_request(
    payload: dict[str, Any],
) -> RuntimeInstallPlanRequest:
    """Strictly parse an install plan preview request (server/test facing)."""

    if not isinstance(payload, dict):
        raise ContractError("install plan request must be a JSON object")
    _reject_unknown_fields(
        payload,
        frozenset(
            {
                "accelerator",
                "install_component_ids",
                "download_source_ids",
                "required_capabilities",
            }
        ),
        "install plan request",
    )
    _require_fields(payload, ("required_capabilities",), "install plan request")
    required_capabilities = _parse_component_ids(
        payload["required_capabilities"], "required_capabilities"
    )
    if "runtime.install-plan.v1" not in required_capabilities:
        raise ContractError(
            "install plan request required_capabilities must contain "
            "runtime.install-plan.v1"
        )
    accelerator = None
    if "accelerator" in payload:
        accelerator = _require_enum(
            RuntimeAccelerator,
            payload["accelerator"],
            "install plan request accelerator",
        )
    install_component_ids = None
    if "install_component_ids" in payload:
        if payload["install_component_ids"] is None:
            raise ContractError("install_component_ids must be an array, not null")
        install_component_ids = _parse_component_ids(
            payload["install_component_ids"], "install_component_ids"
        )
    download_source_ids = None
    if "download_source_ids" in payload:
        if payload["download_source_ids"] is None:
            raise ContractError(
                "install plan request download_source_ids must be an array, not null"
            )
        download_source_ids = _parse_component_ids(
            payload["download_source_ids"], "download_source_ids"
        )
        if not download_source_ids:
            raise ContractError("download_source_ids must be non-empty when provided")
    return RuntimeInstallPlanRequest(
        required_capabilities=required_capabilities,
        accelerator=accelerator,
        install_component_ids=install_component_ids,
        download_source_ids=download_source_ids,
    )


@dataclass(frozen=True, slots=True)
class SchemaValidator:
    """Bundles the parse helpers so callers can inject a fake clock etc."""

    schema_version: int = SCHEMA_VERSION

    def snapshot(self, payload: dict[str, Any]) -> JobSnapshot:
        return parse_job_snapshot(payload)

    def error(self, payload: dict[str, Any]) -> ErrorPayload:
        return parse_error_payload(payload)


__all__ = [
    "ContractError",
    "JobStateTransitionError",
    "SchemaValidator",
    "assert_item_transition",
    "assert_job_transition",
    "is_terminal_item",
    "is_terminal_job",
    "parse_error_payload",
    "parse_job_command",
    "parse_job_ref",
    "parse_job_snapshot",
    "parse_job_update",
    "parse_pipeline_selection",
    "parse_pipeline_spec",
    "parse_runtime_install_plan_request",
    "parse_runtime_install_plan_response",
    "parse_residency_entry",
    "parse_runtime_maintenance_event",
    "parse_runtime_maintenance_receipt",
    "parse_runtime_maintenance_update",
    "parse_runtime_status",
    "parse_submit_request",
]

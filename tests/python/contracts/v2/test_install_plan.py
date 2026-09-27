"""Contract tests for the runtime.install-plan.v1 protocol extension.

Wire decisions under test:

* The install-plan preview is read-only: it installs nothing, downloads no
  dependencies or models, changes no active environment or persistent
  selection, and creates no maintenance operation.
* ``required_capabilities`` must contain ``runtime.install-plan.v1``; the
  request keeps the selection semantics of runtime.component-selection.v1
  (omission vs explicit empty) and runtime.download-sources.v1 (non-empty
  when present).
* ``RuntimeInstallPlan`` echoes the request through nullable requested
  fields, reports the effective closure union with machine reason codes,
  honest blockers with next actions, and deduplicated cost totals where null
  means unknown with reasons instead of zero placeholders.
* ``plan_id`` confirms a plan on ensure maintenance start or retry only, is
  mutually exclusive with selection overrides, requires an explicit
  operation_id, is invalid for cancel, and replays the existing receipt for
  the same operation id and plan.
* Stale/expired/unknown plans fail closed with RUNTIME_INSTALL_PLAN_STALE
  (409, retryable=false, next action: re-preview) and blocked plans with
  RUNTIME_INSTALL_PLAN_BLOCKED (409, retryable=false). The Runtime Host
  outer error code reuses invalid_request with the canonical code carried in
  error.canonical_code.
"""

from __future__ import annotations

import json
from pathlib import Path

import jsonschema
import pytest
from vibeocr.runtime_contracts import dtos, parser
from vibeocr.runtime_contracts.errors import ErrorCode, error_registry
from vibeocr.runtime_contracts.generated.capabilities import (
    ALL_CAPABILITIES,
    RUNTIME_INSTALL_PLAN_V1,
)
from vibeocr.runtime_contracts.generated.error_codes import ERROR_REGISTRY
from vibeocr.runtime_contracts.runtime_host import (
    RuntimeHostValidationError,
    parse_runtime_host_response,
)

ROOT = Path(__file__).resolve().parents[4]
V2 = ROOT / "contracts/runtime/python/vibeocr/runtime_contracts"

INSTALL_PLAN_ERRORS = {
    "RUNTIME_INSTALL_PLAN_STALE": ("conflict", 409, False),
    "RUNTIME_INSTALL_PLAN_BLOCKED": ("conflict", 409, False),
}


def _spec() -> dict:
    return json.loads((V2 / "openapi.yaml").read_text(encoding="utf-8"))


def _host_schema() -> dict:
    return json.loads((V2 / "runtime-host.schema.json").read_text(encoding="utf-8"))


def _golden() -> dict:
    return json.loads((V2 / "golden" / "golden.json").read_text(encoding="utf-8"))


def _schema_validator(name: str) -> jsonschema.Draft202012Validator:
    return jsonschema.Draft202012Validator(
        {
            "$ref": f"#/components/schemas/{name}",
            "components": _spec()["components"],
        }
    )


def _host_validator(name: str) -> jsonschema.Draft202012Validator:
    return jsonschema.Draft202012Validator(
        {
            "$ref": f"#/$defs/{name}",
            "$defs": _host_schema()["$defs"],
        }
    )


def _host_install_plan_request(**overrides: object) -> dict:
    request: dict = {
        "protocol_version": 2,
        "request_kind": "install_plan",
        "product_root": "C:/VibeOCR",
        "component_lock": "C:/VibeOCR/component-lock.json",
        "runtime_manifest": "C:/VibeOCR/backend/runtime-manifest.json",
        "required_capabilities": [RUNTIME_INSTALL_PLAN_V1],
    }
    request.update(overrides)
    return request


def test_capability_registry_declares_install_plan_with_dependencies() -> None:
    registry = json.loads((V2 / "capabilities.json").read_text(encoding="utf-8"))
    assert RUNTIME_INSTALL_PLAN_V1 in registry["capabilities"]
    assert RUNTIME_INSTALL_PLAN_V1 in ALL_CAPABILITIES
    definition = registry["definitions"][RUNTIME_INSTALL_PLAN_V1]
    assert definition["lifecycle"] == "active"
    assert definition["introduced_in"].startswith("2.")
    description = definition["description"]
    for dependency in (
        "runtime.maintenance.v2",
        "runtime.component-selection.v1",
        "runtime.download-sources.v1",
    ):
        assert dependency in description

    bootstrap = json.loads((V2 / "bootstrap.schema.json").read_text(encoding="utf-8"))
    known = bootstrap["properties"]["capabilities"]["items"]["x-vibeocr-known-values"]
    assert RUNTIME_INSTALL_PLAN_V1 in known

    runtime_api = json.loads(
        (V2 / "golden" / "runtime-api.json").read_text(encoding="utf-8")
    )
    assert RUNTIME_INSTALL_PLAN_V1 in runtime_api["health"]["capabilities"]


def test_install_plan_errors_fail_closed_at_409_without_retry() -> None:
    registry = json.loads((V2 / "errors.json").read_text(encoding="utf-8"))
    entries = {entry["code"]: entry for entry in registry["codes"]}
    for code, (category, http_status, retryable) in INSTALL_PLAN_ERRORS.items():
        entry = entries[code]
        assert entry["category"] == category
        assert entry["http_status"] == http_status
        assert entry["retryable"] is retryable
        # The Host outer error code stays invalid_request; the canonical code
        # rides error.canonical_code instead of extending the closed enum.
        assert entry["runtime_host_code"] == "invalid_request"
        assert ERROR_REGISTRY[ErrorCode(code)].retryable is False

    host_error_codes = _host_schema()["$defs"]["RuntimeHostErrorCode"]["enum"]
    assert host_error_codes == [
        "invalid_request",
        "invalid_binding",
        "install_failed",
        "lock_timeout",
        "io_error",
    ]

    error_schema = json.loads(
        (V2 / "schemas" / "errors.schema.json").read_text(encoding="utf-8")
    )
    known_values = error_schema["properties"]["code"]["x-vibeocr-known-values"]
    assert set(INSTALL_PLAN_ERRORS).issubset(known_values)

    stale = _golden()["error_install_plan_stale"]
    parser.parse_error_payload(stale)
    assert stale["code"] == "RUNTIME_INSTALL_PLAN_STALE"
    assert error_registry[ErrorCode.RUNTIME_INSTALL_PLAN_STALE].http_status == 409


def test_request_omission_and_explicit_empty_selection_stay_distinct() -> None:
    fixture = _golden()["install_selection"]

    explicit_empty = dtos.RuntimeInstallPlanRequest(
        required_capabilities=(RUNTIME_INSTALL_PLAN_V1,),
        install_component_ids=(),
    )
    payload = explicit_empty.to_payload()
    assert payload["install_component_ids"] == []
    _schema_validator("RuntimeInstallPlanRequest").validate(payload)

    omitted = dtos.RuntimeInstallPlanRequest(
        required_capabilities=(RUNTIME_INSTALL_PLAN_V1,)
    ).to_payload()
    assert "install_component_ids" not in omitted
    assert "download_source_ids" not in omitted
    assert "accelerator" not in omitted
    _schema_validator("RuntimeInstallPlanRequest").validate(omitted)

    selected = dtos.RuntimeInstallPlanRequest(
        required_capabilities=(RUNTIME_INSTALL_PLAN_V1,),
        accelerator=dtos.RuntimeAccelerator.CPU,
        install_component_ids=tuple(fixture),
        download_source_ids=("pypi-tuna",),
    )
    payload = selected.to_payload()
    assert payload["accelerator"] == "cpu"
    assert payload["download_source_ids"] == ["pypi-tuna"]
    _schema_validator("RuntimeInstallPlanRequest").validate(payload)
    assert parser.parse_runtime_install_plan_request(
        payload
    ) == parser.parse_runtime_install_plan_request(_golden()["install_plan_request"])

    with pytest.raises(ValueError, match="download_source_ids"):
        dtos.RuntimeInstallPlanRequest(
            required_capabilities=(RUNTIME_INSTALL_PLAN_V1,),
            download_source_ids=(),
        )


def test_request_schema_requires_the_install_plan_capability() -> None:
    validator = _schema_validator("RuntimeInstallPlanRequest")

    with pytest.raises(jsonschema.ValidationError):
        validator.validate({"accelerator": "cpu"})

    with pytest.raises(jsonschema.ValidationError):
        validator.validate(
            {
                "required_capabilities": ["runtime.maintenance.v2"],
                "accelerator": "cpu",
            }
        )

    with pytest.raises(jsonschema.ValidationError):
        validator.validate(
            {
                "required_capabilities": [RUNTIME_INSTALL_PLAN_V1],
                "accelerator": "tpu",
            }
        )

    with pytest.raises(jsonschema.ValidationError):
        validator.validate(
            {
                "required_capabilities": [RUNTIME_INSTALL_PLAN_V1],
                "download_source_ids": [],
            }
        )

    with pytest.raises(jsonschema.ValidationError):
        validator.validate(
            {
                "required_capabilities": [RUNTIME_INSTALL_PLAN_V1],
                "plan_id": "plan-1",
            }
        )

    with pytest.raises(parser.ContractError, match="unknown field"):
        parser.parse_runtime_install_plan_request(
            {
                "required_capabilities": [RUNTIME_INSTALL_PLAN_V1],
                "pip_args": ["--index-url", "https://mirror.example.invalid"],
            }
        )

    with pytest.raises(parser.ContractError, match="runtime.install-plan.v1"):
        parser.parse_runtime_install_plan_request(
            {"required_capabilities": ["runtime.maintenance.v2"]}
        )


def test_golden_plans_round_trip_through_parser_and_schema() -> None:
    golden = _golden()
    for key in (
        "install_plan",
        "install_plan_omitted_selection",
        "install_plan_gpu_empty_selection",
        "install_plan_blocked",
    ):
        fixture = golden[key]
        _schema_validator("RuntimeInstallPlan").validate(fixture)
        parsed = parser._parse_runtime_install_plan(fixture)
        assert parsed.plan_id == fixture["plan_id"]
        assert parsed.to_payload() == fixture

    response = {
        "schema_version": 2,
        "plan": golden["install_plan"],
        "negotiated_capabilities": [RUNTIME_INSTALL_PLAN_V1],
    }
    _schema_validator("RuntimeInstallPlanResponse").validate(response)
    parsed = parser.parse_runtime_install_plan_response(response)
    assert parsed.negotiated_capabilities == (RUNTIME_INSTALL_PLAN_V1,)
    assert parsed.plan == parser._parse_runtime_install_plan(golden["install_plan"])


def test_plan_nullable_request_echo_distinguishes_null_from_empty() -> None:
    golden = _golden()

    explicit = parser._parse_runtime_install_plan(
        golden["install_plan_gpu_empty_selection"]
    )
    assert explicit.requested_component_ids == ()
    assert explicit.requested_download_source_ids is None

    omitted = parser._parse_runtime_install_plan(
        golden["install_plan_omitted_selection"]
    )
    assert omitted.requested_component_ids is None
    assert omitted.requested_download_source_ids is None

    requested = parser._parse_runtime_install_plan(golden["install_plan"])
    assert requested.requested_component_ids == ("paddleocr-cpu", "mineru-cpu")
    assert requested.requested_download_source_ids == ("pypi-tuna",)

    with pytest.raises(ValueError, match="requested_download_source_ids"):
        dtos.RuntimeInstallPlan(
            plan_id="plan-x",
            expires_at="2026-09-19T12:34:56Z",
            accelerator=dtos.RuntimeAccelerator.CPU,
            profile_id="portable-cpu",
            effective_component_ids=(),
            effective_download_source_ids=(),
            source=dtos.RuntimeSourceIdentity(
                backend_version="2.8.2",
                backend_source_sha="0" * 40,
                runtime_manifest_sha256="0" * 64,
            ),
            cost=dtos.RuntimeInstallPlanCost(0, 0),
            requested_download_source_ids=(),
        )


def test_plan_component_actions_and_blockers_use_open_machine_codes() -> None:
    golden = _golden()
    blocked = parser._parse_runtime_install_plan(golden["install_plan_blocked"])

    actions = {
        component.component_id: component.action for component in blocked.components
    }
    assert actions == {
        "paddleocr-cpu": dtos.RuntimeInstallPlanAction.REPLACE,
        "mineru-cpu": dtos.RuntimeInstallPlanAction.INSTALL,
    }
    assert blocked.blockers == (
        dtos.RuntimeInstallPlanBlocker(
            code="insufficient_disk_space",
            next_action="free_space",
            component_id="mineru-cpu",
        ),
        dtos.RuntimeInstallPlanBlocker(
            code="download_source_unreachable", next_action="change_source"
        ),
    )

    removed = parser._parse_runtime_install_plan(
        golden["install_plan_omitted_selection"]
    )
    removal = [item for item in removed.components if item.component_id == "mineru-cpu"]
    assert removal[0].action is dtos.RuntimeInstallPlanAction.REMOVE
    assert "mineru-cpu" not in removed.effective_component_ids
    assert removal[0].reason_codes == ("not_in_selected_scope",)

    closure = [
        item
        for item in parser._parse_runtime_install_plan(
            golden["install_plan"]
        ).components
        if item.component_id == "shared-base-cpu"
    ]
    assert closure[0].dependency_state is (
        dtos.RuntimeInstallPlanDependencyState.SATISFIED
    )
    assert closure[0].reason_codes == ("dependency_closure",)


def test_cost_totals_are_deduplicated_and_unknown_means_null_with_reasons() -> None:
    known = dtos.RuntimeInstallPlanCost(
        download_bytes=157286400, additional_disk_bytes=524288000
    )
    assert known.to_payload() == {
        "download_bytes": 157286400,
        "additional_disk_bytes": 524288000,
        "unknown_reason_codes": [],
    }

    unknown = dtos.RuntimeInstallPlanCost(
        download_bytes=None,
        additional_disk_bytes=100,
        unknown_reason_codes=("model_registry_source_size_unknown",),
    )
    assert unknown.to_payload()["download_bytes"] is None

    with pytest.raises(ValueError, match="unknown_reason_codes"):
        dtos.RuntimeInstallPlanCost(download_bytes=None, additional_disk_bytes=100)

    with pytest.raises(parser.ContractError, match="unknown_reason_codes"):
        parser._parse_runtime_install_plan_cost(
            {
                "download_bytes": None,
                "additional_disk_bytes": 100,
                "unknown_reason_codes": [],
            }
        )

    with pytest.raises(parser.ContractError, match="non-negative"):
        parser._parse_runtime_install_plan_cost(
            {
                "download_bytes": -1,
                "additional_disk_bytes": 100,
                "unknown_reason_codes": [],
            }
        )


def test_maintenance_start_confirms_plan_only_for_ensure_with_operation_id() -> None:
    request = dtos.RuntimeMaintenanceRequest(
        operation=dtos.RuntimeMaintenanceOperation.ENSURE,
        operation_id="op-confirm-1",
        plan_id="plan-7f3a91c2e8d4",
        required_capabilities=(RUNTIME_INSTALL_PLAN_V1,),
    )
    payload = request.to_payload()
    assert payload["plan_id"] == "plan-7f3a91c2e8d4"
    _schema_validator("RuntimeMaintenanceRequest").validate(payload)

    legacy = dtos.RuntimeMaintenanceRequest(
        operation=dtos.RuntimeMaintenanceOperation.ENSURE
    ).to_payload()
    assert "plan_id" not in legacy

    with pytest.raises(ValueError, match="plan_id requires ensure"):
        dtos.RuntimeMaintenanceRequest(
            operation=dtos.RuntimeMaintenanceOperation.INSPECT,
            operation_id="op-1",
            plan_id="plan-1",
        )
    with pytest.raises(ValueError, match="operation_id"):
        dtos.RuntimeMaintenanceRequest(
            operation=dtos.RuntimeMaintenanceOperation.ENSURE, plan_id="plan-1"
        )
    for field in ("profile_id", "install_component_ids", "download_source_ids"):
        kwargs: dict = {"plan_id": "plan-1", "operation_id": "op-1"}
        if field == "profile_id":
            kwargs[field] = "portable-cpu"
        elif field == "install_component_ids":
            kwargs[field] = ("paddleocr-cpu",)
        else:
            kwargs[field] = ("pypi-tuna",)
        with pytest.raises(ValueError, match="mutually exclusive"):
            dtos.RuntimeMaintenanceRequest(
                operation=dtos.RuntimeMaintenanceOperation.ENSURE, **kwargs
            )
    with pytest.raises(ValueError, match="mutually exclusive"):
        dtos.RuntimeMaintenanceRequest(
            operation=dtos.RuntimeMaintenanceOperation.ENSURE,
            operation_id="op-1",
            plan_id="plan-1",
            component_ids=("paddleocr-cpu",),
        )

    with pytest.raises(jsonschema.ValidationError):
        _schema_validator("RuntimeMaintenanceRequest").validate(
            {
                "operation": "ensure",
                "operation_id": "op-1",
                "plan_id": "plan-1",
                "profile_id": "portable-cpu",
            }
        )
    with pytest.raises(jsonschema.ValidationError):
        _schema_validator("RuntimeMaintenanceRequest").validate(
            {"operation": "ensure", "plan_id": "plan-1"}
        )


def test_maintenance_retry_replaces_plan_while_cancel_rejects_it() -> None:
    command = dtos.RuntimeMaintenanceCommand(
        command_id="command-1",
        command=dtos.RuntimeMaintenanceCommandKind.RETRY,
        target_operation_id="op-confirm-1",
        new_operation_id="op-confirm-2",
        plan_id="plan-fresh-1",
        required_capabilities=(RUNTIME_INSTALL_PLAN_V1,),
    )
    payload = command.to_payload()
    assert payload["plan_id"] == "plan-fresh-1"
    assert payload["required_capabilities"] == [RUNTIME_INSTALL_PLAN_V1]
    _schema_validator("RuntimeMaintenanceCommandRequest").validate(payload)

    with pytest.raises(ValueError, match="plan_id requires retry"):
        dtos.RuntimeMaintenanceCommand(
            command_id="command-2",
            command=dtos.RuntimeMaintenanceCommandKind.CANCEL,
            target_operation_id="op-confirm-1",
            plan_id="plan-1",
        )
    with pytest.raises(ValueError, match="mutually exclusive"):
        dtos.RuntimeMaintenanceCommand(
            command_id="command-3",
            command=dtos.RuntimeMaintenanceCommandKind.RETRY,
            target_operation_id="op-1",
            new_operation_id="op-2",
            plan_id="plan-1",
            install_component_ids=("paddleocr-cpu",),
        )

    with pytest.raises(jsonschema.ValidationError):
        _schema_validator("RuntimeMaintenanceCommandRequest").validate(
            {
                "command_id": "command-4",
                "command": "cancel",
                "target_operation_id": "op-1",
                "plan_id": "plan-1",
            }
        )
    with pytest.raises(jsonschema.ValidationError):
        _schema_validator("RuntimeMaintenanceCommandRequest").validate(
            {
                "command_id": "command-5",
                "command": "retry",
                "target_operation_id": "op-1",
                "new_operation_id": "op-2",
                "plan_id": "plan-1",
                "download_source_ids": ["pypi-tuna"],
            }
        )


def test_maintenance_status_echoes_plan_id() -> None:
    status = dtos.RuntimeMaintenanceStatus(
        operation_id="op-confirm-1",
        sequence=1,
        operation=dtos.RuntimeMaintenanceOperation.ENSURE,
        operation_state=dtos.RuntimeOperationState.QUEUED,
        phase=dtos.RuntimeMaintenancePhase.VALIDATE_BINDING,
        profile_id="portable-cpu",
        updated_at="2026-09-19T12:00:00+00:00",
        plan_id="plan-7f3a91c2e8d4",
    )
    payload = status.to_payload()
    assert payload["plan_id"] == "plan-7f3a91c2e8d4"
    _schema_validator("RuntimeMaintenanceStatus").validate(payload)
    assert parser._parse_runtime_maintenance_status(payload).plan_id == (
        "plan-7f3a91c2e8d4"
    )

    legacy = dtos.RuntimeMaintenanceStatus(
        operation_id="op-1",
        sequence=1,
        operation=dtos.RuntimeMaintenanceOperation.ENSURE,
        operation_state=dtos.RuntimeOperationState.QUEUED,
        phase=dtos.RuntimeMaintenancePhase.VALIDATE_BINDING,
        profile_id="portable-cpu",
        updated_at="2026-09-19T12:00:00+00:00",
    ).to_payload()
    assert "plan_id" not in legacy
    assert parser._parse_runtime_maintenance_status(legacy).plan_id is None


def test_host_install_plan_request_binds_product_and_capability() -> None:
    validator = _host_validator("RuntimeInstallPlanRequest")

    validator.validate(_host_install_plan_request())
    validator.validate(
        _host_install_plan_request(
            layout_manifest="C:/VibeOCR/layout-manifest.json",
            product_id="vibeocr-desktop",
            accelerator="nvidia_cuda",
            install_component_ids=["mineru-cuda"],
            download_source_ids=["modelscope"],
        )
    )

    for bad in (
        # missing capability
        _host_install_plan_request(required_capabilities=["runtime.maintenance.v2"]),
        # empty download source selection is invalid
        _host_install_plan_request(download_source_ids=[]),
        # unknown field: no pip surface leaks into the protocol
        _host_install_plan_request(pip_args=["--index-url", "https://x.invalid"]),
        # legacy operation enum must not grow install_plan
        _host_install_plan_request(operation="ensure"),
    ):
        with pytest.raises(jsonschema.ValidationError):
            validator.validate(bad)

    assert _host_schema()["$defs"]["RuntimeHostOperation"]["enum"] == [
        "inspect",
        "ensure",
        "repair",
    ]


def test_host_ensure_confirms_plan_without_touching_selection_overrides() -> None:
    validator = _host_validator("RuntimeHostRequest")
    base = {
        "protocol_version": 2,
        "required_capabilities": [RUNTIME_INSTALL_PLAN_V1],
        "operation": "ensure",
        "product_root": "C:/VibeOCR",
        "component_lock": "C:/VibeOCR/component-lock.json",
        "runtime_manifest": "C:/VibeOCR/backend/runtime-manifest.json",
    }

    validator.validate({**base, "operation_id": "op-1", "plan_id": "plan-1"})
    with pytest.raises(jsonschema.ValidationError):
        validator.validate({**base, "plan_id": "plan-1"})
    with pytest.raises(jsonschema.ValidationError):
        validator.validate(
            {**base, "operation_id": "op-1", "plan_id": "plan-1", "accelerator": "cpu"}
        )
    with pytest.raises(jsonschema.ValidationError):
        validator.validate(
            {
                **base,
                "operation_id": "op-1",
                "plan_id": "plan-1",
                "install_component_ids": ["paddleocr-cpu"],
            }
        )
    with pytest.raises(jsonschema.ValidationError):
        validator.validate(
            {
                **base,
                "operation_id": "op-1",
                "plan_id": "plan-1",
                "component_ids": ["x"],
            }
        )
    with pytest.raises(jsonschema.ValidationError):
        validator.validate(
            {
                "protocol_version": 2,
                "required_capabilities": [RUNTIME_INSTALL_PLAN_V1],
                "operation": "inspect",
                "product_root": "C:/VibeOCR",
                "component_lock": "C:/VibeOCR/component-lock.json",
                "runtime_manifest": "C:/VibeOCR/backend/runtime-manifest.json",
                "operation_id": "op-1",
                "plan_id": "plan-1",
            }
        )

    host_command = _host_validator("RuntimeMaintenanceCommandRequest")
    command_base = {
        "protocol_version": 2,
        "required_capabilities": [RUNTIME_INSTALL_PLAN_V1],
        "request_kind": "command",
        "command": "retry",
        "command_id": "command-1",
        "target_operation_id": "op-1",
        "new_operation_id": "op-2",
        "product_root": "C:/VibeOCR",
        "component_lock": "C:/VibeOCR/component-lock.json",
        "runtime_manifest": "C:/VibeOCR/backend/runtime-manifest.json",
    }
    host_command.validate({**command_base, "plan_id": "plan-2"})
    with pytest.raises(jsonschema.ValidationError):
        host_command.validate(
            {**command_base, "plan_id": "plan-2", "download_source_ids": ["pypi-tuna"]}
        )
    with pytest.raises(jsonschema.ValidationError):
        host_command.validate(
            {
                "protocol_version": 2,
                "required_capabilities": [RUNTIME_INSTALL_PLAN_V1],
                "request_kind": "command",
                "command": "cancel",
                "command_id": "command-2",
                "target_operation_id": "op-1",
                "product_root": "C:/VibeOCR",
                "component_lock": "C:/VibeOCR/component-lock.json",
                "runtime_manifest": "C:/VibeOCR/backend/runtime-manifest.json",
                "plan_id": "plan-2",
            }
        )


def test_host_parser_accepts_install_plan_response_with_forward_compat() -> None:
    golden = _golden()
    response = {
        "protocol_version": 2,
        "response_kind": "install_plan",
        "plan": golden["install_plan"],
    }
    parsed = parse_runtime_host_response(json.dumps(response))
    assert parsed["plan"]["plan_id"] == "plan-7f3a91c2e8d4"

    future = dict(response)
    future["future_hint"] = {"tier": "later"}
    future["plan"] = dict(golden["install_plan"], future_field=True)
    parse_runtime_host_response(json.dumps(future))

    for bad in (
        {"protocol_version": 2, "response_kind": "install_plan"},
        {"protocol_version": 2, "response_kind": "ensure", "plan": {}},
        {
            "protocol_version": 2,
            "response_kind": "install_plan",
            "plan": dict(golden["install_plan"], accelerator="tpu"),
        },
        {
            "protocol_version": 2,
            "response_kind": "install_plan",
            "plan": dict(
                golden["install_plan"],
                components=[
                    dict(golden["install_plan"]["components"][0], action="upgrade")
                ],
            ),
        },
    ):
        with pytest.raises(RuntimeHostValidationError):
            parse_runtime_host_response(json.dumps(bad))


def test_host_failure_carries_canonical_install_plan_code() -> None:
    failure = {
        "protocol_version": 2,
        "ok": False,
        "operation": None,
        "error": {
            "code": "invalid_request",
            "message": "install plan stale",
            "retryable": False,
            "canonical_code": "RUNTIME_INSTALL_PLAN_STALE",
            "category": "conflict",
        },
    }
    parsed = parse_runtime_host_response(json.dumps(failure))
    assert parsed["error"]["canonical_code"] == "RUNTIME_INSTALL_PLAN_STALE"


@pytest.mark.parametrize("capabilities", [None, [], ["runtime.maintenance.v2"]])
@pytest.mark.parametrize("host", [False, True])
@pytest.mark.parametrize("retry", [False, True])
def test_plan_confirmation_requires_capability_on_every_wire_branch(
    capabilities, host, retry
) -> None:
    payload = (
        {
            "command": "retry",
            "command_id": "cmd",
            "target_operation_id": "old",
            "new_operation_id": "new",
            "plan_id": "plan",
        }
        if retry
        else {"operation": "ensure", "operation_id": "op", "plan_id": "plan"}
    )
    if host:
        payload.update(
            protocol_version=2,
            product_root="C:/Product",
            component_lock="C:/Product/lock",
            runtime_manifest="C:/Product/manifest",
        )
        if retry:
            payload["request_kind"] = "command"
    if capabilities is not None:
        payload["required_capabilities"] = capabilities
    name = (
        "RuntimeMaintenanceCommandRequest"
        if retry
        else ("RuntimeHostRequest" if host else "RuntimeMaintenanceRequest")
    )
    validator = _host_validator(name) if host else _schema_validator(name)
    with pytest.raises(jsonschema.ValidationError):
        validator.validate(payload)
    payload["required_capabilities"] = [RUNTIME_INSTALL_PLAN_V1]
    validator.validate(payload)
    # The same legacy operation still works without new fields or negotiation.
    del payload["plan_id"]
    del payload["required_capabilities"]
    validator.validate(payload)


@pytest.mark.parametrize("kind", ["preview", "ensure", "retry"])
def test_plan_dto_rejects_missing_capability_before_serialization(kind) -> None:
    with pytest.raises(ValueError, match="runtime.install-plan.v1"):
        if kind == "preview":
            dtos.RuntimeInstallPlanRequest(required_capabilities=())
        elif kind == "ensure":
            dtos.RuntimeMaintenanceRequest(
                operation=dtos.RuntimeMaintenanceOperation.ENSURE,
                operation_id="op",
                plan_id="plan",
            )
        else:
            dtos.RuntimeMaintenanceCommand(
                command_id="cmd",
                command=dtos.RuntimeMaintenanceCommandKind.RETRY,
                target_operation_id="old",
                new_operation_id="new",
                plan_id="plan",
            )


@pytest.mark.parametrize(
    "field", ["accelerator", "install_component_ids", "download_source_ids"]
)
def test_preview_request_rejects_explicit_null_without_changing_intent(field) -> None:
    payload = {"required_capabilities": [RUNTIME_INSTALL_PLAN_V1], field: None}
    with pytest.raises(jsonschema.ValidationError):
        _schema_validator("RuntimeInstallPlanRequest").validate(payload)
    with pytest.raises(parser.ContractError):
        parser.parse_runtime_install_plan_request(payload)


@pytest.mark.parametrize(
    "field",
    [
        "plan_id",
        "profile_id",
        "effective_component_ids",
        "effective_download_source_ids",
        "source",
        "blockers",
    ],
)
def test_invalid_plan_response_cannot_erase_confirmation_binding(field) -> None:
    plan = _golden()["install_plan"]
    plan[field] = None
    with pytest.raises(parser.ContractError):
        parser.parse_runtime_install_plan_response(
            {
                "schema_version": 2,
                "plan": plan,
                "negotiated_capabilities": [RUNTIME_INSTALL_PLAN_V1],
            }
        )


def test_large_plan_cost_and_future_optional_response_fields_round_trip() -> None:
    plan = _golden()["install_plan"]
    plan["cost"]["download_bytes"] = 3221225472
    plan["cost"]["additional_disk_bytes"] = 4294967296
    plan["future_optional"] = True
    response = parser.parse_runtime_install_plan_response(
        {
            "schema_version": 2,
            "plan": plan,
            "negotiated_capabilities": [RUNTIME_INSTALL_PLAN_V1],
            "future_optional": True,
        }
    )
    assert response.plan.cost.download_bytes == 3221225472
    request = dtos.RuntimeMaintenanceRequest(
        operation=dtos.RuntimeMaintenanceOperation.ENSURE,
        operation_id="confirmed",
        plan_id=response.plan.plan_id,
        required_capabilities=(RUNTIME_INSTALL_PLAN_V1,),
    )
    assert request.to_payload()["plan_id"] == plan["plan_id"]


@pytest.mark.parametrize("transport", ["http", "host"])
def test_plan_rejects_duplicate_component_ids_with_conflicting_actions(
    transport,
) -> None:
    plan = _golden()["install_plan"]
    plan["components"].append({**plan["components"][0], "action": "remove"})
    response = {
        "plan": plan,
        "negotiated_capabilities": [RUNTIME_INSTALL_PLAN_V1],
    }
    if transport == "http":
        response["schema_version"] = 2
        with pytest.raises(parser.ContractError, match="component_id.*unique"):
            parser.parse_runtime_install_plan_response(response)
    else:
        response.update(protocol_version=2, response_kind="install_plan")
        with pytest.raises(RuntimeHostValidationError, match="component_id.*unique"):
            parse_runtime_host_response(json.dumps(response))


@pytest.mark.parametrize(
    "key", ["progress_bytes_large", "progress_bytes_unknown", "progress_bytes_zero"]
)
def test_measured_byte_progress_keeps_large_zero_and_unknown_totals(key: str) -> None:
    payload = _golden()[key]
    _schema_validator("ProgressSnapshot").validate(payload)
    host = _host_schema()
    jsonschema.Draft202012Validator(
        {"$ref": "#/$defs/ProgressSnapshot", "$defs": host["$defs"]}
    ).validate(payload)
    progress = dtos.ProgressSnapshot(
        unit=dtos.ProgressUnit(payload["unit"]),
        current=payload["current"],
        total=payload.get("total"),
        estimated_remaining_seconds=payload.get("estimated_remaining_seconds"),
    )
    assert progress.to_payload() == payload

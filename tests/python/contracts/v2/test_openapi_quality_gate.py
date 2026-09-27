"""Offline OpenAPI lint and backward-compatibility gate tests."""

from __future__ import annotations

import copy
import json
import subprocess
import sys
from pathlib import Path

from scripts.check_openapi_quality import (
    detect_breaking_changes,
    lint_document,
)

ROOT = Path(__file__).resolve().parents[4]
FORMAL_OPENAPI = (
    ROOT / "contracts/runtime/python/vibeocr/runtime_contracts/openapi.yaml"
)
RELEASE_BASELINE = (
    ROOT
    / "contracts/runtime/python/vibeocr/runtime_contracts/baselines"
    / "openapi-2.0.0.yaml"
)


def _schema(properties: dict, required: list[str]) -> dict:
    return {
        "type": "object",
        "additionalProperties": False,
        "properties": properties,
        "required": required,
    }


def _document() -> dict:
    return {
        "openapi": "3.1.0",
        "info": {"title": "Compatibility fixture", "version": "1.0.0"},
        "paths": {
            "/v2/items": {
                "post": {
                    "operationId": "createItem",
                    "requestBody": {
                        "required": True,
                        "content": {
                            "application/json": {
                                "schema": {"$ref": "#/components/schemas/CreateItem"}
                            }
                        },
                    },
                    "responses": {
                        "200": {
                            "description": "created",
                            "content": {
                                "application/json": {
                                    "schema": {"$ref": "#/components/schemas/Item"}
                                }
                            },
                        },
                        "400": {"$ref": "#/components/responses/Error"},
                    },
                }
            }
        },
        "components": {
            "schemas": {
                "CreateItem": _schema(
                    {
                        "name": {"type": "string"},
                        "count": {"type": "integer"},
                    },
                    ["name"],
                ),
                "Item": _schema(
                    {
                        "id": {"type": "string"},
                        "name": {"type": "string"},
                    },
                    ["id", "name"],
                ),
                "Error": _schema({"message": {"type": "string"}}, ["message"]),
            },
            "responses": {
                "Error": {
                    "description": "error",
                    "content": {
                        "application/json": {
                            "schema": {"$ref": "#/components/schemas/Error"}
                        }
                    },
                }
            },
        },
    }


def test_formal_openapi_passes_dependency_free_lint() -> None:
    document = json.loads(FORMAL_OPENAPI.read_text(encoding="utf-8"))
    assert lint_document(document) == []


def test_cli_without_paths_lints_the_formal_protocol() -> None:
    result = subprocess.run(
        [sys.executable, "scripts/check_openapi_quality.py"],
        cwd=ROOT,
        capture_output=True,
        text=True,
        check=False,
    )

    assert result.returncode == 0, result.stdout + result.stderr
    assert "OpenAPI quality gate: OK" in result.stdout


def test_formal_openapi_is_compatible_with_immutable_release_baseline() -> None:
    result = subprocess.run(
        [
            sys.executable,
            "scripts/check_openapi_quality.py",
            "--baseline",
            str(RELEASE_BASELINE),
            "--current",
            str(FORMAL_OPENAPI),
        ],
        cwd=ROOT,
        capture_output=True,
        text=True,
        check=False,
    )

    assert result.returncode == 0, result.stdout + result.stderr
    assert "OpenAPI quality gate: OK" in result.stdout


def test_lint_rejects_broken_refs_duplicate_operation_ids_and_invalid_schema() -> None:
    document = _document()
    document["paths"]["/v2/other"] = copy.deepcopy(document["paths"]["/v2/items"])
    document["components"]["schemas"]["CreateItem"]["required"].append("missing")
    document["components"]["schemas"]["Item"]["properties"]["id"] = {
        "$ref": "#/components/schemas/Absent"
    }
    document["paths"]["/v2/items"]["post"]["responses"]["200"]["content"][
        "application/json"
    ]["schema"]["type"] = "invalid"

    errors = lint_document(document)

    assert any("duplicate 'createItem'" in error for error in errors)
    assert any("target does not exist" in error for error in errors)
    assert any("unknown properties missing" in error for error in errors)
    assert any("schema has an invalid type" in error for error in errors)


def test_breaking_gate_detects_removed_path_and_method() -> None:
    baseline = _document()
    without_method = copy.deepcopy(baseline)
    without_method["paths"]["/v2/items"] = {}
    without_path = copy.deepcopy(baseline)
    without_path["paths"] = {}

    assert "POST /v2/items: operation was removed" in detect_breaking_changes(
        baseline, without_method
    )
    assert "POST /v2/items: operation was removed" in detect_breaking_changes(
        baseline, without_path
    )


def test_breaking_gate_detects_new_required_request_field_and_type_change() -> None:
    baseline = _document()
    current = copy.deepcopy(baseline)
    request_schema = current["components"]["schemas"]["CreateItem"]
    request_schema["required"].append("count")
    request_schema["properties"]["name"]["type"] = "integer"

    issues = detect_breaking_changes(baseline, current)

    assert any(
        "request application/json.count: request field became required" in issue
        for issue in issues
    )
    assert any(
        "request application/json.name: type changed" in issue for issue in issues
    )


def test_breaking_gate_detects_existing_type_becoming_unconstrained() -> None:
    baseline = _document()
    current = copy.deepcopy(baseline)
    del current["components"]["schemas"]["Item"]["properties"]["name"]["type"]

    issues = detect_breaking_changes(baseline, current)

    assert any(
        "response 200 application/json.name: type changed" in issue for issue in issues
    )


def test_breaking_gate_detects_request_field_becoming_optional() -> None:
    baseline = _document()
    baseline["components"]["schemas"]["CreateItem"]["required"].append("count")
    current = copy.deepcopy(baseline)
    current["components"]["schemas"]["CreateItem"]["required"].remove("count")

    issues = detect_breaking_changes(baseline, current)

    assert any(
        "request application/json.count: request field is no longer required" in issue
        for issue in issues
    )


def test_breaking_gate_detects_new_required_operation_parameter() -> None:
    baseline = _document()
    current = copy.deepcopy(baseline)
    current["paths"]["/v2/items"]["post"]["parameters"] = [
        {
            "name": "tenant",
            "in": "header",
            "required": True,
            "schema": {"type": "string"},
        }
    ]

    issues = detect_breaking_changes(baseline, current)

    assert "POST /v2/items: required header parameter 'tenant' was added" in issues


def test_breaking_gate_detects_tightened_request_constraints() -> None:
    baseline = _document()
    current = copy.deepcopy(baseline)
    baseline_name = baseline["components"]["schemas"]["CreateItem"]["properties"][
        "name"
    ]
    current_name = current["components"]["schemas"]["CreateItem"]["properties"]["name"]
    baseline_name["minLength"] = 1
    current_name["minLength"] = 3

    issues = detect_breaking_changes(baseline, current)

    assert any(
        "request application/json.name: minLength changed from 1 to 3" in issue
        for issue in issues
    )


def test_breaking_gate_detects_removed_request_union_branch() -> None:
    baseline = _document()
    current = copy.deepcopy(baseline)
    baseline["components"]["schemas"]["CreateItem"]["properties"]["name"] = {
        "oneOf": [{"type": "string"}, {"type": "integer"}]
    }
    current["components"]["schemas"]["CreateItem"]["properties"]["name"] = {
        "oneOf": [{"type": "string"}]
    }

    issues = detect_breaking_changes(baseline, current)

    assert any(
        "request application/json.name: oneOf branch was removed" in issue
        for issue in issues
    )


def test_breaking_gate_detects_added_request_all_of_constraint() -> None:
    baseline = _document()
    current = copy.deepcopy(baseline)
    current["components"]["schemas"]["CreateItem"]["properties"]["name"] = {
        "allOf": [{"type": "string"}, {"minLength": 3}]
    }

    issues = detect_breaking_changes(baseline, current)

    assert any(
        "request application/json.name: allOf constraint was added" in issue
        for issue in issues
    )


def test_breaking_gate_detects_stricter_operation_security() -> None:
    baseline = _document()
    current = copy.deepcopy(baseline)
    current["paths"]["/v2/items"]["post"]["security"] = [{"SessionToken": []}]

    issues = detect_breaking_changes(baseline, current)

    assert "POST /v2/items: security requirements became stricter" in issues


def test_breaking_gate_accepts_explicit_anonymous_security_alternative() -> None:
    baseline = _document()
    current = copy.deepcopy(baseline)
    current["paths"]["/v2/items"]["post"]["security"] = [
        {},
        {"SessionToken": []},
    ]

    assert detect_breaking_changes(baseline, current) == []


def test_breaking_gate_detects_removed_response_status_and_field() -> None:
    baseline = _document()
    current = copy.deepcopy(baseline)
    del current["paths"]["/v2/items"]["post"]["responses"]["400"]
    del current["components"]["schemas"]["Item"]["properties"]["name"]
    current["components"]["schemas"]["Item"]["required"].remove("name")

    issues = detect_breaking_changes(baseline, current)

    assert "POST /v2/items: response status 400 was removed" in issues
    assert any(
        "response 200 application/json.name: response field was removed" in issue
        for issue in issues
    )


def test_breaking_gate_detects_response_field_becoming_required() -> None:
    baseline = _document()
    baseline["components"]["schemas"]["Item"]["properties"]["description"] = {
        "type": "string"
    }
    current = copy.deepcopy(baseline)
    current["components"]["schemas"]["Item"]["required"].append("description")

    issues = detect_breaking_changes(baseline, current)

    assert any(
        "response 200 application/json.description: response field became required"
        in issue
        for issue in issues
    )


def test_breaking_gate_detects_enum_changes_in_both_deployment_directions() -> None:
    baseline = _document()
    baseline["components"]["schemas"]["CreateItem"]["properties"]["count"] = {
        "type": "integer",
        "enum": [1, 2],
    }
    baseline["components"]["schemas"]["Item"]["properties"]["name"]["enum"] = [
        "old",
        "shared",
    ]
    current = copy.deepcopy(baseline)
    current["components"]["schemas"]["CreateItem"]["properties"]["count"][
        "enum"
    ].append(3)
    current["components"]["schemas"]["Item"]["properties"]["name"]["enum"] = ["shared"]

    issues = detect_breaking_changes(baseline, current)

    assert any("request enum added values [3]" in issue for issue in issues)
    assert any("response enum removed values ['old']" in issue for issue in issues)


def test_breaking_gate_detects_type_constraint_presence_change() -> None:
    baseline = _document()
    del baseline["components"]["schemas"]["Item"]["properties"]["name"]["type"]
    current = copy.deepcopy(baseline)
    current["components"]["schemas"]["Item"]["properties"]["name"]["type"] = "string"

    issues = detect_breaking_changes(baseline, current)

    assert any(
        "response 200 application/json.name: type changed from ['any'] to ['string']"
        in issue
        for issue in issues
    )


def test_breaking_gate_detects_enum_constraint_presence_changes() -> None:
    baseline = _document()
    baseline["components"]["schemas"]["Item"]["properties"]["name"]["enum"] = [
        "old",
        "shared",
    ]
    current = copy.deepcopy(baseline)
    current["components"]["schemas"]["CreateItem"]["properties"]["name"]["enum"] = [
        "shared",
    ]
    del current["components"]["schemas"]["Item"]["properties"]["name"]["enum"]

    issues = detect_breaking_changes(baseline, current)

    assert any("request enum constraint was added" in issue for issue in issues)
    assert any("response enum constraint was removed" in issue for issue in issues)


def test_breaking_gate_accepts_enum_opened_as_known_values() -> None:
    baseline = _document()
    baseline_name = baseline["components"]["schemas"]["Item"]["properties"]["name"]
    baseline_name.pop("type")
    baseline_name["enum"] = ["old", "shared"]
    current = copy.deepcopy(baseline)
    current_name = current["components"]["schemas"]["Item"]["properties"]["name"]
    current_name.pop("enum")
    current_name["type"] = "string"
    current_name["x-vibeocr-known-values"] = ["old", "shared", "new"]

    assert detect_breaking_changes(baseline, current) == []


def test_breaking_gate_rejects_request_enum_opened_as_known_values() -> None:
    baseline = _document()
    baseline_name = baseline["components"]["schemas"]["CreateItem"]["properties"][
        "name"
    ]
    baseline_name.pop("type")
    baseline_name["enum"] = ["old", "shared"]
    current = copy.deepcopy(baseline)
    current_name = current["components"]["schemas"]["CreateItem"]["properties"]["name"]
    current_name.pop("enum")
    current_name["type"] = "string"
    current_name["x-vibeocr-known-values"] = ["old", "shared", "new"]

    issues = detect_breaking_changes(baseline, current)

    assert any("request enum constraint was removed" in issue for issue in issues)


def test_cli_returns_nonzero_for_breaking_baseline_current_pair(tmp_path: Path) -> None:
    baseline = tmp_path / "baseline.json"
    current = tmp_path / "current.json"
    baseline.write_text(json.dumps(_document()), encoding="utf-8")
    changed = copy.deepcopy(_document())
    changed["components"]["schemas"]["Item"]["properties"]["id"]["type"] = "integer"
    current.write_text(json.dumps(changed), encoding="utf-8")

    result = subprocess.run(
        [
            sys.executable,
            "scripts/check_openapi_quality.py",
            "--baseline",
            str(baseline),
            "--current",
            str(current),
        ],
        cwd=ROOT,
        capture_output=True,
        text=True,
        check=False,
    )

    assert result.returncode == 1
    assert "BREAKING" in result.stderr
    assert "type changed" in result.stderr


def test_cli_accepts_optional_response_field_addition(tmp_path: Path) -> None:
    baseline = tmp_path / "baseline.json"
    current = tmp_path / "current.json"
    baseline.write_text(json.dumps(_document()), encoding="utf-8")
    changed = copy.deepcopy(_document())
    changed["components"]["schemas"]["Item"]["properties"]["description"] = {
        "type": "string"
    }
    current.write_text(json.dumps(changed), encoding="utf-8")

    result = subprocess.run(
        [
            sys.executable,
            "scripts/check_openapi_quality.py",
            "--baseline",
            str(baseline),
            "--current",
            str(current),
        ],
        cwd=ROOT,
        capture_output=True,
        text=True,
        check=False,
    )

    assert result.returncode == 0, result.stderr
    assert "OpenAPI quality gate: OK" in result.stdout

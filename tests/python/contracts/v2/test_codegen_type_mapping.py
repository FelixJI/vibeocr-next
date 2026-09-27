from __future__ import annotations

from collections.abc import Callable

import pytest

from scripts.generate_runtime_protocol import (
    _csharp_wire_types,
    _python_wire_types,
)


def _document(field_schema: dict[str, object]) -> dict[str, object]:
    return {
        "components": {
            "schemas": {
                "Projection": {
                    "type": "object",
                    "properties": {"value": field_schema},
                    "required": ["value"],
                }
            }
        }
    }


@pytest.mark.parametrize("renderer", (_python_wire_types, _csharp_wire_types))
def test_codegen_rejects_unknown_schema_types(
    renderer: Callable[[dict], str],
) -> None:
    with pytest.raises(ValueError, match="unsupported OpenAPI schema type: decimal"):
        renderer(_document({"type": "decimal"}))


def test_python_and_csharp_codegen_project_the_same_schema_semantics() -> None:
    document = {
        "components": {
            "schemas": {
                "Referenced": {
                    "type": "object",
                    "properties": {"id": {"type": "string"}},
                    "required": ["id"],
                },
                "Projection": {
                    "type": "object",
                    "properties": {
                        "text": {"type": "string"},
                        "count": {"type": "integer"},
                        "ratio": {"type": "number"},
                        "flag": {"type": "boolean"},
                        "metadata": {"type": "object"},
                        "items": {
                            "type": "array",
                            "items": {"$ref": "#/components/schemas/Referenced"},
                        },
                        "nullable_enum": {
                            "type": ["string", "null"],
                            "enum": ["known", None],
                        },
                        "mode": {"type": "string", "enum": ["fast", "safe"]},
                        "unconstrained": {},
                    },
                    "required": [
                        "text",
                        "count",
                        "ratio",
                        "flag",
                        "metadata",
                        "items",
                        "nullable_enum",
                        "mode",
                    ],
                },
            }
        }
    }

    python = _python_wire_types(document)
    csharp = _csharp_wire_types(document)

    for field in (
        "text: Required[str]",
        "count: Required[int]",
        "ratio: Required[float]",
        "flag: Required[bool]",
        "metadata: Required[dict[str, Any]]",
        "items: Required[list[Referenced]]",
        "nullable_enum: Required[str | None]",
        "mode: Required[Literal['fast', 'safe']]",
        "unconstrained: NotRequired[Any]",
    ):
        assert field in python

    for field in (
        "public required string Text { get; init; }",
        "public required int Count { get; init; }",
        "public required double Ratio { get; init; }",
        "public required bool Flag { get; init; }",
        "public required IReadOnlyDictionary<string, JsonElement> Metadata { get; init; }",
        "public required IReadOnlyList<Referenced> Items { get; init; }",
        "public required string? NullableEnum { get; init; }",
        "public required string Mode { get; init; }",
        "public JsonElement? Unconstrained { get; init; }",
    ):
        assert field in csharp


def test_codegen_projects_open_scalar_aliases_without_a_phantom_csharp_type() -> None:
    document = {
        "components": {
            "schemas": {
                "OpenKind": {
                    "type": "string",
                    "x-vibeocr-known-values": ["known"],
                },
                "Projection": {
                    "type": "object",
                    "properties": {
                        "kind": {"$ref": "#/components/schemas/OpenKind"},
                    },
                    "required": ["kind"],
                },
            }
        }
    }

    python = _python_wire_types(document)
    csharp = _csharp_wire_types(document)

    assert "OpenKind = str" in python
    assert "kind: Required[OpenKind]" in python
    assert "public required string Kind { get; init; }" in csharp
    assert "OpenKind" not in csharp

"""Validated save failure detail carried through the existing error channel."""

import os


def validate_save_outcome(
    value: object, expected_path: object
) -> dict[str, object] | None:
    if (
        not isinstance(value, dict)
        or not isinstance(expected_path, str)
        or not expected_path
    ):
        return None
    required = {"reason_code", "committed", "session_usable", "path"}
    if not required <= value.keys() or value.keys() - required - {"recovery_path"}:
        return None
    if value["reason_code"] != "pdf_save_outcome":
        return None
    if (
        type(value["committed"]) is not bool
        or type(value["session_usable"]) is not bool
    ):
        return None
    target = value["path"]
    if not isinstance(target, str) or not os.path.isabs(target):
        return None
    if os.path.normcase(os.path.abspath(target)) != os.path.normcase(
        os.path.abspath(expected_path)
    ):
        return None
    if "recovery_path" in value:
        recovery = value["recovery_path"]
        if not isinstance(recovery, str) or not os.path.isabs(recovery):
            return None
    return dict(value)

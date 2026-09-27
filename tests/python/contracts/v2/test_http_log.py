from __future__ import annotations

import logging
from typing import cast

import pytest
from vibeocr.runtime_contracts.utils.http_log import (
    format_http_transaction,
    guess_request_size,
    guess_response_size,
    log_http_response,
    status_summary,
)


class _ExplodingLogger:
    def debug(self, *_args: object) -> None:
        raise RuntimeError("logging backend unavailable")

    warning = debug
    error = debug


def test_http_logging_is_best_effort() -> None:
    log_http_response(
        cast(logging.Logger, _ExplodingLogger()),
        "GET",
        "http://127.0.0.1:61335/health?token=secret",
        503,
    )


def test_http_transaction_matches_the_redacted_log_contract() -> None:
    message = format_http_transaction(
        "post",
        "http://127.0.0.1:61335/session/model?token=secret&page=3",
        404,
        reason="Not Found",
        elapsed_ms=12.34,
        request_bytes=1024,
        response_bytes=2048,
        stream=True,
    )

    assert message == (
        "POST /session/model?token=<redacted>&page=<redacted> | "
        "404 Not Found（接口/资源不存在） | Not Found | "
        "stream=True | 耗时=12.3ms | 请求体=1.0 KB | 返回体=2.0 KB"
    )
    assert "secret" not in message
    assert "参数校验失败" in status_summary(422)


@pytest.mark.parametrize(
    ("status_code", "method"),
    ((200, "debug"), (404, "warning"), (503, "error")),
)
def test_http_log_level_follows_the_status_class(
    status_code: int,
    method: str,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    logger = logging.getLogger(f"test.http.{status_code}")
    calls: list[str] = []
    monkeypatch.setattr(logger, method, lambda *_args: calls.append(method))

    log_http_response(logger, "GET", "/status", status_code)

    assert calls == [method]


def test_http_size_helpers_count_utf8_and_use_content_length_fallback() -> None:
    assert guess_request_size("中文") == 6
    assert guess_response_size({}, "中文") == 6
    assert guess_response_size({"Content-Length": "12"}, None) == 12
    assert guess_response_size({"Content-Length": "invalid"}, None) is None
    assert "599 Unknown" in status_summary(599)

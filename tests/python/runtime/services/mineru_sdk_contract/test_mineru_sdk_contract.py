"""官方 MinerU 4.1.3 SDK Tasks 协议合同（受控服务器 + 真实 SDK）。

运行环境：``scripts/prepare_mineru_sdk_tests.py`` 装配的
``build/mineru-sdk-contract-venv``（``VIBEOCR_MINERU_SDK_CONTRACTS=1``）。
覆盖 AC：唯一端点、创建清单、批量隔离、取消时序、错误分类脱敏、素材
sidecar、env 干扰与空 key 语义。
"""

from __future__ import annotations

import hashlib
import logging
import socket

import pytest
from vibeocr.runtime.recognition.mineru_api import (
    MineruApiClient,
    MineruApiError,
    MineruCancelled,
    MineruDocument,
)
from vibeocr.runtime.recognition.mineru_config import MineruConfigError
from vibeocr.runtime.recognition.mineru_result import project_document
from vibeocr.runtime_contracts import ErrorCode, MineruConfig, MineruOcrMode, MineruTier

PDF_BYTES = b"%PDF-1.4 controlled server contract"
PRODUCT_LOGGER = "vibeocr.runtime.recognition.mineru_api"


def _config(
    tier: MineruTier = MineruTier.STANDARD,
    ocr_mode: MineruOcrMode = MineruOcrMode.OCR,
    page_range: str = "all",
) -> MineruConfig:
    return MineruConfig(tier=tier, ocr_mode=ocr_mode, page_range=page_range)


def _sha256_hex(payload: bytes) -> str:
    return hashlib.sha256(payload).hexdigest()


def _free_port() -> int:
    # 绑定后立即释放：该端口上的连接必然被拒绝，供 transport 错误用例。
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def _cancelled_after_upload(server, name: str):  # type: ignore[no-untyped-def]
    def cancelled() -> bool:
        event = server.uploaded_events.get(name)
        return event is not None and event.is_set()

    return cancelled


# ---------------------------------------------------------------------------
# AC1：唯一 Tasks 端点，旧 4.0 uploads/jobs/files 协议零调用
# ---------------------------------------------------------------------------


class TestSoleTasksProtocol:
    def test_success_touches_only_tasks_upload_and_result(self, tasks_server):
        result = MineruApiClient(tasks_server.base_url, timeout=60).parse(
            [("contract.pdf", PDF_BYTES)], _config()
        )
        assert isinstance(result["contract.pdf"], MineruDocument)
        methods = {method for method, _ in tasks_server.calls}
        assert methods <= {"POST", "GET", "PUT"}
        for _, path in tasks_server.calls:
            assert path.startswith(("/v1/tasks", "/upload/", "/result/")), path
        assert not any(
            fragment in path
            for _, path in tasks_server.calls
            for fragment in ("/v1/uploads", "/v1/parse/jobs", "/v1/files/")
        )
        # 完整生命周期：创建 -> 详情 -> 上传 -> 终态详情 -> 结果下载。
        assert ("POST", "/v1/tasks") in tasks_server.calls
        assert ("PUT", "/upload/file-1") in tasks_server.calls
        assert ("GET", "/result/task-1") in tasks_server.calls

    def test_cancel_scenario_still_avoids_legacy_endpoints(self, tasks_server):
        tasks_server.hold_running = {"cancel.pdf"}
        with pytest.raises(MineruCancelled):
            MineruApiClient(tasks_server.base_url, timeout=60).parse(
                [("cancel.pdf", PDF_BYTES)],
                _config(),
                cancelled=_cancelled_after_upload(tasks_server, "cancel.pdf"),
            )
        # 取消端点是 POST /v1/tasks/{id}/cancel，不存在旧 DELETE 语义。
        cancel_posts = tasks_server.calls_matching("/cancel")
        assert cancel_posts == [("POST", "/v1/tasks/task-1/cancel")]
        assert all(method != "DELETE" for method, _ in tasks_server.calls)


# ---------------------------------------------------------------------------
# AC2：创建清单（tier/ocr_mode/output_formats/file_name/bytes/sha256sum/page_range）
# ---------------------------------------------------------------------------


class TestCreateManifest:
    @pytest.mark.parametrize(
        ("tier", "ocr_mode", "page_range"),
        [
            (MineruTier.ADVANCED, MineruOcrMode.OCR, "all"),
            (MineruTier.FLASH, MineruOcrMode.TXT, "all"),
            (MineruTier.STANDARD, MineruOcrMode.AUTO, "1-2"),
        ],
        ids=["advanced-ocr", "flash-txt", "standard-pages"],
    )
    def test_manifest_carries_full_configuration(
        self, tasks_server, tier, ocr_mode, page_range
    ):
        MineruApiClient(tasks_server.base_url, timeout=60).parse(
            [("manifest.pdf", PDF_BYTES)], _config(tier, ocr_mode, page_range)
        )
        request = tasks_server.create_requests[0]
        item = request["files"][0]
        assert item["file_name"] == "manifest.pdf"
        assert item["bytes"] == len(PDF_BYTES)
        assert item["sha256sum"] == _sha256_hex(PDF_BYTES)
        assert item["page_range"] == page_range
        assert request["tier"] == tier.value
        assert request["ocr_mode"] == ocr_mode.value
        assert request["output_formats"] == ["default"]

    def test_upload_carries_the_exact_input_bytes(self, tasks_server):
        MineruApiClient(tasks_server.base_url, timeout=60).parse(
            [("bytes.pdf", PDF_BYTES)], _config()
        )
        assert tasks_server.uploaded["file-1"] == PDF_BYTES


# ---------------------------------------------------------------------------
# AC3：批量语义（唯一名、顺序与身份、单文件失败隔离）
# ---------------------------------------------------------------------------


class TestBatchSemantics:
    def test_duplicate_names_are_rejected_before_any_request(self, tasks_server):
        with pytest.raises(ValueError, match="unique"):
            MineruApiClient(tasks_server.base_url, timeout=60).parse(
                [("dup.pdf", b"a"), ("dup.pdf", b"b")], _config()
            )
        assert tasks_server.calls == []

    def test_empty_input_returns_empty_without_requests(self, tasks_server):
        assert (
            MineruApiClient(tasks_server.base_url, timeout=60).parse([], _config())
            == {}
        )
        assert tasks_server.calls == []

    def test_multi_file_batch_keeps_identity_and_order(self, tasks_server):
        inputs = [("first.pdf", b"%PDF-first"), ("second.pdf", b"%PDF-second")]
        result = MineruApiClient(tasks_server.base_url, timeout=60).parse(
            inputs, _config()
        )
        assert list(result) == ["first.pdf", "second.pdf"]
        assert all(isinstance(document, MineruDocument) for document in result.values())
        assert [
            request["files"][0]["file_name"] for request in tasks_server.create_requests
        ] == ["first.pdf", "second.pdf"]
        assert tasks_server.uploaded["file-1"] == b"%PDF-first"
        assert tasks_server.uploaded["file-2"] == b"%PDF-second"

    def test_upstream_file_failure_is_isolated_per_file(self, tasks_server):
        tasks_server.fail_files["bad.pdf"] = (
            "parse_quota_exceeded",
            "SECRET-UPSTREAM tenant body",
        )
        result = MineruApiClient(tasks_server.base_url, timeout=60).parse(
            [("good.pdf", b"%PDF-good"), ("bad.pdf", b"%PDF-bad")], _config()
        )
        assert isinstance(result["good.pdf"], MineruDocument)
        failure = result["bad.pdf"]
        assert isinstance(failure, MineruApiError)
        assert str(failure) == "mineru_sdk_parse_quota_exceeded"
        # 上游响应正文绝不进入产品错误消息。
        assert "SECRET-UPSTREAM" not in str(failure)

    def test_page_range_on_non_pdf_is_a_single_file_error(self, tasks_server):
        # 非 PDF 输入配置页范围：按文件隔离为配置错误，同批 PDF 不受影响。
        result = MineruApiClient(tasks_server.base_url, timeout=60).parse(
            [("photo.png", b"\x89PNG fake"), ("doc.pdf", PDF_BYTES)],
            _config(page_range="1-2"),
        )
        assert isinstance(result["photo.png"], MineruApiError)
        assert str(result["photo.png"]) == "Page ranges require a PDF input"
        assert isinstance(result["doc.pdf"], MineruDocument)
        # 只有 PDF 被提交到服务器。
        assert [
            request["files"][0]["file_name"] for request in tasks_server.create_requests
        ] == ["doc.pdf"]


# ---------------------------------------------------------------------------
# AC4：取消时序与预算
# ---------------------------------------------------------------------------


class TestCancelAndDeadline:
    def test_cancel_before_submission_makes_zero_requests(self, tasks_server):
        with pytest.raises(MineruCancelled):
            MineruApiClient(tasks_server.base_url, timeout=60).parse(
                [("early.pdf", PDF_BYTES)], _config(), cancelled=lambda: True
            )
        assert tasks_server.calls == []

    def test_cancel_during_poll_sends_exactly_one_cancel_post(self, tasks_server):
        tasks_server.hold_running = {"cancel.pdf"}
        with pytest.raises(MineruCancelled):
            MineruApiClient(tasks_server.base_url, timeout=60).parse(
                [("cancel.pdf", PDF_BYTES)],
                _config(),
                cancelled=_cancelled_after_upload(tasks_server, "cancel.pdf"),
            )
        assert len(tasks_server.cancel_calls) == 1
        # 取消后不再继续轮询或重试创建。
        assert len(tasks_server.create_requests) == 1

    @pytest.mark.parametrize("cancel_status", [404, 409], ids=["gone", "terminal"])
    def test_idempotent_cancel_rejections_stay_confirmed(
        self, tasks_server, cancel_status, caplog
    ):
        tasks_server.hold_running = {"cancel.pdf"}
        tasks_server.cancel_status = cancel_status
        with caplog.at_level(logging.WARNING, logger="mineru.api_client"):
            with pytest.raises(MineruCancelled):
                MineruApiClient(tasks_server.base_url, timeout=60).parse(
                    [("cancel.pdf", PDF_BYTES)],
                    _config(),
                    cancelled=_cancelled_after_upload(tasks_server, "cancel.pdf"),
                )
        # 404/409+task_already_terminal 是幂等成功，不产生不可确认告警。
        assert not [
            record
            for record in caplog.records
            if "could not be confirmed" in record.getMessage()
        ]
        assert len(tasks_server.cancel_calls) == 1

    def test_unconfirmable_cancel_still_cancels_and_forwards_warning(
        self, tasks_server, caplog
    ):
        tasks_server.hold_running = {"cancel.pdf"}
        tasks_server.cancel_status = 500
        with caplog.at_level(logging.WARNING, logger=PRODUCT_LOGGER):
            with pytest.raises(MineruCancelled):
                MineruApiClient(tasks_server.base_url, timeout=60).parse(
                    [("cancel.pdf", PDF_BYTES)],
                    _config(),
                    cancelled=_cancelled_after_upload(tasks_server, "cancel.pdf"),
                )
        # facade 挂载的 forwarder 把 SDK 的取消收尾告警转到产品 logger。
        assert [
            record
            for record in caplog.records
            if "could not be confirmed" in record.getMessage()
        ]

    def test_zero_budget_times_out_before_any_side_effect(self, tasks_server):
        with pytest.raises(TimeoutError, match="deadline exceeded"):
            MineruApiClient(tasks_server.base_url, timeout=0).parse(
                [("zero.pdf", PDF_BYTES)], _config()
            )
        assert tasks_server.calls == []

    def test_expired_budget_stops_submitting_remaining_files(self, tasks_server):
        tasks_server.hold_running = {"slow.pdf"}
        with pytest.raises(TimeoutError, match="deadline exceeded"):
            MineruApiClient(tasks_server.base_url, timeout=2.5).parse(
                [("slow.pdf", PDF_BYTES), ("never.pdf", b"%PDF-never")], _config()
            )
        # 第二个文件从未提交。
        assert [
            request["files"][0]["file_name"] for request in tasks_server.create_requests
        ] == ["slow.pdf"]


# ---------------------------------------------------------------------------
# AC5：错误分类与脱敏
# ---------------------------------------------------------------------------


class TestErrorClassification:
    def test_structured_invalid_api_key_fails_whole_batch_as_config_error(
        self, tasks_server
    ):
        tasks_server.create_status = 401
        tasks_server.create_body = {
            "error": {"code": "invalid_api_key", "message": "SECRET key material"}
        }
        with pytest.raises(MineruConfigError) as caught:
            MineruApiClient(tasks_server.base_url, timeout=60).parse(
                [("a.pdf", PDF_BYTES), ("b.pdf", b"%PDF-b")], _config()
            )
        assert caught.value.code == ErrorCode.VALIDATION_ERROR
        assert caught.value.reason == "mineru_api_authentication_failed"
        assert "SECRET" not in str(caught.value)
        # 配置错误整批失败：第一个文件后不再创建任务。
        assert len(tasks_server.create_requests) == 1

    @pytest.mark.parametrize("status", [404, 405])
    def test_unstructured_endpoint_error_maps_to_incompatible(
        self, tasks_server, status
    ):
        tasks_server.create_status = status
        tasks_server.create_body = {"detail": "Not Found"}
        with pytest.raises(MineruConfigError) as caught:
            MineruApiClient(tasks_server.base_url, timeout=60).parse(
                [("a.pdf", PDF_BYTES)], _config()
            )
        assert caught.value.code == ErrorCode.VALIDATION_ERROR
        assert caught.value.reason == "mineru_api_endpoint_incompatible"
        # 旧 4.0 服务的非结构化响应体不进入错误消息。
        assert "Not Found" not in str(caught.value)

    def test_other_http_error_maps_to_sanitized_sdk_code(self, tasks_server):
        # 404/405 之外的 http_error 不是端点配置错误：按单文件脱敏失败。
        tasks_server.create_status = 500
        tasks_server.create_body = {
            "error": {"code": "http_error", "message": "HTTP 500: SECRET internals"}
        }
        result = MineruApiClient(tasks_server.base_url, timeout=60).parse(
            [("a.pdf", PDF_BYTES)], _config()
        )
        failure = result["a.pdf"]
        assert isinstance(failure, MineruApiError)
        assert str(failure) == "mineru_sdk_http_error"
        assert "SECRET" not in str(failure)

    def test_connection_refused_maps_to_transport_error(self):
        # 连接拒绝是传输层故障：按单文件失败，不重试其他文件也不上抛整批。
        base = f"http://127.0.0.1:{_free_port()}"
        result = MineruApiClient(base, timeout=30).parse(
            [("a.pdf", PDF_BYTES)], _config()
        )
        failure = result["a.pdf"]
        assert isinstance(failure, MineruApiError)
        assert str(failure) == "mineru_transport_error_task_submission"


# ---------------------------------------------------------------------------
# AC6：middle_json 素材 sidecar
# ---------------------------------------------------------------------------


class TestResultAssets:
    def test_image_sidecar_flows_into_document_and_projection(
        self, tasks_server, tiny_jpeg, image_middle
    ):
        tasks_server.middle = image_middle("images/flash-image.jpg")
        tasks_server.sidecars = {"images/flash-image.jpg": tiny_jpeg}
        result = MineruApiClient(tasks_server.base_url, timeout=60).parse(
            [("assets.pdf", PDF_BYTES)], _config()
        )
        document = result["assets.pdf"]
        assert isinstance(document, MineruDocument)
        # SDK save() 把素材物化为 sidecar 文件。
        assert document.images
        projected = project_document(document)
        image_block = next(
            block for block in projected.content_list if block["type"] == "image"
        )
        assert image_block["img_path"] in projected.images
        assert projected.images[image_block["img_path"]] == tiny_jpeg

    def test_missing_sidecar_fails_that_file_only(self, tasks_server, image_middle):
        tasks_server.middle = image_middle("images/flash-image.jpg")
        tasks_server.omit_sidecars = True
        result = MineruApiClient(tasks_server.base_url, timeout=60).parse(
            [("missing.pdf", PDF_BYTES)], _config()
        )
        failure = result["missing.pdf"]
        assert isinstance(failure, MineruApiError)
        assert str(failure) == "mineru_sdk_invalid_image_sidecar"


# ---------------------------------------------------------------------------
# AC7/AC8：env 干扰与显式空 key
# ---------------------------------------------------------------------------


class TestEnvironmentInterference:
    def test_explicit_url_and_key_win_over_environment(self, tasks_server, monkeypatch):
        monkeypatch.setenv("MINERU_API_URL", "http://127.0.0.1:9/wrong")
        monkeypatch.setenv("MINERU_API_KEY", "env-leaked-secret")
        result = MineruApiClient(
            tasks_server.base_url, timeout=60, api_key="explicit-contract-key"
        ).parse([("env.pdf", PDF_BYTES)], _config())
        assert isinstance(result["env.pdf"], MineruDocument)
        # 请求只到达显式服务器，鉴权只用显式 key。
        assert tasks_server.create_requests
        assert all(
            header == "Bearer explicit-contract-key"
            for header in tasks_server.authorization_headers
        )

    def test_default_construction_sends_no_authorization_despite_env_key(
        self, tasks_server, monkeypatch
    ):
        monkeypatch.setenv("MINERU_API_KEY", "env-leaked-secret")
        result = MineruApiClient(tasks_server.base_url, timeout=60).parse(
            [("noauth.pdf", PDF_BYTES)], _config()
        )
        assert isinstance(result["noauth.pdf"], MineruDocument)
        # api_key=None 归一化为显式空串：SDK 不回落进程环境。
        assert tasks_server.create_requests
        assert all(header is None for header in tasks_server.authorization_headers)

"""远程 MinerU 连接的 Supervisor 链回归（Backend #113）。

覆盖：远程不 spawn/不 kill 本地进程、连接配置沿 settings 链应用、
活动任务门禁、store 失败回滚、无 mineru 包的远程解析。
"""

from __future__ import annotations

from typing import TYPE_CHECKING, Any

import httpx
import pytest
from vibeocr.runtime.environments.settings_store import RuntimeSettingsStoreError
from vibeocr.runtime.host import composition
from vibeocr.runtime.jobs.budgets import InputItem
from vibeocr.runtime.jobs.module import SupervisorModule, SupervisorOptions
from vibeocr.runtime.jobs.registry import JobRegistry
from vibeocr.runtime.jobs.staging import StagedInput
from vibeocr.runtime.recognition import mineru_readiness as readiness
from vibeocr.runtime.recognition.mineru_adapter import MinerUProcessAdapter
from vibeocr.runtime.recognition.mineru_api import (
    MineruApiClient,
    MineruCancelled,
    MineruDocument,
)
from vibeocr.runtime.recognition.mineru_config import MineruConfigError
from vibeocr.runtime.recognition.mineru_executor import MinerUExecutor
from vibeocr.runtime.recognition.ocr_engines import (
    EngineAvailability,
    EngineDescriptor,
    OcrEngineRegistry,
    OcrEngineResolver,
)
from vibeocr.runtime_contracts import (
    ItemState,
    JobKind,
    JobPriority,
    JobState,
    MineruConfig,
    MineruTier,
    PipelineSelection,
    SettingsSnapshot,
)
from vibeocr.runtime_contracts.dtos import JobItem, OcrEngine

if TYPE_CHECKING:
    from pathlib import Path

    from pytest import MonkeyPatch

REMOTE_EXTRA = {
    "mineru_connection": {
        "mode": "remote",
        "api_url": "https://mineru.example.com/api",
        "api_key": "secret-token-1",
    }
}


@pytest.fixture()
def isolated_connection(monkeypatch: MonkeyPatch):
    monkeypatch.setattr(readiness, "_connection", readiness.MineruConnection())
    monkeypatch.setattr(readiness, "_observations", {})
    yield readiness
    monkeypatch.setattr(readiness, "_connection", readiness.MineruConnection())


class _RecordingLifecycle:
    """记录 start/stop，任何调用都会被断言捕获。"""

    def __init__(self) -> None:
        self.started = 0
        self.stopped = 0

    def start(self) -> None:
        self.started += 1

    def stop(self) -> None:
        self.stopped += 1


class _RecordingClient:
    def __init__(self) -> None:
        self.calls: list[list[str]] = []

    def file_parse(self, files, backend=None, **kwargs):  # type: ignore[no-untyped-def]
        self.calls.append([name for name, _ in files])
        return {name: {"markdown": "remote"} for name, _ in files}


def _adapter(
    lifecycle: _RecordingLifecycle | None = None, client: Any = None
) -> MinerUProcessAdapter:
    return MinerUProcessAdapter(
        client_factory=lambda: client or _RecordingClient(),
        lifecycle=lifecycle,
    )


def _raw_item(item_id: str, data: bytes = b"%PDF-a") -> InputItem:
    return InputItem(
        item_id=item_id,
        display_name=f"{item_id}.pdf",
        data=data,
        encoded_bytes=len(data),
        decoded_pixels=0,
        estimated_pages=1,
    )


def _snapshot(extra: dict[str, Any] | None = None, ttl: int = 300) -> SettingsSnapshot:
    return SettingsSnapshot(default_ttl_seconds=ttl, extra=extra or {})


# ---------------------------------------------------------------------------
# 远程不 spawn / 不 kill 本地
# ---------------------------------------------------------------------------


class TestRemoteNeverSpawnsOrKillsLocal:
    def test_remote_ensure_started_skips_lifecycle(self, isolated_connection):
        lifecycle = _RecordingLifecycle()
        adapter = _adapter(lifecycle)
        isolated_connection.configure_connection(
            isolated_connection.connection_from_extra(REMOTE_EXTRA)
        )
        adapter.ensure_started()
        adapter.recognize_many([_raw_item("it-0")])
        adapter.release_idle()
        adapter.close()
        assert lifecycle.started == 0
        assert lifecycle.stopped == 0

    def test_remote_residency_claims_no_local_process(self, isolated_connection):
        adapter = _adapter()
        isolated_connection.configure_connection(
            isolated_connection.connection_from_extra(REMOTE_EXTRA)
        )
        adapter.ensure_started()
        from vibeocr.runtime_contracts import ResidencyKind

        entry = adapter.residency_status().entries[0]
        assert entry.pipeline == "MinerU"
        assert entry.kind is ResidencyKind.EVICTED
        assert entry.remaining_ttl_seconds is None
        adapter.close()

    def test_remote_residency_hides_leftover_local_process(self, isolated_connection):
        """local 会话切到 remote 后，遗留本地进程不得显示为当前驻留。

        旧实现（无遮蔽）会把遗留的 _process_started 报成 SOFT_TTL 驻留；
        本地子进程仍由既有 release/TTL/close 路径回收，不影响远程服务器。
        """
        from vibeocr.runtime_contracts import ResidencyKind

        lifecycle = _RecordingLifecycle()
        adapter = _adapter(lifecycle)
        adapter.ensure_started()  # local 会话启动（fake lifecycle，无真实进程）
        assert adapter.residency_status().entries[0].kind is ResidencyKind.SOFT_TTL

        isolated_connection.configure_connection(
            isolated_connection.connection_from_extra(REMOTE_EXTRA)
        )
        entry = adapter.residency_status().entries[0]
        assert entry.kind is ResidencyKind.EVICTED
        assert entry.remaining_ttl_seconds is None

        # 遗留本地子进程仍由本地清理路径回收（本地清理，不碰远程）。
        adapter.release_idle()
        assert lifecycle.stopped == 1
        adapter.close()
        assert lifecycle.stopped == 1  # close 对已停进程不再重复 stop

    def test_remote_parse_runs_without_local_package(self, isolated_connection):
        """Base Runtime（无 mineru 包）远程解析：adapter 链全程不触发本地启动。"""
        client = _RecordingClient()
        lifecycle = _RecordingLifecycle()
        adapter = _adapter(lifecycle, client)
        isolated_connection.configure_connection(
            isolated_connection.connection_from_extra(REMOTE_EXTRA)
        )
        results = adapter.recognize_many([_raw_item("it-0"), _raw_item("it-1")])
        assert [item["markdown"] for item in results] == ["remote", "remote"]
        assert len(client.calls) == 1 and len(client.calls[0]) == 2
        assert lifecycle.started == 0
        adapter.close()


# ---------------------------------------------------------------------------
# settings 链：连接应用、活动任务门禁、回滚
# ---------------------------------------------------------------------------


class _FailingStore:
    def load(self, default: SettingsSnapshot) -> SettingsSnapshot:
        return default

    def replace(self, snapshot: SettingsSnapshot) -> None:
        raise RuntimeSettingsStoreError("replace failed")


def _module(tmp_path: Path, executor: Any, store: Any = None) -> SupervisorModule:
    return SupervisorModule(
        options=SupervisorOptions(instance_id="mineru-remote-test"),
        stager_root=tmp_path / "staging",
        executor=executor,
        settings_store=store,
    )


def _executor(lifecycle: _RecordingLifecycle | None = None) -> MinerUExecutor:
    lifecycle = lifecycle or _RecordingLifecycle()
    return MinerUExecutor(adapter_factory=lambda: _adapter(lifecycle))


class TestSettingsChain:
    def test_lazy_executor_applies_connection_before_adapter_exists(
        self, isolated_connection, tmp_path
    ):
        executor = _executor()
        module = _module(tmp_path, executor)
        assert isolated_connection.current_connection().mode == "local"
        module.update_settings(_snapshot(REMOTE_EXTRA))
        assert isolated_connection.current_connection().mode == "remote"
        assert module.settings().extra["mineru_connection"]["mode"] == "remote"
        module.shutdown_now()

    def test_invalid_connection_fails_update_without_persisting(
        self, isolated_connection, tmp_path
    ):
        executor = _executor()
        module = _module(tmp_path, executor)
        previous = module.settings()
        invalid = {
            "mineru_connection": {
                "mode": "remote",
                "api_url": "ftp://mineru.example.com",
            }
        }
        with pytest.raises(MineruConfigError):
            module.update_settings(_snapshot(invalid))
        assert module.settings() is previous
        assert isolated_connection.current_connection().mode == "local"
        module.shutdown_now()

    def test_store_failure_rolls_connection_back(self, isolated_connection, tmp_path):
        executor = _executor()
        module = _module(tmp_path, executor, _FailingStore())
        with pytest.raises(RuntimeSettingsStoreError):
            module.update_settings(_snapshot(REMOTE_EXTRA))
        assert isolated_connection.current_connection().mode == "local"
        assert module.settings().extra == {}
        module.shutdown_now()

    def test_store_failure_rolls_back_after_adapter_materialized(
        self, isolated_connection, tmp_path
    ):
        executor = _executor()
        module = _module(tmp_path, executor, _FailingStore())
        materialized = executor.adapter  # force adapter creation
        with pytest.raises(RuntimeSettingsStoreError):
            module.update_settings(_snapshot(REMOTE_EXTRA))
        assert isolated_connection.current_connection().mode == "local"
        assert materialized._settings is module.settings()
        module.shutdown_now()

    def test_active_tasks_block_connection_change(self, isolated_connection, tmp_path):
        executor = _executor()
        module = _module(tmp_path, executor)
        adapter = executor.adapter
        adapter._active_leases = 1  # 模拟在途 MinerU 解析
        try:
            with pytest.raises(MineruConfigError) as caught:
                module.update_settings(_snapshot(REMOTE_EXTRA))
            assert (
                caught.value.reason
                == "mineru_connection_change_blocked_by_active_tasks"
            )
            # 拒绝时既不落盘也不生效。
            assert module.settings().extra == {}
            assert isolated_connection.current_connection().mode == "local"
        finally:
            adapter._active_leases = 0
        # 任务结束后同一变更可以生效。
        module.update_settings(_snapshot(REMOTE_EXTRA))
        assert isolated_connection.current_connection().mode == "remote"
        module.shutdown_now()

    def test_active_tasks_allow_same_connection_settings_change(
        self, isolated_connection, tmp_path
    ):
        executor = _executor()
        module = _module(tmp_path, executor)
        module.update_settings(_snapshot(REMOTE_EXTRA))
        adapter = executor.adapter
        adapter._active_leases = 1
        try:
            # 连接不变，仅 TTL 调整：允许。
            module.update_settings(_snapshot(REMOTE_EXTRA, ttl=600))
            assert module.settings().default_ttl_seconds == 600
            assert isolated_connection.current_connection().mode == "remote"
        finally:
            adapter._active_leases = 0
        module.shutdown_now()


# ---------------------------------------------------------------------------
# mode 目录动态门禁
# ---------------------------------------------------------------------------


class _FakeEngine:
    def __init__(self, engine_id: OcrEngine) -> None:
        self.engine_id = engine_id

    def descriptor(self) -> EngineDescriptor:
        return EngineDescriptor(
            engine_id=self.engine_id,
            availability=EngineAvailability.READY,
            included_in_base=True,
        )


def _registry(use_mineru: bool):
    resolver = OcrEngineResolver(
        OcrEngineRegistry([_FakeEngine(engine) for engine in OcrEngine])
    )
    return composition._build_recognition_mode_registry(
        engine_resolver=resolver,
        use_paddle=False,
        use_mineru=use_mineru,
    )


class TestModeCatalogGating:
    def test_remote_mode_ready_without_local_package(
        self, isolated_connection, monkeypatch: MonkeyPatch
    ):
        monkeypatch.setattr(composition, "_mineru_available", lambda: False)
        isolated_connection.configure_connection(
            isolated_connection.connection_from_extra(REMOTE_EXTRA)
        )
        modes = {
            mode["id"]: mode for mode in _registry(True).catalog_payload()["modes"]
        }
        assert modes["mineru_document"]["availability"] == "ready"
        assert modes["mineru_document"]["required_component"] is None

    def test_local_mode_without_package_stays_blocked(
        self, isolated_connection, monkeypatch: MonkeyPatch
    ):
        monkeypatch.setattr(composition, "_mineru_available", lambda: False)
        isolated_connection.configure_connection(
            isolated_connection.connection_from_extra(REMOTE_EXTRA)
        )
        registry = _registry(True)
        # 切回 local：目录读取时动态降回准备态。
        isolated_connection.configure_connection(readiness.MineruConnection())
        modes = {mode["id"]: mode for mode in registry.catalog_payload()["modes"]}
        assert modes["mineru_document"]["availability"] == "preparation_required"
        assert modes["mineru_document"]["required_component"] == "mineru-cpu"

    def test_explicit_optout_stays_blocked_even_remote(self, isolated_connection):
        isolated_connection.configure_connection(
            isolated_connection.connection_from_extra(REMOTE_EXTRA)
        )
        modes = {
            mode["id"]: mode for mode in _registry(False).catalog_payload()["modes"]
        }
        assert modes["mineru_document"]["availability"] == "preparation_required"


# ---------------------------------------------------------------------------
# settings wire：PUT /v2/settings 接受/拒绝远程连接配置
# ---------------------------------------------------------------------------


class TestSettingsWire:
    async def test_put_settings_applies_remote_and_rejects_crlf_key(
        self, isolated_connection, tmp_path
    ):
        import httpx
        from vibeocr.runtime.host.app import create_app
        from vibeocr.runtime.host.bootstrap import generate_session_token

        executor = _executor()
        module = _module(tmp_path, executor)
        token = generate_session_token()
        transport = httpx.ASGITransport(app=create_app(module, token))
        body = {
            "schema_version": 2,
            "residency": {"default_ttl_seconds": 300, "pipelines": []},
            "extra": REMOTE_EXTRA,
        }
        bad_body = {
            "schema_version": 2,
            "residency": {"default_ttl_seconds": 300, "pipelines": []},
            "extra": {
                "mineru_connection": {
                    "mode": "remote",
                    "api_url": "https://mineru.example.com/api",
                    "api_key": "a\r\nb",
                }
            },
        }
        headers = {"Authorization": f"Bearer {token}"}
        async with httpx.AsyncClient(
            transport=transport, base_url="http://127.0.0.1", headers=headers
        ) as http:
            accepted = await http.put("/v2/settings", json=body)
            assert accepted.status_code == 200, accepted.text
            assert isolated_connection.current_connection().mode == "remote"
            rejected = await http.put("/v2/settings", json=bad_body)
            assert rejected.status_code == 400
            assert rejected.json()["code"] == "VALIDATION_ERROR"
            # 拒绝后连接保持原值。
            assert isolated_connection.current_connection().mode == "remote"
        module.shutdown_now()


# ---------------------------------------------------------------------------
# 远程配置错误：MineruConfigError 必须一次性按 item 失败，不重试
# ---------------------------------------------------------------------------


class TestRemoteConfigErrorsFailOncePerItem:
    """真实 MinerUApiClient + MockTransport 经 Executor 的确定性失败。

    回归：远程上传 404/401 抛 MineruConfigError(VALIDATION_ERROR, reason)，
    旧实现落入通用恢复路径按 UNKNOWN 无意义重试；现在必须只发一次上传
    POST，outcome 的 error_code=VALIDATION_ERROR 且 message=reason。
    """

    @pytest.mark.parametrize(
        ("status", "reason"),
        [
            (404, "mineru_api_endpoint_incompatible"),
            (401, "mineru_api_authentication_failed"),
        ],
    )
    def test_upload_rejection_fails_items_once_without_retry(
        self, isolated_connection, tmp_path, status, reason
    ):
        posts: list[str] = []

        def handler(request: httpx.Request) -> httpx.Response:
            if request.method == "POST":
                posts.append(request.url.path)
            return httpx.Response(status)

        class _RemoteApi:
            """对齐 MinerUService 远程路径：真实 client.parse。"""

            def file_parse(
                self,
                files: list[tuple[str, bytes]],
                *,
                options: Any = None,
                backend: str | None = None,
                cancelled: Any = lambda: False,
            ) -> Any:
                config = (
                    options
                    if isinstance(options, MineruConfig)
                    else MineruConfig(tier=MineruTier.FLASH)
                )
                return MineruApiClient(
                    "https://mineru.example.com/api",
                    transport=httpx.MockTransport(handler),
                ).parse(list(files), config, cancelled=cancelled)

        isolated_connection.configure_connection(
            isolated_connection.connection_from_extra(REMOTE_EXTRA)
        )
        executor = MinerUExecutor(
            adapter_factory=lambda: MinerUProcessAdapter(
                client_factory=lambda: _RemoteApi()
            )
        )
        reg = JobRegistry(instance_id="mineru-remote-retry")
        record = reg.create(
            kind=JobKind.MINERU_PARSE,
            priority=JobPriority.INTERACTIVE,
            items=[
                JobItem(item_id="it-0", display_name="doc.pdf", state=ItemState.QUEUED)
            ],
            progress_total=1,
        )
        record.pipeline = PipelineSelection(
            "MinerU", mineru=MineruConfig(tier=MineruTier.FLASH)
        )
        record.transition(JobState.QUEUED)
        pdf = tmp_path / "doc.pdf"
        pdf.write_bytes(b"%PDF-1.4 remote upload")
        staged = [
            StagedInput(
                item_id="it-0",
                display_name="doc.pdf",
                path=pdf,
                size_bytes=len(b"%PDF-1.4 remote upload"),
                content_type="application/pdf",
            )
        ]

        executor.execute(record, staged)

        snap = record.snapshot()
        assert posts == ["/api/v1/uploads"]
        assert snap.state is JobState.FAILED
        assert snap.items[0].state is ItemState.FAILED
        assert record.item_errors["it-0"] == "VALIDATION_ERROR"
        assert snap.items[0].error == reason


# ---------------------------------------------------------------------------
# 投影契约失败：真实 file_parse → project_document 链，一次按 item 失败
# ---------------------------------------------------------------------------


def _text_document(text: str, *, extra_middle_block: bool = False) -> MineruDocument:
    """最小真实形态的 4.x 结果；extra_middle_block 制造真契约违规。"""
    middle_blocks = [
        {
            "type": "text",
            "index": 0,
            "content": [{"type": "text", "content": text}],
        }
    ]
    if extra_middle_block:
        middle_blocks.append(
            {
                "type": "text",
                "index": 1,
                "content": [{"type": "text", "content": "orphan"}],
            }
        )
    return MineruDocument(
        markdown="",
        structured_content={
            "pages": [
                {
                    "page_idx": 0,
                    "blocks": [
                        {
                            "type": "text",
                            "bbox": [0.1, 0.1, 0.9, 0.2],
                            "content": text,
                        }
                    ],
                }
            ]
        },
        middle_json={
            "schema": "docvortex.middle",
            "schema_version": "2.0",
            "pages": [{"page_idx": 0, "blocks": middle_blocks}],
        },
        archive=b"",
    )


class TestProjectionFailureIsolatedOncePerItem:
    """自部署 4.0.7 结果投影失败的完整 supervisor 链回归（用户真实日志）。

    旧链路：file_parse 抛 MineruApiError → 通用恢复按 UNKNOWN 重试
    （重发完整解析，真实耗时 75 秒）→ 再次失败 → 整批 item 失败。
    修复后：投影失败按文件隔离为 mineru_error，只发一次解析，
    同批其他文件正常成功。
    """

    def test_mismatched_projection_fails_item_once_and_keeps_good_file(
        self, isolated_connection, tmp_path, monkeypatch
    ):
        parse_calls: list[list[str]] = []

        class _FakeApiClient:
            def parse(self, files, config, *, cancelled=lambda: False):  # type: ignore[no-untyped-def]
                parse_calls.append([name for name, _ in files])
                # 与真实 transport 一致：结果按上传名（唯一 stem）返回。
                return {
                    name: (
                        _text_document("good remote text")
                        if "it-good" in name
                        else _text_document("bad", extra_middle_block=True)
                    )
                    for name, _ in files
                }

        monkeypatch.setattr(
            "vibeocr.runtime.recognition.mineru_service.MineruApiClient",
            lambda url, **kwargs: _FakeApiClient(),
        )
        isolated_connection.configure_connection(
            isolated_connection.connection_from_extra(REMOTE_EXTRA)
        )
        from vibeocr.runtime.recognition.mineru_service import MinerUService

        service = MinerUService.__new__(MinerUService)
        executor = MinerUExecutor(
            adapter_factory=lambda: MinerUProcessAdapter(client_factory=lambda: service)
        )
        # 先经 settings 链应用远程连接：adapter 惰性创建时会用 executor
        # 快照重新 configure_connection，未设置会被默认 local 覆盖。
        executor.configure_settings(_snapshot(REMOTE_EXTRA))
        reg = JobRegistry(instance_id="mineru-projection-retry")
        record = reg.create(
            kind=JobKind.MINERU_PARSE,
            priority=JobPriority.INTERACTIVE,
            items=[
                JobItem(
                    item_id="it-good", display_name="good.pdf", state=ItemState.QUEUED
                ),
                JobItem(
                    item_id="it-bad", display_name="bad.pdf", state=ItemState.QUEUED
                ),
            ],
            progress_total=2,
        )
        record.pipeline = PipelineSelection(
            "MinerU", mineru=MineruConfig(tier=MineruTier.FLASH)
        )
        record.transition(JobState.QUEUED)
        staged = []
        for item_id in ("it-good", "it-bad"):
            path = tmp_path / f"{item_id}.pdf"
            path.write_bytes(b"%PDF-1.4 remote parse")
            staged.append(
                StagedInput(
                    item_id=item_id,
                    display_name=f"{item_id}.pdf",
                    path=path,
                    size_bytes=len(b"%PDF-1.4 remote parse"),
                    content_type="application/pdf",
                )
            )

        executor.execute(record, staged)

        snap = record.snapshot()
        # 每个文件一次解析（compute batch=1，上传名会被重写为唯一 stem），
        # 坏文件不重试：旧链路会对 bad 文件重发一次完整解析（用户实测 75 秒）。
        assert len(parse_calls) == 2
        assert [len(call) for call in parse_calls] == [1, 1]
        assert "it-good" in parse_calls[0][0]
        assert "it-bad" in parse_calls[1][0]
        assert snap.state is JobState.COMPLETED_WITH_ERRORS
        states = {item.item_id: item.state for item in snap.items}
        assert states["it-good"] is ItemState.SUCCEEDED
        assert states["it-bad"] is ItemState.FAILED
        assert record.item_errors["it-bad"] == "BACKEND_UNAVAILABLE"
        failed = next(item for item in snap.items if item.item_id == "it-bad")
        assert "block identity mismatch" in failed.error
        assert record.results["it-good"]["raw_text"] == "good remote text"


class TestUserCancelDuringParseMarksItemsCancelled:
    """解析中用户取消：items 收敛为 CANCELLED，不按错误失败、不重试。

    旧链路：MineruCancelled 落入通用 except Exception → items FAILED
    （error_code=CANCELLED）+ adapter logger.exception 完整错误栈。
    """

    def test_cancelled_parse_marks_items_cancelled_without_error_stack(
        self, isolated_connection, tmp_path, caplog
    ):
        import logging

        class _CancellingClient:
            def __init__(self) -> None:
                self.record: Any = None

            def file_parse(
                self,
                files: list[tuple[str, bytes]],
                *,
                options: Any = None,
                backend: str | None = None,
                cancelled: Any = lambda: False,
            ) -> Any:
                # 模拟用户在解析中按下取消：record 标记取消后，
                # mineru_api.check_cancelled 抛 MineruCancelled。
                assert self.record is not None
                self.record.cancel_requested_at = "2026-10-07T19:52:50+08:00"
                raise MineruCancelled("MinerU job canceled by caller")

        client = _CancellingClient()
        executor = MinerUExecutor(
            adapter_factory=lambda: MinerUProcessAdapter(client_factory=lambda: client)
        )
        reg = JobRegistry(instance_id="mineru-cancel-state")
        record = reg.create(
            kind=JobKind.MINERU_PARSE,
            priority=JobPriority.INTERACTIVE,
            items=[
                JobItem(item_id="it-0", display_name="doc.pdf", state=ItemState.QUEUED)
            ],
            progress_total=1,
        )
        client.record = record
        record.pipeline = PipelineSelection(
            "MinerU", mineru=MineruConfig(tier=MineruTier.FLASH)
        )
        record.transition(JobState.QUEUED)
        pdf = tmp_path / "doc.pdf"
        pdf.write_bytes(b"%PDF-1.4 cancel me")
        staged = [
            StagedInput(
                item_id="it-0",
                display_name="doc.pdf",
                path=pdf,
                size_bytes=len(b"%PDF-1.4 cancel me"),
                content_type="application/pdf",
            )
        ]

        with caplog.at_level(
            logging.DEBUG, logger="vibeocr.runtime.recognition.mineru_adapter"
        ):
            executor.execute(record, staged)

        snap = record.snapshot()
        assert snap.state is JobState.CANCELLED
        # items 是 CANCELLED，不是带 error_code 的 FAILED。
        assert snap.items[0].state is ItemState.CANCELLED
        assert "it-0" not in record.item_errors
        # 取消不是错误：adapter 不得用 logger.exception 打完整错误栈。
        assert not [
            entry
            for entry in caplog.records
            if entry.levelno >= logging.ERROR and "result=failed" in entry.getMessage()
        ]
        assert any("result=cancelled" in entry.getMessage() for entry in caplog.records)

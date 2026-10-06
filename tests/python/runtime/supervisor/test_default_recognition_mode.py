"""默认识别模式（SettingsSnapshot.extra.default_recognition_mode）。

覆盖 ocr.default-recognition-mode.v1 的 HTTP 面契约：GET 回显解析后默认、
PUT 严格校验（类型/known id 恒验，可用性仅值变更时验）且失败不替换原
设置、持久化保存重启语义、持久值无法解析时原样回显，以及任务提交点
冻结（设置保存不改写已排队任务的 pipeline/engine）。
"""

from __future__ import annotations

import json
from collections.abc import Iterable
from pathlib import Path
from typing import Any

import httpx
import pytest
from vibeocr.runtime.environments.settings_store import RuntimeSettingsStore
from vibeocr.runtime.host.app import create_app
from vibeocr.runtime.host.bootstrap import generate_session_token
from vibeocr.runtime.jobs.module import SupervisorModule, SupervisorOptions
from vibeocr.runtime.jobs.registry import JobRecord
from vibeocr.runtime.jobs.staging import StagedInput
from vibeocr.runtime.recognition.recognition_modes import (
    RecognitionModeError,
    RecognitionModeRegistry,
    resolve_default_recognition_mode,
)
from vibeocr.runtime_contracts import (
    CancelMode,
    ResidencyStatus,
    SettingsSnapshot,
)
from vibeocr.runtime_contracts.generated import ALL_CAPABILITIES


class NullExecutor:
    """最小 executor：设置类测试不跑任务。"""

    def execute(self, record: JobRecord, staged: Iterable[StagedInput]) -> None:
        from vibeocr.runtime_contracts import JobState

        record.transition(JobState.FAILED)

    def cancel_mode_for(self, record: JobRecord) -> CancelMode:
        return CancelMode.COOPERATIVE

    def residency_status(self) -> ResidencyStatus:
        return ResidencyStatus()

    def release_idle(self, pipeline: str | None = None) -> ResidencyStatus:
        return ResidencyStatus()

    def preload(self, pipelines: tuple[str, ...]) -> ResidencyStatus:
        return ResidencyStatus()

    def configure_settings(self, snapshot: SettingsSnapshot) -> ResidencyStatus:
        return ResidencyStatus(
            default_ttl_seconds=snapshot.default_ttl_seconds,
            pipelines=snapshot.pipelines,
        )

    def close(self) -> None:
        return None


def _settings_body(
    extra: dict[str, Any] | None = None,
    ttl: int = 300,
) -> dict[str, Any]:
    return {
        "schema_version": 2,
        "residency": {"default_ttl_seconds": ttl, "pipelines": []},
        "extra": extra if extra is not None else {},
    }


def _client(module: SupervisorModule, token: str) -> httpx.AsyncClient:
    return httpx.AsyncClient(
        transport=httpx.ASGITransport(app=create_app(module, token)),
        base_url="http://127.0.0.1",
        headers={"Authorization": f"Bearer {token}"},
    )


def _module_with_store(tmp_path: Path) -> SupervisorModule:
    store = RuntimeSettingsStore(tmp_path / "state" / "supervisor-settings.json")
    return SupervisorModule(
        options=SupervisorOptions(instance_id="sup-default-mode"),
        stager_root=tmp_path / "staging",
        executor=NullExecutor(),
        settings_store=store,
    )


class TestDefaultRecognitionModeCapability:
    def test_capability_is_declared_with_generated_bindings(self) -> None:
        assert "ocr.default-recognition-mode.v1" in ALL_CAPABILITIES

    async def test_health_advertises_capability_descriptor(
        self, pdf_module: SupervisorModule, supervisor_token: str
    ) -> None:
        async with _client(pdf_module, supervisor_token) as http:
            health = await http.get("/v2/health")
        assert health.status_code == 200
        body = health.json()
        assert "ocr.default-recognition-mode.v1" in body["capabilities"]
        descriptor = next(
            item
            for item in body["capability_descriptors"]
            if item["name"] == "ocr.default-recognition-mode.v1"
        )
        assert descriptor["lifecycle"] == "active"
        assert descriptor["introduced_in"] == "2.10.0"


class TestDefaultRecognitionModeResolution:
    def test_missing_key_resolves_compat_rapid_text(self) -> None:
        from vibeocr.runtime.recognition.pipeline_contracts import RecognitionMode

        assert (
            resolve_default_recognition_mode(None, _registry(), require_available=True)
            is RecognitionMode.RAPID_TEXT
        )
        assert (
            resolve_default_recognition_mode({}, _registry(), require_available=True)
            is RecognitionMode.RAPID_TEXT
        )

    def test_explicit_null_is_not_the_missing_key(self) -> None:
        # 显式 null 是类型错误，不得当作缺键回退兼容默认。
        for require_available in (False, True):
            with pytest.raises(RecognitionModeError) as exc_info:
                resolve_default_recognition_mode(
                    {"default_recognition_mode": None},
                    _registry(),
                    require_available=require_available,
                )
            assert exc_info.value.code_name == "RECOGNITION_MODE_UNKNOWN"
            assert exc_info.value.reason_code == "recognition_mode_default_invalid_type"

    def test_non_string_value_fails_closed_unknown(self) -> None:
        with pytest.raises(RecognitionModeError) as exc_info:
            resolve_default_recognition_mode(
                {"default_recognition_mode": 42}, _registry(), require_available=True
            )
        assert exc_info.value.code_name == "RECOGNITION_MODE_UNKNOWN"
        assert exc_info.value.reason_code == "recognition_mode_default_invalid_type"

    def test_unknown_id_fails_closed_unknown(self) -> None:
        with pytest.raises(RecognitionModeError) as exc_info:
            resolve_default_recognition_mode(
                {"default_recognition_mode": "table_ocr_v2"},
                _registry(),
                require_available=False,
            )
        assert exc_info.value.code_name == "RECOGNITION_MODE_UNKNOWN"

    def test_unavailable_id_fails_closed_when_required(self) -> None:
        # 静态缺省探针下 paddle_text 需要 paddleocr-cpu 组件：非 ready。
        with pytest.raises(RecognitionModeError) as exc_info:
            resolve_default_recognition_mode(
                {"default_recognition_mode": "paddle_text"},
                _registry(),
                require_available=True,
            )
        assert exc_info.value.code_name == "RECOGNITION_MODE_UNAVAILABLE"
        # 只读解析（GET 回显路径）不校验可用性：值仍可回显。
        assert (
            resolve_default_recognition_mode(
                {"default_recognition_mode": "paddle_text"},
                _registry(),
                require_available=False,
            ).value
            == "paddle_text"
        )


def _registry() -> RecognitionModeRegistry:
    return RecognitionModeRegistry()


class TestSettingsRoutes:
    async def test_get_echoes_resolved_compat_default_when_key_missing(
        self, pdf_module: SupervisorModule, supervisor_token: str
    ) -> None:
        async with _client(pdf_module, supervisor_token) as http:
            got = await http.get("/v2/settings")
        assert got.status_code == 200
        assert got.json()["extra"]["default_recognition_mode"] == "rapid_text"

    async def test_put_rejects_unknown_id_and_keeps_prior_settings(
        self, pdf_module: SupervisorModule, supervisor_token: str
    ) -> None:
        async with _client(pdf_module, supervisor_token) as http:
            accepted = await http.put(
                "/v2/settings",
                json=_settings_body({"default_recognition_mode": "windows_text"}),
            )
            assert accepted.status_code == 200
            rejected = await http.put(
                "/v2/settings",
                json=_settings_body({"default_recognition_mode": "auto"}),
            )
            assert rejected.status_code == 400
            assert rejected.json()["code"] == "RECOGNITION_MODE_UNKNOWN"
            got = await http.get("/v2/settings")
        assert got.json()["extra"]["default_recognition_mode"] == "windows_text"

    async def test_put_rejects_non_string_value(
        self, pdf_module: SupervisorModule, supervisor_token: str
    ) -> None:
        async with _client(pdf_module, supervisor_token) as http:
            rejected = await http.put(
                "/v2/settings",
                json=_settings_body({"default_recognition_mode": ["rapid_text"]}),
            )
            null_rejected = await http.put(
                "/v2/settings",
                json=_settings_body({"default_recognition_mode": None}),
            )
        for response in (rejected, null_rejected):
            assert response.status_code == 400
            assert response.json()["code"] == "RECOGNITION_MODE_UNKNOWN"
            assert (
                response.json()["detail"]["reason_code"]
                == "recognition_mode_default_invalid_type"
            )

    async def test_put_rejects_unavailable_id_fail_closed(
        self, pdf_module: SupervisorModule, supervisor_token: str
    ) -> None:
        async with _client(pdf_module, supervisor_token) as http:
            accepted = await http.put("/v2/settings", json=_settings_body())
            assert accepted.status_code == 200
            rejected = await http.put(
                "/v2/settings",
                json=_settings_body({"default_recognition_mode": "paddle_text"}),
            )
            assert rejected.status_code == 426, rejected.text
            assert rejected.json()["code"] == "RECOGNITION_MODE_UNAVAILABLE"
            got = await http.get("/v2/settings")
        # 保存失败：原已提交默认/持久值不变（缺键 → 兼容 rapid_text 回显）。
        assert got.json()["extra"]["default_recognition_mode"] == "rapid_text"

    async def test_put_preserves_other_extra_keys(
        self, pdf_module: SupervisorModule, supervisor_token: str
    ) -> None:
        extra = {
            "mineru_connection": {"mode": "local"},
            "default_recognition_mode": "windows_text",
        }
        async with _client(pdf_module, supervisor_token) as http:
            put = await http.put("/v2/settings", json=_settings_body(extra))
        assert put.status_code == 200
        body = put.json()
        assert body["extra"]["mineru_connection"] == {"mode": "local"}
        assert body["extra"]["default_recognition_mode"] == "windows_text"

    async def test_unparsable_stored_value_is_echoed_verbatim_by_get(
        self, tmp_path: Path
    ) -> None:
        store_path = tmp_path / "state" / "supervisor-settings.json"
        store_path.parent.mkdir(parents=True)
        # 手工损坏的持久值：GET 不失败，原样回显供客户端标记并修复。
        store_path.write_text(
            json.dumps(
                {
                    "schema_version": 1,
                    "settings": _settings_body(
                        {"default_recognition_mode": "not_a_mode"}
                    ),
                }
            ),
            encoding="utf-8",
        )
        module = _module_with_store(tmp_path)
        token = generate_session_token()
        async with _client(module, token) as http:
            got = await http.get("/v2/settings")
        assert got.status_code == 200
        assert got.json()["extra"]["default_recognition_mode"] == "not_a_mode"
        # 修复路径：PUT 一个合法默认即可整体替换旧快照。
        async with _client(module, token) as http:
            repaired = await http.put(
                "/v2/settings",
                json=_settings_body({"default_recognition_mode": "rapid_text"}),
            )
            assert repaired.status_code == 200
            got = await http.get("/v2/settings")
        assert got.json()["extra"]["default_recognition_mode"] == "rapid_text"

    async def test_stale_default_does_not_block_unrelated_settings(
        self, tmp_path: Path
    ) -> None:
        # 旧默认已失效（组件被移除）时：同值读改写保存其他设置仍成功，
        # 只有变更默认值的写入才触发可用性校验。
        store_path = tmp_path / "state" / "supervisor-settings.json"
        store_path.parent.mkdir(parents=True)
        store_path.write_text(
            json.dumps(
                {
                    "schema_version": 1,
                    "settings": _settings_body(
                        {"default_recognition_mode": "paddle_text", "topic": 1}
                    ),
                }
            ),
            encoding="utf-8",
        )
        module = _module_with_store(tmp_path)
        token = generate_session_token()
        async with _client(module, token) as http:
            same_value = await http.put(
                "/v2/settings",
                json=_settings_body(
                    {"default_recognition_mode": "paddle_text", "topic": 2}, ttl=120
                ),
            )
            assert same_value.status_code == 200, same_value.text
            assert same_value.json()["residency"]["default_ttl_seconds"] == 120
            assert same_value.json()["extra"]["topic"] == 2
            changed = await http.put(
                "/v2/settings",
                json=_settings_body(
                    {"default_recognition_mode": "windows_text", "topic": 2}
                ),
            )
            assert changed.status_code == 200
            # 回到失效值（值变更）再次触发可用性校验。
            stale = await http.put(
                "/v2/settings",
                json=_settings_body(
                    {"default_recognition_mode": "paddle_text", "topic": 2}
                ),
            )
            assert stale.status_code == 426
            assert stale.json()["code"] == "RECOGNITION_MODE_UNAVAILABLE"


class TestPersistenceAndModuleSeam:
    async def test_saved_default_survives_supervisor_restart(
        self, tmp_path: Path
    ) -> None:
        token = generate_session_token()
        async with _client(_module_with_store(tmp_path), token) as http:
            put = await http.put(
                "/v2/settings",
                json=_settings_body({"default_recognition_mode": "windows_text"}),
            )
            assert put.status_code == 200
        # “重启”：同一路径的新 SupervisorModule 从持久文件加载已提交默认。
        restarted = _module_with_store(tmp_path)
        token2 = generate_session_token()
        async with _client(restarted, token2) as http:
            got = await http.get("/v2/settings")
        assert got.json()["extra"]["default_recognition_mode"] == "windows_text"

    def test_module_update_settings_validates_default_directly(
        self, pdf_module: SupervisorModule
    ) -> None:
        with pytest.raises(RecognitionModeError):
            pdf_module.update_settings(
                SettingsSnapshot(extra={"default_recognition_mode": "paddle_text"})
            )
        # 拒绝后模块内已提交设置不被替换。
        assert pdf_module.settings().extra == {}

    def test_module_update_settings_skips_availability_for_unchanged_value(
        self, pdf_module: SupervisorModule
    ) -> None:
        committed = SettingsSnapshot(
            extra={"default_recognition_mode": "windows_text", "keep": True}
        )
        pdf_module.update_settings(committed)
        # 同值重写（其他字段变化）不触发可用性校验；未知 id 仍恒定拒绝。
        pdf_module.update_settings(
            SettingsSnapshot(
                default_ttl_seconds=120,
                extra={"default_recognition_mode": "windows_text"},
            )
        )
        assert pdf_module.settings().default_ttl_seconds == 120
        with pytest.raises(RecognitionModeError):
            pdf_module.update_settings(
                SettingsSnapshot(extra={"default_recognition_mode": "not_a_mode"})
            )
        assert pdf_module.settings().extra["default_recognition_mode"] == "windows_text"

    async def test_saving_default_does_not_rewrite_submitted_job_parameters(
        self, tmp_path: Path
    ) -> None:
        module = _module_with_store(tmp_path)
        token = generate_session_token()
        manifest = json.dumps(
            {
                "schema_version": 2,
                "request_id": "r-1",
                "kind": "recognition",
                "priority": "interactive",
                "pipeline": {
                    "pipeline_id": "OCR",
                    "engine": "rapidocr",
                    "options_version": 1,
                    "options": {},
                },
                "items": [
                    {
                        "client_item_key": "k",
                        "ordinal": 0,
                        "display_name": "a.png",
                        "source": {"type": "upload.v1", "attachment": "f"},
                    }
                ],
            }
        )
        async with _client(module, token) as http:
            submitted = await http.post(
                "/v2/jobs",
                data={"manifest": manifest},
                files={"f": ("a.png", b"png-bytes", "image/png")},
            )
            assert submitted.status_code == 200
            job_id = submitted.json()["job_id"]
            changed = await http.put(
                "/v2/settings",
                json=_settings_body({"default_recognition_mode": "windows_text"}),
            )
            assert changed.status_code == 200
        record = next(r for r in module.registry if r.job_id == job_id)
        # 提交点冻结：任务 pipeline/engine 不被后续设置保存重写。
        assert record.pipeline.pipeline_id == "OCR"
        assert record.pipeline.engine == "rapidocr"

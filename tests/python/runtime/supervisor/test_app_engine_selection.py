"""OCR engine selection 的 HTTP 边界测试（submit seam + ready catalog）。

覆盖计划 §B1/§3.2 的 wire 语义：

* engine 只在 OCR pipeline 上合法，未知值 fail closed。
* 不可用/需准备返回 426/428 协议错误并回显可选引擎，绝不静默切换。
* ready envelope 的 ocr.engine-selection.v1 descriptor 携带真实探针目录，
  无真实推理后端时目录诚实标记全部不可用。
"""

from __future__ import annotations

import json
from typing import Any

import httpx
import pytest
from vibeocr.runtime.host.app import create_app
from vibeocr.runtime.host.bootstrap import generate_session_token, new_instance_id
from vibeocr.runtime.host.composition import build_supervisor
from vibeocr.runtime.jobs.budgets import AdapterCapability
from vibeocr.runtime.jobs.module import SupervisorModule, SupervisorOptions
from vibeocr.runtime.recognition.ocr_engines import (
    EngineAvailability,
    EngineDescriptor,
    OcrEngineRegistry,
)
from vibeocr.runtime_contracts.dtos import OcrEngine


class _FakeOcrEngine:
    def __init__(
        self,
        engine_id: OcrEngine,
        availability: EngineAvailability = EngineAvailability.READY,
        *,
        required_component: str | None = None,
    ) -> None:
        self.engine_id = engine_id
        self.availability = availability
        self.required_component = required_component

    def descriptor(self) -> EngineDescriptor:
        return EngineDescriptor(
            engine_id=self.engine_id,
            availability=self.availability,
            included_in_base=self.engine_id is OcrEngine.PADDLEOCR,
            required_component=self.required_component,
            reason_code=None
            if self.availability is EngineAvailability.READY
            else "engine_not_installed",
        )

    def capabilities(self, options: Any | None = None) -> AdapterCapability:
        del options
        return AdapterCapability(name="OCR", real_batch=False, max_compute_batch=1)

    def recognize_many(
        self,
        items: list[Any],
        *,
        options: Any | None = None,
        compute_batch: Any | None = None,
    ) -> list[dict[str, Any]]:
        return [{"engine": self.engine_id.value} for _ in items]

    def configure_settings(self, snapshot: Any) -> None:
        del snapshot


class _CapturingExecutor:
    """记录 job pipeline（含 engine）并把 job 立即置为完成。"""

    def __init__(self) -> None:
        self.pipelines: list[Any] = []

    def execute(self, record: Any, staged: Any) -> None:
        from vibeocr.runtime_contracts import ItemState, JobState

        self.pipelines.append(record.pipeline)
        record.transition(JobState.RUNNING)
        for item in record.items:
            record.commit_item_success(
                item.item_id, payload_type="ocr.v1", payload={"text": "ok"}
            )
        record.transition(JobState.COMPLETED)
        del ItemState

    def cancel_mode_for(self, record: Any) -> Any:
        from vibeocr.runtime_contracts import CancelMode

        return CancelMode.COOPERATIVE

    def residency_status(self) -> Any:
        from vibeocr.runtime_contracts import ResidencyStatus

        return ResidencyStatus()

    def release_idle(self, pipeline: str | None = None) -> Any:
        return self.residency_status()

    def preload(self, pipelines: tuple[str, ...]) -> Any:
        return self.residency_status()

    def configure_settings(self, snapshot: Any) -> None:
        del snapshot

    def close(self) -> None:
        return


def _module(
    tmp_path: Any, engines: list[_FakeOcrEngine] | None
) -> tuple[SupervisorModule, _CapturingExecutor]:
    executor = _CapturingExecutor()
    module = SupervisorModule(
        options=SupervisorOptions(instance_id=new_instance_id()),
        stager_root=tmp_path / "staging",
        executor=executor,
        engine_registry=OcrEngineRegistry(engines) if engines is not None else None,
    )
    return module, executor


def _client(app: Any, token: str) -> httpx.AsyncClient:
    return httpx.AsyncClient(
        transport=httpx.ASGITransport(app=app),
        base_url="http://127.0.0.1",
        headers={"Authorization": f"Bearer {token}"},
    )


def _manifest(
    engine: str | None = None,
    pipeline: str = "OCR",
) -> str:
    pipeline_obj: dict[str, Any] = {
        "pipeline_id": pipeline,
        "options_version": 1,
        "options": {},
    }
    if engine is not None:
        pipeline_obj["engine"] = engine
    return json.dumps(
        {
            "schema_version": 2,
            "request_id": "r-1",
            "kind": "recognition",
            "priority": "interactive",
            "pipeline": pipeline_obj,
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


@pytest.fixture()
def engines() -> list[_FakeOcrEngine]:
    return [
        _FakeOcrEngine(OcrEngine.WINDOWS, availability=EngineAvailability.UNAVAILABLE),
        _FakeOcrEngine(OcrEngine.PADDLEOCR),
    ]


async def _submit(http: httpx.AsyncClient, manifest: str) -> httpx.Response:
    return await http.post(
        "/v2/jobs",
        data={"manifest": manifest},
        files={"f": ("a.png", b"png-bytes", "image/png")},
    )


class TestSubmitEngineSelection:
    async def test_explicit_engine_flows_into_job(
        self, tmp_path: Any, engines: list[_FakeOcrEngine]
    ) -> None:
        module, executor = _module(tmp_path, engines)
        token = generate_session_token()
        async with _client(create_app(module, token), token) as http:
            resp = await _submit(http, _manifest(engine="paddleocr"))
        assert resp.status_code == 200
        assert executor.pipelines[0].engine is OcrEngine.PADDLEOCR

    async def test_omitted_engine_applies_paddle_default_at_submit(
        self, tmp_path: Any, engines: list[_FakeOcrEngine]
    ) -> None:
        module, executor = _module(tmp_path, engines)
        token = generate_session_token()
        async with _client(create_app(module, token), token) as http:
            resp = await _submit(http, _manifest())
        assert resp.status_code == 200
        # 缺省 engine 在提交 seam 解析并写回 effective 默认（paddleocr），
        # 后续冻结/门禁与显式 engine 完全一致。
        assert executor.pipelines[0].engine is OcrEngine.PADDLEOCR

    async def test_unknown_engine_value_fails_closed_400(
        self, tmp_path: Any, engines: list[_FakeOcrEngine]
    ) -> None:
        module, _ = _module(tmp_path, engines)
        token = generate_session_token()
        async with _client(create_app(module, token), token) as http:
            resp = await _submit(http, _manifest(engine="paddle"))
        assert resp.status_code == 400
        body = resp.json()
        assert body["code"] == "OCR_ENGINE_UNKNOWN"
        assert body["detail"]["engine"] == "paddle"

    async def test_retired_rapidocr_engine_fails_closed_426(
        self, tmp_path: Any, engines: list[_FakeOcrEngine]
    ) -> None:
        # RapidOCR 已退役：rapidocr 枚举值仍可反序列化（历史记录），但提交
        # 一律以稳定 reason=engine_retired 拒绝，绝不回退到默认 paddleocr。
        module, executor = _module(tmp_path, engines)
        token = generate_session_token()
        async with _client(create_app(module, token), token) as http:
            resp = await _submit(http, _manifest(engine="rapidocr"))
        assert resp.status_code == 426
        body = resp.json()
        assert body["code"] == "OCR_ENGINE_UNAVAILABLE"
        assert body["detail"]["engine"] == "rapidocr"
        assert body["detail"]["reason_code"] == "engine_retired"
        assert body["detail"]["selectable_engines"] == ["paddleocr"]
        assert executor.pipelines == []

    async def test_engine_on_non_ocr_pipeline_rejected_400(
        self, tmp_path: Any, engines: list[_FakeOcrEngine]
    ) -> None:
        module, _ = _module(tmp_path, engines)
        token = generate_session_token()
        async with _client(create_app(module, token), token) as http:
            resp = await _submit(
                http, _manifest(engine="paddleocr", pipeline="TABLE_RECOGNITION")
            )
        assert resp.status_code == 400
        assert resp.json()["code"] == "OCR_ENGINE_NOT_VALID_FOR_PIPELINE"

    async def test_unavailable_engine_rejected_426_with_selectable(
        self, tmp_path: Any, engines: list[_FakeOcrEngine]
    ) -> None:
        module, executor = _module(tmp_path, engines)
        token = generate_session_token()
        async with _client(create_app(module, token), token) as http:
            resp = await _submit(http, _manifest(engine="windows"))
        assert resp.status_code == 426, resp.text
        body = resp.json()
        assert body["code"] == "OCR_ENGINE_UNAVAILABLE"
        assert body["detail"]["selectable_engines"] == ["paddleocr"]
        # 未静默切换：job 从未进入 executor。
        assert executor.pipelines == []

    async def test_preparation_required_rejected_428(self, tmp_path: Any) -> None:
        engines = [
            _FakeOcrEngine(OcrEngine.WINDOWS),
            _FakeOcrEngine(
                OcrEngine.PADDLEOCR,
                availability=EngineAvailability.PREPARATION_REQUIRED,
                required_component="paddleocr-ort-cpu",
            ),
        ]
        module, _ = _module(tmp_path, engines)
        token = generate_session_token()
        async with _client(create_app(module, token), token) as http:
            resp = await _submit(http, _manifest(engine="paddleocr"))
        assert resp.status_code == 428
        body = resp.json()
        assert body["code"] == "OCR_ENGINE_PREPARATION_REQUIRED"
        assert body["detail"]["required_component"] == "paddleocr-ort-cpu"

    async def test_job_recognition_mode_extension_is_strictly_rejected(
        self, tmp_path: Any, engines: list[_FakeOcrEngine]
    ) -> None:
        module, executor = _module(tmp_path, engines)
        token = generate_session_token()
        payload = json.loads(_manifest(engine="paddleocr"))
        payload["pipeline"]["recognition_mode"] = "paddle_text"
        async with _client(create_app(module, token), token) as http:
            rejected = await _submit(http, json.dumps(payload))
            accepted = await _submit(http, _manifest(engine="paddleocr"))

        assert rejected.status_code == 400
        assert rejected.json()["code"] == "VALIDATION_ERROR"
        assert accepted.status_code == 200
        assert executor.pipelines[0].engine is OcrEngine.PADDLEOCR

    async def test_retired_recognition_mode_extension_is_rejected(
        self, tmp_path: Any, engines: list[_FakeOcrEngine]
    ) -> None:
        # rapid_text 已退役：扩展字段即使携带旧值也按未知字段拒绝。
        module, executor = _module(tmp_path, engines)
        token = generate_session_token()
        payload = json.loads(_manifest(engine="paddleocr"))
        payload["pipeline"]["recognition_mode"] = "rapid_text"
        async with _client(create_app(module, token), token) as http:
            rejected = await _submit(http, json.dumps(payload))

        assert rejected.status_code == 400
        assert rejected.json()["code"] == "VALIDATION_ERROR"
        assert executor.pipelines == []

    async def test_default_engine_unavailable_fails_closed_426(
        self, tmp_path: Any
    ) -> None:
        # 默认 paddleocr 缺席/不可用时缺省请求同样 fail closed，不落到其他
        # ready 引擎。
        engines = [
            _FakeOcrEngine(OcrEngine.WINDOWS),
            _FakeOcrEngine(
                OcrEngine.PADDLEOCR, availability=EngineAvailability.UNAVAILABLE
            ),
        ]
        module, executor = _module(tmp_path, engines)
        token = generate_session_token()
        async with _client(create_app(module, token), token) as http:
            resp = await _submit(http, _manifest())
        assert resp.status_code == 426
        assert resp.json()["code"] == "OCR_ENGINE_UNAVAILABLE"
        assert resp.json()["detail"]["engine"] == "paddleocr"
        assert executor.pipelines == []

    async def test_specialized_pipeline_uses_mode_registry_availability(
        self, tmp_path: Any, engines: list[_FakeOcrEngine]
    ) -> None:
        module, executor = _module(tmp_path, engines)
        token = generate_session_token()
        async with _client(create_app(module, token), token) as http:
            resp = await _submit(http, _manifest(pipeline="TABLE_RECOGNITION"))

        assert resp.status_code == 426, resp.text
        assert resp.json()["code"] == "RECOGNITION_MODE_UNAVAILABLE"
        assert resp.json()["detail"]["recognition_mode"] == "paddle_table"
        assert executor.pipelines == []

    async def test_no_registry_value_checks_still_apply(self, tmp_path: Any) -> None:
        # 无真实推理后端（Null/fake executor）：engine 值级校验仍然生效。
        module, _ = _module(tmp_path, None)
        token = generate_session_token()
        async with _client(create_app(module, token), token) as http:
            resp = await _submit(http, _manifest(engine="bogus"))
        assert resp.status_code == 400
        assert resp.json()["code"] == "OCR_ENGINE_UNKNOWN"


class TestOmittedEngineFreezeParity:
    """缺省 engine 与显式 paddleocr 的提交期冻结/门禁完全一致。"""

    @staticmethod
    def _manifest_json(
        request_id: str, engine: str | None, options: dict[str, Any]
    ) -> str:
        pipeline: dict[str, Any] = {
            "pipeline_id": "OCR",
            "options_version": 1,
            "options": options,
        }
        if engine is not None:
            pipeline["engine"] = engine
        return json.dumps(
            {
                "schema_version": 2,
                "request_id": request_id,
                "kind": "recognition",
                "priority": "interactive",
                "pipeline": pipeline,
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

    async def test_omitted_engine_freezes_same_paddle_snapshot_as_explicit(
        self, tmp_path: Any, monkeypatch: pytest.MonkeyPatch
    ) -> None:
        from vibeocr.runtime_contracts import SettingsSnapshot

        monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_RECIPE", "paddleocr-ort-cpu")
        monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_ID", "managed-default")
        monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_REVISION", "3")
        module, _ = _module(
            tmp_path,
            [
                _FakeOcrEngine(
                    OcrEngine.WINDOWS, availability=EngineAvailability.UNAVAILABLE
                ),
                _FakeOcrEngine(OcrEngine.PADDLEOCR),
            ],
        )
        # settings download_source_ids 必须进入提交期冻结快照。
        module.update_settings(
            SettingsSnapshot(download_source_ids=("paddleocr-modelscope",))
        )
        token = generate_session_token()
        options = {
            "model_preset": "small",
            "device": "cpu",
            "use_doc_orientation_classify": True,
        }
        async with _client(create_app(module, token), token) as http:
            omitted = await _submit(
                http, self._manifest_json("parity-omitted", None, options)
            )
            explicit = await _submit(
                http, self._manifest_json("parity-explicit", "paddleocr", options)
            )
        assert omitted.status_code == 200, omitted.text
        assert explicit.status_code == 200, explicit.text
        records = {record.request_id: record for record in module.registry}
        snapshots = [
            records["parity-omitted"].paddle_snapshot,
            records["parity-explicit"].paddle_snapshot,
        ]
        # 缺省与显式 engine 冻结出完全相同的提交期快照：环境、download
        # source 与模型选择一致，执行期不再重新解析。
        assert snapshots[0] is not None and snapshots[1] is not None
        assert snapshots[0] == snapshots[1]
        assert snapshots[0]["source"] == "modelscope"
        assert snapshots[0]["recipe"] == "paddleocr-ort-cpu"
        assert snapshots[0]["environment_id"] == "managed-default"
        assert snapshots[0]["environment_revision"] == "3"

    async def test_omitted_engine_rejects_wrong_device_at_submit(
        self, tmp_path: Any, monkeypatch: pytest.MonkeyPatch
    ) -> None:
        """缺省 engine（解析为 paddleocr）携带环境不匹配设备时，提交期即
        拒绝（VALIDATION_ERROR），不得绕过门禁到执行期才失败。"""
        monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_RECIPE", "paddleocr-ort-cpu")
        module, executor = _module(
            tmp_path,
            [
                _FakeOcrEngine(
                    OcrEngine.WINDOWS, availability=EngineAvailability.UNAVAILABLE
                ),
                _FakeOcrEngine(OcrEngine.PADDLEOCR),
            ],
        )
        token = generate_session_token()
        async with _client(create_app(module, token), token) as http:
            rejected = await _submit(
                http, self._manifest_json("wrong-device", None, {"device": "cuda"})
            )
        assert rejected.status_code == 400, rejected.text
        assert rejected.json()["code"] == "VALIDATION_ERROR"
        assert "matching Paddle ONNX environment" in rejected.text
        # 提交被拒：任务从未进入 executor，也没有任何冻结快照。
        assert executor.pipelines == []
        assert all(record.paddle_snapshot is None for record in module.registry)


class TestReadyEngineCatalog:
    async def test_catalog_reflects_live_probes(
        self, tmp_path: Any, engines: list[_FakeOcrEngine]
    ) -> None:
        module, _ = _module(tmp_path, engines)
        token = generate_session_token()
        async with _client(create_app(module, token), token) as http:
            resp = await http.get("/v2/health")
        assert resp.status_code == 200
        descriptors = resp.json()["capability_descriptors"]
        engine_descriptor = next(
            d for d in descriptors if d["name"] == "ocr.engine-selection.v1"
        )
        catalog = engine_descriptor["ocr_engine_catalog"]
        entries = {entry["id"]: entry for entry in catalog["engines"]}
        assert set(entries) == {"rapidocr", "windows", "paddleocr"}
        # 已退休的 rapidocr 固定投影 unavailable/engine_retired。
        assert entries["rapidocr"]["availability"] == "unavailable"
        assert entries["rapidocr"]["reason_code"] == "engine_retired"
        assert entries["windows"]["availability"] == "unavailable"
        assert entries["paddleocr"]["availability"] == "ready"
        assert entries["paddleocr"]["included_in_base"] is True

    async def test_catalog_without_registry_is_honestly_unavailable(
        self, tmp_path: Any
    ) -> None:
        module, _ = _module(tmp_path, None)
        token = generate_session_token()
        async with _client(create_app(module, token), token) as http:
            resp = await http.get("/v2/health")
        engine_descriptor = next(
            d
            for d in resp.json()["capability_descriptors"]
            if d["name"] == "ocr.engine-selection.v1"
        )
        entries = {
            entry["id"]: entry
            for entry in engine_descriptor["ocr_engine_catalog"]["engines"]
        }
        assert set(entries) == {"rapidocr", "windows", "paddleocr"}
        assert all(entry["availability"] == "unavailable" for entry in entries.values())

    async def test_successful_maintenance_refreshes_cached_engine_catalog(
        self, tmp_path: Any, engines: list[_FakeOcrEngine]
    ) -> None:
        class _SuccessfulMaintenance:
            def execute(self, **kwargs: Any) -> dict[str, Any]:
                return {
                    "schema_version": 2,
                    "operation_id": "ensure-1",
                    "snapshot": {
                        "operation_id": "ensure-1",
                        "sequence": 1,
                        "operation": "ensure",
                        "operation_state": "succeeded",
                        "phase": "commit_runtime",
                        "profile_id": "win-x64-cpu",
                        "updated_at": "2026-08-17T00:00:00Z",
                    },
                    "negotiated_capabilities": list(kwargs["required_capabilities"]),
                }

        module, _ = _module(tmp_path, engines)
        paddle = next(
            engine for engine in engines if engine.engine_id is OcrEngine.PADDLEOCR
        )
        token = generate_session_token()
        app = create_app(
            module,
            token,
            runtime_control=_SuccessfulMaintenance(),  # type: ignore[arg-type]
        )

        async with _client(app, token) as http:
            before = await http.get("/v2/health")
            paddle.availability = EngineAvailability.UNAVAILABLE
            cached = await http.get("/v2/health")
            maintained = await http.post(
                "/v2/runtime/maintenance",
                json={
                    "operation_id": "ensure-1",
                    "operation": "ensure",
                    "profile_id": "win-x64-cpu",
                    "component_ids": [],
                    "required_capabilities": [],
                },
            )
            refreshed = await http.get("/v2/health")

        def paddle_availability(response: httpx.Response) -> str:
            descriptor = next(
                item
                for item in response.json()["capability_descriptors"]
                if item["name"] == "ocr.engine-selection.v1"
            )
            entry = next(
                item
                for item in descriptor["ocr_engine_catalog"]["engines"]
                if item["id"] == "paddleocr"
            )
            return str(entry["availability"])

        assert paddle_availability(before) == "ready"
        assert paddle_availability(cached) == "ready"
        assert maintained.status_code == 200
        assert paddle_availability(refreshed) == "unavailable"


async def test_legacy_ocr_preload_is_rejected_instead_of_warming_default_engine(
    tmp_path: Any,
) -> None:
    paddle = _FakeOcrEngine(OcrEngine.PADDLEOCR)
    module, handle = build_supervisor(
        instance_id=new_instance_id(),
        stager_root=tmp_path / "staging",
        engine_registry=OcrEngineRegistry([paddle]),
        use_real_paddle=False,
        use_mineru=False,
    )
    async with _client(create_app(module, handle.token), handle.token) as http:
        response = await http.post("/v2/runtime/preload", json={"pipelines": ["OCR"]})
    assert response.status_code == 400, response.text
    assert response.json()["code"] == "RECOGNITION_MODE_PIPELINE_MISMATCH"


async def test_preload_and_release_enforce_recognition_mode_lifecycle(
    tmp_path: Any,
) -> None:
    module, executor = _module(tmp_path, None)
    token = generate_session_token()
    app = create_app(module, token)
    async with _client(app, token) as http:
        paddle = await http.post(
            "/v2/runtime/preload",
            json={"pipelines": ["OCR"], "recognition_modes": ["paddle_text"]},
        )
        mineru = await http.post(
            "/v2/runtime/preload",
            json={"pipelines": ["MinerU"], "recognition_modes": ["mineru_document"]},
        )
        retired_release = await http.post(
            "/v2/runtime/release",
            json={"pipeline": "OCR", "recognition_mode": "rapid_text"},
        )

    assert paddle.status_code == 200, paddle.text
    assert mineru.status_code == 426
    assert mineru.json()["code"] == "RECOGNITION_MODE_UNAVAILABLE"
    # rapid_text 已退役：生命周期 seam 同样以 RECOGNITION_MODE_RETIRED
    # 明确拒绝（枚举值仅供历史记录反序列化）。
    assert retired_release.status_code == 426
    assert retired_release.json()["code"] == "RECOGNITION_MODE_RETIRED"
    del executor


async def test_residency_response_keeps_protocol_resource_identity(
    tmp_path: Any,
) -> None:
    from vibeocr.runtime_contracts import ResidencyEntry, ResidencyKind, ResidencyStatus

    module, executor = _module(tmp_path, None)
    executor.residency_status = lambda: ResidencyStatus(  # type: ignore[method-assign]
        entries=(ResidencyEntry(pipeline="MinerU", kind=ResidencyKind.SOFT_TTL),)
    )
    token = generate_session_token()

    async with _client(create_app(module, token), token) as http:
        response = await http.get("/v2/runtime/residency")

    assert response.status_code == 200, response.text
    entry = response.json()["entries"][0]
    assert entry["recognition_mode"] == "mineru_document"
    assert entry["resource_kind"] == "process"
    assert entry["resource_id"] == "mineru-api"


@pytest.mark.parametrize("fail_prepare", [False, True])
async def test_mineru_prepare_refresh_and_typed_submit(
    tmp_path, monkeypatch, fail_prepare
):
    from vibeocr.runtime.recognition import mineru_readiness as readiness
    from vibeocr.runtime.recognition.recognition_modes import (
        ModeAvailability,
        RecognitionModeRegistry,
    )
    from vibeocr.runtime_contracts import MineruTier

    monkeypatch.setattr(readiness.metadata, "version", lambda _: "4.0.2")
    monkeypatch.setattr(readiness, "_observations", {})
    module, executor = _module(tmp_path, None)
    module.recognition_mode_registry = RecognitionModeRegistry(
        availability_probe=lambda definition: ModeAvailability("ready")
    )
    calls = []

    def prepare(pipelines):
        calls.append(pipelines)
        readiness.mark_executed(MineruTier.FLASH)
        if fail_prepare:
            readiness.mark_failed(MineruTier.BASIC)
            raise RuntimeError("basic preparation failed")
        readiness.mark_executed(MineruTier.BASIC)
        return executor.residency_status()

    monkeypatch.setattr(executor, "preload", prepare)
    manifest = json.loads(_manifest(pipeline="MinerU"))
    manifest["kind"] = "mineru_parse"
    manifest["pipeline"]["mineru"] = {"tier": "basic"}
    token = generate_session_token()
    async with _client(create_app(module, token), token) as http:
        before = await _submit(http, json.dumps(manifest))
        assert before.status_code == 428, before.text
        assert before.json()["code"] == "MINERU_TIER_PREPARATION_REQUIRED"
        prepared = await http.post(
            "/v2/runtime/preload",
            json={"pipelines": ["MinerU"], "recognition_modes": ["mineru_document"]},
        )
        assert prepared.status_code == (500 if fail_prepare else 200), prepared.text
        health = await http.get("/v2/health")
        descriptor = next(
            item
            for item in health.json()["capability_descriptors"]
            if item["name"] == "ocr.mineru-config.v1"
        )
        tiers = {
            item["id"]: item["availability"]
            for item in descriptor["mineru_config_catalog"]["tiers"]
        }
        assert tiers["flash"] == "ready"
        assert tiers["basic"] == ("preparation_required" if fail_prepare else "ready")
        assert tiers["advanced"] == "preparation_required"
        after = await _submit(http, json.dumps(manifest))
        assert after.status_code == (428 if fail_prepare else 200), after.text
    assert calls == [("MinerU",)]
    if not fail_prepare:
        assert after.json()["job_id"]


async def test_mineru_only_composition_preloads_real_service_interface(
    tmp_path, monkeypatch
):
    from vibeocr.runtime.recognition.mineru_service import MinerUService

    calls = []
    monkeypatch.setattr(
        "vibeocr.runtime.host.composition._mineru_available", lambda: True
    )
    monkeypatch.setattr(MinerUService, "_ensure_api_running", lambda self: None)
    monkeypatch.setattr(
        MinerUService, "prepare", lambda self, *, cancelled: calls.append("prepare")
    )
    monkeypatch.setattr(MinerUService, "shutdown", lambda self: None)
    module, handle = build_supervisor(
        stager_root=tmp_path / "staging",
        use_real_paddle=False,
        use_mineru=True,
        engine_registry=OcrEngineRegistry([]),
    )
    try:
        async with _client(create_app(module, handle.token), handle.token) as http:
            response = await http.post(
                "/v2/runtime/preload",
                json={
                    "pipelines": ["MinerU"],
                    "recognition_modes": ["mineru_document"],
                },
            )
        assert response.status_code == 200, response.text
        assert calls == ["prepare"]
    finally:
        module.shutdown_now()


@pytest.mark.parametrize("device", ["cpu", "cuda", "directml"])
@pytest.mark.parametrize("ready", [True, False])
async def test_ort_retained_mode_catalog_and_default_follow_paddle_probe(
    tmp_path, monkeypatch, device, ready
):
    from vibeocr.runtime.host.composition import _build_recognition_mode_registry

    recipe = f"paddleocr-ort-{device}"
    monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_RECIPE", recipe)
    status = (
        EngineAvailability.READY if ready else EngineAvailability.PREPARATION_REQUIRED
    )
    module, _ = _module(tmp_path, [_FakeOcrEngine(OcrEngine.PADDLEOCR, status)])
    module.recognition_mode_registry = _build_recognition_mode_registry(
        engine_resolver=module.engine_resolver,
        use_paddle=ready,
        use_mineru=False,
    )
    token = generate_session_token()
    async with _client(create_app(module, token), token) as http:
        health = await http.get("/v2/health")
        assert health.status_code == 200
        descriptor = next(
            item
            for item in health.json()["capability_descriptors"]
            if item["name"] == "ocr.recognition-modes.v1"
        )
        entries = {
            item["id"]: item for item in descriptor["recognition_mode_catalog"]["modes"]
        }
        for mode in ("paddle_table", "paddle_structure"):
            assert {"device", "model_preset"} <= set(entries[mode]["supported_options"])
            assert entries[mode]["availability"] == status.value
            assert entries[mode]["required_component"] == (None if ready else recipe)
            assert entries[mode]["reason_code"] == (
                None if ready else "engine_not_installed"
            )
            response = await http.put(
                "/v2/settings",
                json={
                    "schema_version": 2,
                    "residency": {"default_ttl_seconds": 300, "pipelines": []},
                    "extra": {"default_recognition_mode": mode},
                },
            )
            assert response.status_code == (200 if ready else 426), response.text
            persisted = (await http.get("/v2/settings")).json()
            if ready:
                assert persisted["extra"]["default_recognition_mode"] == mode
            else:
                assert persisted["extra"].get("default_recognition_mode") != mode
        # #270：退役模式不在目录中；旧 id 的 API 请求明确拒绝且可改选。
        for mode in ("paddle_formula", "paddle_document_vl"):
            assert mode not in entries
            manifest = json.loads(_manifest(pipeline="PaddleOCR-VL"))
            if mode == "paddle_formula":
                manifest = json.loads(_manifest(pipeline="FORMULA_RECOGNITION"))
            rejected = await _submit(http, json.dumps(manifest))
            assert rejected.status_code == 426, rejected.text
            assert rejected.json()["code"] == "RECOGNITION_MODE_RETIRED"


@pytest.mark.parametrize("recipe", ["paddleocr-cpu", "paddleocr-cuda"])
async def test_legacy_retained_catalog_settings_and_submit_reject_ort_only_options(
    tmp_path, monkeypatch, recipe
):
    """#270：旧原生 Paddle 环境不再是保留模式后端；提示准备 ONNX 环境。"""
    from vibeocr.runtime.host.composition import _build_recognition_mode_registry

    monkeypatch.setenv("VIBEOCR_MANAGED_ENVIRONMENT_RECIPE", recipe)
    module, _ = _module(tmp_path, [_FakeOcrEngine(OcrEngine.PADDLEOCR)])
    module.recognition_mode_registry = _build_recognition_mode_registry(
        engine_resolver=module.engine_resolver,
        use_paddle=True,
        use_mineru=False,
    )
    token = generate_session_token()
    async with _client(create_app(module, token), token) as http:
        health = (await http.get("/v2/health")).json()
        descriptor = next(
            item
            for item in health["capability_descriptors"]
            if item["name"] == "ocr.recognition-modes.v1"
        )
        entries = {
            item["id"]: item for item in descriptor["recognition_mode_catalog"]["modes"]
        }
        # 退役模式不出现在公开目录。
        assert "paddle_document_vl" not in entries
        assert "paddle_formula" not in entries
        for mode, pipeline, engine in (
            ("paddle_text", "OCR", "paddleocr"),
            ("paddle_table", "TABLE_RECOGNITION", None),
            ("paddle_structure", "PP-StructureV3", None),
        ):
            assert not {"device", "model_preset"}.intersection(
                entries[mode]["supported_options"]
            )
            assert "use_doc_unwarping" in entries[mode]["supported_options"]
            # 保留模式在旧原生环境中不再可作为默认或执行：提示准备 ONNX。
            assert entries[mode]["availability"] == "preparation_required"
            assert entries[mode]["reason_code"] == (
                "mode_requires_paddle_onnx_environment"
            )
            assert entries[mode]["required_component"] == "paddleocr-ort-cpu"
            saved = await http.put(
                "/v2/settings",
                json={
                    "schema_version": 2,
                    "residency": {"default_ttl_seconds": 300, "pipelines": []},
                    "extra": {"default_recognition_mode": mode},
                },
            )
            assert saved.status_code == 426, saved.text
            assert saved.json()["code"] == "RECOGNITION_MODE_UNAVAILABLE"
            manifest = json.loads(_manifest(engine=engine, pipeline=pipeline))
            manifest["pipeline"]["options"] = {"use_doc_unwarping": False}
            rejected = await _submit(http, json.dumps(manifest))
            assert rejected.status_code == 426, rejected.text
            assert rejected.json()["code"] == "RECOGNITION_MODE_UNAVAILABLE"


async def test_cold_mineru_second_http_submit_does_not_block_health_or_cancel(
    tmp_path, monkeypatch
):
    import asyncio
    import threading

    from vibeocr.runtime.recognition import mineru_readiness as readiness
    from vibeocr.runtime.recognition.mineru_adapter import MinerUProcessAdapter
    from vibeocr.runtime.recognition.mineru_executor import MinerUExecutor
    from vibeocr.runtime.recognition.recognition_modes import (
        ModeAvailability,
        RecognitionModeRegistry,
    )
    from vibeocr.runtime_contracts import MineruTier

    entered, finish = threading.Event(), threading.Event()

    class Lifecycle:
        def start(self):
            entered.set()
            assert finish.wait(4)

        def stop(self):
            pass

    class Client:
        def file_parse(self, files, **kwargs):
            return {}

    monkeypatch.setattr(readiness, "_connection", readiness.MineruConnection())
    monkeypatch.setattr(readiness, "_observations", {})
    monkeypatch.setattr(readiness.metadata, "version", lambda _: "4.0.2")
    readiness.mark_executed(MineruTier.BASIC)
    adapter = MinerUProcessAdapter(client_factory=Client, lifecycle=Lifecycle())
    module = SupervisorModule(
        options=SupervisorOptions(instance_id="cold-http"),
        stager_root=tmp_path / "stage",
        executor=MinerUExecutor(lambda: adapter),
    )
    module.recognition_mode_registry = RecognitionModeRegistry(
        availability_probe=lambda definition: ModeAvailability("ready")
    )
    manifest = json.loads(_manifest(pipeline="MinerU"))
    manifest["kind"] = "mineru_parse"
    manifest["pipeline"]["mineru"] = {"tier": "basic"}
    token = generate_session_token()
    watchdog = threading.Timer(1.0, finish.set)
    try:
        async with _client(create_app(module, token), token) as http:
            first = await _submit(http, json.dumps(manifest))
            assert first.status_code == 200, first.text
            assert await asyncio.to_thread(entered.wait, 1)
            manifest["request_id"] = "second"
            watchdog.start()
            began = asyncio.get_running_loop().time()
            second, health, cancelled = await asyncio.gather(
                _submit(http, json.dumps(manifest)),
                http.get("/v2/health"),
                http.post(
                    "/v2/jobs/command",
                    json={
                        "command_id": "cancel-first",
                        "kind": "cancel",
                        "job_id": first.json()["job_id"],
                    },
                ),
            )
            assert (
                second.status_code == health.status_code == cancelled.status_code == 200
            )
            assert second.json()["job_id"] != first.json()["job_id"]
            assert asyncio.get_running_loop().time() - began < 0.5
            assert not finish.is_set(), (
                "HTTP must complete while native startup is still blocked"
            )
            assert adapter._active_leases >= 2
    finally:
        watchdog.cancel()
        finish.set()
        # Stop waits for the deterministic startup barrier, never a real model.
        adapter.close()

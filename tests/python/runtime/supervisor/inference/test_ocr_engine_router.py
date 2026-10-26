"""OcrEngineRoutingAdapter 测试：OCR pipeline 按引擎路由、其余直达 fallback。

另覆盖 executor 对 OcrEngineError 的 fail-fast：确定性引擎错误按协议
错误码标记 item，不进入恢复重试，也不切换引擎。
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any

import pytest
from vibeocr.runtime.jobs.budgets import AdapterCapability, InputItem
from vibeocr.runtime.recognition.ocr_engine_router import (
    OcrEngineRoutingAdapter,
)
from vibeocr.runtime.recognition.ocr_engines import (
    EngineAvailability,
    EngineDescriptor,
    OcrEngineError,
    OcrEngineRegistry,
    OcrEngineResolver,
)
from vibeocr.runtime_contracts import (
    ErrorCode,
    ItemState,
    JobKind,
    JobPriority,
    JobState,
)
from vibeocr.runtime_contracts.dtos import JobItem, OcrEngine, PipelineSelection


@dataclass
class RecordingEngine:
    engine_id: OcrEngine
    availability: EngineAvailability = EngineAvailability.READY
    recognize_calls: list[list[InputItem]] = field(default_factory=list)
    preload_calls: list[tuple[str, ...]] = field(default_factory=list)
    closed: bool = False

    def descriptor(self) -> EngineDescriptor:
        return EngineDescriptor(
            engine_id=self.engine_id, availability=self.availability
        )

    def capabilities(self, options: Any | None = None) -> AdapterCapability:
        del options
        return AdapterCapability(name="OCR", real_batch=False, max_compute_batch=1)

    def recognize_many(
        self,
        items: list[InputItem],
        *,
        options: Any | None = None,
        compute_batch: Any | None = None,
    ) -> list[dict[str, Any]]:
        self.recognize_calls.append(list(items))
        return [{"engine": self.engine_id.value} for _ in items]

    def preload(self, pipelines: tuple[str, ...]) -> Any:
        self.preload_calls.append(tuple(pipelines))
        from vibeocr.runtime_contracts import ResidencyStatus

        return ResidencyStatus()

    def residency_status(self) -> Any:
        from vibeocr.runtime_contracts import ResidencyStatus

        return ResidencyStatus()

    def release_idle(self, pipeline: str | None = None) -> Any:
        return self.residency_status()

    def configure_settings(self, snapshot: Any) -> None:
        del snapshot

    def close(self) -> None:
        self.closed = True


@dataclass
class RecordingFallback:
    recognize_calls: list[Any] = field(default_factory=list)
    capabilities_calls: list[Any] = field(default_factory=list)
    preload_calls: list[tuple[str, ...]] = field(default_factory=list)
    closed: bool = False

    def capabilities(self, options: Any | None = None) -> AdapterCapability:
        self.capabilities_calls.append(options)
        return AdapterCapability(name="fallback", real_batch=False, max_compute_batch=1)

    def recognize_many(
        self,
        items: list[InputItem],
        *,
        options: Any | None = None,
        compute_batch: Any | None = None,
    ) -> list[dict[str, Any]]:
        self.recognize_calls.append((list(items), options))
        return [{"engine": "fallback"} for _ in items]

    def preload(self, pipelines: tuple[str, ...]) -> Any:
        self.preload_calls.append(tuple(pipelines))
        from vibeocr.runtime_contracts import ResidencyStatus

        return ResidencyStatus()

    def residency_status(self) -> Any:
        from vibeocr.runtime_contracts import ResidencyStatus

        return ResidencyStatus()

    def release_idle(self, pipeline: str | None = None) -> Any:
        return self.residency_status()

    def configure_settings(self, snapshot: Any) -> None:
        del snapshot

    def close(self) -> None:
        self.closed = True


def _input(item_id: str = "it-1") -> InputItem:
    return InputItem(item_id=item_id, encoded_bytes=4, decoded_pixels=10, data=b"png")


def _router(
    engines: list[RecordingEngine], fallback: RecordingFallback
) -> tuple[OcrEngineRoutingAdapter, OcrEngineRegistry]:
    registry = OcrEngineRegistry(engines)
    return (
        OcrEngineRoutingAdapter(
            fallback_factory=lambda: fallback, resolver=OcrEngineResolver(registry)
        ),
        registry,
    )


class TestRouting:
    def test_ocr_default_routes_to_paddleocr(self) -> None:
        windows = RecordingEngine(OcrEngine.WINDOWS)
        paddle = RecordingEngine(OcrEngine.PADDLEOCR)
        fallback = RecordingFallback()
        router, _ = _router([windows, paddle], fallback)
        options = PipelineSelection(pipeline_id="OCR", options={})
        payloads = router.recognize_many([_input()], options=options)
        assert payloads == [{"engine": "paddleocr"}]
        assert paddle.recognize_calls and not windows.recognize_calls
        assert not fallback.recognize_calls

    def test_explicit_engine_selection_is_honoured(self) -> None:
        paddle = RecordingEngine(OcrEngine.PADDLEOCR)
        windows = RecordingEngine(OcrEngine.WINDOWS)
        fallback = RecordingFallback()
        router, _ = _router([paddle, windows], fallback)
        options = PipelineSelection(
            pipeline_id="OCR", options={}, engine=OcrEngine.WINDOWS
        )
        assert router.recognize_many([_input()], options=options) == [
            {"engine": "windows"}
        ]
        assert not paddle.recognize_calls

    def test_retired_rapidocr_engine_is_rejected_by_resolver(self) -> None:
        # RapidOCR 已退役：rapidocr 枚举值保留（历史记录反序列化），
        # resolver 以稳定 reason=engine_retired 拒绝，绝不路由执行。
        paddle = RecordingEngine(OcrEngine.PADDLEOCR)
        fallback = RecordingFallback()
        router, _ = _router([paddle], fallback)
        options = PipelineSelection(
            pipeline_id="OCR", options={}, engine=OcrEngine.RAPIDOCR
        )
        with pytest.raises(OcrEngineError) as excinfo:
            router.recognize_many([_input()], options=options)
        assert excinfo.value.code is ErrorCode.OCR_ENGINE_UNAVAILABLE
        assert excinfo.value.reason_code == "engine_retired"
        assert not paddle.recognize_calls
        assert not fallback.recognize_calls

    def test_unavailable_engine_fails_closed_without_fallback(self) -> None:
        # paddle ready 但请求 windows（未注册）：错误，不落到 fallback/paddle。
        paddle = RecordingEngine(OcrEngine.PADDLEOCR)
        fallback = RecordingFallback()
        router, _ = _router([paddle], fallback)
        options = PipelineSelection(
            pipeline_id="OCR", options={}, engine=OcrEngine.WINDOWS
        )
        with pytest.raises(OcrEngineError) as excinfo:
            router.recognize_many([_input()], options=options)
        assert excinfo.value.code is ErrorCode.OCR_ENGINE_UNAVAILABLE
        assert not fallback.recognize_calls
        assert not paddle.recognize_calls

    def test_unavailable_engine_capabilities_raises(self) -> None:
        windows = RecordingEngine(
            OcrEngine.WINDOWS, availability=EngineAvailability.UNAVAILABLE
        )
        fallback = RecordingFallback()
        router, _ = _router([windows], fallback)
        options = PipelineSelection(
            pipeline_id="OCR", options={}, engine=OcrEngine.WINDOWS
        )
        with pytest.raises(OcrEngineError):
            router.capabilities(options)

    def test_non_ocr_pipeline_goes_to_fallback(self) -> None:
        paddle = RecordingEngine(OcrEngine.PADDLEOCR)
        fallback = RecordingFallback()
        router, _ = _router([paddle], fallback)
        options = PipelineSelection(pipeline_id="TABLE_RECOGNITION", options={})
        assert router.recognize_many([_input()], options=options) == [
            {"engine": "fallback"}
        ]
        assert not paddle.recognize_calls
        router.capabilities(options)
        assert fallback.capabilities_calls == [options]

    def test_ocr_capabilities_delegate_to_resolved_engine(self) -> None:
        paddle = RecordingEngine(OcrEngine.PADDLEOCR)
        fallback = RecordingFallback()
        router, _ = _router([paddle], fallback)
        options = PipelineSelection(pipeline_id="OCR", options={})
        capability = router.capabilities(options)
        assert capability.name == "OCR"
        assert not fallback.capabilities_calls


class TestLifecycle:
    def test_preload_splits_ocr_from_other_pipelines(self) -> None:
        paddle = RecordingEngine(OcrEngine.PADDLEOCR)
        fallback = RecordingFallback()
        router, _ = _router([paddle], fallback)
        router.preload(("OCR", "TABLE_RECOGNITION", "FORMULA_RECOGNITION"))
        # OCR 走默认引擎，其余管道直达 fallback。
        assert paddle.preload_calls == [("OCR",)]
        assert fallback.preload_calls == [("TABLE_RECOGNITION", "FORMULA_RECOGNITION")]

    def test_preload_non_ocr_only_skips_engines(self) -> None:
        paddle = RecordingEngine(OcrEngine.PADDLEOCR)
        fallback = RecordingFallback()
        router, _ = _router([paddle], fallback)
        router.preload(("MinerU",))
        assert paddle.preload_calls == []
        assert fallback.preload_calls == []

    def test_residency_and_settings_delegate_to_fallback(self) -> None:
        from vibeocr.runtime_contracts import ResidencyStatus, SettingsSnapshot

        paddle = RecordingEngine(OcrEngine.PADDLEOCR)
        fallback = RecordingFallback()
        router, _ = _router([paddle], fallback)
        assert isinstance(router.residency_status(), ResidencyStatus)
        assert isinstance(router.release_idle("OCR"), ResidencyStatus)
        router.configure_settings(SettingsSnapshot())

    def test_close_closes_engines_and_fallback(self) -> None:
        windows = RecordingEngine(OcrEngine.WINDOWS)
        paddle = RecordingEngine(OcrEngine.PADDLEOCR)
        fallback = RecordingFallback()
        router, _ = _router([windows, paddle], fallback)
        _ = router.fallback
        router.close()
        assert windows.closed and paddle.closed and fallback.closed


class TestExecutorFailFast:
    def test_engine_error_fails_items_without_recovery(self, tmp_path) -> None:
        """AdapterExecutor 集成：引擎错误按协议码标 item，无恢复事件循环。"""

        def _staged_pngs(items: list[JobItem], base: Any) -> list[Any]:
            import io

            from PIL import Image
            from vibeocr.runtime.jobs.staging import StagedInput

            buffer = io.BytesIO()
            Image.new("RGB", (1, 1)).save(buffer, format="PNG")
            png = buffer.getvalue()
            staged = []
            for index, item in enumerate(items):
                path = base / f"f{index}.png"
                path.write_bytes(png)
                staged.append(
                    StagedInput(
                        item_id=item.item_id,
                        display_name=item.display_name,
                        path=path,
                        size_bytes=len(png),
                    )
                )
            return staged

        from vibeocr.runtime.jobs.registry import JobRegistry
        from vibeocr.runtime.recognition.paddle_executor import AdapterExecutor

        class _EngineErrorAdapter:
            def capabilities(self, options: Any | None = None) -> AdapterCapability:
                del options
                return AdapterCapability(
                    name="OCR", real_batch=False, max_compute_batch=1
                )

            def recognize_many(
                self,
                items: list[InputItem],
                *,
                options: Any | None = None,
                compute_batch: Any | None = None,
            ) -> list[dict[str, Any]]:
                raise OcrEngineError(
                    ErrorCode.OCR_ENGINE_UNAVAILABLE,
                    reason_code="engine_not_installed",
                    engine="paddleocr",
                )

            def residency_status(self) -> Any:
                from vibeocr.runtime_contracts import ResidencyStatus

                return ResidencyStatus()

            def release_idle(self, pipeline: str | None = None) -> Any:
                return self.residency_status()

        registry = JobRegistry(instance_id="t-router")
        record = registry.create(
            kind=JobKind.RECOGNITION,
            priority=JobPriority.INTERACTIVE,
            items=[
                JobItem(
                    item_id=f"it-{i}",
                    display_name=f"f{i}.png",
                    state=ItemState.QUEUED,
                )
                for i in range(2)
            ],
            progress_total=2,
            pipeline=PipelineSelection(pipeline_id="OCR", options={}),
        )
        record.transition(JobState.QUEUED)
        staged = _staged_pngs(record.items, tmp_path)
        executor = AdapterExecutor(adapter_factory=_EngineErrorAdapter, device="cpu:0")
        executor.execute(record, staged)
        errors = list(record.item_errors.values())
        assert errors == ["OCR_ENGINE_UNAVAILABLE", "OCR_ENGINE_UNAVAILABLE"]
        event_names = [event.stage for event in record.events]
        assert "ocr_engine_rejected" in event_names
        # 不进入恢复路径：没有 recovery_decision 事件。
        assert "recovery_decision" not in event_names

    def test_retired_rapid_table_pipeline_is_rejected_at_mode_gate(
        self, tmp_path
    ) -> None:
        """RAPID_TABLE 管道已随 RapidTable 引擎退役：枚举值保留供历史记录
        反序列化（parser 可解析），但模式门禁以 RECOGNITION_MODE_RETIRED
        明确拒绝，不再存在任何 rapid 执行路径。"""
        from vibeocr.runtime.recognition.recognition_modes import (
            RecognitionModeError,
            RecognitionModeRegistry,
        )
        from vibeocr.runtime_contracts import (
            parse_pipeline_selection,
            parse_submit_request,
        )

        selection = parse_pipeline_selection(
            {
                "pipeline_id": "RAPID_TABLE",
                "options_version": 1,
                "options": {"device": "cpu"},
            }
        )
        assert selection.pipeline_id == "RAPID_TABLE"
        registry = RecognitionModeRegistry()
        with pytest.raises(RecognitionModeError) as excinfo:
            registry.resolve_execution_fields(pipeline_id="RAPID_TABLE", engine=None)
        assert excinfo.value.code_name == "RECOGNITION_MODE_RETIRED"
        manifest = {
            "schema_version": 2,
            "request_id": "retired-rapid-table",
            "kind": "recognition",
            "priority": "interactive",
            "pipeline": {
                "pipeline_id": "RAPID_TABLE",
                "options_version": 1,
                "options": {"device": "cpu"},
            },
            "items": [
                {
                    "client_item_key": "k",
                    "ordinal": 0,
                    "display_name": "t.png",
                    "source": {"type": "upload.v1", "attachment": "f"},
                }
            ],
        }
        # manifest 可解析（历史形状），拒绝发生在模式门禁层。
        assert parse_submit_request(manifest).pipeline is not None

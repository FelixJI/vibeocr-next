"""MinerUExecutor: drive ``MINERU_PARSE`` jobs through MinerUProcessAdapter.

Shares the backend-agnostic job state machine with
:class:`~vibeocr.runtime.recognition.paddle_executor.AdapterExecutor`; only
the adapter type differs. The supervisor routes ``MINERU_PARSE`` jobs to this
executor via :class:`~vibeocr.runtime.recognition.composite_executor.CompositeExecutor`.
"""

from __future__ import annotations

from typing import TYPE_CHECKING, Any

if TYPE_CHECKING:
    from collections.abc import Callable

    from vibeocr.runtime.recognition.mineru_adapter import MinerUProcessAdapter

from vibeocr.runtime.jobs.budgets import InputItem
from vibeocr.runtime.recognition.paddle_executor import AdapterExecutor


class MinerUExecutor(AdapterExecutor):
    """Drives MinerU document-parse jobs through a MinerUProcessAdapter."""

    def __init__(
        self,
        adapter_factory: Callable[[], MinerUProcessAdapter],
        **coordinator_options: Any,
    ) -> None:
        super().__init__(adapter_factory, **coordinator_options)

    def _recognize_many(
        self, record: Any, items: list[InputItem], options: Any
    ) -> list[dict[str, Any]]:
        return self.adapter.recognize_many(
            items,
            options=options,
            cancelled=lambda: record.cancel_requested_at is not None,
        )

    def _recover_from_exception(
        self,
        record: Any,
        items: list[InputItem],
        *,
        options: Any,
        policy: Any,
        attempt: int,
        exc: Exception,
    ) -> None:
        from vibeocr.runtime.jobs.recovery import FailureClass

        failure = policy.classify(
            str(exc), cancelled=record.cancel_requested_at is not None
        )
        if failure is FailureClass.OOM:
            super()._recover_from_exception(
                record, items, options=options, policy=policy, attempt=attempt, exc=exc
            )
            return
        # A native parse may already have been submitted; transport failure is not retry permission.
        self._fail_items(
            record, items, error_code=self._error_code_for(failure), error=str(exc)
        )

    def _commit_payload(
        self, record: Any, item: InputItem, payload_type: str, payload: dict
    ) -> None:
        if "mineru_error" in payload:
            record.commit_item_failure(
                item.item_id,
                error_code="BACKEND_UNAVAILABLE",
                error=payload["mineru_error"],
            )
            return
        super()._commit_payload(record, item, payload_type, payload)

    def validate_settings(self, snapshot: Any) -> None:
        from vibeocr.runtime.recognition.mineru_readiness import connection_from_extra

        connection_from_extra(getattr(snapshot, "extra", None))
        super().validate_settings(snapshot)

    def configure_recovered_settings(self, snapshot: Any, *, connection_valid: bool):
        if connection_valid:
            return self.configure_settings(snapshot)
        from vibeocr.runtime.recognition.mineru_readiness import (
            CONNECTION_UNAVAILABLE,
            MineruConnection,
            configure_connection,
        )

        configure_connection(MineruConnection(mode=CONNECTION_UNAVAILABLE))
        self._settings = snapshot
        return self.residency_status()

    def configure_settings(self, snapshot: Any):
        """连接配置在共同位置生效，即使惰性 adapter 尚未构建。

        adapter 已构建时由 ``MinerUProcessAdapter.configure_settings`` 原子
        应用（含活动任务门禁）；这里只覆盖 adapter 不存在的窗口，保持
        mode/tier 目录与实际连接一致。非法配置在此 fail closed，
        ``update_settings`` 在验证和持久化成功后才应用配置。
        """
        if self._adapter is None:
            from vibeocr.runtime.recognition.mineru_readiness import (
                configure_connection,
                connection_from_extra,
            )

            configure_connection(
                connection_from_extra(getattr(snapshot, "extra", None))
            )
        return super().configure_settings(snapshot)

    @property
    def adapter(self) -> MinerUProcessAdapter:  # type: ignore[override]
        return super().adapter  # type: ignore[return-value]


__all__ = ["MinerUExecutor"]

"""Rapid 原生会话枚举与 CUDA 激活边界（无需 GPU）。"""

from __future__ import annotations

from types import SimpleNamespace

import pytest
from vibeocr.runtime.recognition.rapid_devices import activate_cuda_sessions


class _Session:
    def __init__(self, *, accepts_cuda: bool = True) -> None:
        self.providers = ["CPUExecutionProvider"]
        self.accepts_cuda = accepts_cuda

    def set_providers(self, providers: list[object]) -> None:
        assert providers == [
            ("CUDAExecutionProvider", {"device_id": "0"}),
            "CPUExecutionProvider",
        ]
        if self.accepts_cuda:
            self.providers = ["CUDAExecutionProvider", "CPUExecutionProvider"]

    def get_providers(self) -> list[str]:
        return self.providers


@pytest.mark.parametrize("with_table", [False, True])
def test_cuda_activation_covers_ocr_and_table_sessions_before_inference(
    with_table: bool,
) -> None:
    sessions = [_Session() for _ in range(3)]
    ocr = SimpleNamespace(
        **{
            name: SimpleNamespace(session=SimpleNamespace(session=session))
            for name, session in zip(
                ("text_det", "text_cls", "text_rec"), sessions, strict=True
            )
        }
    )
    engine = ocr
    if with_table:
        sessions.append(_Session())
        engine = SimpleNamespace(
            ocr_engine=ocr,
            table_structure=SimpleNamespace(
                session=SimpleNamespace(session=sessions[-1])
            ),
        )
    activate_cuda_sessions(engine)
    assert all(
        session.get_providers()[0] == "CUDAExecutionProvider" for session in sessions
    )


def test_cuda_activation_rejects_nested_ocr_cpu_fallback() -> None:
    engine = SimpleNamespace(
        table_structure=SimpleNamespace(session=SimpleNamespace(session=_Session())),
        ocr_engine=SimpleNamespace(
            text_rec=SimpleNamespace(
                session=SimpleNamespace(session=_Session(accepts_cuda=False))
            )
        ),
    )
    with pytest.raises(RuntimeError, match="CUDA session degraded to CPU"):
        activate_cuda_sessions(engine)


def test_cuda_activation_rejects_absent_sessions() -> None:
    with pytest.raises(RuntimeError, match="cuda activation found no ORT sessions"):
        activate_cuda_sessions(SimpleNamespace())

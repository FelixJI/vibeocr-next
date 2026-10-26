"""Bundled Paddle model pack build contract (frozen-listing integrity)."""

from __future__ import annotations

import zipfile
from pathlib import Path

import pytest

from scripts import build_runtime_pack as builder


def _fake_pinned_download(calls: list[tuple[str, str, str]]):
    def download(url: str, destination: Path, digest: str, _cancel) -> Path:
        calls.append((url, str(destination), digest))
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(f"payload:{url}".encode())
        return destination

    return download


def test_model_pack_build_revalidates_existing_cache_against_frozen_listing(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    """损坏/陈旧的模型缓存不得直接进入 pack：必须按冻结清单重新校验并替换。"""
    sources = {
        "ModelA": {
            "inference.onnx": (
                (
                    "https://www.modelscope.cn/models/o/r/resolve/rev/inference.onnx",
                    "a" * 64,
                    "1",
                ),
                (
                    "https://huggingface.co/o/r/resolve/rev/inference.onnx",
                    "a" * 64,
                    "1",
                ),
            )
        }
    }
    monkeypatch.setattr(builder, "_paddle_model_sources", lambda: sources)
    work = tmp_path / "work"
    stale = work / "ModelA" / "inference.onnx"
    stale.parent.mkdir(parents=True)
    stale.write_bytes(b"corrupt cached bytes")
    calls: list[tuple[str, str, str]] = []
    monkeypatch.setattr(builder, "pinned_download", _fake_pinned_download(calls))
    output = tmp_path / "models.zip"
    builder.build_paddle_model_pack(work_dir=work, output=output)
    # 损坏缓存必须被重新下载（两个镜像候选按序尝试，首个成功即止）。
    assert calls, "corrupt cache must be revalidated through the frozen listing"
    with zipfile.ZipFile(output) as archive:
        assert (
            archive.read("ModelA/inference.onnx") == b"payload:" + calls[0][0].encode()
        )


def test_model_pack_build_fails_closed_when_no_mirror_can_serve_model(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    sources = {
        "ModelA": {
            "inference.onnx": (
                ("https://mirror.invalid/a", "a" * 64, "1"),
                ("https://mirror.invalid/b", "a" * 64, "1"),
            )
        }
    }
    monkeypatch.setattr(builder, "_paddle_model_sources", lambda: sources)

    def failing_download(*_args) -> Path:
        raise ValueError("digest mismatch")

    monkeypatch.setattr(builder, "pinned_download", failing_download)
    with pytest.raises(RuntimeError, match="paddle model download failed"):
        builder.build_paddle_model_pack(
            work_dir=tmp_path / "work", output=tmp_path / "models.zip"
        )

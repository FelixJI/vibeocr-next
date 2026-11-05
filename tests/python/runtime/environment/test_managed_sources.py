"""#105 环境级来源配置：解析链、历史失败证据与启动投影的定向测试。

预构建运行时树（PR2）零安装后，来源面只剩 set_sources/解析链/启动投影
与历史记录的只读投影；安装计划的来源绑定与安装取消已随安装事务退役。
"""

from __future__ import annotations

import json
from pathlib import Path
from urllib.parse import urlsplit

import pytest
from test_runtime_installer import (
    _release,
)
from vibeocr.runtime.environments.managed_environments import (
    ManagedEnvironmentError,
    ManagedEnvironmentStore,
)
from vibeocr.runtime.environments.runtime_installer import main
from vibeocr.runtime.environments.runtime_lock import RuntimeStoreLock


def _open(tmp_path: Path, manifest: Path, component: Path, **kwargs):
    return ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        **kwargs,
    )


def _write_record(manager: ManagedEnvironmentStore, env_id: str, **overrides) -> dict:
    """直接写入注册表记录：不创建真实 venv，测试只关心配置解析。"""
    from vibeocr.runtime.environments.runtime_maintenance import _atomic_json

    with RuntimeStoreLock(manager._lock):
        data = manager._read()
        record = {
            "id": env_id,
            "name": f"env-{env_id[:4]}",
            "revision": 1,
            "kind": "venv",
            "path": str(Path(env_id) / "revisions" / "1"),
            "status": "empty",
            "python_version": manager.manifest.python.version,
            "abi": manager.manifest.python.abi,
            **overrides,
        }
        data["environments"][env_id] = record
        _atomic_json(manager._registry, data)
        return record


def _entry(entries: list[dict], kind: str) -> dict:
    return next(item for item in entries if item["kind"] == kind)


def _stub_installed_probe(
    manager: ManagedEnvironmentStore, monkeypatch: pytest.MonkeyPatch
) -> None:
    original = manager._probe

    def probe(record: dict) -> dict:
        if record["status"] == "installed":
            return {
                "healthy": True,
                "reason": None,
                "python": str(manager._venv_python(manager._safe_path(record))),
            }
        return original(record)

    monkeypatch.setattr(manager, "_probe", probe)


def test_source_resolution_chain_overrides_are_isolated(tmp_path: Path) -> None:
    manifest, component = _release(tmp_path / "release")
    manager = _open(tmp_path, manifest, component)
    a = _write_record(manager, "a" * 32)
    b = _write_record(manager, "b" * 32)

    listed = manager.list()
    assert listed["source_config_revision"] == 0
    assert listed["default_source_ids"] == []
    env_a = next(item for item in listed["environments"] if item["id"] == a["id"])
    assert _entry(env_a["resolved_sources"], "paddleocr_model_registry") == {
        "kind": "paddleocr_model_registry",
        "id": "paddleocr-modelscope",
        "display_name": "ModelScope",
        "origin": "product_default",
    }

    # 全局默认：共享 model→ModelScope；A/B 同时继承。
    manager.set_sources(None, "modelscope")
    listed = manager.list()
    assert listed["default_source_ids"] == [
        "paddleocr-modelscope",
        "mineru-modelscope",
    ]
    assert listed["source_config_revision"] == 1
    for item in listed["environments"]:
        assert _entry(item["resolved_sources"], "paddleocr_model_registry") == {
            "kind": "paddleocr_model_registry",
            "id": "paddleocr-modelscope",
            "display_name": "ModelScope",
            "origin": "global_default",
        }

    # 环境 A 只覆盖 PaddleOCR 引擎源：MinerU 继续继承全局；B 完全不动。
    manager.set_sources(a["id"], paddleocr_model_source_id="paddleocr-huggingface")
    listed = manager.list()
    env_a = next(item for item in listed["environments"] if item["id"] == a["id"])
    env_b = next(item for item in listed["environments"] if item["id"] == b["id"])
    assert env_a["override_source_ids"] == ["paddleocr-huggingface"]
    assert _entry(env_a["resolved_sources"], "paddleocr_model_registry") == {
        "kind": "paddleocr_model_registry",
        "id": "paddleocr-huggingface",
        "display_name": "Hugging Face",
        "origin": "environment_override",
    }
    assert (
        _entry(env_a["resolved_sources"], "mineru_model_registry")["origin"]
        == "global_default"
    )
    assert env_b["override_source_ids"] == []
    assert (
        _entry(env_b["resolved_sources"], "paddleocr_model_registry")["id"]
        == "paddleocr-modelscope"
    )
    assert listed["source_config_revision"] == 2

    # 清除环境 override → 回到全局默认。
    manager.set_sources(a["id"], None)
    env_a = next(
        item for item in manager.list()["environments"] if item["id"] == a["id"]
    )
    assert env_a["override_source_ids"] == []
    assert (
        _entry(env_a["resolved_sources"], "paddleocr_model_registry")["id"]
        == "paddleocr-modelscope"
    )

    # 未知 id 与错 kind 拒绝，不写入。
    with pytest.raises(ManagedEnvironmentError, match="unknown model_registry"):
        manager.set_sources(None, "not-a-source")
    with pytest.raises(
        ManagedEnvironmentError, match="unknown paddleocr_model_registry"
    ):
        manager.set_sources(None, paddleocr_model_source_id="mineru-modelscope")
    assert manager.list()["source_config_revision"] == 3


def test_saved_sources_survive_restart_and_legacy_schema_stays_readable(
    tmp_path: Path,
) -> None:
    manifest, component = _release(tmp_path / "release")
    manager = _open(tmp_path, manifest, component)
    a = _write_record(manager, "a" * 32)
    manager.set_sources(None, "huggingface")
    manager.set_sources(a["id"], "modelscope")

    reopened = _open(tmp_path, manifest, component)
    listed = reopened.list()
    assert listed["default_source_ids"] == [
        "paddleocr-huggingface",
        "mineru-huggingface",
    ]
    env_a = next(item for item in listed["environments"] if item["id"] == a["id"])
    assert env_a["override_source_ids"] == ["paddleocr-modelscope", "mineru-modelscope"]
    assert (
        _entry(env_a["resolved_sources"], "paddleocr_model_registry")["origin"]
        == "environment_override"
    )
    assert (
        _entry(env_a["resolved_sources"], "mineru_model_registry")["origin"]
        == "environment_override"
    )

    # 旧 #104 schema（无新键、失败记录无来源字段）兼容可读，不伪造来源。
    legacy_id = "c" * 32
    with RuntimeStoreLock(manager._lock):
        legacy = {
            "schema_version": 1,
            "active_id": None,
            "active_revision": 0,
            "environments": {
                legacy_id: {
                    "id": legacy_id,
                    "name": "legacy record",
                    "revision": 1,
                    "kind": "venv",
                    "path": str(Path(legacy_id) / "revisions" / "1"),
                    "status": "installed",
                    "recipe": "paddleocr-ort-cpu",
                    "source_ids": ["tuna-pypi"],
                    "python_version": manager.manifest.python.version,
                    "abi": manager.manifest.python.abi,
                    "last_install_operation": {
                        "environment_revision": 1,
                        "plan_id": "d" * 32,
                        "recipe": "paddleocr-ort-cpu",
                        "phase": "failed",
                        "reason_code": "io_error",
                        "next_action": "inspect_diagnostics",
                        "detail": "legacy detail",
                    },
                }
            },
        }
        manager._registry.write_text(json.dumps(legacy), encoding="utf-8")
    listed = manager.list()
    legacy_env = listed["environments"][0]
    assert legacy_env["source_ids"] == ["tuna-pypi"]
    assert "requested_source_ids" not in legacy_env["last_install_failure"]
    assert "effective_source_ids" not in legacy_env["last_install_failure"]
    # 历史 package 记录按“被忽略”读取：resolved 只投影模型源。
    assert [entry["kind"] for entry in legacy_env["resolved_sources"]] == [
        "paddleocr_model_registry",
        "mineru_model_registry",
    ]

    # 目录外的历史 id：标注未知、解析回退产品默认，不 fail closed。
    with RuntimeStoreLock(manager._lock):
        data = manager._read()
        data["default_source_ids"] = ["future-source"]
        data["environments"][legacy_id]["override_source_ids"] = ["gone-model"]
        manager._registry.write_text(json.dumps(data), encoding="utf-8")
    listed = manager.list()
    assert listed["unknown_default_source_ids"] == ["future-source"]
    legacy_env = listed["environments"][0]
    assert legacy_env["unknown_source_ids"] == ["gone-model"]
    assert (
        _entry(legacy_env["resolved_sources"], "paddleocr_model_registry")["id"]
        == "paddleocr-modelscope"
    )
    assert (
        _entry(legacy_env["resolved_sources"], "mineru_model_registry")["id"]
        == "mineru-modelscope"
    )


def test_launch_projects_model_source_environment(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manifest, component = _release(tmp_path / "release")
    manager = _open(tmp_path, manifest, component)
    a = _write_record(manager, "a" * 32, status="installed", recipe="paddleocr-ort-cpu")

    def healthy_probe(record: dict) -> dict:
        return {
            "healthy": True,
            "reason": None,
            "python": str(manager._venv_python(manager._safe_path(record))),
        }

    monkeypatch.setattr(manager, "_probe", healthy_probe)

    prepared = manager.prepare_switch(a["id"])
    assert prepared["launch"]["environment"]["PADDLE_PDX_MODEL_SOURCE"] == "modelscope"
    assert prepared["launch"]["environment"]["MINERU_MODEL_SOURCE"] == "modelscope"

    manager.set_sources(None, "modelscope")
    prepared = manager.prepare_switch(a["id"])
    assert prepared["launch"]["environment"]["PADDLE_PDX_MODEL_SOURCE"] == "modelscope"
    assert prepared["launch"]["environment"]["MINERU_MODEL_SOURCE"] == "modelscope"

    # prepare 与 commit 之间来源配置变化 → launch 变化 → 切换失效需重预览。
    manager.set_sources(None, "huggingface")
    with pytest.raises(ManagedEnvironmentError, match="changed before switch"):
        manager.commit_switch(prepared)
    assert manager.list()["active_id"] is None
    prepared = manager.prepare_switch(a["id"])
    assert prepared["launch"]["environment"]["MINERU_MODEL_SOURCE"] == "huggingface"


def test_set_sources_rejected_while_environment_operation_in_flight(
    tmp_path: Path,
) -> None:
    manifest, component = _release(tmp_path / "release")
    manager = _open(tmp_path, manifest, component)
    a = _write_record(manager, "a" * 32)
    b = _write_record(manager, "b" * 32)
    lock = RuntimeStoreLock(
        manager.paths.locks_root / "environment-operations" / f"{a['id']}.lock",
        timeout=0,
    )
    lock.acquire()
    try:
        with pytest.raises(ManagedEnvironmentError, match="in progress"):
            manager.set_sources(a["id"], "huggingface")
        # 全局默认与其它环境的写入不被 A 的在途操作阻塞。
        manager.set_sources(None, "modelscope")
        manager.set_sources(b["id"], "huggingface")
    finally:
        lock.release()
    listed = manager.list()
    assert listed["default_source_ids"] == [
        "paddleocr-modelscope",
        "mineru-modelscope",
    ]
    assert next(item for item in listed["environments"] if item["id"] == b["id"])[
        "override_source_ids"
    ] == ["paddleocr-huggingface", "mineru-huggingface"]
    assert (
        next(item for item in listed["environments"] if item["id"] == a["id"])[
            "override_source_ids"
        ]
        == []
    )


def test_sanitize_download_endpoint_strips_credentials_and_survives_bad_input() -> None:
    """端点脱敏：userinfo/query/fragment 去除；非法端口/IPv6 按契约处理。"""
    from vibeocr.runtime.environments.runtime_selection import (
        sanitize_download_endpoint,
    )

    assert (
        sanitize_download_endpoint(
            "https://user:secret@mirrors.example.org/pypi/simple/?sig=abc#f"
        )
        == "https://mirrors.example.org/pypi/simple/"
    )
    assert sanitize_download_endpoint("http://EXAMPLE.org:8080/x") == (
        "http://example.org:8080/x"
    )
    # IPv6：hostname 去括号后重建 netloc，保持合法方括号形式。
    assert sanitize_download_endpoint("https://[2001:db8::1]:8443/p") == (
        "https://[2001:db8::1]:8443/p"
    )
    # 契约：解析失败/非法端口/非 http(s) 一律返回空串（未知端点）。
    assert sanitize_download_endpoint("https://host.invalid:99999999999999/") == ""
    assert sanitize_download_endpoint("https://host.invalid:notaport/") == ""
    assert sanitize_download_endpoint("file:///C:/private") == ""
    assert sanitize_download_endpoint("not a url") == ""


def test_stdio_set_sources_round_trip(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    manifest, component = _release(tmp_path / "release")
    manager = _open(tmp_path, manifest, component)
    _write_record(manager, "a" * 32)
    request = {
        "protocol_version": 2,
        "request_kind": "environment",
        "action": "set_sources",
        "product_root": str(tmp_path / "product"),
        "component_lock": str(component),
        "runtime_manifest": str(manifest),
        "environment_id": None,
        "model_source_id": "huggingface",
    }
    assert main(["--request-json", json.dumps(request)]) == 0
    envelope = json.loads(capsys.readouterr().out)
    assert envelope["response_kind"] == "environment"
    assert envelope["action"] == "set_sources"
    assert envelope["result"]["default_source_ids"] == [
        "paddleocr-huggingface",
        "mineru-huggingface",
    ]
    assert any(
        source["id"] == "paddleocr-huggingface"
        and source["display_name"] == "Hugging Face"
        for source in envelope["result"]["sources"]
    )
    # 端点脱敏：host 保留，无 userinfo/query。
    paddle = next(
        source
        for source in envelope["result"]["sources"]
        if source["id"] == "paddleocr-modelscope"
    )
    endpoint = urlsplit(paddle["endpoint"])
    assert endpoint.scheme == "https"
    assert endpoint.hostname == "www.modelscope.cn"
    assert "?" not in paddle["endpoint"] and "@" not in paddle["endpoint"]


@pytest.mark.parametrize(
    "requested", [None, ["paddleocr-modelscope", "mineru-modelscope"]]
)
def test_interrupted_install_retains_frozen_sources_after_restart(
    tmp_path: Path, requested: list[str] | None
) -> None:
    manifest, component = _release(tmp_path / "release")
    manager = _open(tmp_path, manifest, component)
    frozen = ["paddleocr-modelscope", "mineru-modelscope"]
    record = _write_record(
        manager,
        "a" * 32,
        last_install_operation={
            "environment_revision": 1,
            "plan_id": "b" * 32,
            "recipe": "paddleocr-ort-directml",
            "phase": "installing",
            "reason_code": "",
            "next_action": "",
            "detail": "",
            "requested_source_ids": requested,
            "effective_source_ids": frozen,
        },
    )
    # A stopped installer leaves its journal in installing; new preferences
    # must not rewrite the source evidence when a restarted reader recovers it.
    manager.set_sources(None, "huggingface")
    restarted = _open(tmp_path, manifest, component)
    failure = restarted.list()["environments"][0]["last_install_failure"]
    assert failure["phase"] == "failed"
    assert failure["reason_code"] == "install_interrupted"
    assert failure["requested_source_ids"] == requested
    assert failure["effective_source_ids"] == frozen
    with manager._target_operation(record["id"]):
        running = restarted.list()["environments"][0]["last_install_failure"]
        assert running["reason_code"] == "install_in_progress"
        assert running["requested_source_ids"] == requested
        assert running["effective_source_ids"] == frozen


def test_engine_sources_migrate_legacy_and_clear_independently(tmp_path: Path) -> None:
    manifest, component = _release(tmp_path / "release")
    manager = _open(tmp_path, manifest, component)
    record = _write_record(manager, "a" * 32, override_source_ids=["modelscope"])
    with RuntimeStoreLock(manager._lock):
        data = manager._read()
        data["default_source_ids"] = ["pypi", "huggingface"]
        manager._registry.write_text(json.dumps(data), encoding="utf-8")
    env = manager.list()["environments"][0]
    assert env["override_source_ids"] == ["paddleocr-modelscope", "mineru-modelscope"]
    manager.set_sources(record["id"], paddleocr_model_source_id="paddleocr-bos")
    env = manager.list()["environments"][0]
    assert (
        _entry(env["resolved_sources"], "paddleocr_model_registry")["id"]
        == "paddleocr-bos"
    )
    # 单引擎提交不清除另一引擎 override：MinerU 保持环境覆盖。
    assert (
        _entry(env["resolved_sources"], "mineru_model_registry")["id"]
        == "mineru-modelscope"
    )
    data = manager._read()
    assert manager._plan_selection(
        data, data["environments"][record["id"]], "cpu", None
    ).model_source_environment() == {
        "PADDLE_PDX_MODEL_SOURCE": "bos",
        "MINERU_MODEL_SOURCE": "modelscope",
    }
    manager.set_sources(record["id"], paddleocr_model_source_id=None)
    env = manager.list()["environments"][0]
    assert env["override_source_ids"] == ["mineru-modelscope"]
    assert (
        _entry(env["resolved_sources"], "paddleocr_model_registry")["id"]
        == "paddleocr-huggingface"
    )
    assert (
        _entry(env["resolved_sources"], "mineru_model_registry")["id"]
        == "mineru-modelscope"
    )
    assert manager.list()["default_source_ids"] == [
        "paddleocr-huggingface",
        "mineru-huggingface",
    ]


def test_engine_source_kind_mismatch_does_not_write(tmp_path: Path) -> None:
    manifest, component = _release(tmp_path / "release")
    manager = _open(tmp_path, manifest, component)
    with pytest.raises(
        ManagedEnvironmentError, match="unknown paddleocr_model_registry"
    ):
        manager.set_sources(None, paddleocr_model_source_id="mineru-modelscope")
    assert manager.list()["source_config_revision"] == 0
    assert manager.list()["default_source_ids"] == []


def test_stdio_independent_engine_sources_preserve_absent_fields(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    manifest, component = _release(tmp_path / "release")
    request = {
        "protocol_version": 2,
        "request_kind": "environment",
        "action": "set_sources",
        "product_root": str(tmp_path / "product"),
        "component_lock": str(component),
        "runtime_manifest": str(manifest),
        "environment_id": None,
        "paddleocr_model_source_id": "paddleocr-bos",
        "mineru_model_source_id": "mineru-modelscope",
    }
    assert main(["--request-json", json.dumps(request)]) == 0
    result = json.loads(capsys.readouterr().out)["result"]
    assert result["default_source_ids"] == [
        "paddleocr-bos",
        "mineru-modelscope",
    ]
    request["paddleocr_model_source_id"] = None
    del request["mineru_model_source_id"]
    assert main(["--request-json", json.dumps(request)]) == 0
    result = json.loads(capsys.readouterr().out)["result"]
    assert result["default_source_ids"] == ["mineru-modelscope"]
    assert (
        _entry(result["resolved_default_sources"], "paddleocr_model_registry")["id"]
        == "paddleocr-modelscope"
    )
    assert (
        _entry(result["resolved_default_sources"], "mineru_model_registry")["id"]
        == "mineru-modelscope"
    )
    assert {source["id"] for source in result["sources"] if source["is_default"]} == {
        "paddleocr-modelscope",
        "mineru-modelscope",
    }


@pytest.mark.parametrize("accepted", [True, False])
def test_cancel_control_uses_unbuffered_process_pipes(
    monkeypatch: pytest.MonkeyPatch, accepted: bool
) -> None:
    import os
    from types import SimpleNamespace

    from vibeocr.runtime.environments import runtime_installer

    incoming_read, incoming_write = os.pipe()
    outgoing_read, outgoing_write = os.pipe()
    with (
        os.fdopen(incoming_read, "rb") as stdin,
        os.fdopen(incoming_write, "wb") as writer,
        os.fdopen(outgoing_read, "rb") as reader,
        os.fdopen(outgoing_write, "wb") as stdout,
    ):
        monkeypatch.setattr(
            runtime_installer, "sys", SimpleNamespace(stdin=stdin, stdout=stdout)
        )
        writer.write(b"cancel\r\n")
        writer.flush()
        # listener 只接零参回调；receipt emitter 由各自协议的 typed wrapper
        # 闭包携带（与 main() 中环境/维护两种接线一致）。
        runtime_installer._listen_environment_cancel(
            lambda: runtime_installer._environment_cancel_receipt(accepted)
        )
        assert json.loads(reader.readline()) == {
            "environment_cancel": "accepted" if accepted else "rejected"
        }


@pytest.mark.parametrize(
    "source_id,provider",
    [
        ("paddleocr-modelscope", "modelscope"),
        ("mineru-modelscope", "modelscope"),
        ("paddleocr-huggingface", "huggingface"),
        ("mineru-huggingface", "huggingface"),
        ("tuna-pypi", None),
    ],
)
def test_model_source_provider_is_runtime_owned(source_id, provider):
    from vibeocr.runtime.environments.runtime_selection import download_source_provider

    assert download_source_provider(source_id) == provider

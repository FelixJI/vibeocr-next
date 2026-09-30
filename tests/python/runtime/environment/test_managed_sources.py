"""#105 环境级来源配置：解析链、计划绑定、失败证据与启动投影的定向测试。"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path
from urllib.parse import urlsplit

import pytest
from test_runtime_installer import (
    _omit_unused_pip_bootstrap,
    _release,
)
from vibeocr.runtime.environments.managed_environments import (
    ManagedEnvironmentError,
    ManagedEnvironmentStore,
)
from vibeocr.runtime.environments.runtime_install_plan import (
    RuntimeInstallPlanStale,
)
from vibeocr.runtime.environments.runtime_installer import main
from vibeocr.runtime.environments.runtime_lock import RuntimeStoreLock


def _open(tmp_path: Path, manifest: Path, component: Path, **kwargs):
    return ManagedEnvironmentStore(
        product_root=tmp_path / "product",
        component_lock=component,
        runtime_manifest=manifest,
        base_python=sys._base_executable,
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
    assert _entry(env_a["resolved_sources"], "package_index") == {
        "kind": "package_index",
        "id": "tuna-pypi",
        "display_name": "TUNA PyPI 镜像",
        "origin": "product_default",
    }
    assert _entry(env_a["resolved_sources"], "model_registry") == {
        "kind": "model_registry",
        "id": None,
        "display_name": None,
        "origin": "product_default",
    }

    # 全局默认：package→PyPI，model→ModelScope；A/B 同时继承。
    manager.set_sources(None, "pypi", "modelscope")
    listed = manager.list()
    assert listed["default_source_ids"] == ["pypi", "modelscope"]
    assert listed["source_config_revision"] == 1
    for item in listed["environments"]:
        assert _entry(item["resolved_sources"], "package_index") == {
            "kind": "package_index",
            "id": "pypi",
            "display_name": "PyPI 官方源",
            "origin": "global_default",
        }
        assert _entry(item["resolved_sources"], "model_registry") == {
            "kind": "model_registry",
            "id": "modelscope",
            "display_name": "ModelScope",
            "origin": "global_default",
        }

    # 环境 A 只覆盖 package：model 继续继承全局；B 完全不动。
    manager.set_sources(a["id"], "tuna-pypi", None)
    listed = manager.list()
    env_a = next(item for item in listed["environments"] if item["id"] == a["id"])
    env_b = next(item for item in listed["environments"] if item["id"] == b["id"])
    assert env_a["override_source_ids"] == ["tuna-pypi"]
    assert _entry(env_a["resolved_sources"], "package_index") == {
        "kind": "package_index",
        "id": "tuna-pypi",
        "display_name": "TUNA PyPI 镜像",
        "origin": "environment_override",
    }
    assert (
        _entry(env_a["resolved_sources"], "model_registry")["origin"]
        == "global_default"
    )
    assert env_b["override_source_ids"] == []
    assert _entry(env_b["resolved_sources"], "package_index")["id"] == "pypi"
    assert listed["source_config_revision"] == 2

    # 清除环境 override → 回到全局默认。
    manager.set_sources(a["id"], None, None)
    env_a = next(
        item for item in manager.list()["environments"] if item["id"] == a["id"]
    )
    assert env_a["override_source_ids"] == []
    assert _entry(env_a["resolved_sources"], "package_index")["id"] == "pypi"

    # 未知 id 与错 kind 拒绝，不写入。
    with pytest.raises(ManagedEnvironmentError, match="unknown package_index"):
        manager.set_sources(None, "not-a-source", None)
    with pytest.raises(ManagedEnvironmentError, match="unknown model_registry"):
        manager.set_sources(None, None, "tuna-pypi")
    assert manager.list()["source_config_revision"] == 3


def test_saved_sources_survive_restart_and_legacy_schema_stays_readable(
    tmp_path: Path,
) -> None:
    manifest, component = _release(tmp_path / "release")
    manager = _open(tmp_path, manifest, component)
    a = _write_record(manager, "a" * 32)
    manager.set_sources(None, "pypi", "huggingface")
    manager.set_sources(a["id"], None, "modelscope")

    reopened = _open(tmp_path, manifest, component)
    listed = reopened.list()
    assert listed["default_source_ids"] == ["pypi", "huggingface"]
    env_a = next(item for item in listed["environments"] if item["id"] == a["id"])
    assert env_a["override_source_ids"] == ["modelscope"]
    assert (
        _entry(env_a["resolved_sources"], "model_registry")["origin"]
        == "environment_override"
    )
    assert _entry(env_a["resolved_sources"], "package_index")["id"] == "pypi"

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
                    "recipe": "rapidocr-cpu",
                    "source_ids": ["tuna-pypi"],
                    "python_version": manager.manifest.python.version,
                    "abi": manager.manifest.python.abi,
                    "last_install_operation": {
                        "environment_revision": 1,
                        "plan_id": "d" * 32,
                        "recipe": "rapidocr-cpu",
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
    assert _entry(legacy_env["resolved_sources"], "package_index")["id"] == "tuna-pypi"

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
    assert _entry(legacy_env["resolved_sources"], "package_index")["id"] == "tuna-pypi"
    assert _entry(legacy_env["resolved_sources"], "model_registry")["id"] is None


def test_preview_binds_requested_effective_and_config_revision(
    tmp_path: Path,
) -> None:
    manifest, component = _release(tmp_path / "release")
    manager = _open(tmp_path, manifest, component)
    a = _write_record(manager, "a" * 32)

    inherited = manager.preview_install(a["id"], "rapidocr-cpu")
    assert inherited["requested_source_ids"] is None
    assert inherited["source_ids"] == ["tuna-pypi"]
    assert inherited["effective_source_ids"] == ["tuna-pypi"]
    assert inherited["source_config_revision"] == 0
    assert inherited["python_origin"] == "product_bundle"
    assert inherited["runtime_wheel_origin"] == "product_bundle"
    assert inherited["dependency_origin"] == "online_index"
    assert _entry(inherited["sources"], "package_index") == {
        "id": "tuna-pypi",
        "kind": "package_index",
        "display_name": "TUNA PyPI 镜像",
        "endpoint": "https://mirrors.tuna.tsinghua.edu.cn/pypi/web/simple/",
        "requested": False,
        "inherited_from": "product_default",
        "usage": "online_index",
    }

    explicit = manager.preview_install(a["id"], "rapidocr-cpu", ("pypi",))
    assert explicit["requested_source_ids"] == ["pypi"]
    assert explicit["source_ids"] == ["pypi"]
    assert _entry(explicit["sources"], "package_index")["requested"] is True

    # 全局默认参与叠加：显式选模型源时 package 继承全局默认。
    manager.set_sources(None, "pypi", None)
    combined = manager.preview_install(a["id"], "rapidocr-cpu", ("modelscope",))
    assert combined["requested_source_ids"] == ["modelscope"]
    assert combined["source_ids"] == ["pypi", "modelscope"]
    assert _entry(combined["sources"], "model_registry") == {
        "id": "modelscope",
        "kind": "model_registry",
        "display_name": "ModelScope",
        "endpoint": "https://www.modelscope.cn",
        "requested": True,
        "inherited_from": "product_default",
        "usage": "model_preference",
        "actual_endpoint": None,
    }
    assert _entry(combined["sources"], "package_index")["inherited_from"] == (
        "global_default"
    )

    # 来源配置变化 → 旧计划 confirm 时 stale；重新预览拿到新修订。
    with pytest.raises(RuntimeInstallPlanStale, match="preview again"):
        manager.install(explicit["plan_id"], a["id"], "rapidocr-cpu", ("pypi",))
    refreshed = manager.preview_install(a["id"], "rapidocr-cpu", ("pypi",))
    assert refreshed["source_config_revision"] == 1


def test_install_freezes_sources_and_failure_evidence_is_durable(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manifest, component = _release(tmp_path / "release")
    attempts: list[str] = []

    def flaky_install(_python: Path, _scope, endpoint: str) -> None:
        attempts.append(endpoint)
        if len(attempts) == 1:
            raise ManagedEnvironmentError("synthetic download failure")

    manager = _open(tmp_path, manifest, component, install_runner=flaky_install)
    _stub_installed_probe(manager, monkeypatch)
    _omit_unused_pip_bootstrap(monkeypatch)
    a = _write_record(manager, "a" * 32)
    plan = manager.preview_install(a["id"], "rapidocr-cpu", ("pypi",))
    with pytest.raises(ManagedEnvironmentError, match="synthetic download failure"):
        manager.install(plan["plan_id"], a["id"], "rapidocr-cpu", ("pypi",))

    # 失败终态冻结请求/生效来源，重启（重建 store）后仍可查。
    failure = next(
        item
        for item in _open(tmp_path, manifest, component).list()["environments"]
        if item["id"] == a["id"]
    )["last_install_failure"]
    assert failure["phase"] == "failed"
    assert failure["requested_source_ids"] == ["pypi"]
    assert failure["effective_source_ids"] == ["pypi"]

    # 后续新预览不冲掉旧来源证据。
    manager.preview_install(a["id"], "rapidocr-cpu", ("pypi",))
    assert (
        next(item for item in manager.list()["environments"] if item["id"] == a["id"])[
            "last_install_failure"
        ]
        == failure
    )
    # 旧 plan 已被新预览取代 → confirm 旧计划 stale（重新预览是显式动作）。
    with pytest.raises(RuntimeInstallPlanStale):
        manager.install(plan["plan_id"], a["id"], "rapidocr-cpu", ("pypi",))

    # 未重新预览时同计划 retry 保留原意图：同来源、同计划成功并留下证据。
    retryable = _open(tmp_path, manifest, component, install_runner=flaky_install)
    _stub_installed_probe(retryable, monkeypatch)
    retry_plan = retryable.preview_install(a["id"], "rapidocr-cpu", ("pypi",))
    installed = retryable.install(
        retry_plan["plan_id"], a["id"], "rapidocr-cpu", ("pypi",)
    )
    assert installed["status"] == "installed"
    assert installed["source_ids"] == ["pypi"]
    assert attempts == ["https://pypi.org/simple"] * 2
    assert (
        next(
            item for item in retryable.list()["environments"] if item["id"] == a["id"]
        )["last_install_failure"]
        is None
    )


def test_inherited_install_uses_resolved_defaults(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manifest, component = _release(tmp_path / "release")
    endpoints: list[str] = []

    def record_install(_python: Path, _scope, endpoint: str) -> None:
        endpoints.append(endpoint)

    manager = _open(tmp_path, manifest, component, install_runner=record_install)
    _stub_installed_probe(manager, monkeypatch)
    _omit_unused_pip_bootstrap(monkeypatch)
    a = _write_record(manager, "a" * 32)
    manager.set_sources(None, "pypi", None)
    manager.set_sources(a["id"], "tuna-pypi", None)
    plan = manager.preview_install(a["id"], "rapidocr-cpu")  # 继承环境配置
    assert plan["requested_source_ids"] is None
    assert plan["source_ids"] == ["tuna-pypi"]
    installed = manager.install(plan["plan_id"], a["id"], "rapidocr-cpu")
    assert installed["source_ids"] == ["tuna-pypi"]
    assert endpoints == ["https://mirrors.tuna.tsinghua.edu.cn/pypi/web/simple/"]


def test_launch_projects_model_source_environment(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    manifest, component = _release(tmp_path / "release")
    manager = _open(tmp_path, manifest, component)
    a = _write_record(manager, "a" * 32, status="installed", recipe="rapidocr-cpu")

    def healthy_probe(record: dict) -> dict:
        return {
            "healthy": True,
            "reason": None,
            "python": str(manager._venv_python(manager._safe_path(record))),
        }

    monkeypatch.setattr(manager, "_probe", healthy_probe)

    prepared = manager.prepare_switch(a["id"])
    assert "PADDLE_PDX_MODEL_SOURCE" not in prepared["launch"]["environment"]
    assert "MINERU_MODEL_SOURCE" not in prepared["launch"]["environment"]

    manager.set_sources(None, None, "modelscope")
    prepared = manager.prepare_switch(a["id"])
    assert prepared["launch"]["environment"]["PADDLE_PDX_MODEL_SOURCE"] == "modelscope"
    assert prepared["launch"]["environment"]["MINERU_MODEL_SOURCE"] == "modelscope"

    # prepare 与 commit 之间来源配置变化 → launch 变化 → 切换失效需重预览。
    manager.set_sources(None, None, "huggingface")
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
            manager.set_sources(a["id"], "pypi", None)
        # 全局默认与其它环境的写入不被 A 的在途操作阻塞。
        manager.set_sources(None, "pypi", None)
        manager.set_sources(b["id"], "pypi", None)
    finally:
        lock.release()
    listed = manager.list()
    assert listed["default_source_ids"] == ["pypi"]
    assert next(item for item in listed["environments"] if item["id"] == b["id"])[
        "override_source_ids"
    ] == ["pypi"]
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


def test_stdio_set_sources_and_inherited_preview_round_trip(
    tmp_path: Path, capsys: pytest.CaptureFixture[str]
) -> None:
    manifest, component = _release(tmp_path / "release")
    manager = _open(tmp_path, manifest, component)
    a = _write_record(manager, "a" * 32)
    base = {
        "product_root": str(tmp_path / "product"),
        "component_lock": str(component),
        "runtime_manifest": str(manifest),
    }
    request = {
        "protocol_version": 2,
        "request_kind": "environment",
        "action": "set_sources",
        **base,
        "environment_id": None,
        "package_source_id": "pypi",
        "model_source_id": None,
    }
    assert main(["--request-json", json.dumps(request)]) == 0
    envelope = json.loads(capsys.readouterr().out)
    assert envelope["response_kind"] == "environment"
    assert envelope["action"] == "set_sources"
    assert envelope["result"]["default_source_ids"] == ["pypi"]
    assert any(
        source["id"] == "pypi" and source["display_name"] == "PyPI 官方源"
        for source in envelope["result"]["sources"]
    )
    # 端点脱敏：host 保留，无 userinfo/query。
    tuna = next(
        source
        for source in envelope["result"]["sources"]
        if source["id"] == "tuna-pypi"
    )
    endpoint = urlsplit(tuna["endpoint"])
    assert endpoint.scheme == "https"
    assert endpoint.hostname == "mirrors.tuna.tsinghua.edu.cn"
    assert "?" not in tuna["endpoint"] and "@" not in tuna["endpoint"]

    preview = {
        "protocol_version": 2,
        "request_kind": "environment",
        "action": "preview_install",
        **base,
        "environment_id": a["id"],
        "recipe": "rapidocr-cpu",
    }
    assert main(["--request-json", json.dumps(preview)]) == 0
    payload = json.loads(capsys.readouterr().out)["result"]
    assert payload["requested_source_ids"] is None
    assert payload["source_ids"] == ["pypi"]
    assert payload["sources"][0]["inherited_from"] == "global_default"
    assert re.fullmatch(r"[0-9a-f]{32}", payload["plan_id"])


@pytest.mark.parametrize("requested", [None, ["pypi"]])
def test_interrupted_install_retains_frozen_sources_after_restart(
    tmp_path: Path, requested: list[str] | None
) -> None:
    manifest, component = _release(tmp_path / "release")
    manager = _open(tmp_path, manifest, component)
    record = _write_record(
        manager,
        "a" * 32,
        last_install_operation={
            "environment_revision": 1,
            "plan_id": "b" * 32,
            "recipe": "rapidocr-cpu",
            "phase": "installing",
            "reason_code": "",
            "next_action": "",
            "detail": "",
            "requested_source_ids": requested,
            "effective_source_ids": ["pypi"],
        },
    )
    # A stopped installer leaves its journal in installing; new preferences
    # must not rewrite the source evidence when a restarted reader recovers it.
    manager.set_sources(None, "tuna-pypi", None)
    restarted = _open(tmp_path, manifest, component)
    failure = restarted.list()["environments"][0]["last_install_failure"]
    assert failure["phase"] == "failed"
    assert failure["reason_code"] == "install_interrupted"
    assert failure["requested_source_ids"] == requested
    assert failure["effective_source_ids"] == ["pypi"]
    with manager._target_operation(record["id"]):
        running = restarted.list()["environments"][0]["last_install_failure"]
        assert running["reason_code"] == "install_in_progress"
        assert running["requested_source_ids"] == requested
        assert running["effective_source_ids"] == ["pypi"]


@pytest.mark.parametrize("returncode", [1, -1])
def test_venv_failure_retains_sanitized_diagnostics(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, returncode: int
) -> None:
    import subprocess

    manifest, component = _release(tmp_path / "release")
    manager = _open(tmp_path, manifest, component)
    record = _write_record(manager, "a" * 32)
    plan = manager.preview_install(record["id"], "rapidocr-cpu")
    diagnostic = (
        "x" * 5000 + "\nvenv failed\nC:\\private\\fixture\npassword=fixture-secret"
    )

    def failed_venv(args, **kwargs):
        assert "venv" in args
        return subprocess.CompletedProcess(args, returncode, "", diagnostic)

    monkeypatch.setattr(subprocess, "run", failed_venv)
    with pytest.raises(ManagedEnvironmentError, match=f"exit {returncode}"):
        manager.install(plan["plan_id"], record["id"], "rapidocr-cpu")
    persisted = manager._read()["environments"][record["id"]]
    failure = persisted["last_install_operation"]
    assert failure["reason_code"] == "venv_creation_failed"
    assert "venv failed" in failure["detail"]
    assert f"exit {returncode}" in failure["detail"]
    assert len(failure["detail"]) <= 4000
    assert "private" not in failure["detail"]
    assert "fixture-secret" not in failure["detail"]
    assert persisted["revision"] == record["revision"]
    assert persisted["path"] == record["path"]

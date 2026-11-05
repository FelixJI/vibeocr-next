"""Unified recipe uses the same guarded switch and isolated launch contract."""

from __future__ import annotations

import json
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import pytest
from test_default_environment import _manager
from test_runtime_installer import _materialize_venv, _write_venv_record
from vibeocr.runtime.environments.managed_environments import (
    ManagedEnvironmentError,
)


@pytest.mark.parametrize("recipe", ["paddleocr-ort-directml"])
def test_recipe_switch_checks_health_and_can_return_to_empty(
    tmp_path, monkeypatch, recipe
):
    # Engine packages/hardware are covered by profile smoke; this exercises the
    # real reservation, launch projection, HTTP health check and selection CAS.
    # 统一推理闭包后 directml 是唯一保留配方；分设备/独立 MinerU 配方记录
    # 由 engine_recipe_retired 只读投影覆盖，不再是可切换目标。
    manager = _manager(tmp_path, monkeypatch)
    empty = _write_venv_record(manager, "空环境")
    _materialize_venv(manager, "空环境", bare=True)
    manager.commit_switch(manager.prepare_switch(empty["id"]))
    target = _write_venv_record(manager, recipe, status="installed", recipe=recipe)
    target_path = str(manager.root / target["id"])

    class Health(BaseHTTPRequestHandler):
        def do_GET(self):
            assert self.path == "/v2/health"
            self.send_response(200)
            self.end_headers()
            self.wfile.write(
                json.dumps(
                    {
                        "schema_version": 2,
                        "protocol_version": 2,
                        "ready": True,
                        "draining": False,
                        "instance_id": "matrix-instance",
                    }
                ).encode()
            )

        def log_message(self, *_args):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Health)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        with manager.switch_reservation(target["id"]) as prepared:
            launch = prepared["launch"]
            env = launch["environment"]
            assert env["VIBEOCR_MANAGED_ENVIRONMENT_RECIPE"] == recipe
            assert env["VIBEOCR_RUNTIME_ROOT"] == target_path
            assert env["VIBEOCR_SHARED_MODEL_CACHE"] == str(
                manager.paths.state_root / "model-cache"
            )
            evidence = {"port": server.server_port, "instance_id": "wrong-instance"}
            with pytest.raises(ManagedEnvironmentError, match="not healthy"):
                manager.commit_switch(prepared, started_health=evidence)
            assert manager._read()["active_id"] == empty["id"]
            evidence["instance_id"] = "matrix-instance"
            assert (
                manager.commit_switch(prepared, started_health=evidence)["active_id"]
                == target["id"]
            )
        with manager.switch_reservation(empty["id"]) as prepared:
            assert prepared["launch"] is None
            assert not prepared["requires_supervisor"]
            assert manager.commit_switch(prepared)["active_id"] == empty["id"]
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=5)

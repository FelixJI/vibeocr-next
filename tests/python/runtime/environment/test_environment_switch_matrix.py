"""Each supported recipe uses the same guarded switch and isolated launch contract."""

from __future__ import annotations

import json
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import pytest
from test_default_environment import _manager
from vibeocr.runtime.environments.managed_environments import ManagedEnvironmentError


@pytest.mark.parametrize(
    "recipe",
    [
        "paddleocr-ort-cpu",
        "paddleocr-ort-cuda",
        "paddleocr-ort-directml",
        "mineru-cpu",
        "mineru-cuda",
    ],
)
def test_recipe_switch_checks_health_and_can_return_to_empty(
    tmp_path, monkeypatch, recipe
):
    # Engine packages/hardware are covered by profile smoke; this exercises the
    # real reservation, launch projection, HTTP health check and selection CAS.
    manager, _ = _manager(tmp_path, monkeypatch)
    empty = manager.create("空环境")
    manager.commit_switch(manager.prepare_switch(empty["id"]))
    target = manager.create(recipe)
    registry = manager._read()
    registry["environments"][target["id"]].update(status="installed", recipe=recipe)
    manager._registry.write_text(json.dumps(registry), encoding="utf-8")

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
            assert env["VIBEOCR_RUNTIME_ROOT"] == target["path"]
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

"""Shared model download contracts; all state and HTTP payloads are synthetic."""

from __future__ import annotations

import errno
import hashlib
import json
import os
import shutil
import socket
import subprocess
import sys
import threading
import types
from concurrent.futures import ThreadPoolExecutor
from functools import partial
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlsplit

import pytest
from vibeocr.runtime.environments import model_cache as cache


@pytest.fixture
def provider_server():
    # This fixture is the upstream checksum producer, not a local identity hash.
    payloads = {"config.json": b'{"fixture":true}', "weights.bin": b"synthetic-model"}
    counts = {name: 0 for name in payloads}
    commit = "a" * 40

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *_args):
            pass

        def do_HEAD(self):
            self.respond(head=True)

        def do_GET(self):
            self.respond(head=False)

        def respond(self, *, head):
            parsed = urlsplit(self.path)
            path = parsed.path
            name = None
            if "/resolve/" in path:
                name = path.split("/resolve/", 1)[1].split("/", 1)[1]
            elif path.endswith("/repo"):
                name = parse_qs(parsed.query)["FilePath"][0]
            if name in payloads:
                data = payloads[name]
                self.send_response(200)
                self.send_header("Content-Length", str(len(data)))
                self.send_header("ETag", hashlib.sha256(data).hexdigest())
                self.send_header("X-Repo-Commit", commit)
                self.end_headers()
                if not head:
                    counts[name] = counts.get(name, 0) + len(data)
                    self.wfile.write(data)
                return
            if path.startswith("/api/models/") and "/tree/" not in path:
                value = {"id": path.removeprefix("/api/models/"), "sha": commit}
            elif path.startswith("/api/models/") and "/tree/" in path:
                value = [
                    {
                        "type": "file",
                        "path": name,
                        "size": len(data),
                        "oid": "b" * 40,
                        "lfs": {
                            "oid": hashlib.sha256(data).hexdigest(),
                            "size": len(data),
                            "pointerSize": 128,
                        },
                    }
                    for name, data in payloads.items()
                ]
            elif path.startswith("/api/v1/models/") and path.endswith("/repo/files"):
                value = {
                    "Code": 200,
                    "Success": True,
                    "Data": {
                        "Files": [
                            {
                                "Path": name,
                                "Size": len(data),
                                "Type": "blob",
                                "Sha256": hashlib.sha256(data).hexdigest(),
                            }
                            for name, data in payloads.items()
                        ]
                    },
                }
            else:
                self.send_error(404)
                return
            data = json.dumps(value).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            if not head:
                self.wfile.write(data)

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield f"http://127.0.0.1:{server.server_port}", payloads, counts
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=5)


def _native_provider(monkeypatch, tmp_path, source, endpoint):
    """Keep the real SDK transport; change only its supported endpoint/config."""
    if source == "huggingface":
        sdk = pytest.importorskip("huggingface_hub")
        monkeypatch.setattr(
            sdk, "HfApi", partial(sdk.HfApi, endpoint=endpoint, token=False)
        )
        monkeypatch.setattr(
            sdk,
            "hf_hub_download",
            partial(sdk.hf_hub_download, endpoint=endpoint, token=False),
        )
    else:
        sdk = pytest.importorskip("modelscope_hub")
        config = sdk.HubConfig(
            endpoint=endpoint,
            token="",
            config_dir=tmp_path / "private-credentials",
            cache_dir=tmp_path / "native-cache",
        )
        monkeypatch.setattr(sdk, "HubApi", partial(sdk.HubApi, config=config))


@pytest.mark.parametrize("source", ["huggingface", "modelscope"])
def test_native_download_reuse_corruption_and_offline(
    tmp_path, monkeypatch, provider_server, source
):
    endpoint, payloads, counts = provider_server
    _native_provider(monkeypatch, tmp_path, source, endpoint)
    shared = tmp_path / "shared"
    asset = cache.ModelAsset("engine-1", source, "org/model")
    first = cache.prepare_models([asset], tmp_path / "one", shared_root=shared)
    assert counts == {name: len(data) for name, data in payloads.items()}
    assert first.downloaded_bytes is None  # SDK progress is not wire-byte evidence.
    second = cache.prepare_models([asset], tmp_path / "two", shared_root=shared)
    assert counts == {name: len(data) for name, data in payloads.items()}
    assert second.downloaded_bytes == 0
    assert first.root != second.root
    assert not (first.root / "weights.bin").samefile(second.root / "weights.bin")
    (first.root / ".cache").mkdir()
    (first.root / ".cache" / "device-specific").write_text("cpu")
    assert not (second.root / ".cache").exists()

    root = cache._asset_root(shared, asset)
    (root / "payload" / "weights.bin").write_bytes(b"x" * len(payloads["weights.bin"]))
    repaired = cache.prepare_models([asset], tmp_path / "three", shared_root=shared)
    assert counts["config.json"] == len(payloads["config.json"])
    assert counts["weights.bin"] == 2 * len(payloads["weights.bin"])
    assert (repaired.root / "weights.bin").read_bytes() == payloads["weights.bin"]

    def offline(*_args, **_kwargs):
        raise ConnectionError("synthetic offline")

    monkeypatch.setattr(cache, "_remote_manifest", offline)
    monkeypatch.setattr(cache, "_download", offline)
    complete = cache.prepare_models([asset], tmp_path / "offline", shared_root=shared)
    assert complete.downloaded_bytes == 0
    (root / "payload" / "weights.bin").unlink()
    with pytest.raises(ConnectionError, match="offline"):
        cache.prepare_models([asset], tmp_path / "missing", shared_root=shared)
    assert not (tmp_path / "missing" / "current.json").exists()


def _synthetic_provider(monkeypatch):
    payloads = {"a.bin": b"aaaa", "b.bin": b"bbbb"}
    calls = []

    def listing(asset):
        return {
            "namespace": asset.namespace,
            "source": asset.source,
            "repo_id": asset.repo_id,
            "revision": "master",
            "files": [
                {
                    "path": name,
                    "size": len(data),
                    "algorithm": "sha256",
                    "checksum": hashlib.sha256(data).hexdigest(),
                }
                for name, data in payloads.items()
            ],
        }

    def download(root, _asset, _manifest, entry):
        calls.append(entry["path"])
        path = root / "payload" / entry["path"]
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(payloads[entry["path"]])
        return path

    monkeypatch.setattr(cache, "_remote_manifest", listing)
    monkeypatch.setattr(cache, "_download", download)
    return payloads, calls


def test_parallel_preparation_and_failed_private_copy_keep_last_commit(
    tmp_path, monkeypatch
):
    _payloads, calls = _synthetic_provider(monkeypatch)
    shared = tmp_path / "shared"
    asset = cache.ModelAsset(
        "engine-1", "modelscope", "org/model", required_paths=("a.bin",)
    )
    with ThreadPoolExecutor(max_workers=2) as pool:
        futures = [
            pool.submit(
                cache.prepare_models, [asset], tmp_path / name, shared_root=shared
            )
            for name in ("one", "two")
        ]
        results = [future.result() for future in futures]
    assert calls == ["a.bin"]
    old_binding = (tmp_path / "one" / "current.json").read_text()
    original_copy = cache.shutil.copy2

    def no_space(source, target):
        if "revisions" in Path(target).parts:
            raise OSError(errno.ENOSPC, "synthetic disk full")
        return original_copy(source, target)

    monkeypatch.setattr(cache.shutil, "copy2", no_space)
    both = cache.ModelAsset("engine-1", "modelscope", "org/model")
    with pytest.raises(OSError, match="disk full"):
        cache.prepare_models([both], tmp_path / "one", shared_root=shared)
    assert (tmp_path / "one" / "current.json").read_text() == old_binding
    assert (results[0].root / "a.bin").read_bytes() == b"aaaa"
    monkeypatch.setattr(cache.shutil, "copy2", original_copy)
    restored = cache.prepare_models([both], tmp_path / "one", shared_root=shared)
    assert (restored.root / "b.bin").read_bytes() == b"bbbb"
    assert calls == ["a.bin", "b.bin"]


def test_frozen_provider_listing_rejects_moved_branch(tmp_path, monkeypatch):
    payloads, calls = _synthetic_provider(monkeypatch)
    asset = cache.ModelAsset("engine-1", "modelscope", "org/model")
    shared = tmp_path / "shared"
    cache.prepare_models([asset], tmp_path / "one", shared_root=shared)
    root = cache._asset_root(shared, asset)
    (root / "payload" / "a.bin").unlink()
    payloads["a.bin"] = b"moved upstream"
    with pytest.raises(cache.ModelCacheError, match="frozen provider listing"):
        cache.prepare_models([asset], tmp_path / "two", shared_root=shared)
    assert calls == ["a.bin", "b.bin", "a.bin"]
    assert not (tmp_path / "two" / "current.json").exists()


def test_paddle_resolver_is_serialized_and_restored(tmp_path, monkeypatch):
    monkeypatch.setenv("VIBEOCR_SHARED_MODEL_CACHE", str(tmp_path / "shared"))
    monkeypatch.setenv("PADDLE_PDX_CACHE_HOME", str(tmp_path / "private"))
    monkeypatch.setenv("PADDLE_PDX_MODEL_SOURCE", "huggingface")
    monkeypatch.setattr(cache, "version", lambda _name: "3.7.2")

    def original(names):
        return Path("legacy")

    manager = types.SimpleNamespace(_get_model_local_path=original)
    monkeypatch.setitem(
        sys.modules,
        "paddlex.inference.utils.official_models",
        types.SimpleNamespace(official_models=manager),
    )
    entered = threading.Event()
    release = threading.Event()
    second_entered = threading.Event()

    def one():
        with pytest.raises(RuntimeError, match="construction failed"):
            with cache.paddle_model_cache():
                outer = manager._get_model_local_path
                assert outer is not original
                with cache.paddle_model_cache():
                    assert manager._get_model_local_path is not outer
                assert manager._get_model_local_path is outer
                entered.set()
                assert release.wait(5)
                raise RuntimeError("construction failed")

    def two():
        assert entered.wait(5)
        with cache.paddle_model_cache():
            second_entered.set()
            assert manager._get_model_local_path is not original

    with ThreadPoolExecutor(max_workers=2) as pool:
        first = pool.submit(one)
        second = pool.submit(two)
        assert entered.wait(5)
        assert not second_entered.wait(0.1)
        release.set()
        first.result()
        second.result()
    assert second_entered.is_set()
    assert manager._get_model_local_path is original
    monkeypatch.setattr(cache, "version", lambda _name: "unsupported-version")
    with cache.paddle_model_cache():
        assert manager._get_model_local_path is original
    monkeypatch.setattr(cache, "version", lambda _name: "3.7.2")

    def unsupported():
        return Path("legacy")

    manager._get_model_local_path = unsupported
    with cache.paddle_model_cache():
        assert manager._get_model_local_path is unsupported


def test_paddle_model_dir_uses_windows_ascii_alias_for_unicode_path(tmp_path):
    # Real GetShortPathNameW, no mocks: the alias must expose the same files.
    if sys.platform != "win32":
        pytest.skip("Windows 8.3 aliases are validated on Windows only")
    revision = (
        tmp_path
        / "PaddleOCR · CPU"
        / "cache"
        / "paddlex"
        / "prepared-models"
        / "PP-LCNet_x1_0_doc_ori"
        / "revisions"
        / "21ab7fdcf2904336b43df13e204c0c64"
    )
    revision.mkdir(parents=True)
    (revision / "inference.json").write_text("{}", encoding="utf-8")
    (revision / "inference.pdiparams").write_bytes(b"synthetic weights")
    alias = cache._paddle_native_model_dir(revision)
    assert str(alias).isascii()
    assert alias != revision
    assert alias.samefile(revision)
    assert (alias / "inference.pdiparams").read_bytes() == b"synthetic weights"


def test_paddle_model_dir_keeps_readable_paths_unchanged():
    # A pure constant: never assume pytest's tmp_path ancestors are ASCII.
    plain = Path("ascii-root") / "prepared-models" / "model"
    assert cache._paddle_native_model_dir(plain) == plain


def test_paddle_model_dir_fails_closed_without_usable_alias(tmp_path, monkeypatch):
    monkeypatch.setattr(cache, "_WINDOWS", True)
    model = tmp_path / "PaddleOCR · CPU" / "cache"
    model.mkdir(parents=True)

    def eight_dot_three_disabled(path):
        # GetShortPathNameW returns the input when the volume has no 8.3 names.
        return path

    monkeypatch.setattr(cache, "_windows_short_path", eight_dot_three_disabled)
    with pytest.raises(cache.ModelCacheError, match="no ASCII short alias"):
        cache._paddle_native_model_dir(model)

    def api_failure(_path):
        raise OSError(3, "synthetic GetShortPathNameW failure")

    monkeypatch.setattr(cache, "_windows_short_path", api_failure)
    with pytest.raises(cache.ModelCacheError, match="short path unavailable"):
        cache._paddle_native_model_dir(model)


def test_paddle_resolver_returns_native_readable_model_dir(tmp_path, monkeypatch):
    payloads, _calls = _synthetic_provider(monkeypatch)
    home = tmp_path / "PaddleOCR · CPU" / "cache" / "paddlex"
    monkeypatch.setenv("VIBEOCR_SHARED_MODEL_CACHE", str(tmp_path / "shared"))
    monkeypatch.setenv("PADDLE_PDX_CACHE_HOME", str(home))
    monkeypatch.setenv("PADDLE_PDX_MODEL_SOURCE", "huggingface")
    monkeypatch.setattr(cache, "version", lambda _name: "3.7.2")

    def original(names):
        return Path("legacy")

    manager = types.SimpleNamespace(_get_model_local_path=original)
    monkeypatch.setitem(
        sys.modules,
        "paddlex.inference.utils.official_models",
        types.SimpleNamespace(official_models=manager),
    )
    with cache.paddle_model_cache():
        resolved = manager._get_model_local_path("PP-LCNet_x1_0_doc_ori")
    revision = next(
        (home / "prepared-models" / "PP-LCNet_x1_0_doc_ori" / "revisions").iterdir()
    )
    assert revision.is_dir()
    assert resolved.samefile(revision)
    assert (resolved / "a.bin").read_bytes() == payloads["a.bin"]
    assert (resolved / "b.bin").read_bytes() == payloads["b.bin"]
    if sys.platform == "win32":
        assert str(resolved).isascii()  # the alias, not the Unicode original
    binding = home / "prepared-models" / "PP-LCNet_x1_0_doc_ori" / "current.json"
    assert "~" not in binding.read_text(encoding="utf-8")  # alias never persisted


def test_paddle_resolver_normalizes_legacy_and_vl_roots(tmp_path, monkeypatch):
    home = tmp_path / "PaddleOCR · CPU" / "cache" / "paddlex"
    vl = home / "official_models" / "PaddleOCR-VL" / "PaddleOCR-VL-0.9B"
    vl.mkdir(parents=True)
    (vl / "weights.bin").write_bytes(b"legacy vl weights")
    monkeypatch.setenv("VIBEOCR_SHARED_MODEL_CACHE", str(tmp_path / "shared"))
    monkeypatch.setenv("PADDLE_PDX_CACHE_HOME", str(home))
    monkeypatch.setenv("PADDLE_PDX_MODEL_SOURCE", "huggingface")
    monkeypatch.setattr(cache, "version", lambda _name: "3.7.2")

    def unreachable(*_args, **_kwargs):
        raise AssertionError("legacy private models must not touch the network")

    monkeypatch.setattr(cache, "_remote_manifest", unreachable)
    monkeypatch.setattr(cache, "_download", unreachable)

    def original(names):
        return Path("legacy")

    manager = types.SimpleNamespace(_get_model_local_path=original)
    monkeypatch.setitem(
        sys.modules,
        "paddlex.inference.utils.official_models",
        types.SimpleNamespace(official_models=manager),
    )
    with cache.paddle_model_cache():
        resolved = manager._get_model_local_path("PaddleOCR-VL-0.9B")
    assert resolved.samefile(vl)
    assert (resolved / "weights.bin").read_bytes() == b"legacy vl weights"
    if sys.platform == "win32":
        assert str(resolved).isascii()


@pytest.mark.parametrize(
    "path", ["../outside", "/root", "a\\b", "NUL.bin", "a:b", "a//b"]
)
def test_provider_paths_cannot_escape_private_view(path):
    with pytest.raises(cache.ModelCacheError):
        cache._relative(path)


def test_legacy_hf_tree_imports_offline_without_changing_old_cache(
    tmp_path, monkeypatch
):
    old = tmp_path / "old"
    old.mkdir()
    data = b"legacy config"
    weights = b"legacy weights"
    (old / "config.json").write_bytes(data)
    (old / "weights.bin").write_bytes(weights)
    tree = old / ".cache" / "huggingface" / "trees" / ("c" * 40 + ".json")
    tree.parent.mkdir(parents=True)
    # This is the provider's existing native receipt, including the distinct
    # Git-blob and LFS checksum contracts used by the real Hub.
    tree.write_text(
        json.dumps(
            {
                "format_version": 1,
                "files": {
                    "config.json": {
                        "size": len(data),
                        "blob_id": hashlib.sha1(
                            f"blob {len(data)}\0".encode() + data
                        ).hexdigest(),
                    },
                    "weights.bin": {
                        "size": len(weights),
                        "blob_id": "b" * 40,
                        "lfs_sha256": hashlib.sha256(weights).hexdigest(),
                    },
                },
            }
        )
    )

    def offline(*_args, **_kwargs):
        raise AssertionError("A complete native cache must not use the network")

    monkeypatch.setattr(cache, "_remote_manifest", offline)
    monkeypatch.setattr(cache, "_download", offline)
    result = cache.prepare_models(
        [cache.ModelAsset("engine-1", "huggingface", "org/model")],
        tmp_path / "new",
        shared_root=tmp_path / "shared",
        legacy_roots=(old,),
    )
    assert result.downloaded_bytes == 0
    assert (result.root / "weights.bin").read_bytes() == weights
    assert (old / "weights.bin").read_bytes() == weights
    assert tree.is_file()


def test_completion_marker_failure_keeps_previous_private_binding(
    tmp_path, monkeypatch
):
    _synthetic_provider(monkeypatch)
    shared = tmp_path / "shared"
    one = cache.ModelAsset(
        "engine-1", "modelscope", "org/model", required_paths=("a.bin",)
    )
    first = cache.prepare_models([one], tmp_path / "view", shared_root=shared)
    binding = tmp_path / "view" / "current.json"
    before = binding.read_text()

    def denied(_path, *_args, **_kwargs):
        raise PermissionError("synthetic marker permission error")

    monkeypatch.setattr(Path, "touch", denied)
    all_files = cache.ModelAsset("engine-1", "modelscope", "org/model")
    with pytest.raises(PermissionError, match="marker permission"):
        cache.prepare_models(
            [all_files],
            tmp_path / "view",
            shared_root=shared,
            completion_marker=".mineru_complete",
        )
    assert binding.read_text() == before
    assert (first.root / "a.bin").read_bytes() == b"aaaa"


def test_mineru_cancels_prepare_child_without_replacing_previous_models(
    tmp_path, monkeypatch
):
    import subprocess

    from vibeocr.runtime.recognition import mineru_service as module
    from vibeocr.runtime.recognition.mineru_api import MineruCancelled

    monkeypatch.setenv("VIBEOCR_SHARED_MODEL_CACHE", str(tmp_path / "shared"))
    monkeypatch.setenv("MINERU_HOME", str(tmp_path / "private"))
    monkeypatch.setattr(module.MinerUService, "_model_root", "previous-good-models")
    cancelled = False
    stopped = False
    shutdowns = []

    class Process:
        def communicate(self, timeout=None):
            nonlocal cancelled
            if not stopped:
                cancelled = True
                raise subprocess.TimeoutExpired("synthetic prepare", timeout)
            return b"", b""

        def poll(self):
            return 1 if stopped else None

        def terminate(self):
            nonlocal stopped
            stopped = True

    monkeypatch.setattr(module.subprocess, "Popen", lambda *_args, **_kwargs: Process())
    monkeypatch.setattr(
        module,
        "JobObjectGuard",
        lambda: types.SimpleNamespace(
            assign_from_popen=lambda _process: None,
            close=lambda: None,
        ),
    )
    service = object.__new__(module.MinerUService)
    monkeypatch.setattr(
        service, "_resolve_python_executable", lambda: Path(sys.executable)
    )
    monkeypatch.setattr(service, "shutdown", lambda: shutdowns.append(True))
    with pytest.raises(MineruCancelled, match="during model preparation"):
        service._prepare_cached_models("basic", lambda: cancelled)
    assert stopped
    assert shutdowns == []
    assert module.MinerUService._model_root == "previous-good-models"


def test_mineru_failed_prepare_does_not_stop_previous_service(monkeypatch):
    from vibeocr.runtime.recognition import mineru_service as module
    from vibeocr.runtime_contracts import MineruConfig, MineruTier

    service = object.__new__(module.MinerUService)
    monkeypatch.setattr(
        module, "current_connection", lambda: types.SimpleNamespace(mode="local")
    )
    monkeypatch.setattr(module.MinerUService, "_server_tier", "basic")
    shutdowns = []
    monkeypatch.setattr(service, "shutdown", lambda: shutdowns.append(True))

    def fail(*_args):
        raise PermissionError("synthetic prepare denied")

    monkeypatch.setattr(service, "_prepare_cached_models", fail)
    with pytest.raises(PermissionError, match="prepare denied"):
        service._call_api(
            b"synthetic", "input.pdf", MineruConfig(tier=MineruTier.STANDARD)
        )
    assert shutdowns == []
    assert module.MinerUService._server_tier == "basic"


@pytest.mark.parametrize("child_exit_code", [0, 1])
def test_mineru_prepare_uses_product_code_and_service_configuration(
    tmp_path, monkeypatch, child_exit_code
):
    from vibeocr.runtime.recognition import mineru_service as module

    home = tmp_path / "private"
    prepared = tmp_path / "prepared"
    prepared.mkdir()
    monkeypatch.setenv("VIBEOCR_SHARED_MODEL_CACHE", str(tmp_path / "shared"))
    monkeypatch.setenv("VIBEOCR_PRODUCT_CODE_ROOT", str(tmp_path / "current-code"))
    monkeypatch.setenv("VIBEOCR_RUNTIME_ACCELERATOR", "cpu")
    monkeypatch.setenv("MINERU_HOME", str(home))
    monkeypatch.delenv("MINERU_CONFIG", raising=False)
    monkeypatch.setenv("PYTHONPATH", "must-not-leak")
    monkeypatch.setattr(module.MinerUService, "_model_root", "old-generation")
    launches = []
    shutdowns = []

    class Process:
        returncode = child_exit_code

        def communicate(self, timeout=None):
            return (
                json.dumps({"model_root": str(prepared)}).encode(),
                b"token=synthetic-secret https://example.invalid/private C:\\synthetic\\private",
            )

        def poll(self):
            return 0

    def launch(arguments, **kwargs):
        launches.append((arguments, kwargs))
        return Process()

    monkeypatch.setattr(module.subprocess, "Popen", launch)
    monkeypatch.setattr(
        module,
        "JobObjectGuard",
        lambda: types.SimpleNamespace(
            assign_from_popen=lambda _process: None, close=lambda: None
        ),
    )
    service = object.__new__(module.MinerUService)
    monkeypatch.setattr(
        service, "_resolve_python_executable", lambda: Path(sys.executable)
    )
    monkeypatch.setattr(service, "shutdown", lambda: shutdowns.append(True))
    if child_exit_code:
        with pytest.raises(module.MineruApiError) as failure:
            service._prepare_cached_models("standard", lambda: False)
        assert "synthetic-secret" not in str(failure.value)
        assert "example.invalid" not in str(failure.value)
        assert "synthetic\\private" not in str(failure.value)
        assert module.MinerUService._model_root == "old-generation"
        assert shutdowns == []
        return
    service._prepare_cached_models("standard", lambda: False)
    arguments, kwargs = launches[0]
    assert arguments[0] == sys.executable
    assert arguments[1:4] == ["-I", "-B", "-c"]
    assert "VIBEOCR_PRODUCT_CODE_ROOT" in arguments[4]
    assert arguments[-1] == "standard"
    env = kwargs["env"]
    assert "PYTHONPATH" not in env
    assert env["CUDA_VISIBLE_DEVICES"] == "-1"
    assert env["GGML_VK_VISIBLE_DEVICES"] == " "
    assert "small_backend: onnx" in Path(env["MINERU_CONFIG"]).read_text()
    assert "engine: llama-cpp" in Path(env["MINERU_CONFIG"]).read_text()
    assert env.get("MINERU_MODEL_BASE_DIR") != "old-generation"
    assert module.MinerUService._model_root == str(prepared)
    assert shutdowns == [True]


def test_paddle_native_hf_endpoint_is_preserved_for_listing_and_payload(
    tmp_path, monkeypatch, provider_server
):
    sdk = pytest.importorskip("huggingface_hub")
    endpoint, payloads, counts = provider_server
    monkeypatch.setitem(
        sys.modules,
        "paddlex.utils.flags",
        types.SimpleNamespace(HUGGING_FACE_ENDPOINT=endpoint),
    )
    real_api, real_download = sdk.HfApi, sdk.hf_hub_download

    def api(**kwargs):
        assert kwargs.get("endpoint") == endpoint
        return real_api(token=False, **kwargs)

    def download(*args, **kwargs):
        assert kwargs.get("endpoint") == endpoint
        return real_download(*args, token=False, **kwargs)

    monkeypatch.setattr(sdk, "HfApi", api)
    monkeypatch.setattr(sdk, "hf_hub_download", download)
    asset = cache.ModelAsset("paddlex-3.7.2", "huggingface", "org/model")
    prepared = cache.prepare_models(
        [asset], tmp_path / "view", shared_root=tmp_path / "shared"
    )
    assert counts == {name: len(data) for name, data in payloads.items()}
    assert (prepared.root / "weights.bin").read_bytes() == payloads["weights.bin"]


_NATIVE_ADAPTER_CHECK = """
import json
from importlib.metadata import distribution, version
from pathlib import Path
from vibeocr.runtime.environments.model_cache import paddle_model_cache
adapter = __import__('os').environ['FIXTURE_ADAPTER']
versions = {}
for name in ('paddlex', 'mineru', 'huggingface-hub', 'modelscope-hub'):
    try:
        dist = distribution(name)
        versions[name] = {'version': dist.version, 'metadata': str(dist._path)}
    except __import__('importlib').metadata.PackageNotFoundError:
        pass
assert versions['huggingface-hub']['version'] == '1.33.0', versions
assert versions['modelscope-hub']['version'] == '0.4.5', versions
if __import__('os').environ['MINERU_MODEL_SOURCE'] == 'modelscope':
    from modelscope_hub import constants
    assert constants.API_TIMEOUT == constants.API_CONNECT_TIMEOUT == 1
    assert constants.API_MAX_RETRIES == constants.DOWNLOAD_RETRY_TIMES == 1
    assert constants.DOWNLOAD_TIMEOUT == 1
markers = 0
if adapter == 'paddle':
    assert version('paddlex') == '3.7.2'
    from paddlex.inference.utils.official_models import official_models
    name = official_models.model_list[0]
    with paddle_model_cache():
        root = Path(official_models.get_model_path(name))
    assert (root / 'config.json').is_file()
    assert (root / 'weights.bin').is_file()
else:
    assert version('mineru') == '4.0.10'
    from vibeocr.runtime.recognition.mineru_service import MinerUService
    service = object.__new__(MinerUService)
    service._prepare_cached_models('basic', lambda: False)
    root = Path(service._model_root)
    from mineru.config import config
    from mineru.model.registry import model_repos_for_tier
    from mineru.model.download import MODEL_COMPLETE_MARKER, verify_model_repo
    config.model.base_dir = str(root)
    repos = model_repos_for_tier('basic')
    assert all(verify_model_repo(repo).ready for repo in repos)
    name = [repo.name for repo in repos]
    for repo in repos:
        for path in repo.required_paths():
            local = path.local_path()
            if local.is_dir():
                marker = local / MODEL_COMPLETE_MARKER
                assert marker.is_file()
                marker.unlink()
                assert not verify_model_repo(repo).ready
                marker.touch()
                markers += 1
    assert markers > 0, 'fixture must exercise native directory completion markers'
print(json.dumps({'root': str(root), 'sdk': versions, 'models': name, 'markers': markers}))
"""


@pytest.mark.parametrize("adapter", ["paddle", "mineru"])
@pytest.mark.parametrize("source", ["huggingface", "modelscope"])
def test_native_adapter_cold_hot_offline_missing_and_repair(
    tmp_path, provider_server, adapter, source
):
    """Opt-in native SDK integration: real manager, service child, registry and verifier."""
    if os.environ.get("VIBEOCR_NATIVE_MODEL_TESTS") != "1":
        pytest.skip("requires the locked native SDK test harness")
    endpoint, payloads, counts = provider_server
    shared = tmp_path / "shared"
    if adapter == "mineru":
        from mineru.model.registry import model_repos_for_tier

        # Consume the real registry; infer directory fixture payloads from paths.
        repos = model_repos_for_tier("basic", small_backend="torch")
        payloads.clear()
        counts.clear()
        for repo in repos:
            for path in repo.required_paths():
                name = path.relative_path
                if not Path(name).suffix:
                    name += "/asset.bin"
                payloads[name] = f"fixture:{name}".encode()
    total = {name: len(data) for name, data in payloads.items()}

    def prepare(private, address=endpoint, *, success=True):
        private.mkdir()
        config = private / "mineru.yaml"
        config.write_text(
            "model:\n  small_backend: torch\n"
            f"  source: {source}\n  base_dir: {str(private / 'legacy')!r}\n",
            encoding="utf-8",
        )
        env = {
            **os.environ,
            "FIXTURE_ADAPTER": adapter,
            "PYTHONUTF8": "1",
            "PYTHONIOENCODING": "utf-8",
            "VIBEOCR_SHARED_MODEL_CACHE": str(shared),
            "VIBEOCR_MANAGED_ENVIRONMENT_ID": "native-model-fixture",
            "VIBEOCR_PRODUCT_CODE_ROOT": str(
                Path(__file__).resolve().parents[4] / "src/runtime"
            ),
            "MINERU_HOME": str(private / "mineru"),
            "MINERU_CONFIG": str(config),
            "MINERU_MODEL_SOURCE": source,
            "PADDLE_PDX_MODEL_SOURCE": source,
            "PADDLE_PDX_CACHE_HOME": str(private / "paddlex"),
            "PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK": "1",
            "PADDLE_PDX_HUGGING_FACE_ENDPOINT": address,
            "HF_ENDPOINT": address,
            "HF_HOME": str(private / "hf"),
            "HF_HUB_DISABLE_TELEMETRY": "1",
            "HF_HUB_ETAG_TIMEOUT": "1",
            "HF_HUB_DOWNLOAD_TIMEOUT": "1",
            "HF_TOKEN": "",
            "MODELSCOPE_ENDPOINT": address,
            "MODELSCOPE_API_TOKEN": "",
            "MODELSCOPE_API_TIMEOUT": "1",
            "MODELSCOPE_API_CONNECT_TIMEOUT": "1",
            "MODELSCOPE_API_MAX_RETRIES": "1",
            "MODELSCOPE_DOWNLOAD_TIMEOUT": "1",
            "MODELSCOPE_DOWNLOAD_MAX_RETRIES": "1",
            "MODELSCOPE_HOME": str(private / "ms-home"),
            "MODELSCOPE_CACHE": str(private / "ms-cache"),
            "HOME": str(private),
            "USERPROFILE": str(private),
        }
        result = subprocess.run(
            [sys.executable, "-I", "-B", "-c", _NATIVE_ADAPTER_CHECK],
            env=env,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=120,
        )
        if success:
            assert result.returncode == 0, result.stderr
            return json.loads(result.stdout.strip().splitlines()[-1])
        assert result.returncode != 0, result.stdout
        assert not list(private.rglob("current.json"))
        return {"rejected": True, "error": result.stderr[-1500:]}

    one = prepare(tmp_path / "one")
    assert counts == total
    two = prepare(tmp_path / "two")
    assert counts == total
    assert one["root"] != two["root"]
    first_file = next(Path(one["root"]).rglob(next(iter(payloads)).rsplit("/", 1)[-1]))
    second_file = next(Path(two["root"]).rglob(first_file.name))
    assert not first_file.samefile(second_file)

    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        offline = f"http://127.0.0.1:{listener.getsockname()[1]}"
        # Bound without listening: the native transport gets connection refused.
        complete = prepare(tmp_path / "offline-complete", offline)
        assert counts == total
        missing = next(shared.rglob("payload")) / next(iter(payloads))
        assert missing.is_file()
        missing.unlink()
        downloads = missing.parents[len(Path(next(iter(payloads))).parts)] / "downloads"
        # Only this fresh fixture asset's SDK download cache is removed.
        assert downloads.is_relative_to(shared)
        if downloads.exists():
            shutil.rmtree(downloads)
        rejected = prepare(tmp_path / "offline-missing", offline, success=False)
        assert counts == total

    repaired = prepare(tmp_path / "missing-repaired")
    missing_name = next(iter(payloads))
    assert counts == {
        name: size * (2 if name == missing_name else 1) for name, size in total.items()
    }
    payload_root = next(shared.rglob("payload"))
    corrupt_name = list(payloads)[-1]
    (payload_root / corrupt_name).write_bytes(b"x" * total[corrupt_name])
    repaired_corruption = prepare(tmp_path / "corruption-repaired")
    expected = {
        name: size * (1 + (name == missing_name) + (name == corrupt_name))
        for name, size in total.items()
    }
    assert counts == expected
    evidence = {
        "adapter": adapter,
        "source": source,
        "cold_payload_bytes": sum(total.values()),
        "second_private_payload_bytes": 0,
        "offline_payload_bytes": 0,
        "counts": counts,
        "cold": one,
        "hot": two,
        "offline": complete,
        "missing": rejected,
        "missing_repaired": repaired,
        "corruption_repaired": repaired_corruption,
    }
    (tmp_path / "native-adapter-evidence.json").write_text(
        json.dumps(evidence, indent=2), encoding="utf-8"
    )
    print(
        json.dumps(
            {
                key: evidence[key]
                for key in (
                    "adapter",
                    "source",
                    "cold_payload_bytes",
                    "second_private_payload_bytes",
                    "offline_payload_bytes",
                    "counts",
                )
            }
        )
    )

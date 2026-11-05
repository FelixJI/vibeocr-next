"""component/source selection 的 HTTP 面契约（Protocol 2.8.0）。

预构建运行时树（PR2）零安装后，/v2/runtime/maintenance 与 Runtime Host
request_kind=start/command 的选择字段转发已随在线维护事务退役；本文件
保留 health 目录投影与 settings 的下载来源面。
"""

from __future__ import annotations

import httpx
from vibeocr.runtime.environments.runtime_selection import (
    component_variant_catalog_payload,
    download_source_catalog_payload,
)
from vibeocr.runtime.host.app import create_app


async def test_health_declares_selection_capability_catalogs(
    pdf_module, supervisor_token: str
) -> None:
    app = create_app(pdf_module, supervisor_token)
    async with httpx.AsyncClient(
        transport=httpx.ASGITransport(app=app),
        base_url="http://127.0.0.1",
        headers={"Authorization": f"Bearer {supervisor_token}"},
    ) as client:
        health = (await client.get("/v2/health")).json()

    descriptors = {d["name"]: d for d in health["capability_descriptors"]}
    source_descriptor = descriptors["runtime.download-sources.v1"]
    variant_descriptor = descriptors["runtime.component-selection.v1"]

    # catalog 结构与业务键唯一性（fail closed 的构建期防线在
    # runtime_selection 单测，这里钉住 HTTP 面的真实输出形状）。
    sources = source_descriptor["download_source_catalog"]["sources"]
    assert sources == download_source_catalog_payload()["sources"]
    assert len({s["id"] for s in sources}) == len(sources)
    assert {s["id"] for s in sources} == {
        "huggingface",
        "modelscope",
        "paddleocr-huggingface",
        "paddleocr-modelscope",
        "paddleocr-bos",
        "mineru-huggingface",
        "mineru-modelscope",
    }
    assert {s["kind"] for s in sources} == {
        "model_registry",
        "paddleocr_model_registry",
        "mineru_model_registry",
    }

    variants = variant_descriptor["component_variant_catalog"]["variants"]
    assert variants == component_variant_catalog_payload()["variants"]
    keys = {(v["feature_id"], v["accelerator"]) for v in variants}
    assert len(keys) == len(variants)
    component_ids = {v["component_id"] for v in variants}
    assert {"ocr_engine", "pdf_document_tools"}.isdisjoint(component_ids)


async def test_settings_roundtrips_download_source_ids(
    pdf_module, supervisor_token: str
) -> None:
    app = create_app(pdf_module, supervisor_token)
    async with httpx.AsyncClient(
        transport=httpx.ASGITransport(app=app),
        base_url="http://127.0.0.1",
        headers={"Authorization": f"Bearer {supervisor_token}"},
    ) as client:
        put = await client.put(
            "/v2/settings",
            json={
                "schema_version": 2,
                "residency": {"default_ttl_seconds": 300, "pipelines": []},
                "extra": {},
                "download_source_ids": ["paddleocr-modelscope"],
            },
        )
        assert put.status_code == 200
        assert put.json()["download_source_ids"] == ["paddleocr-modelscope"]

        got = await client.get("/v2/settings")
        assert got.json().get("download_source_ids") == ["paddleocr-modelscope"]

        same_kind = await client.put(
            "/v2/settings",
            json={
                "schema_version": 2,
                "residency": {"default_ttl_seconds": 300, "pipelines": []},
                "extra": {},
                "download_source_ids": ["paddleocr-modelscope", "paddleocr-bos"],
            },
        )
        assert same_kind.status_code == 400

        model_source = await client.put(
            "/v2/settings",
            json={
                "schema_version": 2,
                "residency": {"default_ttl_seconds": 300, "pipelines": []},
                "extra": {},
                "download_source_ids": ["paddleocr-modelscope", "mineru-modelscope"],
            },
        )
        assert model_source.status_code == 200
        assert model_source.json()["download_source_ids"] == [
            "paddleocr-modelscope",
            "mineru-modelscope",
        ]

        unknown = await client.put(
            "/v2/settings",
            json={
                "schema_version": 2,
                "residency": {"default_ttl_seconds": 300, "pipelines": []},
                "extra": {},
                "download_source_ids": ["unknown-model-source"],
            },
        )
        assert unknown.status_code == 400
        assert unknown.json()["code"] == "DOWNLOAD_SOURCE_UNKNOWN"

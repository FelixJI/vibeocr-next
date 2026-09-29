"""结构图像从 worker 落盘到授权读取、导出的最小闭环。"""

from __future__ import annotations

from types import SimpleNamespace

import httpx
import pytest
from PIL import Image
from vibeocr.runtime.documents.export import (
    OcrExportRequest,
    resolve_export_images,
    write_export,
)
from vibeocr.runtime.jobs.module import SupervisorModule
from vibeocr.runtime.recognition.paddle_executor import AdapterExecutor
from vibeocr.runtime.recognition.result_assets import (
    ResultAssetBudget,
    ResultAssetSink,
    sanitize_asset_name,
)
from vibeocr.runtime_contracts import ItemState


@pytest.mark.parametrize("kind", ["image", "chart", "seal"])
def test_result_asset_binding_read_and_export(tmp_path, kind):
    job_dir = tmp_path / "job"
    results_dir = job_dir / "results"
    sink = ResultAssetSink(results_dir, "it-0000", ResultAssetBudget(results_dir))
    image = sink.save(Image.new("RGB", (2, 2), "red"), name="C:/private/fig.png")
    assert image["name"] == f"{image['asset_id']}.png"
    assert "C:" not in str(image)
    block = {"type": kind, "text": "结构内容", "image": image}
    payload = {"content_list": [block]}
    AdapterExecutor._bind_asset_refs(payload, "job-a", "it-0000")

    record = SimpleNamespace(
        items=[SimpleNamespace(item_id="it-0000", state=ItemState.SUCCEEDED)],
        results={"it-0000": payload},
    )
    module = SimpleNamespace(
        registry=SimpleNamespace(get=lambda _job_id: record),
        stager=SimpleNamespace(job_dir=lambda _job_id: job_dir),
    )

    def resolve(job, item, asset):
        return SupervisorModule.resolve_result_asset(module, job, item, asset)

    asset_id = image["asset_id"]
    data, media_type = resolve("job-a", "it-0000", asset_id)
    assert media_type == "image/png" and data.startswith(b"\x89PNG")
    assert resolve("job-b", "it-0000", asset_id) is None
    assert resolve("job-a", "it-9999", asset_id) is None

    images, missing = resolve_export_images([block], resolve)
    assert missing == 0 and images == {image["name"]: data}
    request = OcrExportRequest("", "", "", [block], tmp_path / "out.docx", "docx")
    assert write_export(request, images=images)
    from docx import Document

    assert len(Document(str(request.output_path)).inline_shapes) == 1
    block["image"] = {"available": False, "reason": "asset_too_large"}
    assert resolve_export_images([block], resolve) == ({}, 1)


def test_asset_name_rejects_traversal():
    assert sanitize_asset_name("a/../../secret", fallback="safe.png") == "safe.png"


async def test_result_asset_route_reuses_auth(pdf_app, pdf_module, supervisor_token):
    pdf_module.resolve_result_asset = lambda *_ids: (b"\x89PNG\r\nasset", "image/png")
    path = "/v2/jobs/job-a/items/it-0000/assets/opaque123"
    async with httpx.AsyncClient(
        transport=httpx.ASGITransport(app=pdf_app),
        base_url="http://127.0.0.1",
    ) as client:
        assert (await client.get(path)).status_code == 401
        response = await client.get(
            path, headers={"Authorization": f"Bearer {supervisor_token}"}
        )
        assert response.status_code == 200
        assert response.headers["content-type"] == "image/png"
        assert response.content == b"\x89PNG\r\nasset"
        invalid = await client.get(
            "/v2/jobs/job-a/items/it-0000/assets/bad.id",
            headers={"Authorization": f"Bearer {supervisor_token}"},
        )
        assert invalid.status_code == 400

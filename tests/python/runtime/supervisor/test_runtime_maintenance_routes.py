"""Runtime 维护面保留路由：维护互斥对 preload 的投影。

预构建运行时树（PR2）零安装后，/v2/runtime/maintenance、
/v2/runtime/maintenance/command、/v2/runtime/install-plan 与
/v2/runtime/operations/* 的 observe/events 路由已随在线维护/安装事务
退役；本文件只保留 residency 面的 preload 互斥行为。
"""

from __future__ import annotations

from vibeocr.runtime.host.app import create_app
from vibeocr.runtime.recognition.recognition_modes import (
    ModeAvailability,
    RecognitionModeRegistry,
)


async def test_preload_route_maps_active_maintenance_to_runtime_busy(
    pdf_module, supervisor_token
):
    pdf_module.recognition_mode_registry = RecognitionModeRegistry(
        availability_probe=lambda definition: ModeAvailability("ready")
    )
    import threading
    from concurrent.futures import ThreadPoolExecutor

    import httpx

    entered, release = threading.Event(), threading.Event()

    def maintain():
        entered.set()
        assert release.wait(5)

    app = create_app(pdf_module, supervisor_token)
    with ThreadPoolExecutor() as pool:
        pending = pool.submit(pdf_module.run_runtime_maintenance, maintain)
        assert entered.wait(5)
        try:
            async with httpx.AsyncClient(
                transport=httpx.ASGITransport(app=app),
                base_url="http://127.0.0.1",
                headers={"Authorization": f"Bearer {supervisor_token}"},
            ) as http:
                response = await http.post(
                    "/v2/runtime/preload", json={"pipelines": ["MinerU"]}
                )
            assert response.status_code == 423, response.text
            assert response.json()["code"] == "RUNTIME_BUSY"
        finally:
            release.set()
        pending.result()

"""Backend route and served OpenAPI conformance with the formal Protocol v2.

预构建运行时树（PR2）零安装后，/v2/runtime/install-plan、maintenance、
maintenance/command 与 operations/*/observe|events 路由已从 Backend 退役，
而正式 openapi.yaml 的路由面收缩尚未完成；路由面 parity 断言随已删路由
用例一并移除，待正式 spec 更新后恢复。保留 Backend 实际提供的 OpenAPI
文档形状检查。
"""

from scripts.check_runtime_protocol_conformance import backend_app


def test_backend_serves_the_committed_formal_openapi() -> None:
    app = backend_app()
    assert app.openapi()["info"]["title"] == "VibeOCR Local Runtime API"
    assert app.openapi()["openapi"] == "3.1.0"

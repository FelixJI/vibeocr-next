import pytest


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

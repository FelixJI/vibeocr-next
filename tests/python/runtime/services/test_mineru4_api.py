"""MinerU 4 投影合同与真实 4.0.x 结果投影（传输合同见 mineru_sdk_contract/）。

官方 SDK 4.1.3 Tasks 协议的传输/取消/错误分类合同由
``tests/python/runtime/services/mineru_sdk_contract/``（真实 SDK + 受控
服务器）覆盖；本文件保留结果投影契约（schema 校验、页/块身份、表格、
caption/footnote、data URI）与 tier 目录门禁。
"""

import json
from pathlib import Path

import pytest
from vibeocr.runtime.recognition import mineru_readiness as readiness
from vibeocr.runtime.recognition.mineru_api import (
    MineruApiError,
    MineruDocument,
)
from vibeocr.runtime.recognition.mineru_config import (
    MineruConfigError,
    resolve_mineru_config,
)
from vibeocr.runtime.recognition.mineru_result import project_document
from vibeocr.runtime_contracts import (
    ErrorCode,
    MineruConfig,
    MineruTier,
    PipelineSelection,
)

FIXTURES = Path(__file__).parent / "fixtures" / "mineru4"


def test_real_basic_three_page_projection():
    # Captured from unmodified MinerU4.0.2, basic/onnx, generated 3-page PDF.
    # These short lines are classified as headers upstream, intentionally
    # excluded from text by the existing product contract, retained in blocks.
    middle = json.loads((FIXTURES / "basic-middle.json").read_text())
    structured = json.loads((FIXTURES / "basic-structured.json").read_text())
    result = project_document(MineruDocument("", structured, middle))
    assert [b["page_idx"] for b in result.content_list] == [0, 1, 2]
    assert result.content_list[0]["bbox"] == [115.0, 131.0, 364.0, 150.0]
    assert "page 3" in result.content_list[2]["text"]
    assert result.raw_text == ""


def test_real_407_empty_page_without_blocks_key_projects():
    # mineru-api 4.0.7（docvortex>=0.4.24）用 ParseResult.to_dict(skip_defaults=
    # True) 序列化 middle_json 输出：PageInfo.blocks 是 default_factory=list，
    # 零 block 页面省略 "blocks" 键；structured_content 渲染器对每页无条件
    # 输出 "blocks": []。缺键必须等价空列表，否则空白页文档必然抛
    # "MinerU block identity mismatch"（用户真实日志 mineru_result.py:144）。
    middle = {
        "schema": "docvortex.middle",
        "schema_version": "2.0",
        "metadata": {
            "file_suffix": "pdf",
            "producer": {"name": "mineru", "version": "4.0.7"},
        },
        "is_full_document": True,
        "pages": [
            {
                "page_idx": 0,
                "blocks": [
                    {
                        "type": "text",
                        "index": 0,
                        "bbox": [0.1, 0.1, 0.9, 0.2],
                        "content": [{"type": "text", "content": "content page"}],
                    }
                ],
            },
            {"page_idx": 1},  # 空白页：上游 skip_defaults 省略 blocks 键
        ],
    }
    structured = {
        "pages": [
            {
                "page_idx": 0,
                "blocks": [
                    {
                        "type": "text",
                        "bbox": [0.1, 0.1, 0.9, 0.2],
                        "content": "content page",
                    }
                ],
            },
            {"page_idx": 1, "blocks": []},
        ]
    }
    result = project_document(MineruDocument("", structured, middle))
    assert result.raw_text == "content page"
    assert [block["page_idx"] for block in result.content_list] == [0]


def test_genuine_block_count_mismatch_is_still_rejected():
    # 缺键=空列表是上游序列化契约；真正的块数量不一致仍必须 fail closed，
    # 校验本身不因兼容而删除。
    middle = {
        "schema": "docvortex.middle",
        "schema_version": "2.0",
        "pages": [
            {
                "page_idx": 0,
                "blocks": [
                    {
                        "type": "text",
                        "index": 0,
                        "content": [{"type": "text", "content": "a"}],
                    },
                    {
                        "type": "text",
                        "index": 1,
                        "content": [{"type": "text", "content": "b"}],
                    },
                ],
            }
        ],
    }
    structured = {
        "pages": [
            {
                "page_idx": 0,
                "blocks": [
                    {"type": "text", "bbox": [0.1, 0.1, 0.9, 0.2], "content": "a"}
                ],
            }
        ]
    }
    with pytest.raises(MineruApiError, match="block identity mismatch"):
        project_document(MineruDocument("", structured, middle))


@pytest.mark.parametrize(
    "options",
    [
        {"backend": "pipeline"},
        {"backend": "vlm-engine"},
        {"enable_table": False},
        {"enable_formula": False},
        {"lang_list": ["ch", "korean"]},
        {"effort": "xhigh"},
    ],
)
def test_unequal_legacy_choices_require_reselection(options):
    with pytest.raises(MineruConfigError) as caught:
        resolve_mineru_config(PipelineSelection("MinerU", options=options))
    assert caught.value.code == ErrorCode.MINERU_CONFIG_MIGRATION_REQUIRED


def test_legacy_equivalence_and_explicit_tier():
    assert resolve_mineru_config(PipelineSelection("MinerU")).tier == MineruTier.BASIC
    migrated = resolve_mineru_config(
        PipelineSelection("MinerU", options={"effort": "high", "start_page_id": 2})
    )
    assert migrated.tier == MineruTier.STANDARD
    assert migrated.page_range == "3-r1"
    config = MineruConfig(tier=MineruTier.ADVANCED, language="korean")
    assert resolve_mineru_config(PipelineSelection("MinerU", mineru=config)) is config


def test_unsafe_referenced_sidecar_is_rejected():
    # SDK save() 已把素材物化为 images 映射；middle_json 引用的路径仍必须
    # 过安全校验，绝不把任意路径当作图片键。
    middle = {
        "schema": "docvortex.middle",
        "schema_version": "2.0",
        "pages": [],
        "image_path": "../outside.png",
    }
    with pytest.raises(MineruApiError, match="Unsafe"):
        project_document(
            MineruDocument("", {"pages": []}, middle, images={"../outside.png": b"bad"})
        )


def test_missing_referenced_sidecar_fails_closed():
    # middle_json 引用的素材在 images 映射中缺失：按失败处理，不得以空
    # 图片通过（传输层的对应合同见 mineru_sdk_contract 的 sidecar 用例）。
    middle = {
        "schema": "docvortex.middle",
        "schema_version": "2.0",
        "pages": [
            {
                "page_idx": 0,
                "blocks": [
                    {
                        "type": "image",
                        "index": 0,
                        "content": [
                            {
                                "type": "image_body",
                                "index": 0,
                                "image_path": "images/missing.jpg",
                                "content": "",
                            }
                        ],
                    }
                ],
            }
        ],
    }
    structured = {
        "pages": [
            {
                "page_idx": 0,
                "blocks": [
                    {"type": "image", "bbox": [0.1, 0.1, 0.9, 0.9], "content": ""}
                ],
            }
        ]
    }
    with pytest.raises(MineruApiError, match="Missing MinerU image sidecar"):
        project_document(MineruDocument("", structured, middle))


def test_real_native_table_and_embedded_image():
    middle = json.loads(
        (FIXTURES / "table-image-middle.json").read_text(encoding="utf8")
    )
    structured = json.loads(
        (FIXTURES / "table-image-structured.json").read_text(encoding="utf8")
    )
    result = project_document(MineruDocument("", structured, middle))
    table = next(b for b in result.content_list if b["type"] == "table")
    assert "Apples" in table["text"] and "18" in table["text"]
    assert table["table"]["row_count"] == 3
    image = next(b for b in result.content_list if b["type"] == "image")
    assert result.images[image["img_path"]].startswith(b"\xff\xd8")
    assert "blue product sample" in result.raw_text


def test_catalog_requires_actual_execution_not_just_installed_packages(monkeypatch):
    # SDK 客户端已安装（4.1 起以 mineru.parser.api_client 可导入为准），
    # 但 tier 就绪仍要求真实执行观察。
    monkeypatch.setattr(readiness, "_mineru4_installed", lambda: True)
    monkeypatch.setattr(readiness, "_observations", {})
    assert all(
        tier["availability"] == "preparation_required"
        for tier in readiness.catalog_payload()["tiers"]
    )
    with pytest.raises(MineruConfigError) as caught:
        readiness.require_ready(MineruTier.BASIC)
    assert caught.value.code == ErrorCode.MINERU_TIER_PREPARATION_REQUIRED
    readiness.mark_executed(MineruTier.BASIC)
    readiness.require_ready(MineruTier.BASIC)
    assert (
        next(t for t in readiness.catalog_payload()["tiers"] if t["id"] == "advanced")[
            "availability"
        ]
        == "preparation_required"
    )
    readiness.mark_failed(MineruTier.BASIC)
    with pytest.raises(MineruConfigError):
        readiness.require_ready(MineruTier.BASIC)


def test_native_code_body_is_not_rendered_markdown():
    # Validated docvortex.middle/2.0 and rendered by locked docvortex 0.4.14.
    middle = json.loads((FIXTURES / "code-middle.json").read_text(encoding="utf-8"))
    structured = json.loads(
        (FIXTURES / "code-structured.json").read_text(encoding="utf-8")
    )
    assert structured["pages"][0]["blocks"][0]["content"] == "```python\nprint(1)\n```"
    result = project_document(MineruDocument("", structured, middle))
    assert result.raw_text == "print(1)"
    assert result.content_list[0]["code_body"] == "print(1)"
    assert result.markdown_text == "```\nprint(1)\n```"
    assert "<pre><code>print(1)</code></pre>" in result.html_text


def test_native_semantic_text_lists_equations_and_code_annotations():
    # Validated and rendered with locked docvortex 0.4.14; no OCR inference claim.
    middle = json.loads((FIXTURES / "semantic-middle.json").read_text(encoding="utf-8"))
    structured = json.loads(
        (FIXTURES / "semantic-structured.json").read_text(encoding="utf-8")
    )
    result = project_document(MineruDocument("", structured, middle))
    assert (
        result.raw_text
        == "A * B < C\nfirst\nsecond\nx=1\nExample code\nprint(1)\nCode note"
    )
    assert result.content_list[1]["list_items"] == ["first", "second"]
    assert "- first\n- second" in result.markdown_text
    assert "- - " not in result.markdown_text
    assert "$$x=1$$" in result.markdown_text
    assert "Example code\n\n```\nprint(1)\n```\n\nCode note" in result.markdown_text
    assert "A \\* B &lt; C" in result.markdown_text
    assert "<p>A * B &lt; C</p>" in result.html_text
    assert "<li>first</li><li>second</li>" in result.html_text
    assert "Example code" in result.html_text and "Code note" in result.html_text


@pytest.mark.parametrize("suffix", ["docx", "pdf"])
def test_native_visual_annotations_survive_all_projections(suffix):
    # Validated/rendered by docvortex 0.4.14; synthetic schema fixture, not OCR.
    middle = json.loads(
        (FIXTURES / f"annotations-{suffix}-middle.json").read_text(encoding="utf-8")
    )
    structured = json.loads(
        (FIXTURES / f"annotations-{suffix}-structured.json").read_text(encoding="utf-8")
    )
    result = project_document(MineruDocument("", structured, middle))
    for kind in ("image", "chart", "table"):
        caption = f"{kind} caption *literal*"
        footnote = f"{kind} note <b>literal</b>"
        assert result.raw_text.count(caption) == 1
        assert result.raw_text.count(footnote) == 1
        assert result.raw_text.index(caption) < result.raw_text.index(footnote)
        assert f"{kind} caption \\*literal\\*" in result.markdown_text
        assert f"{kind} note &lt;b&gt;literal&lt;/b&gt;" in result.markdown_text
        assert caption in result.html_text
        assert f"{kind} note &lt;b&gt;literal&lt;/b&gt;" in result.html_text
    assert result.raw_text.count("Cell") == 1


@pytest.mark.parametrize("suffix", ["docx", "pdf"])
def test_native_visual_bodies_formula_fallback_and_nested_code_fence(suffix):
    middle = json.loads(
        (FIXTURES / f"annotations-{suffix}-middle.json").read_text(encoding="utf-8")
    )
    structured = json.loads(
        (FIXTURES / f"annotations-{suffix}-structured.json").read_text(encoding="utf-8")
    )
    result = project_document(MineruDocument("", structured, middle))
    assert result.raw_text.count("image body *literal*") == 1
    assert "image body \\*literal\\*" in result.markdown_text
    assert "image body *literal*" in result.html_text
    for projection in (result.raw_text, result.markdown_text, result.html_text):
        assert "Revenue" in projection and "42" in projection
    assert "<table>" not in result.raw_text
    equation = next(b for b in result.content_list if b["type"] == "equation")
    assert equation["img_path"] in result.images
    assert f"![]({equation['img_path']})" in result.markdown_text
    assert f'<img src="{equation["img_path"]}"' in result.html_text
    assert "````\nfirst\n```\n# literal heading\nlast\n````" in result.markdown_text
    assert (
        "<pre><code>first\n```\n# literal heading\nlast</code></pre>"
        in result.html_text
    )


@pytest.mark.parametrize("suffix", ["docx", "pdf"])
def test_native_table_fallback_and_index_keep_semantic_content(suffix):
    middle = json.loads(
        (FIXTURES / f"annotations-{suffix}-middle.json").read_text(encoding="utf-8")
    )
    structured = json.loads(
        (FIXTURES / f"annotations-{suffix}-structured.json").read_text(encoding="utf-8")
    )
    result = project_document(MineruDocument("", structured, middle))
    fallback = next(b for b in result.content_list if b["type"] == "table_unparsed")
    assert fallback["text"] == "Item  Count\nApple  12"
    for projection in (result.raw_text, result.markdown_text, result.html_text):
        assert projection.count("Fallback caption") == 1
        assert projection.count("Fallback note") == 1
        assert "Apple  12" in projection
    assert "Fallback caption\nItem  Count\nApple  12\nFallback note" in result.raw_text
    assert "Chapter A ... 1\nChapter B ... 2" in result.raw_text
    assert "Chapter A ... 1<br>Chapter B ... 2" in result.html_text

    image_table = [b for b in result.content_list if b["type"] == "table_unparsed"][1]
    assert image_table["text"] == ""
    assert image_table["img_path"] in result.images
    assert image_table["img_path"] in result.markdown_text
    assert image_table["img_path"] in result.html_text
    for projection in (result.raw_text, result.markdown_text, result.html_text):
        assert projection.count("Image fallback caption") == 1
        assert projection.count("Image fallback note") == 1


@pytest.mark.parametrize("suffix", ["docx", "pdf"])
def test_native_empty_code_keeps_annotations(suffix):
    middle = json.loads(
        (FIXTURES / f"annotations-{suffix}-middle.json").read_text(encoding="utf-8")
    )
    structured = json.loads(
        (FIXTURES / f"annotations-{suffix}-structured.json").read_text(encoding="utf-8")
    )
    result = project_document(MineruDocument("", structured, middle))
    for projection in (result.raw_text, result.markdown_text, result.html_text):
        assert projection.count("Empty code caption") == 1
        assert projection.count("Empty code note") == 1
    assert "Empty code caption\n\nEmpty code note" in result.markdown_text
    assert "<p>Empty code caption</p>\n<p>Empty code note</p>" in result.html_text


@pytest.mark.parametrize("suffix", ["docx", "pdf"])
@pytest.mark.parametrize("fmt", ["docx", "xlsx"])
def test_native_projection_survives_office_export(suffix, fmt, tmp_path):
    from vibeocr.runtime.documents.export_service import ExportService

    middle = json.loads(
        (FIXTURES / f"annotations-{suffix}-middle.json").read_text(encoding="utf-8")
    )
    structured = json.loads(
        (FIXTURES / f"annotations-{suffix}-structured.json").read_text(encoding="utf-8")
    )
    result = project_document(MineruDocument("", structured, middle))
    path = tmp_path / f"result.{fmt}"
    assert ExportService.export(result, path, fmt)
    if fmt == "docx":
        from docx import Document

        document = Document(path)
        text = "\n".join(p.text for p in document.paragraphs)
        assert len(document.inline_shapes) == 2
    else:
        from openpyxl import load_workbook

        workbook = load_workbook(path)
        text = "\n".join(
            str(value)
            for sheet in workbook
            for row in sheet.values
            for value in row
            if value is not None
        )
        workbook.close()
    for value in (
        "Fallback caption",
        "Apple  12",
        "Fallback note",
        "Image fallback caption",
        "Image fallback note",
        "Empty code caption",
        "Empty code note",
        "image caption *literal*",
        "image body *literal*",
        "image note <b>literal</b>",
        "chart caption *literal*",
        "Revenue",
        "42",
        "chart note <b>literal</b>",
        "Chapter A ... 1\nChapter B ... 2",
        "Reference Alpha 2026",
    ):
        assert text.count(value) == 1

# tests/core/test_pipeline_options_migration.py
"""Task 2 测试：验证各管道 Options 类继承 BasePipelineOptions 且字段拆分正确。"""

from vibeocr.runtime.recognition.core.pipelines.pipeline_mineru import MinerUOptions
from vibeocr.runtime.recognition.core.pipelines.pipeline_ocr import OCROptions
from vibeocr.runtime.recognition.core.pipelines.pipeline_pp_structure import (
    PPStructureV3Options,
)


def test_ocr_options_roundtrip():
    opts = OCROptions(
        use_doc_orientation_classify=True,
        use_doc_unwarping=False,
        use_textline_orientation=True,
    )
    d = opts.to_dict()
    assert d["pipeline"] == "OCR"
    assert d["use_doc_unwarping"] is False
    restored = OCROptions.from_dict(d)
    assert restored.use_doc_unwarping is False


def test_pp_structure_options():
    opts = PPStructureV3Options(use_table_recognition=False)
    d = opts.to_dict()
    assert d["pipeline"] == "PP-StructureV3"
    assert d["use_table_recognition"] is False


def test_mineru_options():
    opts = MinerUOptions(parse_method="ocr")
    d = opts.to_dict()
    assert d["pipeline"] == "MinerU"
    assert d["parse_method"] == "ocr"


# #270：PaddleOCRVLOptions 随 paddle_document_vl 模式退役删除；历史
# recognition_options 记录的 vl_* 字段由宿主兼容读取层处理。

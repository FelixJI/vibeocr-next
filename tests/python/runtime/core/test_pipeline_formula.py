# tests/core/test_pipeline_formula.py
"""FormulaRecognitionPipeline（锁定 PaddleOCR 3.7.0 独立公式管线）合同测试。

回归锚点（旧实现会失败）：
- 旧实现用 PPStructureV3 冒充公式管线、把 batch_size 塞给 predict、
  从 parsing_res_list 提取并伪造 score=1.0；新实现构造真实
  FormulaRecognitionPipeline（model/batch 为构造参数），predict 只收
  方向/去畸变，结果从 formula_res_list[].rec_formula 解析且 score=None。
"""

from vibeocr.runtime.recognition.core.pipelines.pipeline_formula import (
    FORMULA_RECOGNITION_SPEC,
    FormulaRecognitionOptions,
    _create_formula_pipeline,
    _formula_constructor_kwargs,
    _recognize_formula,
)


def test_formula_options_defaults():
    opts = FormulaRecognitionOptions()
    assert opts.pipeline == "FORMULA_RECOGNITION"
    assert opts.formula_recognition_batch_size == 1
    assert opts.formula_recognition_model_name is None
    assert opts.formula_recognition_model_dir is None
    assert opts.use_doc_orientation_classify is True
    # 与 OCROptions wire 默认一致（PDF 文字层场景默认不跑矫正网络）
    assert opts.use_doc_unwarping is False


def test_formula_options_to_dict():
    opts = FormulaRecognitionOptions(formula_recognition_batch_size=4)
    d = opts.to_dict()
    assert d["formula_recognition_batch_size"] == 4
    assert d["pipeline"] == "FORMULA_RECOGNITION"


def test_formula_spec():
    assert FORMULA_RECOGNITION_SPEC.name == "FORMULA_RECOGNITION"
    assert FORMULA_RECOGNITION_SPEC.display_name == "公式识别"
    assert FORMULA_RECOGNITION_SPEC.options_class is FormulaRecognitionOptions
    # model/batch 是构造参数：必须有 constructor_kwargs 映射
    assert FORMULA_RECOGNITION_SPEC.constructor_kwargs is _formula_constructor_kwargs


def test_constructor_kwargs_map_model_and_batch():
    """model 名/目录/批量是构造参数；None 透传（底层回退默认）。"""
    kwargs = _formula_constructor_kwargs(
        FormulaRecognitionOptions(
            formula_recognition_model_name="PP-FormulaNet_plus-S",
            formula_recognition_batch_size=8,
        )
    )
    assert kwargs["formula_recognition_model_name"] == "PP-FormulaNet_plus-S"
    assert kwargs["formula_recognition_batch_size"] == 8
    assert kwargs["formula_recognition_model_dir"] is None
    assert kwargs["use_doc_orientation_classify"] is True
    assert kwargs["use_doc_unwarping"] is False


def test_create_formula_pipeline_uses_locked_class(monkeypatch):
    """必须构造锁定版本的 FormulaRecognitionPipeline（而非 PPStructureV3）。"""
    import sys
    import types

    captured: dict = {}

    class _Captured:
        def __init__(self, **kwargs):
            captured.update(kwargs)

        def predict(self, *args, **kwargs):
            return []

    stub = types.ModuleType("paddleocr")
    stub.FormulaRecognitionPipeline = _Captured
    stub.PPStructureV3 = object  # 不应被使用
    monkeypatch.setitem(sys.modules, "paddleocr", stub)

    instance = _create_formula_pipeline("cpu", enable_mkldnn=False)
    assert isinstance(instance, _Captured)
    assert captured["device"] == "cpu"
    assert captured["enable_mkldnn"] is False


def test_consumed_constructor_model_reaches_parent_log(monkeypatch, caplog):
    import logging
    import sys
    import types

    from vibeocr.runtime.processes.utils.subprocess_log import SubprocessLogForwarder

    received = {}

    class _PaddleFormula:
        def __init__(self, **kwargs):
            received.update(kwargs)

    stub = types.ModuleType("paddleocr")
    stub.FormulaRecognitionPipeline = _PaddleFormula
    monkeypatch.setitem(sys.modules, "paddleocr", stub)
    with caplog.at_level(logging.INFO):
        _create_formula_pipeline(
            "cpu", formula_recognition_model_name="PP-FormulaNet_plus-L"
        )
        worker_record = next(
            record for record in caplog.records if "[Paddle consumed]" in record.message
        )
        line = logging.Formatter(
            "%(asctime)s [%(levelname)s] %(name)s: %(message)s"
        ).format(worker_record)
        SubprocessLogForwarder(
            logger_name="vibeocr.subprocess.paddle.test",
            source_label="[Paddle worker]",
        ).forward(line)

    assert received["formula_recognition_model_name"] == "PP-FormulaNet_plus-L"
    assert any(
        record.name == "vibeocr.subprocess.paddle.test"
        and "construct FORMULA_RECOGNITION formula_recognition_model_name=PP-FormulaNet_plus-L"
        in record.message
        for record in caplog.records
    )


class _DictResult(dict):
    """模拟 PaddleX FormulaRecognitionResult：dict 子类。"""


class _FakePipeline:
    def __init__(self, result_list):
        self._result_list = result_list
        self.predict_calls: list[dict] = []

    def predict(self, input, **kwargs):  # noqa: A002 — 模拟 PaddleOCR API
        self.predict_calls.append(dict(kwargs))
        return list(self._result_list)


class _FakeService:
    def __init__(self, result_list):
        self.pipeline = _FakePipeline(result_list)
        self.requests: list[tuple[str, object]] = []

    def get_or_create_pipeline(self, name, options=None):
        self.requests.append((name, options))
        return self.pipeline


def _formula_result(
    formulas: list[tuple[str, list[float] | None]],
    *,
    angle: int | None = None,
) -> _DictResult:
    res: dict = {}
    if angle is not None:
        import numpy as np

        rgb_arr = np.zeros((2, 2, 3), dtype=np.uint8)
        res["doc_preprocessor_res"] = {"angle": angle, "output_img": rgb_arr}
    else:
        res["doc_preprocessor_res"] = None
    res["formula_res_list"] = [
        (
            {
                "rec_formula": latex,
                "formula_region_id": index,
                "dt_polys": polys,
            }
            if polys is not None
            else {"rec_formula": latex, "formula_region_id": index}
        )
        for index, (latex, polys) in enumerate(formulas)
    ]
    return _DictResult(res)


def test_recognize_formula_parses_formula_res_list():
    """结果从 formula_res_list[].rec_formula 解析；score=None（不伪造 1.0）。"""
    res = _formula_result(
        [
            (r"a^2 + b^2", [10.0, 20.0, 300.0, 80.0]),
            (r"\sqrt{2}", None),
        ]
    )
    service = _FakeService([res])
    result = _recognize_formula(
        service, image=None, options=FormulaRecognitionOptions()
    )

    assert result.pipeline_type == "FORMULA_RECOGNITION"
    assert len(result.text_blocks) == 2
    block = result.text_blocks[0]
    assert block.label == "formula"
    assert block.text == r"a^2 + b^2"
    assert block.bbox == (10.0, 20.0, 300.0, 80.0)
    # 上游无置信度：真实 unknown，不是 1.0/0.9
    assert block.score is None
    assert result.text_blocks[1].bbox is None
    assert result.text_blocks[1].score is None
    # 阅读顺序：formula_region_id
    assert [b.order for b in result.text_blocks] == [0, 1]
    # content_list 结构块（供 C# RawBlocks 消费）
    assert result.content_list[0] == {
        "type": "formula",
        "text": r"a^2 + b^2",
        "bbox": [10.0, 20.0, 300.0, 80.0],
        "block_id": "formula-recognition-block-0",
    }
    assert result.content_list[1]["bbox"] is None
    # markdown 用 $$ 包裹；raw_text 为 LaTeX 行
    assert r"a^2 + b^2" in result.raw_text
    assert "$$" in result.markdown_text
    # 无已知置信度：avg=0.0、无低置信度项
    assert result.avg_score == 0.0
    assert result.low_confidence_items == []


def test_recognize_formula_predict_only_orientation_and_unwarping():
    """predict 只收方向/去畸变；batch/model 不再塞给 predict（旧实现会失败）。"""
    res = _formula_result([(r"x = 1", [0.0, 0.0, 5.0, 5.0])])
    service = _FakeService([res])
    opts = FormulaRecognitionOptions(
        formula_recognition_batch_size=4,
        formula_recognition_model_name="PP-FormulaNet_plus-S",
        use_doc_unwarping=True,
    )
    _recognize_formula(service, image=None, options=opts)
    predict_kwargs = service.pipeline.predict_calls[0]
    assert set(predict_kwargs) == {
        "use_doc_orientation_classify",
        "use_doc_unwarping",
    }
    assert predict_kwargs["use_doc_unwarping"] is True
    # options 透传给缓存层（构造签名用）
    name, passed = service.requests[0]
    assert name == "FORMULA_RECOGNITION"
    assert passed is opts


def test_recognize_formula_with_preprocessed_output_img():
    """doc_preprocessor_res 含 output_img 时提取预处理图与角度。"""
    res = _formula_result([(r"x = 1", None)], angle=90)
    service = _FakeService([res])
    result = _recognize_formula(
        service, image=None, options=FormulaRecognitionOptions()
    )
    assert result.preproc_angle == 90
    assert result.preproc_img_w == 2
    assert result.preproc_img_h == 2
    assert result.preprocessed_image
    assert result.preprocessed_image.startswith(b"\x89PNG")


def test_recognize_formula_upstream_error_raises():
    """上游 settings 校验失败必须显式失败，不能静默返回空结果。"""
    service = _FakeService([_DictResult({"error": "invalid model settings"})])
    import pytest

    with pytest.raises(RuntimeError, match="公式识别管线返回错误"):
        _recognize_formula(service, image=None, options=FormulaRecognitionOptions())


def test_recognize_formula_empty_output():
    """predict 返回空列表时不崩溃。"""
    service = _FakeService([])
    result = _recognize_formula(
        service, image=None, options=FormulaRecognitionOptions()
    )
    assert result.raw_text == ""
    assert result.text_blocks == []

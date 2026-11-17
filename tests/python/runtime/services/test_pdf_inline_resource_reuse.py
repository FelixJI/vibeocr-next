"""Shared inline backgrounds retain resource identity through candidate redaction."""

import random

import pymupdf as fitz
import pytest
from pymupdf import mupdf
from vibeocr.runtime.documents.models.pdf_document import PdfDocument, PdfPageInfo
from vibeocr.runtime.documents.pdf_resource_graft import PageResourceGraft
from vibeocr.runtime.documents.pdf_service import PdfService


def _stream(doc, content, dictionary="<< >>"):
    number = doc.get_new_xref()
    doc.update_object(number, dictionary)
    doc.update_stream(number, content)
    return number


def _form(doc, content, resources="<< >>"):
    return _stream(
        doc,
        content,
        f"<< /Type /XObject /Subtype /Form /BBox [0 0 500 500] /Resources {resources} >>",
    )


def _fixture(old=False):
    doc = fitz.open()
    doc.new_page(width=500, height=500)
    data = random.Random(42).randbytes(400 * 400)
    inline = (
        b"q 100 0 0 100 0 0 cm BI /W 400 /H 400 /CS /G /BPC 8 /F /AHx ID "
        + data.hex().encode()
        + b"> EI Q"
    )
    picture = _form(doc, inline)
    outer = _form(doc, b"/BG Do", f"<< /XObject << /BG {picture} 0 R >> >>")
    pattern = _stream(
        doc,
        inline,
        "<< /Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 100 100] /XStep 100 /YStep 100 /Resources << >> >>",
    )
    words = ""
    if old:
        font = doc[0].insert_font("helv")
        text = _form(
            doc,
            b"BT /helv 18 Tf 30 160 Td (OLD-FORM) Tj ET",
            f"<< /Font << /helv {font} 0 R >> >>",
        )
        wrapper = _form(doc, b"/TEXT Do", f"<< /XObject << /TEXT {text} 0 R >> >>")
        words = f" /WORDS {wrapper} 0 R"
    for index in range(4):
        page = doc[index] if index == 0 else doc.new_page(width=500, height=500)
        doc.xref_set_key(
            page.xref,
            "Resources",
            f"<< /XObject << /ART {outer} 0 R{words} >> /Pattern << /PAT {pattern} 0 R >> >>",
        )
        page.set_contents(
            _stream(
                doc,
                b"q /ART Do /Pattern cs /PAT scn 300 300 100 100 re f Q"
                + (b" /WORDS Do" if old else b""),
            )
        )
    model = PdfDocument(pages=[PdfPageInfo(page_index=i) for i in range(4)])
    return doc, model, (picture, outer, pattern)


def _data(pages, word):
    return [
        {
            "page": index,
            "ocr_result": {
                "preproc_angle": 0,
                "text_blocks": [
                    {"text": f"{word}{index}", "score": 1, "bbox": [100, 100, 600, 200]}
                ],
            },
        }
        for index in pages
    ]


def _resources(doc):
    forms = {entry[0] for page in doc for entry in page.get_xobjects()}
    pdf = mupdf.pdf_document_from_fz_document(doc.this)
    patterns = set()
    for index in range(doc.page_count):
        resources = pdf.pdf_lookup_page_obj(index).pdf_dict_getp("Resources/Pattern")
        patterns.update(
            resources.pdf_dict_get_val(i).pdf_to_num()
            for i in range(resources.pdf_dict_len())
        )
    return forms, patterns


@pytest.fixture(autouse=True)
def fallback_font(monkeypatch):
    monkeypatch.setattr(
        "vibeocr.runtime.documents.pdf_service._CJK_RESOLVER.resolve", lambda text: None
    )


@pytest.mark.parametrize("resource_scope", ["explicit", "missing", "null"])
def test_shared_inline_forms_and_pattern_survive_repeated_cross_batch_overwrite(
    tmp_path,
    resource_scope,
):
    doc, model, (picture, _, _) = _fixture()
    if resource_scope == "null":
        doc.xref_set_key(picture, "Resources", "null")
    elif resource_scope == "missing":
        pdf = mupdf.pdf_document_from_fz_document(doc.this)
        pdf.pdf_new_indirect(picture, 0).pdf_dict_dels("Resources")
    pixels = [page.get_pixmap().samples for page in doc]
    initial_size = len(doc.write(garbage=0, deflate=True))
    target = tmp_path / "inline.pdf"
    for word in ("FIRST", "SECOND", "THIRD"):
        for index in range(4):
            assert PdfService.add_text_layer_batch(
                doc, model, _data([index], word), overwrite=True
            ) == {index: (1, 0)}
        PdfService.save_with_rewrite(
            doc, model, str(target), rewrite_text_layers=False, copy_export=True
        )
        with fitz.open(target) as saved:
            forms, patterns = _resources(saved)
            assert len(forms) == 2 and len(patterns) == 1
            assert target.stat().st_size <= initial_size + 4 * 2048 + 65536
            assert [page.get_pixmap().samples for page in saved] == pixels
            assert [page.get_text().strip() for page in saved] == [
                f"{word}{i}" for i in range(4)
            ]
    doc.close()


def test_nested_text_is_removed_without_changing_cancelled_page():
    doc, model, originals = _fixture(old=True)
    refs = [page.xref for page in doc]
    untouched = doc[1].get_pixmap().samples
    cancel = iter([False, True])
    assert PdfService.add_text_layer_batch(
        doc,
        model,
        _data([0, 1], "NEW"),
        overwrite=True,
        cancel_check=lambda: next(cancel),
    ) == {0: (1, 0)}
    assert doc[0].get_text().strip() == "NEW0"
    assert doc[1].get_text().strip() == "OLD-FORM"
    assert doc[1].get_pixmap().samples == untouched
    assert [page.xref for page in doc] == refs
    assert set(originals[:2]) <= {entry[0] for entry in doc[0].get_xobjects()}
    doc.close()


def _tagged_objects(doc):
    pdf = mupdf.pdf_document_from_fz_document(doc.this)
    result = []
    for number in range(1, doc.xref_length()):
        obj = pdf.pdf_new_indirect(number, 0)
        if obj.pdf_is_dict() and PageResourceGraft._LINEAGE_KEY in (
            obj.pdf_dict_get_key(i).pdf_to_name() for i in range(obj.pdf_dict_len())
        ):
            result.append(number)
    return result


@pytest.mark.parametrize("collision", ["[]", "null"])
def test_source_lineage_key_collision_is_preserved_and_private_tags_do_not_escape(
    collision,
):
    doc, model, (_, outer, _) = _fixture()
    for word in ("FIRST", "SECOND"):
        PdfService.add_text_layer_batch(doc, model, _data([0, 1], word), overwrite=True)
    assert not _tagged_objects(doc)
    with fitz.open(stream=doc.write(garbage=0), filetype="pdf") as output:
        assert not _tagged_objects(output)
    # Keep an explicitly present null: xref_set_key(null) can remove the key.
    dictionary = doc.xref_object(outer).rstrip()
    doc.update_object(
        outer, dictionary[:-2] + f" /{PageResourceGraft._LINEAGE_KEY} {collision} >>"
    )
    assert _tagged_objects(doc) == [outer]
    PdfService.add_text_layer_batch(doc, model, _data([0], "THIRD"), overwrite=True)
    changed = {entry[0] for entry in doc[0].get_xobjects()} - {outer}
    assert len(_tagged_objects(doc)) == 2
    copied = set(_tagged_objects(doc)) - {outer}
    assert copied <= changed
    assert all(
        doc.xref_get_key(number, PageResourceGraft._LINEAGE_KEY)[1] == collision
        for number in copied
    )
    assert doc[0].get_text().strip() == "THIRD0"
    doc.close()


def test_resource_can_be_marked_after_add_only_page_in_same_batch():
    doc, model, (picture, outer, _) = _fixture()
    doc[1].insert_text((30, 160), "OLD-PAGE", render_mode=3)
    assert PdfService.add_text_layer_batch(
        doc, model, _data([0, 1], "NEW"), overwrite=True
    ) == {0: (1, 0), 1: (1, 0)}
    assert {entry[0] for page in (doc[0], doc[1]) for entry in page.get_xobjects()} == {
        picture,
        outer,
    }
    assert [doc[i].get_text().strip() for i in (0, 1)] == ["NEW0", "NEW1"]
    assert not _tagged_objects(doc)
    doc.close()


@pytest.mark.parametrize(
    "operation", [b"(OLD) Tj", b"[(OLD)] TJ", b"(OLD) '", b'0 0 (OLD) "']
)
@pytest.mark.parametrize("resources", ["explicit", "missing"])
def test_native_classifier_rejects_every_text_show_operator(operation, resources):
    with fitz.open() as doc, fitz.open() as candidate:
        doc.new_page()
        font = doc[0].insert_font("helv")
        form = _form(
            doc,
            b"BT /helv 12 Tf " + operation + b" ET",
            f"<< /Font << /helv {font} 0 R >> >>",
        )
        if resources == "missing":
            doc.xref_set_key(form, "Resources", "null")
        graft = PageResourceGraft(doc, candidate)
        assert not graft._text_free_form(graft.target.pdf_new_indirect(form, 0))


@pytest.mark.parametrize(
    "fault", ["missing", "cycle", "depth", "exception", "incomplete"]
)
def test_native_classification_failures_keep_candidate_graft(monkeypatch, fault):
    with fitz.open() as doc, fitz.open() as candidate:
        doc.new_page()
        form = _form(doc, b"/Missing Do" if fault == "missing" else b"q Q")
        if fault == "cycle":
            doc.update_stream(form, b"/Self Do")
            doc.xref_set_key(
                form, "Resources", f"<< /XObject << /Self {form} 0 R >> >>"
            )
        elif fault == "depth":
            monkeypatch.setattr(PageResourceGraft, "_MAX_FORM_DEPTH", 4)
            for _ in range(PageResourceGraft._MAX_FORM_DEPTH):
                form = _form(
                    doc, b"/Child Do", f"<< /XObject << /Child {form} 0 R >> >>"
                )
        elif fault == "exception":

            def fail(*args):
                raise RuntimeError("injected native parser error")

            monkeypatch.setattr(mupdf, "pdf_process_contents", fail)
        elif fault == "incomplete":
            original = mupdf.pdf_process_contents

            def incomplete(scan, pdf, resources, stream, cookie):
                original(scan, pdf, resources, stream, cookie)
                cookie.m_internal.incomplete = 1
                assert cookie.incomplete() == 1

            monkeypatch.setattr(mupdf, "pdf_process_contents", incomplete)
        doc.xref_set_key(
            doc[0].xref, "Resources", f"<< /XObject << /ART {form} 0 R >> >>"
        )
        candidate.insert_pdf(doc, final=False)
        graft = PageResourceGraft(doc, candidate)
        graft.remember_resources(doc, 0, redacting=True)
        mapped = candidate.Graftmaps[doc._graft_id].this.pdf_graft_mapped_object(
            graft.target.pdf_new_indirect(form, 0)
        )
        assert (
            mapped.pdf_dict_gets(graft._LINEAGE_KEY).m_internal_value()
            not in graft.tags
        )
        # A redaction-created Form without a trusted token is copied, never rebound.
        clone = candidate.get_new_xref()
        candidate.update_object(
            clone,
            "<< /Type /XObject /Subtype /Form /BBox [0 0 500 500] /Resources << >> >>",
        )
        candidate.update_stream(clone, b"q Q")
        copied = graft.copy(graft.source.pdf_new_indirect(clone, 0))
        assert copied.pdf_to_num() != form
        assert not _tagged_objects(doc)


def test_changed_inline_text_form_stabilizes_after_first_redaction(tmp_path):
    doc, model, (picture, _, _) = _fixture()
    font = doc[0].insert_font("helv")
    doc.xref_set_key(picture, "Resources", f"<< /Font << /helv {font} 0 R >> >>")
    doc.update_stream(
        picture,
        doc.xref_stream(picture) + b" BT /helv 12 Tf 30 160 Td (OLD-MIXED) Tj ET",
    )
    target = tmp_path / "mixed.pdf"
    first_size = None
    first_counts = None
    for word in ("FIRST", "SECOND", "THIRD"):
        for index in range(4):
            PdfService.add_text_layer_batch(
                doc, model, _data([index], word), overwrite=True
            )
        PdfService.save_with_rewrite(
            doc, model, str(target), rewrite_text_layers=False, copy_export=True
        )
        with fitz.open(target) as output:
            assert [page.get_text().strip() for page in output] == [
                f"{word}{i}" for i in range(4)
            ]
            forms, patterns = _resources(output)
            fonts = {font[0] for page in output for font in page.get_fonts()}
            counts = (len(forms), len(patterns), len(fonts))
            if first_size is None:
                first_size = target.stat().st_size
                first_counts = counts
            else:
                assert counts == first_counts
                assert target.stat().st_size <= first_size + 4096
    assert not _tagged_objects(doc)
    doc.close()


def test_missing_resource_form_does_not_inherit_or_cache_a_parent_scope():
    with fitz.open() as doc, fitz.open() as candidate:
        doc.new_page()
        font = doc[0].insert_font("helv")
        child = _form(doc, b"/TEXT Do")
        doc.xref_set_key(child, "Resources", "null")
        parents = []
        for index, word in enumerate(("PARENT-A", "PARENT-B")):
            text = _form(
                doc,
                b"BT /helv 12 Tf 30 160 Td (" + word.encode() + b") Tj ET",
                f"<< /Font << /helv {font} 0 R >> >>",
            )
            parent = _form(
                doc,
                b"/CHILD Do",
                f"<< /XObject << /TEXT {text} 0 R /CHILD {child} 0 R >> >>",
            )
            parents.append(parent)
            page = doc[index] if index == 0 else doc.new_page()
            doc.xref_set_key(
                page.xref, "Resources", f"<< /XObject << /PARENT {parent} 0 R >> >>"
            )
            page.set_contents(_stream(doc, b"/PARENT Do"))
            assert page.get_text().strip() == word
        candidate.insert_pdf(doc, final=False)
        graft = PageResourceGraft(doc, candidate)
        for index in range(2):
            graft.remember_resources(doc, index, redacting=True)
        assert not graft.tags
        assert all(
            not graft._text_free_form(graft.target.pdf_new_indirect(parent, 0))
            for parent in parents
        )

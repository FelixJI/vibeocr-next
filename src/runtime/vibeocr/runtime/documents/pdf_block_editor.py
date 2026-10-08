"""Single-page OCR correction with isolated mutable resources and reusable artwork."""

from __future__ import annotations

import logging
from dataclasses import dataclass, field

import pymupdf as fitz
from vibeocr.runtime.documents.models.pdf_document import PdfDocument
from vibeocr.runtime.documents.models.pdf_ocr_options import PdfGlobalSettings
from vibeocr.runtime.recognition.models.ocr_result import TextBlock

logger = logging.getLogger(__name__)


def _copy_dictionary(doc: fitz.Document, owner: int, key: str) -> int | None:
    kind, value = doc.xref_get_key(owner, key)
    if kind == "null":
        return None
    copied = doc.get_new_xref()
    doc.update_object(
        copied, doc.xref_object(int(value.split()[0])) if kind == "xref" else value
    )
    doc.xref_set_key(owner, key, f"{copied} 0 R")
    return copied


def _isolate_resources(doc: fitz.Document, owner: int, forms: dict[int, int]) -> None:
    resources = _copy_dictionary(doc, owner, "Resources")
    if resources is None:
        return
    _copy_dictionary(doc, resources, "Font")
    xobjects = _copy_dictionary(doc, resources, "XObject")
    if xobjects is None:
        return
    for name in doc.xref_get_keys(xobjects):
        kind, value = doc.xref_get_key(xobjects, name)
        if kind != "xref":
            continue
        source = int(value.split()[0])
        if doc.xref_get_key(source, "Subtype")[1] != "/Form":
            continue  # Images and font objects are immutable; their containers are copied.
        if source not in forms:
            copied = doc.get_new_xref()
            forms[source] = copied
            doc.update_object(copied, doc.xref_object(source))
            doc.update_stream(copied, doc.xref_stream(source))
            _isolate_resources(doc, copied, forms)
        doc.xref_set_key(xobjects, name, f"{forms[source]} 0 R")


def _keys(doc: fitz.Document, page: int) -> dict[str, str]:
    return {key: doc.xref_get_key(page, key)[1] for key in ("Contents", "Resources")}


@dataclass
class _Baseline:
    keys: dict[str, str]
    owned: set[int]
    committed: dict[str, str] = field(default_factory=dict)
    text_owned: set[int] = field(default_factory=set)


class PdfBlockEditor:
    """Owned by one backend session; callers hold its fitz lock throughout a write."""

    def __init__(self, doc: fitz.Document) -> None:
        self.doc = doc
        self._pages: dict[int, _Baseline] = {}
        self._retained: set[int] = set()

    def _retire(self, owned: set[int]) -> None:
        owned = owned | self._retained
        try:
            self._retire_unreferenced(owned)
        except Exception:
            # Collection is best effort: a successful commit must not become a fake
            # rollback because an old resource cannot be inspected or retired.
            self._retained.update(owned)
            logger.warning(
                "PDF block edit resources retained after collection failure",
                exc_info=True,
            )

    def _retire_unreferenced(self, owned: set[int]) -> None:
        if not owned:
            return
        doc = self.doc
        # Only retire our objects. Protect all live consumers, including copied pages
        # and cached baselines; no arbitrary PDF reference parser or whole-file rewrite.
        protected: set[int] = set().union(
            *(state.owned for state in self._pages.values())
        )
        fonts: set[int] = set()
        for page in doc:
            protected.update(page.get_contents())
            owners = [page.xref] + [item[0] for item in page.get_xobjects()]
            protected.update(owners)
            for owner in owners:
                for key in ("Resources", "Resources/Font", "Resources/XObject"):
                    kind, value = doc.xref_get_key(owner, key)
                    if kind == "xref":
                        protected.add(int(value.split()[0]))
            fonts.update(item[0] for item in page.get_fonts(full=True))
        pending = list(fonts)
        protected.update(fonts)
        while pending:
            font = pending.pop()
            for key in doc.xref_get_keys(font):
                kind, value = doc.xref_get_key(font, key)
                targets = []
                if kind == "xref":
                    targets = [int(value.split()[0])]
                elif key == "DescendantFonts" and kind == "array":
                    tokens = value.strip("[] ").split()
                    if len(tokens) % 3 or any(
                        tokens[i] != "R" for i in range(2, len(tokens), 3)
                    ):
                        self._retained.update(owned)
                        return  # Unexpected font shape: conservatively retain our objects.
                    targets = [int(tokens[i]) for i in range(0, len(tokens), 3)]
                for target in targets:
                    if target not in protected:
                        protected.add(target)
                        pending.append(target)
        self._retained = owned & protected
        for xref in owned - protected:
            doc.update_object(xref, "null")
        # ponytail: empty xref slots remain until normal save garbage collection;
        # small empty slots accumulate linearly until a save performs garbage collection.

    def invalidate(self, pages: list[int]) -> None:
        retired: set[int] = set()
        for index in pages:
            if 0 <= index < self.doc.page_count:
                state = self._pages.pop(self.doc.page_xref(index), None)
                if state is not None:
                    retired.update(state.owned | state.text_owned)
        self._retire(retired)

    def prune(self) -> None:
        live = {self.doc.page_xref(i) for i in range(self.doc.page_count)}
        retired: set[int] = set()
        for page in set(self._pages) - live:
            old = self._pages.pop(page)
            retired.update(old.owned | old.text_owned)
        self._retire(retired)

    def rewrite(
        self,
        model: PdfDocument,
        page_index: int,
        blocks: list[TextBlock],
        angle: int,
        settings: PdfGlobalSettings | None,
    ) -> tuple[int, int]:
        from vibeocr.runtime.documents.pdf_service import PdfService

        doc = self.doc
        settings = settings or PdfGlobalSettings()
        self.prune()
        target = doc.page_xref(page_index)
        original = _keys(doc, target)
        state = self._pages.get(target)
        stale = state is not None and state.committed != original
        if stale:
            self._pages.pop(target)
        baseline = (
            None if stale or state is None else _Baseline(state.keys, state.owned)
        )
        count, first_xref = doc.page_count, doc.xref_length()
        committed = False
        try:
            doc.fullcopy_page(page_index)
            temporary = doc.page_xref(count)
            doc.xref_set_key(temporary, "Annots", "null")
            if baseline is None:
                _isolate_resources(doc, temporary, {})
                # Trusted OCR only: same whole-layer replacement semantics as the old
                # strict candidate, also remove whitespace that would retain old fonts.
                page = doc[count]
                page.add_redact_annot(page.rect * page.derotation_matrix, fill=None)
                page.apply_redactions(images=fitz.PDF_REDACT_IMAGE_NONE, graphics=0)
                page.clean_contents(sanitize=True)
                if page.get_text().strip():
                    return 0, len(blocks)
                baseline = _Baseline(
                    _keys(doc, temporary), set(range(first_xref, doc.xref_length()))
                )
            else:
                for key, value in baseline.keys.items():
                    doc.xref_set_key(temporary, key, value)
            resources = _copy_dictionary(doc, temporary, "Resources")
            if resources is None:
                resources = doc.get_new_xref()
                doc.update_object(resources, "<<>>")
                doc.xref_set_key(temporary, "Resources", f"{resources} 0 R")
            fonts = _copy_dictionary(doc, resources, "Font")
            if fonts is None:
                fonts = doc.get_new_xref()
                doc.update_object(fonts, "<<>>")
                doc.xref_set_key(resources, "Font", f"{fonts} 0 R")
            with fitz.open() as text_doc:
                page = doc[count]
                text_page = text_doc.new_page(
                    width=page.mediabox.width, height=page.mediabox.height
                )
                text_page.set_mediabox(page.mediabox)
                text_page.set_cropbox(page.cropbox)
                text_page.set_rotation(page.rotation)
                written, skipped = PdfService._write_blocks_to_page(
                    text_doc, 0, blocks, angle, settings
                )
                if written != len(blocks) or skipped:
                    return 0, max(skipped, len(blocks) - written)
                imported = doc.page_count
                doc.insert_pdf(
                    text_doc, links=False, annots=False, widgets=False, final=True
                )
                text_resources = _copy_dictionary(
                    doc, doc.page_xref(imported), "Resources"
                )
                if text_resources is None or set(doc.xref_get_keys(text_resources)) != {
                    "Font"
                }:
                    raise ValueError("Text-only candidate has unexpected resources")
                text_fonts = _copy_dictionary(doc, text_resources, "Font")
                if text_fonts is None:
                    raise ValueError("Text-only candidate has no font resources")
                for name in doc.xref_get_keys(text_fonts):
                    doc.xref_set_key(fonts, name, doc.xref_get_key(text_fonts, name)[1])
                contents = doc[count].get_contents() + doc[imported].get_contents()
                doc.xref_set_key(
                    temporary,
                    "Contents",
                    "[" + " ".join(f"{xref} 0 R" for xref in contents) + "]",
                )
                doc.delete_page(imported)
            prepared = _keys(doc, temporary)
            doc.xref_set_key(target, "Contents", prepared["Contents"])
            doc.xref_set_key(target, "Resources", prepared["Resources"])
            committed = True
            baseline.committed = prepared
            baseline.text_owned = (
                set(range(first_xref, doc.xref_length())) - baseline.owned
            )
            self._pages[target] = baseline
            info = model.pages[page_index]
            info.ocr_text_blocks = blocks
            info.ocr_preproc_angle = angle
            info.has_text_layer = True
            info.thumbnail = None
            model.is_modified = True
            return written, skipped
        except Exception:
            committed = False
            for key, value in original.items():
                doc.xref_set_key(target, key, value)
            if state is not None:
                self._pages[target] = state
            raise
        finally:
            while doc.page_count > count:
                doc.delete_page(doc.page_count - 1)
            if committed:
                if state is not None:
                    self._retire(state.text_owned | (state.owned if stale else set()))
            else:
                if state is not None:
                    self._pages[target] = state
                self._retire(set(range(first_xref, doc.xref_length())))

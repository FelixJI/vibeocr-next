"""Copy candidate content while retaining unchanged source resources."""

from __future__ import annotations

import pymupdf as fitz
from pymupdf import mupdf


class _TextOperations(mupdf.PdfProcessor2):
    """Ask MuPDF to parse show operations and actual Form calls, not stream bytes."""

    def __init__(self) -> None:
        super().__init__()
        self.shows_text = False
        self.forms: list[str] = []
        self.use_virtual_op_Tj()
        self.use_virtual_op_TJ()
        self.use_virtual_op_squote()
        self.use_virtual_op_dquote()
        self.use_virtual_op_Do_form()

    def op_Tj(self, *args: object) -> None:
        self.shows_text = True

    op_TJ = op_Tj
    op_squote = op_Tj
    op_dquote = op_Tj

    def op_Do_form(self, ctx: object, name: str, raw: object) -> None:
        self.forms.append(name)


class PageResourceGraft:
    _LINEAGE_KEY = "VibeOCRResourceSource"
    _MAX_FORM_DEPTH = 64

    def __init__(self, target: fitz.Document, candidate: fitz.Document) -> None:
        self.target = mupdf.pdf_document_from_fz_document(target.this)
        self.candidate = candidate
        self.source = mupdf.pdf_document_from_fz_document(candidate.this)
        self.resources: dict[int, mupdf.PdfObj] = {}
        self.copied: dict[int, mupdf.PdfObj] = {}
        self.values = mupdf.PdfGraftMap(self.target)
        self.visited: set[tuple[int, int]] = set()
        self.marked: set[tuple[int, int]] = set()
        self.text_free: dict[tuple[int, int], bool] = {}
        self.active: set[tuple[int, int]] = set()
        # Keep native tokens alive: pointer identity denotes only this candidate's
        # object lineage through MuPDF's shallow Form dictionary clone.
        self.tags: dict[int, tuple[mupdf.PdfObj, mupdf.PdfObj]] = {}

    def _text_free_form(self, obj: mupdf.PdfObj, depth: int = 0) -> bool:
        identity = (obj.pdf_to_num(), obj.pdf_to_gen())
        resources = obj.pdf_dict_gets("Resources")
        if resources.pdf_is_null():
            # Prove self-contained Forms with an empty scope. Never infer a
            # caller's inherited resources: unresolved Do calls must fail closed.
            resources = self.target.pdf_new_dict(0)
        if (
            not identity[0]
            or depth >= self._MAX_FORM_DEPTH
            or identity in self.active
            or not resources.pdf_is_dict()
            or obj.pdf_dict_gets("Subtype").pdf_to_name() != "Form"
            or not obj.pdf_is_stream()
        ):
            return False
        if identity in self.text_free:
            return self.text_free[identity]
        self.active.add(identity)
        try:
            scan = _TextOperations()
            cookie = mupdf.FzCookie()
            try:
                mupdf.pdf_process_contents(scan, self.target, resources, obj, cookie)
            finally:
                scan.pdf_close_processor()
            xobjects = resources.pdf_dict_gets("XObject")
            result = (
                not scan.shows_text
                and not cookie.errors()
                and not cookie.incomplete()
                and all(
                    self._text_free_form(xobjects.pdf_dict_gets(name), depth + 1)
                    for name in scan.forms
                )
            )
        except Exception:
            # A malformed or unsupported stream takes the ordinary candidate graft.
            result = False
        finally:
            self.active.remove(identity)
        self.text_free[identity] = result
        return result

    def remember_resources(
        self, original: fitz.Document, page: int, *, redacting: bool
    ) -> None:
        mapping = self.candidate.Graftmaps[original._graft_id].this
        page_object = self.target.pdf_lookup_page_obj(page)
        pending = [
            page_object.pdf_dict_get_inheritable(mupdf.pdf_new_name("Resources"))
        ]
        seen: set[tuple[int, int]] = set()
        while pending:
            obj = pending.pop()
            if obj.pdf_is_indirect():
                identity = (obj.pdf_to_num(), obj.pdf_to_gen())
                if identity in seen:
                    continue
                seen.add(identity)
                form = obj.pdf_dict_gets("Subtype").pdf_to_name() == "Form"
                resource = (
                    form
                    or obj.pdf_dict_gets("Subtype").pdf_to_name() == "Image"
                    or obj.pdf_dict_gets("PatternType").pdf_to_int() == 1
                )
                if resource:
                    copied = mapping.pdf_graft_mapped_object(obj)
                    if identity not in self.visited:
                        self.visited.add(identity)
                        self.resources[copied.pdf_to_num()] = obj
                    # A shared resource may first occur on an add-only page, then
                    # on a redacted page in this batch. Marking is independent.
                    if form and redacting and identity not in self.marked:
                        self.marked.add(identity)
                        keys = (
                            copied.pdf_dict_get_key(i).pdf_to_name()
                            for i in range(copied.pdf_dict_len())
                        )
                        if self._LINEAGE_KEY not in keys and self._text_free_form(obj):
                            token = self.source.pdf_new_array(0)
                            self.tags[token.m_internal_value()] = (token, obj)
                            copied.pdf_dict_puts(self._LINEAGE_KEY, token)
            if obj.pdf_is_dict():
                pending.extend(
                    obj.pdf_dict_get_val(i) for i in range(obj.pdf_dict_len())
                )
            elif obj.pdf_is_array():
                pending.extend(obj.pdf_array_get(i) for i in range(obj.pdf_array_len()))

    def copy(self, obj: mupdf.PdfObj) -> mupdf.PdfObj:
        number = obj.pdf_to_num()
        if number in self.resources:
            return self.resources[number]
        if number and obj.pdf_dict_gets("Subtype").pdf_to_name() == "Form":
            lineage = self.tags.get(
                obj.pdf_dict_gets(self._LINEAGE_KEY).m_internal_value()
            )
            if lineage is not None:
                return lineage[1]
        if number in self.copied:
            return self.copied[number]
        if number:
            # Reserve before descending: PDF resource graphs can contain cycles.
            reference = self.target.pdf_add_object(mupdf.PdfObj())
            self.copied[number] = reference
            value = self.copy_value(obj.pdf_resolve_indirect())
            self.target.pdf_update_object(reference.pdf_to_num(), value)
            if obj.pdf_is_stream():
                self.target.pdf_update_stream(reference, obj.pdf_load_raw_stream(), 1)
            return reference
        return self.copy_value(obj)

    def copy_value(self, obj: mupdf.PdfObj) -> mupdf.PdfObj:
        if obj.pdf_is_dict():
            result = self.target.pdf_new_dict(obj.pdf_dict_len())
            for index in range(obj.pdf_dict_len()):
                key = obj.pdf_dict_get_key(index)
                value = obj.pdf_dict_get_val(index)
                if (
                    key.pdf_to_name() == self._LINEAGE_KEY
                    and value.m_internal_value() in self.tags
                ):
                    continue
                result.pdf_dict_put(key, self.copy(value))
            return result
        if obj.pdf_is_array():
            result = self.target.pdf_new_array(obj.pdf_array_len())
            for index in range(obj.pdf_array_len()):
                result.pdf_array_push(self.copy(obj.pdf_array_get(index)))
            return result
        return self.values.pdf_graft_mapped_object(obj)

    def page_keys(self, page: int) -> tuple[str, str]:
        source = self.source.pdf_lookup_page_obj(page)
        references = []
        for key in ("Contents", "Resources"):
            value = self.copy(source.pdf_dict_gets(key))
            if not value.pdf_is_indirect():
                value = self.target.pdf_add_object(value)
            references.append(f"{value.pdf_to_num()} 0 R")
        return references[0], references[1]

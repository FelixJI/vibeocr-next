"""Copy candidate page content while retaining source image references."""

from __future__ import annotations

import pymupdf as fitz
from pymupdf import mupdf


class PageResourceGraft:
    def __init__(self, target: fitz.Document, candidate: fitz.Document) -> None:
        self.target = mupdf.pdf_document_from_fz_document(target.this)
        self.candidate = candidate
        self.source = mupdf.pdf_document_from_fz_document(candidate.this)
        self.images: dict[int, mupdf.PdfObj] = {}
        self.copied: dict[int, mupdf.PdfObj] = {}
        self.fonts = mupdf.PdfGraftMap(self.target)

    def remember_images(self, original: fitz.Document, page: int) -> None:
        mapping = self.candidate.Graftmaps[original._graft_id].this
        for image in original.get_page_images(page, full=True):
            for xref in image[:2]:
                if xref:
                    ref = self.target.pdf_new_indirect(xref, 0)
                    copied = mapping.pdf_graft_mapped_object(ref)
                    self.images[copied.pdf_to_num()] = ref

    def copy(self, obj: mupdf.PdfObj) -> mupdf.PdfObj:
        number = obj.pdf_to_num()
        if number in self.images:
            return self.images[number]
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
                # A single graft map preserves shared font programs and CID mappings.
                copied = (
                    self.fonts.pdf_graft_mapped_object(value)
                    if key.pdf_to_name() == "Font"
                    else self.copy(value)
                )
                result.pdf_dict_put(key, copied)
            return result
        if obj.pdf_is_array():
            result = self.target.pdf_new_array(obj.pdf_array_len())
            for index in range(obj.pdf_array_len()):
                result.pdf_array_push(self.copy(obj.pdf_array_get(index)))
            return result
        return self.fonts.pdf_graft_mapped_object(obj)

    def page_keys(self, page: int) -> tuple[str, str]:
        source = self.source.pdf_lookup_page_obj(page)
        references = []
        for key in ("Contents", "Resources"):
            value = self.copy(source.pdf_dict_gets(key))
            if not value.pdf_is_indirect():
                value = self.target.pdf_add_object(value)
            references.append(f"{value.pdf_to_num()} 0 R")
        return references[0], references[1]

"""Bounded topology inference for Paddle ORT cell-to-HTML conversion only.

Image-space boxes remain owned by PaddleX. The returned coordinates are private
boundary indexes, used only by its existing HTML generator (never for OCR/bbox).
"""

from __future__ import annotations

import math
from collections.abc import Sequence

from vibeocr.runtime.documents.tables.contracts import (
    MAX_TABLE_CELLS,
    TableCellV1,
    TableModelV1,
)


class TableGeometryError(ValueError):
    """Detected cells do not prove one complete, non-overlapping grid."""


def topology_coordinates(cells: Sequence[Sequence[float]]) -> list[list[int]]:
    if not 0 < len(cells) <= MAX_TABLE_CELLS:
        raise TableGeometryError(
            "Paddle table cell count is empty or exceeds the limit"
        )
    boxes = []
    for cell in cells:
        if len(cell) != 4:
            raise TableGeometryError("Paddle table cell must have four coordinates")
        box = tuple(float(value) for value in cell)
        if (
            not all(math.isfinite(value) for value in box)
            or box[2] <= box[0]
            or box[3] <= box[1]
        ):
            raise TableGeometryError("Paddle table cell coordinates are invalid")
        boxes.append(box)

    def boundaries(axis: int) -> tuple[dict[tuple[int, int], int], int]:
        endpoints = sorted(
            (box[axis + side * 2], index, side, box[axis + 2] - box[axis])
            for index, box in enumerate(boxes)
            for side in (0, 1)
        )
        mapping = {}
        groups = []
        for position, index, side, extent in endpoints:
            if groups:
                group = groups[-1]
                # Complete cluster diameter, not adjacent-point chaining. A narrow
                # cell constrains its own boundary even beside very wide/merged cells.
                joins = (
                    position - group["start"] <= 0.05 * min(group["extent"], extent)
                    and index not in group["cells"]
                )
            else:
                joins = False
            if not joins:
                groups.append(
                    {"start": position, "extent": extent, "cells": set(), "ends": []}
                )
            group = groups[-1]
            group["extent"] = min(group["extent"], extent)
            group["cells"].add(index)
            group["ends"].append((index, side))
            mapping[index, side] = len(groups) - 1

        # A slightly sloped shared edge can straddle two complete clusters.
        # Join only adjacent clusters proved by facing edges of actual neighbours,
        # never by proximity between arbitrary same-facing edges.
        merged = []
        other = 1 - axis
        for group in groups:
            previous = merged[-1] if merged else None
            shared_edge = False
            if previous is not None and not previous["cells"] & group["cells"]:
                for first, first_side in previous["ends"]:
                    for second, second_side in group["ends"]:
                        if first_side == second_side:
                            continue
                        before, after = (
                            (first, second) if first_side == 1 else (second, first)
                        )
                        a, b = boxes[before], boxes[after]
                        low, high = (
                            max(a[other], b[other]),
                            min(a[other + 2], b[other + 2]),
                        )
                        if high - low <= 0.5 * min(
                            a[other + 2] - a[other], b[other + 2] - b[other]
                        ):
                            continue
                        edge_a, edge_b = a[axis + 2], b[axis]
                        if abs(edge_b - edge_a) > 0.05 * min(
                            a[axis + 2] - a[axis], b[axis + 2] - b[axis]
                        ):
                            continue
                        # A real intervening narrow cell must block the adjacency
                        # claim even when it exists in only part of the shared band.
                        if any(
                            index not in (before, after)
                            and min(edge_a, edge_b)
                            < (box[axis] + box[axis + 2]) / 2
                            < max(edge_a, edge_b)
                            and min(high, box[other + 2]) > max(low, box[other])
                            for index, box in enumerate(boxes)
                        ):
                            continue
                        shared_edge = True
                        break
                    if shared_edge:
                        break
            if shared_edge:
                previous["cells"].update(group["cells"])
                previous["ends"].extend(group["ends"])
            else:
                merged.append(group)
        mapping = {
            endpoint: boundary
            for boundary, group in enumerate(merged)
            for endpoint in group["ends"]
        }
        return mapping, len(merged) - 1

    x, columns = boundaries(0)
    y, rows = boundaries(1)
    logical = tuple(
        TableCellV1(
            cell_id=f"cell-{index}",
            row=y[index, 0],
            column=x[index, 0],
            rowspan=y[index, 1] - y[index, 0],
            colspan=x[index, 1] - x[index, 0],
            text="",
        )
        for index in range(len(boxes))
    )
    try:
        TableModelV1(
            table_id="paddle-cell-topology",
            row_count=rows,
            column_count=columns,
            cells=logical,
        )
    except ValueError as exc:
        raise TableGeometryError(
            f"Paddle table cell topology is inconsistent: {exc}"
        ) from exc
    if sum(cell.rowspan * cell.colspan for cell in logical) != rows * columns:
        raise TableGeometryError(
            "Paddle table cell topology has missing or ambiguous regions"
        )
    # Upstream uses a fixed five-unit edge tolerance. Exact index spacing avoids
    # changing that algorithm; these values never replace real detected boxes.
    return [
        [x[index, 0] * 20, y[index, 0] * 20, x[index, 1] * 20, y[index, 1] * 20]
        for index in range(len(boxes))
    ]

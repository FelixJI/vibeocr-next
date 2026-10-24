from __future__ import annotations

import json
from pathlib import Path

import pytest
from vibeocr.runtime.recognition.paddle_table_geometry import (
    TableGeometryError,
    topology_coordinates,
)

FIXTURE = Path(__file__).parents[3] / "fixtures" / "paddle_ort_cell_boundaries.json"


@pytest.mark.parametrize("tag", ["upright", "unwarped"])
@pytest.mark.parametrize("scale", [0.01, 1, 7])
def test_real_detected_edges_preserve_four_by_four_under_scale_and_translation(
    tag, scale
):
    boxes = json.loads(FIXTURE.read_text())["boxes"][tag]
    transformed = [
        [
            value * scale + (135 if index % 2 == 0 else -50)
            for index, value in enumerate(box)
        ]
        for box in boxes
    ]
    saved = [list(box) for box in transformed]
    assert topology_coordinates(transformed) == [
        [column * 20, row * 20, (column + 1) * 20, (row + 1) * 20]
        for row in range(4)
        for column in range(4)
    ]
    assert transformed == saved


def test_narrow_column_only_in_one_row_and_legal_colspans_are_preserved():
    boxes = [
        [0, 0, 300, 50],
        [0.01, 50.1, 100.01, 100],
        [100.02, 50, 101.02, 100],
        [101.01, 50, 300.01, 100],
        [0, 100.1, 101.03, 150],
        [101.02, 100, 300.02, 150],
    ]
    assert topology_coordinates(boxes) == [
        [0, 0, 60, 20],
        [0, 20, 20, 40],
        [20, 20, 40, 40],
        [40, 20, 60, 40],
        [0, 40, 40, 60],
        [40, 40, 60, 60],
    ]


def test_rowspans_colspans_and_unequal_row_heights_keep_the_observed_topology():
    boxes = [
        [0, 0, 300, 40],
        [0, 40, 80, 140],
        [80, 40, 200, 90],
        [200, 40, 300, 90],
        [80, 90, 300, 140],
    ]
    assert topology_coordinates(boxes) == [
        [0, 0, 60, 20],
        [0, 20, 20, 60],
        [20, 20, 40, 40],
        [40, 20, 60, 40],
        [20, 40, 60, 60],
    ]


@pytest.mark.parametrize(
    "boxes",
    [
        [[0, 0, 100, 50], [100, 0, 200, 50], [0, 50, 100, 100]],
        [[0, 0, 100, 50], [0, 0, 100, 50]],
        [[row * 4, row * 50, 100 + row * 4, (row + 1) * 50] for row in range(4)],
        [[0, 0, float("nan"), 50]],
        [],
    ],
)
def test_incomplete_overlapping_or_ambiguous_cells_fail_instead_of_inventing_cells(
    boxes,
):
    with pytest.raises(TableGeometryError):
        topology_coordinates(boxes)


def test_real_eighteen_cells_use_facing_neighbour_edges_without_changing_boxes():
    boxes = json.loads(FIXTURE.read_text())["unequal_rows"]["boxes"]
    saved = [list(box) for box in boxes]
    logical = topology_coordinates(boxes)
    assert len({(box[0], box[1]) for box in logical}) == 18
    assert {tuple(box) for box in logical} == {
        (column * 20, row * 20, (column + 1) * 20, (row + 1) * 20)
        for row in range(6)
        for column in range(3)
    }
    assert boxes == saved


def test_unaligned_partial_row_narrow_cell_blocks_wide_edge_join_and_fails():
    boxes = [
        [0, 0, 100, 50],
        [100.2, 0, 101, 50],
        [101.2, 0, 220, 50],
        [0, 50, 101.1, 100],
        [101.2, 50, 220, 100],
    ]
    # Its 0.2-unit gaps are not justified by the 0.8-unit cell width. The
    # adapter must reject this geometry rather than join the broad neighbours
    # across the intervening cell and silently reduce the table to two columns.
    with pytest.raises(TableGeometryError, match="missing or ambiguous"):
        topology_coordinates(boxes)

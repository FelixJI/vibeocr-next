from __future__ import annotations

import pytest

from scripts.verify_paddle_structure_native import _construction_only_role


@pytest.mark.parametrize(
    "e2e,cell_html,inactive",
    [
        (False, True, "wireless_table_rec_model"),
        (True, False, "wireless_table_cells_detection_model"),
        (False, False, None),
        (True, True, None),
    ],
)
def test_only_explicitly_unused_wireless_route_dependency_is_exempt(
    e2e, cell_html, inactive
):
    for role in (
        "wireless_table_rec_model",
        "wireless_table_cells_detection_model",
        "wired_table_rec_model",
        "wired_table_cells_detection_model",
        "general_ocr_pipeline.text_det_model",
        "general_ocr_pipeline.text_rec_model",
        "table_orientation_classify_model",
    ):
        assert _construction_only_role(
            "root." + role, wireless_e2e=e2e, wireless_cell_html=cell_html
        ) is (role == inactive)

"""Grid rows of design section 13: paste, delete, paste into a grid, a new grid from
one block. Runs after the build context tests, in the same world.

The player starts inside the Earth base, where every line of sight ends at a wall
within 15 m. These tests teleport the character onto the free edge of the test
station and paste their grid into the open air beyond it, so the crosshair
reaches it and nothing else.
"""

from __future__ import annotations

import math
import time
import xml.etree.ElementTree as ET

import pytest

import rig
from conftest import wait_until
from se_remote import CallOp

PASTE_NAME = "Undo Paste Test"
# A 3x3 wall facing the character, a big target for the look-at aim
PASTE_CELLS = [(0, y, z) for y in (-1, 0, 1) for z in (-1, 0, 1)]
# Station cells: where the character stands, where the test grid goes
VANTAGE = (15, 0.6, 4)
PASTE_AT = (24, 1, 4)


@pytest.fixture(scope="module", autouse=True)
def on_the_station(game):
    api = game.api
    if api.get_character()["state"] == "sitting":
        api.key("F")
        wait_until(
            lambda: api.get_character()["state"] != "sitting", "leaving the seat"
        )
    target = rig.station_point(VANTAGE)
    api.character_teleport(*target)
    wait_until(
        lambda: math.dist(api.get_character()["position"], target) < 1.0,
        "the teleport onto the station",
    )


def paste_test_grid(game) -> dict:
    _, forward, up = rig.station_frame()
    last = game.last_node_id()
    (pasted,) = game.api.paste_blueprint(
        xml=rig.blueprint_xml(PASTE_NAME, PASTE_CELLS),
        position=rig.station_point(PASTE_AT),
        forward=forward,
        up=up,
    )
    game.wait_recorded(last, f"pasted {PASTE_NAME}")
    return pasted


def pasted_grids(game) -> list[dict]:
    return [g for g in game.grids() if g.get("name") == PASTE_NAME]


def has_all_cells(game, grid_id: int) -> bool:
    result = game.api.call([CallOp.cube_exists(grid_id, cell) for cell in PASTE_CELLS])
    return all(result.call(i)["exists"] for i in range(len(PASTE_CELLS)))


def store_rows() -> list[ET.Element]:
    (index,) = (rig.APPDATA / "Undo" / "Worlds").glob("*/grids/index.xml")
    return list(ET.parse(index).getroot().iter("StoreRow"))


def test_paste_then_undo_and_redo(game):
    pasted = paste_test_grid(game)
    node = game.build()["nodes"][-1]

    # The node refers to its store entry, whose row describes the grid
    (entry,) = node["storeRefs"]
    row = next(r for r in store_rows() if r.get("Id") == entry)
    assert row.get("Reason") == "Pasted"
    assert row.get("MainGridName") == PASTE_NAME
    assert row.get("BlockCount") == str(len(PASTE_CELLS))
    assert row.get("MainGridEntityId") == str(pasted["entityId"])

    assert game.undo() == f"Undo: pasted {PASTE_NAME}"
    wait_until(lambda: not pasted_grids(game), "the pasted grid to go")

    assert game.redo() == f"Redo: pasted {PASTE_NAME}"
    (grid,) = wait_until(lambda: pasted_grids(game), "the grid to return")
    # A local server creates it again without remapping, under the same id
    assert grid["entityId"] == pasted["entityId"]
    assert grid["isStatic"]
    assert has_all_cells(game, grid["entityId"])

    game.undo()
    wait_until(lambda: not pasted_grids(game), "the grid to go again")


def test_delete_grid_then_undo(game):
    """Remote's grid_close sends the player's close request, which is recorded"""
    pasted = paste_test_grid(game)
    last = game.last_node_id()

    assert game.api.close_grid(pasted["entityId"])["closed"]
    wait_until(lambda: not pasted_grids(game), "the closed grid to go")
    node = game.wait_recorded(last, f"deleted {PASTE_NAME}")
    (entry,) = node["storeRefs"]
    assert any(
        r.get("Id") == entry and r.get("Reason") == "Deleted" for r in store_rows()
    )

    assert game.undo() == f"Undo: deleted {PASTE_NAME}"
    (grid,) = wait_until(lambda: pasted_grids(game), "the grid to come back")
    assert grid["entityId"] == pasted["entityId"]
    assert has_all_cells(game, grid["entityId"])

    assert game.redo() == f"Redo: deleted {PASTE_NAME}"
    wait_until(lambda: not pasted_grids(game), "the grid to go again")

    game.undo()
    wait_until(lambda: pasted_grids(game), "the grid to come back again")
    # Leave the world without it
    game.undo(expect=f"Undo: pasted {PASTE_NAME}")
    wait_until(lambda: not pasted_grids(game), "the pasted grid to go")


def test_clipboard_delete_is_one_node(game):
    """Ctrl-Delete deletes the grid's group, one close request per grid, and records
    them as a single node"""
    api = game.api
    pasted = paste_test_grid(game)
    last = game.last_node_id()

    aim_at(api, pasted)
    api.key("Delete", ["LeftControl"])
    box = wait_until(
        lambda: next(
            (s for s in api.list_screens() if s.get("type") == "MyGuiScreenMessageBox"),
            None,
        ),
        "the delete confirmation",
    )
    api.control_click(text="Yes", screen=box["index"])
    wait_until(lambda: not pasted_grids(game), "the deleted grid to go")
    game.wait_recorded(last, f"deleted {PASTE_NAME}")
    time.sleep(0.5)
    assert [n["id"] for n in game.build()["nodes"] if n["id"] > last] == [
        game.last_node_id()
    ]

    assert game.undo() == f"Undo: deleted {PASTE_NAME}"
    (grid,) = wait_until(lambda: pasted_grids(game), "the grid to come back")
    assert grid["entityId"] == pasted["entityId"]
    game.undo(expect=f"Undo: pasted {PASTE_NAME}")
    wait_until(lambda: not pasted_grids(game), "the pasted grid to go")


def click(api) -> None:
    """A left click as gameplay input; the GUI click endpoint does not reach the
    clipboard or the cube builder"""
    api.set_input_state(mouse_left=True, mode="override")
    time.sleep(0.2)
    api.clear_input_state()


def aim_at(api, grid: dict) -> None:
    def aimed():
        api.character_look_at(*grid["position"])
        target = api.get_character_target(max_distance=200).get("block") or {}
        return target.get("gridId") == grid["entityId"]

    wait_until(aimed, "the crosshair on the test grid", interval=0.3)


def test_paste_into_grid_then_undo(game):
    """Copies the wall and pastes it onto the wall's face, which the clipboard
    snaps to the wall and sends as a paste into that grid"""
    api = game.api
    wall = paste_test_grid(game)
    grid = wall["entityId"]
    # The face towards the character is the wall's -X side
    merged = [(x - 1, y, z) for x, y, z in PASTE_CELLS]

    def merged_cells() -> int:
        result = api.call([CallOp.cube_exists(grid, cell) for cell in merged])
        return sum(result.call(i)["exists"] for i in range(len(merged)))

    aim_at(api, wall)
    last = game.last_node_id()
    api.key("C", ["LeftControl"])
    api.key("V", ["LeftControl"])
    time.sleep(1)  # the preview snaps to the wall once it updated
    aim_at(api, wall)
    click(api)
    game.wait_recorded(last, f"pasted {len(merged)} blocks into {PASTE_NAME}")
    assert merged_cells() == len(merged)

    assert game.undo().startswith("Undo: pasted 9 blocks")
    wait_until(lambda: merged_cells() == 0, "the pasted blocks to go")
    assert has_all_cells(game, grid)

    assert game.redo().startswith("Redo: pasted 9 blocks")
    wait_until(lambda: merged_cells() == len(merged), "the pasted blocks to return")

    game.undo()
    wait_until(lambda: merged_cells() == 0, "the pasted blocks to go again")
    game.undo(expect=f"Undo: pasted {PASTE_NAME}")
    wait_until(lambda: not pasted_grids(game), "the wall to go")


def test_new_grid_from_one_block_then_undo(game):
    """The cube builder aimed at empty air spawns a new one block grid. It is a
    ship and falls, so the test undoes it within a couple of seconds."""
    api = game.api
    api.set_toolbar_slot(0, "MyObjectBuilder_CubeBlock/LargeBlockArmorBlock")
    api.character_look_at(*rig.station_point((24, 1, -4)))
    assert not api.get_character_target(max_distance=200)["hit"]

    def new_grids():
        return [g for g in game.grids() if g["name"].startswith("Large Grid")]

    assert not new_grids()
    last = game.last_node_id()
    api.key("D1")
    wait_until(
        lambda: (api.get_character().get("weapon") or {}).get("type") == "MyCubePlacer",
        "the cube builder",
    )
    try:
        time.sleep(0.5)  # the gizmo follows the camera from the next frame
        click(api)
        (grid,) = wait_until(new_grids, "the new grid")
        game.wait_recorded(last, f"placed 1 block, new grid {grid['name']}")

        assert game.undo().startswith("Undo: placed 1 block, new grid")
        wait_until(lambda: not new_grids(), "the new grid to go")

        assert game.redo().startswith("Redo: placed 1 block, new grid")
        (again,) = wait_until(new_grids, "the new grid to return")
        assert again["entityId"] == grid["entityId"]

        game.undo()
        wait_until(lambda: not new_grids(), "the new grid to go again")
    finally:
        api.key("D0")

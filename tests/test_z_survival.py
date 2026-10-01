"""Permission row of design section 13, in a survival copy of the test world.
Sorted after test_world_save.py, since it loads a world of its own.

Offline the game counts everyone as having creative rights, so what keeps undo from
removing or restoring blocks for free there is the plugin's own check for creative
tools.
"""

from __future__ import annotations

import pytest

import rig
from harness import Game, wait_until

NEEDS_TOOLS = "Undo not available: needs creative tools"


@pytest.fixture(scope="module")
def survival(game):
    api = game.api
    rig.prepare_world(rig.SURVIVAL_WORLD, mode="Survival")
    rig.load_world(api, rig.SURVIVAL_WORLD)
    rig.ensure_character(api)
    rig.focus_gameplay(api)
    api.unpause()
    # Out of the cryo chamber: a seated character also builds from the inventories
    # the seat reaches through the conveyors, the base's cargo here
    if api.get_character()["state"] == "sitting":
        api.key("F")
        wait_until(
            lambda: api.get_character()["state"] != "sitting", "leaving the seat"
        )
    world = Game(api)
    assert world.status()["mode"] == "Offline"
    assert world.build()["count"] == 0
    return world


def creative_tools(game, on: bool) -> None:
    assert game.api.set_admin_flag("creativeTools", on)["creativeToolsEnabled"] is on


def test_raze_undo_needs_creative_tools(survival):
    """Undoing a build removes the block, which a survival player cannot do for free"""
    game, cell = survival, rig.BUILD_CELL
    last = game.last_node_id()

    assert game.api.character_build_block(game.station, cell)["sent"]
    wait_until(lambda: game.exists(cell), "the block")
    game.wait_recorded(last, "placed 1 block")
    # Without creative tools the game builds a construction site
    assert game.build_ratio(cell) < 1

    assert game.undo(expect=None) == NEEDS_TOOLS
    assert game.exists(cell)
    assert game.build()["current"] != 0

    creative_tools(game, True)
    assert game.undo() == "Undo: placed 1 block"
    wait_until(lambda: not game.exists(cell), "the block to go")


def test_restore_without_creative_tools_places_construction_sites(survival):
    """Undoing a removal builds the block again the way the player could: as a
    construction site paid from the inventory, with new ids and default settings"""
    game, cell = survival, rig.SECOND_LIGHT
    before = game.block(cell)
    last = game.last_node_id()

    creative_tools(game, True)
    game.api.character_grid_event(game.station, cell, "raze")
    wait_until(lambda: not game.exists(cell), "the removal")
    game.wait_recorded(last, "removed 1 block")

    creative_tools(game, False)
    assert game.undo(expect=None) == "Undo not available: missing components"
    assert not game.exists(cell)

    game.api.add_inventory_item("MyObjectBuilder_Component", "Construction", 5)
    message = game.undo()
    assert message == "Undo: removed 1 block (restored as construction sites)"
    wait_until(lambda: game.exists(cell), "the construction site")
    after = game.block(cell)
    assert after["definition"] == before["definition"]
    assert game.build_ratio(cell) < 1
    assert after["customName"] != before["customName"]

    # Removing it again is a raze: refused, then allowed with the tools
    assert game.redo(expect=None) == "Redo not available: needs creative tools"
    assert game.exists(cell)
    creative_tools(game, True)
    game.redo()
    wait_until(lambda: not game.exists(cell), "the removal again")

    # With the tools the block comes back as itself
    assert game.undo() == "Undo: removed 1 block"
    wait_until(lambda: game.exists(cell), "the restore")
    restored = game.block(cell)
    assert restored["entityId"] == before["entityId"]
    assert restored["customName"] == before["customName"]
    creative_tools(game, False)


def test_paste_and_close_need_creative_tools(survival):
    """A grid pasted with the tools on stays when they are off: closing it and
    pasting it again are both refused"""
    game = survival
    name = "Undo Survival Paste"
    position = rig.station_point((8, 12, 8))
    last = game.last_node_id()

    creative_tools(game, True)
    game.api.paste_blueprint(
        xml=rig.blueprint_xml(name, [(0, 0, 0), (1, 0, 0)]), position=position
    )
    game.wait_recorded(last, f"pasted {name}")

    creative_tools(game, False)
    assert game.undo(expect=None) == NEEDS_TOOLS
    assert any(g.get("name") == name for g in game.grids())

    creative_tools(game, True)
    game.undo()
    wait_until(
        lambda: all(g.get("name") != name for g in game.grids()), "the grid to go"
    )

    creative_tools(game, False)
    assert game.redo(expect=None) == "Redo not available: copy and paste is disabled"
    assert all(g.get("name") != name for g in game.grids())

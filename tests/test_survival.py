"""Permission row of design section 13, in a survival copy of the test world.
Sorted after test_world_save.py, since it loads a world of its own.

In survival the plugin is on only while creative tools are (design section 2).
Without them it records nothing and leaves the keys to the game. The tools can be
switched any time, and the history waits for them to come back.
"""

from __future__ import annotations

import pytest

import rig
from harness import Game, wait_until


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


def test_off_without_creative_tools(survival):
    """A block built by hand is not recorded, and the undo key is the game's"""
    game, cell = survival, rig.BUILD_CELL

    def build():
        assert game.api.character_build_block(game.station, cell)["sent"]
        wait_until(lambda: game.exists(cell), "the block")

    game.quiet(build)
    assert game.build()["count"] == 0
    game.quiet(lambda: game.api.key(*game.undo_key))
    assert game.exists(cell)


def test_the_history_waits_for_creative_tools(survival):
    """A removal recorded with the tools on is undone only once they are back on,
    and then with its full state"""
    game, cell = survival, rig.SECOND_LIGHT
    before = game.block(cell)
    last = game.last_node_id()

    creative_tools(game, True)
    game.api.character_grid_event(game.station, cell, "raze")
    wait_until(lambda: not game.exists(cell), "the removal")
    game.wait_recorded(last, "removed 1 block")

    # With something to undo, the keys are still the game's while the tools are off
    creative_tools(game, False)
    game.quiet(game.vanilla_keys, wait=0)
    assert not game.exists(cell)

    creative_tools(game, True)
    assert game.undo() == "Undo: removed 1 block"
    wait_until(lambda: game.exists(cell), "the restore")
    restored = game.block(cell)
    assert restored["entityId"] == before["entityId"]
    assert restored["customName"] == before["customName"]
    creative_tools(game, False)


def test_paste_and_close_wait_for_creative_tools(survival):
    """A grid pasted with the tools on stays while they are off"""
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
    game.quiet(lambda: game.api.key(*game.undo_key))
    assert any(g.get("name") == name for g in game.grids())

    creative_tools(game, True)
    game.undo()
    wait_until(
        lambda: all(g.get("name") != name for g in game.grids()), "the grid to go"
    )
    creative_tools(game, False)

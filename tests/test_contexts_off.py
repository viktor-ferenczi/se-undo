"""All three contexts switched off in the options: the plugin records nothing, takes
no key, and the vanilla keys it displaces while enabled work as in the vanilla game.
"""

from __future__ import annotations

import time

import pytest

import rig
from harness import wait_until

UNDO_CONFIG = {
    "EnableBuildContext": "false",
    "EnableTerminalContext": "false",
    "EnableTextContext": "false",
}

STAND = (2, 1.0, 14)
AIM = (2, 1.3, 15)


@pytest.fixture(scope="module", autouse=True)
def out_of_the_seat(game):
    game.stand_at(STAND)


def untouched(game) -> bool:
    status = game.status()
    return all(h["count"] == 0 for h in status["histories"].values())


def test_build_context_off(game):
    api, cell = game.api, rig.BUILD_CELL
    assert untouched(game)
    assert api.character_build_block(game.station, cell)["sent"]
    wait_until(lambda: game.exists(cell), "the block")
    api.character_grid_event(game.station, rig.PAINT_LIGHT, "color")
    game.api.paste_blueprint(
        xml=rig.blueprint_xml("Undo Unrecorded", [(0, 0, 0)]),
        position=rig.station_point((24, 1, 4)),
    )
    time.sleep(1.5)
    assert untouched(game)

    game.quiet(lambda: api.key(*game.undo_key))
    game.quiet(lambda: api.key(*game.redo_key))
    assert game.exists(cell)


def test_vanilla_keys_are_back(game):
    """Ctrl-Z is relative dampeners again, and Ctrl-H does not open the grid history"""
    api = game.api

    def dampeners():
        return api.get_character()["dampeners"]

    if dampeners():
        api.key("Z")
        wait_until(lambda: not dampeners(), "dampeners off")
    api.key("Z", ["LeftControl"])
    wait_until(dampeners, "relative dampeners on Ctrl-Z")

    api.key("H", ["LeftControl"])
    time.sleep(1)
    assert game.screen("GridHistoryScreen") is None
    # Vanilla toggles the render profiler on it; once more puts it back
    api.key("H", ["LeftControl"])


def test_terminal_context_off(game):
    api = game.api
    with game.open_terminal(AIM) as screen:
        before = api.get_property(game.station, rig.TURRET_CONTROLLER, "MultiplierAz")
        api.control_set("MultiplierAz", 0.7, screen=screen)
        time.sleep(1.5)
        changed = api.get_property(game.station, rig.TURRET_CONTROLLER, "MultiplierAz")
        assert changed["value"] != before["value"]
        assert untouched(game)

        game.quiet(lambda: api.key(*game.undo_key))
        after = api.get_property(game.station, rig.TURRET_CONTROLLER, "MultiplierAz")
        assert after["value"] == changed["value"]


def test_text_context_off(game):
    api = game.api
    with game.open_terminal(AIM, leave_search_box=False) as screen:
        focused = api.get_focus()["control"]

        def text():
            return game.control(screen, ident=focused["id"])["properties"]["text"]

        api.type_text("turret")
        wait_until(lambda: text() == "turret", "the typed text")
        time.sleep(0.8)
        game.quiet(lambda: api.key(*game.undo_key))
        assert text() == "turret"
    assert untouched(game)

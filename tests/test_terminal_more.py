"""More of the terminal context: the kinds of values a terminal control can hold,
toolbar actions, a multi selection, the coalescing window, programmable block
programs, and steps whose block is gone.

Changes are made from outside the terminal where that is enough (the rig records
those too) and undone in the terminal, where the terminal history has the keys.
"""

from __future__ import annotations

import time
from contextlib import contextmanager

import pytest

import rig
from harness import wait_until

STAND = (2, 1.0, 14)
AIM = (2, 1.3, 15)
LIGHT = rig.TARGET
LIGHT_NAME = rig.TARGET_NAME
OTHER = rig.SECOND_LIGHT
CONTROLLER_NAME = "Undo Turret Controller"
PB_NAME = "Undo Programmable Block"
# Longer than the coalescing window of 500 ms
PAUSE = 0.8


@pytest.fixture(scope="module", autouse=True)
def at_the_turret_controller(game):
    game.stand_at(STAND)


@contextmanager
def terminal(game):
    with game.open_terminal(AIM) as screen:
        yield screen


def prop(game, name: str, cell=LIGHT, grid=None):
    return game.api.get_property(grid or game.station, cell, name)["value"]


def set_prop(game, name: str, value, cell=LIGHT, grid=None) -> None:
    game.api.set_property(grid or game.station, cell, name, value)


def recorded(game, last: int, label: str) -> dict:
    return game.wait_recorded(last, label, history="terminal")


def last_id(game) -> int:
    return game.last_node_id("terminal")


def close(a, b) -> bool:
    if isinstance(a, dict):
        return all(close(a[k], b[k]) for k in a)
    if isinstance(a, float) or isinstance(b, float):
        return abs(a - b) < 0.01
    return a == b


@pytest.mark.parametrize(
    "name, value",
    [
        ("Radius", 12.5),
        ("OnOff", False),
        ("Color", {"r": 0.2, "g": 0.6, "b": 0.9}),
    ],
)
def test_value_kinds(game, name, value):
    """A slider, a switch and a color, each stored as text in the history"""
    label = f"changed {name} of {LIGHT_NAME}"
    before = prop(game, name)
    last = last_id(game)
    set_prop(game, name, value)
    recorded(game, last, label)
    changed = prop(game, name)
    assert close(changed, value) and not close(changed, before)

    with terminal(game):
        assert game.undo() == f"Undo: {label}"
        assert close(prop(game, name), before)
        assert game.redo() == f"Redo: {label}"
        assert close(prop(game, name), changed)
        game.undo()
    assert close(prop(game, name), before)


def test_toolbar_action_is_a_step(game):
    """What a toolbar slot or a hotkey does: the block's terminal action"""
    api = game.api
    before = prop(game, "Radius")
    last = last_id(game)
    api.apply_action(game.station, LIGHT, "IncreaseRadius")
    recorded(game, last, f"changed Radius of {LIGHT_NAME}")
    assert prop(game, "Radius") > before

    time.sleep(PAUSE)
    api.apply_action(game.station, LIGHT, "OnOff_Off")
    recorded(game, last + 1, f"changed OnOff of {LIGHT_NAME}")
    assert prop(game, "OnOff") is False

    with terminal(game):
        game.undo()
        assert prop(game, "OnOff") is True
        game.undo()
        assert close(prop(game, "Radius"), before)


def test_multi_selection_is_one_step_with_each_block_s_own_value(game):
    """Both lights of the block group selected, one slider moved: one node. The
    lights had different radii, and the undo gives each its own back."""
    api = game.api
    last = last_id(game)
    set_prop(game, "Radius", 5.0, LIGHT)
    recorded(game, last, f"changed Radius of {LIGHT_NAME}")
    time.sleep(PAUSE)
    set_prop(game, "Radius", 9.0, OTHER)
    recorded(game, last + 1, "changed Radius of Undo Light 2")
    time.sleep(PAUSE)

    with terminal(game) as screen:
        api.control_set("FunctionalBlockListbox", 0, screen=screen)
        items = game.control(screen, "FunctionalBlockListbox")["properties"]["items"]
        assert [i["text"] for i in items if i["selected"]] == [
            f"*{rig.GROUP_NAME}*",
            "Undo Light 2",
            LIGHT_NAME,
        ]

        api.control_set("Radius", 0.9, screen=screen)
        node = recorded(game, last + 2, "changed Radius of 2 blocks")
        both = prop(game, "Radius", LIGHT)
        assert both == prop(game, "Radius", OTHER) and both > 9.0

        assert game.undo() == f"Undo: {node['label']}"
        assert close(prop(game, "Radius", LIGHT), 5.0)
        assert close(prop(game, "Radius", OTHER), 9.0)
        for _ in range(2):
            game.undo()
    assert not close(prop(game, "Radius", LIGHT), 5.0)


def gray(level: float) -> dict:
    return {"r": level, "g": level, "b": level}


def test_changes_coalesce_per_control_inside_the_window(game):
    before = prop(game, "Color")
    last = last_id(game)

    # A burst on one control: one node, back to the value before the burst
    for level in (0.2, 0.3, 0.4):
        set_prop(game, "Color", gray(level))
    recorded(game, last, f"changed Color of {LIGHT_NAME}")
    time.sleep(PAUSE)
    assert last_id(game) == last + 1

    # The same control after a pause: a node of its own
    set_prop(game, "Color", gray(0.6))
    recorded(game, last + 1, f"changed Color of {LIGHT_NAME}")
    time.sleep(PAUSE)

    # Two controls with no pause: a node each
    set_prop(game, "Color", gray(0.8))
    set_prop(game, "OnOff", False)
    recorded(game, last + 3, f"changed OnOff of {LIGHT_NAME}")
    assert game.labels(last, "terminal")[2] == f"changed Color of {LIGHT_NAME}"

    with terminal(game):
        game.undo(expect=f"Undo: changed OnOff of {LIGHT_NAME}")
        game.undo()
        assert close(prop(game, "Color"), gray(0.6))
        game.undo()
        assert close(prop(game, "Color"), gray(0.4))
        game.undo()
        assert close(prop(game, "Color"), before)


def saved_program(game) -> str | None:
    station = game.saved_station()
    block = next(
        b
        for b in station.iter("MyObjectBuilder_CubeBlock")
        if b.findtext("EntityId") == str(rig.IDS[rig.PROGRAMMABLE])
    )
    return block.findtext("Program")


def test_program_of_a_programmable_block(game):
    api = game.api
    pb = rig.IDS[rig.PROGRAMMABLE]
    label = f"changed the program of {PB_NAME}"
    one = 'void Main() { Echo("one"); }'
    two = 'void Main() { Echo("two"); }'
    assert not saved_program(game)
    last = last_id(game)

    api.set_pb_program(pb, one)
    recorded(game, last, label)
    time.sleep(PAUSE)
    api.set_pb_program(pb, two)
    recorded(game, last + 1, label)
    assert saved_program(game) == two

    with terminal(game):
        assert game.undo() == f"Undo: {label}"
    assert saved_program(game) == one
    with terminal(game):
        game.undo()
    # Back to a block that never had a program
    assert not saved_program(game)
    with terminal(game):
        assert game.redo() == f"Redo: {label}"
        game.redo()
    assert saved_program(game) == two
    with terminal(game):
        game.undo()
        game.undo()
    assert not saved_program(game)


def test_step_of_a_removed_block_is_refused_until_the_block_is_back(game):
    label = "changed OnOff of Undo Light 2"
    last = last_id(game)
    set_prop(game, "OnOff", False, OTHER)
    recorded(game, last, label)
    count = game.terminal()["count"]

    game.raze_block(OTHER)
    with terminal(game):
        message = game.undo(expect=None)
        assert message == "Undo not available: the block no longer exists"
        assert game.terminal()["current"] == last + 1

    # Another block in its place is not that block either
    game.build_block(OTHER)
    with terminal(game):
        assert game.undo(expect=None) == message
    assert game.terminal()["count"] == count

    game.undo(expect="Undo: placed 1 block")
    wait_until(lambda: not game.exists(OTHER), "the armor block to go")
    game.undo(expect="Undo: removed 1 block")
    wait_until(lambda: game.exists(OTHER), "the light to come back")
    assert prop(game, "OnOff", OTHER) is False

    with terminal(game):
        assert game.undo() == f"Undo: {label}"
    assert prop(game, "OnOff", OTHER) is True


def test_block_on_another_grid(game):
    """The terminal history is the world's, not the open terminal's grid: a light
    on the ship far out in space is undone from the station's terminal"""
    ship, cell = rig.DRIFT_SHIP_ID, rig.DRIFT_LIGHT
    last = last_id(game)
    set_prop(game, "OnOff", False, cell, ship)
    recorded(game, last, "changed OnOff of Drift Light")
    with terminal(game):
        game.undo()
    assert prop(game, "OnOff", cell, ship) is True


def test_the_two_histories_do_not_touch_each_other(game):
    build = game.build()
    last = last_id(game)
    set_prop(game, "OnOff", False)
    recorded(game, last, f"changed OnOff of {LIGHT_NAME}")
    assert (game.build()["count"], game.build()["current"]) == (
        build["count"],
        build["current"],
    )

    # In gameplay the keys are the build history's: the switch stays
    if game.build()["current"] == 0:
        game.nothing_to_undo()
    else:
        game.undo()
        game.redo()
    assert prop(game, "OnOff") is False
    assert game.terminal()["current"] == last + 1

    with terminal(game):
        game.undo()
    assert prop(game, "OnOff") is True
    assert (game.build()["count"], game.build()["current"]) == (
        build["count"],
        build["current"],
    )


def test_combobox_after_other_controls(game):
    label = f"changed Content of {CONTROLLER_NAME}"
    last = last_id(game)
    with terminal(game) as screen:
        game.api.control_set("Content", 1, screen=screen)
        recorded(game, last, label)
        game.undo()

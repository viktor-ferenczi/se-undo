"""Terminal and text rows of design section 13. Runs after the build and grid tests,
in the same world.

The terminal is opened on the test station's turret controller with the injected F
key. The game offers "use" only when the view ray passes one of the block's terminal
detectors before it hits the model, and this block is an open frame with its console
inside, so the character stands in front of the console and looks at its screen.
The terminal history only takes Ctrl-Z while the terminal screen has focus, so the
tests that change something from outside open it for the undo.
"""

from __future__ import annotations

import math
import time
from contextlib import contextmanager

import pytest

import rig
from conftest import wait_until
from se_remote import CallOp

BLOCK = rig.TURRET_CONTROLLER
BLOCK_NAME = "Undo Turret Controller"
CHECKBOX = "EnableTargetLocking"
SLIDER = "MultiplierAz"
# Station cells: where the character stands, and the console screen it looks at
STAND = (BLOCK[0], 1.0, BLOCK[2] - 1.0)
AIM = (BLOCK[0], BLOCK[1] + 0.3, BLOCK[2])


@pytest.fixture(scope="module", autouse=True)
def at_the_turret_controller(game):
    api = game.api
    rig.focus_gameplay(api)
    if api.get_character()["state"] == "sitting":
        api.key("F")
        wait_until(
            lambda: api.get_character()["state"] != "sitting", "leaving the seat"
        )
    target = rig.station_point(STAND)
    api.character_teleport(*target)
    wait_until(
        lambda: math.dist(api.get_character()["position"], target) < 1.5,
        "the teleport in front of the turret controller",
    )


def terminal_index(api) -> int | None:
    return next(
        (
            i
            for i, s in enumerate(api.list_screens())
            if s.get("type") == "MyGuiScreenTerminal" and s.get("hasFocus")
        ),
        None,
    )


@contextmanager
def terminal(game):
    """The block's terminal, opened with F and closed again afterwards"""
    api = game.api
    api.character_look_at(*rig.station_point(AIM))

    def opened():
        if terminal_index(api) is None:
            api.key("F")
            time.sleep(0.5)
        return terminal_index(api) is not None

    wait_until(opened, "the terminal", interval=0.5)
    # Screens take no input while their opening transition runs
    time.sleep(1)
    try:
        yield terminal_index(api)
    finally:
        rig.focus_gameplay(api)


def control(game, screen: int, name: str | None = None, ident: str | None = None):
    """A control of the screen by name, or by its Remote id where names repeat"""
    found = []

    def walk(node):
        if node.get("id") == ident if ident else node.get("name") == name:
            found.append(node)
        for child in node.get("controls") or node.get("children") or []:
            walk(child)

    tree = game.api.get_controls(screen, depth=12)
    for node in tree if isinstance(tree, list) else tree.get("controls", []):
        walk(node)
    assert found, f"no control {name or ident}"
    return found[0]


def prop(game, name: str):
    return game.api.get_property(game.station, BLOCK, name)["value"]


def recorded(game, last: int, label: str) -> dict:
    return game.wait_recorded(last, label, history="terminal")


def test_checkbox_through_the_terminal(game):
    label = f"changed {CHECKBOX} of {BLOCK_NAME}"
    before = prop(game, CHECKBOX)
    last = game.last_node_id("terminal")

    with terminal(game) as screen:
        game.api.control_set(CHECKBOX, not before, screen=screen)
        recorded(game, last, label)
        assert prop(game, CHECKBOX) is (not before)

        # The search box has focus but no text history, so the terminal gets the key
        assert game.undo() == f"Undo: {label}"
        assert prop(game, CHECKBOX) is before
        wait_until(
            lambda: control(game, screen, CHECKBOX)["properties"]["isChecked"]
            is before,
            "the checkbox to follow the undo",
        )

        assert game.redo() == f"Redo: {label}"
        assert prop(game, CHECKBOX) is (not before)

        game.undo()
        assert prop(game, CHECKBOX) is before


def test_slider_through_the_terminal(game):
    label = f"changed {SLIDER} of {BLOCK_NAME}"
    before = prop(game, SLIDER)
    last = game.last_node_id("terminal")

    with terminal(game) as screen:
        # Three steps like a drag; they land in one node
        for value in (0.2, 0.5, 0.8):
            game.api.control_set(SLIDER, value, screen=screen)
        recorded(game, last, label)
        changed = prop(game, SLIDER)
        assert changed != before
        assert game.last_node_id("terminal") == last + 1

        assert game.undo() == f"Undo: {label}"
        assert prop(game, SLIDER) == before

        assert game.redo() == f"Redo: {label}"
        assert prop(game, SLIDER) == changed

        game.undo()
        assert prop(game, SLIDER) == before


def test_property_set_outside_the_terminal(game):
    """The rig records terminal changes with the terminal closed too"""
    label = f"changed {CHECKBOX} of {BLOCK_NAME}"
    before = prop(game, CHECKBOX)
    last = game.last_node_id("terminal")

    game.api.set_property(game.station, BLOCK, CHECKBOX, not before)
    recorded(game, last, label)

    # Gameplay has the build history; this one answers in the terminal only
    with terminal(game):
        assert game.undo() == f"Undo: {label}"
        assert prop(game, CHECKBOX) is before
        assert game.redo() == f"Redo: {label}"
        assert prop(game, CHECKBOX) is (not before)
        game.undo()
    assert prop(game, CHECKBOX) is before


def test_block_name(game):
    def name():
        return game.block(BLOCK)["customName"]

    # Through the Name box of the terminal, which renames on every text change
    last = game.last_node_id("terminal")
    with terminal(game) as screen:
        for text in ("Renamed", "Renamed Controller"):
            game.api.control_set("Name", text, screen=screen)
        label = f"renamed block {BLOCK_NAME} to Renamed Controller"
        recorded(game, last, label)
        assert name() == "Renamed Controller"
        assert game.last_node_id("terminal") == last + 1

        assert game.undo() == f"Undo: {label}"
        assert name() == BLOCK_NAME
        assert game.redo() == f"Redo: {label}"
        assert name() == "Renamed Controller"
        game.undo()
    assert name() == BLOCK_NAME

    # Through SetCustomName, which is what the mod API and Remote call
    last = game.last_node_id("terminal")
    game.api.set_custom_name(game.station, BLOCK, "Set From Outside")
    label = f"renamed block {BLOCK_NAME} to Set From Outside"
    recorded(game, last, label)
    assert game.last_node_id("terminal") == last + 1
    with terminal(game):
        assert game.undo() == f"Undo: {label}"
    assert name() == BLOCK_NAME


def test_grid_name(game):
    """Renamed through Remote's grid call, which sends the same request as the OK
    button of the Info tab. Switching to that tab through the Remote API crashes
    the client (SE1-0071)."""

    def name():
        return game.api.get_grid(game.station)["name"]

    renamed = "Undo Renamed Station"
    label = f"renamed grid {rig.STATION_NAME} to {renamed}"
    last = game.last_node_id("terminal")
    game.api.call(
        [CallOp.grid_method(game.station, "SetCustomName", {"name": renamed})]
    ).call(0)
    recorded(game, last, label)
    assert name() == renamed

    with terminal(game):
        assert game.undo() == f"Undo: {label}"
        assert name() == rig.STATION_NAME
        assert game.redo() == f"Redo: {label}"
        assert name() == renamed
        game.undo()
    assert name() == rig.STATION_NAME


def test_text_box_typing_then_undo(game):
    """The block search box of the terminal has the focus when it opens"""
    api = game.api
    with terminal(game) as screen:
        focused = api.get_focus()["control"]
        assert focused["type"] == "MyGuiControlTextbox"
        nodes = game.terminal()["count"]

        def text():
            # Every search box of the terminal names its text box the same
            return control(game, screen, ident=focused["id"])["properties"]["text"]

        def press(key: str, expected: str):
            api.key(key, ["LeftControl"])
            wait_until(lambda: text() == expected, f"{expected!r} after Ctrl-{key}")

        assert text() == ""
        api.type_text("turret")
        wait_until(lambda: text() == "turret", "the typed text")
        # Longer than the coalescing window, so the next word is its own step
        time.sleep(0.8)
        api.type_text(" c")
        wait_until(lambda: text() == "turret c", "more typed text")

        press("Z", "turret")
        press("Z", "")
        press("Y", "turret")
        press("Y", "turret c")
        press("Z", "turret")
        press("Z", "")

        # The text box took those keys; the terminal history saw none of them
        assert game.terminal()["count"] == nodes


@pytest.mark.skip(
    reason="The Remote API has no endpoint to read or write a PB program (SE1-0060)"
)
def test_pb_program():
    """Save a program in the editor, Ctrl-Z in the terminal, the old program is back"""

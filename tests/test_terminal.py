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

import time

import pytest

import rig
from harness import wait_until
from se_remote import GetOp

BLOCK = rig.TURRET_CONTROLLER
BLOCK_NAME = "Undo Turret Controller"
CHECKBOX = "EnableTargetLocking"
SLIDER = "MultiplierAz"
INFO_PAGE = 3
# Station cells: where the character stands, and the console screen it looks at
STAND = (BLOCK[0], 1.0, BLOCK[2] - 1.0)
AIM = (BLOCK[0], BLOCK[1] + 0.3, BLOCK[2])


@pytest.fixture(scope="module", autouse=True)
def at_the_turret_controller(game):
    game.stand_at(STAND)


def terminal(game, leave_search_box: bool = True):
    return game.open_terminal(AIM, leave_search_box)


def control(game, screen: int, name: str | None = None, ident: str | None = None):
    return game.control(screen, name, ident)


def focus_is_text(api) -> bool:
    return api.get_focus()["control"]["type"] == "MyGuiControlTextbox"


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
    """Renamed on the Info tab, with its text box and OK button"""

    def name():
        return game.api.get_grid(game.station)["name"]

    renamed = "Undo Renamed Station"
    label = f"renamed grid {rig.STATION_NAME} to {renamed}"
    last = game.last_node_id("terminal")
    with terminal(game) as screen:
        game.api.control_set("TerminalTabs", INFO_PAGE, screen=screen)
        wait_until(
            lambda: control(game, screen, "RenameShipText")["properties"]["text"]
            == rig.STATION_NAME,
            "the Info page",
        )
        game.api.control_set("RenameShipText", renamed, screen=screen)
        game.api.control_click(name="RenameShipButton", screen=screen)
        recorded(game, last, label)
        assert name() == renamed

        assert game.undo() == f"Undo: {label}"
        assert name() == rig.STATION_NAME
        assert game.redo() == f"Redo: {label}"
        assert name() == renamed
        game.undo()
    assert name() == rig.STATION_NAME


def test_text_box_typing_then_undo(game):
    """With the cursor in a text box the keys are local to it, with or without
    something to undo there. The terminal history takes them after leaving the box."""
    api = game.api
    with terminal(game, leave_search_box=False) as screen:
        focused = api.get_focus()["control"]
        before = game.terminal()
        message = game.last_message()

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

        # Nothing left to undo in the box: the key still stays there
        api.key("Z", ["LeftControl"])
        api.key("Y", ["LeftControl"])
        api.key("Y", ["LeftControl"])
        api.key("Y", ["LeftControl"])
        time.sleep(0.5)
        assert text() == "turret c"
        after = game.terminal()
        assert (after["count"], after["current"]) == (
            before["count"],
            before["current"],
        )
        assert game.last_message() == message

        # Out of the box, the same key is the terminal's
        api.key("Tab")
        wait_until(lambda: not focus_is_text(api), "the focus to leave the box")
        assert game.undo(expect=None).startswith(("Undo: ", "Nothing to undo"))
        assert text() == "turret c"


def test_a_block_type_new_to_the_world_has_its_controls(game):
    """The game creates the terminal controls of a block type with its first block.
    Asking the control factory about a type before that leaves it with an empty
    list for the rest of the session, so the plugin must not."""
    api, cell = game.api, rig.BUILD_CELL
    rig.focus_gameplay(api)
    last = game.last_node_id()
    assert api.character_build_block(game.station, cell, subtype="LargeWarhead")["sent"]
    wait_until(lambda: game.exists(cell), "the warhead")
    game.wait_recorded(last, "placed 1 block")
    try:
        result = api.batch(gets=[GetOp.properties(game.station, cell)])
        names = {p["id"] for p in result.get(0)}
        assert "DetonationTime" in names, names
    finally:
        game.undo()
        wait_until(lambda: not game.exists(cell), "the warhead to go")


def editor(api) -> int | None:
    return next(
        (
            i
            for i, s in enumerate(api.list_screens())
            if s.get("type") == "MyGuiScreenEditor"
        ),
        None,
    )


def program_of(game) -> str:
    return game.api.get_pb_program(rig.IDS[rig.PROGRAMMABLE]) or ""


def test_pb_program_saved_from_the_editor(game):
    """Two programs saved with the editor's OK button, each a node. Inside the
    editor Ctrl-Z is the editor's own; back in the terminal it takes the saves
    back one by one, down to a block without a program."""
    api = game.api
    label = "changed the program of Undo Programmable Block"
    game.stand_at((rig.PROGRAMMABLE[0], 1.0, rig.PROGRAMMABLE[2] - 1.0))
    aim = (rig.PROGRAMMABLE[0], rig.PROGRAMMABLE[1], rig.PROGRAMMABLE[2])
    assert program_of(game) == ""
    last = game.last_node_id("terminal")

    with game.open_terminal(aim) as screen:
        for word in ("// first ", "// second "):
            api.control_click(text="Edit", screen=screen)
            wait_until(lambda: editor(api) is not None, "the editor")
            time.sleep(1)  # the opening transition takes no input
            api.type_text(word)
            time.sleep(0.5)
            # The multi line editor keeps its vanilla undo; the plugin stays out
            game.quiet(lambda: api.key(*game.undo_key))
            api.control_click(text="OK", screen=editor(api))
            wait_until(lambda: editor(api) is None, "the editor to close")
            recorded(game, last, label)
            last += 1
            time.sleep(1)

    second = program_of(game)
    assert second.startswith("// second ") and "// first " in second

    with game.open_terminal(aim):
        assert game.undo() == f"Undo: {label}"
    first = program_of(game)
    assert first.startswith("// first ") and "// second " not in first

    with game.open_terminal(aim):
        game.undo()
    assert program_of(game) == ""

    with game.open_terminal(aim):
        assert game.redo() == f"Redo: {label}"
        game.redo()
    assert program_of(game) == second

"""The two checks before an edit, both switched on here (they are off by default):
a removal that would split the grid asks first, with or without symmetry, and
with symmetry on an edit that would not be the same at every mirror image is not
made, a message with an OK button says why.

Edits that pass go through as usual: four copies placed, a block removed that
holds nothing. A taken mirror cell stops the placement, a missing copy stops the
paint and the removal, and nothing changes. The bridge to part B stays on No and
goes on Yes, without the red split warning the question replaces.

The dialog texts are read from the client's status file. Each dialog is also
saved as a screenshot into tests/artifacts.
"""

from __future__ import annotations

import time

import rig
from harness import wait_until
from test_symmetry import (  # noqa: F401 -- the fixtures and EXTRA_GRIDS too
    EXTRA_GRIDS,
    LARGE,
    PAINT_SLOT,
    RED,
    aim_at_block,
    builder_in_hand,
    floors,
    mirror,
    place,
    probe,
    set_planes,
    symmetry_off,
)
from test_symmetry_warnings import GREEN, QUIET_S, restore_bridge, warnings

UNDO_CONFIG = {"ConfirmGridSplit": "true", "PreventSymmetryBreak": "true"}

SPLIT = "Removing 1 block would split Undo Test Station into 2 parts. Remove?"


def broken(done: int, of: int, verb: str) -> str:
    return (
        f"Nothing was changed: only {done} of {of} mirrored blocks would be {verb}, "
        "the edit would break the symmetry."
    )


def dialogs(game) -> list[str]:
    return game.status().get("dialogs", [])


def message_box(api):
    return next(
        (s for s in api.list_screens() if s.get("type") == "MyGuiScreenMessageBox"),
        None,
    )


def answer(game, before: list[str], text: str, button: str) -> None:
    """This dialog came, alone; it is closed with the button"""
    box = wait_until(lambda: message_box(game.api), f"the dialog: {text}")
    assert dialogs(game)[len(before) :] == [text]
    name = "split" if text == SPLIT else "symmetry"
    # The dialog fades in
    time.sleep(0.8)
    rig.ARTIFACTS.mkdir(parents=True, exist_ok=True)
    (rig.ARTIFACTS / f"dialog-{name}.png").write_bytes(game.api.screenshot_bytes())
    game.api.clear_input_state()
    game.api.control_click(text=button, screen=box["index"])
    wait_until(lambda: not message_box(game.api), "the dialog to close")
    rig.focus_gameplay(game.api)


def no_dialog(game, before: list[str]) -> None:
    time.sleep(QUIET_S)
    assert dialogs(game) == before and not message_box(game.api)


def test_edits_that_pass(game):
    """Four copies placed with symmetry, and a block that holds nothing removed
    without it: no dialog, the edits happen and are recorded"""
    surface = LARGE
    grid = surface.grid(game)
    before = dialogs(game)
    planes = {"x": (surface.x, False), "z": (surface.z, False)}
    node, new = place(game, surface, grid, surface.near, planes)
    assert len(new) == 4 and node["label"] == "placed 4 blocks"

    symmetry_off(game, grid)
    game.raze_block(sorted(new)[0], grid)
    no_dialog(game, before)


def test_taken_mirror_cell_stops_the_placement(game):
    surface = LARGE
    grid = surface.grid(game)
    cell = probe(game, surface, grid, surface.near)
    game.build_block(mirror(cell, "x", surface.x, False), grid)
    before = dialogs(game)
    cells = set(game.cubes(grid))
    last = game.last_node_id()
    set_planes(game, grid, {"x": (surface.x, False), "z": (surface.z, False)})
    game.mouse("left")
    answer(game, before, broken(3, 4, "placed"), "OK")
    time.sleep(1.0)
    assert set(game.cubes(grid)) == cells
    assert game.last_node_id() == last
    assert warnings(game) == []


def test_missing_copy_stops_paint_and_removal(game):
    """Two copies placed, one removed without symmetry: with symmetry on the
    other one can neither be painted nor removed"""
    surface = LARGE
    grid = surface.grid(game)
    planes = {"x": (surface.x, False)}
    _, new = place(game, surface, grid, surface.near, planes)
    assert len(new) == 2, new
    source = min(new, key=lambda c: sum((a - b) ** 2 for a, b in zip(c, surface.near)))
    (other,) = new - {source}
    symmetry_off(game, grid)
    game.raze_block(other, grid)

    set_planes(game, grid, planes)
    color = game.color(source, grid)
    paint = next(c for c in (RED, GREEN) if tuple(round(v, 3) for v in c) != color)
    game.api.set_build_color(PAINT_SLOT, *paint)
    aim_at_block(game, surface, source, grid)
    before = dialogs(game)
    last = game.last_node_id()
    game.mouse("middle")
    answer(game, before, broken(1, 2, "painted"), "OK")
    assert game.color(source, grid) == color

    before = dialogs(game)
    aim_at_block(game, surface, source, grid)
    game.mouse("right")
    answer(game, before, broken(1, 2, "removed"), "OK")
    time.sleep(1.0)
    assert game.exists(source, grid)
    assert game.last_node_id() == last
    assert warnings(game) == []


def ask_to_remove_bridge(game) -> None:
    game.api.character_grid_event(game.station, rig.BRIDGE, "raze")


def test_split_asks_first(game):
    """Without symmetry: No keeps the bridge, Yes removes it. With the bridge on
    a plane the same question; the red split warning does not follow it."""
    grid = game.station
    symmetry_off(game, grid)
    before = dialogs(game)
    last = game.last_node_id()
    ask_to_remove_bridge(game)
    answer(game, before, SPLIT, "No")
    time.sleep(1.0)
    assert game.exists(rig.BRIDGE) and game.last_node_id() == last

    for planes in (None, {"z": (rig.BRIDGE[2], False)}):
        if planes:
            set_planes(game, grid, planes)
        before = dialogs(game)
        last = game.last_node_id()
        ask_to_remove_bridge(game)
        answer(game, before, SPLIT, "Yes")
        wait_until(lambda: not game.exists(rig.BRIDGE), "the bridge to go")
        game.wait_recorded(last, "removed 1 block, 1 part split off")
        time.sleep(QUIET_S)
        assert warnings(game) == []
        restore_bridge(game)

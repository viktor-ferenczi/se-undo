"""Grid store with oversized backups set to "always raise the budget", and the
buttons of the grid history dialog the main dialog test does not press.
"""

from __future__ import annotations

import time

import rig
from harness import wait_until
from test_grid_ops import click
from test_grid_store import (
    MB,
    dialog,
    grids,
    has_file,
    message_box,
    name_of,
    paste,
    rows,
    shown,
    table_control,
)

UNDO_CONFIG = {"OversizedGridBackups": "AlwaysRaise"}


def open_dialog(game) -> int:
    api = game.api
    rig.focus_gameplay(api)
    api.key("H", ["LeftControl"])
    screen = wait_until(lambda: dialog(api), "the grid history dialog")["index"]
    time.sleep(1)  # the opening transition takes no input
    return screen


def test_dialog_of_a_world_with_no_backups(game):
    api = game.api
    screen = open_dialog(game)
    assert table_control(api, screen)["properties"]["rowsCount"] == 0
    assert shown(game)["rows"] == []

    # Nothing selected: Paste and Delete have nothing to act on
    api.control_click(text="Paste", screen=screen)
    api.control_click(text="Delete", screen=screen)
    time.sleep(0.5)
    assert dialog(api) and not message_box(api)

    # A second press of the key does not stack another dialog
    api.key("H", ["LeftControl"])
    time.sleep(0.5)
    kinds = [s.get("type") for s in api.list_screens()]
    assert kinds.count("GridHistoryScreen") == 1

    api.control_click(text="Close", screen=screen)
    wait_until(lambda: not dialog(api), "the dialog to close")
    assert "gridHistory" not in game.status() or not game.status()["gridHistory"]


def test_oversized_backup_raises_the_budget_without_asking(game):
    assert rig.undo_config("GridStoreBudgetPerWorldMb") == "1"
    last = game.last_node_id()
    big = paste(game, "Big", 1_200_000, expect_node=False)
    node = game.wait_recorded(last, f"pasted {name_of('Big')}")
    assert not message_box(game.api)
    assert not node["barrier"]
    assert rig.undo_config("GridStoreBudgetPerWorldMb") == "64"
    (row,) = rows("Big")
    assert row.get("Id") in node["storeRefs"] and int(row.get("Bytes")) > MB
    assert has_file(row)

    assert game.undo() == f"Undo: pasted {name_of('Big')}"
    wait_until(lambda: not grids(game, "Big"), "the grid to go")
    assert game.redo() == f"Redo: pasted {name_of('Big')}"
    (grid,) = wait_until(lambda: grids(game, "Big"), "the grid to return")
    assert grid["entityId"] == big["entityId"]
    game.undo()
    wait_until(lambda: not grids(game, "Big"), "the grid to go again")


def test_delete_answered_with_no_keeps_the_backup(game):
    api = game.api
    paste(game, "A")
    game.undo()
    wait_until(lambda: not grids(game, "A"), "the pasted grid to go")
    before = len(rows())

    screen = open_dialog(game)
    names = [r[1] for r in shown(game)["rows"]]
    api.control_set("GridHistoryTable", names.index(name_of("A")), screen=screen)
    api.control_click(text="Delete", screen=screen)
    box = wait_until(lambda: message_box(api), "the delete confirmation")
    api.control_click(text="No", screen=box["index"])
    wait_until(lambda: not message_box(api), "the question to close")
    assert len(rows()) == before and has_file(rows("A")[0])
    assert dialog(api)

    # Escape closes the dialog
    time.sleep(1)  # the dialog takes no input while the question fades out
    api.key("Escape")
    wait_until(lambda: not dialog(api), "the dialog to close on Escape")


def test_paste_button_puts_the_backup_on_the_clipboard(game):
    api = game.api
    game.stand_at((15, 0.6, 4), tolerance=1.0)
    api.character_look_at(*rig.station_point((24, 1, -8)))
    assert not api.get_character_target(max_distance=200)["hit"]

    screen = open_dialog(game)
    names = [r[1] for r in shown(game)["rows"]]
    api.control_set("GridHistoryTable", names.index(name_of("A")), screen=screen)
    api.control_click(text="Paste", screen=screen)
    wait_until(lambda: not dialog(api), "the dialog to close")

    last = game.last_node_id()
    time.sleep(1)  # the preview follows the camera from the next frames
    click(api)
    (grid,) = wait_until(lambda: grids(game, "A"), "the grid pasted from the history")
    game.wait_recorded(last, f"pasted {name_of('A')}")
    assert grid["entityId"] != 0

    assert game.undo() == f"Undo: pasted {name_of('A')}"
    wait_until(lambda: not grids(game, "A"), "the pasted grid to go")

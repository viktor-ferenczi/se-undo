"""The option switches of the config dialog, each set away from what the rest of the
suite runs with: other key bindings, a linear history with a small limit, removed
blocks rebuilt from their definition, no recording outside the terminal, terminal
text boxes without their own undo, no HUD text, no history in the save, oversized
backups never stored, and another storage folder.

The plugin reads its config once at start, so this file has a client of its own.
"""

from __future__ import annotations

import time

import pytest

import rig
from harness import wait_until
from test_grid_store import SPOTS, grids, message_box, name_of, paste

STORAGE = rig.APPDATA / "UndoElsewhere"


def binding(key: str) -> str:
    return f"<Key>{key}</Key><Ctrl>true</Ctrl><Alt>true</Alt><Shift>false</Shift>"


UNDO_CONFIG = {
    "UndoTree": None,
    "UndoBinding": binding("Z"),
    "RedoBinding": binding("Y"),
    "GridHistoryBinding": binding("H"),
    "MaxNodesBuild": "10",
    "MaxNodesTerminal": "10",
    "RestoreRemovedBlocksWithFullState": "false",
    "RecordTerminalChangesOutsideTerminal": "false",
    "SeparateTextUndoInTerminal": "false",
    "Notifications": "false",
    "PersistInTheWorldSave": "false",
    "OversizedGridBackups": "NeverStore",
    "ClientStorageRoot": str(STORAGE),
}

CTRL_ALT = ["LeftControl", "LeftAlt"]
STAND = (2, 1.0, 14)
AIM = (2, 1.3, 15)
SLIDER = "MultiplierAz"
CONTROLLER = rig.TURRET_CONTROLLER
ROW = [(x, 1, 2) for x in range(1, 13)]


@pytest.fixture(scope="module", autouse=True)
def rebound_keys(game):
    game.undo_key = ("Z", CTRL_ALT)
    game.redo_key = ("Y", CTRL_ALT)
    game.stand_at(STAND)


def test_storage_folder_is_the_configured_one(game):
    """The status file the whole suite reads comes from there, and so does the
    grid store"""
    assert game.status_file == STORAGE / "status.json"
    assert game.status()["mode"] == "Offline"
    game.paste("Undo Stored Elsewhere", [(0, 0, 0)], (24, 1, 0))
    (index,) = STORAGE.glob("Worlds/*/grids/index.xml")
    assert "Undo Stored Elsewhere" in index.read_text(encoding="utf-8")
    assert not (rig.APPDATA / "Undo").exists()
    game.undo()
    wait_until(lambda: not game.grids_named("Undo Stored Elsewhere"), "the grid to go")


def test_rebound_keys(game):
    """Undo and redo answer on Ctrl-Alt-Z and Ctrl-Alt-Y. Ctrl-Z is not the
    plugin's any more and does what it does in the vanilla game."""
    api, cell = game.api, ROW[0]
    game.build_block(cell)

    def dampeners():
        return api.get_character()["dampeners"]

    if dampeners():
        api.key("Z")
        wait_until(lambda: not dampeners(), "dampeners off")
    game.quiet(lambda: api.key("Z", ["LeftControl"]))
    assert game.exists(cell)
    wait_until(dampeners, "vanilla relative dampeners on Ctrl-Z")
    game.quiet(lambda: api.key("Y", ["LeftControl"]))

    assert game.undo() == "Undo: placed 1 block"
    wait_until(lambda: not game.exists(cell), "the block to go")
    assert game.redo() == "Redo: placed 1 block"
    wait_until(lambda: game.exists(cell), "the block to return")
    game.undo()
    wait_until(lambda: not game.exists(cell), "the block to go again")


def test_grid_history_key_is_rebound(game):
    api = game.api
    api.key("H", ["LeftControl"])
    time.sleep(1)
    assert game.screen("GridHistoryScreen") is None
    # Vanilla toggles the render profiler on Ctrl-H; once more puts it back
    api.key("H", ["LeftControl"])

    api.key("H", CTRL_ALT)
    wait_until(lambda: game.screen("GridHistoryScreen"), "the grid history dialog")
    rig.focus_gameplay(api)


def test_no_hud_text_with_notifications_off(game):
    api, cell = game.api, ROW[0]
    latest = api.get_hud_notifications()["latest"]
    game.build_block(cell)
    assert game.undo() == "Undo: placed 1 block"
    time.sleep(1)
    new = api.get_hud_notifications(after=latest)["notifications"]
    assert not [n["text"] for n in new if "ndo" in n["text"]]


def test_small_limit_on_a_linear_history(game):
    first = game.last_node_id() + 1
    for cell in ROW:
        game.build_block(cell)
    build = game.build()
    assert build["count"] == 10
    assert [n["id"] for n in build["nodes"]] == list(range(first + 2, first + 12))

    for _ in range(10):
        game.undo()
    wait_until(lambda: not any(game.exists(c) for c in ROW[2:]), "ten blocks to go")
    game.nothing_to_undo()
    # The two oldest steps fell out of the history; their blocks stay
    assert game.exists(ROW[0]) and game.exists(ROW[1])


def test_removed_block_is_rebuilt_from_its_definition(game):
    """With the full state option off even a creative world gets a new block of
    the same kind: complete and under the old id, but without its name"""
    cell = rig.SECOND_LIGHT
    before = game.block(cell)
    game.raze_block(cell)

    assert game.undo() == "Undo: removed 1 block"
    wait_until(lambda: game.exists(cell), "the rebuilt light")
    after = game.block(cell)
    assert after["definition"] == before["definition"]
    assert game.build_ratio(cell) == 1
    assert after["customName"] != before["customName"]
    assert after["entityId"] == before["entityId"]

    game.redo()
    wait_until(lambda: not game.exists(cell), "the removal again")
    game.undo()
    wait_until(lambda: game.exists(cell), "the light again")


def value(game) -> float:
    return game.api.get_property(game.station, CONTROLLER, SLIDER)["value"]


def test_only_terminal_changes_are_recorded(game):
    api = game.api
    label = f"changed {SLIDER} of Undo Turret Controller"
    before = value(game)
    assert game.terminal()["count"] == 0

    # A script, a toolbar slot or a mod setting the property is not the player
    api.set_property(game.station, CONTROLLER, SLIDER, 3.0)
    time.sleep(1.2)
    assert value(game) != before
    assert game.terminal()["count"] == 0
    outside = value(game)

    with game.open_terminal(AIM, leave_search_box=False) as screen:
        api.control_set(SLIDER, 0.8, screen=screen)
        game.wait_recorded(0, label, history="terminal")
        changed = value(game)
        assert changed != outside

        # The cursor is still in the block search box. With the separate text undo
        # off the keys are the terminal history's there too.
        assert game.focus_is_text()
        assert game.undo() == f"Undo: {label}"
        assert value(game) == outside
        assert game.redo() == f"Redo: {label}"
        assert value(game) == changed


def test_small_limit_on_the_terminal_history(game):
    api = game.api
    with game.open_terminal(AIM) as screen:
        for step in range(12):
            last = game.last_node_id("terminal")
            api.control_set(SLIDER, 0.1 + 0.07 * step, screen=screen)
            game.wait_recorded(
                last, f"changed {SLIDER} of Undo Turret Controller", history="terminal"
            )
        assert game.terminal()["count"] == 10


def test_history_is_not_saved_with_the_world(game):
    assert game.build()["count"] > 0
    game.saved_station()
    time.sleep(2)
    assert not (rig.WORLD / "Undo.xml.gz").exists()
    assert not list((rig.WORLD / "Backup").glob("*/Undo.xml.gz"))


def test_oversized_backup_is_never_stored(game):
    """Over the 1 MB budget of the rig: no question, the backup is dropped and the
    paste stays as a step that cannot be undone"""
    last = game.last_node_id()
    paste(game, "Big", 1_200_000, expect_node=False)
    node = game.wait_recorded(last, f"pasted {name_of('Big')}")
    assert not message_box(game.api)
    assert node["barrier"] and not node["storeRefs"]
    assert game.undo(expect=None) == (
        "Undo not available: backup was too large for the budget"
    )
    assert grids(game, "Big")
    assert rig.undo_config("GridStoreBudgetPerWorldMb") == "1"
    assert SPOTS["Big"]

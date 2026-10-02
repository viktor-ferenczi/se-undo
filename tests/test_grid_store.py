"""Grid store rows of design section 13: retention, an oversized backup, the grid
history dialog. Runs after the grid tests, in the same world, with a per world
budget of 1 MB (rig.UNDO_CONFIG).

The test grids are two block stations whose backup is a known size: a light named
with random text, which gzip cannot shrink (rig.heavy_blueprint_xml).
"""

from __future__ import annotations

import gzip
import time
import xml.etree.ElementTree as ET

import pytest

import rig
from harness import wait_until
from test_grid_ops import click, on_the_station  # noqa: F401 -- autouse fixture

MB = 1024 * 1024
# Three of these fit into 1 MB, four do not
ENTRY_BYTES = 300_000
SPOTS = {
    "A": (24, 1, 12),
    "B": (24, 1, 8),
    "C": (24, 1, 4),
    "D": (24, 1, 0),
    "Big": (24, 1, -4),
    "Esc": (24, 6, 12),
}
SWITCH = (1, 1, 0)


def name_of(key: str) -> str:
    return f"Undo Store {key}"


def store_folder():
    (folder,) = (rig.APPDATA / "Undo" / "Worlds").glob("*/grids")
    return folder


def rows(key: str | None = None) -> list[ET.Element]:
    found = list(ET.parse(store_folder() / "index.xml").getroot().iter("StoreRow"))
    return [r for r in found if key is None or r.get("MainGridName") == name_of(key)]


def has_file(row: ET.Element) -> bool:
    return (store_folder() / f"{row.get('Id')}.sbc.gz").exists()


def grids(game, key: str) -> list[dict]:
    return [g for g in game.grids() if g.get("name") == name_of(key)]


def message_box(api):
    return next(
        (s for s in api.list_screens() if s.get("type") == "MyGuiScreenMessageBox"),
        None,
    )


def paste(game, key: str, payload: int = ENTRY_BYTES, expect_node: bool = True) -> dict:
    _, forward, up = rig.station_frame()
    last = game.last_node_id()
    (pasted,) = game.api.paste_blueprint(
        xml=rig.heavy_blueprint_xml(name_of(key), payload, seed=ord(key[0])),
        position=rig.station_point(SPOTS[key]),
        forward=forward,
        up=up,
    )
    if expect_node:
        game.wait_recorded(last, f"pasted {name_of(key)}")
    return pasted


def delete(game, grid: dict, key: str, expect_node: bool = True) -> None:
    last = game.last_node_id()
    assert game.api.close_grid(grid["entityId"])["closed"]
    wait_until(lambda: not grids(game, key), "the closed grid to go")
    if expect_node:
        game.wait_recorded(last, f"deleted {name_of(key)}")


def test_retention_spares_the_newest_backup_of_each_grid(game):
    if rig.undo_config("GridStoreBudgetPerWorldMb") != "1":
        pytest.skip("an earlier run on this client raised the budget")

    # A is backed up twice: when pasted and, changed, when deleted
    a = paste(game, "A")
    game.api.set_enabled(a["entityId"], SWITCH, False)
    time.sleep(0.5)
    delete(game, a, "A")
    pasted_a, deleted_a = rows("A")
    assert pasted_a.get("Id") != deleted_a.get("Id")
    assert int(pasted_a.get("Bytes")) > ENTRY_BYTES

    paste(game, "B")
    assert len(rows("A")) == 2

    # The fourth entry goes over the budget. The older copy of A gives way; B is
    # older than A's newest copy, but it is the only backup of its grid.
    paste(game, "C")
    assert [r.get("Reason") for r in rows("A")] == ["Deleted"]
    assert not has_file(pasted_a) and has_file(deleted_a)
    assert len(rows("B")) == 1 and len(rows("C")) == 1

    # Only newest copies are left now, so the oldest of them goes: A's
    paste(game, "D")
    assert not rows("A") and not has_file(deleted_a)
    assert [len(rows(k)) for k in "BCD"] == [1, 1, 1]
    used = sum(int(r.get("Bytes")) for r in rows())
    assert used <= MB

    # The history still has the delete of A, but its backup is gone
    for key in "DCB":
        assert game.undo() == f"Undo: pasted {name_of(key)}"
        wait_until(lambda: not grids(game, key), "the pasted grid to go")
    assert game.undo(expect=None) == "Undo not available: backup was cleaned up"
    build = game.build()
    current = next(n for n in build["nodes"] if n["id"] == build["current"])
    assert current["label"] == f"deleted {name_of('A')}"


def answer(game, text: str) -> None:
    api = game.api
    box = wait_until(lambda: message_box(api), "the budget question")
    # No undo or redo while the question is open, the box has the input anyway
    api.control_click(text=text, screen=box["index"])
    wait_until(lambda: not message_box(api), "the question to close")


def test_closing_the_budget_question_counts_as_no(game):
    """Escape instead of a button: the backup is dropped like on No"""
    if rig.undo_config("GridStoreBudgetPerWorldMb") != "1":
        pytest.skip("an earlier run on this client raised the budget")
    api = game.api
    last = game.last_node_id()
    paste(game, "Esc", 1_200_000, expect_node=False)
    wait_until(lambda: message_box(api), "the budget question")

    # Undo and redo wait for the answer
    time.sleep(1)
    assert game.last_node_id() == last

    api.key("Escape")
    wait_until(lambda: not message_box(api), "the question to close")
    node = game.wait_recorded(last, f"pasted {name_of('Esc')}")
    assert node["barrier"] and not node["storeRefs"]
    assert not rows("Esc")
    assert not list(store_folder().glob("*.tmp"))
    assert rig.undo_config("GridStoreBudgetPerWorldMb") == "1"
    assert game.undo(expect=None) == (
        "Undo not available: backup was too large for the budget"
    )


def test_oversized_backup_asks_once_per_backup(game):
    if rig.undo_config("GridStoreBudgetPerWorldMb") != "1":
        pytest.skip("an earlier run on this client raised the budget")
    payload = 1_200_000

    # No: the paste stays, but neither it nor the delete can be undone
    last = game.last_node_id()
    big = paste(game, "Big", payload, expect_node=False)
    answer(game, "No")
    node = game.wait_recorded(last, f"pasted {name_of('Big')}")
    assert node["barrier"] and not node["storeRefs"]
    assert game.undo(expect=None) == (
        "Undo not available: backup was too large for the budget"
    )
    assert grids(game, "Big")

    last = game.last_node_id()
    delete(game, big, "Big", expect_node=False)
    answer(game, "No")
    assert game.wait_recorded(last, f"deleted {name_of('Big')}")["barrier"]
    assert game.undo(expect=None) == (
        "Undo not available: backup was too large for the budget"
    )
    assert not grids(game, "Big")
    assert not rows("Big")
    assert not list(store_folder().glob("*.tmp"))
    assert rig.undo_config("GridStoreBudgetPerWorldMb") == "1"

    # Yes: the budget goes up to the next step and the backup is kept
    last = game.last_node_id()
    big = paste(game, "Big", payload, expect_node=False)
    answer(game, "Yes")
    node = game.wait_recorded(last, f"pasted {name_of('Big')}")
    assert not node["barrier"]
    assert rig.undo_config("GridStoreBudgetPerWorldMb") == "64"
    (row,) = rows("Big")
    assert row.get("Id") in node["storeRefs"] and int(row.get("Bytes")) > MB
    assert has_file(row)

    # It fits now, so the delete does not ask, and undo brings the grid back
    game.api.set_enabled(big["entityId"], SWITCH, False)
    time.sleep(0.5)
    delete(game, big, "Big")
    assert not message_box(game.api)
    assert game.undo() == f"Undo: deleted {name_of('Big')}"
    (grid,) = wait_until(lambda: grids(game, "Big"), "the grid to come back")
    assert grid["entityId"] == big["entityId"]

    game.redo(expect=f"Redo: deleted {name_of('Big')}")
    wait_until(lambda: not grids(game, "Big"), "the grid to go again")


# ---------------------------------------------------------------------------
# Grid history dialog
# ---------------------------------------------------------------------------

COLUMNS = ["Time", "Name", "Blocks", "Grids", "PCU", "Size", "Reason"]
# Column widths as fractions of the table without its scrollbar, which takes the
# last 5% of the control
WIDTHS = [0.25, 0.27, 0.09, 0.08, 0.08, 0.1, 0.13]
COLUMNS_SHARE = 0.95
# The control is this many row heights tall: header, visible rows, padding
VISIBLE_ROWS = 14
LINES = VISIBLE_ROWS + 1.2
WINDOW = (1280, 720)


def dialog(api) -> dict | None:
    return next(
        (s for s in api.list_screens() if s.get("type") == "GridHistoryScreen"), None
    )


def table_control(api, screen: int) -> dict:
    def walk(node):
        if isinstance(node, dict):
            if node.get("name") == "GridHistoryTable":
                return node
            node = list(node.values())
        if isinstance(node, list):
            for child in node:
                found = walk(child)
                if found:
                    return found
        return None

    return walk(api.get_controls(screen))


def table_point(api, screen: int, column: int, line: int) -> tuple[int, int]:
    """Window pixel inside a table cell; line 0 is the header. Remote reports the
    control in GUI coordinates, which span a centered 4:3 area of the window.
    Its control/hover endpoint would do this, but lands elsewhere (SE1-0072)."""
    table = table_control(api, screen)
    left, top = table["topLeft"]["x"], table["topLeft"]["y"]
    width, height = table["size"]["x"], table["size"]["y"]
    x = left + width * COLUMNS_SHARE * (sum(WIDTHS[:column]) + WIDTHS[column] / 2)
    y = top + height * (line + 0.5) / LINES
    gui_width = WINDOW[1] * 4 / 3
    return round((WINDOW[0] - gui_width) / 2 + x * gui_width), round(y * WINDOW[1])


def table_click(api, screen: int, column: int, line: int, double: bool = False):
    # The table learns which header or row the cursor is over while handling the
    # frame before the click, so the cursor has to be there first
    x, y = table_point(api, screen, column, line)
    api.mouse_move(x, y)
    time.sleep(0.2)
    api.click(x, y, double=double)


def shown(game) -> dict:
    return game.status()["gridHistory"]


def click_header(game, screen: int, column: str, expect_keys: str) -> list[list[str]]:
    table_click(game.api, screen, COLUMNS.index(column), 0)
    wait_until(lambda: shown(game)["sortKeys"] == expect_keys, f"sort by {expect_keys}")
    return shown(game)["rows"]


def test_grid_history_dialog(game):
    api = game.api
    rig.focus_gameplay(api)
    api.character_look_at(*rig.station_point((24, 1, -8)))
    assert not api.get_character_target(max_distance=200)["hit"]
    assert not grids(game, "D")

    api.key("H", ["LeftControl"])
    screen = wait_until(lambda: dialog(api), "the grid history dialog")["index"]

    # One table row per index row of this world, newest first
    table = table_control(api, screen)
    index = rows()
    assert table["properties"]["rowsCount"] == len(index)
    listed = shown(game)
    assert listed["sortKeys"] == "-Time"
    times = [r[0] for r in listed["rows"]]
    assert times == sorted(times, reverse=True)
    assert sorted(r[1] for r in listed["rows"]) == sorted(
        r.get("MainGridName") for r in index
    )

    # Time is the first key already, so the click flips it. Name then goes in
    # front of it: by name, oldest first inside each name.
    by_time = click_header(game, screen, "Time", "Time")
    assert [r[0] for r in by_time] == sorted(times)
    by_name = click_header(game, screen, "Name", "Name,Time")
    keys = [(r[1].lower(), r[0]) for r in by_name]
    assert keys == sorted(keys)
    assert len({r[1] for r in by_name}) > 1

    # Delete asks first, then the row and its file are gone
    doomed = next(i for i, r in enumerate(by_name) if r[1] == name_of("C"))
    (row_c,) = rows("C")
    api.control_set("GridHistoryTable", doomed, screen=screen)
    api.control_click(text="Delete", screen=screen)
    box = wait_until(lambda: message_box(api), "the delete confirmation")
    api.control_click(text="Yes", screen=box["index"])
    wait_until(lambda: not rows("C"), "the backup of C to go")
    assert not has_file(row_c)
    by_name = shown(game)["rows"]
    assert name_of("C") not in [r[1] for r in by_name]

    # A double click puts the backup on the clipboard and closes the dialog; the
    # player then places it like any paste
    line = next(i for i, r in enumerate(by_name) if r[1] == name_of("D"))
    assert line < VISIBLE_ROWS
    screen = dialog(api)["index"]
    table_click(api, screen, 1, line + 1, double=True)
    wait_until(lambda: not dialog(api), "the dialog to close")
    assert rig.undo_config("GridHistorySortKeys") == "Name,Time"

    last = game.last_node_id()
    time.sleep(1)  # the preview follows the camera from the next frames
    click(api)
    (grid,) = wait_until(lambda: grids(game, "D"), "the grid pasted from the history")
    game.wait_recorded(last, f"pasted {name_of('D')}")
    assert grid["entityId"] != 0

    assert game.undo() == f"Undo: pasted {name_of('D')}"
    wait_until(lambda: not grids(game, "D"), "the pasted grid to go")


def test_store_entry_is_a_blueprint_file(game):
    """An entry can be copied into the blueprints folder by hand: it is the game's
    blueprint format, gzip compressed"""
    row = rows()[-1]
    with gzip.open(store_folder() / f"{row.get('Id')}.sbc.gz", "rt") as file:
        root = ET.parse(file).getroot()
    assert root.tag == "Definitions"
    grid = root.find("ShipBlueprints/ShipBlueprint/CubeGrids/CubeGrid")
    assert grid.findtext("DisplayName") == row.get("MainGridName")

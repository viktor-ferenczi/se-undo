"""Snapshots taken before edits (SE-1360), design section 9: the first edit of a grid
backs up its group, edits within the interval do not, the first after a longer pause
does again, each grid keeps a bounded number of them, and the grid history dialog's
Restore puts one back in place as an undoable step.

The interval is 3 seconds here instead of 15 minutes, so the pauses are short.
"""

from __future__ import annotations

import gzip
import re
import time
import xml.etree.ElementTree as ET
from pathlib import Path

import rig
from harness import wait_until
from se_remote import OpError
from test_grid_ops import on_the_station  # noqa: F401 -- autouse fixture
from test_grid_store import dialog, shown
from test_store_policy import open_dialog

INTERVAL_S = 3
KEEP = 3
UNDO_CONFIG = {
    "GridSnapshotIntervalS": str(INTERVAL_S),
    "GridSnapshotsPerGrid": str(KEEP),
    # Room for the station's backups next to each other
    "GridStoreBudgetPerWorldMb": "64",
}

BIG_NAME = "Undo Snapshot Big"
BIG_CELLS = [(x, y, z) for x in range(16) for y in range(16) for z in range(16)]


# Where the player's snapshots are stored and logged: the local grid store
# offline. The dedicated server and lobby files point these at the server's.
SNAPSHOTS = {
    "folders": lambda: (rig.APPDATA / "Undo" / "Worlds").glob("*/grids"),
    "log": lambda: rig.APPDATA / "SpaceEngineers.log",
}


def store_folder() -> Path | None:
    return next(iter(SNAPSHOTS["folders"]()), None)


def rows() -> list[ET.Element]:
    folder = store_folder()
    if folder is None or not (folder / "index.xml").exists():
        return []
    return list(ET.parse(folder / "index.xml").getroot().iter("StoreRow"))


def edited(grid_id: int = rig.STATION_ID) -> list[ET.Element]:
    """The grid's snapshot rows, oldest first"""
    found = [
        r
        for r in rows()
        if r.get("Reason") == "Edited" and int(r.get("MainGridEntityId")) == grid_id
    ]
    return sorted(found, key=lambda r: r.get("TimestampUtc"))


def wait_count(count: int, grid_id: int = rig.STATION_ID) -> list[ET.Element]:
    return wait_until(
        lambda: (found := edited(grid_id)) and len(found) == count and found,
        f"{count} snapshots",
    )


def no_new_snapshot(count: int) -> None:
    # A snapshot is written in the background; give one that should not come time to
    time.sleep(1.5)
    assert len(edited()) == count


def pause() -> None:
    time.sleep(INTERVAL_S + 0.5)


def entry_blocks(
    row: ET.Element, grid_id: int = rig.STATION_ID
) -> dict[tuple, ET.Element]:
    """The blocks of one grid in a snapshot's file, by min cell"""
    data = gzip.decompress((store_folder() / f"{row.get('Id')}.sbc.gz").read_bytes())
    root = ET.fromstring(data)
    grid = next(
        g for g in root.iter("CubeGrid") if int(g.findtext("EntityId")) == grid_id
    )
    blocks = {}
    for block in grid.find("CubeBlocks"):
        m = block.find("Min")
        cell = (0, 0, 0) if m is None else tuple(int(m.get(a, "0")) for a in "xyz")
        blocks[cell] = block
    return blocks


def color(block: ET.Element) -> tuple:
    hsv = block.find("ColorMaskHSV")
    return tuple(round(float(hsv.get(a)), 3) for a in "xyz")


def snapshot_lines() -> list[str]:
    log = SNAPSHOTS["log"]().read_text("utf-8", "replace")
    return [line for line in log.splitlines() if "Snapshot of" in line]


def test_first_edit_backs_up_the_station(game):
    assert edited() == []
    game.build_block(rig.BUILD_CELL)
    (row,) = wait_count(1)

    # Taken before the edit: the new block is not in it, the rest of the station is
    blocks = entry_blocks(row)
    assert rig.BUILD_CELL not in blocks
    assert set(blocks) == set(game.cubes()) - {rig.BUILD_CELL}
    assert row.get("MainGridName") == rig.STATION_NAME

    line = snapshot_lines()[-1]
    assert "ms on the game thread" in line and "ms in the background" in line, line


def test_edits_within_the_interval_take_none(game):
    game.raze_block(rig.BUILD_CELL)
    no_new_snapshot(1)
    game.build_block(rig.BUILD_CELL)
    no_new_snapshot(1)


def test_an_edit_after_a_pause_takes_one(game):
    light = rig.PAINT_LIGHT
    original = game.color(light)
    pause()
    last = game.last_node_id()
    game.api.character_grid_event(game.station, light, "color")
    wait_until(lambda: game.color(light) != original, "the paint")
    game.wait_recorded(last, "painted 1 block")
    first, second = wait_count(2)

    # The new one has the light before it was painted, and the block built since
    blocks = entry_blocks(second)
    assert rig.BUILD_CELL in blocks
    assert color(blocks[light]) == color(entry_blocks(first)[light])
    assert color(blocks[light]) != game.color(light)

    # Undo and redo are no edits of the player
    game.undo()
    game.redo()
    no_new_snapshot(2)


def test_each_grid_keeps_a_bounded_number(game):
    oldest = edited()[0].get("TimestampUtc")
    pause()
    game.raze_block(rig.BUILD_CELL)
    wait_count(3)
    pause()
    game.build_block(rig.BUILD_CELL)
    kept = wait_until(
        lambda: (r := edited())
        and len(r) == KEEP
        and r[0].get("TimestampUtc") != oldest
        and r,
        "the oldest snapshot to go",
    )
    assert len(kept) == KEEP
    assert "older snapshot removed" in snapshot_lines()[-1]
    # The removed row's file went with it, unless another row shares it
    ids = {r.get("Id") for r in rows()}
    files = {p.name.removesuffix(".sbc.gz") for p in store_folder().glob("*.sbc.gz")}
    assert files == ids


def cells(game) -> set | None:
    """The station's block cells, None while a restore re-creates it: a client
    briefly does not know the grid"""
    try:
        return set(game.cubes())
    except OpError:
        return None


def restore_newest(game) -> str:
    """Restores the newest snapshot of the station through the dialog's button,
    returns the notification"""
    api = game.api
    screen = open_dialog(game)
    shown_rows = shown(api)
    index = next(
        i
        for i, cells in enumerate(shown_rows)
        if cells[1] == rig.STATION_NAME and cells[6] == "Edited"
    )
    api.control_set("GridHistoryTable", index, screen=screen)
    api.control_click(text="Restore", screen=screen)
    wait_until(lambda: not dialog(api), "the dialog to close")
    return wait_until(
        lambda: (m := game.last_message()) and m.startswith("Grid history:") and m,
        "the restore",
        timeout=30,
    )


def test_restore_puts_the_station_back_as_it_was(game):
    pause()
    # The snapshot before this edit is what the restore brings back
    # An unchanged group is one file, so a new snapshot may share an earlier id
    newest = edited()[-1].get("TimestampUtc")
    game.raze_block(rig.BUILD_CELL)
    # An element without children is falsy, so the wait gets a list
    (snapshot,) = wait_until(
        lambda: (r := edited()) and r[-1].get("TimestampUtc") > newest and r[-1:],
        "the snapshot",
    )
    stored = entry_blocks(snapshot)
    assert rig.BUILD_CELL in stored

    # Damage after the snapshot: another block gone
    light = rig.SECOND_LIGHT
    light_id = game.block(light)["entityId"]
    game.raze_block(light)
    damaged = set(game.cubes())
    assert light not in damaged

    message = restore_newest(game)
    assert re.match(
        rf"Grid history: restored {rig.STATION_NAME} from \d{{4}}-\d\d-\d\d \d\d:\d\d:\d\d$",
        message,
    ), message
    wait_until(lambda: cells(game) == set(stored), "the station as in the snapshot")
    # Back as itself: the same grid and block ids
    assert game.block(light)["entityId"] == light_id
    assert game.exists(rig.BUILD_CELL)

    # One step: undo goes back to the damaged station, redo restores again
    assert game.undo().startswith(f"Undo: restored {rig.STATION_NAME} from ")
    wait_until(lambda: cells(game) == damaged, "the station as before the restore")
    assert game.redo().startswith(f"Redo: restored {rig.STATION_NAME} from ")
    wait_until(lambda: cells(game) == set(stored), "the restore again")
    # The restore is no edit of the player and takes no snapshot of its own
    assert edited()[-1].get("TimestampUtc") == snapshot.get("TimestampUtc")


def test_a_big_grid_is_backed_up_off_the_game_thread(game):
    pasted = game.paste(BIG_NAME, BIG_CELLS, (40, 1, -8))
    grid_id = pasted["entityId"]
    game.raze_block((0, 15, 0), grid=grid_id)
    (row,) = wait_count(1, grid_id)
    assert int(row.get("BlockCount")) == len(BIG_CELLS)
    line = next(l for l in reversed(snapshot_lines()) if BIG_NAME in l)
    game_ms, background_ms = (
        int(n)
        for n in re.search(
            r"(\d+) ms on the game thread, (\d+) ms in the background", line
        ).groups()
    )
    print(
        f"\n{len(BIG_CELLS)} blocks: {game_ms} ms on the game thread, {background_ms} ms in the background"
    )
    # Taking the builders is all the game thread does; well within a frame budget
    assert game_ms < 50, line

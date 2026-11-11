"""The Paint Replacer plugin through Undo's API (SE-1357): each replacement, on the
aimed grid, on all its subgrids and as a flood fill, is one step, and undo and redo
give every block its exact paint back, the aimed block included. The replacement
also takes the snapshot of SE-1360 before it changes the grids.

Paint Replacer comes from the working copy next to this repo
(rig.PAINT_REPLACER, UNDO_PAINT_REPLACER_REPO). Its modifier key is Alt; the
middle mouse button replaces the aimed paint with the player's selected color.
The test wall stands beside the station with a rotor on top, whose top part is a
subgrid.
"""

from __future__ import annotations

import time

import pytest

import rig
from harness import wait_until
from se_remote import OpError

PLUGINS = (rig.PAINT_REPLACER,)
INTERVAL_S = 2
UNDO_CONFIG = {
    "GridSnapshotIntervalS": str(INTERVAL_S),
    "GridStoreBudgetPerWorldMb": "64",
}

WALL_NAME = "Undo Paint Wall"
WALL_AT = (18, 1, 4)
# Wall cells, x is the wall's own 0; the character stands three cells before it
WALL = [(0, y, z) for y in (-1, 0, 1) for z in (-1, 0, 1)]
VANTAGE = (15, 0.6, 4)
STATOR = (0, 2, 0)
AIMED = (0, 0, 0)
# Keeps a paint of its own, so a replacement has to leave it alone
ODD = (0, -1, -1)

PAINT = (0.1, 0.5, 0.2)
ODD_PAINT = (0.8, 0.3, -0.1)
MODIFIER = ["LeftAlt", "Alt"]


def station_cell(cell) -> tuple:
    return tuple(a + b for a, b in zip(WALL_AT, cell))


def rounded(hsv) -> tuple:
    return tuple(round(float(c), 3) for c in hsv)


def paints(game, grid) -> dict[tuple, tuple]:
    """Color and skin of every block of a grid, None while a client does not
    know the grid"""
    try:
        cubes = game.cubes(grid)
    except OpError:
        return None
    return {
        cell: (rounded(c["colorMaskHsv"]), c.get("skin")) for cell, c in cubes.items()
    }


class Setup:
    def __init__(self, game):
        self.game = game
        self.wall = game.paste(WALL_NAME, WALL, WALL_AT)["entityId"]
        known = {g["entityId"] for g in game.grids()}
        game.build_block(STATOR, grid=self.wall, subtype="LargeStator")
        (top,) = wait_until(
            lambda: [g for g in game.grids() if g["entityId"] not in known],
            "the rotor's top part",
        )
        self.top = top["entityId"]
        (head,) = game.cubes(self.top)
        for step in ((1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1), (0, 1, 0)):
            cell = tuple(h + d for h, d in zip(head, step))
            game.api.character_build_block(self.top, cell)
            try:
                wait_until(lambda: game.exists(cell, self.top), "the block", timeout=3)
                break
            except AssertionError:
                continue
        # Everything in one paint but one block of the wall
        for grid in (self.wall, self.top):
            cells = list(game.cubes(grid))
            lo = tuple(min(c[i] for c in cells) for i in range(3))
            hi = tuple(max(c[i] for c in cells) for i in range(3))
            game.api.character_grid_event(grid, lo, "color", max_pos=hi, hsv=PAINT)
        game.api.character_grid_event(self.wall, ODD, "color", hsv=ODD_PAINT)
        wait_until(lambda: self.painted(), "the test paint")

    def painted(self) -> bool:
        wall, top = paints(self.game, self.wall), paints(self.game, self.top)
        return (
            wall is not None
            and top is not None
            and all(p[0] == (ODD_PAINT if c == ODD else PAINT) for c, p in wall.items())
            and all(p[0] == PAINT for p in top.values())
        )

    def state(self) -> tuple:
        return paints(self.game, self.wall), paints(self.game, self.top)


@pytest.fixture(scope="module")
def setup(game):
    # Paint Replacer takes a creative world without creative tools for survival,
    # where it is off by default (SE-1363)
    assert game.api.set_admin_flag("creativeTools", True)["creativeToolsEnabled"]
    # A joined client under load missed a teleport once
    for attempt in range(3):
        try:
            game.stand_at(VANTAGE, tolerance=1.0)
            break
        except AssertionError:
            if attempt == 2:
                raise
    return Setup(game)


def aim(game, grid: int, cell) -> None:
    """Looks at a block of the wall until the crosshair is on it"""
    point = rig.station_point(station_cell(cell))
    deadline = time.monotonic() + 15
    target = None
    while time.monotonic() < deadline:
        game.api.character_look_at(*point, tolerance=0.5)
        time.sleep(0.2)
        target = game.api.get_character_target(max_distance=100).get("block")
        if target and target["gridId"] == grid and tuple(target["min"]) == cell:
            return
    raise AssertionError(f"The view did not settle on {cell}: {target}")


def replace(game, setup, keys=()) -> None:
    """Modifier and middle mouse button on the aimed wall block, in first person"""
    api = game.api
    if api.get_view()["thirdPerson"]:
        api.key("V")
        time.sleep(1)
    game.stand_at(VANTAGE, tolerance=1.0)
    aim(game, setup.wall, AIMED)
    held = MODIFIER + list(keys)
    api.set_input_state(keys=held, mode="override")
    time.sleep(1)
    api.set_input_state(keys=held, mouse_middle=True, mode="override")
    time.sleep(0.5)
    api.set_input_state(keys=held, mode="override")
    time.sleep(0.5)
    api.clear_input_state()


def one_step(game, setup, keys, label: str, top_changes: bool) -> None:
    before = setup.state()
    # A pause longer than the snapshot interval: the replacement backs the group up
    time.sleep(INTERVAL_S + 0.5)
    snapshots = edited_count(game, setup)
    last = game.last_node_id()

    replace(game, setup, keys)
    node = wait_until(
        lambda: (n := game.build()["nodes"])
        and n[-1]["id"] > last
        and n[-1]["label"].startswith(label + ", painted ")
        and n[-1],
        f"the step {label!r}",
    )
    after = wait_until(
        lambda: (s := setup.state()) and None not in s and s != before and s,
        "the replacement",
    )
    # One step, and nothing recorded after it
    time.sleep(1)
    assert game.labels(last) == [node["label"]], game.labels(last)

    wall, top = after
    selected = wall[AIMED][0]
    assert selected != PAINT
    assert wall[ODD] == before[0][ODD]
    assert all(p[0] == selected for c, p in wall.items() if c != ODD)
    assert (top == before[1]) != top_changes
    if top_changes:
        assert all(p[0] == selected for p in top.values())
    changed = sum(
        a != b
        for old, new in zip(before, after)
        for a, b in zip(old.values(), new.values())
    )
    assert node["label"].endswith(f", painted {changed} blocks"), node["label"]
    assert edited_count(game, setup) == snapshots + 1

    # Undo puts back the exact paint of every block, the aimed one too
    assert game.undo() == f"Undo: {node['label']}"
    wait_until(lambda: setup.state() == before, "the old paint")
    assert game.redo() == f"Redo: {node['label']}"
    wait_until(lambda: setup.state() == after, "the new paint again")
    game.undo()
    wait_until(lambda: setup.state() == before, "the old paint again")


def edited_count(game, setup) -> int:
    """Snapshots of the wall's group taken before edits, from the store the
    player's history is in"""
    folders = STORE["folders"]()
    count = 0
    for folder in folders:
        index = folder / "index.xml"
        if index.exists():
            text = index.read_text("utf-8")
            count += text.count(f'Reason="Edited" MainGridName="{WALL_NAME}"')
    return count


# Where the player's grid store is: the local one offline. The dedicated server
# and lobby files set STORE_FOLDERS to the server's.
def local_store():
    return list((rig.APPDATA / "Undo" / "Worlds").glob("*/grids"))


STORE = {"folders": local_store}


@pytest.fixture(scope="module", autouse=True)
def store(request):
    STORE["folders"] = getattr(request.module, "STORE_FOLDERS", local_store)


def test_replace_on_the_aimed_grid(game, setup):
    one_step(game, setup, [], f"replaced paint on {WALL_NAME}", top_changes=False)


def test_replace_on_all_subgrids(game, setup):
    one_step(
        game,
        setup,
        ["LeftControl"],
        f"replaced paint on {WALL_NAME} and its subgrids",
        top_changes=True,
    )


def test_flood_fill(game, setup):
    one_step(
        game,
        setup,
        ["LeftShift"],
        f"flood filled paint on {WALL_NAME}",
        top_changes=False,
    )

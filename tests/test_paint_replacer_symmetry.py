"""Paint Replacer with symmetry planes on the wall grid (SE-1359): a replacement on
the aimed grid, on all its subgrids and as a flood fill is still one Undo step,
and undo and redo give every block its exact paint back, mirror images of the
aimed block included. The planes are set and the game's symmetry option is on.

Paint Replacer repaints with empty hands and leaves symmetry to the cube builder,
whose mirrored painting tests/test_symmetry.py covers. Skins are not covered: the
test accounts own no skins, and the game refuses to put an unowned skin on a
block, by hand, through Remote or through an undo alike.
"""

from __future__ import annotations

import time

import pytest

import test_paint_replacer as pr
from harness import wait_until
from test_paint_replacer import (  # noqa: F401 -- fixtures
    INTERVAL_S,
    PLUGINS,
    edited_count,
    replace,
    setup,
    store,
)

# The wall is the plane x = 0 of its grid, cells y and z from -1 to 1, the aimed
# block in the middle. Each setup crosses the wall; the last one mirrors the aimed
# block onto the block with the odd paint.
# Every test takes a snapshot of the wall; more than the default ten are kept
UNDO_CONFIG = {**pr.UNDO_CONFIG, "GridSnapshotsPerGrid": "100"}

PLANES = {
    "z-even": {"z": (0, False)},
    "z-odd": {"z": (0, True)},
    "yz-odd": {"y": (0, True), "z": (0, True)},
    "xyz-even": {"x": (0, False), "y": (0, False), "z": (0, False)},
    "onto-odd": {"y": (0, True), "z": (-1, True)},
}


def mirror(cell, axis: str, p: int, odd: bool) -> tuple:
    c = list(cell)
    i = "xyz".index(axis)
    c[i] = 2 * p - c[i] + ((1 if odd else 0) if axis == "z" else -(1 if odd else 0))
    return tuple(c)


def orbit(cell, planes: dict) -> set:
    cells = {tuple(cell)}
    for axis, (p, odd) in planes.items():
        cells |= {mirror(c, axis, p, odd) for c in cells}
    return cells


def set_planes(game, grid: int, planes: dict) -> None:
    args = {}
    for axis in "xyz":
        if axis in planes:
            p, odd = planes[axis]
            cell = [0, 0, 0]
            cell["xyz".index(axis)] = p
            args[axis], args[f"{axis}_odd"] = cell, odd
        else:
            args[axis], args[f"{axis}_odd"] = None, False
    game.api.set_grid_symmetry(grid, **args)
    game.api.set_game_option("cubeBuilderUseSymmetry", True)


def symmetry_off(game, grid: int) -> None:
    game.api.set_grid_symmetry(grid, x=None, y=None, z=None)
    game.api.set_game_option("cubeBuilderUseSymmetry", False)


def sym_step(game, setup, planes: dict, keys, label: str) -> None:
    before = setup.state()
    set_planes(game, setup.wall, planes)
    try:
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
        time.sleep(1.5)
        after = setup.state()
        # One step, nothing recorded after it, the game's own painting included
        assert game.labels(last) == [node["label"]], game.labels(last)
        wall = after[0]
        selected = wall[pr.AIMED]
        assert selected[0] != pr.PAINT, selected
        # Every mirror image of the aimed block that had the aimed paint got the
        # new one
        for cell in orbit(pr.AIMED, planes) & set(wall):
            if before[0][cell] == before[0][pr.AIMED]:
                assert wall[cell] == selected, (cell, wall[cell])
        changed = sum(
            a != b
            for old, new in zip(before, after)
            for a, b in zip(old.values(), new.values())
        )
        assert node["label"].endswith(
            f", painted {changed} block{"s" if changed != 1 else ""}"
        ), (
            node["label"],
            changed,
        )
        assert edited_count(game, setup) == snapshots + 1

        assert game.undo() == f"Undo: {node['label']}"
        wait_until(lambda: setup.state() == before, "the old paint")
        assert game.redo() == f"Redo: {node['label']}"
        wait_until(lambda: setup.state() == after, "the new paint again")
        game.undo()
        wait_until(lambda: setup.state() == before, "the old paint again")
    finally:
        symmetry_off(game, setup.wall)


@pytest.mark.parametrize("planes", list(PLANES))
def test_aimed_grid(game, setup, planes):
    sym_step(game, setup, PLANES[planes], [], f"replaced paint on {pr.WALL_NAME}")


@pytest.mark.parametrize("planes", list(PLANES))
def test_all_subgrids(game, setup, planes):
    sym_step(
        game,
        setup,
        PLANES[planes],
        ["LeftControl"],
        f"replaced paint on {pr.WALL_NAME} and its subgrids",
    )


@pytest.mark.parametrize("planes", list(PLANES))
def test_flood_fill(game, setup, planes):
    sym_step(
        game,
        setup,
        PLANES[planes],
        ["LeftShift"],
        f"flood filled paint on {pr.WALL_NAME}",
    )

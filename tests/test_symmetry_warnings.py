"""The red warnings after an edit made with symmetry on: one when the edit did not
come out the same at every mirror image, one when a removal split the grid.

A placement, a paint and a removal that reach every copy are quiet, and so are a
block on a plane and a block that mirrors into its other variant. A copy whose
cell is taken, a copy that is not there to remove or to paint, and a removal that
splits the grid each give one warning, which names the copies that were done.
Without symmetry nothing warns. tests/test_symmetry_warnings_off.py has the two
options switched off.

The warnings are read from the client's status file. Each one is also saved as
a screenshot into tests/artifacts.
"""

from __future__ import annotations

import re
import time

import rig
from harness import wait_until
from test_symmetry import (  # noqa: F401 -- the fixtures and EXTRA_GRIDS too
    EXTRA_GRIDS,
    LARGE,
    PAINT_SLOT,
    RED,
    VARIANTS,
    aim_at_block,
    builder_in_hand,
    floors,
    mirror,
    place,
    probe,
    set_planes,
    symmetry_off,
)

# A warning comes when the result is known: on a client of a server up to a
# second and a half after the request
QUIET_S = 3.0
GREEN = (0.33, 0.6, 0.1)


def warnings(game) -> list[str]:
    return game.status().get("warnings", [])


def expect(game, before: list[str], *texts: str) -> None:
    """Exactly these warnings came since before, and no more after them"""
    wait_until(
        lambda: warnings(game)[len(before) :] == list(texts),
        f"the warnings {texts}, got {warnings(game)[len(before):]}",
    )
    shot(game, texts[-1])
    time.sleep(QUIET_S)
    assert warnings(game)[len(before) :] == list(texts)


def expect_none(game, before: list[str]) -> None:
    time.sleep(QUIET_S)
    assert warnings(game) == before


def shot(game, text: str) -> None:
    name = re.sub(r"[^a-z0-9]+", "-", text.lower()).strip("-")
    rig.ARTIFACTS.mkdir(parents=True, exist_ok=True)
    (rig.ARTIFACTS / f"warning-{name}.png").write_bytes(game.api.screenshot_bytes())


def paint(game, surface, cell, grid: int) -> None:
    """The middle button on this block, with a color it does not have yet; the
    build colors of an earlier run stay with the client. A press can get lost
    on a busy machine, so it is repeated until the block changes."""
    color = game.color(cell, grid)
    new = next(c for c in (RED, GREEN) if tuple(round(v, 3) for v in c) != color)
    game.api.set_build_color(PAINT_SLOT, *new)
    aim_at_block(game, surface, cell, grid)

    def painted():
        game.mouse("middle")
        return game.color(cell, grid) != color

    wait_until(painted, "the paint", interval=0.5)


def test_edits_that_reach_every_copy_are_quiet(game):
    """Four copies placed, painted and removed"""
    surface = LARGE
    grid = surface.grid(game)
    planes = {"x": (surface.x, False), "z": (surface.z, False)}
    before = warnings(game)
    _, new = place(game, surface, grid, surface.near, planes)
    assert len(new) == 4, new
    expect_none(game, before)

    source = min(new, key=lambda c: sum((a - b) ** 2 for a, b in zip(c, surface.near)))
    colors = {c: game.color(c, grid) for c in new}
    paint(game, surface, source, grid)
    wait_until(lambda: all(game.color(c, grid) != colors[c] for c in new), "the paint")
    expect_none(game, before)

    game.mouse("right")
    wait_until(lambda: not any(game.exists(c, grid) for c in new), "all four to go")
    expect_none(game, before)


def test_block_on_a_plane_is_quiet(game):
    """Its mirror image is the block itself"""
    surface = LARGE
    grid = surface.grid(game)
    cell = probe(game, surface, grid, surface.near)
    before = warnings(game)
    planes = {"x": (cell[0], False), "z": (surface.z, False)}
    _, new = place(game, surface, grid, None, planes)
    assert new == {cell, mirror(cell, "z", surface.z, False)}
    expect_none(game, before)


def test_mirrored_variant_is_quiet(game):
    """The copy of a left window is a right window, which counts as the same"""
    surface = LARGE
    grid = surface.grid(game)
    game.cube_builder(f"MyObjectBuilder_CubeBlock/{VARIANTS['large'][0]}")
    before = warnings(game)
    node, _ = place(game, surface, grid, surface.side, {"x": (surface.x, False)})
    assert node["label"] == "placed 2 blocks"
    expect_none(game, before)


def test_taken_mirror_cell_warns(game):
    """One of four copies has a block in its way"""
    surface = LARGE
    grid = surface.grid(game)
    cell = probe(game, surface, grid, surface.near)
    game.build_block(mirror(cell, "x", surface.x, False), grid)
    before = warnings(game)
    planes = {"x": (surface.x, False), "z": (surface.z, False)}
    _, new = place(game, surface, grid, None, planes)
    assert len(new) == 3, new
    expect(game, before, "Symmetry mismatch: placed 3 of 4 mirrored blocks")


def test_removal_and_paint_without_the_copy_warn(game):
    """Two copies placed, one removed without symmetry: painting the other one
    and removing it with symmetry on finds nothing at the mirror image"""
    surface = LARGE
    grid = surface.grid(game)
    planes = {"x": (surface.x, False)}
    _, new = place(game, surface, grid, surface.near, planes)
    assert len(new) == 2, new
    source = min(new, key=lambda c: sum((a - b) ** 2 for a, b in zip(c, surface.near)))
    (other,) = new - {source}
    symmetry_off(game, grid)
    game.raze_block(other, grid)
    expect_none(game, warnings(game))

    before = warnings(game)
    set_planes(game, grid, planes)
    paint(game, surface, source, grid)
    expect(game, before, "Symmetry mismatch: painted 1 of 2 mirrored blocks")

    before = warnings(game)
    game.mouse("right")
    wait_until(lambda: not game.exists(source, grid), "the block to go")
    expect(game, before, "Symmetry mismatch: removed 1 of 2 mirrored blocks")


def remove_bridge(game) -> None:
    game.raze_block(rig.BRIDGE, label="removed 1 block, 1 part split off")


def restore_bridge(game) -> None:
    assert game.undo() == "Undo: removed 1 block, 1 part split off"
    wait_until(lambda: game.exists(rig.BRIDGE), "the bridge back")


def test_split_warns(game):
    """The bridge to part B removed: quiet without symmetry; with the bridge on
    the plane the split warning alone; with the plane elsewhere the copy is a
    floor block that stays, so both warnings"""
    grid = game.station
    symmetry_off(game, grid)
    before = warnings(game)
    remove_bridge(game)
    expect_none(game, before)
    restore_bridge(game)

    set_planes(game, grid, {"z": (rig.BRIDGE[2], False)})
    remove_bridge(game)
    expect(game, before, "Grid split: 1 part split off")
    restore_bridge(game)

    before = warnings(game)
    set_planes(game, grid, {"x": (8, False)})
    remove_bridge(game)
    expect(
        game,
        before,
        "Symmetry mismatch: removed 1 of 2 mirrored blocks",
        "Grid split: 1 part split off",
    )
    restore_bridge(game)

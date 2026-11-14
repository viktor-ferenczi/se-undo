"""The two symmetry warning options switched off: a taken mirror cell and a
removal that splits the grid, which warn in tests/test_symmetry_warnings.py, are
quiet. The plugin reads its config once at start, so this file has a client of
its own.
"""

from __future__ import annotations

import rig
from test_symmetry import (  # noqa: F401 -- the fixtures and EXTRA_GRIDS too
    EXTRA_GRIDS,
    LARGE,
    builder_in_hand,
    floors,
    mirror,
    place,
    probe,
    set_planes,
)
from test_symmetry_warnings import expect_none, remove_bridge, restore_bridge, warnings

UNDO_CONFIG = {"WarnOnSymmetryMismatch": "false", "WarnOnSymmetrySplit": "false"}


def test_no_warnings_with_the_options_off(game):
    surface = LARGE
    grid = surface.grid(game)
    cell = probe(game, surface, grid, surface.near)
    game.build_block(mirror(cell, "x", surface.x, False), grid)
    planes = {"x": (surface.x, False), "z": (surface.z, False)}
    _, new = place(game, surface, grid, None, planes)
    assert len(new) == 3, new
    expect_none(game, [])

    set_planes(game, grid, {"x": (8, False)})
    remove_bridge(game)
    expect_none(game, [])
    assert warnings(game) == []
    restore_bridge(game)

"""The undo of a big repaint on a grid with many emissive blocks (SE-1357): every
paint request makes the game update the emissivity of all emissive blocks of the
grid, one render message each, and the render queue of a frame holds 1M messages.
The replay sends its requests within a frame budget, so the game survives, and every
block still gets its exact paint back.

The grid: 40 by 38 armor blocks in a checkerboard of two paints, under 1520 neon
tubes. One request paints the armor over; its undo needs a request per block, as
neighbors differ, 1520 requests of 1521 messages each, 2.3M in one frame without the
budget (Paint Replacer PR #15 has the crash).
"""

from __future__ import annotations

import time

import rig
from harness import wait_until

NAME = "Undo Paint Budget"
GRID_ID = 777000558000001
# Station cell of the grid's block 0, 0, 0: high above the station, out of the way
AT = (-20, 40, -20)
SIZE_X, SIZE_Z = 40, 38
ARMOR = [(x, 0, z) for x in range(SIZE_X) for z in range(SIZE_Z)]

EVEN = (0.1, 0.5, 0.2)
ODD = (0.8, 0.3, -0.1)
OVER = (0.6, 0.3, 0.0)


def paint_of(cell) -> tuple:
    return EVEN if (cell[0] + cell[2]) % 2 == 0 else ODD


def _colored(xsi: str, subtype: str, cell, hsv, entity_id: int) -> str:
    block = rig._block(xsi, subtype, cell, entity_id=entity_id)
    return block.replace(
        '<ColorMaskHSV x="0" y="-0.8" z="0.55" />',
        f'<ColorMaskHSV x="{hsv[0]}" y="{hsv[1]}" z="{hsv[2]}" />',
    )


def EXTRA_GRIDS(position, forward, up) -> str:
    """The test grid, a static station put into the world before it loads"""
    blocks = []
    for i, cell in enumerate(ARMOR):
        blocks.append(
            _colored(
                "MyObjectBuilder_CubeBlock",
                "LargeBlockArmorBlock",
                cell,
                paint_of(cell),
                GRID_ID + 1 + i,
            )
        )
        blocks.append(
            _colored(
                "MyObjectBuilder_EmissiveBlock",
                "LargeNeonTubesStraight1",
                (cell[0], 1, cell[2]),
                OVER,
                GRID_ID + 10001 + i,
            )
        )
    return rig._grid(
        NAME,
        GRID_ID,
        blocks,
        rig.station_point(AT),
        forward,
        up,
        True,
        extra="<IsUnsupportedStation>true</IsUnsupportedStation>",
    )


def armor(game) -> dict[tuple, tuple] | None:
    """The paint of every armor block, None while the client misses blocks"""
    cubes = game.cubes(GRID_ID)
    paints = {
        cell: tuple(round(float(c), 3) for c in cubes[cell]["colorMaskHsv"])
        for cell in ARMOR
        if cell in cubes
    }
    return paints if len(paints) == len(ARMOR) else None


def settled(game, label: str) -> None:
    """The replay finished within its time, its result known"""
    wait_until(
        lambda: not game.build().get("locked"), f"the end of {label}", timeout=60
    )
    node = next(n for n in game.build()["nodes"] if n["label"] == label)
    assert not node["unknown"], node


def test_undo_of_a_big_repaint(game):
    before = wait_until(lambda: armor(game), "the test grid")
    assert before == {cell: paint_of(cell) for cell in ARMOR}
    over = {cell: OVER for cell in ARMOR}
    label = f"painted {len(ARMOR)} blocks"

    last = game.last_node_id()
    game.api.character_grid_event(
        GRID_ID, (0, 0, 0), "color", max_pos=(SIZE_X - 1, 0, SIZE_Z - 1), hsv=OVER
    )
    game.wait_recorded(last, label)
    wait_until(lambda: armor(game) == over, "the paint over the checkerboard")

    started = time.monotonic()
    assert game.undo() == f"Undo: {label}"
    wait_until(lambda: armor(game) == before, "the checkerboard back", timeout=60)
    settled(game, label)
    print(f"undo of {len(ARMOR)} requests: {time.monotonic() - started:.1f} s")
    assert game.api.get_state(), "the client survived"

    assert game.redo() == f"Redo: {label}"
    wait_until(lambda: armor(game) == over, "the paint over again", timeout=60)
    settled(game, label)
    game.undo()
    wait_until(lambda: armor(game) == before, "the checkerboard again", timeout=60)
    settled(game, label)

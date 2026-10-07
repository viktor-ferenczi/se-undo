"""The same removals as in test_block_links.py, in a world without temporary
containers. There the game drops what a removed block holds as loose items, one
floating object per item kind, instead of one container bag. Both lie where the
block was and have to go before the block can come back.
"""

from __future__ import annotations

import time

import pytest

import rig
from harness import wait_until

WORLD_SETTINGS = {"TemporaryContainers": "false"}

GRID = rig.LINKS_ID
CARGO = rig.LINKS["cargo"]


@pytest.fixture(scope="module", autouse=True)
def out_of_the_seat(game):
    game.leave_seat()


def items(game) -> dict:
    inventory = game.api.get_inventory(GRID, CARGO)["items"]
    return {i["subtypeId"]: int(i["amountRaw"]) for i in inventory}


def free(game) -> bool:
    return game.api.character_build_block(GRID, CARGO, test_only=True)["canPlaceBlock"]


def test_container_comes_back_with_its_items(game):
    held = items(game)
    assert set(held) == set(rig.LINKS_ITEMS)
    entity = game.block(CARGO, GRID)["entityId"]

    node = game.raze_block(CARGO, GRID)
    # Polled, not slept on: the items fall out of the cell under gravity, which
    # on a busy machine can happen within a second
    wait_until(lambda: not free(game), "the loose items in the cell", timeout=5)

    assert game.undo() == f"Undo: {node['label']}"
    wait_until(lambda: game.exists(CARGO, GRID), "the container")
    assert game.block(CARGO, GRID)["entityId"] == entity
    assert items(game) == held


def test_nothing_is_left_lying_around(game):
    """Removed by the redo and restored again: the cell is free in between, which
    it would not be with the items of the first removal still there"""
    held = items(game)
    assert game.redo() == "Redo: removed 1 block"
    wait_until(lambda: not game.exists(CARGO, GRID), "the removal again")
    time.sleep(1)
    assert free(game)
    assert game.undo() == "Undo: removed 1 block"
    wait_until(lambda: game.exists(CARGO, GRID), "the container")
    assert items(game) == held

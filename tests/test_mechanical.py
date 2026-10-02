"""Removals that take a mechanical connection apart: the base of a rotor, hinge or
piston, or the part on its other end. What the block held comes loose and falls or
drifts away, so undo does not try to connect it again. It closes what is left of the
grid group and creates the group from a backup taken before the removal.

The rigs are built in the test: a base placed on the station brings its top part as
a subgrid, and one armor block goes onto that.
"""

from __future__ import annotations

import math
import time

import pytest

import rig
from harness import wait_until

KINDS = {
    "rotor": ("LargeStator", (3, 1, 3)),
    "hinge": ("MyObjectBuilder_MotorAdvancedStator/LargeHinge", (11, 0.5, 4)),
    "piston": ("LargePistonBase", (11, 1, 3)),
}
NEIGHBORS = [(0, 1, 0), (1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1), (0, -1, 0)]
# Where the character stands to place a block with the cube builder
BUILDER_AT = (15, 0.6, 4)
# A part that came loose is further off than this within a second or two
ATTACHED_M = 0.3


@pytest.fixture(scope="module", autouse=True)
def out_of_the_seat(game):
    game.leave_seat()


class Rig:
    """A mechanical base on a grid, its top part, and one armor block on that"""

    def __init__(self, game, subtype: str, cell, grid=None):
        self.game = game
        self.grid = grid or game.station
        known = {g["entityId"] for g in game.grids()}
        cubes = set(game.cubes(self.grid))
        last = game.last_node_id()
        if "/" in subtype:
            # Placed by hand: the build request of the Remote API always asks for
            # an upright block, which the game refuses for a hinge
            game.stand_at(BUILDER_AT, tolerance=1.0)
            game.cube_builder(subtype)
            assert game.aim(cell)
            game.mouse("left")
            game.api.key("D0")
        else:
            sent = game.api.character_build_block(self.grid, cell, subtype=subtype)
            assert sent["sent"], sent
        game.wait_recorded(last, "placed 1 block")
        # A base that is not built upright has its min corner in another cell
        (self.cell,) = set(game.cubes(self.grid)) - cubes
        (top,) = wait_until(
            lambda: [g for g in game.grids() if g["entityId"] not in known],
            "the top part's grid",
        )
        self.top = top["entityId"]
        (head,) = game.cubes(self.top)
        self.extra = self.build_on_top(head)
        self.head = head
        self.cells = set(game.cubes(self.top))
        self.base_id = game.block(self.cell, self.grid)["entityId"]
        self.head_id = game.block(head, self.top)["entityId"]
        self.distance = self.distance_now()

    def build_on_top(self, head) -> tuple:
        """One armor block beside the top part, on the first side that takes one"""
        game = self.game
        for step in NEIGHBORS:
            cell = tuple(h + d for h, d in zip(head, step))
            last = game.last_node_id()
            game.api.character_build_block(self.top, cell)
            try:
                wait_until(lambda: game.exists(cell, self.top), "the block", timeout=3)
            except AssertionError:
                continue
            game.wait_recorded(last, "placed 1 block")
            return cell
        raise AssertionError("No side of the top part takes a block")

    def distance_now(self) -> float:
        """From the top grid to the grid the base is on; the same while the two
        are connected, however the pair moves and turns"""
        api = self.game.api
        top, base = api.get_grid(self.top), api.get_grid(self.grid)
        return math.dist(top["position"], base["position"])

    def attached(self) -> bool:
        game = self.game
        if not game.exists(self.cell, self.grid):
            return False
        if not any(g["entityId"] == self.top for g in game.grids()):
            return False
        return abs(self.distance_now() - self.distance) < ATTACHED_M

    def assert_whole(self) -> None:
        """Base, top part and extra block are back as themselves, and stay"""
        game = self.game
        wait_until(self.attached, "the group to be back in one piece")
        assert game.block(self.cell, self.grid)["entityId"] == self.base_id
        assert set(game.cubes(self.top)) == self.cells
        assert game.block(self.head, self.top)["entityId"] == self.head_id
        # A top part that is only lying there falls, drifts or gets pushed away
        time.sleep(3)
        assert self.attached(), self.distance_now()


def saved_top_id(game, built) -> str | None:
    """What the base points at in a saved world"""
    base = next(
        b
        for b in game.saved_station().iter("MyObjectBuilder_CubeBlock")
        if b.findtext("EntityId") == str(built.base_id)
    )
    return base.findtext("TopBlockId")


@pytest.mark.parametrize("kind", KINDS)
def test_removed_base_comes_back_with_its_subgrid(game, kind):
    subtype, cell = KINDS[kind]
    built = Rig(game, subtype, cell)
    cell = built.cell
    lamp = game.block(rig.TARGET)["entityId"]
    assert saved_top_id(game, built) == str(built.head_id)

    node = game.raze_block(cell)
    time.sleep(2)  # time for the top part to come loose

    assert game.undo() == f"Undo: {node['label']}"
    built.assert_whole()
    assert saved_top_id(game, built) == str(built.head_id)
    # The station is the same grid with the same blocks
    assert game.grid_named(rig.STATION_NAME)["entityId"] == game.station
    assert game.block(rig.TARGET)["entityId"] == lamp

    assert game.redo() == f"Redo: {node['label']}"
    wait_until(lambda: not game.exists(cell), "the base to go again")
    time.sleep(1)
    assert game.undo() == f"Undo: {node['label']}"
    built.assert_whole()


def test_removed_top_part_comes_back_connected(game):
    """The other end: the rotor head removed from under the block it carries"""
    subtype, cell = "LargeStator", (3, 1, 9)
    built = Rig(game, subtype, cell)

    node = game.raze_block(built.head, built.top)
    time.sleep(2)

    assert game.undo() == f"Undo: {node['label']}"
    built.assert_whole()


def test_steps_before_the_removal_still_work_after_the_restore(game):
    """The restore creates the grids again. Older steps on them find them."""
    subtype, cell = "LargeStator", (7, 1, 9)
    marker = (9, 1, 9)
    game.build_block(marker)
    built = Rig(game, subtype, cell)

    cell = built.cell
    game.raze_block(cell)
    time.sleep(1)
    game.undo(expect="Undo: removed 1 block")
    built.assert_whole()

    # Back over the extra block, the base and the marker
    game.undo(expect="Undo: placed 1 block")
    wait_until(lambda: not game.exists(built.extra, built.top), "the extra block")
    game.undo(expect="Undo: placed 1 block")
    wait_until(lambda: not game.exists(cell), "the base to go")
    game.undo(expect="Undo: placed 1 block")
    wait_until(lambda: not game.exists(marker), "the marker to go")


def test_moving_ship_is_restored_where_it_is_now(game):
    """A rotor on the drifting ship. The ship flies on between the removal and the
    undo, and the group comes back at the ship, not where the backup was taken."""
    api, ship = game.api, rig.DRIFT_SHIP_ID
    built = Rig(game, "LargeStator", (1, 1, 1), grid=ship)
    start = api.get_grid(ship)["position"]

    # A push, then the thruster is off again and the ship coasts: the backup holds
    # the ship as it is at the removal, a thruster left on included
    api.set_enabled(ship, rig.DRIFT_THRUSTER, True)
    try:
        wait_until(lambda: api.get_grid(ship)["speed"] > 5, "the ship to move")
    finally:
        api.set_enabled(ship, rig.DRIFT_THRUSTER, False)
    node = game.raze_block(built.cell, ship)
    time.sleep(2)
    before = api.get_grid(ship)
    assert math.dist(before["position"], start) > 5

    assert game.undo() == f"Undo: {node['label']}"
    wait_until(built.attached, "the rotor and its top part on the ship")
    after = api.get_grid(ship)
    travelled = math.dist(after["position"], before["position"])
    assert travelled < math.dist(after["position"], start)
    assert after["speed"] == pytest.approx(before["speed"], abs=2)
    assert game.block(built.cell, ship)["entityId"] == built.base_id
    time.sleep(2)
    assert built.attached()


def test_undo_of_placing_a_base_takes_its_top_part_along(game):
    """Placing a rotor creates its top part as a grid of its own. Undoing the
    placement must not leave that part behind."""
    known = {g["entityId"] for g in game.grids()}
    cell = (11, 1, 9)
    game.build_block(cell, subtype="LargeStator")
    wait_until(
        lambda: [g for g in game.grids() if g["entityId"] not in known],
        "the top part's grid",
    )

    assert game.undo() == "Undo: placed 1 block"
    wait_until(lambda: not game.exists(cell), "the base to go")
    time.sleep(2)
    assert not [g for g in game.grids() if g["entityId"] not in known]

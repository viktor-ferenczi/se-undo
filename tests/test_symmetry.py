"""The cube builder with symmetry on, driven with real mouse and keyboard input:
one, two and three planes through block centers and along block edges, on the
large test station and on a small grid plate; blocks on a plane; mirror cells that
are taken; mirrored block variants and orientations; single and area removal;
line and plane builds; paint; and a later change to one of the copies.

Every action is one undo step for all the copies the game built, and redo brings
each copy back with its subtype, orientation, color and state.

The planes are the client's own, set through Remote. The game mirrors a cell on
each plane, an odd plane runs along the cell's edge:
    x' = 2p - x - odd, y' = 2p - y - odd, z' = 2p - z + odd
"""

from __future__ import annotations

import re
import time
from dataclasses import dataclass

import pytest

import rig
from harness import AIM_TOLERANCE, wait_until
from se_remote import CallOp

# --- the small grid plate --------------------------------------------------------

PLATE_NAME = "Undo Symmetry Plate"
PLATE_ID = 777000557000001
PLATE_SIZE = 24
SMALL_BLOCK_M = 0.5
# Station cell of the plate's block 0, 0, 0, out in the air beside the station
PLATE_AT = (-8, 0, 4)


def plate_point(cell) -> list[float]:
    """World position of a plate cell centre, like rig.station_point"""
    scale = SMALL_BLOCK_M / rig.LARGE_BLOCK_M
    return rig.station_point([PLATE_AT[i] + cell[i] * scale for i in range(3)])


def EXTRA_GRIDS(position, forward, up) -> str:
    """The plate, a static small grid of armor blocks, upright like the station"""
    blocks = [
        rig._block("MyObjectBuilder_CubeBlock", "SmallBlockArmorBlock", (x, 0, z))
        for x in range(PLATE_SIZE)
        for z in range(PLATE_SIZE)
    ]
    grid = rig._grid(
        PLATE_NAME,
        PLATE_ID,
        blocks,
        rig.station_point(PLATE_AT),
        forward,
        up,
        True,
        extra="<IsUnsupportedStation>true</IsUnsupportedStation>",
    )
    return grid.replace(
        "<GridSizeEnum>Large</GridSizeEnum>", "<GridSizeEnum>Small</GridSizeEnum>"
    )


# --- where the tests build ---------------------------------------------------------


@dataclass
class Surface:
    """A floor to build on, in cells of its grid: where the character stands,
    points on the floor's top face to aim at, and where the x and z planes go"""

    name: str
    armor: str
    stand: tuple
    near: tuple
    side: tuple
    # Lines start here and come towards the character
    line: tuple
    x: int
    z: int
    point: callable

    def grid(self, game) -> int:
        return (
            game.station
            if self.name == "large"
            else game.grid_named(PLATE_NAME)["entityId"]
        )


LARGE = Surface(
    "large",
    "LargeBlockArmorBlock",
    stand=(15, 0.6, 4),
    near=(11, 0.5, 4),
    side=(11, 0.5, 6),
    line=(9, 0.5, 4),
    x=8,
    z=8,
    point=rig.station_point,
)
SMALL = Surface(
    "small",
    "SmallBlockArmorBlock",
    stand=(21, 2.5, 12),
    near=(17, 0.5, 12),
    side=(17, 0.5, 14),
    line=(14, 0.5, 12),
    x=12,
    z=8,
    point=plate_point,
)
SURFACES = [LARGE, SMALL]


def mirror(cell, axis: str, p: int, odd: bool) -> tuple:
    c = list(cell)
    i = "xyz".index(axis)
    c[i] = 2 * p - c[i] + ((1 if odd else 0) if axis == "z" else -(1 if odd else 0))
    return tuple(c)


def orbit(cell, planes: dict) -> set:
    """The cell and every mirror image of it the game builds"""
    cells = {tuple(cell)}
    for axis, (p, odd) in planes.items():
        cells |= {mirror(c, axis, p, odd) for c in cells}
    return cells


def plural(count: int, noun: str = "block") -> str:
    return f"1 {noun}" if count == 1 else f"{count} {noun}s"


# --- driving the client ------------------------------------------------------------


def set_planes(game, grid: int, planes: dict) -> None:
    """Only the planes given, the others removed; symmetry on"""
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


def stand_on(game, surface: Surface) -> None:
    if surface is LARGE:
        game.stand_at(surface.stand, tolerance=1.0)
        return
    rig.focus_gameplay(game.api)
    game.leave_seat()
    target = plate_point(surface.stand)
    game.api.character_teleport(*target)
    wait_until(
        lambda: sum(
            (a - b) ** 2 for a, b in zip(game.api.get_character()["position"], target)
        )
        < 1.0,
        "the teleport onto the plate",
    )


def aim(game, surface: Surface, cell, grid: int) -> dict:
    """Looks at a point in the surface's cells, returns the block under the
    crosshair, which must be on the surface's grid"""
    point = surface.point(cell)
    target = None
    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        game.api.character_look_at(*point, tolerance=AIM_TOLERANCE)
        time.sleep(0.2)
        target = game.api.get_character_target(max_distance=100).get("block")
        if (
            target
            and target["gridId"] == grid
            and all(abs(m - c) <= 1.5 for m, c in zip(target["min"], cell))
        ):
            return target
    raise AssertionError(f"The view did not settle on {cell}: {target}")


def aim_at_block(game, surface: Surface, cell, grid: int) -> None:
    """The crosshair on this very block"""

    def aimed():
        game.api.character_look_at(
            *surface.point((cell[0] + 0.2, cell[1], cell[2])), tolerance=AIM_TOLERANCE
        )
        time.sleep(0.2)
        target = game.api.get_character_target(max_distance=100).get("block")
        return (
            target and target["gridId"] == grid and tuple(target["min"]) == tuple(cell)
        )

    wait_until(aimed, f"the crosshair on {cell}", interval=0.1)


def drag(game, button: str, keys=("LeftControl",)) -> None:
    """A mouse button held with Ctrl (and Shift for a plane) while the view moves,
    the cube builder's line and plane mode, see test_builder_input.drag. The line
    or area ends at the character's feet, whichever way the view moves."""
    api = game.api
    held = {"keys": list(keys), f"mouse_{button}": True}
    api.set_input_state(mode="override", mouse_x=0, mouse_y=0, **held)
    time.sleep(0.4)
    for step in range(1, 5):
        api.set_input_state(mode="override", mouse_x=0, mouse_y=15 * step, **held)
        time.sleep(0.3)
    api.set_input_state(mode="override", keys=list(keys), mouse_x=0, mouse_y=60)
    time.sleep(0.3)
    api.clear_input_state()


def snapshot(game, grid: int) -> dict:
    """What a restore must bring back, per cell: subtype, orientation, color, skin"""
    keys = ("subtypeId", "forward", "up", "colorMaskHsv", "skin")
    return {
        cell: {k: cube.get(k) for k in keys} for cell, cube in game.cubes(grid).items()
    }


def wait_node(game, last: int) -> dict:
    """The newest node once something was recorded after last, and nothing else
    for a moment: a symmetry action that became several nodes shows here"""
    wait_until(lambda: game.last_node_id() > last, "a new node")
    time.sleep(1.0)
    return game.build()["nodes"][-1]


def assert_one_step(game, last: int, label: str) -> dict:
    nodes = [n for n in game.build()["nodes"] if n["id"] > last]
    assert [n["label"] for n in nodes] == [label]
    assert game.build()["current"] == nodes[0]["id"]
    return nodes[0]


def undo_redo(game, grid: int, label: str, before: dict, after: dict) -> None:
    """Undo goes back to before, redo to after, cell by cell; ends undone"""
    assert game.undo() == f"Undo: {label}"
    wait_until(lambda: snapshot(game, grid) == before, "the state before")
    assert game.redo() == f"Redo: {label}"
    wait_until(lambda: snapshot(game, grid) == after, "the state after, every copy")
    assert game.undo() == f"Undo: {label}"
    wait_until(lambda: snapshot(game, grid) == before, "the state before again")


def place(game, surface: Surface, grid: int, point, planes: dict) -> tuple:
    """Left click with symmetry; returns the node and the new cells. Without a
    point the view stays where it is."""
    if point is not None:
        aim(game, surface, point, grid)
    set_planes(game, grid, planes)
    before = set(game.cubes(grid))
    last = game.last_node_id()
    game.mouse("left")
    node = wait_node(game, last)
    return node, set(game.cubes(grid)) - before


def build_and_check(
    game, surface: Surface, planes: dict, point=None, expect_count=None
):
    """A symmetric placement is one step for every copy the game built, undo and
    redo take and bring back all of them"""
    grid = surface.grid(game)
    before = snapshot(game, grid)
    last = game.last_node_id()
    node, new = place(game, surface, grid, point or surface.near, planes)
    expected = orbit(next(iter(new)), planes) - set(before)
    assert new == expected
    if expect_count is not None:
        assert len(new) == expect_count, new
    label = f"placed {plural(len(new))}"
    assert_one_step(game, last, label)
    after = snapshot(game, grid)
    undo_redo(game, grid, label, before, after)
    return new, after


# --- fixtures ------------------------------------------------------------------------


@pytest.fixture(scope="module")
def floors(game) -> dict:
    """The cells of both grids as the world has them, and the grids there are"""
    cells = {s.name: set(game.cubes(s.grid(game))) for s in SURFACES}
    return {"cells": cells, "grids": {g["entityId"] for g in game.grids()}}


@pytest.fixture(autouse=True)
def builder_in_hand(game, request, floors):
    """The cube builder with an armor block of the surface's size; symmetry off at
    the end, and whatever a failed test left on the floors removed. The same slot
    pressed twice would switch to the next block of its group, so the hand is
    emptied first."""
    callspec = getattr(request.node, "callspec", None)
    surface = callspec.params.get("surface", LARGE) if callspec else LARGE
    stand_on(game, surface)
    game.api.clear_input_state()
    game.api.key("D0")
    time.sleep(0.3)
    game.cube_builder(surface.armor)
    yield surface
    game.api.clear_input_state()
    for s in SURFACES:
        grid = s.grid(game)
        symmetry_off(game, grid)
        # Farthest from the floor first, nothing splits off
        left = set(game.cubes(grid)) - floors["cells"][s.name]
        for cell in sorted(left, key=lambda c: -abs(c[1])):
            game.api.character_grid_event(grid, cell, "raze")
    for grid in game.grids():
        if grid["entityId"] not in floors["grids"]:
            game.api.close_grid(grid["entityId"])


# --- planes ---------------------------------------------------------------------------


@pytest.mark.parametrize("surface", SURFACES, ids=lambda s: s.name)
@pytest.mark.parametrize(
    "axes,odd",
    [("x", False), ("x", True), ("z", False), ("z", True), ("y", False)]
    + [("xz", False), ("xz", True), ("xyz", False)],
    ids=lambda v: v if isinstance(v, str) else ("odd" if v else "even"),
)
def test_planes(game, surface, axes, odd):
    """Two, four and eight copies; the y plane at 0 puts the copies under the
    floor, which the floor holds"""
    planes = {}
    for axis in axes:
        p = {"x": surface.x, "z": surface.z, "y": 0}[axis]
        planes[axis] = (p, odd and axis != "y")
    build_and_check(game, surface, planes, expect_count=2 ** len(axes))


@pytest.mark.parametrize("surface", SURFACES, ids=lambda s: s.name)
def test_three_odd_planes(game, surface):
    """Odd planes on all three axes, eight copies in one step. The odd y plane at
    2 puts each copy on top of another: the character cannot see the top of a
    block to build on, nor would anything hold a copy below the floor."""
    planes = {"x": (surface.x, True), "y": (2, True), "z": (surface.z, True)}
    new, _ = build_and_check(game, surface, planes, expect_count=8)
    assert {c[1] for c in new} == {1, 2}


def probe(game, surface: Surface, grid: int, point) -> tuple:
    """The cell a click at this point builds into, found by a placement without
    symmetry and its undo; the view stays where it is"""
    aim(game, surface, point, grid)
    symmetry_off(game, grid)
    last = game.last_node_id()
    before = set(game.cubes(grid))
    game.mouse("left")
    game.wait_recorded(last, "placed 1 block")
    (cell,) = set(game.cubes(grid)) - before
    assert game.undo() == "Undo: placed 1 block"
    wait_until(lambda: not game.exists(cell, grid), "the probe to go")
    return cell


@pytest.mark.parametrize("surface", SURFACES, ids=lambda s: s.name)
def test_block_on_a_plane(game, surface):
    """A block on an even plane is its own mirror image: the copies that fall into
    the same cell are built once"""
    grid = surface.grid(game)
    cell = probe(game, surface, grid, surface.near)
    before = snapshot(game, grid)
    last = game.last_node_id()
    planes = {"x": (cell[0], False), "z": (surface.z, False)}
    node, new = place(game, surface, grid, None, planes)
    assert new == {cell, mirror(cell, "z", surface.z, False)}
    assert_one_step(game, last, "placed 2 blocks")
    undo_redo(
        game, grid, "placed 2 blocks", before, snapshot_after(game, grid, before, new)
    )


def snapshot_after(game, grid, before, new) -> dict:
    after = snapshot(game, grid)
    assert set(after) == set(before) | new
    return after


@pytest.mark.parametrize("surface", SURFACES, ids=lambda s: s.name)
def test_mirror_cells_taken(game, surface):
    """Copies whose cell is taken are not built: one by a block placed there
    before, one by the floor itself (an odd y plane through the floor's top)"""
    grid = surface.grid(game)
    cell = probe(game, surface, grid, surface.near)
    taken = mirror(cell, "x", surface.x, False)
    # Something else in the way, built without symmetry
    blocker = game.build_block(taken, grid, subtype=surface.armor)

    before = snapshot(game, grid)
    last = game.last_node_id()
    planes = {"x": (surface.x, False), "y": (cell[1], True)}
    node, new = place(game, surface, grid, None, planes)
    assert new == {cell}, new
    assert_one_step(game, last, "placed 1 block")
    after = snapshot_after(game, grid, before, new)
    # The block in the way stays through all of it
    undo_redo(game, grid, "placed 1 block", before, after)
    assert game.exists(taken, grid)

    assert game.undo() == f"Undo: {blocker['label']}"
    wait_until(lambda: not game.exists(taken, grid), "the blocker to go")


# --- block variants and orientation -----------------------------------------------------


VARIANTS = {
    "large": ("Window1x2SideLeft", "Window1x2SideRight"),
    "small": ("SmallWindow1x2SideLeft", "SmallWindow1x2SideRight"),
}


@pytest.mark.parametrize("surface", SURFACES, ids=lambda s: s.name)
def test_mirrored_variant(game, surface):
    """A block with a left and a right variant: the mirror copy is the other one,
    and redo builds each copy as the variant it was"""
    left, right = VARIANTS[surface.name]
    grid = surface.grid(game)
    game.cube_builder(f"MyObjectBuilder_CubeBlock/{left}")
    before = snapshot(game, grid)
    last = game.last_node_id()
    node, new = place(game, surface, grid, surface.side, {"x": (surface.x, False)})
    assert node["label"] == "placed 2 blocks"
    assert_one_step(game, last, "placed 2 blocks")
    after = snapshot(game, grid)
    placed = {c: after[c] for c in after if c not in before}
    assert sorted(b["subtypeId"] for b in placed.values()) == sorted([left, right])
    undo_redo(game, grid, "placed 2 blocks", before, after)


@pytest.mark.parametrize("surface", SURFACES, ids=lambda s: s.name)
def test_mirrored_orientation(game, surface):
    """A slope turned with the rotation keys, mirrored on two planes: four
    orientations, each one back after redo"""
    grid = surface.grid(game)
    slope = "LargeBlockArmorSlope" if surface is LARGE else "SmallBlockArmorSlope"
    game.cube_builder(slope)
    aim(game, surface, surface.side, grid)
    game.api.key("PageDown")
    game.api.key("Delete")
    time.sleep(0.3)
    new, after = build_and_check(
        game,
        surface,
        {"x": (surface.x, False), "z": (surface.z, False)},
        point=surface.side,
        expect_count=4,
    )
    orientations = {(after[c]["forward"], after[c]["up"]) for c in new}
    assert len(orientations) > 1, orientations


# --- removal ----------------------------------------------------------------------------


@pytest.mark.parametrize("surface", [LARGE], ids=lambda s: s.name)
def test_single_removal(game, surface):
    """Batteries placed on two planes and renamed one by one; the right button
    with symmetry removes all four in one step, undo brings each back under its
    id with its name. (The cube builder's ray misses a small light lying on the
    floor.)"""
    grid = surface.grid(game)
    game.cube_builder("MyObjectBuilder_BatteryBlock/LargeBlockBatteryBlock")
    planes = {"x": (surface.x, False), "z": (surface.z, False)}
    empty = snapshot(game, grid)
    last = game.last_node_id()
    node, new = place(game, surface, grid, surface.near, planes)
    assert node["label"] == "placed 4 blocks", node
    names = {}
    for i, cell in enumerate(sorted(new)):
        block = game.block(cell, grid)
        names[cell] = (block["entityId"], f"Mirror Battery {i}")
        game.api.set_custom_name(grid, cell, f"Mirror Battery {i}")
    wait_until(
        lambda: all(
            game.block(c, grid)["customName"] == n for c, (_, n) in names.items()
        ),
        "the names",
    )
    placed = game.last_node_id()
    assert placed == last + 1

    source = min(new, key=lambda c: sum((a - b) ** 2 for a, b in zip(c, surface.near)))
    aim_at_block(game, surface, source, grid)
    game.mouse("right")
    wait_until(lambda: not any(game.exists(c, grid) for c in new), "all four to go")
    node = wait_node(game, placed)
    assert_one_step(game, placed, "removed 4 blocks")

    def restored():
        for cell, (entity_id, name) in names.items():
            block = game.block(cell, grid)
            if (
                not block
                or block["entityId"] != entity_id
                or block["customName"] != name
            ):
                return False
        return True

    assert game.undo() == "Undo: removed 4 blocks"
    wait_until(restored, "every battery back with its id and name")
    assert game.redo() == "Redo: removed 4 blocks"
    wait_until(
        lambda: not any(game.exists(c, grid) for c in new), "all four to go again"
    )
    game.undo()
    wait_until(restored, "every battery back again")

    assert game.undo() == "Undo: placed 4 blocks"
    wait_until(lambda: snapshot(game, grid) == empty, "no batteries")


def quarters(cells, surface: Surface) -> int:
    """How many of the four quarters the x and z planes cut the floor into have
    some of the cells"""
    return len({(c[0] < surface.x, c[2] < surface.z) for c in cells})


@pytest.mark.parametrize("surface", SURFACES, ids=lambda s: s.name)
def test_line_build_and_area_removal(game, surface):
    """A line built with Ctrl and a drag on a mirror plane is one step for both
    lines; Ctrl and the right button over it removes both lines in one step"""
    grid = surface.grid(game)
    planes = {"x": (surface.x, False), "z": (surface.z, False)}
    before = snapshot(game, grid)
    last = game.last_node_id()
    aim(game, surface, surface.line, grid)
    set_planes(game, grid, planes)
    drag(game, "left")
    node = wait_node(game, last)
    count = int(re.fullmatch(r"placed (\d+) blocks", node["label"]).group(1))
    line = set(game.cubes(grid)) - set(before)
    assert len(line) == count, (count, line)
    # Each copy works out its own line from the mirrored start, they need not be
    # equally long
    assert quarters(line, surface) == 4, line
    assert_one_step(game, last, node["label"])
    after = snapshot(game, grid)
    undo_redo(game, grid, node["label"], before, after)
    assert game.redo() == f"Redo: {node['label']}"
    wait_until(lambda: snapshot(game, grid) == after, "the lines")

    # The removal starts on the block nearest to the character, the others are
    # behind it, and goes away from it
    built = game.last_node_id()
    source = min(
        line, key=lambda c: sum((a - b) ** 2 for a, b in zip(c, surface.stand))
    )
    aim_at_block(game, surface, source, grid)
    drag(game, "right")
    removal = wait_node(game, built)
    removed = int(re.match(r"removed (\d+) blocks", removal["label"]).group(1))
    assert removed >= 4
    assert_one_step(game, built, removal["label"])
    gone = snapshot(game, grid)
    assert len(set(after) - set(gone)) == removed and set(gone) <= set(after)
    assert quarters(set(after) - set(gone), surface) == 4
    undo_redo(game, grid, removal["label"], after, gone)
    # undo_redo ended with the removal undone: the lines are back
    assert game.undo() == f"Undo: {node['label']}"
    wait_until(lambda: snapshot(game, grid) == before, "no lines")


@pytest.mark.parametrize("surface", SURFACES, ids=lambda s: s.name)
def test_plane_build(game, surface):
    """Ctrl-Shift and a drag builds an area of blocks; with two planes the four
    areas are one step. The drag ends at the character's feet whichever way the
    view moves, so the area starts off to the side of it."""
    grid = surface.grid(game)
    planes = {"x": (surface.x, False), "z": (surface.z, False)}
    before = snapshot(game, grid)
    last = game.last_node_id()
    aim(game, surface, surface.side, grid)
    set_planes(game, grid, planes)
    drag(game, "left", keys=("LeftControl", "LeftShift"))
    node = wait_node(game, last)
    area = set(game.cubes(grid)) - set(before)
    assert node["label"] == f"placed {plural(len(area))}"
    assert quarters(area, surface) == 4, area
    # An area, not a line: two axes vary within a quarter
    quarter = {c for c in area if c[0] > surface.x and c[2] < surface.z}
    assert sum(len({c[i] for c in quarter}) > 1 for i in range(3)) == 2, area
    assert_one_step(game, last, node["label"])
    undo_redo(game, grid, node["label"], before, snapshot(game, grid))


# --- paint ------------------------------------------------------------------------------


PAINT_SLOT = 8
RED = (0.0, 0.6, 0.1)


@pytest.mark.parametrize("surface", SURFACES, ids=lambda s: s.name)
def test_paint(game, surface):
    """The middle button paints the block and its mirror images, one step"""
    grid = surface.grid(game)
    game.api.set_build_color(PAINT_SLOT, *RED)
    aim(game, surface, surface.near, grid)
    set_planes(game, grid, {"x": (surface.x, False), "z": (surface.z, False)})
    before = snapshot(game, grid)
    last = game.last_node_id()
    game.mouse("middle")
    wait_node(game, last)
    after = snapshot(game, grid)
    painted = [c for c in after if after[c] != before[c]]
    assert len(painted) == 4, painted
    assert orbit(painted[0], {"x": (surface.x, False), "z": (surface.z, False)}) == set(
        painted
    )
    assert_one_step(game, last, "painted 4 blocks")
    undo_redo(game, grid, "painted 4 blocks", before, after)


# --- a later change to one copy -----------------------------------------------------------


def test_later_change_to_one_copy(game):
    """Two lights placed with symmetry. One is renamed (a terminal step, in the
    other history): undo of the placement still removes both, and redo brings
    both back under their ids, the renamed one with its name. Then one copy is
    destroyed by damage, which no history records: undo removes the other one,
    and redo brings back that one, as the undo saved it. A served player (a
    ServedGame, which has a steam_id) leaves out the damage."""
    surface = LARGE
    grid = surface.grid(game)
    game.cube_builder("MyObjectBuilder_InteriorLight/SmallLight")
    empty = snapshot(game, grid)
    last = game.last_node_id()
    node, new = place(game, surface, grid, surface.near, {"x": (surface.x, False)})
    assert node["label"] == "placed 2 blocks"
    one, other = sorted(new)
    ids = {c: game.block(c, grid)["entityId"] for c in new}
    game.api.set_custom_name(grid, one, "Renamed Copy")
    wait_until(
        lambda: game.block(one, grid)["customName"] == "Renamed Copy", "the name"
    )
    assert game.last_node_id() == last + 1

    assert game.undo() == "Undo: placed 2 blocks"
    wait_until(lambda: snapshot(game, grid) == empty, "both lights to go")
    assert game.redo() == "Redo: placed 2 blocks"
    wait_until(lambda: all(game.exists(c, grid) for c in new), "both lights back")
    assert {c: game.block(c, grid)["entityId"] for c in new} == ids
    assert game.block(one, grid)["customName"] == "Renamed Copy"

    # Destroyed: nothing records damage. A client of a server cannot damage a block
    # through Remote, the server never hears of it.
    if getattr(game, "steam_id", None):
        game.undo()
        wait_until(lambda: snapshot(game, grid) == empty, "no lights")
        return
    game.api.call([CallOp.slim_block_method(grid, other, "DoDamage", {"amount": 1e6})])
    wait_until(lambda: not game.exists(other, grid), "the copy to be destroyed")
    assert game.last_node_id() == last + 1
    assert game.undo().startswith("Undo: placed 2 blocks")
    wait_until(lambda: snapshot(game, grid) == empty, "the other light to go")
    assert game.redo().startswith("Redo: placed 2 blocks")
    wait_until(lambda: game.exists(one, grid), "the renamed light back")
    assert game.block(one, grid)["customName"] == "Renamed Copy"
    assert not game.exists(other, grid)
    game.undo()
    wait_until(lambda: snapshot(game, grid) == empty, "no lights")

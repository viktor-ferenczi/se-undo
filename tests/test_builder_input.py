"""What a player does with the cube builder in the hand, driven with real mouse and
keyboard input: placing and removing single blocks, a line of blocks, rotated blocks,
painting one block, repainting the whole grid.

The character stands on the free edge of the test station and looks along its floor.
The aim lands up to a block off at that distance, so each test reads which block is
under the crosshair and works out the cells from that.
"""

from __future__ import annotations

import re
import time

import pytest

import rig

from harness import AIM_TOLERANCE, wait_until

VANTAGE = (15, 0.6, 4)
# Points on the floor's top face, in station cells
NEAR = (11, 0.5, 4)
# The cube builder reaches about 12 m
FAR = (11, 0.5, 2)
SIDE = (11, 0.5, 6)
LINE_START = (9, 0.5, 4)
# Build color slot the tests repaint; the world's default armor color is another
PAINT_SLOT = 8
RED = (0.0, 0.6, 0.1)
BLUE = (0.6, 0.5, 0.1)


@pytest.fixture(scope="module", autouse=True)
def on_the_station_edge(game):
    game.stand_at(VANTAGE, tolerance=1.0)
    yield
    game.api.clear_input_state()
    game.api.key("D0")


@pytest.fixture(autouse=True)
def builder_in_hand(game):
    """Taken into the hand anew for every test. The same slot pressed twice would
    switch to the next block of its group, so the hand is emptied first."""
    game.api.clear_input_state()
    game.api.key("D0")
    time.sleep(0.3)
    game.cube_builder()


def place(game, point) -> tuple:
    """Left click on the floor; returns the cell of the new block. The block goes
    onto the face under the crosshair, which is the top of a floor block or the
    side of a block an earlier test step put there."""
    target = game.aim(point)
    assert target and target["gridId"] == game.station, target
    before = set(game.cubes())
    last = game.last_node_id()
    game.mouse("left")
    game.wait_recorded(last, "placed 1 block")
    (cell,) = set(game.cubes()) - before
    return cell


def aim_at_block(game, cell) -> None:
    def aimed():
        game.api.character_look_at(
            *rig.station_point((cell[0] + 0.2, cell[1], cell[2])),
            tolerance=AIM_TOLERANCE,
        )
        time.sleep(0.2)
        target = game.api.get_character_target(max_distance=100).get("block")
        return target and tuple(target["min"]) == tuple(cell)

    wait_until(aimed, f"the crosshair on {cell}", interval=0.1)


def test_left_click_places_then_undo_and_redo(game):
    cell = place(game, NEAR)

    assert game.undo() == "Undo: placed 1 block"
    wait_until(lambda: not game.exists(cell), "the block to go")

    assert game.redo() == "Redo: placed 1 block"
    wait_until(lambda: game.exists(cell), "the block to return")

    game.undo()
    wait_until(lambda: not game.exists(cell), "the block to go again")


def test_right_click_removes_then_undo_and_redo(game):
    cell = place(game, NEAR)
    placed = game.last_node_id()

    aim_at_block(game, cell)
    game.mouse("right")
    wait_until(lambda: not game.exists(cell), "the removal")
    game.wait_recorded(placed, "removed 1 block")

    assert game.undo() == "Undo: removed 1 block"
    wait_until(lambda: game.exists(cell), "the restore")
    assert game.redo() == "Redo: removed 1 block"
    wait_until(lambda: not game.exists(cell), "the removal again")

    # Back over both nodes: the block is there in between and gone at the end
    game.undo()
    wait_until(lambda: game.exists(cell), "the restore again")
    assert game.undo() == "Undo: placed 1 block"
    wait_until(lambda: not game.exists(cell), "the block to go")


def test_three_clicks_are_three_steps_undone_newest_first(game):
    first = game.last_node_id()
    cells = [place(game, point) for point in (NEAR, SIDE, FAR)]
    assert len(set(cells)) == 3
    assert game.last_node_id() == first + 3

    for newest in reversed(range(3)):
        game.undo()
        wait_until(lambda: not game.exists(cells[newest]), "the newest block to go")
        assert [game.exists(c) for c in cells] == [i < newest for i in range(3)]

    for oldest in range(3):
        game.redo()
        wait_until(lambda: game.exists(cells[oldest]), "the oldest block to return")
        assert [game.exists(c) for c in cells] == [i <= oldest for i in range(3)]

    for _ in cells:
        game.undo()
    wait_until(lambda: not any(game.exists(c) for c in cells), "all three to go")


def drag(game, button: str) -> None:
    """Ctrl and a mouse button held while the view moves: the cube builder's line
    mode. The view follows the mouse position of the held input state, counted
    from 0, 0; the look-at call would let the button go. Moving the mouse down
    brings the crosshair along the floor towards the character."""
    api = game.api
    held = {"keys": ["LeftControl"], f"mouse_{button}": True}
    api.set_input_state(mode="override", mouse_x=0, mouse_y=0, **held)
    time.sleep(0.4)
    for step in range(1, 5):
        api.set_input_state(mode="override", mouse_x=0, mouse_y=15 * step, **held)
        time.sleep(0.3)
    # The button goes first: the release ends the line, with Ctrl still down
    api.set_input_state(mode="override", keys=["LeftControl"], mouse_x=0, mouse_y=60)
    time.sleep(0.3)
    api.clear_input_state()


def test_line_of_blocks_is_one_step(game):
    """A line built with Ctrl and a drag is one request of the cube builder and one
    undo step, however many blocks it has"""
    before = set(game.cubes())
    last = game.last_node_id()
    assert game.aim(LINE_START)

    drag(game, "left")
    node = wait_until(
        lambda: game.last_node_id() > last and game.build()["nodes"][-1],
        "the line to be recorded",
    )
    count = int(re.fullmatch(r"placed (\d+) blocks", node["label"]).group(1))
    assert count >= 2
    line = set(game.cubes()) - before
    assert len(line) == count
    assert game.last_node_id() == last + 1

    assert game.undo() == f"Undo: {node['label']}"
    wait_until(lambda: set(game.cubes()) == before, "the whole line to go")

    assert game.redo() == f"Redo: {node['label']}"
    wait_until(lambda: set(game.cubes()) - before == line, "the same line to return")

    game.undo()
    wait_until(lambda: set(game.cubes()) == before, "the line to go again")


def test_rotated_block_keeps_its_orientation(game):
    """A slope turned with the rotation keys comes back the way it was placed"""
    api = game.api
    game.cube_builder("LargeBlockArmorSlope")
    try:
        assert game.aim(SIDE)
        api.key("PageDown")
        api.key("Delete")
        time.sleep(0.3)
        last = game.last_node_id()
        cell = place(game, SIDE)
        placed = game.cubes()[cell]
        assert placed["subtypeId"] == "LargeBlockArmorSlope"
        # Not the orientation every block of the injected station has
        assert (placed["forward"], placed["up"]) != ("Forward", "Up")

        game.undo()
        wait_until(lambda: not game.exists(cell), "the slope to go")
        game.redo()
        wait_until(lambda: game.exists(cell), "the slope to return")
        again = game.cubes()[cell]
        for key in ("subtypeId", "forward", "up", "colorMaskHsv"):
            assert again[key] == placed[key], key

        # Removed by hand and restored: the same again
        aim_at_block(game, cell)
        game.mouse("right")
        wait_until(lambda: not game.exists(cell), "the removal")
        game.wait_recorded(last + 1, "removed 1 block")
        game.undo()
        wait_until(lambda: game.exists(cell), "the restore")
        restored = game.cubes()[cell]
        for key in ("subtypeId", "forward", "up", "colorMaskHsv"):
            assert restored[key] == placed[key], key

        game.undo(expect="Undo: placed 1 block")
        wait_until(lambda: not game.exists(cell), "the slope to go again")
    finally:
        game.cube_builder()


def paint_color(game, hsv) -> None:
    api = game.api
    api.set_build_color(PAINT_SLOT, *hsv)
    settings = api.get_player_settings()
    assert settings["selectedBuildColorSlot"] == PAINT_SLOT, settings


def colors(game) -> dict:
    return {cell: game.color(cell) for cell in game.cubes()}


def same(a, b) -> bool:
    return all(abs(x - y) < 0.01 for x, y in zip(a, b))


def test_middle_click_paints_one_block(game):
    paint_color(game, RED)
    assert game.aim(NEAR)
    before = colors(game)
    last = game.last_node_id()

    # The paint goes to the block the builder's box is on, which is the one under
    # the crosshair or the one next to it
    game.mouse("middle")
    game.wait_recorded(last, "painted 1 block")
    after = colors(game)
    (cell,) = [c for c in after if after[c] != before[c]]
    original = before[cell]
    assert same(after[cell], RED) and not same(original, RED)

    # The same color again changes nothing and records nothing
    game.mouse("middle")
    time.sleep(1)
    assert game.last_node_id() == last + 1

    assert game.undo() == "Undo: painted 1 block"
    wait_until(lambda: same(game.color(cell), original), "the old color")
    assert game.redo() == "Redo: painted 1 block"
    wait_until(lambda: same(game.color(cell), RED), "the new color")
    game.undo()
    wait_until(lambda: same(game.color(cell), original), "the old color again")


def test_whole_grid_repaint_is_one_step_and_restores_every_color(game):
    """Ctrl-Shift and the middle button repaint the grid. One block has another
    color before that, and the undo gives each block its own color back."""
    paint_color(game, RED)
    assert game.aim(SIDE)
    plain = colors(game)
    last = game.last_node_id()
    game.mouse("middle")
    game.wait_recorded(last, "painted 1 block")
    mixed = colors(game)
    (odd,) = [c for c in mixed if mixed[c] != plain[c]]
    assert same(mixed[odd], RED)

    paint_color(game, BLUE)
    assert game.aim(NEAR)
    game.mouse("middle", keys=["LeftControl", "LeftShift"])
    node = game.wait_recorded(last + 1, f"painted {len(mixed)} blocks")
    assert all(same(c, BLUE) for c in colors(game).values())
    assert game.last_node_id() == last + 2

    assert game.undo() == f"Undo: {node['label']}"
    wait_until(lambda: colors(game) == mixed, "every block's own color")

    assert game.redo() == f"Redo: {node['label']}"
    wait_until(lambda: all(same(c, BLUE) for c in colors(game).values()), "the repaint")

    game.undo()
    game.undo(expect="Undo: painted 1 block")
    wait_until(lambda: not same(game.color(odd), RED), "the first paint to go")


def test_functional_block_comes_back_under_its_id(game):
    """Redo of a placement builds the block with the entity id it had"""
    game.cube_builder("MyObjectBuilder_InteriorLight/SmallLight")
    try:
        cell = place(game, SIDE)
        placed = game.block(cell)
        assert placed["definition"]["subtypeId"] == "SmallLight"

        game.undo()
        wait_until(lambda: not game.exists(cell), "the light to go")
        game.redo()
        wait_until(lambda: game.exists(cell), "the light to return")
        assert game.block(cell)["entityId"] == placed["entityId"]

        game.undo()
        wait_until(lambda: not game.exists(cell), "the light to go again")
    finally:
        game.cube_builder()

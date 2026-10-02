"""Corner cases of the build context: what the history does when the world changed
behind its back, and what a restored block or grid keeps.
"""

from __future__ import annotations

import time

import pytest

import rig
from harness import wait_until

FREE = [(x, 1, 2) for x in range(2, 12)]
EAST = (24, 1, 4)
SOUTH = (24, 1, 12)

RIG_NAME = "Undo Cargo Rig"
RIG_FLOOR = [(x, 0, z) for x in range(5) for z in range(3)]
SMALL_CARGO = (0, 1, 0)
LARGE_CARGO = (1, 1, 0)
ITEMS = {"SteelPlate": 7, "Motor": 3}


@pytest.fixture(scope="module", autouse=True)
def out_of_the_seat(game):
    game.stand_at((15, 0.6, 4), tolerance=1.0)


def integrity(game, cell, grid=None) -> float:
    return float(game.cubes(grid)[tuple(cell)]["integrity"])


def test_block_destroyed_by_damage_is_no_step(game):
    """Damage is not the player's edit. A block destroyed that way leaves no node,
    and the step that placed it still goes both ways."""
    cell = FREE[0]
    node = game.build_block(cell)
    damage = game.api.damage_block(game.station, cell, 1e7)
    assert damage["accepted"], damage
    wait_until(lambda: not game.exists(cell), "the block to be destroyed")
    time.sleep(1)
    assert game.last_node_id() == node["id"]
    # The debris of the block is in the way of a new one for a moment
    wait_until(
        lambda: game.api.character_build_block(game.station, cell, test_only=True)[
            "canPlaceBlock"
        ],
        "the cell to be free again",
    )

    # Nothing left to remove; the step is taken all the same
    assert game.undo() == "Undo: placed 1 block"
    assert game.redo() == "Redo: placed 1 block"
    wait_until(lambda: game.exists(cell), "the block built again by the redo")
    game.undo()
    wait_until(lambda: not game.exists(cell), "the block to go")


def test_damaged_block_comes_back_damaged(game):
    cell = rig.PAINT_LIGHT
    full = integrity(game, cell)
    game.api.damage_block(game.station, cell, full * 0.3)
    wait_until(lambda: integrity(game, cell) < full, "the damage")
    damaged = integrity(game, cell)
    before = game.block(cell)

    game.raze_block(cell)
    game.undo()
    wait_until(lambda: game.exists(cell), "the restore")
    assert integrity(game, cell) == pytest.approx(damaged, rel=0.01)
    assert game.block(cell)["entityId"] == before["entityId"]


def test_switched_off_block_comes_back_switched_off(game):
    api, cell = game.api, rig.SECOND_LIGHT
    api.set_enabled(game.station, cell, False)
    wait_until(lambda: not game.block(cell)["enabled"], "the switch")
    before = game.block(cell)

    game.raze_block(cell)
    game.undo()
    wait_until(lambda: game.exists(cell), "the restore")
    after = game.block(cell)
    for key in ("entityId", "enabled", "customName", "colorMask"):
        assert after[key] == before[key], key


def test_removal_that_leaves_four_parts(game):
    """The middle of a plus sign: three arms split off, one stays the grid"""
    name = "Undo Plus"
    arms = [(2, 0, 0), (-2, 0, 0), (0, 0, 2), (0, 0, -2)]
    cells = [(0, 0, 0)] + arms + [(x // 2, 0, z // 2) for x, _, z in arms]
    centre = (0, 0, 0)
    plus = game.paste(name, cells, EAST)
    grid = plus["entityId"]
    names = {g["name"] for g in game.grids()}

    def parts() -> int:
        return len({g["name"] for g in game.grids()} - names) + 1

    node = game.raze_block(centre, grid, label="removed 1 block, 3 parts split off")
    assert parts() == 4

    for step in ("undo", "redo", "undo"):
        getattr(game, step)(expect=f"{step.capitalize()}: {node['label']}")
        if step == "redo":
            wait_until(lambda: parts() == 4, "four parts again")
            continue
        wait_until(lambda: parts() == 1, "one grid again")
        assert set(game.cubes(grid)) == set(cells)

    game.undo(expect=f"Undo: pasted {name}")
    wait_until(lambda: not game.grids_named(name), "the plus to go")


def cargo_rig(game) -> int:
    blocks = rig.cargo_container(SMALL_CARGO, "LargeBlockSmallContainer", ITEMS)
    blocks += rig.cargo_container(LARGE_CARGO, "LargeBlockLargeContainer", {})
    return game.paste(RIG_NAME, RIG_FLOOR, SOUTH, blocks=blocks)["entityId"]


def items(game, grid: int, cell) -> dict:
    inventory = game.api.get_inventory(grid, cell)
    return {i["subtypeId"]: int(i["amountRaw"]) for i in inventory["items"]}


def test_deleted_grid_comes_back_with_the_items_in_its_cargo(game):
    grid = cargo_rig(game)
    expected = {name: amount * 1_000_000 for name, amount in ITEMS.items()}
    assert items(game, grid, SMALL_CARGO) == expected
    last = game.last_node_id()

    assert game.api.close_grid(grid)["closed"]
    wait_until(lambda: not game.grids_named(RIG_NAME), "the rig to go")
    game.wait_recorded(last, f"deleted {RIG_NAME}")
    game.undo()
    wait_until(lambda: game.grids_named(RIG_NAME), "the rig to come back")
    assert items(game, grid, SMALL_CARGO) == expected


def test_removed_cargo_container_comes_back_with_its_items(game):
    (rig_grid,) = game.grids_named(RIG_NAME)
    grid = rig_grid["entityId"]
    expected = items(game, grid, SMALL_CARGO)
    before = game.block(SMALL_CARGO, grid)

    game.raze_block(SMALL_CARGO, grid)
    game.undo()
    wait_until(lambda: game.exists(SMALL_CARGO, grid), "the container", timeout=5)
    assert game.block(SMALL_CARGO, grid)["entityId"] == before["entityId"]
    assert items(game, grid, SMALL_CARGO) == expected


def test_block_larger_than_one_cell(game):
    """A large cargo container takes 3x3x3 cells and is addressed by its min cell"""
    (rig_grid,) = game.grids_named(RIG_NAME)
    grid = rig_grid["entityId"]
    before = game.cubes(grid)[LARGE_CARGO]
    assert before["cellMax"] == [3, 3, 2]
    entity = game.block(LARGE_CARGO, grid)["entityId"]

    game.raze_block(LARGE_CARGO, grid)
    assert not game.exists((2, 2, 1), grid)
    game.undo()
    wait_until(lambda: game.exists(LARGE_CARGO, grid), "the container")
    after = game.cubes(grid)[LARGE_CARGO]
    assert (after["cellMin"], after["cellMax"]) == (
        before["cellMin"],
        before["cellMax"],
    )
    assert game.block(LARGE_CARGO, grid)["entityId"] == entity
    game.redo()
    wait_until(lambda: not game.exists(LARGE_CARGO, grid), "the removal again")
    game.undo()
    wait_until(lambda: game.exists((2, 2, 1), grid), "the container again")


def test_blueprint_of_two_grids_is_one_step(game):
    """A paste that creates two grids is undone and redone as a whole"""
    names = ["Undo Twin A", "Undo Twin B"]
    cells = [(0, 0, 0), (1, 0, 0)]
    _, forward, up = rig.station_frame()
    last = game.last_node_id()
    pasted = game.api.paste_blueprint(
        xml=rig.blueprint_grids_xml(
            [(names[0], cells + [(2, 0, 0)], (0, 0, 0)), (names[1], cells, (0, 10, 0))]
        ),
        position=rig.station_point((24, 4, -4)),
        forward=forward,
        up=up,
    )
    assert len(pasted) == 2
    node = game.wait_recorded(last, f"pasted {names[0]} and 1 more grid")
    ids = {g["name"]: g["entityId"] for g in pasted}

    def present() -> list[bool]:
        return [bool(game.grids_named(n)) for n in names]

    assert game.undo() == f"Undo: {node['label']}"
    wait_until(lambda: present() == [False, False], "both grids to go")
    assert game.redo() == f"Redo: {node['label']}"
    wait_until(lambda: present() == [True, True], "both grids to return")
    assert {n: game.grids_named(n)[0]["entityId"] for n in names} == ids
    game.undo()
    wait_until(lambda: present() == [False, False], "both grids to go again")


def test_two_pastes_of_one_blueprint_are_told_apart(game):
    name = "Undo Same Name"
    cells = [(0, 0, 0), (0, 1, 0)]
    first = game.paste(name, cells, (24, 1, 0))
    second = game.paste(name, cells, (24, 1, -3))
    assert first["entityId"] != second["entityId"]

    game.undo()
    wait_until(lambda: len(game.grids_named(name)) == 1, "the second paste to go")
    assert game.grids_named(name)[0]["entityId"] == first["entityId"]
    game.undo()
    wait_until(lambda: not game.grids_named(name), "the first paste to go")
    game.redo()
    wait_until(lambda: game.grids_named(name), "the first paste to return")
    assert game.grids_named(name)[0]["entityId"] == first["entityId"]
    game.undo()
    wait_until(lambda: not game.grids_named(name), "the first paste to go again")


def test_deleted_ship_comes_back_as_a_ship(game):
    """The drift ship is dynamic and out in space. Deleted and restored it is the
    same ship: its id, its blocks' ids, not a station."""
    ship = rig.DRIFT_SHIP_ID
    blocks = {p: game.block(p, ship)["entityId"] for p in rig.DRIFT_IDS}
    cells = set(game.cubes(ship))
    last = game.last_node_id()

    assert game.api.close_grid(ship)["closed"]
    wait_until(lambda: not game.grids_named(rig.DRIFT_SHIP_NAME), "the ship to go")
    game.wait_recorded(last, f"deleted {rig.DRIFT_SHIP_NAME}")

    game.undo()
    (back,) = wait_until(
        lambda: game.grids_named(rig.DRIFT_SHIP_NAME), "the ship to come back"
    )
    assert back["entityId"] == ship
    assert not back["isStatic"]
    assert set(game.cubes(ship)) == cells
    assert {p: game.block(p, ship)["entityId"] for p in blocks} == blocks


@pytest.mark.parametrize("key, screen", [("G", "toolbar"), ("Escape", "pause menu")])
def test_keys_are_ignored_under_another_screen(game, key, screen):
    """The build history answers on the bare gameplay screen only"""
    api, cell = game.api, FREE[1]
    game.build_block(cell)
    api.key(key)
    wait_until(
        lambda: len(api.list_screens()) > 2
        and api.list_screens()[-1].get("type")
        not in ("MyGuiScreenGamePlay", "MyGuiScreenHudSpace"),
        f"the {screen}",
    )
    time.sleep(1)  # the opening transition takes no input
    try:
        game.quiet(lambda: api.key(*game.undo_key))
        assert game.exists(cell)
    finally:
        rig.focus_gameplay(api)
        api.unpause()

    assert game.undo() == "Undo: placed 1 block"
    wait_until(lambda: not game.exists(cell), "the block to go")

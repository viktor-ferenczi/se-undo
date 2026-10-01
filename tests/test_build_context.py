"""Build context rows of design section 13, driven through the Remote plugin.

The tests share one world and run in file order; each leaves the station the way
it found it, except the limits test at the end.
"""

from __future__ import annotations

import time

import rig
from conftest import wait_until
from harness import links
from se_remote import CallOp


def test_build_then_undo_and_redo(game):
    cell = rig.BUILD_CELL
    assert not game.exists(cell)
    last = game.last_node_id()

    assert game.api.character_build_block(game.station, cell)["sent"]
    wait_until(lambda: game.exists(cell), "the block")
    game.wait_recorded(last, "placed 1 block")

    assert game.undo() == "Undo: placed 1 block"
    wait_until(lambda: not game.exists(cell), "the block to go")

    assert game.redo() == "Redo: placed 1 block"
    wait_until(lambda: game.exists(cell), "the block to return")

    game.undo()
    wait_until(lambda: not game.exists(cell), "the block to go again")


def test_displaced_vanilla_keys(game):
    """Ctrl-Z is relative dampeners in vanilla and plain Z toggles dampeners. While
    undo holds Ctrl-Z, neither fires on it; Ctrl-Shift-Z is relative dampeners."""
    api = game.api
    if api.get_character()["state"] == "sitting":
        api.key("F")
        wait_until(
            lambda: api.get_character()["state"] != "sitting", "leaving the seat"
        )

    def dampeners():
        return api.get_character()["dampeners"]

    def settle():
        time.sleep(0.5)
        return dampeners()

    before = dampeners()
    api.key("Z")
    wait_until(lambda: dampeners() != before, "plain Z to toggle dampeners")
    if dampeners():
        api.key("Z")
        wait_until(lambda: not dampeners(), "dampeners off")

    # Relative dampeners would switch them on; redo and undo leave them off. Redo
    # first, the build test left its node undone, so the pair changes nothing.
    game.redo(expect="Redo: ")
    game.undo(expect="Undo: ")
    assert settle() is False

    # The replacement switches them on, and plain Z does not toggle them back
    api.key("Z", ["LeftControl", "LeftShift"])
    wait_until(dampeners, "relative dampeners on Ctrl-Shift-Z")
    assert settle() is True


def test_raze_then_undo_keeps_the_block_detail(game):
    cell = rig.SECOND_LIGHT
    before = game.block(cell)
    last = game.last_node_id()

    game.api.character_grid_event(game.station, cell, "raze")
    wait_until(lambda: not game.exists(cell), "the removal")
    game.wait_recorded(last, "removed 1 block")

    assert game.undo() == "Undo: removed 1 block"
    wait_until(lambda: game.exists(cell), "the restore")
    after = game.block(cell)
    for key in (
        "entityId",
        "customName",
        "colorMask",
        "enabled",
        "isFunctional",
        "definition",
    ):
        assert after[key] == before[key], key

    game.redo()
    wait_until(lambda: not game.exists(cell), "the removal again")
    game.undo()
    wait_until(lambda: game.exists(cell), "the restore again")
    assert game.block(cell)["entityId"] == before["entityId"]


def test_raze_a_referenced_block_then_undo(game):
    cell = rig.TARGET
    target_id = rig.IDS[cell]
    assert links(game.saved_station()) == dict.fromkeys(
        ("toolbar", "turret", "event", "group"), True
    )
    last = game.last_node_id()

    game.api.character_grid_event(game.station, cell, "raze")
    wait_until(lambda: not game.exists(cell), "the removal")
    game.wait_recorded(last, "removed 1 block")

    # The block group and the event controller drop the block when it closes
    razed = links(game.saved_station())
    assert not razed["group"] and not razed["event"]

    game.undo()
    wait_until(lambda: game.exists(cell), "the restore")
    restored = game.block(cell)
    assert restored["entityId"] == target_id
    assert restored["customName"] == rig.TARGET_NAME
    # Give the event controller its 10 frame update before saving
    time.sleep(1)
    assert links(game.saved_station()) == dict.fromkeys(
        ("toolbar", "turret", "event", "group"), True
    )


def _grid_names(game) -> set[str]:
    return {g["name"] for g in game.grids()}


def test_raze_that_splits_then_undo(game):
    light = rig.IDS[rig.PART_B_LIGHT]
    grids_before = _grid_names(game)
    last = game.last_node_id()

    game.api.character_grid_event(game.station, rig.BRIDGE, "raze")
    game.wait_recorded(last, "removed 1 block, 1 part split off")
    assert len(_grid_names(game)) == len(grids_before) + 1
    assert game.block(rig.PART_B_LIGHT) is None

    for step in ("undo", "redo", "undo"):
        getattr(game, step)()
        if step == "undo":
            wait_until(lambda: game.exists(rig.BRIDGE), "the bridge")
            assert _grid_names(game) == grids_before
            assert all(game.exists(p) for p in rig.PART_B)
            block = game.block(rig.PART_B_LIGHT)
            assert (block["entityId"], block["gridId"]) == (light, game.station)
        else:
            wait_until(
                lambda: len(_grid_names(game)) == len(grids_before) + 1, "the split"
            )

    # The cockpit on the main part still points at the light on part B
    slots = [e.text for e in game.saved_station().iter("BlockEntityId")]
    assert str(light) in slots


def test_raze_that_splits_a_drifting_part_then_undo(game):
    ship = rig.DRIFT_SHIP_ID
    before = {
        p: game.block(p, ship)["entityId"]
        for p in (rig.DRIFT_THRUSTER, rig.DRIFT_BATTERY, rig.DRIFT_LIGHT)
    }
    grids_before = _grid_names(game)
    last = game.last_node_id()

    # The thruster pushes the part away as soon as it is off the ship
    game.api.set_enabled(ship, rig.DRIFT_THRUSTER, True)
    try:
        game.api.character_grid_event(ship, rig.DRIFT_BRIDGE, "raze")
        game.wait_recorded(last, "removed 1 block, 1 part split off")
        piece = next(g for g in game.grids() if g["name"] not in grids_before)[
            "entityId"
        ]
        time.sleep(1.5)

        def world(grid):
            call = CallOp.grid_method(
                grid, "GridIntegerToWorld", {"pos": list(rig.DRIFT_LIGHT)}
            )
            return game.api.call([call]).call(0)["world"]

        drift = sum((a - b) ** 2 for a, b in zip(world(piece), world(ship))) ** 0.5
        assert drift > 5, f"the part drifted only {drift:.1f} m"

        game.undo()
        wait_until(lambda: game.exists(rig.DRIFT_BRIDGE, ship), "the bridge")
        assert _grid_names(game) == grids_before
        for pos, entity_id in before.items():
            block = game.block(pos, ship)
            assert (block["entityId"], block["gridId"]) == (entity_id, ship)
    finally:
        game.api.set_enabled(ship, rig.DRIFT_THRUSTER, False)


def test_paint_then_undo_and_redo(game):
    cell = rig.PAINT_LIGHT
    original = game.block(cell)["colorMask"]
    last = game.last_node_id()

    # The Remote paint event uses one fixed color
    game.api.character_grid_event(game.station, cell, "color")
    painted = wait_until(
        lambda: (c := game.block(cell)["colorMask"]) != original and c, "the paint"
    )
    game.wait_recorded(last, "painted 1 block")

    game.undo()
    wait_until(lambda: game.block(cell)["colorMask"] == original, "the old color")
    game.redo()
    wait_until(lambda: game.block(cell)["colorMask"] == painted, "the new color")
    game.undo()
    wait_until(lambda: game.block(cell)["colorMask"] == original, "the old color again")


def test_tree_keeps_the_abandoned_branch(game):
    a, b, c = rig.TREE_CELLS
    last = game.last_node_id()

    game.api.character_build_block(game.station, a)
    node_a = game.wait_recorded(last, "placed 1 block")
    game.api.character_build_block(game.station, b)
    node_b = game.wait_recorded(node_a["id"], "placed 1 block")
    game.undo()
    game.undo()
    wait_until(lambda: not game.exists(a) and not game.exists(b), "both undone")

    game.api.character_build_block(game.station, c)
    node_c = game.wait_recorded(node_b["id"], "placed 1 block")

    nodes = {n["id"]: n for n in game.build()["nodes"]}
    assert node_a["id"] in nodes and node_b["id"] in nodes
    assert nodes[node_a["id"]]["parent"] == nodes[node_c["id"]]["parent"]
    assert nodes[node_b["id"]]["parent"] == node_a["id"]

    # Redo follows the branch visited last
    game.undo()
    wait_until(lambda: not game.exists(c), "c undone")
    game.redo()
    wait_until(lambda: game.exists(c), "c redone")
    assert not game.exists(a)
    assert game.build()["current"] == node_c["id"]


def test_terminal_context_has_its_own_history(game):
    cell = rig.BUILD_CELL
    last = game.last_node_id()
    game.api.character_build_block(game.station, cell)
    game.wait_recorded(last, "placed 1 block")

    game.api.key("K")
    terminal = wait_until(
        lambda: next(
            (
                i
                for i, s in enumerate(game.api.list_screens())
                if s.get("type") == "MyGuiScreenTerminal" and s.get("hasFocus")
            ),
            None,
        )
        is not None,
        "the terminal",
    )
    assert terminal
    # Screens take no input while their opening transition runs
    time.sleep(1)
    try:
        assert game.undo(expect=None) == "Nothing to undo"
        assert game.exists(cell)
    finally:
        rig.focus_gameplay(game.api)

    assert game.undo() == "Undo: placed 1 block"
    wait_until(lambda: not game.exists(cell), "the block to go")


def test_limits_drop_the_oldest_nodes(game):
    first_new = game.last_node_id() + 1
    for cell in rig.LIMITS_AREA:
        game.api.character_build_block(game.station, cell)
    wait_until(
        lambda: game.last_node_id() >= first_new + len(rig.LIMITS_AREA) - 1,
        "all builds recorded",
        timeout=60,
    )

    build = game.build()
    assert build["count"] == 200
    ids = [n["id"] for n in build["nodes"]]
    new = [i for i in ids if i >= first_new]
    # All older nodes went first, then the first ten of this run
    assert new == list(range(first_new + 10, first_new + 210))
    assert len(ids) == len(new)

"""A removed block comes back as the block it was: its settings, its inventory, and
everything other blocks hold on to it. The links rig (rig.links_xml) is a station
whose blocks point at each other through toolbars, block lists, bound cameras and
block groups.

The check is the same for every block: save the world, remove the block, undo, save
again. The rig's part of the sector file has to be the same as before. A saved world
is all a block has, so nothing a player could have set is left out.
"""

from __future__ import annotations

import difflib
import re
import time
import xml.dom.minidom
import xml.etree.ElementTree as ET

import pytest

import rig
from harness import wait_until

GRID = rig.LINKS_ID


@pytest.fixture(scope="module", autouse=True)
def out_of_the_seat(game):
    game.leave_seat()
    game.api.set_custom_data(GRID, rig.LINKS["program"], "some custom data")


def picture(game, grid: int = GRID) -> dict[str, str]:
    """The grid as the world save has it: one text per block, keyed by its cell,
    plus the block groups and what the grid itself has. The order of the blocks in
    the file is left out, a restored block is added at the end."""
    saved = game.saved_grid(grid)
    parts = {}
    blocks = saved.find("CubeBlocks")
    for block in list(blocks):
        cell = block.find("Min")
        key = "block " + (
            ",".join(cell.get(axis) for axis in "xyz") if cell is not None else "0,0,0"
        )
        parts[key] = pretty(normalized(block))
        blocks.remove(block)
    groups = saved.find("BlockGroups")
    if groups is not None:
        for group in list(groups):
            members = sorted(pretty(v) for v in group.iter("Vector3I"))
            parts["group " + group.findtext("Name")] = "\n".join(members)
        saved.remove(groups)
    parts["grid"] = pretty(saved)
    return parts


# What changes in a block by itself: frame counters of the LCD surfaces and of the
# offensive combat block, and the charge of the battery that feeds the rig
VOLATILE = ("UpdateStartOfFirstTexture", "CurrentStoredPower", "RunAwayStartedFrame")


def normalized(block: ET.Element) -> ET.Element:
    """A block's components in a fixed order. The game writes them in the order
    they were added to the block, which is not the same for a block built from a
    builder as for one loaded with its grid; and it names the mod storage by the
    type it was added as, the class or its base, which reads back the same."""
    components = block.find("ComponentContainer/Components")
    if components is not None:
        for type_id in components.iter("TypeId"):
            if type_id.text == "MyModStorageComponent":
                type_id.text = "MyModStorageComponentBase"
        components[:] = sorted(components, key=lambda c: c.findtext("TypeId") or "")
    return block


def pretty(element: ET.Element) -> str:
    text = xml.dom.minidom.parseString(ET.tostring(element)).toprettyxml(indent=" ")
    # A grid created again from a builder has the last digit of its orientation
    # rounded differently
    text = re.sub(r"(-?\d+\.\d{5})\d+", r"\1", text)
    return "\n".join(
        line
        for line in text.splitlines()[1:]
        if line.strip()
        and not line.strip().startswith(tuple(f"<{v}>" for v in VOLATILE))
    )


def assert_same(before: dict, after: dict) -> None:
    problems = []
    for key in sorted(set(before) | set(after)):
        old, new = before.get(key, ""), after.get(key, "")
        if old != new:
            diff = difflib.unified_diff(
                old.splitlines(), new.splitlines(), "before", "after", lineterm="", n=1
            )
            problems.append(f"--- {key}\n" + "\n".join(diff))
    assert not problems, "\n".join(problems)


def items_of(game, cell) -> dict:
    inventory = game.api.get_inventory(GRID, cell)["items"]
    return {i["subtypeId"]: int(i["amountRaw"]) for i in inventory}


def test_the_rig_loaded_as_written(game):
    """Guards the other tests: every link of the rig is in the saved world, so a
    test that finds it again after an undo has found something"""
    saved = pretty(game.saved_grid(GRID))
    ids = rig.LINKS_IDS
    for name in ("light", "light2", "camera", "timer"):
        assert saved.count(str(ids[name])) >= 2, name
    assert f"<BindedCamera>{ids['camera']}</BindedCamera>" in saved
    assert f"<CameraId>{ids['camera']}</CameraId>" in saved
    assert "<Value>Lamp</Value>" in saved
    assert rig.LINKS_LCD_TEXT in saved and "<FontSize>2.5</FontSize>" in saved
    assert "some custom data" in saved and "Echo(Storage)" in saved
    assert len(game.cubes(GRID)) == len(rig.LINKS_FLOOR) + len(rig.LINKS)
    cargo = game.api.get_inventory(GRID, rig.LINKS["cargo"])["items"]
    assert {i["subtypeId"] for i in cargo} == set(rig.LINKS_ITEMS)


@pytest.mark.parametrize("name", rig.LINKS)
def test_removed_block_comes_back_as_it_was(game, name):
    cell = rig.LINKS[name]
    before = picture(game)

    node = game.raze_block(cell, GRID)
    time.sleep(1)
    assert game.undo() == f"Undo: {node['label']}"
    wait_until(lambda: game.exists(cell, GRID), "the block")
    time.sleep(1)  # controllers pick their blocks up again within some frames

    assert_same(before, picture(game))
    if name == "cargo":
        assert items_of(game, cell) == {
            item: amount * 1_000_000 for item, amount in rig.LINKS_ITEMS.items()
        }
    if name == "program":
        assert game.block(cell, GRID)["customData"] == "some custom data"


def test_undo_redo_undo_of_a_removal_changes_nothing(game):
    cell = rig.LINKS["light"]
    before = picture(game)
    game.raze_block(cell, GRID)
    for step, there in (("undo", True), ("redo", False), ("undo", True)):
        getattr(game, step)()
        wait_until(lambda: game.exists(cell, GRID) is there, f"the block after {step}")
    time.sleep(1)
    assert_same(before, picture(game))


def test_toolbar_slot_works_on_the_restored_block(game):
    """Not only stored: the timer's first slot switches the light, also after the
    light was removed and restored, and after the timer was"""
    api = game.api
    light, timer = rig.LINKS["light"], rig.LINKS["timer"]

    def trigger_switches_the_light() -> bool:
        before = game.block(light, GRID)["enabled"]
        other = game.block(rig.LINKS["light2"], GRID)["enabled"]
        api.apply_action(GRID, timer, "TriggerNow")
        try:
            wait_until(
                lambda: game.block(light, GRID)["enabled"] is not before,
                "the light to switch",
                timeout=5,
            )
        except AssertionError:
            return False
        # Slot 2 switches the other light; put both back
        wait_until(
            lambda: game.block(rig.LINKS["light2"], GRID)["enabled"] is not other,
            "the other light to switch",
        )
        api.apply_action(GRID, timer, "TriggerNow")
        wait_until(
            lambda: game.block(light, GRID)["enabled"] is before,
            "the light to switch back",
        )
        return True

    assert trigger_switches_the_light()
    for cell in (light, timer):
        game.raze_block(cell, GRID)
        game.undo()
        wait_until(lambda: game.exists(cell, GRID), "the block")
        time.sleep(1)
        assert trigger_switches_the_light(), cell


def test_deleted_grid_comes_back_as_it_was(game):
    before = picture(game)
    last = game.last_node_id()
    assert game.api.close_grid(GRID)["closed"]
    wait_until(lambda: not game.grids_named(rig.LINKS_NAME), "the rig to go")
    game.wait_recorded(last, f"deleted {rig.LINKS_NAME}")

    game.undo()
    wait_until(lambda: game.grids_named(rig.LINKS_NAME), "the rig to come back")
    time.sleep(1)
    assert_same(before, picture(game))


TIMER_AT = (2, 1, 1)
CARGO_AT = (5, 1, 1)


def test_redo_of_a_placement_brings_back_what_was_set_since(game):
    """A timer is placed, renamed and its delay changed. Undo removes it; redo
    puts back that timer, not a new one from the definition."""
    api = game.api
    game.build_block(TIMER_AT, GRID, subtype="TimerBlockLarge")
    api.set_custom_name(GRID, TIMER_AT, "Placed And Renamed")
    api.set_property(GRID, TIMER_AT, "TriggerDelay", 42.0)
    time.sleep(1)
    before = picture(game)
    entity = game.block(TIMER_AT, GRID)["entityId"]

    assert game.undo() == "Undo: placed 1 block"
    wait_until(lambda: not game.exists(TIMER_AT, GRID), "the timer to go")
    assert game.redo() == "Redo: placed 1 block"
    wait_until(lambda: game.exists(TIMER_AT, GRID), "the timer to return")
    time.sleep(1)

    block = game.block(TIMER_AT, GRID)
    assert (block["entityId"], block["customName"]) == (entity, "Placed And Renamed")
    assert_same(before, picture(game))

    game.undo()
    wait_until(lambda: not game.exists(TIMER_AT, GRID), "the light to go again")


def test_redo_of_a_placement_brings_back_what_was_put_in(game):
    """A container is placed and filled from the rig's cargo. Undo takes it away
    with its items, nothing is left lying on the floor; redo brings both back."""
    api = game.api
    source = rig.LINKS["cargo"]
    game.build_block(CARGO_AT, GRID, subtype="LargeBlockSmallContainer")
    plates = next(
        i
        for i in api.get_inventory(GRID, source)["items"]
        if i["subtypeId"] == "SteelPlate"
    )
    api.transfer_inventory(GRID, source, GRID, CARGO_AT, plates["itemId"], 4_000_000)
    wait_until(
        lambda: items_of(game, CARGO_AT) == {"SteelPlate": 4_000_000}, "the transfer"
    )

    assert game.undo() == "Undo: placed 1 block"
    wait_until(lambda: not game.exists(CARGO_AT, GRID), "the container to go")
    time.sleep(1)
    # Nothing spilled into the cell: a block can be placed there
    assert api.character_build_block(GRID, CARGO_AT, test_only=True)["canPlaceBlock"]

    assert game.redo() == "Redo: placed 1 block"
    wait_until(lambda: game.exists(CARGO_AT, GRID), "the container to return")
    assert items_of(game, CARGO_AT) == {"SteelPlate": 4_000_000}

    game.undo()
    wait_until(lambda: not game.exists(CARGO_AT, GRID), "the container to go again")


def test_removed_container_can_be_removed_and_restored_again(game):
    """The items the first removal dropped are taken away by its undo, and the redo
    drops none, so the same step goes back and forth"""
    cell = rig.LINKS["cargo"]
    held = items_of(game, cell)
    assert held
    node = game.raze_block(cell, GRID)
    time.sleep(1)
    assert not game.api.character_build_block(GRID, cell, test_only=True)[
        "canPlaceBlock"
    ]

    for _ in range(2):
        assert game.undo() == f"Undo: {node['label']}"
        wait_until(lambda: game.exists(cell, GRID), "the container")
        assert items_of(game, cell) == held
        assert game.redo() == f"Redo: {node['label']}"
        wait_until(lambda: not game.exists(cell, GRID), "the removal again")
        time.sleep(0.5)
        assert game.api.character_build_block(GRID, cell, test_only=True)[
            "canPlaceBlock"
        ]
    game.undo()
    wait_until(lambda: game.exists(cell, GRID), "the container at the end")
    assert items_of(game, cell) == held


def test_restore_is_refused_while_something_is_in_the_way(game):
    """The character stands where the removed block was. The game would refuse the
    block without a word; the undo says so and stays undone, and works once the
    cell is free."""
    api, cell = game.api, rig.LINKS["light2"]
    # The rig floats 60 m above the test station, on the same axes
    up = rig.station_frame()[2]
    point = [p + 60 * u for p, u in zip(rig.station_point(cell), up)]
    node = game.raze_block(cell, GRID)
    here = api.get_character()["position"]
    api.character_teleport(*point)
    time.sleep(1)
    try:
        current = game.build()["current"]
        assert game.undo(expect=None) == "Undo not available: something is in the way"
        assert game.build()["current"] == current
        assert not game.exists(cell, GRID)
    finally:
        api.character_teleport(*here)
        time.sleep(1)
    assert game.undo() == f"Undo: {node['label']}"
    wait_until(lambda: game.exists(cell, GRID), "the light")

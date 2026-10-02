"""Edge cases of the history saved with the world: both histories and what their
steps need come back after a load, a broken or foreign history file is an empty
history, and steps whose grid is not in the loaded world are refused.
"""

from __future__ import annotations

import gzip
import re

import pytest

import rig
from harness import wait_until
from test_world_save import FILE, document, save, shape

STAND = (2, 1.0, 14)
AIM = (2, 1.3, 15)
WALL = [(0, y, z) for y in (-1, 0, 1) for z in (-1, 0, 1)]
LOG = rig.APPDATA / "SpaceEngineers.log"


def load(game) -> None:
    """Loads the test world from its folder as it is on disk"""
    api = game.api
    rig.focus_gameplay(api)
    rig.load_world(api)
    rig.ensure_character(api)
    rig.focus_gameplay(api)
    api.unpause()


@pytest.fixture(scope="module", autouse=True)
def out_of_the_seat(game):
    game.leave_seat()


def radius(game) -> float:
    return game.api.get_property(game.station, rig.TARGET, "Radius")["value"]


def test_both_histories_and_a_removed_block_come_back(game):
    api, light = game.api, rig.SECOND_LIGHT
    before = game.block(light)
    old_radius = radius(game)
    api.set_property(game.station, rig.TARGET, "Radius", 11.0)
    game.wait_recorded(0, f"changed Radius of {rig.TARGET_NAME}", history="terminal")
    game.raze_block(light)
    shapes = shape(game.build()), shape(game.terminal())

    world = save(game)
    saved = document(world / FILE)
    assert len(saved.findall("Terminal/Nodes/Node")) == 2  # with the root
    load(game)
    assert (shape(game.build()), shape(game.terminal())) == shapes
    assert not game.exists(light)

    # The removed block is in the history file, with all it had
    assert game.undo() == "Undo: removed 1 block"
    wait_until(lambda: game.exists(light), "the light")
    after = game.block(light)
    assert (after["entityId"], after["customName"]) == (
        before["entityId"],
        before["customName"],
    )

    game.stand_at(STAND)
    with game.open_terminal(AIM):
        assert game.undo() == f"Undo: changed Radius of {rig.TARGET_NAME}"
    assert radius(game) == pytest.approx(old_radius)


def test_redo_of_a_paste_after_a_load_reads_the_grid_store(game):
    name = "Undo Stored Wall"
    wall = game.paste(name, WALL, (24, 1, 4))
    game.undo()
    wait_until(lambda: not game.grids_named(name), "the wall to go")

    save(game)
    load(game)
    assert not game.grids_named(name)
    assert game.redo() == f"Redo: pasted {name}"
    (again,) = wait_until(lambda: game.grids_named(name), "the wall")
    assert again["entityId"] == wall["entityId"]
    assert set(game.cubes(again["entityId"])) == set(WALL)
    game.undo()
    wait_until(lambda: not game.grids_named(name), "the wall to go again")


def log_has(text: str) -> bool:
    return text in LOG.read_text(encoding="utf-8", errors="replace")


def test_corrupt_history_file_is_an_empty_history(game):
    world = save(game)
    assert game.build()["count"] > 0
    (world / FILE).write_bytes(b"this is not a gzip stream")

    load(game)
    assert game.build()["count"] == 0 and game.terminal()["count"] == 0
    assert log_has("Loading the history failed")
    game.nothing_to_undo()

    # The plugin works on, and the next save writes a good file
    game.build_block(rig.BUILD_CELL)
    save(game)
    assert [n.findtext("Label") for n in document(world / FILE).iter("Node")][-1] == (
        "placed 1 block"
    )
    game.undo()
    wait_until(lambda: not game.exists(rig.BUILD_CELL), "the block to go")


def test_history_file_of_another_format_version_is_an_empty_history(game):
    game.build_block(rig.BUILD_CELL)
    world = save(game)
    with gzip.open(world / FILE, "rt", encoding="utf-8") as file:
        text = file.read()
    changed, count = re.subn(r"<Version>\d+</Version>", "<Version>999</Version>", text)
    assert count == 1
    with gzip.open(world / FILE, "wt", encoding="utf-8") as file:
        file.write(changed)

    load(game)
    assert game.build()["count"] == 0
    assert log_has("has another format version")
    # The block of the lost step is part of the world now
    assert game.exists(rig.BUILD_CELL)
    game.nothing_to_undo()


def test_history_belongs_to_its_world(game):
    """Another world starts with its own, empty history; coming back, the first
    world has the history of its last save"""
    api = game.api
    game.build_block(rig.TREE_CELLS[0])
    save(game)
    mine = shape(game.build())

    other = rig.prepare_world(rig.SURVIVAL_WORLD, mode="Creative")
    rig.load_world(api, other)
    rig.ensure_character(api)
    rig.focus_gameplay(api)
    api.unpause()
    assert game.status()["histories"]["build"]["count"] == 0
    game.nothing_to_undo()
    assert not game.exists(rig.TREE_CELLS[0])
    # A step here must not show up over there
    game.build_block(rig.TREE_CELLS[1])

    load(game)
    assert shape(game.build()) == mine
    assert not game.exists(rig.TREE_CELLS[1])
    assert game.undo() == "Undo: placed 1 block"
    wait_until(lambda: not game.exists(rig.TREE_CELLS[0]), "the block to go")


def test_steps_of_a_grid_missing_from_the_loaded_world_are_refused(game):
    """The world file is edited behind the plugin's back: the pasted wall is cut
    out of the sector. The steps that need it say so and stay in the history."""
    name = "Undo Vanishing Wall"
    extra = (1, 0, 0)
    wall = game.paste(name, WALL, (24, 1, 4))
    game.build_block(extra, wall["entityId"])
    count = game.build()["count"]
    world = save(game)

    sector = world / "SANDBOX_0_0_0_.sbs"
    text = sector.read_text(encoding="utf-8")
    start = text.index(f"<EntityId>{wall['entityId']}</EntityId>")
    start = text.rindex("<MyObjectBuilder_EntityBase", 0, start)
    end = text.index("</MyObjectBuilder_EntityBase>", start)
    end += len("</MyObjectBuilder_EntityBase>")
    sector.write_text(text[:start] + text[end:], encoding="utf-8")
    (world / "SANDBOX_0_0_0_.sbsB5").unlink(missing_ok=True)

    load(game)
    assert not game.grids_named(name)
    build = game.build()
    assert build["count"] == count
    current = build["current"]

    message = game.undo(expect=None)
    assert message.startswith("Undo not available: "), message
    assert game.build()["current"] == current
    assert game.redo(expect=None) == "Nothing to redo"

"""How the history itself behaves in game with the plugin's default, linear, model:
the redo branch is dropped by a new action, both ends answer with "nothing to",
fast key presses are not lost, steps of different kinds come back in order, and a
chain of steps survives the grid it worked on being deleted and re-created.
"""

from __future__ import annotations

import time

import rig
from harness import wait_until

# The rig turns the tree on for the other files
UNDO_CONFIG = {"UndoTree": None}

ROW = [(x, 1, 2) for x in range(2, 12)]
WALL = [(0, y, z) for y in (-1, 0, 1) for z in (-1, 0, 1)]
WALL_AT = (24, 1, 4)


def test_undo_and_redo_while_seated(game):
    """The world starts with the character in a cryo chamber. The keys work from a
    seat too, where vanilla Ctrl-Z would switch the ship's relative dampeners."""
    assert game.api.get_character()["state"] == "sitting"
    cell = ROW[0]
    game.build_block(cell)

    assert game.undo() == "Undo: placed 1 block"
    wait_until(lambda: not game.exists(cell), "the block to go")
    assert game.redo() == "Redo: placed 1 block"
    wait_until(lambda: game.exists(cell), "the block to return")
    game.undo()
    wait_until(lambda: not game.exists(cell), "the block to go again")
    assert game.api.get_character()["state"] == "sitting"


def test_both_ends_of_the_history(game):
    game.leave_seat()
    # The seated test left one undone node
    game.redo()
    assert game.redo(expect=None) == "Nothing to redo"
    game.undo()
    game.nothing_to_undo()
    build = game.build()
    assert (build["count"], build["current"]) == (1, 0)
    assert not game.exists(ROW[0])


def test_undo_key_with_nothing_to_undo_is_the_vanilla_key(game):
    """A player who is flying, not building: with no step to take back Ctrl-Z is
    relative dampeners, as in the vanilla game, and the plugin says nothing. With a
    step to take back the same key is undo and leaves the dampeners alone."""
    api = game.api

    def dampeners():
        return api.get_character()["dampeners"]

    def off():
        if dampeners():
            api.key("Z")
            wait_until(lambda: not dampeners(), "dampeners off")

    off()
    assert game.build()["current"] == 0
    game.quiet(lambda: api.key(*game.undo_key))
    wait_until(dampeners, "relative dampeners on Ctrl-Z")

    off()
    game.redo()
    assert game.undo() == "Undo: placed 1 block"
    time.sleep(0.5)
    assert dampeners() is False


def test_new_action_drops_the_redo_branch(game):
    a, b, c = ROW[1:4]
    first = game.last_node_id()
    node_a = game.build_block(a)
    node_b = game.build_block(b)
    game.undo()
    wait_until(lambda: not game.exists(b), "b undone")

    node_c = game.build_block(c)
    ids = [n["id"] for n in game.build()["nodes"] if n["id"] > first]
    assert ids == [node_a["id"], node_c["id"]], "b and the seated test's node are gone"
    assert node_b["id"] not in ids
    assert game.build()["nodes"][-1]["parent"] == node_a["id"]

    assert game.redo(expect=None) == "Nothing to redo"
    assert not game.exists(b)

    # What is left is a straight line: c, then a
    game.undo()
    wait_until(lambda: not game.exists(c), "c undone")
    game.undo()
    wait_until(lambda: not game.exists(a), "a undone")
    game.nothing_to_undo()


def test_fast_key_presses_are_all_taken(game):
    """Five presses with nothing waited for in between take five steps"""
    cells = ROW[4:9]
    bottom = game.build()["current"]
    for cell in cells:
        game.build_block(cell)
    top = game.build()["current"]

    for _ in cells:
        game.api.key(*game.undo_key)
    wait_until(lambda: not any(game.exists(c) for c in cells), "all five to go")
    assert game.build()["current"] == bottom

    for _ in cells:
        game.api.key(*game.redo_key)
    wait_until(lambda: all(game.exists(c) for c in cells), "all five to return")
    assert game.build()["current"] == top

    for _ in cells:
        game.undo()
    wait_until(lambda: not any(game.exists(c) for c in cells), "all five to go again")


def test_each_step_shows_a_hud_notification(game):
    api = game.api
    latest = api.get_hud_notifications()["latest"]
    cell = ROW[9]
    game.build_block(cell)
    game.undo()
    game.redo()
    game.undo()

    def texts():
        new = api.get_hud_notifications(after=latest)["notifications"]
        return [n["text"] for n in new if "ndo" in n["text"] or "edo" in n["text"]]

    wait_until(lambda: len(texts()) >= 3, "the notifications")
    assert texts() == [
        "Undo: placed 1 block",
        "Redo: placed 1 block",
        "Undo: placed 1 block",
    ]


def test_steps_of_different_kinds_come_back_in_order(game):
    """Place, paint, remove another block, paste a grid: undone newest first, each
    step leaves exactly the state before it, and redo walks the same way back"""
    cell, light = ROW[0], rig.SECOND_LIGHT
    name = "Undo Mixed Steps"
    start = game.build()["current"]
    original = game.color(rig.PAINT_LIGHT)

    def state():
        return (
            game.exists(cell),
            game.color(rig.PAINT_LIGHT) != original,
            game.exists(light),
            bool(game.grids_named(name)),
        )

    assert state() == (False, False, True, False)
    game.build_block(cell)
    last = game.last_node_id()
    game.api.character_grid_event(game.station, rig.PAINT_LIGHT, "color")
    game.wait_recorded(last, "painted 1 block")
    game.raze_block(light)
    game.paste(name, WALL, WALL_AT)
    done = (True, True, False, True)
    assert state() == done

    steps = [
        (f"pasted {name}", (True, True, False, False)),
        ("removed 1 block", (True, True, True, False)),
        ("painted 1 block", (True, False, True, False)),
        ("placed 1 block", (False, False, True, False)),
    ]
    for label, expected in steps:
        assert game.undo() == f"Undo: {label}"
        wait_until(lambda: state() == expected, f"the state before {label}")
    assert game.build()["current"] == start

    after = [expected for _, expected in reversed(steps)][1:] + [done]
    for (label, _), expected in zip(reversed(steps), after):
        assert game.redo() == f"Redo: {label}"
        wait_until(lambda: state() == expected, f"the state after {label}")

    for _ in steps:
        game.undo()
    wait_until(lambda: state() == (False, False, True, False), "the start again")


def test_steps_on_a_grid_survive_its_delete_and_undo(game):
    """Paste a wall, add a block to it, paint that block, delete the wall. Undo
    brings the wall back with the block and its paint, then takes the steps back
    one by one; redo builds on the wall the redo of the paste created."""
    name = "Undo Chain Wall"
    extra = (1, 0, 0)
    wall = game.paste(name, WALL, WALL_AT)
    grid = wall["entityId"]
    game.build_block(extra, grid)
    unpainted = game.color(extra, grid)
    last = game.last_node_id()
    game.api.character_grid_event(grid, extra, "color")
    game.wait_recorded(last, "painted 1 block")
    painted = game.color(extra, grid)
    assert painted != unpainted

    assert game.api.close_grid(grid)["closed"]
    wait_until(lambda: not game.grids_named(name), "the wall to go")
    game.wait_recorded(last + 1, f"deleted {name}")

    assert game.undo() == f"Undo: deleted {name}"
    (back,) = wait_until(lambda: game.grids_named(name), "the wall to come back")
    assert back["entityId"] == grid
    assert game.color(extra, grid) == painted

    assert game.undo() == "Undo: painted 1 block"
    wait_until(lambda: game.color(extra, grid) == unpainted, "the paint to go")
    assert game.undo() == "Undo: placed 1 block"
    wait_until(lambda: not game.exists(extra, grid), "the extra block to go")
    assert game.undo() == f"Undo: pasted {name}"
    wait_until(lambda: not game.grids_named(name), "the wall to go again")

    # All the way forward again, on a wall created by the redo
    assert game.redo() == f"Redo: pasted {name}"
    wait_until(lambda: game.grids_named(name), "the wall")
    assert game.redo() == "Redo: placed 1 block"
    wait_until(lambda: game.exists(extra, grid), "the extra block")
    assert game.redo() == "Redo: painted 1 block"
    wait_until(lambda: game.color(extra, grid) == painted, "the paint")
    assert game.redo() == f"Redo: deleted {name}"
    wait_until(lambda: not game.grids_named(name), "the wall to go for good")
    assert game.redo(expect=None) == "Nothing to redo"

    # A step whose grid is gone is refused and stays where it is
    current = game.build()["current"]
    time.sleep(0.3)
    assert game.build()["current"] == current

"""Friends (lobby) games, design section 14: the host runs Undo as it does offline
and serves the player who joined it the way the companion does on a dedicated
server. Whatever the host replays, for itself or for the joined player, reaches
the other client through the game: blocks built again with their old ids
(BuildBlockRequestInternal), split parts merged back (MergeGrid_MergeBlock), grids
created again (CreateFromObjectBuilderAndAdd), the block links put back, and the
program a host sets.

The test world is creative, so both players may record. The joined player is an
administrator in it, which the teleport needs. The histories of the joined player come from the host's status
file of the players, the answers to its steps from its own status file. Terminal
steps of the joined player are sent as raw companion messages, like in the
dedicated server tests.
"""

from __future__ import annotations

import time

import lobby_rig
import rig
from harness import links, wait_until

# Shared/Companion/Protocol.cs: the channel, MessageType.Step and StepContext
CHANNEL = 48771
STEP = 3
TERMINAL = 1

PASTE_CELLS = [(0, y, z) for y in (-1, 0, 1) for z in (-1, 0, 1)]
PASTE_AT = (24, 1, 9)
# A battery mounts on the floor in the build endpoint's fixed orientation
BATTERY = "LargeBlockBatteryBlock"
BATTERY_CELL = (rig.SECOND_LIGHT[0] - 1, 1, rig.SECOND_LIGHT[2] - 2)
LIGHT_NAME = "Undo Light 2"
# The game takes a client's terminal changes only from within reach of the block
NEAR_LIGHT = (rig.SECOND_LIGHT[0], 1.0, rig.SECOND_LIGHT[2] - 1.0)
# In front of the turret controller, looking at its screen (test_terminal_more.py)
CONSOLE_STAND = (2, 1.0, 14)
CONSOLE_AIM = (2, 1.3, 15)
PB_NAME = "Undo Programmable Block"


def grid_names(game) -> list[str]:
    return sorted(g["name"] for g in game.grids())


def named(game, name: str) -> list[dict]:
    return [g for g in game.grids() if g.get("name") == name]


def paste(game, name: str) -> dict:
    _, forward, up = rig.station_frame()
    last = game.last_node_id()
    (pasted,) = game.api.paste_blueprint(
        xml=rig.blueprint_xml(name, PASTE_CELLS),
        position=rig.station_point(PASTE_AT),
        forward=forward,
        up=up,
    )
    game.wait_recorded(last, f"pasted {name}")
    return pasted


def terminal_step(game, undo: bool = True) -> str:
    """The joined player's step in the terminal context, as its plugin sends it"""
    marker = game.status_file.stat().st_mtime_ns
    assert game.api.send_mod_message(CHANNEL, bytes([STEP, TERMINAL, int(undo)]))[
        "sent"
    ]
    expect = "Undo: " if undo else "Redo: "
    wait_until(
        lambda: game.status_file.stat().st_mtime_ns != marker
        and game.last_message().startswith(expect),
        f"the answer {expect!r}",
    )
    return game.last_message()


def test_the_host_serves_the_joined_player(host, joiner):
    host_log, join_log = lobby_rig.log(lobby_rig.HOST), lobby_rig.log(lobby_rig.JOIN)
    assert "Undo: Info: Session mode: LobbyHost" in host_log
    assert f"Undo: Info: Serving undo to {lobby_rig.JOIN_ID}" in host_log
    assert "Undo: Info: Session mode: LobbyClient" in join_log
    assert "Undo: Info: The server runs the Undo companion, protocol 1" in join_log
    assert joiner.status()["companion"]
    assert joiner.status()["histories"]["build"]["count"] == 0


# --- the host's steps, seen by the joined client ---------------------------------


def test_a_host_build_reaches_the_joined_client(host, joiner):
    """A redo builds the block again under the id it had"""
    cell = BATTERY_CELL
    last = host.last_node_id()
    assert host.api.character_build_block(host.station, cell, BATTERY)["sent"]
    host.wait_recorded(last, "placed 1 block")
    entity_id = wait_until(
        lambda: (b := joiner.block(cell)) and b["entityId"], "the battery over there"
    )
    assert host.block(cell)["entityId"] == entity_id

    host.undo()
    wait_until(lambda: not joiner.exists(cell), "the battery gone over there")
    host.redo()
    wait_until(
        lambda: (b := joiner.block(cell)) and b["entityId"] == entity_id,
        "the battery back under its id over there",
    )
    host.undo()
    wait_until(lambda: not joiner.exists(cell), "the battery gone again")


def test_a_host_restore_of_a_referenced_block(host, joiner):
    """The light comes back under its old id with its name and in its block group
    on the joined client, and on the host the toolbar slot, the turret controller's
    tool list and the event controller's selection point at it again"""
    cell = rig.TARGET

    def grouped() -> bool:
        groups = joiner.api.list_block_groups(joiner.station)
        return rig.IDS[cell] in groups.get(rig.GROUP_NAME, [])

    assert grouped()
    last = host.last_node_id()
    host.api.character_grid_event(host.station, cell, "raze")
    host.wait_recorded(last, "removed 1 block")
    wait_until(lambda: not joiner.exists(cell), "the removal over there")
    wait_until(lambda: not grouped(), "the group to drop it over there")

    host.undo()
    restored = wait_until(lambda: joiner.block(cell), "the restore over there")
    assert restored["entityId"] == rig.IDS[cell]
    assert restored["customName"] == rig.TARGET_NAME
    wait_until(grouped, "the group fix-up over there")
    # Give the event controller its 10 frame update before saving
    time.sleep(1)
    assert links(host.saved_station()) == dict.fromkeys(
        ("toolbar", "turret", "event", "group"), True
    )


def test_a_host_merge_reaches_the_joined_client(host, joiner):
    before = grid_names(joiner)
    last = host.last_node_id()
    host.api.character_grid_event(host.station, rig.BRIDGE, "raze")
    host.wait_recorded(last, "removed 1 block, 1 part split off")
    wait_until(lambda: len(grid_names(joiner)) == len(before) + 1, "the split")

    host.undo()
    wait_until(lambda: grid_names(joiner) == before, "the merge over there")
    block = joiner.block(rig.PART_B_LIGHT)
    assert (block["entityId"], block["gridId"]) == (
        rig.IDS[rig.PART_B_LIGHT],
        joiner.station,
    )


def test_a_host_grid_restore_reaches_the_joined_client(host, joiner):
    name = "Undo Host Paste"
    pasted = paste(host, name)
    wait_until(lambda: named(joiner, name), "the pasted grid over there")

    last = host.last_node_id()
    host.api.close_grid(pasted["entityId"])
    host.wait_recorded(last, f"deleted {name}")
    wait_until(lambda: not named(joiner, name), "the grid gone over there")

    assert host.undo() == f"Undo: deleted {name}"
    (grid,) = wait_until(lambda: named(joiner, name), "the grid back over there")
    assert grid["entityId"] == pasted["entityId"]
    host.undo(expect=f"Undo: pasted {name}")
    wait_until(lambda: not named(joiner, name), "the pasted grid gone over there")


def test_a_host_program_reaches_the_joined_client(host, joiner):
    """A program the host sets through the mod API only recompiles there, the
    game tells no client. An undo or redo on the host sends the editor's program
    request instead, which the game broadcasts."""
    pb = rig.IDS[rig.PROGRAMMABLE]
    label = f"changed the program of {PB_NAME}"
    one = 'void Main() { Echo("one"); }'
    two = 'void Main() { Echo("two"); }'
    last = host.last_node_id("terminal")
    host.api.set_pb_program(pb, one)
    host.wait_recorded(last, label, "terminal")
    time.sleep(0.8)  # past the coalescing window
    host.api.set_pb_program(pb, two)
    host.wait_recorded(last + 1, label, "terminal")
    assert not joiner.api.get_pb_program(pb)

    host.stand_at(CONSOLE_STAND)
    with host.open_terminal(CONSOLE_AIM):
        assert host.undo() == f"Undo: {label}"
        wait_until(lambda: joiner.api.get_pb_program(pb) == one, "the old program")
        assert host.redo() == f"Redo: {label}"
        wait_until(lambda: joiner.api.get_pb_program(pb) == two, "the new program")
        host.undo()
        host.undo()
    # No request carries "no program", the joined client keeps the last one
    assert not host.api.get_pb_program(pb)


# --- the joined player's steps, replayed by the host -------------------------------


def test_a_joined_build_then_undo_and_redo(host, joiner):
    cell = BATTERY_CELL
    last = joiner.last_node_id()
    assert joiner.api.character_build_block(joiner.station, cell, BATTERY)["sent"]
    joiner.wait_recorded(last, "placed 1 block")
    entity_id = wait_until(
        lambda: (b := host.block(cell)) and b["entityId"], "the battery on the host"
    )

    assert joiner.undo() == "Undo: placed 1 block"
    wait_until(lambda: not host.exists(cell), "the battery gone on the host")
    assert joiner.redo() == "Redo: placed 1 block"
    wait_until(
        lambda: (b := joiner.block(cell)) and b["entityId"] == entity_id,
        "the battery back under its id",
    )
    joiner.undo()
    wait_until(lambda: not joiner.exists(cell), "the battery gone again")


def test_a_joined_raze_then_undo_keeps_the_block_detail(host, joiner):
    cell = rig.SECOND_LIGHT
    before = host.block(cell)
    last = joiner.last_node_id()
    joiner.api.character_grid_event(joiner.station, cell, "raze")
    joiner.wait_recorded(last, "removed 1 block")
    wait_until(lambda: not host.exists(cell), "the removal on the host")

    assert joiner.undo() == "Undo: removed 1 block"
    wait_until(lambda: host.exists(cell), "the restore on the host")
    for game in (host, joiner):
        after = wait_until(lambda: game.block(cell), "the light")
        for key in ("entityId", "customName", "colorMask", "enabled", "definition"):
            assert after[key] == before[key], key


def test_a_joined_split_then_merge_back(host, joiner):
    # The joined client sees only the grids within its sync distance
    before = {game: grid_names(game) for game in (host, joiner)}
    last = joiner.last_node_id()
    joiner.api.character_grid_event(joiner.station, rig.BRIDGE, "raze")
    joiner.wait_recorded(last, "removed 1 block, 1 part split off")
    wait_until(lambda: len(grid_names(host)) == len(before[host]) + 1, "the split")

    joiner.undo()
    for game in (host, joiner):
        wait_until(lambda: grid_names(game) == before[game], "the merge")
        block = game.block(rig.PART_B_LIGHT)
        assert (block["entityId"], block["gridId"]) == (
            rig.IDS[rig.PART_B_LIGHT],
            game.station,
        )


def test_a_joined_paste_and_delete_then_undo(host, joiner):
    name = "Undo Joined Paste"
    pasted = paste(joiner, name)
    wait_until(lambda: named(host, name), "the pasted grid on the host")

    last = joiner.last_node_id()
    joiner.api.close_grid(pasted["entityId"])
    joiner.wait_recorded(last, f"deleted {name}")
    wait_until(lambda: not named(host, name), "the grid gone on the host")

    assert joiner.undo() == f"Undo: deleted {name}"
    (grid,) = wait_until(lambda: named(host, name), "the grid back on the host")
    assert grid["entityId"] == pasted["entityId"]
    assert joiner.redo() == f"Redo: deleted {name}"
    wait_until(lambda: not named(host, name), "the grid gone again")
    joiner.undo()
    wait_until(lambda: named(joiner, name), "the grid back again")


def test_a_joined_terminal_change_then_undo(host, joiner):
    light = rig.SECOND_LIGHT
    label = f"changed Radius of {LIGHT_NAME}"

    def radius(game) -> float:
        value = game.api.get_property(game.station, light, "Radius")["value"]
        return round(float(value), 2)

    joiner.stand_at(NEAR_LIGHT)
    before = radius(host)
    last = joiner.last_node_id("terminal")
    joiner.api.set_property(joiner.station, light, "Radius", before + 2)
    joiner.wait_recorded(last, label, "terminal")

    assert terminal_step(joiner) == f"Undo: {label}"
    wait_until(lambda: radius(host) == before, "the old value on the host")
    assert terminal_step(joiner, undo=False) == f"Redo: {label}"
    wait_until(lambda: radius(host) == before + 2, "the new value on the host")
    terminal_step(joiner)
    wait_until(lambda: radius(joiner) == before, "the old value again")


def test_the_joined_grid_history_lists_the_hosts_backups(joiner):
    joiner.api.key("H", ["LeftControl"])
    try:
        rows = wait_until(
            lambda: (g := joiner.status().get("gridHistory")) and g["rows"],
            "the rows from the host",
        )
        assert [row[1] for row in rows].count("Undo Joined Paste") >= 2
    finally:
        rig.focus_gameplay(joiner.api)

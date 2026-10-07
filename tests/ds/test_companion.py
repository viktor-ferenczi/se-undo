"""Dedicated server with the Undo companion (design section 14): the server records
what its clients do and replays their steps, under the old ids, and the results
reach every client through the game.

The first client is the server's administrator with creative tools, in a survival
world. The second client, an administrator too but without creative tools except
in the ownership test, joins for the replication and permission tests. The
histories are read from the companion's status file on the server, the answers to
the steps from the client's.

Terminal steps and the untrusted requests are sent as raw companion messages
through Remote's mod message endpoint: the same message the client plugin sends
for Ctrl-Z in the terminal, and messages no client plugin would send.
"""

from __future__ import annotations

import os
import time
from concurrent.futures import ThreadPoolExecutor
import xml.etree.ElementTree as ET

import pytest

import ds_rig
import rig
from harness import wait_until
from se_remote import CallOp

# Shared/Companion/Protocol.cs: the channel, MessageType.Step and StepContext
CHANNEL = 48771
STEP = 3
BUILD, TERMINAL = 0, 1

PASTE_NAME = "Undo Paste Test"
PASTE_CELLS = [(0, y, z) for y in (-1, 0, 1) for z in (-1, 0, 1)]
PASTE_AT = (24, 1, 9)
RADIUS = "Radius"
LIGHT_NAME = "Undo Light 2"
# A battery mounts on the floor in the build endpoint's fixed orientation
BATTERY = "LargeBlockBatteryBlock"
BATTERY_CELL = (rig.SECOND_LIGHT[0] - 1, 1, rig.SECOND_LIGHT[2] - 2)
# In front of the light: the server takes a client's terminal changes only from
# within reach of the block
NEAR_LIGHT = (rig.SECOND_LIGHT[0], 1.0, rig.SECOND_LIGHT[2] - 1.0)


def step_message(context: int, undo: bool) -> bytes:
    """The client plugin's step: message type, context, undo or redo"""
    return bytes([STEP, context, 1 if undo else 0])


def send(game, message: bytes, expect: str | None = None) -> str:
    """Sends a raw companion message; with expect, waits for the answer"""
    marker = game.status_file.stat().st_mtime_ns
    assert game.api.send_mod_message(CHANNEL, message)["sent"]
    if expect is None:
        return ""
    wait_until(
        lambda: game.status_file.stat().st_mtime_ns != marker
        and game.last_message().startswith(expect),
        f"the answer {expect!r}",
    )
    return game.last_message()


def terminal_step(game, undo: bool = True, expect: str = "Undo: ") -> str:
    return send(game, step_message(TERMINAL, undo), expect)


def radius(game) -> float:
    value = game.api.get_property(game.station, rig.SECOND_LIGHT, RADIUS)["value"]
    return round(float(value), 2)


def server_log() -> str:
    return (ds_rig.SERVER_DATA / "SpaceEngineersDedicated.log").read_text(
        errors="replace"
    )


def grid_names(game) -> set[str]:
    return {g["name"] for g in game.grids()}


def pasted_grids(game) -> list[dict]:
    return [g for g in game.grids() if g.get("name") == PASTE_NAME]


def has_all_cells(game, grid_id: int) -> bool:
    result = game.api.call([CallOp.cube_exists(grid_id, cell) for cell in PASTE_CELLS])
    return all(result.call(i)["exists"] for i in range(len(PASTE_CELLS)))


def store_rows(steam_id: int) -> list[ET.Element]:
    """The served player's grid store index on the server"""
    (index,) = (ds_rig.SERVER_DATA / "Undo" / "Worlds").glob(
        f"*/players/{steam_id}/grids/index.xml"
    )
    return list(ET.parse(index).getroot().iter("StoreRow"))


def paste_test_grid(game) -> dict:
    _, forward, up = rig.station_frame()
    last = game.last_node_id()
    (pasted,) = game.api.paste_blueprint(
        xml=rig.blueprint_xml(PASTE_NAME, PASTE_CELLS),
        position=rig.station_point(PASTE_AT),
        forward=forward,
        up=up,
    )
    game.wait_recorded(last, f"pasted {PASTE_NAME}")
    return pasted


# --- the first client --------------------------------------------------------------


def test_the_client_finds_the_companion(game):
    log = ds_rig.LOG.read_text(errors="replace")
    assert "Undo: Info: Session mode: ServerClient" in log
    assert "Undo: Info: The server runs the Undo companion, protocol 1" in log
    assert f"Undo: Info: Serving undo to {ds_rig.CLIENT_ID}" in server_log()
    assert ds_rig.server_player(ds_rig.CLIENT_ID)["connected"]
    assert game.status()["companion"]


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
    for key in ("entityId", "customName", "colorMask", "enabled", "definition"):
        assert after[key] == before[key], key

    game.redo()
    wait_until(lambda: not game.exists(cell), "the removal again")
    game.undo()
    wait_until(lambda: game.exists(cell), "the restore again")
    assert game.block(cell)["entityId"] == before["entityId"]


def test_raze_a_referenced_block_then_undo(game):
    """The server restores it under its old entity id, which the toolbar slots and
    controller lists that point at it use"""
    cell = rig.TARGET
    last = game.last_node_id()

    game.api.character_grid_event(game.station, cell, "raze")
    wait_until(lambda: not game.exists(cell), "the removal")
    game.wait_recorded(last, "removed 1 block")

    game.undo()
    wait_until(lambda: game.exists(cell), "the restore")
    restored = game.block(cell)
    assert restored["entityId"] == rig.IDS[cell]
    assert restored["customName"] == rig.TARGET_NAME


def test_raze_that_splits_then_merge_back(game):
    light = rig.IDS[rig.PART_B_LIGHT]
    grids_before = grid_names(game)
    last = game.last_node_id()

    game.api.character_grid_event(game.station, rig.BRIDGE, "raze")
    game.wait_recorded(last, "removed 1 block, 1 part split off")
    wait_until(
        lambda: len(grid_names(game)) == len(grids_before) + 1,
        "the split on the client",
    )

    for name in ("undo", "redo", "undo"):
        getattr(game, name)()
        if name == "undo":
            wait_until(lambda: game.exists(rig.BRIDGE), "the bridge")
            wait_until(lambda: grid_names(game) == grids_before, "the merge")
            assert all(game.exists(p) for p in rig.PART_B)
            block = game.block(rig.PART_B_LIGHT)
            assert (block["entityId"], block["gridId"]) == (light, game.station)
        else:
            wait_until(
                lambda: len(grid_names(game)) == len(grids_before) + 1, "the split"
            )


def test_paint_then_undo_and_redo(game):
    cell = rig.PAINT_LIGHT
    original = game.block(cell)["colorMask"]
    last = game.last_node_id()

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


def test_paste_then_undo_and_redo(game):
    pasted = paste_test_grid(game)
    (entry,) = game.build()["nodes"][-1]["storeRefs"]
    row = next(r for r in store_rows(ds_rig.CLIENT_ID) if r.get("Id") == entry)
    assert row.get("Reason") == "Pasted"
    assert row.get("MainGridName") == PASTE_NAME
    assert row.get("MainGridEntityId") == str(pasted["entityId"])

    assert game.undo() == f"Undo: pasted {PASTE_NAME}"
    wait_until(lambda: not pasted_grids(game), "the pasted grid to go")
    assert game.redo() == f"Redo: pasted {PASTE_NAME}"
    (grid,) = wait_until(lambda: pasted_grids(game), "the grid to return")
    # The server creates it again without remapping, under the same id
    assert grid["entityId"] == pasted["entityId"]
    assert has_all_cells(game, grid["entityId"])

    game.undo()
    wait_until(lambda: not pasted_grids(game), "the grid to go again")


def test_delete_grid_then_undo(game):
    pasted = paste_test_grid(game)
    last = game.last_node_id()

    # A client sends the close request, the server closes the grid
    game.api.close_grid(pasted["entityId"])
    wait_until(lambda: not pasted_grids(game), "the closed grid to go")
    game.wait_recorded(last, f"deleted {PASTE_NAME}")

    assert game.undo() == f"Undo: deleted {PASTE_NAME}"
    (grid,) = wait_until(lambda: pasted_grids(game), "the grid to come back")
    assert grid["entityId"] == pasted["entityId"]
    assert has_all_cells(game, grid["entityId"])

    assert game.redo() == f"Redo: deleted {PASTE_NAME}"
    wait_until(lambda: not pasted_grids(game), "the grid to go again")
    game.undo()
    wait_until(lambda: pasted_grids(game), "the grid to come back again")
    game.undo(expect=f"Undo: pasted {PASTE_NAME}")
    wait_until(lambda: not pasted_grids(game), "the pasted grid to go")


def test_terminal_property_then_undo(game):
    """The client sends terminal changes as synced values; the server tells the
    control from the values before and after"""
    label = f"changed {RADIUS} of {LIGHT_NAME}"
    light = rig.SECOND_LIGHT
    game.stand_at(NEAR_LIGHT)
    before = radius(game)
    last = game.last_node_id("terminal")

    game.api.set_property(game.station, light, RADIUS, before + 2)
    game.wait_recorded(last, label, history="terminal")

    assert terminal_step(game) == f"Undo: {label}"
    wait_until(lambda: radius(game) == before, "the old value")
    assert terminal_step(game, undo=False, expect="Redo: ") == f"Redo: {label}"
    wait_until(lambda: radius(game) == before + 2, "the new value")
    terminal_step(game)
    wait_until(lambda: radius(game) == before, "the old value again")


def test_block_name_then_undo(game):
    cell = rig.SECOND_LIGHT
    name = game.block(cell)["customName"]
    last = game.last_node_id("terminal")

    game.api.set_custom_name(game.station, cell, "Renamed Light")
    game.wait_recorded(last, f"renamed block {name} to Renamed Light", "terminal")

    terminal_step(game)
    wait_until(lambda: game.block(cell)["customName"] == name, "the old name")


def test_grid_history_lists_the_servers_backups(game):
    """Ctrl-H asks the server for the rows of the player's grid store"""
    game.api.key("H", ["LeftControl"])
    try:
        rows = wait_until(
            lambda: (g := game.status().get("gridHistory")) and g["rows"],
            "the rows from the server",
        )
        names = [row[1] for row in rows]
        assert names.count(PASTE_NAME) >= 2  # pasted and deleted
    finally:
        rig.focus_gameplay(game.api)


# --- the second client -------------------------------------------------------------


def test_the_other_client_sees_the_restore(game, game2):
    """What the server replays reaches every client: the block back under its id,
    the split piece merged into the station again"""
    cell = rig.SECOND_LIGHT
    entity_id = game.block(cell)["entityId"]
    last = game.last_node_id()
    game.api.character_grid_event(game.station, cell, "raze")
    game.wait_recorded(last, "removed 1 block")
    wait_until(lambda: not game2.exists(cell), "the removal on the other client")

    game.undo()
    wait_until(lambda: game2.exists(cell), "the restore on the other client")
    assert game2.block(cell)["entityId"] == entity_id

    grids_before = grid_names(game2)
    last = game.last_node_id()
    game.api.character_grid_event(game.station, rig.BRIDGE, "raze")
    game.wait_recorded(last, "removed 1 block, 1 part split off")
    wait_until(lambda: len(grid_names(game2)) == len(grids_before) + 1, "the split")
    game.undo()
    wait_until(
        lambda: grid_names(game2) == grids_before, "the merge on the other client"
    )
    block = game2.block(rig.PART_B_LIGHT)
    assert (block["entityId"], block["gridId"]) == (
        rig.IDS[rig.PART_B_LIGHT],
        game2.station,
    )


def test_ownership_is_checked_on_the_server(game, game2):
    """A terminal step on a block that belongs to someone else since is refused.
    A client cannot hand a block to another player on a dedicated server, so the
    second client, with creative tools for this test, removes the first client's
    battery and builds its own in the cell; the step resolves to that one."""
    cell = BATTERY_CELL
    # Within reach of the battery for the terminal change
    game.stand_at(NEAR_LIGHT)
    last = game.last_node_id()
    assert game.api.character_build_block(game.station, cell, BATTERY)["sent"]
    game.wait_recorded(last, "placed 1 block")
    battery = wait_until(
        lambda: (b := game.block(cell)) and b["ownerSteamId"] == ds_rig.CLIENT_ID and b,
        "the battery",
    )
    label = f"changed OnOff of {battery['customName']}"

    last = game.last_node_id("terminal")
    game.api.set_property(game.station, cell, "OnOff", False)
    game.wait_recorded(last, label, history="terminal")

    assert game2.api.set_admin_flag("creativeTools", True)["creativeToolsEnabled"]
    try:
        last2 = game2.last_node_id()
        game2.api.character_grid_event(game2.station, cell, "raze")
        removed = game2.wait_recorded(last2, "removed 1 block")
        assert game2.api.character_build_block(game2.station, cell, BATTERY)["sent"]
        game2.wait_recorded(removed["id"], "placed 1 block")
        wait_until(
            lambda: (b := game.block(cell)) and b["ownerSteamId"] == ds_rig.CLIENT2_ID,
            "the other player's battery",
        )

        assert (
            terminal_step(game, expect="Undo not available")
            == "Undo not available: the block belongs to someone else"
        )

        # The second player takes their steps back; the first one's battery returns
        game2.undo()
        game2.undo(expect="Undo: removed 1 block")
        wait_until(
            lambda: (b := game.block(cell)) and b["ownerSteamId"] == ds_rig.CLIENT_ID,
            "the first player's battery",
        )
    finally:
        game2.api.set_admin_flag("creativeTools", False)

    terminal_step(game)
    wait_until(
        lambda: game.api.get_property(game.station, cell, "OnOff")["value"] is True,
        "the battery on again",
    )
    game.undo(expect="Undo: placed 1 block")
    wait_until(lambda: not game.exists(cell), "the battery gone")


def test_a_player_without_creative_tools_gets_the_vanilla_keys(game2):
    """Nothing is recorded for a player without creative tools in survival, and
    Ctrl-Z is the relative dampeners on their client"""
    player = wait_until(
        lambda: ds_rig.server_player(ds_rig.CLIENT2_ID), "the handshake"
    )
    assert player["connected"]
    nodes = player["histories"]["build"]["count"]

    game2.api.set_custom_name(game2.station, rig.PAINT_LIGHT, "Not Recorded")
    game2.vanilla_keys()
    histories = ds_rig.server_player(ds_rig.CLIENT2_ID)["histories"]
    assert histories["build"]["count"] == nodes
    assert histories["terminal"]["count"] == 0


def test_a_step_without_creative_tools_is_refused(game2):
    """The client plugin would not send it; the server checks anyway"""
    assert (
        send(game2, step_message(BUILD, True), expect="Undo not available")
        == "Undo not available: needs creative tools"
    )


def test_an_oversized_message_is_dropped(game2):
    send(game2, step_message(BUILD, True) + bytes(2000))
    wait_until(
        lambda: f"Dropped a message of 2003 bytes from {ds_rig.CLIENT2_ID}"
        in server_log(),
        "the drop in the server log",
    )


def test_a_burst_of_requests_is_limited(game2):
    """A step costs a token of a bucket of 20 that fills by 10 a second; the
    requests come from parallel threads so the bucket cannot keep up"""
    refused = (
        f"{ds_rig.CLIENT2_ID}: Undo not available: too many requests, wait a moment"
    )
    before = server_log().count(refused)
    with ThreadPoolExecutor(max_workers=10) as pool:
        for _ in range(60):
            pool.submit(game2.api.send_mod_message, CHANNEL, step_message(BUILD, True))
    wait_until(lambda: server_log().count(refused) > before, "the rate limit")

    # The bucket fills up again
    time.sleep(3)
    assert (
        send(game2, step_message(BUILD, True), expect="Undo not available: needs")
        == "Undo not available: needs creative tools"
    )


# --- across a restart of the server ------------------------------------------------


@pytest.mark.skipif(os.environ.get("UNDO_ATTACH") == "1", reason="restarts")
def test_the_history_survives_a_server_restart(game):
    """The served histories are saved in the world, UndoPlayers.xml.gz, and work
    again after the server loaded it"""
    cell = rig.SECOND_LIGHT
    entity_id = game.block(cell)["entityId"]
    last = game.last_node_id()
    game.api.character_grid_event(game.station, cell, "raze")
    game.wait_recorded(last, "removed 1 block")

    ds_rig.stop_client()
    ds_rig.stop_server()
    assert (ds_rig.WORLD / "UndoPlayers.xml.gz").exists()
    ds_rig.prepare_server(admin=True, fresh=False)
    ds_rig.start_server()
    api = ds_rig.start_client()
    api.set_admin_flag("creativeTools", True)
    game.api = api
    wait_until(lambda: game.status().get("companion"), "the handshake")

    assert game.build()["nodes"][-1]["label"] == "removed 1 block"
    assert game.undo() == "Undo: removed 1 block"
    wait_until(lambda: game.exists(cell), "the restore after the restart")
    assert game.block(cell)["entityId"] == entity_id

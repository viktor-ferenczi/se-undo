"""Dedicated server row of design section 13: one client of a Magnetar server,
first as its administrator with creative tools, then, after a server restart
without administrators, as a regular survival player with the same history.

The tests run in file order and build on each other.
"""

from __future__ import annotations

import gzip
import math
import time
import xml.etree.ElementTree as ET

import pytest

import ds_rig
import rig
import test_terminal as tt
from harness import Game, links, station_element, wait_until

PASTED = f"pasted {ds_rig.SOURCE_NAME}"
NEEDS_TOOLS = "not available: needs creative tools"
VANTAGE = (15, 0.6, 4)
OPEN_AIR = (24, 6, -6)


def copies(game) -> list[int]:
    return [g["entityId"] for g in game.grids() if g["name"] == ds_rig.SOURCE_NAME]


def history_file():
    (path,) = (ds_rig.CLIENT.appdata / "Undo" / "Servers").glob("*/*/*/history.xml.gz")
    return path


def unlocked(game) -> bool:
    return not game.build()["locked"]


def test_the_plugin_sees_a_dedicated_server_client(game):
    assert game.status()["mode"] == "ServerClient"
    assert game.build()["count"] == 0


def test_paste_then_undo_as_admin(game):
    """The clipboard's paste is a request; the server never names the new grid, so
    the plugin recognizes it when it arrives (design section 6)"""
    api = game.api
    target = rig.station_point(VANTAGE)
    api.character_teleport(*target)
    wait_until(
        lambda: math.dist(api.get_character()["position"], target) < 1.0,
        "the teleport onto the station",
    )

    # Remote's target endpoint names no grid on a client, the distance has to do
    def aimed():
        api.character_look_at(*rig.station_point(ds_rig.SOURCE_AT))
        hit = api.get_character_target(max_distance=200)
        return hit.get("hit") and 15 < hit["distance"] < 26

    wait_until(aimed, "the crosshair on the copy source", interval=0.3)
    assert len(copies(game)) == 1
    api.key("C", ["LeftControl"])
    api.key("V", ["LeftControl"])
    time.sleep(1)
    api.character_look_at(*rig.station_point(OPEN_AIR))
    time.sleep(1)
    api.set_input_state(mouse_left=True, mode="override")
    time.sleep(0.2)
    api.clear_input_state()

    node = game.wait_recorded(0, PASTED)
    assert not node["referenceLost"]
    (pasted,) = [g for g in copies(game) if g != ds_rig.SOURCE_ID]

    assert game.undo() == f"Undo: {PASTED}"
    wait_until(lambda: copies(game) == [ds_rig.SOURCE_ID], "the pasted grid to go")
    wait_until(lambda: unlocked(game), "the close to complete")

    # Redo sends the paste request again. The grid comes back under a new id,
    # which the plugin learns by matching it, or the next undo could not close it.
    assert game.redo() == f"Redo: {PASTED}"
    wait_until(lambda: len(copies(game)) == 2, "the grid to return")
    wait_until(lambda: unlocked(game), "the paste to be matched")
    assert not game.build()["nodes"][-1]["unknown"]
    (again,) = [g for g in copies(game) if g != ds_rig.SOURCE_ID]
    assert again != pasted

    assert game.undo() == f"Undo: {PASTED}"
    wait_until(lambda: copies(game) == [ds_rig.SOURCE_ID], "the grid to go again")
    wait_until(lambda: unlocked(game), "the close to complete")
    game.redo()
    wait_until(lambda: len(copies(game)) == 2, "the grid to return again")
    wait_until(lambda: unlocked(game), "the paste to be matched")


def test_a_client_restore_loses_links_across_the_boundary(game):
    """A client cannot reach the id preserving restore, so a removed block comes
    back through a paste into the grid: same name and settings, new entity id
    (design section 7)"""
    cell = rig.TARGET
    before = game.block(cell)
    assert before["entityId"] == rig.IDS[cell]
    last = game.last_node_id()

    game.api.character_grid_event(game.station, cell, "raze")
    wait_until(lambda: not game.exists(cell), "the removal")
    # A client waits the pending timeout for grid splits before it records
    game.wait_recorded(last, "removed 1 block")

    assert game.undo() == "Undo: removed 1 block (restored, some block links lost)"
    wait_until(lambda: game.exists(cell), "the restore")
    wait_until(lambda: unlocked(game), "the restore to complete")
    after = game.block(cell)
    assert after["customName"] == before["customName"] == rig.TARGET_NAME
    assert after["entityId"] != before["entityId"]


def test_terminal_changes_on_a_client(game):
    """The terminal controls of a client exist only once its grids arrived; the
    plugin's hooks have to be there by then, and the values sync through the server"""
    api = game.api
    target = rig.station_point(tt.STAND)
    api.character_teleport(*target)
    wait_until(
        lambda: math.dist(api.get_character()["position"], target) < 1.5,
        "the teleport in front of the turret controller",
    )

    def name():
        return game.block(tt.BLOCK)["customName"]

    label = f"changed {tt.CHECKBOX} of {tt.BLOCK_NAME}"
    before = tt.prop(game, tt.CHECKBOX)
    last = game.last_node_id("terminal")
    api.set_property(game.station, tt.BLOCK, tt.CHECKBOX, not before)
    tt.recorded(game, last, label)

    rename = f"renamed block {tt.BLOCK_NAME} to Renamed On Server"
    api.set_custom_name(game.station, tt.BLOCK, "Renamed On Server")
    tt.recorded(game, last + 1, rename)

    # The Info tab cannot be driven on a server client (SE1-0075), so the grid name
    # is not part of this
    with tt.terminal(game):
        assert game.undo() == f"Undo: {rename}"
        wait_until(lambda: name() == tt.BLOCK_NAME, "the old name")
        assert game.undo() == f"Undo: {label}"
        wait_until(lambda: tt.prop(game, tt.CHECKBOX) is before, "the old value")
        assert game.redo() == f"Redo: {label}"
        wait_until(lambda: tt.prop(game, tt.CHECKBOX) is (not before), "the new value")
        game.undo()


def test_history_is_stored_per_server_player_and_world(game):
    """A client has no save folder: the history goes under the plugin's storage,
    written within the autosave interval after a change (design section 8)"""
    status = ds_rig.CLIENT.status_file
    path = wait_until(
        lambda: (p := history_file()).stat().st_mtime_ns >= status.stat().st_mtime_ns
        and p,
        "the history file",
    )
    world, player, server = path.parents[0], path.parents[1], path.parents[2]
    assert server.name.endswith(ds_rig.SERVER_NAME)
    assert server.name.split("_")[0].isdigit()
    assert player.name.startswith(f"{ds_rig.CLIENT_ID}_")
    assert world.name.startswith(ds_rig.WORLD_NAME)

    with gzip.open(path, "rb") as file:
        document = ET.parse(file).getroot()
    labels = [n.findtext("Label") for n in document.findall("Build/Nodes/Node")]
    assert labels[1:] == [PASTED, "removed 1 block"]
    # The grid store of the session is next to it
    assert (world / "grids" / "index.xml").exists()
    # And nothing went where an offline world keeps its store
    assert not (ds_rig.CLIENT.appdata / "Undo" / "Worlds").exists()


@pytest.fixture(scope="module")
def player(game):
    """The same player after a server restart without administrators. The server
    saves its world on the way down."""
    before = [n["label"] for n in game.build()["nodes"]], game.build()["current"]
    ds_rig.stop_client()
    ds_rig.stop_server()
    ds_rig.prepare_server(admin=False, fresh=False)
    ds_rig.start_server()
    api = ds_rig.start_client()
    regular = Game(api, ds_rig.CLIENT.status_file)
    regular.before = before
    return regular


def test_the_saved_server_world_shows_the_lost_links(player):
    station = station_element(ds_rig.WORLD / "SANDBOX_0_0_0_.sbs", rig.STATION_ID)
    # The toolbar slot and the tool list still hold the old id, which no block
    # has any more; the block group lost the light when it was removed
    saved = links(station)
    assert saved["toolbar"] and saved["turret"] and not saved["group"]
    ids = [e.text for e in station.iter("EntityId")]
    assert str(rig.IDS[rig.TARGET]) not in ids


def test_a_regular_player_gets_the_history_back_but_not_the_rights(player):
    game = player
    assert game.status()["mode"] == "ServerClient"
    labels = [n["label"] for n in game.build()["nodes"]]
    assert (labels, game.build()["current"]) == game.before

    flag = game.api.set_admin_flag("creativeTools", False)
    assert flag["promoteLevel"] == "None" and not flag["creativeToolsEnabled"]

    # Undo would close the pasted grid, redo would remove the block again
    assert game.undo(expect=None) == f"Undo {NEEDS_TOOLS}"
    assert game.redo(expect=None) == f"Redo {NEEDS_TOOLS}"
    assert len(copies(game)) == 2
    assert game.exists(rig.TARGET)
    # Refused by the plugin, so the server had nothing to kick the client for
    time.sleep(2)
    assert game.api.get_state()["active"]


def test_a_regular_player_can_undo_a_paint(player):
    game, cell = player, rig.PAINT_LIGHT
    before = game.block(cell)["colorMask"]
    last = game.last_node_id()

    game.api.character_grid_event(game.station, cell, "color")
    wait_until(lambda: game.block(cell)["colorMask"] != before, "the paint")
    game.wait_recorded(last, "painted 1 block")

    assert game.undo() == "Undo: painted 1 block"
    wait_until(lambda: game.block(cell)["colorMask"] == before, "the old color")

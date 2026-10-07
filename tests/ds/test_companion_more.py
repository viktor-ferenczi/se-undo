"""More of the dedicated server with the companion (SE1-0098): the client's own
input paths, which test_companion.py stands in for with raw messages and Remote's
requests, and the cases around them.

- terminal steps taken in the client's terminal screen, programs and a block
  toolbar recorded on the server, the grid name
- a paste into a grid, a new grid from one block, a rotor base with its subgrid
  (the group snapshot), a block group put back
- the server's refusals: something in the way, a grid someone else deleted
- the grid history dialog's Paste and Delete, through the server
- the histories written into the world on the server's autosave

The first client is the server's administrator with creative tools, as in
test_companion.py. The world saves itself every minute.
"""

from __future__ import annotations

import gzip
import math
import time


import ds_rig
import rig
from harness import wait_until
from se_remote import CallOp
from test_grid_store import dialog, message_box, shown
from test_mechanical import Rig

# Shared/Companion/Protocol.cs
CHANNEL = 48771
STEP = 3
TERMINAL = 1

WORLD_SETTINGS = {"AutoSaveInMinutes": "1"}

CONTROLLER = rig.TURRET_CONTROLLER
CONTROLLER_NAME = "Undo Turret Controller"
SLIDER = "MultiplierAz"
# In front of the turret controller, looking at its screen (test_terminal.py)
CONSOLE_STAND = (2, 1.0, 14)
CONSOLE_AIM = (2, 1.3, 15)
PB_NAME = "Undo Programmable Block"
# The free edge of the station, facing the copy source 9 m out
VANTAGE = (15, 0.6, 4)
OPEN_AIR = (24, 1, -4)
PASTE_CELLS = [(0, y, z) for y in (-1, 0, 1) for z in (-1, 0, 1)]


def terminal_step(game, undo: bool = True, expect: str | None = None) -> str:
    """The step the client plugin sends for Ctrl-Z or Ctrl-Y in the terminal"""
    marker = game.status_file.stat().st_mtime_ns
    message = bytes([STEP, TERMINAL, int(undo)])
    assert game.api.send_mod_message(CHANNEL, message)["sent"]
    expect = expect or ("Undo: " if undo else "Redo: ")
    wait_until(
        lambda: game.status_file.stat().st_mtime_ns != marker
        and game.last_message().startswith(expect),
        f"the answer {expect!r}",
    )
    return game.last_message()


def teleport(api, cell) -> None:
    target = rig.station_point(cell)
    api.character_teleport(*target)
    wait_until(
        lambda: math.dist(api.get_character()["position"], target) < 1.5,
        "the teleport",
    )


def click(api) -> None:
    """A left click as gameplay input; a press shorter than a frame gets lost"""
    api.set_input_state(mouse_left=True, mode="override")
    time.sleep(0.5)
    api.clear_input_state()


def aim_at_source(api) -> None:
    """Remote's target endpoint names no grid on a client (SE1-0075), so the
    distance has to do: the source is the only thing that far out"""

    def aimed():
        api.character_look_at(*rig.station_point(ds_rig.SOURCE_AT), tolerance=0.5)
        hit = api.get_character_target(max_distance=200)
        return hit.get("hit") and 15 < hit["distance"] < 26

    wait_until(aimed, "the crosshair on the copy source", interval=0.3)


def station_there(game) -> bool:
    return any(g["entityId"] == game.station for g in game.grids())


def named(game, name: str) -> list[dict]:
    return [g for g in game.grids() if g.get("name") == name]


# --- the terminal --------------------------------------------------------------


def test_a_step_from_the_client_terminal(game):
    """The slider moves in the client's terminal screen, Ctrl-Z and Ctrl-Y there
    go to the server as terminal steps"""
    label = f"changed {SLIDER} of {CONTROLLER_NAME}"

    def value():
        return game.api.get_property(game.station, CONTROLLER, SLIDER)["value"]

    game.stand_at(CONSOLE_STAND)
    before = value()
    last = game.last_node_id("terminal")
    with game.open_terminal(CONSOLE_AIM) as screen:
        for v in (0.2, 0.5, 0.8):
            game.api.control_set(SLIDER, v, screen=screen)
        game.wait_recorded(last, label, "terminal")
        changed = wait_until(lambda: (v := value()) != before and v, "the value")

        assert game.undo() == f"Undo: {label}"
        wait_until(lambda: value() == before, "the old value")
        assert game.redo() == f"Redo: {label}"
        wait_until(lambda: value() == changed, "the new value")
        game.undo()
        wait_until(lambda: value() == before, "the old value again")


def test_a_program_is_recorded_on_the_server(game):
    """The client's program requests are recorded where they run; the undo sends
    the request again from the server, which the game broadcasts"""
    pb = rig.IDS[rig.PROGRAMMABLE]
    label = f"changed the program of {PB_NAME}"
    one = 'void Main() { Echo("one"); }'
    two = 'void Main() { Echo("two"); }'
    last = game.last_node_id("terminal")
    game.api.set_pb_program(pb, one)
    game.wait_recorded(last, label, "terminal")
    time.sleep(0.8)  # past the coalescing window
    game.api.set_pb_program(pb, two)
    game.wait_recorded(last + 1, label, "terminal")

    assert terminal_step(game) == f"Undo: {label}"
    wait_until(lambda: game.api.get_pb_program(pb) == one, "the old program")
    assert terminal_step(game, undo=False) == f"Redo: {label}"
    wait_until(lambda: game.api.get_pb_program(pb) == two, "the new program")
    terminal_step(game)
    wait_until(lambda: game.api.get_pb_program(pb) == one, "the old program again")


def test_a_block_toolbar_is_recorded_on_the_server(game):
    from se_autopilot import enter_cockpit

    api = game.api
    label = "changed the toolbar of Undo Cockpit"

    def slots() -> dict[int, str]:
        return {item["slot"]: item["name"] for item in api.get_toolbar()["items"]}

    assert enter_cockpit(api, game.station, timeout=60)["state"] == "sitting"
    try:
        before = wait_until(slots, "the cockpit toolbar")
        last = game.last_node_id("terminal")
        api.key("G")
        wait_until(lambda: game.screen("MyGuiScreenCubeBuilder"), "the toolbar screen")
        time.sleep(1)
        api.set_toolbar_slot(0, None)
        game.wait_recorded(last, label, "terminal")
        rig.focus_gameplay(api)
        wait_until(lambda: 0 not in slots(), "the cleared slot")

        assert terminal_step(game) == f"Undo: {label}"
        wait_until(lambda: slots() == before, "the slot back")
        assert terminal_step(game, undo=False) == f"Redo: {label}"
        wait_until(lambda: 0 not in slots(), "the slot cleared again")
        terminal_step(game)
        wait_until(lambda: slots() == before, "the slot back again")
    finally:
        rig.focus_gameplay(api)
        game.leave_seat()


def test_a_grid_name_is_recorded_on_the_server(game):
    """The server takes a client's rename only from within 15 m of the grid, and
    for a grid the player owns the most blocks of when anyone owns blocks there.
    The client pastes one with a battery, close to where it stands."""
    name, renamed = "Undo Named Grid", "Renamed Grid"
    teleport(game.api, VANTAGE)
    _, forward, up = rig.station_frame()
    battery = rig._block(
        "MyObjectBuilder_BatteryBlock", "LargeBlockBatteryBlock", (0, 2, 0), entity_id=0
    )
    last = game.last_node_id()
    (pasted,) = game.api.paste_blueprint(
        xml=rig.blueprint_xml(name, PASTE_CELLS, blocks=battery),
        position=rig.station_point((19, 1, 1)),
        forward=forward,
        up=up,
    )
    game.wait_recorded(last, f"pasted {name}")
    grid = pasted["entityId"]

    label = f"renamed grid {name} to {renamed}"
    last = game.last_node_id("terminal")
    game.api.call([CallOp.grid_method(grid, "SetCustomName", {"name": renamed})])
    game.wait_recorded(last, label, "terminal")

    assert terminal_step(game) == f"Undo: {label}"
    wait_until(lambda: named(game, name), "the old name")
    assert terminal_step(game, undo=False) == f"Redo: {label}"
    wait_until(lambda: named(game, renamed), "the new name")
    terminal_step(game)
    wait_until(lambda: named(game, name), "the old name again")
    game.undo(expect=f"Undo: pasted {name}")
    wait_until(lambda: not named(game, name), "the grid gone")


# --- the build context -----------------------------------------------------------


def test_a_paste_into_a_grid(game):
    """Ctrl-C on the copy source, Ctrl-V, the preview snapped onto the source's
    face and a click: the client's paste into that grid"""
    api, grid = game.api, ds_rig.SOURCE_ID
    merged = [(x - 1, y, z) for x, y, z in ds_rig.SOURCE_CELLS]

    def merged_cells() -> int:
        result = api.call([CallOp.cube_exists(grid, cell) for cell in merged])
        return sum(result.call(i)["exists"] for i in range(len(merged)))

    teleport(api, VANTAGE)
    aim_at_source(api)
    last = game.last_node_id()
    api.key("C", ["LeftControl"])
    api.key("V", ["LeftControl"])
    time.sleep(1)  # the preview snaps to the grid once it updated
    aim_at_source(api)
    click(api)
    label = f"pasted {len(merged)} blocks into {ds_rig.SOURCE_NAME}"
    game.wait_recorded(last, label)
    wait_until(lambda: merged_cells() == len(merged), "the pasted blocks")

    assert game.undo() == f"Undo: {label}"
    wait_until(lambda: merged_cells() == 0, "the pasted blocks to go")
    assert game.redo() == f"Redo: {label}"
    wait_until(lambda: merged_cells() == len(merged), "the pasted blocks again")
    game.undo()
    wait_until(lambda: merged_cells() == 0, "the pasted blocks to go again")


def test_a_new_grid_from_one_block(game):
    """The cube builder aimed at empty air; the grid is a ship and falls, so the
    steps follow quickly"""
    api = game.api

    def new_grids():
        return [g for g in game.grids() if g["name"].startswith("Large Grid")]

    teleport(api, VANTAGE)
    api.set_toolbar_slot(0, "MyObjectBuilder_CubeBlock/LargeBlockArmorBlock")
    api.character_look_at(*rig.station_point(OPEN_AIR))
    assert not api.get_character_target(max_distance=200)["hit"]
    last = game.last_node_id()
    api.key("D1")
    wait_until(
        lambda: (api.get_character().get("weapon") or {}).get("type") == "MyCubePlacer",
        "the cube builder",
    )
    try:
        time.sleep(0.5)  # the gizmo follows the camera from the next frame
        click(api)
        (grid,) = wait_until(new_grids, "the new grid")
        game.wait_recorded(last, f"placed 1 block, new grid {grid['name']}")

        assert game.undo().startswith("Undo: placed 1 block, new grid")
        wait_until(lambda: not new_grids(), "the new grid to go")
        assert game.redo().startswith("Redo: placed 1 block, new grid")
        (again,) = wait_until(new_grids, "the new grid back")
        assert again["entityId"] == grid["entityId"]
        game.undo()
        wait_until(lambda: not new_grids(), "the new grid to go again")
    finally:
        api.key("D0")


def test_a_rotor_base_comes_back_with_its_subgrid(game):
    """The server takes a snapshot of the grid group before the removal and
    creates it again from that. The station is part of the group, so it goes and
    comes back on the client too."""
    built = Rig(game, "LargeStator", (3, 1, 3))
    last = game.last_node_id()
    game.api.character_grid_event(game.station, built.cell, "raze")
    game.wait_recorded(last, "removed 1 block")
    wait_until(lambda: not game.exists(built.cell), "the removal")

    for _ in range(2):
        game.undo()
        # The station goes along with the group, the client sees it again a
        # moment later
        wait_until(lambda: station_there(game), "the station back", timeout=60)
        built.assert_whole()
        game.redo()
        wait_until(lambda: station_there(game), "the station again", timeout=60)
        wait_until(lambda: not game.exists(built.cell), "the removal again")
    game.undo()
    wait_until(lambda: station_there(game), "the station at the end", timeout=60)


def test_a_block_group_is_put_back(game):
    cell = rig.TARGET

    def grouped() -> bool:
        groups = game.api.list_block_groups(game.station)
        return rig.IDS[cell] in groups.get(rig.GROUP_NAME, [])

    assert grouped()
    last = game.last_node_id()
    game.api.character_grid_event(game.station, cell, "raze")
    game.wait_recorded(last, "removed 1 block")
    wait_until(lambda: not grouped(), "the group to drop the light")
    game.undo()
    wait_until(grouped, "the light back in its group")


# --- refusals --------------------------------------------------------------------


def test_a_restore_with_something_in_the_way(game):
    """The client's character stands in the removed block's cell; the server says
    so and the step stays"""
    api, cell = game.api, rig.SECOND_LIGHT
    last = game.last_node_id()
    api.character_grid_event(game.station, cell, "raze")
    node = game.wait_recorded(last, "removed 1 block")
    here = api.get_character()["position"]
    teleport(api, (cell[0], 0.6, cell[2]))
    time.sleep(1)
    try:
        current = game.build()["current"]
        assert game.undo(expect=None) == "Undo not available: something is in the way"
        assert game.build()["current"] == current
        assert not game.exists(cell)
    finally:
        api.character_teleport(*here)
        time.sleep(1)
    assert game.undo() == f"Undo: {node['label']}"
    wait_until(lambda: game.exists(cell), "the light")


def test_a_step_on_a_grid_someone_else_deleted(game, game2):
    """The second client deletes the grid the first one changed"""
    name = "Undo Gone Grid"
    _, forward, up = rig.station_frame()
    last = game.last_node_id()
    (pasted,) = game.api.paste_blueprint(
        xml=rig.blueprint_xml(name, PASTE_CELLS),
        position=rig.station_point((24, 1, 9)),
        forward=forward,
        up=up,
    )
    game.wait_recorded(last, f"pasted {name}")
    grid = pasted["entityId"]
    last = game.last_node_id()
    game.api.character_grid_event(grid, (0, 1, 1), "raze")
    game.wait_recorded(last, "removed 1 block")

    assert game2.api.set_admin_flag("creativeTools", True)["creativeToolsEnabled"]
    try:
        wait_until(lambda: named(game2, name), "the grid on the other client")
        game2.api.close_grid(grid)
        wait_until(lambda: not named(game, name), "the grid gone")
    finally:
        game2.api.set_admin_flag("creativeTools", False)

    assert (
        game.undo(expect="Undo not available")
        == "Undo not available: the grid no longer exists"
    )


# --- the grid history dialog -----------------------------------------------------


def open_dialog(game) -> int:
    rig.focus_gameplay(game.api)
    game.api.key("H", ["LeftControl"])
    screen = wait_until(lambda: dialog(game.api), "the dialog")["index"]
    wait_until(lambda: shown(game)["rows"], "the rows from the server")
    time.sleep(1)  # the opening transition takes no input
    return screen


def select(game, screen: int, name: str) -> None:
    names = [row[1] for row in shown(game)["rows"]]
    game.api.control_set("GridHistoryTable", names.index(name), screen=screen)


def test_the_dialog_pastes_and_deletes_backups_on_the_server(game):
    """Paste fetches the backup from the server in parts and puts it on the
    clipboard, a click places it; Delete removes the row on the server"""
    api, name = game.api, "Undo Dialog Grid"
    _, forward, up = rig.station_frame()
    last = game.last_node_id()
    api.paste_blueprint(
        xml=rig.blueprint_xml(name, PASTE_CELLS),
        position=rig.station_point((24, 1, 9)),
        forward=forward,
        up=up,
    )
    game.wait_recorded(last, f"pasted {name}")
    game.undo()
    wait_until(lambda: not named(game, name), "the pasted grid to go")

    teleport(api, VANTAGE)
    api.character_look_at(*rig.station_point(OPEN_AIR))
    screen = open_dialog(game)
    select(game, screen, name)
    api.control_click(text="Paste", screen=screen)
    wait_until(lambda: not dialog(api), "the dialog to close")
    last = game.last_node_id()
    time.sleep(1)  # the preview follows the camera from the next frames
    click(api)
    wait_until(lambda: named(game, name), "the grid pasted from the history")
    game.wait_recorded(last, f"pasted {name}")
    game.undo()
    wait_until(lambda: not named(game, name), "the paste undone")

    screen = open_dialog(game)
    count = len(shown(game)["rows"])
    select(game, screen, name)
    api.control_click(text="Delete", screen=screen)
    box = wait_until(lambda: message_box(api), "the delete confirmation")
    api.control_click(text="Yes", screen=box["index"])
    wait_until(lambda: len(shown(game)["rows"]) == count - 1, "one row less")
    rig.focus_gameplay(api)


# --- saving ----------------------------------------------------------------------


def test_the_autosave_writes_the_histories(game):
    """The server writes UndoPlayers.xml.gz whenever it saves the world"""
    path = ds_rig.WORLD / "UndoPlayers.xml.gz"
    label = game.build()["nodes"][-1]["label"]
    since = time.time()
    wait_until(
        lambda: path.exists() and path.stat().st_mtime > since,
        "the autosave",
        timeout=90,
        interval=2,
    )
    text = gzip.decompress(path.read_bytes()).decode("utf-8")
    assert str(ds_rig.CLIENT_ID) in text and label in text

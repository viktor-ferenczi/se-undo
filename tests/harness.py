"""What the tests use to look at the game and at the plugin's status file"""

from __future__ import annotations

import json
import math
import re
import time
import xml.etree.ElementTree as ET
from contextlib import contextmanager

import rig
from se_remote import CallOp, GetOp

# Degrees, for Remote's look-at where a test aims at one block (SE1-0080)
AIM_TOLERANCE = 0.5


class Game:
    def __init__(self, api, status_file=rig.STATUS_FILE):
        self.api = api
        self.status_file = status_file
        # A test file that changes the key bindings in its UNDO_CONFIG sets these
        self.undo_key = ("Z", ["LeftControl"])
        self.redo_key = ("Y", ["LeftControl"])
        self.station = self.grid_named(rig.STATION_NAME)["entityId"]
        # The world folder the client saves into, for saved_grid
        self.world = rig.WORLD

    # --- world state ---------------------------------------------------------

    def grids(self) -> list[dict]:
        return self.api.list_grids()

    def grid_named(self, name: str) -> dict:
        return next(g for g in self.grids() if g.get("name") == name)

    def cubes(self, grid=None) -> dict[tuple, dict]:
        """Every block of the grid by its min cell, armor included"""
        result = self.api.batch(
            gets=[GetOp.cube_list(grid or self.station, limit=5000)]
        )
        cubes = result.get(0)
        cubes = cubes.get("cubes", cubes) if isinstance(cubes, dict) else cubes
        return {tuple(c["cellMin"]): c for c in cubes}

    def color(self, pos, grid=None) -> tuple:
        hsv = self.cubes(grid)[tuple(pos)]["colorMaskHsv"]
        return tuple(round(float(c), 3) for c in hsv)

    def exists(self, pos, grid=None) -> bool:
        result = self.api.call([CallOp.cube_exists(grid or self.station, pos)])
        return result.call(0)["exists"]

    def block(self, pos, grid=None) -> dict | None:
        result = self.api.batch(gets=[GetOp.block(grid or self.station, pos)])
        try:
            return result.get(0)
        except Exception:  # noqa: BLE001 -- NOT_FOUND
            return None

    def build_ratio(self, pos, grid=None) -> float:
        """How far the block at this cell is built, 1 when complete"""
        grid = grid or self.station
        result = self.api.batch(gets=[GetOp.cube_list(grid, limit=1000)])
        cubes = result.get(0)
        cubes = cubes.get("cubes", cubes) if isinstance(cubes, dict) else cubes
        cube = next(c for c in cubes if tuple(c["cellMin"]) == tuple(pos))
        return float(cube["buildLevelRatio"])

    def grids_named(self, name: str) -> list[dict]:
        return [g for g in self.grids() if g.get("name") == name]

    def build_block(self, cell, grid=None, subtype="LargeBlockArmorBlock") -> dict:
        """Places one block through the cube builder's request; returns its node"""
        last = self.last_node_id()
        sent = self.api.character_build_block(
            grid or self.station, cell, subtype=subtype
        )
        assert sent["sent"], sent
        wait_until(lambda: self.exists(cell, grid), f"the block at {cell}")
        return self.wait_recorded(last, "placed 1 block")

    def raze_block(self, cell, grid=None, label="removed 1 block") -> dict:
        last = self.last_node_id()
        self.api.character_grid_event(grid or self.station, cell, "raze")
        wait_until(lambda: not self.exists(cell, grid), f"the removal at {cell}")
        return self.wait_recorded(last, label)

    def paste(
        self, name: str, cells, at, static: bool = True, blocks: str = ""
    ) -> dict:
        """Pastes a one grid blueprint of armor blocks at a station cell, upright
        like the station; returns the pasted grid"""
        _, forward, up = rig.station_frame()
        last = self.last_node_id()
        (pasted,) = self.api.paste_blueprint(
            xml=rig.blueprint_xml(name, cells, static=static, blocks=blocks),
            position=rig.station_point(at),
            forward=forward,
            up=up,
        )
        self.wait_recorded(last, f"pasted {name}")
        return pasted

    # --- plugin state --------------------------------------------------------

    def status(self) -> dict:
        for _ in range(20):
            try:
                return json.loads(self.status_file.read_text(encoding="utf-8"))
            except (OSError, ValueError):  # written while read
                time.sleep(0.05)
        raise AssertionError("status.json is not readable")

    def build(self) -> dict:
        return self.status()["histories"]["build"]

    def terminal(self) -> dict:
        return self.status()["histories"]["terminal"]

    def last_message(self) -> str:
        return self.status().get("lastMessage") or ""

    # --- input ---------------------------------------------------------------

    def undo(self, expect: str | None = "Undo: ") -> str:
        return self._press(self.undo_key, expect)

    def redo(self, expect: str | None = "Redo: ") -> str:
        return self._press(self.redo_key, expect)

    def _press(self, binding: tuple, expect: str | None) -> str:
        # Each press ends with a new notification, which also rewrites the status
        key, modifiers = binding
        before = self.status()
        marker = self.status_file.stat().st_mtime_ns
        self.api.key(key, modifiers)

        # On a served client other writes can come first, like the server's
        # "nothing to undo" state after a step was recorded
        def answered() -> bool:
            if self.status_file.stat().st_mtime_ns == marker:
                return False
            return expect is None or self.last_message().startswith(expect)

        try:
            wait_until(answered, f"reaction to {'+'.join(modifiers + [key])}")
        except AssertionError:
            if self.status_file.stat().st_mtime_ns == marker:
                raise
        message = self.last_message()
        if expect is not None:
            assert message.startswith(
                expect
            ), f"{message!r} after {before['lastMessage']!r}"
        return message

    def wait_recorded(self, after_id: int, label: str, history: str = "build") -> dict:
        """Waits for a node newer than after_id with this label, returns it"""

        def newest():
            nodes = self.status()["histories"][history]["nodes"]
            return nodes[-1] if nodes and nodes[-1]["id"] > after_id else None

        node = wait_until(
            lambda: (n := newest()) and n["label"] == label and n, f"node {label!r}"
        )
        return node

    def last_node_id(self, history: str = "build") -> int:
        nodes = self.status()["histories"][history]["nodes"]
        return nodes[-1]["id"] if nodes else 0

    def nothing_to_undo(self) -> None:
        """The undo key in gameplay with an empty build history: the plugin stays
        out of it, no notification, and the game gets the key"""
        assert self.build()["current"] == 0
        self.quiet(lambda: self.api.key(*self.undo_key))

    def vanilla_keys(self) -> None:
        """Ctrl-Z is relative dampeners, and Ctrl-H does not open the grid history.
        Works without a status file, so on a client too."""
        api = self.api

        def dampeners():
            return api.get_character()["dampeners"]

        if dampeners():
            api.key("Z")
            wait_until(lambda: not dampeners(), "dampeners off")
        api.key("Z", ["LeftControl"])
        wait_until(dampeners, "relative dampeners on Ctrl-Z")

        api.key("H", ["LeftControl"])
        time.sleep(1)
        assert self.screen("GridHistoryScreen") is None
        # Vanilla toggles the render profiler on it; once more puts it back
        api.key("H", ["LeftControl"])

    def quiet(self, press, wait: float = 0.7) -> None:
        """Runs press, which must not make the plugin react: no status write"""
        marker = self.status_file.stat().st_mtime_ns
        message = self.last_message()
        press()
        time.sleep(wait)
        assert self.status_file.stat().st_mtime_ns == marker, self.last_message()
        assert self.last_message() == message

    def labels(self, after_id: int = 0, history: str = "build") -> list[str]:
        nodes = self.status()["histories"][history]["nodes"]
        return [n["label"] for n in nodes if n["id"] > after_id]

    # --- character -----------------------------------------------------------

    def leave_seat(self) -> None:
        api = self.api
        if api.get_character()["state"] == "sitting":
            api.key("F")
            wait_until(
                lambda: api.get_character()["state"] != "sitting", "leaving the seat"
            )

    def stand_at(self, cell, tolerance: float = 1.5) -> None:
        """Teleports the character to a station cell"""
        rig.focus_gameplay(self.api)
        self.leave_seat()
        target = rig.station_point(cell)
        self.api.character_teleport(*target)
        wait_until(
            lambda: math.dist(self.api.get_character()["position"], target) < tolerance,
            f"the teleport to {cell}",
        )

    def aim(self, cell) -> dict:
        """Looks at a point given in station cells, returns the block under the
        crosshair. Half a degree of tolerance puts the view within a quarter of a
        block at the cube builder's reach; the tests still take the cell from the
        answer, a point on an edge can go either way."""
        point = rig.station_point(cell)
        target = None
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline:
            self.api.character_look_at(*point, tolerance=AIM_TOLERANCE)
            time.sleep(0.2)
            target = self.api.get_character_target(max_distance=100).get("block")
            if target and all(abs(m - c) <= 1.5 for m, c in zip(target["min"], cell)):
                return target
        raise AssertionError(f"The view did not settle on {cell}: {target}")

    def mouse(self, button: str = "left", keys=(), hold: float = 0.5) -> None:
        """A mouse button press as gameplay input; the GUI click endpoint does not
        reach the cube builder or the clipboard. Held for several frames: with a
        few clients on one machine a frame can take a while, and a press shorter
        than a frame is lost."""
        self.api.set_input_state(
            mode="override", keys=list(keys) or None, **{f"mouse_{button}": True}
        )
        time.sleep(hold)
        self.api.clear_input_state()

    def cube_builder(self, definition: str = "LargeBlockArmorBlock") -> None:
        """Takes the block into the hand through toolbar slot 1"""
        api = self.api
        if "/" not in definition:
            definition = f"MyObjectBuilder_CubeBlock/{definition}"
        api.set_toolbar_slot(0, definition)
        api.key("D1")
        wait_until(
            lambda: (api.get_character().get("weapon") or {}).get("type")
            == "MyCubePlacer",
            "the cube builder",
        )
        time.sleep(0.5)  # the gizmo follows the camera from the next frame

    # --- terminal ------------------------------------------------------------

    def terminal_index(self) -> int | None:
        return next(
            (
                i
                for i, s in enumerate(self.api.list_screens())
                if s.get("type") == "MyGuiScreenTerminal" and s.get("hasFocus")
            ),
            None,
        )

    def focus_is_text(self) -> bool:
        return self.api.get_focus()["control"]["type"] == "MyGuiControlTextbox"

    @contextmanager
    def open_terminal(self, aim, leave_search_box: bool = True):
        """The terminal of the block the character looks at, opened with F and
        closed again afterwards. aim is a point in station cells.

        It opens with the cursor in the block search box, where Ctrl-Z and Ctrl-Y
        belong to that box. Tab moves the focus on to the block list, so the keys
        reach the terminal history."""
        api = self.api
        api.character_look_at(*rig.station_point(aim))

        def opened():
            if self.terminal_index() is None:
                api.key("F")
                time.sleep(0.5)
            return self.terminal_index() is not None

        wait_until(opened, "the terminal", interval=0.5)
        # Screens take no input while their opening transition runs
        time.sleep(1)
        try:
            assert self.focus_is_text()
            if leave_search_box:
                api.key("Tab")
                wait_until(
                    lambda: not self.focus_is_text(), "the focus to leave the box"
                )
            yield self.terminal_index()
        finally:
            rig.focus_gameplay(api)

    def control(self, screen: int, name: str | None = None, ident: str | None = None):
        """A control of the screen by name, or by its Remote id where names repeat"""
        found = []

        def walk(node):
            if node.get("id") == ident if ident else node.get("name") == name:
                found.append(node)
            for child in node.get("controls") or node.get("children") or []:
                walk(child)

        tree = self.api.get_controls(screen, depth=12)
        for node in tree if isinstance(tree, list) else tree.get("controls", []):
            walk(node)
        assert found, f"no control {name or ident}"
        return found[0]

    def screen(self, kind: str) -> dict | None:
        return next((s for s in self.api.list_screens() if s.get("type") == kind), None)

    # --- saved world ---------------------------------------------------------

    def saved_station(self) -> ET.Element:
        """Saves the world and returns the station's grid element from the sector file"""
        return self.saved_grid(self.station)

    def saved_grid(self, grid: int) -> ET.Element:
        """Saves the world and returns a grid's element from the sector file"""
        sector = self.world / "SANDBOX_0_0_0_.sbs"
        before = sector.stat().st_mtime_ns

        def started() -> bool:
            try:
                self.api.save()
            except Exception as err:  # noqa: BLE001 -- 409 while a save is running
                if "409" not in str(err):
                    raise
                return False
            return True

        wait_until(started, "the save to start", interval=0.5)
        wait_until(lambda: sector.stat().st_mtime_ns != before, "the save", timeout=60)
        # The file is written in place; wait until it is complete
        wait_until(
            lambda: sector.read_bytes().rstrip().endswith(b"</MyObjectBuilder_Sector>"),
            "a complete save",
        )
        return station_element(sector, grid)


def station_element(sector, station_id: int) -> ET.Element:
    """A grid's element from a sector file saved as XML"""
    text = sector.read_text(encoding="utf-8")
    start = text.index(f"<EntityId>{station_id}</EntityId>")
    start = text.rindex("<MyObjectBuilder_EntityBase", 0, start)
    end = text.index("</MyObjectBuilder_EntityBase>", start) + len(
        "</MyObjectBuilder_EntityBase>"
    )
    chunk = re.sub(r"xsi:type=\"[^\"]+\"", "", text[start:end])
    chunk = re.sub(r"xsi:nil=\"true\"", "", chunk)
    return ET.fromstring(chunk)


def links(station: ET.Element) -> dict:
    """Where the station's blocks point at the target light, from a saved sector"""
    target = str(rig.IDS[rig.TARGET])
    slots = [e.text for e in station.iter("BlockEntityId")]
    tools = [e.text for e in station.iter("ToolIds") for e in e]
    selected = [e.text for e in station.iter("SelectedBlocks") for e in e]
    groups = {
        g.findtext("Name"): {
            (int(v.findtext("X")), int(v.findtext("Y")), int(v.findtext("Z")))
            for v in g.iter("Vector3I")
        }
        for g in station.iter("MyObjectBuilder_BlockGroup")
    }
    return {
        "toolbar": target in slots,
        "turret": target in tools,
        "event": target in selected,
        "group": rig.TARGET in groups.get(rig.GROUP_NAME, set()),
    }


def wait_until(predicate, what: str, timeout: float = 20.0, interval: float = 0.1):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        result = predicate()
        if result:
            return result
        time.sleep(interval)
    raise AssertionError(f"Timed out waiting for {what}")

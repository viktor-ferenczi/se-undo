"""What the tests use to look at the game and at the plugin's status file"""

from __future__ import annotations

import json
import re
import time
import xml.etree.ElementTree as ET

import rig
from se_remote import CallOp, GetOp


class Game:
    def __init__(self, api, status_file=rig.STATUS_FILE):
        self.api = api
        self.status_file = status_file
        self.station = self.grid_named(rig.STATION_NAME)["entityId"]

    # --- world state ---------------------------------------------------------

    def grids(self) -> list[dict]:
        return self.api.list_grids()

    def grid_named(self, name: str) -> dict:
        return next(g for g in self.grids() if g.get("name") == name)

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
        return self._press("Z", expect)

    def redo(self, expect: str | None = "Redo: ") -> str:
        return self._press("Y", expect)

    def _press(self, key: str, expect: str | None) -> str:
        # Each press ends with a new notification, which also rewrites the status
        before = self.status()
        marker = self.status_file.stat().st_mtime_ns
        self.api.key(key, ["LeftControl"])
        wait_until(
            lambda: self.status_file.stat().st_mtime_ns != marker,
            f"reaction to Ctrl-{key}",
        )
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

    # --- saved world ---------------------------------------------------------

    def saved_station(self) -> ET.Element:
        """Saves the world and returns the station's grid element from the sector file"""
        sector = rig.WORLD / "SANDBOX_0_0_0_.sbs"
        before = sector.stat().st_mtime_ns
        self.api.save()
        wait_until(lambda: sector.stat().st_mtime_ns != before, "the save", timeout=60)
        # The file is written in place; wait until it is complete
        wait_until(
            lambda: sector.read_bytes().rstrip().endswith(b"</MyObjectBuilder_Sector>"),
            "a complete save",
        )
        return station_element(sector, self.station)


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

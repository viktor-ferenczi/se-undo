"""Session setup: one isolated client with the test world loaded for the whole run.

UNDO_ATTACH=1 reuses a client of this rig that is already in the test world, and
UNDO_KEEP=1 leaves the client running after the run; both help while iterating.
"""

from __future__ import annotations

import json
import os
import re
import time
import xml.etree.ElementTree as ET

import pytest

import rig
from se_remote import CallOp, GetOp


class Game:
    def __init__(self, api):
        self.api = api
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

    # --- plugin state --------------------------------------------------------

    @staticmethod
    def status() -> dict:
        for _ in range(20):
            try:
                return json.loads(rig.STATUS_FILE.read_text(encoding="utf-8"))
            except (OSError, ValueError):  # written while read
                time.sleep(0.05)
        raise AssertionError("status.json is not readable")

    def build(self) -> dict:
        return self.status()["histories"]["build"]

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
        marker = rig.STATUS_FILE.stat().st_mtime_ns
        self.api.key(key, ["LeftControl"])
        wait_until(
            lambda: rig.STATUS_FILE.stat().st_mtime_ns != marker,
            f"reaction to Ctrl-{key}",
        )
        message = self.last_message()
        if expect is not None:
            assert message.startswith(
                expect
            ), f"{message!r} after {before['lastMessage']!r}"
        return message

    def wait_idle(self):
        wait_until(lambda: not self.build()["locked"], "the history to unlock")

    def wait_recorded(self, count_before: int, label: str) -> dict:
        """Waits for a new node with this label, returns it"""

        def newest():
            nodes = self.build()["nodes"]
            return nodes[-1] if nodes and nodes[-1]["id"] > count_before else None

        node = wait_until(
            lambda: (n := newest()) and n["label"] == label and n, f"node {label!r}"
        )
        return node

    def last_node_id(self) -> int:
        nodes = self.build()["nodes"]
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
        text = sector.read_text(encoding="utf-8")
        start = text.index(f"<EntityId>{self.station}</EntityId>")
        start = text.rindex("<MyObjectBuilder_EntityBase", 0, start)
        end = text.index("</MyObjectBuilder_EntityBase>", start) + len(
            "</MyObjectBuilder_EntityBase>"
        )
        chunk = re.sub(r"xsi:type=\"[^\"]+\"", "", text[start:end])
        chunk = re.sub(r"xsi:nil=\"true\"", "", chunk)
        return ET.fromstring(chunk)


def wait_until(predicate, what: str, timeout: float = 20.0, interval: float = 0.1):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        result = predicate()
        if result:
            return result
        time.sleep(interval)
    raise AssertionError(f"Timed out waiting for {what}")


@pytest.fixture(scope="session")
def game():
    api = rig.api()
    attach = os.environ.get("UNDO_ATTACH") == "1" and rig.running_pid()
    try:
        if not attach:
            rig.stop()
            rig.prepare_world()
            rig.launch()
            api.wait_for_api(max_wait=240)
            rig.load_world(api)
        rig.ensure_character(api)
        rig.focus_gameplay(api)
        yield Game(api)
    finally:
        if os.environ.get("UNDO_KEEP") != "1":
            rig.stop()

"""A client without Undo on a server with the companion. In a file of its own: a
client that left the server and joins again with the same id within seconds timed out
during the world download in the rig (DirectTransport), so this client joins once.
"""

from __future__ import annotations

import os

import pytest

import ds_rig
import rig
from harness import wait_until


def _quietly(predicate) -> bool:
    try:
        return predicate()
    except AssertionError:
        return False


@pytest.mark.skipif(os.environ.get("UNDO_ATTACH") == "1", reason="starts a client")
def test_a_client_without_undo(game):
    """The second client joins without the plugin. The server records nothing for
    it, and what it builds reaches the first client as usual."""
    api = ds_rig.start_client(
        client=ds_rig.CLIENT2,
        client_id=ds_rig.CLIENT2_ID,
        name=ds_rig.CLIENT2_NAME,
        plugins=("remote", "direct-transport"),
    )
    try:
        log = ds_rig.CLIENT2.appdata / "SpaceEngineers.log"
        assert "Undo:" not in log.read_text(errors="replace")
        assert ds_rig.server_player(ds_rig.CLIENT2_ID) is None
        assert api.set_admin_flag("creativeTools", True)["creativeToolsEnabled"]
        cell = rig.BUILD_CELL
        station = next(
            g["entityId"] for g in api.list_grids() if g["name"] == rig.STATION_NAME
        )

        # The server refuses the build in survival until the creative tools flag
        # reached it, so it is sent again until the block is there
        def built() -> bool:
            api.character_build_block(station, cell)
            return wait_until(lambda: game.exists(cell), "the block", timeout=2)

        wait_until(lambda: _quietly(built), "the block on the first client", timeout=30)
        assert ds_rig.server_player(ds_rig.CLIENT2_ID) is None
        api.character_grid_event(station, cell, "raze")
        wait_until(lambda: not game.exists(cell), "the block gone")
    finally:
        rig.stop(ds_rig.CLIENT2)

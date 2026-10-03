"""Dedicated server row of design section 13: the plugin is off on a client of a
server (design section 2). One client of a Magnetar server, joined as its
administrator with creative tools on, so only the session mode keeps undo off.
"""

from __future__ import annotations

import time

import ds_rig
import rig
from harness import wait_until


def test_the_plugin_is_off_on_a_server_client(game):
    log = ds_rig.LOG.read_text(errors="replace")
    assert "Undo: Info: Session mode: ServerClient" in log
    assert "Undo: Info: Not the host, undo is off in this world" in log


def test_a_removal_is_not_recorded_or_undone(game):
    cell = rig.TARGET
    game.api.character_grid_event(game.station, cell, "raze")
    wait_until(lambda: not game.exists(cell), "the removal")

    game.vanilla_keys()
    time.sleep(1)
    assert not game.exists(cell)
    # Nothing written: no status, no client history, no grid store
    assert not (ds_rig.CLIENT.appdata / "Undo").exists()

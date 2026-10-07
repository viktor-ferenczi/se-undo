"""A dedicated server without the Undo companion (design section 14): the plugin
stays off on its client. One client of a Magnetar server, joined as its
administrator with creative tools on, so only the missing companion keeps undo off.
"""

from __future__ import annotations

import time

import ds_rig
import rig
from harness import wait_until

COMPANION = False


def test_the_plugin_is_off_without_the_companion(game):
    wait_until(
        lambda: "does not run the Undo companion"
        in ds_rig.LOG.read_text(errors="replace"),
        "the handshake to time out",
        timeout=30,
    )
    log = ds_rig.LOG.read_text(errors="replace")
    assert "Undo: Info: Session mode: ServerClient" in log
    assert "Undo: Info: Asking the server for the Undo companion" in log
    assert (
        "Undo: Info: The server does not run the Undo companion, undo is off in this world"
        in log
    )


def test_a_removal_is_not_recorded_or_undone(game):
    cell = rig.TARGET
    game.api.character_grid_event(game.station, cell, "raze")
    wait_until(lambda: not game.exists(cell), "the removal")

    game.vanilla_keys()
    time.sleep(1)
    assert not game.exists(cell)
    # Nothing written: no status file, no grid store
    assert not (ds_rig.CLIENT.appdata / "Undo").exists()

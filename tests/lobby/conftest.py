"""Session setup of the lobby tests: the host loads the test world as a friends
game, then the second client joins it. Both stay up for the whole file.

UNDO_KEEP=1 leaves both running after the run, UNDO_ATTACH=1 reuses them while
iterating on one file.
"""

from __future__ import annotations

import os

import pytest

import lobby_rig  # first, it puts the other rigs on the path
import ds_rig
import rig
from harness import Game, wait_until

ATTACH = os.environ.get("UNDO_ATTACH") == "1"
KEEP = os.environ.get("UNDO_KEEP") == "1"


@pytest.fixture(scope="module")
def host():
    if ATTACH and rig.running_pid(lobby_rig.HOST):
        api = rig.api(lobby_rig.HOST)
    else:
        api = lobby_rig.start_host()
    try:
        game = Game(api, lobby_rig.HOST.status_file)
        game.world = lobby_rig.WORLD
        yield game
    finally:
        if not KEEP:
            lobby_rig.stop()


@pytest.fixture(scope="module")
def joiner(host):
    """The joined player. Its histories are the host's, the answers its own."""
    if ATTACH and rig.running_pid(lobby_rig.JOIN):
        api = rig.api(lobby_rig.JOIN)
    else:
        api = lobby_rig.start_join()
    wait_until(
        lambda: any(g.get("name") == rig.STATION_NAME for g in api.list_grids()),
        "the station on the joined client",
        timeout=120,
    )
    return ds_rig.ServedGame(
        api, lobby_rig.JOIN, lobby_rig.JOIN_ID, lobby_rig.PLAYERS_STATUS
    )

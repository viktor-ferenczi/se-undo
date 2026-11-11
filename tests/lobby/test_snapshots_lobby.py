"""The snapshot tests of tests/test_snapshots.py for the player who joined a
friends game; the host takes the snapshots into the grid store it keeps for that
player and restores them on request.
"""

from __future__ import annotations

import pytest

import lobby_rig
from test_snapshots import *  # noqa: F401,F403 -- the tests, their fixtures and UNDO_CONFIG


@pytest.fixture(scope="module")
def game(joiner):
    return joiner


def SNAPSHOT_FOLDERS():
    return (lobby_rig.HOST.appdata / "Undo" / "Worlds").glob(
        f"*/players/{lobby_rig.JOIN_ID}/grids"
    )


def SNAPSHOT_LOG():
    return lobby_rig.HOST.appdata / "SpaceEngineers.log"

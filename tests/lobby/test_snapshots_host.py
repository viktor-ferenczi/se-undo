"""The snapshot tests of tests/test_snapshots.py for the host of a friends game,
whose restores the joined client sees too.
"""

from __future__ import annotations

import pytest

import lobby_rig
from test_snapshots import *  # noqa: F401,F403 -- the tests, their fixtures and UNDO_CONFIG


@pytest.fixture(scope="module")
def game(host):
    return host


def SNAPSHOT_FOLDERS():
    return (lobby_rig.HOST.appdata / "Undo" / "Worlds").glob("*/grids")


def SNAPSHOT_LOG():
    return lobby_rig.HOST.appdata / "SpaceEngineers.log"

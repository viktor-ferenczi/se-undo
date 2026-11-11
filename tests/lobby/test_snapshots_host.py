"""The snapshot tests of tests/test_snapshots.py for the host of a friends game,
whose restores the joined client sees too.
"""

from __future__ import annotations

import pytest

import lobby_rig
import test_snapshots
from test_snapshots import *  # noqa: F401,F403 -- the tests, their fixture and UNDO_CONFIG


@pytest.fixture(scope="module")
def game(host):
    return host


test_snapshots.SNAPSHOTS.update(
    folders=lambda: (lobby_rig.HOST.appdata / "Undo" / "Worlds").glob("*/grids"),
    log=lambda: lobby_rig.HOST.appdata / "SpaceEngineers.log",
)

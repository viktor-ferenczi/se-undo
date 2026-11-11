"""The snapshot tests of tests/test_snapshots.py on a client of a dedicated server
with the companion. The server takes the snapshots before the player's edits,
into that player's grid store, and the dialog's Restore asks the server for it.
"""

from __future__ import annotations

import ds_rig
import test_snapshots
from test_snapshots import *  # noqa: F401,F403 -- the tests and their fixtures

SERVER_UNDO_CONFIG = {
    "GridSnapshotIntervalS": str(test_snapshots.INTERVAL_S),
    "GridSnapshotsPerGrid": str(test_snapshots.KEEP),
    "GridStoreBudgetPerPlayerMb": "64",
}


def SNAPSHOT_FOLDERS():
    return (ds_rig.SERVER_DATA / "Undo" / "Worlds").glob(
        f"*/players/{ds_rig.CLIENT_ID}/grids"
    )


def SNAPSHOT_LOG():
    return ds_rig.SERVER_DATA / "SpaceEngineersDedicated.log"

"""The Paint Replacer tests of tests/test_paint_replacer.py on a client of a
dedicated server with the Undo companion. Paint Replacer runs on the client only;
Undo on the client passes its RecordPaint call on to the companion, which records
the repaint the client's paint requests make on the server.
"""

from __future__ import annotations

import ds_rig
import test_paint_replacer
from test_paint_replacer import *  # noqa: F401,F403 -- the tests, fixtures and PLUGINS

SERVER_UNDO_CONFIG = {
    "GridSnapshotIntervalS": str(test_paint_replacer.INTERVAL_S),
    "GridStoreBudgetPerPlayerMb": "64",
}


def STORE_FOLDERS():
    return list(
        (ds_rig.SERVER_DATA / "Undo" / "Worlds").glob(
            f"*/players/{ds_rig.CLIENT_ID}/grids"
        )
    )

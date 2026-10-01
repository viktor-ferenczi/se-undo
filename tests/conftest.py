"""Session setup: one isolated client with the test world loaded for the whole run.

UNDO_ATTACH=1 reuses a client of this rig that is already in the test world, and
UNDO_KEEP=1 leaves the client running after the run; both help while iterating.
"""

from __future__ import annotations

import os
import shutil

import pytest

import rig
from harness import Game


@pytest.fixture(scope="session")
def game():
    api = rig.api()
    attach = os.environ.get("UNDO_ATTACH") == "1" and rig.running_pid()
    try:
        if not attach:
            rig.stop()
            # The grid stores of the previous run, keyed by world folder and id
            shutil.rmtree(rig.APPDATA / "Undo" / "Worlds", ignore_errors=True)
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

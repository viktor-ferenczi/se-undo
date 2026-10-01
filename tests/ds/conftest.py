"""Session setup of the dedicated server tests: a fresh server with the client as
its administrator, and the client joined with creative tools switched on.

UNDO_KEEP=1 leaves the server and the client running after the run, UNDO_ATTACH=1
reuses them while iterating on the first tests.
"""

from __future__ import annotations

import os

import pytest

import ds_rig
import rig
from harness import Game

ATTACH = os.environ.get("UNDO_ATTACH") == "1"


@pytest.fixture(scope="session")
def server():
    if ATTACH and ds_rig.server_pid() and rig.running_pid(ds_rig.CLIENT):
        yield
        return
    ds_rig.stop_client()
    ds_rig.stop_server()
    ds_rig.clear_client_storage()
    ds_rig.prepare_server(admin=True)
    try:
        ds_rig.start_server()
        yield
    finally:
        if os.environ.get("UNDO_KEEP") != "1":
            ds_rig.stop_client()
            ds_rig.stop_server()


@pytest.fixture(scope="session")
def game(server):
    if ATTACH and rig.running_pid(ds_rig.CLIENT):
        api = rig.api(ds_rig.CLIENT)
    else:
        api = ds_rig.start_client()
    flag = api.set_admin_flag("creativeTools", True)
    assert flag["promoteLevel"] == "Owner" and flag["creativeToolsEnabled"]
    return Game(api, ds_rig.CLIENT.status_file)

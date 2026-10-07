"""Session setup of the dedicated server tests: a fresh server per test file, with
the client as its administrator and creative tools switched on.

A test file sets COMPANION = False for a server without the Undo companion. The
second client, without creative tools, joins only for the tests that ask for game2.

UNDO_KEEP=1 leaves the server and the clients running after the run, UNDO_ATTACH=1
reuses them while iterating on one file.
"""

from __future__ import annotations

import os

import pytest

import ds_rig
import rig
from harness import Game

ATTACH = os.environ.get("UNDO_ATTACH") == "1"
KEEP = os.environ.get("UNDO_KEEP") == "1"


class ServedGame(Game):
    """A client of the server. The histories are the server's, from its status
    file; the last message is what the companion answered, from the client's."""

    def __init__(self, api, client: rig.Client, steam_id: int):
        super().__init__(api, client.status_file)
        self.steam_id = steam_id

    def status(self) -> dict:
        status = super().status()
        player = ds_rig.server_player(self.steam_id)
        status["histories"] = (player or {}).get("histories") or {
            "build": {"nodes": [], "current": 0, "count": 0},
            "terminal": {"nodes": [], "current": 0, "count": 0},
        }
        return status


@pytest.fixture(scope="module")
def server(request):
    companion = getattr(request.module, "COMPANION", True)
    if ATTACH and ds_rig.server_pid():
        yield companion
        return
    ds_rig.stop_client()
    ds_rig.stop_server()
    ds_rig.clear_client_storage()
    ds_rig.prepare_server(admin=True, companion=companion)
    try:
        ds_rig.start_server()
        yield companion
    finally:
        if not KEEP:
            ds_rig.stop_client()
            ds_rig.stop_server()


def _join(client: rig.Client, start):
    if ATTACH and rig.running_pid(client):
        return rig.api(client)
    return start()


@pytest.fixture(scope="module")
def game(server):
    api = _join(ds_rig.CLIENT, ds_rig.start_client)
    flag = api.set_admin_flag("creativeTools", True)
    assert flag["promoteLevel"] == "Owner" and flag["creativeToolsEnabled"]
    if server:
        return ServedGame(api, ds_rig.CLIENT, ds_rig.CLIENT_ID)
    return Game(api, ds_rig.CLIENT.status_file)


@pytest.fixture(scope="module")
def game2(game):
    """The second client, an administrator without creative tools"""
    api = _join(ds_rig.CLIENT2, ds_rig.start_client2)
    return ServedGame(api, ds_rig.CLIENT2, ds_rig.CLIENT2_ID)

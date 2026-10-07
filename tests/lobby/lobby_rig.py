"""Lobby rig for the Undo tests: one headless client hosts the test world as a
friends game, another joins it, both over DirectTransport and without Steam
(SE1-0076, the workspace's se1/notes/direct-transport-lobby-rig). Both run Undo.
The host records and replays its own steps like offline, and serves the joined
player the way the companion does on a dedicated server (design section 14).

Host: Pulsar folder ~/.se-test/undo-lobby-host, user data
~/.se-test/undo-lobby-host-data, Remote port 24198, lobby port UDP 27131.
Joined client: ~/.se-test/undo-lobby-join, ~/.se-test/undo-lobby-join-data, Remote
port 24199. Both Pulsar folders are cloned from the dedicated server rig's client
(~/.se-test/undo-mp, Docs/TESTING.md), which has DirectTransport.

Also usable from the command line while iterating::

    uv run python tests/lobby/lobby_rig.py start
    uv run python tests/lobby/lobby_rig.py stop
"""

from __future__ import annotations

import os
import shutil
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "ds"))

import ds_rig  # noqa: E402
import rig  # noqa: E402

HOME = Path.home()

HOST = rig.Client(
    os.environ.get("UNDO_LOBBY_HOST_PULSAR_DIR", HOME / ".se-test/undo-lobby-host"),
    "UndoLobbyHostInterim.bin",
    os.environ.get("UNDO_LOBBY_HOST_APPDATA", HOME / ".se-test/undo-lobby-host-data"),
    int(os.environ.get("UNDO_LOBBY_HOST_REMOTE_PORT", "24198")),
)
JOIN = rig.Client(
    os.environ.get("UNDO_LOBBY_JOIN_PULSAR_DIR", HOME / ".se-test/undo-lobby-join"),
    "UndoLobbyJoinInterim.bin",
    os.environ.get("UNDO_LOBBY_JOIN_APPDATA", HOME / ".se-test/undo-lobby-join-data"),
    int(os.environ.get("UNDO_LOBBY_JOIN_REMOTE_PORT", "24199")),
)
# From the block of ids this machine uses for test clients
HOST_ID = 76561199500000161
JOIN_ID = 76561199500000162
LOBBY_PORT = int(os.environ.get("UNDO_LOBBY_PORT", "27131"))

WORLD = HOST.appdata / "Saves" / str(HOST_ID) / "UndoLobby"
# The host's status of the players it serves, next to its own status.json
PLAYERS_STATUS = HOST.appdata / "Undo" / "status-players.json"


def log(client: rig.Client) -> str:
    path = client.appdata / "SpaceEngineers.log"
    return path.read_text(errors="replace") if path.exists() else ""


def start_host(timeout: float = 420.0):
    """The host loads a fresh creative test world offline, saves it, which writes
    the binary sector a bare XML world lacks, and loads it again as a friends game.
    Returns its API once its character stands in the world."""
    stop()
    for client in (HOST, JOIN):
        shutil.rmtree(client.appdata / "Undo", ignore_errors=True)
    rig.prepare_world(WORLD)
    # The joined player may teleport: an administrator, as the dedicated server rig's
    # clients are
    checkpoint = WORLD / "Sandbox.sbc"
    text = checkpoint.read_text(encoding="utf-8")
    promoted = (
        f"<PromotedUsers><dictionary><item><Key>{JOIN_ID}</Key><Value>Admin</Value>"
        "</item></dictionary></PromotedUsers></MyObjectBuilder_Checkpoint>"
    )
    checkpoint.write_text(
        text.replace("</MyObjectBuilder_Checkpoint>", promoted, 1), encoding="utf-8"
    )
    ds_rig.launch_client(
        HOST,
        [
            "--host-lobby",
            str(LOBBY_PORT),
            "--client-id",
            str(HOST_ID),
            "--client-name",
            "UndoHost",
        ],
    )
    api = rig.api(HOST)
    api.wait_for_api(max_wait=240)
    rig.load_world(api, WORLD, timeout)
    try:
        api.reload(save=True, online_mode="FRIENDS", max_players=4)
    except Exception as err:  # noqa: BLE001 -- the load outlives the HTTP timeout
        print(f"reload request returned early ({type(err).__name__})")
    deadline = time.monotonic() + timeout
    while api_state(api).get("multiplayer") != "lobby-host":
        if rig.running_pid(HOST) is None:
            raise RuntimeError(f"The host exited, see {HOST.launch_log}")
        if time.monotonic() > deadline:
            raise TimeoutError("The host did not start hosting")
        time.sleep(1)
    rig.wait_world(api, timeout)
    rig.ensure_character(api)
    rig.focus_gameplay(api)
    return api


def api_state(api) -> dict:
    try:
        return api.get_state()
    except Exception:  # noqa: BLE001 -- the API times out while a world loads
        return {}


def start_join(timeout: float = 420.0):
    """The joined client: joins at its main menu, respawns at the Earth base"""
    ds_rig.launch_client(
        JOIN,
        [
            "--join-lobby",
            f"127.0.0.1:{LOBBY_PORT}",
            "--client-id",
            str(JOIN_ID),
            "--client-name",
            "UndoJoiner",
        ],
    )
    api = rig.api(JOIN)
    api.wait_for_api(max_wait=240)
    ds_rig.wait_joined(api, timeout, JOIN)
    return api


def stop() -> None:
    rig.stop(JOIN)
    rig.stop(HOST)


if __name__ == "__main__":
    command = sys.argv[1] if len(sys.argv) > 1 else ""
    if command == "start":
        start_host()
        start_join()
        print(
            f"Joined. Remote API of the host on {HOST.port}, of the client {JOIN.port}"
        )
    elif command == "stop":
        stop()
    else:
        sys.exit(__doc__)

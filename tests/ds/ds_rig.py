"""Dedicated server rig for the Undo tests: a Magnetar server with DirectTransport
and one headless client that joins it, both isolated from everything else on the
machine (se/notes/game-test-instance-modes, mode A).

Server: Magnetar config folder and DS data folder under ~/.se-test/undo-ds, UDP port
27116. Client: Pulsar folder ~/.se-test/undo-mp with a renamed launcher, user data
~/.se-test/undo-mp-data, Remote port 24177. The client's Pulsar folder is created
once by hand, see Docs/TESTING.md; everything else is written here on each run.

Also usable from the command line while iterating::

    uv run python tests/ds/ds_rig.py start [--admin]   # server and client, joined
    uv run python tests/ds/ds_rig.py stop
"""

from __future__ import annotations

import os
import re
import shutil
import signal
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

import rig  # noqa: E402

HOME = Path.home()

# --- server ------------------------------------------------------------------

ROOT = Path(os.environ.get("UNDO_DS_ROOT", HOME / ".se-test/undo-ds"))
MAGNETAR = Path(os.environ.get("UNDO_MAGNETAR_DIR", HOME / ".config/Magnetar"))
MAGNETAR_TEMPLATE = MAGNETAR / "Magnetar"
DS64 = Path(
    os.environ.get(
        "UNDO_DS64",
        HOME
        / ".steam/debian-installation/steamapps/common"
        / "SpaceEngineersDedicatedServer/DedicatedServer64",
    )
)
DS_CONFIG_TEMPLATE = (
    HOME / ".config/SpaceEngineersDedicated/SpaceEngineers-Dedicated.cfg"
)
SERVER_PORT = int(os.environ.get("UNDO_DS_PORT", "27116"))
SERVER_CONFIG = ROOT / "magnetar"
SERVER_DATA = ROOT / "data"
SERVER_PID = ROOT / "server.pid"
SERVER_LOG = ROOT / "server.log"
WORLD_NAME = "UndoTestServer"
WORLD = SERVER_DATA / "Saves" / WORLD_NAME
SERVER_NAME = "Undo Test Server"

# --- client ------------------------------------------------------------------

CLIENT = rig.Client(
    os.environ.get("UNDO_MP_PULSAR_DIR", HOME / ".se-test/undo-mp"),
    "UndoMpInterim.bin",
    os.environ.get("UNDO_MP_APPDATA", HOME / ".se-test/undo-mp-data"),
    int(os.environ.get("UNDO_MP_REMOTE_PORT", "24177")),
)
# From the block of ids this machine uses for test clients
CLIENT_ID = 76561199500000131
CLIENT_NAME = "UndoTester"
CLIENT_PLUGINS = ("remote", "AC284074-A676-4930-B47A-F30450988608", "direct-transport")

UNDO_CONFIG = {
    "DebugStatusFile": "true",
    "RecordTerminalChangesOutsideTerminal": "true",
    "LogLevel": "Debug",
    # The client history is written this soon after a change
    "ClientAutosaveIntervalS": "5",
}

# A small station in the open air beside the test station, to copy with Ctrl-C
SOURCE_NAME = "Undo Copy Source"
SOURCE_ID = 777000555000201
SOURCE_CELLS = [(0, y, z) for y in (-1, 0, 1) for z in (-1, 0, 1)]
SOURCE_AT = (24, 1, 4)


def _profile(ids) -> str:
    folders = "".join(
        f"<LocalFolderConfig><Id>{i}</Id><DebugBuild>true</DebugBuild></LocalFolderConfig>"
        for i in ids
    )
    return (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        '<Profile xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
        'xmlns:xsd="http://www.w3.org/2001/XMLSchema">'
        f"<Name>Current</Name><GitHub /><DevFolder>{folders}</DevFolder>"
        "<Local /><Mods /></Profile>\n"
    )


def prepare_world() -> None:
    """The offline rig's test world in survival, plus the copy source"""
    rig.prepare_world(WORLD, mode="Survival")
    _, forward, up = rig.station_frame()
    source = rig._grid(
        SOURCE_NAME,
        SOURCE_ID,
        [rig._armor(p) for p in SOURCE_CELLS],
        rig.station_point(SOURCE_AT),
        forward,
        up,
        True,
        # Unsupported, a static grid out of voxel contact would turn into a ship
        extra="<IsUnsupportedStation>true</IsUnsupportedStation>",
    )
    sector = WORLD / "SANDBOX_0_0_0_.sbs"
    text = sector.read_text(encoding="utf-8")
    text = text.replace("</SectorObjects>", source + "</SectorObjects>", 1)
    # A joining client crashes in MyEventControllerBlock.ProcessSelectedBlocks when
    # the station's event controller arrives with a selected block (SE1-0074)
    text, count = re.subn(
        rf"<SelectedBlocks><long>{rig.IDS[rig.TARGET]}</long></SelectedBlocks>",
        "",
        text,
    )
    assert count == 1
    sector.write_text(text, encoding="utf-8")

    # In an offline world the game takes everyone for its owner
    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = WORLD / name
        text, count = re.subn(
            r"<OnlineMode>\w+</OnlineMode>",
            "<OnlineMode>PUBLIC</OnlineMode>",
            path.read_text(encoding="utf-8"),
        )
        assert count >= 1, name
        path.write_text(text, encoding="utf-8")


def prepare_server(admin: bool, fresh: bool = True) -> None:
    """Writes the server's Magnetar config and dedicated config. A fresh server
    also gets a new copy of the world; otherwise the world it saved is kept."""
    if fresh:
        prepare_world()

    # Magnetar: the plugin sources of the machine's own instance, with a profile
    # that enables DirectTransport only
    if not (SERVER_CONFIG / "Sources").exists():
        SERVER_CONFIG.mkdir(parents=True, exist_ok=True)
        shutil.copytree(MAGNETAR_TEMPLATE / "Sources", SERVER_CONFIG / "Sources")
        shutil.copy(MAGNETAR_TEMPLATE / "config.xml", SERVER_CONFIG / "config.xml")
    (SERVER_CONFIG / "Profiles").mkdir(exist_ok=True)
    (SERVER_CONFIG / "Profiles" / "Current.xml").write_text(
        _profile(["direct-transport"]), encoding="utf-8"
    )

    text = DS_CONFIG_TEMPLATE.read_text(encoding="utf-8")
    admins = f"<unsignedLong>{CLIENT_ID}</unsignedLong>" if admin else ""
    for tag, value in {
        "IP": "127.0.0.1",
        "ServerPort": SERVER_PORT,
        "SteamPort": SERVER_PORT + 1000,
        "Administrators": admins,
        "ServerName": SERVER_NAME,
        "WorldName": WORLD_NAME,
        "PauseGameWhenEmpty": "false",
        "AutoRestartEnabled": "false",
        "IgnoreLastSession": "true",
        "RemoteApiEnabled": "false",
        "LoadWorld": WORLD,
    }.items():
        text, count = re.subn(
            rf"<{tag}>.*?</{tag}>|<{tag} />",
            f"<{tag}>{value}</{tag}>",
            text,
            flags=re.S,
        )
        assert count == 1, tag
    SERVER_DATA.mkdir(parents=True, exist_ok=True)
    (SERVER_DATA / "SpaceEngineers-Dedicated.cfg").write_text(text, encoding="utf-8")


def server_pid() -> int | None:
    try:
        pid = int(SERVER_PID.read_text().strip())
        cmdline = Path(f"/proc/{pid}/cmdline").read_bytes()
    except (OSError, ValueError):
        return None
    return pid if str(SERVER_CONFIG).encode() in cmdline else None


def start_server(timeout: float = 300.0) -> None:
    if server_pid():
        raise RuntimeError(f"The test server is already running, pid {server_pid()}")
    log = open(SERVER_LOG, "w")
    process = subprocess.Popen(
        [
            str(MAGNETAR / "MagnetarInterim.bin"),
            "-multiInstance",
            "-stableLogs",
            "-noimplicitmod",
            "-consent",
            "deny",
            "-config",
            str(SERVER_CONFIG),
            "-ds64",
            str(DS64),
            "-path",
            str(SERVER_DATA),
        ],
        cwd=MAGNETAR,
        env={**os.environ, "SE_DIRECT_TRANSPORT": "1"},
        stdout=log,
        stderr=subprocess.STDOUT,
        stdin=subprocess.DEVNULL,
        start_new_session=True,
    )
    SERVER_PID.write_text(str(process.pid))

    deadline = time.monotonic() + timeout
    while "Game ready" not in SERVER_LOG.read_text(errors="replace"):
        if process.poll() is not None:
            raise RuntimeError(f"The server exited, see {SERVER_LOG}")
        if time.monotonic() > deadline:
            raise TimeoutError(f"The server did not get ready, see {SERVER_LOG}")
        time.sleep(1)


def stop_server(timeout: float = 120.0) -> None:
    """SIGTERM makes Magnetar save the world and quit"""
    pid = server_pid()
    if pid is None:
        return
    os.kill(pid, signal.SIGTERM)
    deadline = time.monotonic() + timeout
    while server_pid() is not None:
        if time.monotonic() > deadline:
            os.kill(pid, signal.SIGKILL)
            break
        time.sleep(1)
    SERVER_PID.unlink(missing_ok=True)


def start_client(timeout: float = 420.0):
    """Starts the client, which joins on its own, and returns its API once the
    character stands in the world"""
    if not CLIENT.launcher.exists():
        raise RuntimeError(f"{CLIENT.launcher} is missing, see Docs/TESTING.md")
    profile = CLIENT.pulsar / "Legacy" / "Profiles" / "Current.xml"
    profile.write_text(_profile(CLIENT_PLUGINS), encoding="utf-8")

    CLIENT.status_file.unlink(missing_ok=True)
    rig.launch(
        CLIENT,
        [
            "--connect",
            f"127.0.0.1:{SERVER_PORT}",
            "--client-id",
            str(CLIENT_ID),
            "--client-name",
            CLIENT_NAME,
        ],
        UNDO_CONFIG,
    )
    api = rig.api(CLIENT)
    api.wait_for_api(max_wait=240)
    wait_joined(api, timeout)
    return api


def wait_joined(api, timeout: float = 420.0) -> None:
    """The plugin writes its status file when the session starts"""
    deadline = time.monotonic() + timeout
    while not CLIENT.status_file.exists():
        if rig.running_pid(CLIENT) is None:
            raise RuntimeError(f"The client exited, see {CLIENT.launch_log}")
        if time.monotonic() > deadline:
            raise TimeoutError("The client did not join the server")
        time.sleep(1)
    rig.wait_world(api, timeout)
    spawn(api)
    rig.focus_gameplay(api)


def spawn(api, timeout: float = 300.0) -> None:
    """A new player gets the faction list, then the spawn points, whose first row
    is the Earth base's medical room. The API answers slowly while the base streams
    in, a minute can pass between the two clicks."""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            if api.get_character().get("state") not in (None, "dead"):
                return
        except Exception:  # noqa: BLE001 -- 503 without a character
            pass
        screens = api.list_screens()
        medical = next(
            (i for i, s in enumerate(screens) if "Medical" in s.get("type", "")), None
        )
        if medical is not None:
            for button in ("Join", "Respawn"):
                try:
                    api.control_click(text=button, screen=medical)
                    break
                except Exception:  # noqa: BLE001 -- the other page of the screen
                    continue
        time.sleep(3)
    raise TimeoutError("No live character")


def stop_client() -> None:
    rig.stop(CLIENT)


def clear_client_storage() -> None:
    """The histories and grid stores earlier runs left for this server"""
    shutil.rmtree(CLIENT.appdata / "Undo", ignore_errors=True)


if __name__ == "__main__":
    command = sys.argv[1] if len(sys.argv) > 1 else ""
    if command == "start":
        stop_client()
        stop_server()
        clear_client_storage()
        prepare_server(admin="--admin" in sys.argv)
        start_server()
        start_client()
        print(f"Joined. Remote API on port {CLIENT.port}, server log {SERVER_LOG}")
    elif command == "stop":
        stop_client()
        stop_server()
    else:
        sys.exit(__doc__)

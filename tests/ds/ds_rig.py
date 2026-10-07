"""Dedicated server rig for the Undo tests: a Magnetar server with DirectTransport
and the Undo companion, and up to two headless clients that join it, all isolated
from everything else on the machine (se/notes/game-test-instance-modes, mode A).

Server: Magnetar config folder and DS data folder under ~/.se-test/undo-ds, UDP port
27116. It compiles the companion from this repo (UndoServer.xml) as a dev folder.
Client: Pulsar folder ~/.se-test/undo-mp with a renamed launcher, user data
~/.se-test/undo-mp-data, Remote port 24177. The second client is cloned from it into
~/.se-test/undo-mp2, user data ~/.se-test/undo-mp2-data, Remote port 24178. The first
client's Pulsar folder is created once by hand, see Docs/TESTING.md; everything else
is written here on each run.

Also usable from the command line while iterating::

    uv run python tests/ds/ds_rig.py start [--admin] [--no-companion] [--two]
    uv run python tests/ds/ds_rig.py stop
"""

from __future__ import annotations

import json
import os
import re
import shutil
import signal
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

import rig  # noqa: E402
from harness import Game  # noqa: E402

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
UNDO_ID = "AC284074-A676-4930-B47A-F30450988608"
# The companion's config and its status file, under the server's instance folder
SERVER_UNDO_CONFIG = {"DebugStatusFile": "true", "LogLevel": "Debug"}
SERVER_STATUS = SERVER_DATA / "Undo" / "status-players.json"

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
LOG = CLIENT.appdata / "SpaceEngineers.log"
CLIENT_PLUGINS = ("remote", UNDO_ID, "direct-transport")

# The second client, an administrator without creative tools
CLIENT2 = rig.Client(
    os.environ.get("UNDO_MP2_PULSAR_DIR", HOME / ".se-test/undo-mp2"),
    "UndoMp2Interim.bin",
    os.environ.get("UNDO_MP2_APPDATA", HOME / ".se-test/undo-mp2-data"),
    int(os.environ.get("UNDO_MP2_REMOTE_PORT", "24178")),
)
CLIENT2_ID = 76561199500000132
CLIENT2_NAME = "UndoTester2"

UNDO_CONFIG = {
    "DebugStatusFile": "true",
    "RecordTerminalChangesOutsideTerminal": "true",
    "LogLevel": "Debug",
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


def register_source(sources: Path, name: str, folder: Path, file: str) -> None:
    """Points the loader's dev folder source of this name at a folder, adding it
    when missing. The rig's loaders compile Remote and Undo from these working trees."""
    tree = ET.parse(sources)
    local = tree.getroot().find("LocalPluginSources")
    if local is None:
        local = ET.SubElement(tree.getroot(), "LocalPluginSources")
    for plugin in local.findall("LocalPlugin"):
        if plugin.findtext("Name") == name:
            local.remove(plugin)
    plugin = ET.SubElement(local, "LocalPlugin")
    for tag, value in (
        ("Name", name),
        ("Folder", str(folder)),
        ("File", file),
        ("Enabled", "true"),
    ):
        ET.SubElement(plugin, tag).text = value
    tree.write(sources, encoding="utf-8", xml_declaration=True)


def prepare_world(settings: dict | None = None) -> None:
    """The offline rig's test world in survival, plus the copy source. settings
    changes session settings of the world, by element name."""
    rig.prepare_world(WORLD, mode="Survival", settings=settings)
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


def prepare_server(
    admin: bool, fresh: bool = True, companion: bool = True, settings=None
) -> None:
    """Writes the server's Magnetar config and dedicated config. A fresh server
    also gets a new copy of the world and starts with an empty grid store;
    otherwise the world it saved is kept. Without the companion the server runs
    DirectTransport only."""
    if fresh:
        prepare_world(settings)
        shutil.rmtree(SERVER_DATA / "Undo", ignore_errors=True)

    # Magnetar: the plugin sources of the machine's own instance, with a profile
    # that enables DirectTransport only
    if not (SERVER_CONFIG / "Sources").exists():
        SERVER_CONFIG.mkdir(parents=True, exist_ok=True)
        shutil.copytree(MAGNETAR_TEMPLATE / "Sources", SERVER_CONFIG / "Sources")
        shutil.copy(MAGNETAR_TEMPLATE / "config.xml", SERVER_CONFIG / "config.xml")
    register_source(
        SERVER_CONFIG / "Sources" / "sources.xml", "se-undo", rig.REPO, "UndoServer.xml"
    )
    (SERVER_CONFIG / "Profiles").mkdir(exist_ok=True)
    (SERVER_CONFIG / "Profiles" / "Current.xml").write_text(
        _profile(["direct-transport", UNDO_ID] if companion else ["direct-transport"]),
        encoding="utf-8",
    )

    text = DS_CONFIG_TEMPLATE.read_text(encoding="utf-8")
    # The second client is an administrator too, for the ownership test; it plays
    # without creative tools otherwise
    admins = (
        f"<unsignedLong>{CLIENT_ID}</unsignedLong><unsignedLong>{CLIENT2_ID}</unsignedLong>"
        if admin
        else ""
    )
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

    options = "".join(f"  <{k}>{v}</{k}>\n" for k, v in SERVER_UNDO_CONFIG.items())
    (SERVER_DATA / "Undo.cfg").write_text(
        '<?xml version="1.0" encoding="utf-8"?>\n'
        f"<UndoServerConfig>\n{options}</UndoServerConfig>\n",
        encoding="utf-8",
    )
    SERVER_STATUS.unlink(missing_ok=True)


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


def clone_client(client: rig.Client) -> None:
    """Clones the first client's Pulsar folder for another client, the way
    rig.ensure_pulsar clones client slots"""
    if client.launcher.exists():
        return
    if not CLIENT.launcher.exists():
        raise RuntimeError(f"{CLIENT.launcher} is missing, see Docs/TESTING.md")
    client.pulsar.mkdir(parents=True)
    for entry in CLIENT.pulsar.iterdir():
        target = client.pulsar / entry.name
        if entry.is_symlink():
            target.symlink_to(entry.resolve())
        elif entry.is_file():
            shutil.copy2(entry, target)
    shutil.copy2(CLIENT.launcher, client.launcher)
    shutil.copytree(
        CLIENT.pulsar / "Legacy",
        client.pulsar / "Legacy",
        ignore=shutil.ignore_patterns("Preloader", "info*.log"),
    )


def launch_client(
    client: rig.Client, args: list[str], plugins=CLIENT_PLUGINS, config=None
) -> None:
    """Launches a client with Remote and Undo compiled from the working copies.
    plugins are the dev folders its profile enables."""
    clone_client(client)
    legacy = client.pulsar / "Legacy"
    profile = legacy / "Profiles" / "Current.xml"
    profile.write_text(_profile(plugins), encoding="utf-8")
    sources = legacy / "Sources" / "sources.xml"
    register_source(sources, "remote", rig.REMOTE_REPO, "Remote.xml")
    register_source(sources, "se-undo", rig.REPO, "Undo.xml")

    (client.appdata / "SpaceEngineers.log").unlink(missing_ok=True)
    rig.launch(client, args, config or UNDO_CONFIG)


def start_client(
    timeout: float = 420.0,
    client: rig.Client = CLIENT,
    client_id: int = CLIENT_ID,
    name: str = CLIENT_NAME,
    plugins=CLIENT_PLUGINS,
):
    """Starts a client, which joins on its own, and returns its API once the
    character stands in the world"""
    launch_client(
        client,
        [
            "--connect",
            f"127.0.0.1:{SERVER_PORT}",
            "--client-id",
            str(client_id),
            "--client-name",
            name,
        ],
        plugins,
    )
    api = rig.api(client)
    api.wait_for_api(max_wait=240)
    wait_joined(api, timeout, client, undo=UNDO_ID in plugins)
    return api


def start_client2(timeout: float = 420.0):
    return start_client(timeout, CLIENT2, CLIENT2_ID, CLIENT2_NAME)


def joined(client: rig.Client = CLIENT) -> bool:
    """The plugin logs the session mode when the session starts"""
    log = client.appdata / "SpaceEngineers.log"
    return log.exists() and "Undo: Info: Session mode:" in log.read_text(
        errors="replace"
    )


def wait_joined(
    api, timeout: float = 420.0, client: rig.Client = CLIENT, undo: bool = True
) -> None:
    """A client without Undo is only waited for until its world is ready"""
    deadline = time.monotonic() + timeout
    while undo and not joined(client):
        if rig.running_pid(client) is None:
            raise RuntimeError(f"The client exited, see {client.launch_log}")
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


def server_player(steam_id: int, status: Path = SERVER_STATUS) -> dict | None:
    """The companion's status of one player, None before its handshake"""
    for _ in range(20):
        try:
            players = json.loads(status.read_text(encoding="utf-8"))
            return players["players"].get(str(steam_id))
        except FileNotFoundError:
            return None
        except ValueError:  # written while read
            continue
    raise AssertionError("status-players.json is not readable")


class ServedGame(Game):
    """A client of the server. The histories are the server's, from its status
    file of the players; the last message is what the companion answered, from
    the client's."""

    def __init__(
        self, api, client: rig.Client, steam_id: int, players: Path = SERVER_STATUS
    ):
        super().__init__(api, client.status_file)
        self.steam_id = steam_id
        self.players = players

    def status(self) -> dict:
        status = super().status()
        player = server_player(self.steam_id, self.players)
        status["histories"] = (player or {}).get("histories") or {
            "build": {"nodes": [], "current": 0, "count": 0},
            "terminal": {"nodes": [], "current": 0, "count": 0},
        }
        return status


def stop_client() -> None:
    rig.stop(CLIENT)
    rig.stop(CLIENT2)


def clear_client_storage() -> None:
    """What earlier runs left in the clients' storage folders: status files, and
    grid stores of the plugin versions that kept them on the client"""
    for client in (CLIENT, CLIENT2):
        shutil.rmtree(client.appdata / "Undo", ignore_errors=True)


if __name__ == "__main__":
    command = sys.argv[1] if len(sys.argv) > 1 else ""
    if command == "start":
        stop_client()
        stop_server()
        clear_client_storage()
        prepare_server(
            admin="--admin" in sys.argv, companion="--no-companion" not in sys.argv
        )
        start_server()
        start_client()
        if "--two" in sys.argv:
            start_client2()
        print(f"Joined. Remote API on port {CLIENT.port}, server log {SERVER_LOG}")
    elif command == "stop":
        stop_client()
        stop_server()
    else:
        sys.exit(__doc__)

"""Isolated headless client for the Undo tests.

Own Pulsar folder with the Remote and Undo plugins as dev folders, own game user
data folder (-appdata), own Remote port. The launcher is a renamed copy of
Interim.bin, so nothing that stops "Interim.bin" by name takes this client down.

Pulsar folder, created once by hand (see tests/README.md)::

    notes/pulsar-dev-instances/new-pulsar-instance.sh ~/.se-test/undo
    # profile trimmed to the remote and Undo dev folders, UndoInterim.bin copied
"""

from __future__ import annotations

import math
import os
import re
import shutil
import signal
import subprocess
import sys
import time
import zipfile
from pathlib import Path

HOME = Path.home()
REPO = Path(__file__).resolve().parent.parent
REMOTE_REPO = REPO.parent / "remote"

sys.path.insert(0, str(REMOTE_REPO / "skills" / "se-remote"))

from se_remote import RemoteAPI  # noqa: E402

PULSAR_DIR = Path(os.environ.get("UNDO_PULSAR_DIR", HOME / ".se-test/undo"))
LAUNCHER = PULSAR_DIR / "UndoInterim.bin"
APPDATA = Path(os.environ.get("UNDO_APPDATA", HOME / ".se-test/undo-data"))
PORT = int(os.environ.get("UNDO_REMOTE_PORT", "24176"))
BASE_URL = f"http://127.0.0.1:{PORT}"
PID_FILE = APPDATA / "game.pid"
LAUNCH_LOG = APPDATA / "launch.log"
GAME_LOG = APPDATA / "SpaceEngineers.log"
STATUS_FILE = APPDATA / "Undo" / "status.json"

GAME_ARGS = [
    "-multiInstance",
    "-lazySteam",
    "-noprompt",
    "-noupdate",
    "-nosplash",
    "-stablelogs",
    "-sources",
    "--no-steam",
    "-appdata",
    str(APPDATA),
    "--headless",
    "--quality",
    "minimal",
    "--resolution",
    "1280x720",
]

# Saves of the offline (no Steam) player
SAVES = APPDATA / "Saves" / "1234567891011"
WORLD_NAME = "UndoTestEarth"
WORLD = SAVES / WORLD_NAME
TEMPLATE_ZIP = REMOTE_REPO / "Worlds" / "RemoteAPITestEarthPlanet.zip"

# Undo options for the run. Everything else keeps its default. The tree option is
# on for the whole run, since the plugin reads its config once at start.
UNDO_CONFIG = {
    "DebugStatusFile": "true",
    "RecordTerminalChangesOutsideTerminal": "true",
    "UndoTree": "true",
    "LogLevel": "Debug",
}


# ---------------------------------------------------------------------------
# The test station, injected into the world file
# ---------------------------------------------------------------------------

STATION_NAME = "Undo Test Station"
STATION_ID = 777000555000001

# Large grid positions. The floor is x 0..15, z 0..15 at y 0. Functional blocks
# stand on it in the row z 15. Part B sits beyond a one block bridge at x 16, so
# removing the bridge splits it off. The world has unsupported stations enabled,
# so split parts stay static instead of falling onto the base below.
FLOOR = [(x, 0, z) for x in range(16) for z in range(16)]
BRIDGE = (16, 0, 8)
PART_B = [(x, 0, z) for x in range(17, 20) for z in range(7, 10)]

COCKPIT = (0, 1, 15)
TURRET_CONTROLLER = (2, 1, 15)
EVENT_CONTROLLER = (4, 1, 15)
TARGET = (6, 1, 15)
SECOND_LIGHT = (8, 1, 15)
PAINT_LIGHT = (10, 1, 15)
PART_B_LIGHT = (18, 1, 8)

IDS = {
    COCKPIT: 777000555000011,
    TURRET_CONTROLLER: 777000555000012,
    EVENT_CONTROLLER: 777000555000013,
    TARGET: 777000555000014,
    SECOND_LIGHT: 777000555000015,
    PART_B_LIGHT: 777000555000016,
    PAINT_LIGHT: 777000555000017,
}

GROUP_NAME = "Undo Group"
TARGET_NAME = "Undo Target"

# Free cells above the floor for the limits test: 15 x 14 = 210
LIMITS_AREA = [(x, 1, z) for z in range(14) for x in range(15)]
BUILD_CELL = (15, 1, 15)
TREE_CELLS = [(15, 1, 12), (15, 1, 13), (15, 1, 14)]
ALTITUDE_M = 300.0

# A dynamic ship far out in space. Its outer part carries a battery and a thruster
# that is off; the test switches the thruster on and removes the bridge, so the
# split off part is pushed away before the undo puts it back.
DRIFT_SHIP_NAME = "Undo Drift Ship"
DRIFT_SHIP_ID = 777000555000101
DRIFT_MAIN = [(x, 0, z) for x in range(3) for z in range(3)]
DRIFT_BRIDGE = (3, 0, 1)
DRIFT_PART = [(4, 0, 1), (4, 0, 2), (5, 0, 1), (5, 0, 2)]
DRIFT_THRUSTER = (4, 1, 1)
DRIFT_BATTERY = (5, 1, 1)
DRIFT_LIGHT = (5, 1, 2)
DRIFT_IDS = {
    DRIFT_THRUSTER: 777000555000102,
    DRIFT_BATTERY: 777000555000103,
    DRIFT_LIGHT: 777000555000104,
}
DRIFT_DISTANCE_M = 250000.0


def _vec(tag: str, pos) -> str:
    return f'<{tag} x="{pos[0]}" y="{pos[1]}" z="{pos[2]}" />'


def _block(
    xsi_type, subtype, pos, extra="", entity_id=None, forward="Forward", up="Up"
) -> str:
    entity_id = entity_id or IDS.get(pos)
    entity = f"<EntityId>{entity_id}</EntityId>" if entity_id else ""
    return (
        f'<MyObjectBuilder_CubeBlock xsi:type="{xsi_type}">'
        f"<SubtypeName>{subtype}</SubtypeName>{entity}{_vec('Min', pos)}"
        f'<BlockOrientation Forward="{forward}" Up="{up}" />'
        f'<ColorMaskHSV x="0" y="-0.8" z="0.55" />{extra}'
        f"</MyObjectBuilder_CubeBlock>"
    )


def _armor(pos) -> str:
    return _block("MyObjectBuilder_CubeBlock", "LargeBlockArmorBlock", pos)


def _light(pos, name: str, entity_id=None) -> str:
    # The light mounts with its back, which faces down this way
    return _block(
        "MyObjectBuilder_InteriorLight",
        "SmallLight",
        pos,
        f"<CustomName>{name}</CustomName><Enabled>true</Enabled>",
        entity_id=entity_id,
        forward="Up",
        up="Backward",
    )


def _toolbar_slot(index: int, entity_id: int) -> str:
    return (
        f"<Slot><Index>{index}</Index><Item />"
        f'<Data xsi:type="MyObjectBuilder_ToolbarItemTerminalBlock">'
        f"<Action>OnOff</Action><BlockEntityId>{entity_id}</BlockEntityId></Data></Slot>"
    )


def _group(name: str, cells) -> str:
    blocks = "".join(
        f"<Vector3I><X>{x}</X><Y>{y}</Y><Z>{z}</Z></Vector3I>" for x, y, z in cells
    )
    return f"<MyObjectBuilder_BlockGroup><Name>{name}</Name><Blocks>{blocks}</Blocks></MyObjectBuilder_BlockGroup>"


def _grid(name, entity_id, blocks, position, forward, up, static, extra="") -> str:
    return (
        '<MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_CubeGrid">'
        f"<SubtypeName /><EntityId>{entity_id}</EntityId>"
        "<PersistentFlags>CastShadows InScene</PersistentFlags>"
        "<PositionAndOrientation>"
        + _vec("Position", position)
        + _vec("Forward", forward)
        + _vec("Up", up)
        + "</PositionAndOrientation>"
        "<GridSizeEnum>Large</GridSizeEnum>"
        f"<CubeBlocks>{''.join(blocks)}</CubeBlocks>"
        f"<IsStatic>{'true' if static else 'false'}</IsStatic>"
        f"{extra}<DisplayName>{name}</DisplayName>"
        "<DestructibleBlocks>true</DestructibleBlocks>"
        "</MyObjectBuilder_EntityBase>"
    )


def station_xml(position, forward, up) -> str:
    blocks = [_armor(p) for p in FLOOR + [BRIDGE] + PART_B]
    blocks.append(
        _block(
            "MyObjectBuilder_Cockpit",
            "LargeBlockCockpit",
            COCKPIT,
            "<CustomName>Undo Cockpit</CustomName>"
            '<Toolbar><ToolbarType>Ship</ToolbarType><SelectedSlot xsi:nil="true" /><Slots>'
            + _toolbar_slot(0, IDS[TARGET])
            + _toolbar_slot(1, IDS[PART_B_LIGHT])
            + "</Slots></Toolbar>",
        )
    )
    blocks.append(
        _block(
            "MyObjectBuilder_TurretControlBlock",
            "LargeTurretControlBlock",
            TURRET_CONTROLLER,
            f"<CustomName>Undo Turret Controller</CustomName><ToolIds><long>{IDS[TARGET]}</long></ToolIds>",
        )
    )
    blocks.append(
        _block(
            "MyObjectBuilder_EventControllerBlock",
            "EventControllerLarge",
            EVENT_CONTROLLER,
            f"<CustomName>Undo Event Controller</CustomName><SelectedBlocks><long>{IDS[TARGET]}</long></SelectedBlocks>",
        )
    )
    blocks.append(_light(TARGET, TARGET_NAME))
    blocks.append(_light(SECOND_LIGHT, "Undo Light 2"))
    blocks.append(_light(PART_B_LIGHT, "Part B Light"))
    blocks.append(_light(PAINT_LIGHT, "Paint Light"))
    groups = f"<BlockGroups>{_group(GROUP_NAME, [TARGET, SECOND_LIGHT])}</BlockGroups>"
    return _grid(STATION_NAME, STATION_ID, blocks, position, forward, up, True, groups)


def drift_ship_xml(position, forward, up) -> str:
    blocks = [_armor(p) for p in DRIFT_MAIN + [DRIFT_BRIDGE] + DRIFT_PART]
    blocks.append(
        _block(
            "MyObjectBuilder_Thrust",
            "LargeBlockSmallThrust",
            DRIFT_THRUSTER,
            "<CustomName>Drift Thruster</CustomName><Enabled>false</Enabled>"
            "<ThrustOverride>345600</ThrustOverride>",
            entity_id=DRIFT_IDS[DRIFT_THRUSTER],
        )
    )
    blocks.append(
        _block(
            "MyObjectBuilder_BatteryBlock",
            "LargeBlockBatteryBlock",
            DRIFT_BATTERY,
            "<CustomName>Drift Battery</CustomName><Enabled>true</Enabled>"
            "<CurrentStoredPower>3</CurrentStoredPower><ProducerEnabled>true</ProducerEnabled>",
            entity_id=DRIFT_IDS[DRIFT_BATTERY],
        )
    )
    blocks.append(_light(DRIFT_LIGHT, "Drift Light", entity_id=DRIFT_IDS[DRIFT_LIGHT]))
    return _grid(DRIFT_SHIP_NAME, DRIFT_SHIP_ID, blocks, position, forward, up, False)


def _normalize(v):
    length = math.sqrt(sum(c * c for c in v))
    return [c / length for c in v]


def _cross(a, b):
    return [
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    ]


def prepare_world() -> Path:
    """Fresh copy of the Remote suite's Earth world with the test station."""
    if WORLD.exists():
        shutil.rmtree(WORLD)
    SAVES.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(TEMPLATE_ZIP) as archive:
        archive.extractall(SAVES)
    (SAVES / "RemoteAPITestEarthPlanet").rename(WORLD)

    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = WORLD / name
        text = path.read_text(encoding="utf-8")
        text = re.sub(
            r"<SessionName>.*?</SessionName>",
            f"<SessionName>{WORLD_NAME}</SessionName>",
            text,
        )
        text = text.replace("<TrashRemovalEnabled>true", "<TrashRemovalEnabled>false")
        text = re.sub(
            r"<GameMode>\w+</GameMode>", "<GameMode>Creative</GameMode>", text
        )
        text = text.replace("<StationVoxelSupport>false", "<StationVoxelSupport>true")
        path.write_text(text, encoding="utf-8")

    sector = WORLD / "SANDBOX_0_0_0_.sbs"
    text = sector.read_text(encoding="utf-8")

    # The station floats above the player, upright against the planet's gravity
    # (the planet is centred on the origin)
    start = text.index(
        '<MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_Character">'
    )
    match = re.search(r'<Position x="([^"]+)" y="([^"]+)" z="([^"]+)"', text[start:])
    character = [float(c) for c in match.groups()]
    up = _normalize(character)
    side = _normalize(_cross(up, [1.0, 0.0, 0.0]))
    forward = _cross(up, side)
    position = [character[i] + up[i] * ALTITUDE_M for i in range(3)]

    far = [c * DRIFT_DISTANCE_M for c in up]
    grids = station_xml(position, forward, up) + drift_ship_xml(far, forward, up)
    text = text.replace("</SectorObjects>", grids + "</SectorObjects>", 1)
    sector.write_text(text, encoding="utf-8")

    # The binary sector would win over the edited XML
    (WORLD / "SANDBOX_0_0_0_.sbsB5").unlink(missing_ok=True)
    return WORLD


# ---------------------------------------------------------------------------
# Client configuration and process
# ---------------------------------------------------------------------------


def write_configs() -> None:
    APPDATA.mkdir(parents=True, exist_ok=True)
    (APPDATA / "Storage").mkdir(exist_ok=True)

    options = "".join(
        f"  <{key}>{value}</{key}>\n" for key, value in UNDO_CONFIG.items()
    )
    (APPDATA / "Storage" / "Undo.cfg").write_text(
        '<?xml version="1.0" encoding="utf-8"?>\n'
        '<Config xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
        'xmlns:xsd="http://www.w3.org/2001/XMLSchema">\n'
        f"{options}</Config>\n",
        encoding="utf-8",
    )

    (APPDATA / "Remote.cfg").write_text(
        f"""<?xml version="1.0" encoding="utf-8"?>
<PluginConfig xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <Enabled>true</Enabled>
  <ListenIP>127.0.0.1</ListenIP>
  <ListenPort>{PORT}</ListenPort>
  <AdminPassword>SpaceEngineers</AdminPassword>
  <GridGetRateLimit>1000</GridGetRateLimit>
  <GridGetBurstSize>2000</GridGetBurstSize>
  <GridSetRateLimit>100</GridSetRateLimit>
  <GridSetBurstSize>200</GridSetBurstSize>
  <GridMaxGetOpsPerRequest>500</GridMaxGetOpsPerRequest>
  <GridMaxSetOpsPerRequest>100</GridMaxSetOpsPerRequest>
</PluginConfig>
""",
        encoding="utf-8",
    )

    # The Earth world is experimental; a fresh user data folder says it is not
    game_cfg = APPDATA / "SpaceEngineers.cfg"
    if not game_cfg.exists():
        shutil.copy(HOME / ".se-test/perf-data/SpaceEngineers.cfg", game_cfg)
    text = game_cfg.read_text(encoding="utf-8")
    text = re.sub(
        r"(<Key>ExperimentalMode</Key>\s*<Value>\s*<Value[^>]*>)\w+(</Value>)",
        r"\1True\2",
        text,
    )
    game_cfg.write_text(text, encoding="utf-8")


def running_pid() -> int | None:
    try:
        pid = int(PID_FILE.read_text().strip())
    except (OSError, ValueError):
        return None
    try:
        exe = os.readlink(f"/proc/{pid}/exe")
    except OSError:
        return None
    # The apphost re-execs itself; its exe is the dotnet host or the copy
    cmdline = Path(f"/proc/{pid}/cmdline").read_bytes().split(b"\0")
    return pid if exe and cmdline and cmdline[0].endswith(b"UndoInterim.bin") else None


def launch() -> int:
    if running_pid():
        raise RuntimeError(
            f"The Undo test client is already running, pid {running_pid()}"
        )
    write_configs()
    log = open(LAUNCH_LOG, "w")
    process = subprocess.Popen(
        [str(LAUNCHER), *GAME_ARGS],
        cwd=PULSAR_DIR,
        stdout=log,
        stderr=subprocess.STDOUT,
        stdin=subprocess.DEVNULL,
        start_new_session=True,
    )
    PID_FILE.write_text(str(process.pid))
    return process.pid


def stop() -> None:
    """Stops only the client this rig started."""
    pid = running_pid()
    if pid is None:
        return
    os.kill(pid, signal.SIGTERM)
    for _ in range(30):
        if running_pid() is None:
            break
        time.sleep(1)
    else:
        os.kill(pid, signal.SIGKILL)
    PID_FILE.unlink(missing_ok=True)


def api() -> RemoteAPI:
    return RemoteAPI(BASE_URL, username="admin", password="SpaceEngineers")


def load_world(client: RemoteAPI, timeout: float = 420.0) -> None:
    """Loads the test world. A bare XML sector first fails with a "needs XML" box;
    OK retries the load with XML allowed. The session counts as loaded once it
    stays active over several polls with no message box left."""
    try:
        client.load(str(WORLD))
    except Exception as err:  # noqa: BLE001 -- the load outlives the HTTP timeout
        print(f"load request returned early ({type(err).__name__})")
    deadline = time.monotonic() + timeout
    stable = 0
    while time.monotonic() < deadline:
        time.sleep(2)
        try:
            boxes = [
                s
                for s in client.list_screens()
                if s.get("type") == "MyGuiScreenMessageBox"
            ]
            for box in boxes:
                client.control_click(text="OK", screen=box["index"])
            stable = stable + 1 if not boxes and client.get_state().get("active") else 0
            if stable >= 3:
                return
        except Exception:  # noqa: BLE001 -- the API answers 500 while loading
            stable = 0
    raise TimeoutError("The test world did not become active")


def ensure_character(client: RemoteAPI, timeout: float = 90.0) -> dict:
    """Respawns when the world starts on the medical screen (the save's player
    is not the offline player)."""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try:
            character = client.get_character()
            if character.get("state") not in (None, "dead"):
                return character
        except Exception:  # noqa: BLE001 -- 503 without a character
            pass
        screen = next(
            (s for s in client.list_screens() if "Medical" in s.get("type", "")), None
        )
        if screen is not None:
            try:
                client.control_set("m_respawnsTable", 0, screen=screen["index"])
                time.sleep(1)
                client.control_click(text="Respawn", screen=screen["index"])
            except Exception:  # noqa: BLE001
                pass
        time.sleep(3)
    raise TimeoutError("No live character")


def focus_gameplay(client: RemoteAPI, timeout: float = 30.0) -> None:
    """Closes the welcome screen and anything else above the gameplay screen, so
    the build context is active"""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        screens = client.list_screens()
        extra = [
            i
            for i, s in enumerate(screens)
            if s.get("type") not in ("MyGuiScreenGamePlay", "MyGuiScreenHudSpace")
        ]
        if not extra:
            return
        client.close_screen(extra[-1])
        time.sleep(0.5)
    raise TimeoutError("Could not get back to the gameplay screen")

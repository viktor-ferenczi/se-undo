"""Isolated headless client for the Undo tests.

Own Pulsar folder with the Remote and Undo plugins as dev folders, own game user
data folder (-appdata), own Remote port. The launcher is a renamed copy of
Interim.bin, so nothing that stops "Interim.bin" by name takes this client down.

Pulsar folder, created once by hand (see tests/README.md)::

    notes/pulsar-dev-instances/new-pulsar-instance.sh ~/.se-test/undo
    # profile trimmed to the remote and Undo dev folders, UndoInterim.bin copied
"""

from __future__ import annotations

import base64
import math
import os
import random
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

WINDOWED = os.environ.get("UNDO_WINDOWED") == "1"
# Logs of the test runs, not committed
ARTIFACTS = REPO / "tests" / "artifacts"


class Client:
    """One isolated client: its Pulsar folder with a renamed launcher, its game
    user data folder and its Remote port"""

    def __init__(self, pulsar: Path, launcher: str, appdata: Path, port: int):
        self.pulsar = Path(pulsar)
        self.launcher = self.pulsar / launcher
        self.appdata = Path(appdata)
        self.port = port
        self.pid_file = self.appdata / "game.pid"
        self.launch_log = self.appdata / "launch.log"
        self.status_file = self.appdata / "Undo" / "status.json"

    def args(self) -> list[str]:
        args = [
            "-multiInstance",
            "-lazySteam",
            "-noprompt",
            "-noupdate",
            "-nosplash",
            "-stablelogs",
            "-sources",
            "--no-steam",
            "-appdata",
            str(self.appdata),
            "--quality",
            "minimal",
            "--resolution",
            "1280x720",
            # No test listens. Headless alone only mutes the output, this also
            # keeps the game from loading its sounds.
            "--no-audio",
        ]
        # More options for a run by hand, like "--max-fps 30"
        args += os.environ.get("UNDO_EXTRA_ARGS", "").split()
        # UNDO_WINDOWED=1 opens a real window, for checks done by hand
        return args if WINDOWED else args + ["--headless"]


# Slot 0 is the Pulsar folder set up by hand (tests/README.md). Every further slot is
# a client of its own, cloned from slot 0 on first use, so the test files can run
# side by side: UNDO_SLOT=3 uv run pytest tests/test_terminal.py
SLOT = int(os.environ.get("UNDO_SLOT", "0"))
BASE_PULSAR = HOME / ".se-test/undo"
LAUNCHER = "UndoInterim.bin"
_NAME = "undo" if SLOT == 0 else f"undo-s{SLOT}"

CLIENT = Client(
    os.environ.get("UNDO_PULSAR_DIR", HOME / ".se-test" / _NAME),
    LAUNCHER,
    os.environ.get("UNDO_APPDATA", HOME / ".se-test" / f"{_NAME}-data"),
    # 24177 is the dedicated server rig's client
    int(os.environ.get("UNDO_REMOTE_PORT", 24176 if SLOT == 0 else 24180 + SLOT)),
)
PULSAR_DIR = CLIENT.pulsar
APPDATA = CLIENT.appdata
STATUS_FILE = CLIENT.status_file

# Saves of the offline (no Steam) player
SAVES = APPDATA / "Saves" / "1234567891011"
WORLD_NAME = "UndoTestEarth"
WORLD = SAVES / WORLD_NAME
# The same world in survival, for the permission tests
SURVIVAL_WORLD_NAME = "UndoTestSurvival"
SURVIVAL_WORLD = SAVES / SURVIVAL_WORLD_NAME
TEMPLATE_ZIP = REMOTE_REPO / "Worlds" / "RemoteAPITestEarthPlanet.zip"

# Undo options for the run. Everything else keeps its default. The tree option is
# on for the whole run, since the plugin reads its config once at start.
UNDO_CONFIG = {
    "DebugStatusFile": "true",
    "RecordTerminalChangesOutsideTerminal": "true",
    "UndoTree": "true",
    "LogLevel": "Debug",
    # Small enough for the retention and oversized backup tests to reach
    "GridStoreBudgetPerWorldMb": "1",
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
PROGRAMMABLE = (12, 1, 15)
PART_B_LIGHT = (18, 1, 8)

IDS = {
    COCKPIT: 777000555000011,
    TURRET_CONTROLLER: 777000555000012,
    EVENT_CONTROLLER: 777000555000013,
    TARGET: 777000555000014,
    SECOND_LIGHT: 777000555000015,
    PART_B_LIGHT: 777000555000016,
    PAINT_LIGHT: 777000555000017,
    PROGRAMMABLE: 777000555000018,
}

GROUP_NAME = "Undo Group"
TARGET_NAME = "Undo Target"

# Free cells above the floor for the limits test: 15 x 14 = 210
LIMITS_AREA = [(x, 1, z) for z in range(14) for x in range(15)]
BUILD_CELL = (15, 1, 15)
TREE_CELLS = [(15, 1, 12), (15, 1, 13), (15, 1, 14)]
ALTITUDE_M = 300.0
LARGE_BLOCK_M = 2.5

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
            # Turned around: its open side with the console faces the floor, where
            # the character can stand to open its terminal
            forward="Backward",
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
    # Turned so its keyboard faces the floor, where the character stands to open it
    blocks.append(
        _block(
            "MyObjectBuilder_MyProgrammableBlock",
            "LargeProgrammableBlock",
            PROGRAMMABLE,
            "<CustomName>Undo Programmable Block</CustomName>",
            forward="Backward",
        )
    )
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


# ---------------------------------------------------------------------------
# The links rig: a second station whose blocks point at each other in every way the
# tests can check, for the "a restored block is the block it was" tests
# ---------------------------------------------------------------------------

LINKS_NAME = "Undo Links Rig"
LINKS_ID = 777000556000001
LINKS_FLOOR = [(x, 0, z) for x in range(8) for z in range(3)]
LINKS = {
    "light": (0, 1, 0),
    "light2": (1, 1, 0),
    "camera": (2, 1, 0),
    "timer": (3, 1, 0),
    "buttons": (4, 1, 0),
    "sensor": (5, 1, 0),
    "event": (6, 1, 0),
    "remote": (7, 1, 0),
    "turret": (0, 1, 2),
    "cargo": (1, 1, 2),
    "program": (2, 1, 2),
    "cockpit": (4, 1, 2),
    "battery": (6, 1, 2),
    "lcd": (3, 1, 2),
    "offensive": (7, 1, 2),
    "defensive": (5, 1, 2),
    "flight": (0, 1, 1),
    "recorder": (7, 1, 1),
}
LINKS_LCD_TEXT = "Hello from the LCD"
LINKS_IDS = {name: 777000556000011 + i for i, name in enumerate(LINKS)}
LINKS_GROUP = "Links Group"
LINKS_ITEMS = {"SteelPlate": 7, "Motor": 3}
LINKS_PROGRAM = "void Main() { Echo(Storage); }"


def _slot(index: int, target: str, action: str = "OnOff") -> str:
    return _toolbar_slot(index, LINKS_IDS[target]).replace(
        "<Action>OnOff</Action>", f"<Action>{action}</Action>"
    )


def _toolbar(kind: str, slots: str, tag: str = "Toolbar") -> str:
    return (
        f"<{tag}><ToolbarType>{kind}</ToolbarType>"
        f'<SelectedSlot xsi:nil="true" /><Slots>{slots}</Slots></{tag}>'
    )


def links_xml(position, forward, up) -> str:
    def block(name, xsi_type, subtype, extra="", **kwargs):
        return _block(
            f"MyObjectBuilder_{xsi_type}",
            subtype,
            LINKS[name],
            f"<CustomName>Links {name}</CustomName>{extra}",
            entity_id=LINKS_IDS[name],
            **kwargs,
        )

    # Lights, the camera and the sensor mount with their back, which faces down
    # this way. A block that is not mounted splits off at the first removal.
    light = dict(forward="Up", up="Backward")
    ids = LINKS_IDS
    blocks = [_armor(p) for p in LINKS_FLOOR]
    blocks += [
        block(
            "light", "InteriorLight", "SmallLight", "<Enabled>true</Enabled>", **light
        ),
        block(
            "light2", "InteriorLight", "SmallLight", "<Enabled>false</Enabled>", **light
        ),
        block("camera", "CameraBlock", "LargeCameraBlock", **light),
        block(
            "timer",
            "TimerBlock",
            "TimerBlockLarge",
            _toolbar("Character", _slot(0, "light") + _slot(1, "light2"))
            + "<Delay>3000</Delay>",
        ),
        block(
            "buttons",
            "ButtonPanel",
            "ButtonPanelLarge",
            _toolbar("Character", _slot(0, "light") + _slot(2, "light2"))
            + "<AnyoneCanUse>true</AnyoneCanUse><CustomButtonNames><dictionary>"
            "<item><Key>0</Key><Value>Lamp</Value></item>"
            "</dictionary></CustomButtonNames>",
        ),
        block(
            "sensor",
            "SensorBlock",
            "LargeBlockSensor",
            _toolbar("Character", _slot(0, "light2") + _slot(1, "light"))
            + "<DetectPlayers>false</DetectPlayers>",
            **light,
        ),
        block(
            "event",
            "EventControllerBlock",
            "EventControllerLarge",
            _toolbar("Character", _slot(0, "light2"))
            + f"<SelectedBlocks><long>{ids['light']}</long><long>{ids['camera']}</long></SelectedBlocks>",
        ),
        block(
            "remote",
            "RemoteControl",
            "LargeBlockRemoteControl",
            f"<BindedCamera>{ids['camera']}</BindedCamera>",
        ),
        block(
            "turret",
            "TurretControlBlock",
            "LargeTurretControlBlock",
            f"<CameraId>{ids['camera']}</CameraId>"
            f"<ToolIds><long>{ids['light']}</long><long>{ids['light2']}</long></ToolIds>",
        ),
        cargo_container(
            LINKS["cargo"],
            "LargeBlockSmallContainer",
            LINKS_ITEMS,
            entity_id=ids["cargo"],
            extra="<CustomName>Links cargo</CustomName>",
        ),
        block(
            "program",
            "MyProgrammableBlock",
            "LargeProgrammableBlock",
            f"<Program>{LINKS_PROGRAM}</Program>",
        ),
        block(
            "cockpit",
            "Cockpit",
            "LargeBlockCockpit",
            _toolbar(
                "Ship",
                _slot(0, "light")
                + _slot(3, "camera", "View")
                + _slot(4, "timer", "TriggerNow"),
            ),
        ),
        block(
            "lcd",
            "TextPanel",
            "LargeLCDPanel",
            f"<PublicDescription>{LINKS_LCD_TEXT}</PublicDescription>"
            "<FontSize>2.5</FontSize><ContentType>TEXT_AND_IMAGE</ContentType>"
            "<Alignment>Align_Center</Alignment><TextPadding>7</TextPadding>"
            "<FontColor><PackedValue>4278255615</PackedValue></FontColor>",
            # The panel mounts with its front side
            forward="Down",
            up="Forward",
        ),
        # The AI blocks, each with a toolbar of its own
        block(
            "offensive",
            "OffensiveCombatBlock",
            "LargeOffensiveCombat",
            _toolbar("Character", _slot(0, "light") + _slot(1, "timer", "TriggerNow")),
        ),
        block("defensive", "DefensiveCombatBlock", "LargeDefensiveCombat"),
        block(
            "flight",
            "FlightMovementBlock",
            "LargeFlightMovement",
            _toolbar("Character", _slot(0, "light2")),
        ),
        block("recorder", "PathRecorderBlock", "LargePathRecorderBlock"),
        # Power for the timer, whose toolbar a test triggers
        block(
            "battery",
            "BatteryBlock",
            "LargeBlockBatteryBlock",
            "<Enabled>true</Enabled><CurrentStoredPower>3</CurrentStoredPower>"
            "<ProducerEnabled>true</ProducerEnabled>",
        ),
    ]
    groups = (
        "<BlockGroups>"
        + _group(LINKS_GROUP, [LINKS["light"], LINKS["light2"], LINKS["camera"]])
        + _group("Links Other", [LINKS["cargo"], LINKS["light"]])
        + "</BlockGroups>"
    )
    return _grid(LINKS_NAME, LINKS_ID, blocks, position, forward, up, True, groups)


def _blueprint_grid(
    name: str, blocks: str, static: bool, position=(0, 0, 0), entity_id: int = 0
) -> str:
    # Grids of one blueprint need different ids: the paste gives each id a new
    # one, and two grids without an id would end up as the same entity
    entity = f"<EntityId>{entity_id}</EntityId>" if entity_id else ""
    return (
        f"<CubeGrid><SubtypeName />{entity}"
        "<PersistentFlags>CastShadows InScene</PersistentFlags>"
        f"<PositionAndOrientation>{_vec('Position', position)}"
        '<Forward x="0" y="0" z="-1" /><Up x="0" y="1" z="0" /></PositionAndOrientation>'
        f"<GridSizeEnum>Large</GridSizeEnum><CubeBlocks>{blocks}</CubeBlocks>"
        # Unsupported, a static grid out of voxel contact would turn into a ship
        f"<IsStatic>{'true' if static else 'false'}</IsStatic>"
        f"<IsUnsupportedStation>{'true' if static else 'false'}</IsUnsupportedStation>"
        f"<DisplayName>{name}</DisplayName></CubeGrid>"
    )


def _blueprint(name: str, grids: str) -> str:
    return (
        '<?xml version="1.0"?>'
        '<Definitions xmlns:xsd="http://www.w3.org/2001/XMLSchema" '
        'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><ShipBlueprints>'
        '<ShipBlueprint xsi:type="MyObjectBuilder_ShipBlueprintDefinition">'
        f'<Id Type="MyObjectBuilder_ShipBlueprintDefinition" Subtype="{name}" />'
        f"<CubeGrids>{grids}</CubeGrids>"
        "</ShipBlueprint></ShipBlueprints></Definitions>"
    )


def blueprint_xml(name: str, cells, static: bool = True, blocks: str = "") -> str:
    """A bp.sbc document with one large grid of armor blocks, for the paste tests"""
    blocks = "".join(_armor(p) for p in cells) + blocks
    return _blueprint(name, _blueprint_grid(name, blocks, static))


def blueprint_grids_xml(grids) -> str:
    """A blueprint of several separate grids of armor blocks: (name, cells, offset
    in metres) each. The first one names the blueprint."""
    return _blueprint(
        grids[0][0],
        "".join(
            _blueprint_grid(name, "".join(_armor(p) for p in cells), True, offset, i)
            for i, (name, cells, offset) in enumerate(grids, start=1)
        ),
    )


def cargo_container(
    pos, subtype: str, items: dict[str, int], entity_id: int = 0, extra: str = ""
) -> str:
    """A cargo container holding components, by subtype and amount"""
    content = "".join(
        "<MyObjectBuilder_InventoryItem>"
        f"<Amount>{amount}</Amount>"
        f'<PhysicalContent xsi:type="MyObjectBuilder_Component"><SubtypeName>{item}</SubtypeName></PhysicalContent>'
        f"<ItemId>{index}</ItemId></MyObjectBuilder_InventoryItem>"
        for index, (item, amount) in enumerate(items.items())
    )
    return _block(
        "MyObjectBuilder_CargoContainer",
        subtype,
        pos,
        f"{extra}<ComponentContainer><Components><ComponentData><TypeId>MyInventoryBase</TypeId>"
        f'<Component xsi:type="MyObjectBuilder_Inventory"><Items>{content}</Items>'
        f"<nextItemId>{len(items)}</nextItemId></Component></ComponentData></Components>"
        "</ComponentContainer>",
        entity_id=entity_id,
    )


def heavy_blueprint_xml(name: str, payload_bytes: int, seed: int) -> str:
    """A two block station whose backup is about payload_bytes large in the grid
    store: its programmable block holds that much random text as a comment, which
    gzip cannot shrink below the random bytes behind it. Block names would not do,
    the game cuts them short. The light is there to be switched, which makes the
    next backup of the grid differ from the last."""
    noise = random.Random(seed).randbytes(payload_bytes)
    cells = [(0, 0, 0), (1, 0, 0)]
    blocks = "".join(_armor(p) for p in cells)
    blocks += _block(
        "MyObjectBuilder_MyProgrammableBlock",
        "LargeProgrammableBlock",
        (0, 1, 0),
        f"<Program>/*{base64.b64encode(noise).decode('ascii')}*/</Program>",
        entity_id=0,
        forward="Backward",
    )
    blocks += _light((1, 1, 0), "Switch", entity_id=0)
    return blueprint_xml(name, [], blocks=blocks)


def undo_config(key: str) -> str | None:
    """An option as the plugin last saved it; None while it has its default"""
    text = (APPDATA / "Storage" / "Undo.cfg").read_text(encoding="utf-8")
    match = re.search(rf"<{key}>(.*?)</{key}>", text)
    return match.group(1) if match else None


def _normalize(v):
    length = math.sqrt(sum(c * c for c in v))
    return [c / length for c in v]


def _cross(a, b):
    return [
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0],
    ]


def station_frame(sector_text: str | None = None):
    """Position, forward and up of the test station. It floats above the saved
    character, upright against the planet's gravity (the planet is centred on the
    origin)."""
    if sector_text is None:
        with zipfile.ZipFile(TEMPLATE_ZIP) as archive:
            sector_text = archive.read(
                "RemoteAPITestEarthPlanet/SANDBOX_0_0_0_.sbs"
            ).decode("utf-8")
    start = sector_text.index(
        '<MyObjectBuilder_EntityBase xsi:type="MyObjectBuilder_Character">'
    )
    match = re.search(
        r'<Position x="([^"]+)" y="([^"]+)" z="([^"]+)"', sector_text[start:]
    )
    character = [float(c) for c in match.groups()]
    up = _normalize(character)
    side = _normalize(_cross(up, [1.0, 0.0, 0.0]))
    forward = _cross(up, side)
    position = [character[i] + up[i] * ALTITUDE_M for i in range(3)]
    return position, forward, up


def station_point(cell) -> list[float]:
    """World position of a station cell centre; fractions address inside a cell.
    Grid axes: X right, Y up, Z backward."""
    position, forward, up = station_frame()
    right = _cross(forward, up)
    return [
        position[i]
        + LARGE_BLOCK_M * (cell[0] * right[i] + cell[1] * up[i] - cell[2] * forward[i])
        for i in range(3)
    ]


def prepare_world(
    world: Path = WORLD, mode: str = "Creative", settings: dict | None = None
) -> Path:
    """Fresh copy of the Remote suite's Earth world with the test station.
    settings changes session settings of the world, by element name."""
    # A grid store an earlier copy of this world left, keyed by folder name and id
    for store in (APPDATA / "Undo" / "Worlds").glob(f"{world.name}*"):
        shutil.rmtree(store)
    if world.exists():
        shutil.rmtree(world)
    world.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(TEMPLATE_ZIP) as archive:
        archive.extractall(world.parent)
    (world.parent / "RemoteAPITestEarthPlanet").rename(world)

    for name in ("Sandbox.sbc", "Sandbox_config.sbc"):
        path = world / name
        text = path.read_text(encoding="utf-8")
        text = re.sub(
            r"<SessionName>.*?</SessionName>",
            f"<SessionName>{world.name}</SessionName>",
            text,
        )
        text = text.replace("<TrashRemovalEnabled>true", "<TrashRemovalEnabled>false")
        text = re.sub(r"<GameMode>\w+</GameMode>", f"<GameMode>{mode}</GameMode>", text)
        text = text.replace("<StationVoxelSupport>false", "<StationVoxelSupport>true")
        # Pasting in creative needs it, by hand and for undo
        text = text.replace("<EnableCopyPaste>false", "<EnableCopyPaste>true")
        text = text.replace("<EnableIngameScripts>false", "<EnableIngameScripts>true")
        for key, value in (settings or {}).items():
            text, count = re.subn(
                rf"<{key}>[^<]*</{key}>", f"<{key}>{value}</{key}>", text
            )
            if not count:
                text = text.replace(
                    "</Settings>", f"<{key}>{value}</{key}></Settings>", 1
                )
        path.write_text(text, encoding="utf-8")

    sector = world / "SANDBOX_0_0_0_.sbs"
    text = sector.read_text(encoding="utf-8")

    position, forward, up = station_frame(text)
    far = [c * DRIFT_DISTANCE_M for c in up]
    grids = station_xml(position, forward, up) + drift_ship_xml(far, forward, up)
    # The links rig floats 60 m above the test station
    grids += links_xml([p + 60 * u for p, u in zip(position, up)], forward, up)
    text = text.replace("</SectorObjects>", grids + "</SectorObjects>", 1)
    sector.write_text(text, encoding="utf-8")

    # The binary sector would win over the edited XML
    (world / "SANDBOX_0_0_0_.sbsB5").unlink(missing_ok=True)
    return world


# ---------------------------------------------------------------------------
# Client configuration and process
# ---------------------------------------------------------------------------


def write_configs(client: Client = CLIENT, undo_config: dict | None = None) -> None:
    appdata = client.appdata
    appdata.mkdir(parents=True, exist_ok=True)
    (appdata / "Storage").mkdir(exist_ok=True)

    options = "".join(
        f"  <{key}>{value}</{key}>\n"
        for key, value in (undo_config or UNDO_CONFIG).items()
        if value is not None
    )
    (appdata / "Storage" / "Undo.cfg").write_text(
        '<?xml version="1.0" encoding="utf-8"?>\n'
        '<Config xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" '
        'xmlns:xsd="http://www.w3.org/2001/XMLSchema">\n'
        f"{options}</Config>\n",
        encoding="utf-8",
    )

    (appdata / "Remote.cfg").write_text(
        f"""<?xml version="1.0" encoding="utf-8"?>
<PluginConfig xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <Enabled>true</Enabled>
  <ListenIP>127.0.0.1</ListenIP>
  <ListenPort>{client.port}</ListenPort>
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
    game_cfg = appdata / "SpaceEngineers.cfg"
    if not game_cfg.exists():
        shutil.copy(HOME / ".config/SpaceEngineers/SpaceEngineers.cfg", game_cfg)
    text = game_cfg.read_text(encoding="utf-8")
    text = re.sub(
        r"(<Key>ExperimentalMode</Key>\s*<Value>\s*<Value[^>]*>)\w+(</Value>)",
        r"\1True\2",
        text,
    )
    game_cfg.write_text(text, encoding="utf-8")


def ensure_pulsar(client: Client = CLIENT) -> None:
    """Clones the Pulsar folder of slot 0 for another slot, the way
    notes/pulsar-dev-instances/new-pulsar-instance.sh does: launcher files copied,
    the large read only trees linked, the plugin sources, profile and build cache
    copied, the Preloader folder left for the first launch to create."""
    if client.launcher.exists():
        return
    if not (BASE_PULSAR / LAUNCHER).exists():
        raise RuntimeError(f"{BASE_PULSAR} is not set up, see tests/README.md")
    client.pulsar.mkdir(parents=True)
    for entry in BASE_PULSAR.iterdir():
        target = client.pulsar / entry.name
        if entry.is_symlink():
            target.symlink_to(entry.resolve())
        elif entry.is_file():
            shutil.copy2(entry, target)
    shutil.copytree(
        BASE_PULSAR / "Legacy",
        client.pulsar / "Legacy",
        ignore=shutil.ignore_patterns("Preloader", "info.log"),
    )


def running_pid(client: Client = CLIENT) -> int | None:
    try:
        pid = int(client.pid_file.read_text().strip())
        cmdline = Path(f"/proc/{pid}/cmdline").read_bytes().split(b"\0")
    except (OSError, ValueError):
        return None
    # Only a process started from this client's launcher counts
    return pid if cmdline[0] == str(client.launcher).encode() else None


def launch(client: Client = CLIENT, extra_args=(), undo_config=None) -> int:
    if running_pid(client):
        raise RuntimeError(
            f"The test client is already running, pid {running_pid(client)}"
        )
    ensure_pulsar(client)
    write_configs(client, undo_config)
    log = open(client.launch_log, "w")
    process = subprocess.Popen(
        [str(client.launcher), *client.args(), *extra_args],
        cwd=client.pulsar,
        stdout=log,
        stderr=subprocess.STDOUT,
        stdin=subprocess.DEVNULL,
        start_new_session=True,
    )
    client.pid_file.write_text(str(process.pid))
    return process.pid


def stop(client: Client = CLIENT) -> None:
    """Stops only the client this rig started."""
    pid = running_pid(client)
    if pid is None:
        return
    os.kill(pid, signal.SIGTERM)
    for _ in range(30):
        if running_pid(client) is None:
            break
        time.sleep(1)
    else:
        os.kill(pid, signal.SIGKILL)
    client.pid_file.unlink(missing_ok=True)


def api(client: Client = CLIENT) -> RemoteAPI:
    return RemoteAPI(
        f"http://127.0.0.1:{client.port}", username="admin", password="SpaceEngineers"
    )


def load_world(client: RemoteAPI, world: Path = WORLD, timeout: float = 420.0) -> None:
    """Loads a test world. A bare XML sector first fails with a "needs XML" box,
    which wait_world clicks."""
    try:
        client.load(str(world))
    except Exception as err:  # noqa: BLE001 -- the load outlives the HTTP timeout
        print(f"load request returned early ({type(err).__name__})")
    wait_world(client, timeout)


def _message_boxes(client: RemoteAPI) -> list[dict]:
    return [
        s for s in client.list_screens() if s.get("type") == "MyGuiScreenMessageBox"
    ]


def reload_world(client: RemoteAPI, timeout: float = 420.0) -> None:
    """Loads the world again from its folder, without saving first."""
    try:
        client.reload(save=False)
    except Exception as err:  # noqa: BLE001 -- the load outlives the HTTP timeout
        print(f"reload request returned early ({type(err).__name__})")
    wait_world(client, timeout)
    ensure_character(client)
    focus_gameplay(client)
    client.unpause()


def wait_world(client: RemoteAPI, timeout: float = 420.0) -> None:
    """Waits until the session is ready, clicking OK on any message box on the
    way. OK on the "needs XML" box retries the load with XML allowed."""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        time.sleep(1)
        try:
            boxes = _message_boxes(client)
            for box in boxes:
                client.control_click(text="OK", screen=box["index"])
            if not boxes and client.get_state().get("ready"):
                return
        except Exception:  # noqa: BLE001 -- the API times out while a world loads
            pass
    raise TimeoutError("The test world did not become ready")


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
    the build context is active.

    A screen is closed by its index, and the list changes while a world starts,
    so it has to read the same twice first. The loading screen is left to finish
    by itself: it is what adds the HUD screen, and a world without that crashes
    when the character leaves its seat."""
    keep = ("MyGuiScreenGamePlay", "MyGuiScreenHudSpace")

    def kinds():
        return [s.get("type") for s in client.list_screens()]

    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        screens = kinds()
        extra = [i for i, kind in enumerate(screens) if kind not in keep]
        if not extra and set(keep) <= set(screens):
            return
        time.sleep(0.3)
        if extra and "MyGuiScreenLoading" not in screens and kinds() == screens:
            client.close_screen(extra[-1])
            time.sleep(0.3)
    raise TimeoutError("Could not get back to the gameplay screen")

"""Persistence rows of design section 13: the history is saved with the world,
lands in its backups, and comes back on a reload, a backup restore and Save As.
Runs last: it reloads the world, and Save As moves the session to another folder.
"""

from __future__ import annotations

import gzip
import shutil
import time
import xml.etree.ElementTree as ET
from pathlib import Path

import rig
from conftest import wait_until

FILE = "Undo.xml.gz"
SAVE_AS = "UndoSaveAsTest"


def document(path: Path) -> ET.Element:
    with gzip.open(path, "rb") as file:
        return ET.parse(file).getroot()


def saved_labels(path: Path) -> list[str]:
    nodes = document(path).findall("Build/Nodes/Node")
    return [n.findtext("Label") for n in nodes if n.findtext("Id") != "0"]


def shape(history: dict) -> tuple:
    """What has to survive a save: the nodes, their order in the tree, the cursor"""
    nodes = [(n["id"], n["parent"], n["label"]) for n in history["nodes"]]
    return history["current"], nodes


def save(game, name: str | None = None) -> Path:
    """Saves and returns the world folder once the history file is in it"""
    folder = rig.SAVES / name if name else rig.WORLD
    target = folder / FILE
    before = target.stat().st_mtime_ns if target.exists() else 0
    game.api.save(name=name)
    wait_until(
        lambda: target.exists() and target.stat().st_mtime_ns != before,
        "the history file in the save",
        timeout=60,
    )
    # The backup is the last step of a save
    wait_until(
        lambda: (newest_backup(folder) / FILE).exists()
        and (newest_backup(folder) / FILE).read_bytes() == target.read_bytes(),
        "the backup of the save",
        timeout=60,
    )
    return folder


def newest_backup(folder: Path = rig.WORLD) -> Path:
    return max((folder / "Backup").iterdir(), key=lambda p: p.name)


def build(game, cell) -> None:
    last = game.last_node_id()
    assert game.api.character_build_block(game.station, cell)["sent"]
    wait_until(lambda: game.exists(cell), "the block")
    game.wait_recorded(last, "placed 1 block")


def test_history_is_saved_with_the_world_and_loaded_back(game):
    cell = rig.BUILD_CELL
    assert not game.exists(cell)
    build(game, cell)
    before = shape(game.build())
    terminal = shape(game.terminal())

    save(game)
    labels = [label for _, _, label in before[1]]
    assert saved_labels(rig.WORLD / FILE) == labels
    assert document(rig.WORLD / FILE).findtext("Version") == "1"
    # The backup of this save has the same file
    assert (newest_backup() / FILE).read_bytes() == (rig.WORLD / FILE).read_bytes()

    rig.reload_world(game.api)
    assert shape(game.build()) == before
    assert shape(game.terminal()) == terminal
    assert game.exists(cell)

    assert game.undo() == "Undo: placed 1 block"
    wait_until(lambda: not game.exists(cell), "the block to go")
    assert game.redo() == "Redo: placed 1 block"
    wait_until(lambda: game.exists(cell), "the block to return")


def test_backup_restore_brings_the_history_of_that_save(game):
    cell, later = rig.BUILD_CELL, rig.TREE_CELLS[0]
    assert game.exists(cell) and not game.exists(later)
    save(game)
    saved = shape(game.build())
    backup = newest_backup()

    # The world moves on after that save, and is saved again. Backup folders are
    # named by the second, a save within the same one would reuse the folder.
    build(game, later)
    time.sleep(1.1)
    save(game)
    assert newest_backup() != backup
    assert shape(game.build()) != saved

    # What MyGuiScreenLoadSandbox.CopyBackupUpALevel does when a backup is loaded:
    # the world folder's files go, the backup's files come in their place
    for path in rig.WORLD.iterdir():
        if path.is_file():
            path.unlink()
    for path in backup.iterdir():
        if path.is_file():
            shutil.copy2(path, rig.WORLD / path.name)
    rig.reload_world(game.api)

    assert shape(game.build()) == saved
    assert game.exists(cell) and not game.exists(later)
    assert game.undo() == "Undo: placed 1 block"
    wait_until(lambda: not game.exists(cell), "the block to go")


def test_save_as_takes_the_history_along(game):
    """Save As from within the game writes a new snapshot through the same staging
    folder. The session continues in the new folder afterwards."""
    shutil.rmtree(rig.SAVES / SAVE_AS, ignore_errors=True)
    labels = [n["label"] for n in game.build()["nodes"]]

    folder = save(game, SAVE_AS)
    assert folder != rig.WORLD
    assert saved_labels(folder / FILE) == labels


# ---------------------------------------------------------------------------
# The load menu: its Save As copies the world folder, its Backups screen restores
# ---------------------------------------------------------------------------

MENU_COPY = "UndoMenuCopyTest"


def top(api) -> dict:
    return api.list_screens()[-1]


def wait_screen(api, kind: str) -> int:
    return wait_until(lambda: top(api)["type"] == kind and top(api), kind, timeout=60)[
        "index"
    ]


def load_menu(api) -> int:
    """The load game screen of the title menu, leaving the session without saving"""
    if top(api)["type"] != "MyGuiScreenLoadSandbox":
        rig.focus_gameplay(api)
        api.key("Escape")
        menu = wait_screen(api, "MyGuiScreenMainMenu")
        api.control_click(name="ExitToMainMenu", screen=menu)
        api.control_click(text="No", screen=wait_screen(api, "MyGuiScreenMessageBox"))
        wait_until(
            lambda: [s["type"] for s in api.list_screens()]
            == ["MyGuiScreenIntroVideo", "MyGuiScreenMainMenu"],
            "the title menu",
            timeout=60,
        )
        time.sleep(1)
        api.control_click(name="LoadWorld", screen=top(api)["index"])
    screen = wait_screen(api, "MyGuiScreenLoadSandbox")

    # The list of saves is filled a moment after the screen opens, and again
    # after Save As; a click on the name selects nothing before that
    def selected():
        api.control_click(text=rig.WORLD_NAME, screen=screen)
        return browser(api, screen)["properties"]["selectedRowIndex"] is not None

    wait_until(selected, "the test world's row", interval=0.5)
    return screen


def browser(api, screen: int) -> dict:
    return next(
        c for c in api.get_controls(screen) if c.get("name") == "SaveBrowserTable"
    )


def test_save_as_from_the_load_menu_copies_the_history(game):
    api = game.api
    shutil.rmtree(rig.SAVES / MENU_COPY, ignore_errors=True)
    screen = load_menu(api)

    api.control_click(name="SaveAs", screen=screen)
    dialog = wait_screen(api, "MyGuiScreenSaveAs")
    # The dialog proposes the name of the world it copies. That is the first row's
    # here, whatever was selected: the save browser keeps a multi selection of its
    # own, which Remote's row selection does not change (SE1-0073).
    proposed = next(
        c for c in api.get_controls(dialog) if c.get("type") == "MyGuiControlTextbox"
    )
    source = rig.SAVES / proposed["properties"]["text"] / FILE
    assert source.exists()
    api.control_set("Textbox", MENU_COPY, screen=dialog)
    api.control_click(text="OK", screen=dialog)
    copy = rig.SAVES / MENU_COPY / FILE
    wait_until(copy.exists, "the copied world", timeout=60)
    wait_screen(api, "MyGuiScreenLoadSandbox")
    assert copy.read_bytes() == source.read_bytes()


def test_backup_restore_through_the_load_menu(game):
    """The game's own restore: Backups, pick one, Load"""
    api = game.api
    # A backup whose history differs from the one in the world folder now, so the
    # file can only be there afterwards because the game copied it back
    backups = sorted((rig.WORLD / "Backup").iterdir(), reverse=True)
    current = (rig.WORLD / FILE).read_bytes()
    line = next(i for i, b in enumerate(backups) if (b / FILE).read_bytes() != current)
    wanted = backups[line] / FILE

    screen = load_menu(api)
    api.control_click(name="Backup", screen=screen)
    time.sleep(1)
    # Row 0 leads back to the worlds, the backups follow, newest first
    api.control_set("SaveBrowserTable", line + 1, screen=screen)
    marker = rig.STATUS_FILE.stat().st_mtime_ns
    api.control_click(name="Load", screen=screen)
    wait_until(
        lambda: rig.STATUS_FILE.stat().st_mtime_ns != marker,
        "the restored world to start",
        timeout=120,
    )
    rig.wait_world(api)
    rig.ensure_character(api)
    rig.focus_gameplay(api)
    api.unpause()

    assert (rig.WORLD / FILE).read_bytes() == wanted.read_bytes()
    build = game.build()
    assert [n["label"] for n in build["nodes"]] == saved_labels(wanted)
    assert str(build["current"]) == document(wanted).findtext("Build/CurrentId")

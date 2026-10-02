"""Client setup: every test file is a session of its own.

Each file gets a freshly started client and a fresh copy of the test world, so the
files do not depend on each other and can run side by side on different client
slots (rig.SLOT, tests/run_pieces.py). A file changes the plugin options of its
client with a module level UNDO_CONFIG dict, merged over rig.UNDO_CONFIG; None
puts an option back to the plugin's default. WORLD_SETTINGS changes session settings
of the test world the same way.

UNDO_ATTACH=1 reuses a client of this slot that is already in the test world, and
UNDO_KEEP=1 leaves the client running after the run; both help while iterating on
one file.

What the plugin logged during each test, the steps it recorded and every undo and
redo notification, goes to tests/artifacts/<test file>.log. Nothing reads it back:
it is for a person who wants to see what happened in the game.
"""

from __future__ import annotations

import os
import shutil
from pathlib import Path

import pytest

import rig
from harness import Game


@pytest.fixture(scope="module")
def game(request):
    api = rig.api()
    attach = os.environ.get("UNDO_ATTACH") == "1" and rig.running_pid()
    config = {**rig.UNDO_CONFIG, **getattr(request.module, "UNDO_CONFIG", {})}
    # The plugin writes its status file into its storage folder, which a test file
    # can move with the ClientStorageRoot option
    root = config.get("ClientStorageRoot")
    default_status = rig.STATUS_FILE
    if root:
        shutil.rmtree(root, ignore_errors=True)
        # What earlier runs left in the default folder would look like this run's
        shutil.rmtree(rig.APPDATA / "Undo", ignore_errors=True)
        rig.STATUS_FILE = Path(root) / "status.json"
    plugin_log(request.module).unlink(missing_ok=True)
    try:
        if not attach:
            rig.stop()
            # The grid stores of the previous run, keyed by world folder and id
            shutil.rmtree(rig.APPDATA / "Undo" / "Worlds", ignore_errors=True)
            rig.STATUS_FILE.unlink(missing_ok=True)
            rig.prepare_world(settings=getattr(request.module, "WORLD_SETTINGS", None))
            rig.launch(undo_config=config)
            api.wait_for_api(max_wait=240)
            rig.load_world(api)
        rig.ensure_character(api)
        rig.focus_gameplay(api)
        yield Game(api, rig.STATUS_FILE)
    finally:
        rig.STATUS_FILE = default_status
        if os.environ.get("UNDO_KEEP") != "1":
            rig.stop()


def plugin_log(module) -> Path:
    return rig.ARTIFACTS / f"{module.__name__}.log"


def plugin_lines(offset: int) -> tuple[list[str], int]:
    """The plugin's lines in the game log from a byte offset on, and the new end"""
    log = rig.APPDATA / "SpaceEngineers.log"
    try:
        data = log.read_bytes()
    except OSError:
        return [], offset
    text = data[offset if offset <= len(data) else 0 :].decode("utf-8", "replace")
    lines = [
        line.split(" ->  ", 1)[-1]
        for line in text.splitlines()
        if " Undo: " in line and "Terminal controls:" not in line
    ]
    return lines, len(data)


@pytest.fixture(autouse=True)
def log_what_the_plugin_did(request):
    _, offset = plugin_lines(0)
    yield
    lines, _ = plugin_lines(offset)
    rig.ARTIFACTS.mkdir(parents=True, exist_ok=True)
    with open(plugin_log(request.module), "a", encoding="utf-8") as file:
        file.write(f"=== {request.node.name}\n" + "".join(f"{l}\n" for l in lines))

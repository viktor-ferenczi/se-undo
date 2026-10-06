# In game tests

Pytest suite driven through the [Remote plugin](../../remote). It runs on an
isolated headless client, so it never touches the game you play with, and it can
run while other test clients are up. This file is about the offline rig; the
dedicated server rig under `ds/` is described in [Docs/TESTING.md](../Docs/TESTING.md).

## One time setup

The Remote plugin repo has to sit next to this one (`se1/plugins/remote`): the rig
imports its Python client and copies its Earth test world. The game's own
`~/.config/SpaceEngineers/SpaceEngineers.cfg` seeds the client's config on the first
run, with experimental mode switched on.
The grid tests need two Remote fixes from CometWorks/remote#28 (`grid_close` through
the player's close request, the target endpoint naming armor blocks), which are on
its main branch. The terminal tests read programs with Remote's `GetProgram` call op
(`get_pb_program`), also on its main branch.

A Pulsar folder of its own, with only the Remote and Undo dev folders enabled:

```bash
~/ws/se/notes/pulsar-dev-instances/new-pulsar-instance.sh ~/.se-test/undo
cp -p ~/.se-test/undo/Interim.bin ~/.se-test/undo/UndoInterim.bin
```

Then register this repo in `~/.se-test/undo/Legacy/Sources/sources.xml` as a
`LocalPlugin` (file `Undo.xml`) next to `remote`, and leave only these two in the
`DevFolder` list of `~/.se-test/undo/Legacy/Profiles/Current.xml`: `remote` and
`AC284074-A676-4930-B47A-F30450988608`.

The renamed launcher keeps the client out of reach of anything that stops
`Interim.bin` by name.

That folder is client slot 0. Further slots (`UNDO_SLOT=1`, `2`, ...) are cloned from
it on first use into `~/.se-test/undo-s<N>`, with user data in
`~/.se-test/undo-s<N>-data` and Remote port `24180 + N`, so several test files can
run at the same time.

## Running

Every test file is a session of its own: it starts a client, loads a fresh copy of
the test world, and stops the client at the end. No file depends on another.

```bash
uv run python tests/run_pieces.py
```

runs all of them, six clients at a time by default, in about six minutes.
`--jobs N` changes the number of clients. A client is only started while that leaves
16 GB of RAM available (`--min-free-gb`), counting about 6 GB per client.

```bash
uv run python tests/run_pieces.py terminal survival
```

runs pieces by name, and a file can be run directly, on slot 0 or another slot:

```bash
UNDO_SLOT=3 uv run pytest tests/test_terminal.py
```

Plain `uv run pytest` runs the files one after the other on one slot, which takes
about 27 minutes.

| File | What it covers |
|---|---|
| `test_build_context.py` | build, raze, referenced blocks, splits, paint, tree option, displaced vanilla keys, the 200 node limit |
| `test_builder_input.py` | the cube builder with real mouse input: place, remove, a line with Ctrl and a drag, rotated blocks, paint one block, repaint the grid |
| `test_grid_ops.py` | paste, delete, Ctrl-Delete, Ctrl-X, paste into a grid, new grid from one block |
| `test_grid_store.py` | retention, the budget question (Yes, No, Escape), the grid history dialog |
| `test_store_policy.py` | "always raise" for oversized backups, the dialog's Paste, Delete and Close buttons |
| `test_history.py` | the default linear history, both ends, fast key presses, HUD text, mixed steps, a chain of steps across a deleted and restored grid, undo from a seat |
| `test_corner_cases.py` | damage, damaged and switched off blocks, a removal that leaves four parts, cargo with items, multi cell blocks, a two grid blueprint, a deleted ship, keys under another screen |
| `test_block_links.py` | every block of a rig full of toolbars, block lists, bound cameras and groups removed and restored, compared through the saved world; inventory; redo of a placement; a restore with something in the way |
| `test_spilled_items.py` | a container with items removed and restored in a world without temporary containers |
| `test_mechanical.py` | rotor, hinge and piston bases removed and restored with their subgrids, the top part removed, a rotor on a moving ship, undo of placing a rotor |
| `test_terminal.py` | checkbox, slider, names, text box, a program saved from the PB editor |
| `test_terminal_more.py` | value kinds, toolbar actions, multi selection, coalescing, programs through the mod API, a running program, steps whose block is gone |
| `test_terminal_endurance.py` | a combobox, and more slider changes than the 30 after which the recording used to stop (SE1-0079) |
| `test_text_context.py` | text boxes of the toolbar config and chat screens |
| `test_options.py` | the option switches: other keys, small limits, full state off, recording only in the terminal, no HUD text, no history in the save, "never store", another storage folder |
| `test_contexts_off.py` | all contexts disabled, vanilla keys back |
| `test_world_save.py` | save, backups, Save As, restore through the load menu |
| `test_persistence_edge.py` | both histories after a load, redo from the grid store, a corrupt or foreign history file, another world, a grid missing from the save |
| `test_survival.py` | survival: off without creative tools, the history waits for them |

A file sets the plugin options of its client with a module level `UNDO_CONFIG`
dict, merged over `rig.UNDO_CONFIG`; `None` puts an option back to its default.
`WORLD_SETTINGS` changes session settings of its test world the same way.

## What a run does

Each file copies the Remote suite's Earth world into its slot's user data folder,
turns it into a creative world with trash removal off, unsupported stations and copy
and paste on, and injects a test station 300 m above the player, the links rig 60 m above
that and a small ship far out in space. The station carries a turret controller and a programmable block,
whose terminals the tests open with F; scripts are enabled. The grid store of the
previous run is removed.
The tests that need a line of sight teleport the character onto the free edge of the
station, since the player spawns inside the Earth base with a wall in every direction.
The rig writes `Remote.cfg` and `Storage/Undo.cfg` (status file, tree option, debug
log, a grid store budget of 1 MB per world) before it starts the client.

The clients are headless, offline (`--no-steam`), 1280x720, lowest quality, audio
off. Rendering stays on: the cube builder and the terminal need a camera. Headless
rendering runs on the Mesa driver of the integrated GPU; the NVIDIA driver cannot
present to an offscreen surface (Remote's `Docs/CommandLineOptions.md`).

No test looks at a picture. Every assertion reads the Remote API, the plugin's status
file (`<user data>/Undo/status.json`, written after every history change) or files
on disk. What the plugin logged during each test, the steps it recorded and each
undo and redo notification, is copied to `tests/artifacts/<test file>.log`; the
pytest output of `run_pieces.py` is in `tests/artifacts/logs/`.

`rig.py` prepares the worlds and runs the client, `harness.py` has what the tests
use to look at the game and to drive it, `conftest.py` starts the client per file.

`UNDO_KEEP=1` leaves the client running, `UNDO_ATTACH=1` reuses it on the next
run of the same file. `UNDO_WINDOWED=1` starts it with a real window, for checks done
by hand, and `UNDO_EXTRA_ARGS` adds launcher options.

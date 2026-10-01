# In game tests

Pytest suite driven through the [Remote plugin](../../remote). It runs on an
isolated headless client, so it never touches the game you play with, and it can
run while other test clients are up.

## One time setup

The Remote plugin repo has to sit next to this one (`se1/plugins/remote`): the rig
imports its Python client and copies its Earth test world. The game's own
`~/.config/SpaceEngineers/SpaceEngineers.cfg` seeds the client's config on the first
run, with experimental mode switched on.
The grid tests need two Remote fixes from CometWorks/remote#28 (`grid_close` through
the player's close request, the target endpoint naming armor blocks); until it is
merged, check out its `fixes` branch there.

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

## Running

```bash
uv run pytest
```

Each run copies the Remote suite's Earth world into `~/.se-test/undo-data`, turns
it into a creative world with trash removal off, unsupported stations and copy and
paste on, and injects a test station 300 m above the player and a small ship far out
in space. The station carries a turret controller, whose terminal the terminal tests
open with F, and a programmable block; scripts are enabled. It also removes the grid store the previous run left under `Undo/Worlds`.
The grid tests teleport the character onto the free edge of the station and paste
their test grid into the open air beside it, since the player spawns inside the Earth
base with a wall in every direction.
It writes `Remote.cfg` (port 24176) and `Storage/Undo.cfg` (status file, tree
option, debug log) before it starts the client, and stops that client at the end.

`UNDO_KEEP=1` leaves the client running, `UNDO_ATTACH=1` reuses it on the next
run. `UNDO_WINDOWED=1` starts it with a real window, for checks done by hand. The plugin writes `~/.se-test/undo-data/Undo/status.json` after every history
change; the tests read the histories and the last notification from there.

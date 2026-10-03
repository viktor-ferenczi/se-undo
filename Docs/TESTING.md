# Testing

Three layers. The first needs nothing but the .NET SDK, the other two start their own
isolated game processes and leave the game you play with alone. All game clients are
headless.

| What | Command | Time |
|---|---|---|
| Unit tests: history, storage, grid store, text history | `dotnet test UndoTests` | seconds |
| In game suite, offline clients | `uv run python tests/run_pieces.py` | about 6 minutes |
| Dedicated server suite, server plus one client | `uv run pytest tests/ds` | about 3 minutes |

The game suites use different folders and ports and can run at the same time. A
client in a world takes about 5.5 GB of RAM, the server about 3 GB.

## Offline rig

Setup and details are in [tests/README.md](../tests/README.md). Every test file is
a session of its own on a client slot: slot 0 is the Pulsar folder `~/.se-test/undo`
with user data `~/.se-test/undo-data` and Remote port 24176, further slots are cloned
from it. `tests/run_pieces.py` runs the 19 files side by side, six clients at a time
by default, and only starts a client while 16 GB of RAM stay available. Each file
copies the Remote plugin's Earth world, adds the test grids and loads it.

`UNDO_KEEP=1` leaves the client of a file running, `UNDO_ATTACH=1` reuses it.

## Dedicated server rig

One time setup, a second Pulsar folder for the client that joins the server:

```bash
~/ws/se/notes/pulsar-dev-instances/new-pulsar-instance.sh ~/.se-test/undo-mp
```

```bash
cp -p ~/.se-test/undo-mp/Interim.bin ~/.se-test/undo-mp/UndoMpInterim.bin
```

Its `Legacy/Sources/sources.xml` has to list `remote`, `se-undo` and `direct-transport`
as local plugins, which it does when the folder is cloned from a Pulsar folder that has
them. The rig writes the profile itself on every start.

The server side needs the machine's Magnetar install (`~/.config/Magnetar`, with
`direct-transport` among the local plugin sources of `Magnetar/Sources/sources.xml`)
and the Dedicated Server from Steam. The rig copies the plugin sources into its own
Magnetar config folder once and writes everything else on each run:

| | Where |
|---|---|
| Magnetar config (`-config`) | `~/.se-test/undo-ds/magnetar` |
| Server data (`-path`), world `Saves/UndoTestServer` | `~/.se-test/undo-ds/data` |
| Server log | `~/.se-test/undo-ds/server.log` |
| Server port | UDP 27116 |
| Client user data, game log, `launch.log` | `~/.se-test/undo-mp-data` |
| Client Remote port | 24177 |

`tests/ds/ds_rig.py` has the paths; each can be changed with an environment variable
named there. The suite starts a fresh server with the client as its administrator
and checks that the plugin stays off on the client, even with creative tools on.

To bring the pair up by hand and drive the client through its Remote API:

```bash
uv run python tests/ds/ds_rig.py start --admin
```

```bash
uv run python tests/ds/ds_rig.py stop
```

`UNDO_KEEP=1` leaves server and client running after a test run, `UNDO_ATTACH=1`
reuses a running pair for the administrator tests.

## What is not covered

Hosting for friends and joining a friend's lobby need Steam lobbies and two Steam
accounts, so neither mode has a test. See section 13 of [DESIGN.md](DESIGN.md) for
that and for the full list of what ran where.

# Testing

Three layers. The first needs nothing but the .NET SDK, the other two start their own
isolated game processes and leave the game you play with alone. All game clients are
headless.

| What | Command | Time |
|---|---|---|
| Unit tests: history, storage, grid store, text history, companion protocol | `dotnet test UndoTests` | seconds |
| In game suite, offline clients | `uv run python tests/run_pieces.py` | about 6 minutes |
| Dedicated server suite, a server per file and up to two clients | `uv run pytest tests/ds` | about 18 minutes |
| Lobby suite, a host and a joined client | `uv run pytest tests/lobby` | about 4 minutes |

The game suites use different folders and ports and can run at the same time. A
client in a world takes about 5.5 GB of RAM, the server about 3 GB.

Both plugins also build for .NET Framework 4.8, which Pulsar Legacy and Magnetar Legacy
use. On Windows the projects target it next to .NET 10. On Linux a compile check works
from a clean copy of the working tree (the `obj` folders of the .NET 10 build would be
compiled along otherwise), with the reference assemblies from the NuGet cache:

```bash
ROOT=$(ls -d ~/.nuget/packages/microsoft.netframework.referenceassemblies.net48/*/build | tail -1)
MagnetarBin=~/.config/Magnetar/Libraries/MagnetarInterim dotnet build ServerPlugin -c Release -p:TargetFrameworks=net48 -p:TargetFramework= -p:TargetFrameworkRootPath=$ROOT/
```

The same for `ClientPlugin`, without `MagnetarBin`.

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

Its `Legacy/Sources/sources.xml` has to list `direct-transport` as a local plugin, which
it does when the folder is cloned from a Pulsar folder that has it. The rig writes the
profile on every start and points the `remote` and `se-undo` sources at the working
copies next to each other (`se1/plugins/remote` and this repo). The second client's
Pulsar folder, `~/.se-test/undo-mp2`, is cloned from the first on its first start.

The server side needs the machine's Magnetar install (`~/.config/Magnetar`, with
`direct-transport` among the local plugin sources of `Magnetar/Sources/sources.xml`)
and the Dedicated Server from Steam. The rig copies the plugin sources into its own
Magnetar config folder once, registers this repo there as the `se-undo` source with
`UndoServer.xml`, and writes everything else on each run:

| | Where |
|---|---|
| Magnetar config (`-config`) | `~/.se-test/undo-ds/magnetar` |
| Server data (`-path`), world `Saves/UndoTestServer` | `~/.se-test/undo-ds/data` |
| Server log | `~/.se-test/undo-ds/server.log`, the game's own in `data/SpaceEngineersDedicated.log` |
| Companion config, `Undo.cfg` | `~/.se-test/undo-ds/data` |
| Companion status, grid stores | `~/.se-test/undo-ds/data/Undo` |
| Server port | UDP 27116 |
| Client user data, game log, `launch.log` | `~/.se-test/undo-mp-data` |
| Client Remote port | 24177 |
| Second client user data | `~/.se-test/undo-mp2-data` |
| Second client Remote port | 24178 |

`tests/ds/ds_rig.py` has the paths; each can be changed with an environment variable
named there. Every test file starts a fresh server with the first client as its
administrator, creative tools on:

- `test_dedicated.py`, a server without the companion: the plugin stays off on the
  client.
- `test_companion.py`, a server with it: the steps of the build context, terminal
  steps, the grid history dialog and an ownership refusal with the first client; the
  second client, a regular player, sees the restored blocks and merged grids, gets the
  vanilla keys, and has its untrusted requests refused (a step without creative tools,
  an oversized message, a burst); at the end the server restarts on its saved world
  and an older step is undone. The histories come from the companion's status file,
  `status-players.json`.
- `test_companion_more.py`, a server with it, autosaving every minute: a terminal
  step from the client's terminal screen, a program, a block toolbar and a grid name
  recorded on the server, a paste into a grid, a new grid from one block, a rotor base
  with its subgrid, a block group put back, the refusals for something in the way and
  for a grid the second client deleted, the grid history dialog's Paste and Delete,
  and the histories in the world after an autosave.
- `test_client_without_undo.py`, a server with it and a second client without the
  plugin, which joins once: a client that left and joined again with the same id
  within seconds timed out during the world download.

The terminal steps and the untrusted requests go to the server as raw companion
messages through Remote's `POST /v1/game/mod-message`, which the Remote working copy
needs (branch `server-companion` of CometWorks/remote until it merges). The block
group checks of `test_companion_more.py` and of the lobby tests use Remote's
`block_groups` get op, on its branch `test-matrix` until that merges.

To bring the pair up by hand and drive the client through its Remote API:

```bash
uv run python tests/ds/ds_rig.py start --admin [--two] [--no-companion]
```

```bash
uv run python tests/ds/ds_rig.py stop
```

`UNDO_KEEP=1` leaves the server and the clients running after a test run,
`UNDO_ATTACH=1` reuses them while iterating on one file.

## Lobby rig

A friends game without Steam (DirectTransport, SE1-0076): one client hosts the test
world, a second one joins it. Both Pulsar folders are cloned from the dedicated server
rig's client on their first start, so `~/.se-test/undo-mp` has to be set up first.

| | Host | Joined client |
|---|---|---|
| Pulsar folder | `~/.se-test/undo-lobby-host` | `~/.se-test/undo-lobby-join` |
| User data | `~/.se-test/undo-lobby-host-data` | `~/.se-test/undo-lobby-join-data` |
| Remote port | 24198 | 24199 |
| Lobby port | UDP 27131 | |

The host loads a fresh creative copy of the test world offline, saves it and loads it
again as a friends game; the joined player is an administrator in it, for the
teleport. `tests/lobby/test_lobby.py` checks that the host's own undo and redo reach
the joined client (blocks under their old ids, merged parts, re-created grids, block
groups, programs), and that the host serves the joined player like the companion. The
joined player's histories are in the host's `Undo/status-players.json`.

```bash
uv run python tests/lobby/lobby_rig.py start
```

```bash
uv run python tests/lobby/lobby_rig.py stop
```

`UNDO_KEEP=1` and `UNDO_ATTACH=1` work as in the other rigs.

## What is not covered

Pulsar Legacy and Magnetar Legacy run .NET Framework 4.8 on Windows only. The client
plugin was run under Proton (Wine Mono) with the offline tests, see SE1-0098 in the
workspace for that recipe and for what is left for Windows. See section 13 of
[DESIGN.md](DESIGN.md) for what ran where.

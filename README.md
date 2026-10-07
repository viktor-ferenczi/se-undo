# Undo

Space Engineers 1 client plugin for [Pulsar](https://github.com/SpaceGT/Pulsar), with a
server companion for [Magnetar](https://github.com/CometWorks/magnetar) dedicated servers.

Video: [undo after editing a ship](https://youtu.be/fS_KpBiItiU)

[![Undo after editing a ship](Docs/UndoShipThumbnail.png)](https://youtu.be/fS_KpBiItiU)

Video: [undo and redo of terminal changes](https://youtu.be/xcvybKVaNjA)

[![Undo and redo of terminal changes](Docs/UndoTerminalThumbnail.png)](https://youtu.be/xcvybKVaNjA)

Ctrl-Z reverts the latest operation in the current context, Ctrl-Y applies it again.
Covered: placing and removing blocks, painting and skins, pasting and deleting grids,
terminal property changes, block and grid names, Custom Data, block toolbars, programmable
block programs and single line text fields. The history is saved with the world and follows its backups.

Grids the plugin captures for undo (deleted, pasted, split) are kept under a size budget
and listed in a grid history dialog, sortable by time, name, block count and more. Double
click puts a backed up grid group on the clipboard for pasting. Ctrl-H opens it in
gameplay.

![Grid history dialog](Docs/UndoHistory.png)

Vanilla binds Ctrl-Z to relative dampeners and Ctrl-Y to toggling all reactors. While
this plugin is enabled those move to Ctrl-Shift-Z and Ctrl-Shift-Y. In gameplay Ctrl-Z
is still relative dampeners while there is nothing to undo. All four bindings,
the history limits and the storage locations are in the plugin settings.

Where it works, always in creative or in survival with creative tools enabled:

- Offline games.
- Friends multiplayer as the host. Joined players who run Undo get it too.
- Joining a friend's game, if the host runs Undo.
- On a dedicated server that runs the Undo companion, the Magnetar plugin of the same
  name from MagnetarHub. The server keeps each player's history in the world save and
  replays the steps, so removed blocks and deleted grids come back as themselves.
  The client only asks for a step; the server checks every one of them.

Where it is off, the plugin records nothing and takes no keys: Ctrl-Z, Ctrl-Y and
Ctrl-H do what they do without it. In survival the history waits until creative
tools are back on.

Design: [Docs/DESIGN.md](Docs/DESIGN.md). It records what the plugin hooks, how undo is
replayed through the game's own requests so multiplayer stays in sync, what is
allowed in survival, and how the server companion works.

## Development

Based on the [server plugin template](https://github.com/CometWorks/server-plugin-template):
`ClientPlugin` (Pulsar), `ServerPlugin` (Magnetar) and `Shared`, which both compile. The
manifests are `Undo.xml` for PluginHub and `UndoServer.xml` for MagnetarHub, with the same id.
Run `setup.py` once to detect the game and dedicated server folders, then build the solution.
Load the working copy through a Pulsar or Magnetar development folder. Builds deploy only if
`Pulsar` or `MagnetarData` is set in `Directory.Build.props.user`, see the template's README
for the details.

Tests: `dotnet test UndoTests` runs the history, storage, grid store and protocol unit
tests without the game. The in game suites under `tests/` run on isolated headless
clients, offline and joined to a dedicated server with and without the companion, see
[Docs/TESTING.md](Docs/TESTING.md).

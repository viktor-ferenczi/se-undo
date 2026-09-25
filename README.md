# Undo

Space Engineers 1 client plugin for [Pulsar](https://github.com/SpaceGT/Pulsar).

Ctrl-Z reverts the last operation in the current context, Ctrl-Y applies it again.
Covered: placing and removing blocks, painting and skins, pasting and deleting grids,
terminal property changes, block and grid names, programmable block programs and single
line text fields. The history is saved with the world and follows its backups.

Grids the plugin captures for undo (deleted, pasted, split) are kept under a size budget
and listed in a grid history dialog, sortable by time, name, block count and more. Double
click puts a backed up grid group on the clipboard for pasting.

Vanilla binds Ctrl-Z to relative dampeners and Ctrl-Y to toggling all reactors. While
this plugin is enabled those move to Ctrl-Shift-Z and Ctrl-Shift-Y. All four bindings,
the history limits and the storage locations are in the plugin settings.

Design: [Docs/DESIGN.md](Docs/DESIGN.md). It records what the plugin hooks, how undo is
replayed through the game's own requests so multiplayer stays in sync, and what is
allowed in survival.

## Development

Based on the [client plugin template](https://github.com/CometWorks/client-plugin-template).
Run `setup.py` once to detect the game folder, then build the solution. Each build deploys
into Pulsar's `Local` plugin folder, see the template's README for the details.

# Undo

Space Engineers 1 client plugin for [Pulsar](https://github.com/SpaceGT/Pulsar).

Ctrl-Z reverts the last operation in the current context, Ctrl-Y applies it again.
Covered: placing and removing blocks, painting and skins, pasting and deleting grids,
terminal property changes, block and grid names, programmable block programs and single
line text fields. The history is saved with the world and follows its backups.

Both keys can be rebound in the plugin settings. Vanilla binds Ctrl-Z to relative
dampeners and Ctrl-Y to toggling all reactors; while this plugin is enabled those two
need another binding.

Design: [Docs/DESIGN.md](Docs/DESIGN.md). It records what the plugin hooks, how undo is
replayed through the game's own requests so multiplayer stays in sync, and what is
allowed in survival.

## Development

Based on the [client plugin template](https://github.com/CometWorks/client-plugin-template).
Run `setup.py` once to detect the game folder, then build the solution. Each build deploys
into Pulsar's `Local` plugin folder, see the template's README for the details.

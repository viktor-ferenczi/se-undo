# Undo plugin design

Client plugin for Space Engineers 1, loaded by Pulsar. Ctrl-Z reverts the last
operation in the current context, Ctrl-Y applies it again. Primary use: offline ship
design in creative, then friends multiplayer and creative servers.

All game paths below were read in the decompiled client code on 2026-09-25.
Paths are relative to `Sandbox.Game/Sandbox/Game/` unless another assembly is named.

## 1. Facts the design rests on

- The game has no undo except the 50 step text undo inside
  `MyGuiControlMultilineEditableText`, the control the programmable block (PB) editor
  uses. It reacts to the control characters U+001A (Ctrl-Z) and U+0019 (Ctrl-Y). The
  plugin leaves that control alone.
- `MyGuiControlTextbox` (single line fields) has no undo. Its `KeypressUndo` and
  `KeypressRedo` are empty stubs, and `HandleInput` ignores Z and Y while Ctrl is
  held, so Ctrl-Z never types a character.
- Ctrl-Z and Ctrl-Y are bound in gameplay: `DAMPING_RELATIVE` and
  `TOGGLE_REACTORS_ALL`, both handled only in `MyGuiScreenGamePlay.HandleUnhandledInput`
  through `MyControllerHelper.IsControl`.
- Every world change a player makes is a request to the server: `BuildBlocksRequest`,
  `RazeBlocksRequest`, `TryPasteGrid_Implementation`, `OnGridClosedRequest`,
  `SkinBlockRequest`, `SyncPropertyChanged_Implementation`, `SetCustomNameEvent`,
  `OnChangeDisplayNameRequest`, `UpdateProgram`. Offline the "server" is the same
  process and requests are locally invoked, so the same code works in all modes. The
  plugin replays through these public entry points and never mutates entities directly,
  so replication to friends and permission checks come for free.
- A file only survives save, backup and restore when it is written into the save's
  staging folder `<world>/.new` while the snapshot is written. The game then copies it
  to the world folder, into every `Backup/<timestamp>/` folder, and back on restore.
  Files written straight into the world folder are deleted by the save cleanup;
  subfolders survive but are neither backed up nor restored.
- Server side pastes renumber entity ids (`MyEntities.RemapObjectBuilderCollection`),
  so nothing in the history may depend on an entity id staying valid across an undo.
- A client connected to a dedicated server has no save folder. `CurrentPath` points at
  an uncreated placeholder under the local Saves folder that can collide with a local
  world of the same name.

## 2. Session modes and permission policy

| Mode | Detection | Policy |
|---|---|---|
| Offline | `MyMultiplayer.Static == null` | Full functionality, subject to creative tools in survival |
| Hosting a lobby (friends) | `Sync.IsServer && MyMultiplayer.Static is MyMultiplayerLobby` | Same as offline. Replays go through the request methods, so clients get them |
| Client of a lobby | `MyMultiplayer.Static is MyMultiplayerLobbyClient` | Same as DS client |
| Client of a dedicated server | `MyMultiplayer.Static is MyMultiplayerClientBase` and not a lobby client | Client only. History is stored locally, see section 8 |

Within a mode, each operation kind has a predicate the plugin evaluates before applying
an undo or redo. It mirrors the server check so the plugin refuses cleanly instead of
sending a request that fails with a notification:

| Operation kind | Predicate (client side mirror of the server check) |
|---|---|
| Build blocks | Always allowed. In survival without creative tools the blocks come back as construction sites and cost components, which is what a player could do by hand |
| Raze blocks | `MySession.Static.CreativeMode \|\| MySession.Static.CreativeToolsEnabled(Sync.MyId)` |
| Restore blocks with full state (paste into grid) | `HasPlayerCreativeRights(Sync.MyId)`; otherwise fall back to Build blocks |
| Paste grids, close grids, group snapshot restore | `IsCopyPastingEnabledForUser(Sync.MyId)` and the ownership rule of `OnGridClosedRequest` (admin, big owner or faction leader) |
| Paint and skin | Ownership rule of `ColorGridOrBlockRequestValidation` (no creative rights needed) |
| Terminal property, block name | `control.CanLocalPlayerChangeValue()` on the resolved block |
| Grid name | Same as above, big owner |
| PB program | `IsUserScripter(Sync.MyId)` |

Survival as a regular player therefore has: build, paint, terminal property, names, PB
program. It does not have raze, paste, close and snapshot restore, so undo cannot be
used to remove blocks for free or to duplicate ships. Enabling creative tools unlocks
the rest, which matches what the player could do through the game anyway. Nothing in
the plugin needs admin rights of its own.

## 3. Contexts and key handling

A context is where Ctrl-Z looks for the last operation. Each has its own history and
its own node limit.

| Context | Active when | History | Persisted |
|---|---|---|---|
| Build | `MyGuiScreenGamePlay` has focus and no other game screen is open (`MyGuiScreenGamePlay.ActiveGameplayScreen == null`) | Block build and raze, grid paste and close, paint and skin | Yes, per world |
| Terminal | `MyGuiScreenTerminal` is the focused screen and no text box has focus | Terminal property changes, block and grid names, PB program updates | Yes, per world |
| Text | A `MyGuiControlTextbox` has keyboard focus, in any screen | Text and caret snapshots of that control | No, dies with the control |
| PB editor | `MyGuiScreenEditor` | Vanilla undo, untouched | No |

Terminal changes made while the terminal is closed (toolbar toggles, hotkeys, scripts)
are not recorded by default. They would flood the history and are trivially reversible
by the same key. A config option records them anyway, which the test suite uses.

Key handling per context:

- Build: Harmony prefix on `MyGuiScreenGamePlay.HandleUnhandledInput`. When the undo or
  redo binding is newly pressed, run it and mark the key as consumed for this frame. A
  postfix on `MyControllerHelper.IsControl(context, controlId, ...)` rewrites the
  answer for the two displaced vanilla controls: it returns false for
  `DAMPING_RELATIVE` and `TOGGLE_REACTORS_ALL` while the plugin consumed the key, and
  returns true for them when their replacement binding from the config is newly
  pressed (`MyControlStateType.NEW_PRESSED` only). The vanilla code that follows,
  sound, `SwitchDamping`, `SetDampeningEntity`, `SwitchReactors` and the input
  recording, runs unchanged. Disabling the vanilla controls instead is not an option:
  with the Ctrl variant disabled, `MyVRageInput.IsPriorityKeyPressed` lets Ctrl-Z fall
  through to plain `DAMPING`.
- Terminal: prefix on `MyGuiScreenTerminal.HandleUnhandledInput`, same pattern. A
  focused text box consumes the key before this runs, so the two never clash.
- Text: prefix on `MyGuiControlTextbox.HandleInput`. When the control has focus and the
  binding is newly pressed, restore the previous snapshot and return the control (input
  consumed).

Default bindings are Ctrl-Z and Ctrl-Y in every context. This takes relative dampeners
and "toggle all reactors" away from their default keys while the plugin is enabled, so
the config provides replacement bindings for both: Ctrl-Shift-Z for relative dampeners
and Ctrl-Shift-Y for toggle all reactors. All four bindings are `Binding` values in the
config dialog and can be changed or cleared. When a replacement is cleared the vanilla
control is simply unreachable through the keyboard while undo holds its key, which the
dialog says next to the option. The plugin's bindings are only checked when the
respective context is active, so Ctrl-Shift-Z in a text box still means nothing.

## 4. History model

```
History (one per persisted context and world)
  nodes: List<Node>        capped, oldest dropped first
  current: node id         the node whose forward action is applied
  root: node id            sentinel, "nothing applied"

Node
  id, parentId, childIds   tree; with the tree option off there is one child per node
  createdUtc
  label                    short text for HUD notifications: "placed 3 blocks"
  forward: Op[]            what the player did, replayed on redo
  reverse: Op[]            replayed on undo, in reverse order
  bytes                    serialized size, for the memory budget
```

Undo: apply `current.reverse`, move `current` to its parent. Redo: pick a child (the
most recently visited one), apply its `forward`, move `current` there. A new player
action becomes a child of `current`. With the tree option off, the other children of
`current` are dropped when a new action is recorded, which gives the usual linear
undo. With it on, they stay, and redo walks the last visited branch. A branch picker
dialog is not part of the first version; the data model already supports it.

Limits per persisted context: a node count (default 200) and a byte budget for
snapshots (default 64 MB), both in the config. When either is exceeded the oldest
nodes are dropped from the root side. A dropped node's descendants on other branches
are dropped with it.

Replayed actions are not recorded: a re-entrancy flag on the recorder is set while the
executor runs an op, and every record hook checks it.

## 5. Recorded operations

Each Op is a small serializable record: a kind and its arguments. Ops refer to grids by
a plugin assigned grid handle and to blocks by `(gridHandle, Vector3I position)`, see
section 6. Every op has a validator (the predicate from section 2) and an apply method.

| Player action | Record hook | Forward op | Reverse op | Notes |
|---|---|---|---|---|
| Place blocks (single, line, plane) | Prefix on `MyCubeGrid.BuildBlocks(Vector3 color, MyStringHash skin, HashSet<MyBlockLocation>, long, long)` and on the `MyBlockBuildArea` overload | `BuildBlocks(grid, locations, color, skin)` | `RazeBlocks(grid, positions)` | Locations carry definition, orientation and min/max, enough to rebuild. Client allocates the block entity ids before the request, but positions are used as the stable reference |
| Place a block into empty space (new grid) | Prefix on the private static `MyCubeBuilder.RequestGridSpawn(GridSpawnRequestData)` when the server is local (offline, hosting), which carries the definition, position and visuals; the new grid is then the next `MyEntities.OnEntityAdd` of a one block grid built by the local identity. On a DS client the request is not invoked locally, so the same `OnEntityAdd` match is keyed on the cube builder placement position instead | `PasteGrids(snapshot)` | `CloseGrids(grid)` | Snapshot is one block, cheap |
| Remove blocks with the cube builder | Prefix on `MyCubeGrid.RazeBlocks(List<Vector3I>, long, ulong)` and `RazeBlocks(ref Vector3I, ref Vector3UByte, long)`; snapshot each `MySlimBlock.GetCopyObjectBuilder()` first | `RazeBlocks` | `RestoreBlocks(grid, blockBuilders)` | Restore uses `PasteBlocksToGrid` with a one grid builder positioned on the target grid, which keeps names, settings, integrity. Without creative rights it degrades to `BuildBlocks` from the builders' definition, orientation, color, skin |
| Removal that splits the grid | `MyCubeGrid.OnSplitGridCreated` / `OnGridSplit` fired while the raze action is open | as above | `CloseGrids(pieces)`, `MergeIntoGrid(mainGrid, pieceSnapshots)`, then `RestoreBlocks` | The split off pieces are new grids; their builders are captured right after the split. `MergeIntoGrid` is `PasteBlocksToGrid` with the pieces' builders. If capture fails (piece already gone), the node is replaced by a group snapshot node, section 7 |
| Paste grids from clipboard (free placement) | Prefix on `MyGridClipboard.PasteGridInternal` captures the clipboard builders; result grids come from a postfix on the nested `MyCubeGrid+PasteGridData.TryPasteGrid` (`___m_pastedGrids`) when the server is local, else from `OnEntityAdd` matching (section 6) | `PasteGrids(builders, position)` | `CloseGrids(handles)` | Same paste request the clipboard uses: `MyMultiplayer.RaiseStaticEvent(TryPasteGrid_Implementation, MyPasteGridParameters)` |
| Paste blocks into an existing grid | Postfix on `MyCubeGrid.PasteBlocksToGridClient_Implementation(MyObjectBuilder_CubeGrid, MatrixI)` gives the merged builder and its transform | `MergeIntoGrid(grid, builder)` | `RazeBlocks(grid, transformed positions)` | Runs on every machine, so the client sees the exact positions |
| Delete grid or group (clipboard Delete, Cut) | Prefix on `MyGridClipboard.DeleteGrid` / `DeleteGroup`; snapshot `grid.GetObjectBuilder(true)` for each grid (same as `CopyGridInternal`: clear pilots and turret shooting) | `CloseGrids` | `PasteGrids(snapshots at original position and velocity)` | Restored grids get new ids; handles are remapped |
| Paint or skin blocks, area or whole grid | Prefix on `MyCubeGrid.ChangeColorAndSkin(MySlimBlock, Vector3?, MyStringHash?)` records old and new per block; only when the change was initiated locally (a prefix on `SkinBlocks` / `SkinGrid` opens a "paint stroke" and `ChangeColorAndSkin` calls while a stroke is open are attributed to it) | `Paint(grid, [(pos, hsv, skin)])` | `Paint(grid, [(pos, oldHsv, oldSkin)])` | Holding the mouse button calls `SkinBlocks` every frame. Calls are coalesced into one node until the button is released or 300 ms pass without a call. Apply groups equal (hsv, skin) runs into boxes and calls `SkinBlocks(min, max, hsv, skin, false)` per box |
| Terminal property change | Prefix and postfix on `MyTerminalValueControl<TBlock, TValue>.SetValue(TBlock, TValue)` for every closed control type found through `MyTerminalControlFactory.GetControls(Type)` for all registered block types. Prefix reads `GetValue(block)` as the old value | `SetProperty(block, controlId, value)` | `SetProperty(block, controlId, oldValue)` | Values: bool, float, long, Color, StringBuilder, enums, MyStringId. Multi select changes with N target blocks become one node with N ops. Apply resolves the control by id via `MyTerminalControlFactory.GetControls` and calls `SetValue`, which triggers the normal sync. See risk R1 for the closed generic patching |
| Block custom name | Covered by the terminal "Name" text box through `SetValue`; also a prefix on `MyTerminalBlock.SetCustomName(string)` when the terminal is open, deduplicated with the property node | `SetCustomName` | `SetCustomName(old)` | Text box edits are committed once when the field loses focus or Enter is pressed, so one node per rename |
| Grid name | Prefix on `MyCubeGrid.ChangeDisplayNameRequest(string)`; old value is `DisplayName` | `SetGridName` | `SetGridName(old)` | Applied when the server broadcast arrives |
| PB program | Prefix on `MyProgrammableBlock.SendUpdateProgramRequest(string)` (client) and on `UpdateProgram(string)` when `Sync.IsServer`; old value is the current `IMyProgrammableBlock.ProgramData` | `SetProgram(pb, source)` | `SetProgram(pb, oldSource)` | Apply sets `IMyProgrammableBlock.ProgramData`, which recompiles or sends the request. Runtime state and Storage are lost, accepted. Sources are stored gzip compressed |
| Single line text box edits | `TextChanged` on the focused `MyGuiControlTextbox`; snapshots of (text, caret) coalesced while typing continues within 500 ms | n/a | n/a | Transient per control, keyed by a `ConditionalWeakTable<MyGuiControlTextbox, TextHistory>`. Default 100 snapshots per control |

Not recorded in the first version: inventory transfers, production queue changes,
projector settings that are already terminal properties (those are covered), merge
block merges, grid conversion (station/ship), voxel edits, welding and grinding in
survival. Inventory and queue undo are feasible later through `MyInventory.TransferByUser`
and the `*QueueItemRequest` methods, but item ids change and other actors consume
items, so it would be best effort only.

## 6. Entity references and id remapping

Grids are referred to by a handle, a plugin assigned integer stored in the history.
The `GridRegistry` maps handle to current `EntityId` and back. It is filled on load
from `MyEntities` (existing grids get handles on first use) and updated whenever the
plugin re-creates a grid.

Blocks are referred to by `(gridHandle, Vector3I min)`. Positions survive re-creation
of a grid from a builder, block entity ids don't. Terminal ops resolve the block at
apply time by `grid.GetCubeBlock(pos)?.FatBlock` and refuse if it isn't the expected
definition.

Finding the grids created by a paste request:

- Server local (offline, hosting): postfix on the nested `MyCubeGrid+PasteGridData.TryPasteGrid`
  reads `___m_pastedGrids`. The paste op is tagged with a correlation id before the
  request; the postfix runs on the same thread later in the paste job, and the
  executor matches by order of pending pastes, which the game processes FIFO.
- DS client: the server never reports the new ids. `MyEntities.OnEntityAdd` is watched
  for a limited time after the request; a candidate matches when its `DisplayName`,
  block count and position (within a small tolerance of the requested
  `PositionAndOrientation`) match a pending paste. Unmatched pastes leave the node in
  a "reference lost" state: undoing it is refused with a notification and the node is
  kept so redo of earlier nodes still works.

The registry is saved with the history as `(handle, entityId)` pairs. On load, entity
ids that no longer exist are marked lost; a node whose ops reference a lost handle is
refused, not dropped, because a later node may re-create the grid and reattach the
handle.

## 7. Grid group snapshot fallback

A `GroupSnapshot` op holds the builders of a logical grid group (`GetConnectedGrids(GridLinkTypeEnum.Logical)`)
with their positions, plus the handles of the current grids. Apply: `CloseGrids` on the
current grids of those handles, then `PasteGrids` with the saved builders at their saved
positions, then remap the handles to the new grids. It needs paste rights, so it is
never available to a survival player without creative tools.

It is used only when the plugin cannot build a cheaper reverse: a split whose pieces
could not be captured, or an op whose apply failed half way and left the group in an
unknown state (section 9). Because the snapshot has to exist before the change, the
recorder takes a group snapshot lazily on the first action against a grid group in a
session and refreshes it when a node is applied successfully, keeping at most one
snapshot per group and dropping it when the group falls out of the history. The byte
budget in section 4 covers these snapshots.

## 8. Persistence

File: `Undo.xml.gz` in the world folder, one top level file. The root element is the
plugin's own `UndoDocument` (version, both persisted histories, the grid registry),
serialized with `System.Xml.Serialization.XmlSerializer` and gzip compressed. Grid and
block snapshots are stored as XML text produced by
`MyObjectBuilderSerializerKeen.SerializeXML(Stream, ob)`, nested as element text. The
game's own `MyObjectBuilderSerializer` cannot be used: it refuses paths outside the Mods
and Content folders. Keen's serializer handles the polymorphic object builders; the
outer gzip makes the nested XML cheap. A binary (protobuf) variant is not needed; the
history is small next to the world files.

Hooks:

- Snapshot: `MySession.OnSavingCheckpoint` (main thread, inside `MySession.Save`).
  The plugin serializes the document into a byte array here, while the game state is
  consistent.
- Write: postfix on `MyLocalCache.SaveCheckpoint(MyObjectBuilder_Checkpoint, string sessionPath, out ulong, List<MyCloudFile>)`,
  only when `sessionPath` ends with `.new`. Writes the byte array to
  `<sessionPath>/Undo.xml.gz` and adds a `MyCloudFile` to `fileList` so cloud saves
  carry it. This runs on the save worker thread, hence the snapshot step above.
- Load: `MySessionComponentBase.BeforeStart` of the plugin's session component
  (`[MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]`, registered automatically
  from plugin assemblies by `MySession.RegisterComponentsFromAssembly`). Entities
  exist and `CurrentPath` is set. Missing file or version mismatch means an empty
  history, never an error dialog.
- Unload: `MySession.OnUnloading` clears the in-memory histories and text histories.

Backups and restores need no extra code: `MySessionSnapshot.Backup` copies all top
level files of the world folder, and `MyGuiScreenLoadSandbox.CopyBackupUpALevel`
deletes the top level files and copies the backup's files back, including ours. Save
As from within the game writes a fresh snapshot through the same staging folder, so
the file follows. Save As from the Load menu copies top level files, so it follows too.

Client of a server (dedicated or someone else's lobby): no save folder, and no save
event. The document goes into the plugin's own storage folder, with one sub-folder per
server and per player character:

```
<storage root>/Servers/<server>/<player>/<world>.xml.gz

storage root   default <UserDataPath>/Undo, configurable
server         Sync.ServerId, plus the sanitized host name for readability when known
player         Sync.MyId (the Steam id) and the local identity id
               (MySession.Static.LocalPlayerId), joined with an underscore, so two
               accounts or a reset identity on the same server don't share a history
world          sanitized SessionName, plus WorldId when it isn't Guid.Empty
```

It is written on `OnUnloading`, when the `OnServerSaving(true)` RPC arrives, and at
most once per the configured interval after a change (default one minute). This history
is not tied to the server's own backups; if the server restores an older world the
plugin refuses nodes whose grids are gone, as in section 6. Old files are removed when
they are older than the configured retention (default 90 days) so the folder does not
grow with every server ever visited. The config has a switch to turn client side
persistence off.

## 9. Applying an action

`Executor.Undo()` / `Redo()` run on the main thread from the key handlers:

1. Pick the node. If there is none, notify "Nothing to undo" and stop.
2. Validate every op of the node with its predicate and reference resolution. If any
   fails, notify why ("Needs creative tools", "Grid no longer exists") and stop. Nothing
   is applied partially by the plugin's own choice.
3. Set the re-entrancy flag, apply the ops in order, clear the flag.
4. Ops that complete asynchronously (paste, close, grid name, paint on a client) are
   tracked by the `Pending` list: the op registers what it expects (a grid added, a
   grid removed, a broadcast arrived) and the executor keeps the history locked for
   further undo or redo until they complete or a timeout of 5 seconds passes. On
   timeout the node is marked "unknown result": the next undo of it goes through the
   group snapshot path if a snapshot exists, otherwise it is refused.
5. Move `current`, show a HUD notification with the node label.

Failures reported by the server (`BuildBlocksFailedNotify`, `OnColorGridBlockFailed`,
`ValidationFailed`) are already shown by the game. The plugin also marks the node
"unknown result" when it sees them while a pending op is open.

## 10. Configuration

Stored by the template's `ConfigStorage` in `<UserDataPath>/Storage/Undo.cfg`, edited
through the generated dialog.

Everything with a number or a key in this document is an option here; the code has no
other tunables.

| Option | Default | Notes |
|---|---|---|
| Undo binding, Redo binding | Ctrl-Z, Ctrl-Y | `Binding` type from the template; checked only in the active context |
| Relative dampeners binding | Ctrl-Shift-Z | Replacement for the displaced vanilla `DAMPING_RELATIVE`; clear to drop it |
| Toggle all reactors binding | Ctrl-Shift-Y | Replacement for the displaced vanilla `TOGGLE_REACTORS_ALL`; clear to drop it |
| Enable Build context, Terminal context, Text context | on | Per context switch; a disabled context neither records nor takes the keys |
| Max nodes: Build, Terminal | 200 | Node cap per persisted history |
| Max nodes: Text | 100 | Per text box |
| Snapshot budget MB: Build, Terminal | 64 | Byte cap for all snapshots in a history |
| Undo tree | off | Keep abandoned branches |
| Group link type for snapshots | Logical | `GridLinkTypeEnum` used to collect a grid group; Physical also follows connectors |
| Record terminal changes outside the terminal | off | Toolbar and script driven property changes |
| Restore removed blocks with full state | on | Use the paste path when creative rights allow it; off always rebuilds from the definition |
| Paint stroke timeout ms | 300 | Coalescing window for held mouse painting |
| Text coalescing window ms | 500 | Typing pauses shorter than this stay in one text snapshot |
| Pending operation timeout s | 5 | How long the executor waits for an asynchronous op before marking the node unknown |
| Paste match window s | 5 | How long `OnEntityAdd` candidates are matched to a pending paste on a client |
| Paste match position tolerance m | 0.5 | Position tolerance for that match |
| Persist in the world save | on | Section 8, offline and hosting |
| Persist on multiplayer client | on | Section 8, client side storage |
| Client storage root | `<UserDataPath>/Undo` | Root of the `Servers/` tree |
| Client autosave interval s | 60 | Minimum time between client side writes after a change |
| Client history retention days | 90 | Files older than this are deleted at plugin start |
| Notifications | on | HUD text on undo, redo and refusals |
| Notification duration ms | 2000 | HUD text lifetime |
| Debug status file | off | Writes `<Client storage root>/status.json` after every history change, for the tests |
| Log level | Info | Plugin log verbosity in the game log |

## 11. Code structure

```
ClientPlugin/
  Plugin.cs                 IPlugin: Harmony PatchAll, config, session component wiring
  Config.cs                 options above
  Session/UndoSession.cs    MySessionComponentBase: load, unload, save snapshot, per world state
  History/                  Node, History, Op base, GridRegistry, limits
  Ops/                      one file per op kind: BuildBlocks, RazeBlocks, RestoreBlocks,
                            PasteGrids, CloseGrids, MergeIntoGrid, Paint, SetProperty,
                            SetCustomName, SetGridName, SetProgram, GroupSnapshot
  Record/                   Harmony patches per hook, the Recorder (open node, coalescing,
                            re-entrancy flag), paint stroke and paste correlation helpers
  Apply/                    Executor, Pending tracking, permission predicates
  Input/                    key handlers for the three contexts, IsControl suppression
  Text/                     TextHistory and the textbox patch
  Storage/                  UndoDocument, XML serialization, save and load hooks,
                            client side world file
  Settings/                 template config dialog (unchanged)
```

Ops are data, not behavior, except for `Apply` and `Validate`. The recorder never
calls game mutation methods and the executor never records. Anything that talks to
the game is behind the Ops and the Record patches so the History and Storage code is
testable without the game.

## 12. Test suite

Pytest under `tests/` in this repo, driven through the Remote plugin's REST API and
its Python client (`se1/plugins/remote/skills/se-remote/se_remote.py`). The rig is an
isolated client the way `se-performance-improvements` does it: its own Pulsar folder
with Remote and Undo registered as dev folders, its own `-appdata`, its own Remote
port, headless, resolution 1280x720. The test world is a copy of the Remote suite's
Earth world with `TrashRemovalEnabled` off. `Undo.cfg` is written before launch with
the debug status file and "record outside terminal" turned on.

Coverage, one test per row, each followed by redo where it applies:

| Area | Drive | Verify |
|---|---|---|
| Build then undo | `POST /v1/character/build-block`, `POST /v1/input/key` Ctrl-Z | `CubeExists` call op on the position |
| Raze then undo | `POST /v1/character/grid-event` raze | `CubeExists`, and block detail (custom name, color) survives the restore |
| Paint then undo | grid-event color (fixed HSV differs from default) | `colorMask` in block detail |
| Paste then undo | `POST /v1/blueprints/paste` | grid gone from `grid_list`; redo brings it back with the same name and block count |
| Close grid then undo | `grid_close` set op | grid back by name and block count |
| Terminal property | open the terminal with the injected F key, `control/set` on a checkbox and a slider, also `property` set op with recording outside the terminal on | `property` get op |
| Block name, grid name | terminal Name text box via `control/set`; grid name via the info tab | block detail, `grid` get op |
| Text box | type into a search box via `input/type`, Ctrl-Z with the box focused | `properties.text` of the control |
| Limits | 210 builds, expect 200 nodes in the status file and the oldest gone | status file |
| Tree option | undo twice, do a new action, undo, redo along both branches | status file and world state |
| Persistence | `POST /v1/game/save`, check `Undo.xml.gz` in the save folder and in the newest `Backup/` folder, `game/reload`, undo still reverts the last build | filesystem and world state |
| Backup restore | copy the newest backup's files up a level on disk the way the game does, load, history matches | world state |
| Permissions | survival world: raze undo refused without creative tools, allowed after `settings/admin-flag` enables them | notification text in the log, world state |
| Dedicated server client | Magnetar DS with DirectTransport and one client (`notes/game-test-instance-modes`, mode A); paste and undo as admin, refusal as a regular player | world state through the client's API |

Gaps that need Remote plugin work first, tracked in a separate ticket: no endpoint to
read or write a PB program, no skin in block detail, painting only in one fixed color,
no host lobby endpoint (so the friends host mode is a manual test until then).

## 13. Follow up tickets

- Remote plugin endpoints for the Undo tests: PB program get and set, skin in block
  detail, paint with a given color and skin, host an offline lobby.
- Optional server companion (Magnetar plugin): report the entity ids of a paste
  request back to the requesting client, so DS clients don't rely on the
  `OnEntityAdd` matching heuristic; optionally a server validated "restore blocks
  with state" for admins. Everything else works client only, so this is not required
  for the first release.

## 14. Risks and open points

- R1, closed generic patching. `MyTerminalValueControl<TBlock, TValue>.SetValue` must
  be patched per closed type. Instantiations over reference types share JIT code, so
  patching one may patch several; the patcher dedupes by `MethodBase.MethodHandle`
  after `GetMethod` on each closed type and tolerates "already patched". Fallback if
  this proves unreliable: subscribe to `Sync<T>.ValueChangedFromTo` on the properties
  in `MySyncedBlock.SyncType` of the blocks shown in the terminal, and record at the
  sync level with the property index instead of the control id.
- R2, paste correlation on DS clients is a heuristic (section 6). Acceptable for the
  stated use case; the companion ticket removes it.
- R3, `RazeBlocks` reverse in survival without creative rights only rebuilds skeleton
  blocks. Documented in the notification text ("restored as construction sites").
- R4, memory: a group snapshot of a large ship is several MB uncompressed. Snapshots are
  kept gzip compressed in memory as well, and the byte budget bounds the total.
- R5, the game's paste strips blocks with missing DLC or skins and scripts for non
  scripters, so an undo of a delete can come back slightly different on a server. The
  plugin reports "restored with changes" when the block count differs.
- R6, `ChangeColorAndSkin` also fires for changes received from other players. The paint
  stroke attribution in section 5 filters those; a change that arrives during a local
  stroke on the same grid could be misattributed. Rare, and the reverse just repaints
  those blocks to their pre-stroke color.

## 15. Implementation order

1. History, Node, Op base, GridRegistry, UndoDocument serialization, unit tests without
   the game (plain `dotnet test` project against the History and Storage code).
2. Key handling in the three contexts, HUD notifications, config dialog.
3. Build context ops: build, raze (with split handling), paint. Test rig and the first
   pytest cases.
4. Paste, close, merge ops with paste correlation. Group snapshot fallback.
5. Terminal context: property, names, PB program.
6. Text context.
7. Persistence in the save folder, client side world file, backup tests.
8. Survival and DS client behavior, permission tests, tree option.

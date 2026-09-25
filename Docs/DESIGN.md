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
  process and requests are locally invoked, so the same code works in all modes. On a
  client the plugin replays through these request methods only. Where it is the server
  (offline, hosting) it prefers the server entry points that keep entity ids, listed
  below, because the game replicates their results and block associations survive.
- A file only survives save, backup and restore when it is written into the save's
  staging folder `<world>/.new` while the snapshot is written. The game then copies it
  to the world folder, into every `Backup/<timestamp>/` folder, and back on restore.
  Files written straight into the world folder are deleted by the save cleanup;
  subfolders survive but are neither backed up nor restored.
- Server side pastes renumber entity ids (`MyEntities.RemapObjectBuilderCollection`).
  `MyEntityIdRemapHelper.RemapEntityId` allocates a fresh id for every id it has not
  seen, so a reference that crosses the pasted set breaks in both directions: a toolbar
  slot, turret controller, event controller, sensor, timer, button panel, AI block or
  rotor top that points at a block outside the pasted grids gets a dangling id, and
  blocks outside keep pointing at ids that no longer exist. References inside the
  pasted set are remapped consistently (all such builders implement `Remap`).
- The server has entry points that keep entity ids and that the game replicates on its
  own: `MyCubeGrid.MergeGrid_MergeBlock(gridToMerge, offset, checkMergeOrder)` merges
  two live grids and sends `MergeGrid_MergeBlockClient` to clients; `BuildBlockRequestInternal`
  builds one block from a full `MyObjectBuilder_CubeBlock`, using `location.EntityId`,
  and broadcasts `BuildBlockSucess` with the builder; `MyEntities.CreateFromObjectBuilderAndAdd`
  creates a grid from a builder without remapping, and the replication layer sends it
  to clients (this is how grid-backups and hangar restore grids on servers). None of
  these are reachable from a client of a dedicated server.
- A grid split (`MyCubeGrid.CreateSplit`) moves the block entities to the new grid; the
  blocks keep their entity ids and the new grid starts with the same transform.
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
  storeRefs                ids of grid store entries the ops need, section 9
```

Undo: apply `current.reverse`, move `current` to its parent. Redo: pick a child (the
most recently visited one), apply its `forward`, move `current` there. A new player
action becomes a child of `current`. With the tree option off, the other children of
`current` are dropped when a new action is recorded, which gives the usual linear
undo. With it on, they stay, and redo walks the last visited branch. A branch picker
dialog is not part of the first version; the data model already supports it.

Limit per persisted context: a node count (default 200), in the config. When it is
exceeded the oldest nodes are dropped from the root side. A dropped node's descendants
on other branches are dropped with it. Nodes hold only small data; grid group builders
live in the grid store of section 9 and have their own byte budgets there.

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
| Remove blocks with the cube builder | Prefix on `MyCubeGrid.RazeBlocks(List<Vector3I>, long, ulong)` and `RazeBlocks(ref Vector3I, ref Vector3UByte, long)`; snapshot each `MySlimBlock.GetCopyObjectBuilder()` first, plus the association record of section 7 | `RazeBlocks` | `RestoreBlocks(grid, blockBuilders)` | Server: `BuildBlockRequestInternal(visuals, location, builder, ...)` per block with the saved builder and the original entity id, so toolbars and other references to the block work again; replicated by `BuildBlockSucess`. Client: `PasteBlocksToGrid` with a one grid builder, which keeps names, settings and integrity but gets a new id. Without creative rights: `BuildBlocks` from definition, orientation, color, skin |
| Removal that splits the grid | `MyCubeGrid.OnSplitGridCreated` / `OnGridSplit` fired while the raze action is open; record the piece grid handles and their transform relative to the main grid | as above | Server: `MergeBack(mainGrid, pieces)` then `RestoreBlocks`. Client: `CloseGrids(pieces)`, `MergeIntoGrid(mainGrid, pieceBuilders)`, then `RestoreBlocks` | `MergeBack` is the live merge `MergeGrid_MergeBlock(piece, offset, checkMergeOrder: false)`; the piece's block entities are the same objects the split moved, so ids, toolbars, block groups and controller references all survive. If a dynamic piece drifted, the server sets its world matrix back to the recorded one first. The client path captures the piece builders right after the split and re-pastes them; references crossing the piece boundary are lost there, section 7. If capture fails the node is refused |
| Paste grids from clipboard (free placement) | Prefix on `MyGridClipboard.PasteGridInternal` captures the clipboard builders; result grids come from a postfix on the nested `MyCubeGrid+PasteGridData.TryPasteGrid` (`___m_pastedGrids`) when the server is local, else from `OnEntityAdd` matching (section 6) | `PasteGrids(builders, position)` | `CloseGrids(handles)` | Server: the forward builders are re-read from the pasted grids after the paste so a redo re-creates them with `CreateFromObjectBuilderAndAdd` under the same ids. Client: the same paste request the clipboard uses, `MyMultiplayer.RaiseStaticEvent(TryPasteGrid_Implementation, MyPasteGridParameters)` |
| Paste blocks into an existing grid | Postfix on `MyCubeGrid.PasteBlocksToGridClient_Implementation(MyObjectBuilder_CubeGrid, MatrixI)` gives the merged builder and its transform | `MergeIntoGrid(grid, builder)` | `RazeBlocks(grid, transformed positions)` | Runs on every machine, so the client sees the exact positions |
| Delete grid or group (clipboard Delete, Cut) | Prefix on `MyGridClipboard.DeleteGrid` / `DeleteGroup`; snapshot `grid.GetObjectBuilder(true)` for each grid (same as `CopyGridInternal`: clear pilots and turret shooting) | `CloseGrids` | `PasteGrids(snapshots at original position and velocity)` | Server: re-created without remapping, ids and references to other grids survive. Client: paste request, new ids, handles remapped |
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
of a grid from a builder on a client, block entity ids don't there. The handle also
keeps the last known entity id so the server paths can restore under the same id. Terminal ops resolve the block at
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

## 7. Block associations and the snapshot fallback

Blocks point at other blocks by entity id: toolbar slots in cockpits, timers, sensors,
button panels, event controllers and AI blocks; turret controller azimuth, elevation,
camera and tool lists; event controller selected blocks; rotor and piston tops; block
groups (per grid, by block reference). The design keeps these intact wherever the
plugin is the server, and states the loss where it is a client.

What survives by itself when the entity id is preserved:

- Toolbar items keep `BlockEntityId` and resolve it again on the next update once the
  block exists (`MyToolbarItemTerminalBlock` looks the id up with `TryGetEntityById`).
- Rotor and piston tops and mechanical connections, same lookup by id.
- Everything inside a live merged piece, nothing is re-created there.

What the referencing side drops when a block closes, and the plugin has to put back:

| Association | Dropped how | Recorded at removal | Reapplied after restore |
|---|---|---|---|
| Block groups | `MyGridTerminalSystem` removes the block from its groups and deletes empty groups | Group names the block was in (`grid.BlockGroups`) | Server: `MyCubeGrid.AddGroup` / `AddUpdateGroup` with the block re-added; client: the group rename request path that syncs groups |
| Turret controller tools | `BlockRemovedTool` on `OnClose` and `RemovedFromScene` | Turret controllers in the logical group whose `ToolIds` (from `GetObjectBuilderCubeBlock`) contain the block | The controller's add tools request, the same one its terminal list uses |
| Event controller selected blocks | `RemoveBlocks` when the block leaves | Controllers whose `SelectedBlocks` contain the block | `AddBlocks` request of the controller |

The scan for referencing controllers runs over the fat blocks of the logical group at
removal time and only for turret and event controllers, so it is cheap. Other block
types that hold ids (AI blocks, sensors, timers) do so through toolbars and survive by
themselves.

Client of a dedicated server: none of the id preserving entry points are reachable, so
a restored block or piece gets new ids. Associations inside the re-pasted set are
remapped consistently by the game; those crossing its boundary are lost, and the
fix-up table above cannot be applied because the old ids are gone. The plugin reports
"restored, some block links lost" in that case. The companion plugin in the follow up
ticket removes this limitation by exposing the id preserving restore on the server.

Snapshot fallback. A `GroupSnapshot` op holds the builders of a logical grid group
(`GetConnectedGrids(GridLinkTypeEnum.Logical)`, link type configurable) with their
positions, plus the handles of the current grids. Apply on the server: close the
current grids and re-create the saved builders with `CreateFromObjectBuilderAndAdd`
without remapping, so ids and cross references come back. Apply on a client: the paste
request, with the losses above. It is used in one situation only: an op applied on a
client completed with an unknown result (section 10). Before the executor sends such an
asynchronous op it takes the group snapshot, keeps it while the op is pending, drops
it on success and attaches it to the node on timeout. Server side ops complete
synchronously and never need it. Snapshots are written to the grid store of section 9 like every other group builder
and only referenced from the node.

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
<storage root>/Servers/<server>/<player>/<world>/history.xml.gz

storage root   default <UserDataPath>/Undo, configurable
server         Sync.ServerId, plus the sanitized host name for readability when known
player         Sync.MyId (the Steam id) and the local identity id
               (MySession.Static.LocalPlayerId), joined with an underscore, so two
               accounts or a reset identity on the same server don't share a history
world          sanitized SessionName, plus WorldId when it isn't Guid.Empty
```

Offline and hosted worlds keep `Undo.xml.gz` in the save folder as above; their grid
store entries (section 9) live under `<storage root>/Worlds/<world key>/` where the
world key is the save folder name plus `WorldId`, so backups don't multiply the store.

It is written on `OnUnloading`, when the `OnServerSaving(true)` RPC arrives, and at
most once per the configured interval after a change (default one minute). This history
is not tied to the server's own backups; if the server restores an older world the
plugin refuses nodes whose grids are gone, as in section 6. Old files are removed when
they are older than the configured retention (default 90 days) so the folder does not
grow with every server ever visited. The config has a switch to turn client side
persistence off.

## 9. Grid store and recovery dialog

Every grid group builder the plugin has to capture anyway (delete, paste, split pieces
on a client, the pre-op snapshot of section 7) goes into one store instead of into the
history nodes. Nodes reference an entry by id. Keeping those entries longer than the
history needs them, under a retention policy, gives a grid recovery feature with no
extra backup work: the player opens a dialog, picks a backed up grid group, and gets it
on the clipboard to paste wherever they want.

Layout, under the storage root of section 8:

```
<storage root>/
  Worlds/<world key>/grids/            offline and hosted worlds
  Servers/<server>/<player>/<world>/grids/   client sessions
    index.xml                          one row per entry, everything the dialog shows
    <id>.sbc.gz                        the builders, blueprint format
```

An entry file is a `MyObjectBuilder_Definitions` with one `ShipBlueprints` item whose
`CubeGrids` hold the group, written with `MyObjectBuilderSerializerKeen.SerializeXML`
gzip compressed. That is the game's own blueprint file format, so an entry can also be
copied into the blueprints folder by hand, and the dialog can hand it to the clipboard
through the game's own `MyGuiBlueprintScreen_Reworked.CopyBlueprintPrefabToClipboard(prefab, MyClipboardComponent.Static.Clipboard)`,
which also sets the owner and the drag point. `id` is the SHA-256 of the uncompressed
XML, so an unchanged group deleted twice is stored once and indexed twice.

Index row: id, UTC timestamp, reason (deleted, pasted, split, snapshot), main grid name
(the largest grid), grid count, total block count, PCU (sum of definition PCU), grid
size class (large, small, mixed), static or dynamic, main grid entity id, compressed
bytes. Everything is computed from the builders at write time, so the dialog never
opens an entry file until the player picks one. The index is rewritten on every change
and loaded once per session.

Retention. Two byte budgets, per world folder and for the whole storage root, both in
the config. When a write pushes a world folder over its budget, or the total over its
budget, cleanup removes entries oldest first in two passes: the first pass skips an
entry when it is the newest entry of its grid group, the second pass runs only if the
first could not get under budget and removes oldest first without exception. Grid
group identity for this rule is the main grid entity id (plus name, so a renamed grid
keeps its line). A history node whose entry was removed by cleanup is refused on undo
with "backup was cleaned up", the node itself stays. The total budget cleanup considers
entries across all world folders, so an old world's entries give way to the current
one. The client history retention days of section 8 delete whole world folders as
before.

Oversized entries. When a single entry, once compressed, is larger than the per world
budget or the total budget, cleanup cannot make room for it, so the plugin asks the
player right away with a message box (`MyGuiSandbox.CreateMessageBox`, Yes and No):

> Backing up "<main grid name>" (<grid count> grids, <block count> blocks) needs
> <entry MB> MB, more than the <per world | total> grid store budget of <budget MB> MB.
> Raise the budget to <new MB> MB? "No" keeps the budget and drops the backup, so the
> change you just made cannot be undone.

Yes raises every exceeded budget to the smallest multiple of the budget raise step
(config, default 64 MB) that holds the entry, saves the config, and commits the entry
and its history node. No, or closing the box, discards the entry and records a barrier
node in place of the action's node. Ctrl-Z at a barrier says "Undo not available:
backup was too large for the budget" and goes no further; older nodes stay in the
tree but cannot be reached by undo because the world has changed past them. The
config option "Oversized grid backups" pre-answers the question with "Always raise"
or "Never store", default "Ask".

The player's action itself is not held back: the capture runs in a prefix before the
game's delete or paste, and the message box answer arrives a frame or more later. The
entry is written to a temporary file next to the store meanwhile, the history of that
context is locked for undo and redo until the answer comes (the same lock as pending
ops in section 10), and the answer then commits or deletes the temporary file.

Dialog. Opened by a configurable binding (default Ctrl-Shift-H, to be checked against
the vanilla default controls at implementation time) in the Build context, and by a
button in the plugin's config dialog. It is a `MyGuiScreenBase` with a
`MyGuiControlTable` listing the entries of the current world and player only, one row
per index entry. Columns in this order: Time (local), Name, Blocks, Grids, PCU, Size,
Reason. Bytes and the static flag are shown in the row tooltip rather than as columns
so the table fits at 1280x720.

Sorting keeps a history of clicked columns. The sort key list starts as
`[Time descending]`. Clicking a column moves it to the front of the list; clicking the
column that is already first flips its direction. Rows are ordered by comparing the
keys in list order, so after Time descending then Name, the table is by name with the
newest first inside each name. The plugin sorts the rows itself and re-adds them (the
table's own `SortByColumn` knows one column), using `ColumnClicked` for the clicks.
The key list is saved in the config so the dialog reopens the way it was left.

Double click (`ItemDoubleClicked`) or the Paste button loads the entry's builders onto
the clipboard with the method above and closes the dialog; the player then places it
with the normal paste flow, so the server's paste permissions apply unchanged. A
Delete button removes the selected entry (and its file when no other index row shares
the id), with a confirmation. Entries referenced by an undo node can be deleted too;
the node is then refused as described above.

## 10. Applying an action

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

## 11. Configuration

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
| Grid store budget per world MB | 256 | Section 9 retention, per world folder |
| Grid store budget total MB | 1024 | Section 9 retention, whole storage root |
| Budget raise step MB | 64 | Granularity when a budget is raised for an oversized entry |
| Oversized grid backups | Ask | Ask, Always raise, Never store; section 9 |
| Grid history binding | Ctrl-Shift-H | Opens the recovery dialog in the Build context |
| Grid history sort keys | Time descending | Saved column sort history of the dialog |
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

## 12. Code structure

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
  GridStore/                entry files, index, hashing, retention cleanup
  Gui/                      grid history screen, table, sort key list
  Settings/                 template config dialog (unchanged)
```

Ops are data, not behavior, except for `Apply` and `Validate`. The recorder never
calls game mutation methods and the executor never records. Anything that talks to
the game is behind the Ops and the Record patches so the History and Storage code is
testable without the game.

## 13. Test suite

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
| Raze a referenced block then undo | test world has a cockpit toolbar slot, a turret controller tool list, an event controller selection and a block group pointing at the block | after undo the block has the same entity id, the toolbar slot, tool list, selection and group still contain it |
| Raze that splits the grid then undo | remove the single connecting block of a two part test grid | one grid again with the original id, all block ids unchanged, cross part toolbar slots intact |
| Paint then undo | grid-event color (fixed HSV differs from default) | `colorMask` in block detail |
| Paste then undo | `POST /v1/blueprints/paste` | grid gone from `grid_list`; redo brings it back with the same name and block count |
| Close grid then undo | `grid_close` set op | grid back by name and block count |
| Terminal property | open the terminal with the injected F key, `control/set` on a checkbox and a slider, also `property` set op with recording outside the terminal on | `property` get op |
| Block name, grid name | terminal Name text box via `control/set`; grid name via the info tab | block detail, `grid` get op |
| Text box | type into a search box via `input/type`, Ctrl-Z with the box focused | `properties.text` of the control |
| Limits | 210 builds, expect 200 nodes in the status file and the oldest gone | status file |
| Tree option | undo twice, do a new action, undo, redo along both branches | status file and world state |
| Grid store retention | delete grids until the per world budget is exceeded, with two deletes of one grid among them | the older copy of that grid is gone, its newest copy stays, unrelated older entries are gone first; then push past the point where only newest copies remain and see the oldest of those go |
| Oversized entry | set the per world budget to 1 MB, delete a grid whose backup is larger, answer No through the message box, Ctrl-Z; then repeat with Yes | first: the grid stays deleted, the log has the barrier refusal, the config is unchanged; second: the config budget is raised to the next step, the entry is in the index, undo restores the grid |
| Grid history dialog | open with the binding, read the table through `/v1/ui/screens/{i}/controls`, click columns in the order Time then Name, double click a row | rows sorted by name then time descending; after the double click the clipboard is active (paste via `/v1/input/key` and a new grid with that name appears) |
| Persistence | `POST /v1/game/save`, check `Undo.xml.gz` in the save folder and in the newest `Backup/` folder, `game/reload`, undo still reverts the last build | filesystem and world state |
| Backup restore | copy the newest backup's files up a level on disk the way the game does, load, history matches | world state |
| Permissions | survival world: raze undo refused without creative tools, allowed after `settings/admin-flag` enables them | notification text in the log, world state |
| Dedicated server client | Magnetar DS with DirectTransport and one client (`notes/game-test-instance-modes`, mode A); paste and undo as admin, refusal as a regular player | world state through the client's API |

Gaps that need Remote plugin work first, tracked in a separate ticket: no endpoint to
read or write a PB program, no skin in block detail, painting only in one fixed color,
no host lobby endpoint (so the friends host mode is a manual test until then).

## 14. Follow up tickets

- Remote plugin endpoints for the Undo tests: PB program get and set, skin in block
  detail, paint with a given color and skin, host an offline lobby.
- Optional server companion (Magnetar plugin): expose the id preserving restore
  paths of section 7 to clients (single block from builder, live merge of a split
  piece, grid re-creation without remap) so block associations survive undo on
  dedicated servers, and report paste result ids so DS clients don't rely on the
  `OnEntityAdd` matching heuristic. Everything else works client only, so this is not
  required for the first release.

## 15. Risks and open points

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
- R7, `BuildBlockRequestInternal` is used by projectors only; that it honors the
  builder's terminal state and `location.EntityId` for a non projection build, and that
  a live merge of a drifted dynamic piece works after resetting its world matrix, must
  be confirmed in the first prototype. Fallback for both is the client path with its
  documented losses.
- R4, disk and memory: a group snapshot of a large ship is several MB uncompressed.
  Entries are written gzip compressed and read only when applied or pasted; the store
  budgets bound the total on disk.
- R5, the game's paste strips blocks with missing DLC or skins and scripts for non
  scripters, so an undo of a delete can come back slightly different on a server. The
  plugin reports "restored with changes" when the block count differs.
- R6, `ChangeColorAndSkin` also fires for changes received from other players. The paint
  stroke attribution in section 5 filters those; a change that arrives during a local
  stroke on the same grid could be misattributed. Rare, and the reverse just repaints
  those blocks to their pre-stroke color.

## 16. Implementation order

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
9. Grid store retention and the grid history dialog. The store itself exists from
   step 4 because the paste and delete ops write into it.

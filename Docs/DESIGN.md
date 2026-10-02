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
  blocks keep their entity ids and grid positions, and the new grid starts with the
  same transform.
- Splits after a removal are not immediate. `RemoveBlockInternal` schedules
  `DetectDisconnects` for the grid's after simulation update, which runs on the
  parallel update threads, so `OnGridSplit` fires a frame later and possibly off the
  main thread. While the check is due, the grid's `m_disconnectsDirty` is set.
- With the world setting "unsupported stations" (`StationVoxelSupport`) off, a split
  of a static grid tests both parts for voxel contact, and a station floating in the
  air turns into a ship, main part included. That is vanilla behavior the plugin
  does not change.
- A `[Server]` request raised on a local server runs synchronously inside the call. An
  event marked only `[Broadcast]` is not invoked on the server that raises it
  (`MyReplicationLayerBase.ShouldServerInvokeLocally`), so a hook on the client half of
  a request, like `PasteBlocksToGridClient_Implementation`, never runs where the server
  is local.
- A static grid pasted out of voxel contact becomes a ship a moment later
  (`CheckConvertToDynamic`), unsupported stations allowed or not, unless its builder
  has `IsUnsupportedStation` set.
- A client connected to a dedicated server has no save folder. `CurrentPath` points at
  an uncreated placeholder under the local Saves folder that can collide with a local
  world of the same name.

## 2. Session modes and permission policy

| Mode | Detection | Policy |
|---|---|---|
| Offline | `MyMultiplayer.Static == null` | Full functionality, subject to creative tools in survival |
| Hosting a lobby (friends) | `Sync.IsServer && MyMultiplayer.Static is MyMultiplayerLobby` | Same as offline. Replays go through the request methods or the server entry points of section 1, both of which the game replicates to the joined clients |
| Client of a lobby | `MyMultiplayer.Static is MyMultiplayerLobbyClient` | Same as DS client |
| Client of a dedicated server | `MyMultiplayer.Static is MyMultiplayerClientBase` and not a lobby client | Client only. History is stored locally, see section 8 |

`Permissions.Mode` (`SessionMode`) is this table; the status file and the log name the
mode at session start. The ops themselves only ask `Sync.IsServer`: every server entry
point of section 1 sits behind it, and a client sends requests only.

Within a mode, each operation kind has a predicate the plugin evaluates before applying
an undo or redo. It mirrors the server check so the plugin refuses cleanly instead of
sending a request that fails with a notification.

"Creative" below is `MySession.Static.CreativeMode || MySession.Static.CreativeToolsEnabled(Sync.MyId)`.
The first design used `HasPlayerCreativeRights` in several rows. That is the server's
check for requests from clients, but it is true for everyone when `MyMultiplayer.Static`
is null, and the server skips it for requests it invokes itself, so offline and for a
host it limits nothing. "Creative" is never looser than the server's checks
(`CreativeToolsEnabled` implies the creative rights), so a client that passes it is not
kicked.

| Operation kind | Predicate (client side mirror of the server check) |
|---|---|
| Build blocks | Always allowed. Without Creative the blocks come back as construction sites and cost components, which is what a player could do by hand; the plugin makes the cube builder's own check first (`MyCubeBuilder.BuildComponent.GetBlocksPlacementMaterials`, `HasBuildingMaterials` of the local character) and refuses with "missing components", because a local server builds its own requests without it. A seated character also pays from the inventories its seat reaches through the conveyors |
| Raze blocks | Creative |
| Restore blocks with full state (paste into grid, or the block builders on a server) | Creative; otherwise fall back to Build blocks |
| Merge split pieces back | Creative |
| Paste grids, close grids, group snapshot restore | `IsCopyPastingEnabledForUser(Sync.MyId)` for the paste; for the close Creative, then the ownership rule of `OnGridClosedRequest` (space master, no big owner, a big owner, or the faction leader of one). The server answers a rights failure with `ValidationFailed`, which can kick, so a client has to check first. In creative without creative tools copy and paste follows the world's `EnableCopyPaste`, which the Earth test world has off; the test rig turns it on |
| Paint and skin | Ownership rule of `ColorGridOrBlockRequestValidation` (no creative rights needed) |
| Terminal property, block name | `CanLocalPlayerChangeValue()` of the resolved block |
| Grid name | The `BigOwner` validation of `OnChangeDisplayNameRequest`: the grid has no big owner or the player is one. The server skips it for its own requests |
| PB program | `IsUserScripter(Sync.MyId)`, and `CanLocalPlayerChangeValue()` of the block |

Survival as a regular player therefore has: build, paint, terminal property, names, PB
program. It does not have raze, paste, close and snapshot restore, so undo cannot be
used to remove blocks for free or to duplicate ships. Enabling creative tools unlocks
the rest, which matches what the player could do through the game anyway. Nothing in
the plugin needs admin rights of its own.

A refusal reads "Undo not available: needs creative tools" (or "the grid no longer
exists", "missing components", "copy and paste is disabled", and so on). What an op
has to say about a step that went through is appended to the step's notification in
parentheses: "restored as construction sites", "restored, some block links lost",
"restored with changes".

## 3. Contexts and key handling

A context is where Ctrl-Z looks for the last operation. Each has its own history and
its own node limit.

| Context | Active when | History | Persisted |
|---|---|---|---|
| Build | `MyGuiScreenGamePlay` has focus and no other game screen is open (`MyGuiScreenGamePlay.ActiveGameplayScreen == null`) | Block build and raze, grid paste and close, paint and skin | Yes, per world |
| Terminal | `MyGuiScreenTerminal` is the focused screen and no text field has the cursor | Terminal property changes, block and grid names, PB program updates | Yes, per world |
| Text | A `MyGuiControlTextbox` has keyboard focus, in any screen | Text and caret snapshots of that control | No, dies with the control |
| PB editor | `MyGuiScreenEditor` | Vanilla undo, untouched | No |

Terminal changes made while the terminal is closed (toolbar toggles, hotkeys, scripts)
are not recorded by default. They would flood the history and are trivially reversible
by the same key. A config option records them anyway, which the test suite uses.

Key handling per context:

- Build: Harmony prefix on `MyGuiScreenGamePlay.HandleUnhandledInput`. When the undo or
  redo binding is newly pressed, run it and mark the key as consumed for this frame. A
  postfix on `MyControllerHelper.IsControl(context, controlId, ...)` rewrites the
  answer: while the plugin consumed a key, it returns false for every control bound
  to that key, which covers `DAMPING_RELATIVE` and `TOGGLE_REACTORS_ALL`, and returns
  true for the displaced control when its replacement binding from the config is newly
  pressed (`MyControlStateType.NEW_PRESSED` only). All controls on the key, not only
  the two: the priority check that keeps plain `DAMPING` (Z) quiet under Ctrl-Z
  compares modifiers exactly (`MyControl.IsModifierPressed`), so under Ctrl-Shift-Z
  plain Z fires too and would switch the dampeners straight back. The vanilla code that follows,
  sound, `SwitchDamping`, `SetDampeningEntity`, `SwitchReactors` and the input
  recording, runs unchanged. Disabling the vanilla controls instead is not an option:
  with the Ctrl variant disabled, `MyVRageInput.IsPriorityKeyPressed` lets Ctrl-Z fall
  through to plain `DAMPING`.
- Terminal: prefix on `MyGuiScreenTerminal.HandleUnhandledInput`, same pattern. It
  does nothing while the focused control is a `MyGuiControlTextbox` or a
  `MyGuiControlMultilineEditableText`. The terminal history is reachable only here,
  so a change recorded with the terminal closed is undone after opening it.
- Text: prefix on `MyGuiControlTextbox.HandleInput`. When the control has focus and the
  binding is newly pressed, restore the snapshot in that direction if there is one,
  and return the control (input consumed) either way.

While the cursor is in a text field, Ctrl-Z and Ctrl-Y belong to that field alone:
the plugin's text history in a single line box (search boxes included), the vanilla
undo in a multi line one. They never reach the terminal history from there, also
when the field has nothing to undo, because mixing character level undo with the
terminal history would be confusing. Clicking or tabbing out of the field hands the
keys back to the terminal. The value a text field sets is still part of the terminal
history as a whole: a rename typed into the Name box is one terminal node. The
terminal's control panel page opens with the cursor in the block search box, so the
keys act on the terminal only after the focus moved to another control.
The config option "Separate text undo in the terminal" switches this off for the
terminal's single line boxes: the keys then always act on the terminal history.

Default bindings are Ctrl-Z and Ctrl-Y in every context. This takes relative dampeners
and "toggle all reactors" away from their default keys while the plugin is enabled, so
the config provides replacement bindings for both: Ctrl-Shift-Z for relative dampeners
and Ctrl-Shift-Y for toggle all reactors. All four bindings are `Binding` values in the
config dialog and can be changed or cleared. When a replacement is cleared the vanilla
control is simply unreachable through the keyboard while undo holds its key, which the
dialog says next to the option. The plugin's bindings are only checked when the
respective context is active, so Ctrl-Shift-Z in a text box still means nothing.

In the build context the undo key is the plugin's only while there is something to
undo (added on 2026-10-02). With an empty build history and no recording or replay
in progress the prefix does not take the key, so the game handles it: Ctrl-Z is
relative dampeners, as in the vanilla game, for a player who is flying and not
editing. No "Nothing to undo" is shown there. The redo key has no such fallback,
its vanilla action would switch all reactors. The terminal keeps its "Nothing to undo".

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

A history is locked for undo and redo while an asynchronous replay is pending
(section 10), and the build history also while a removal waits for its grid splits
to settle before it becomes a node (section 5). Ctrl-Z then says the last operation
is still in progress. On a local server that wait is two frames.

## 5. Recorded operations

Each Op is a small serializable record: a kind and its arguments. Ops refer to grids by
a plugin assigned grid handle and to blocks by `(gridHandle, Vector3I position)`, see
section 6. Every op has a validator (the predicate from section 2) and an apply method.

| Player action | Record hook | Forward op | Reverse op | Notes |
|---|---|---|---|---|
| Place blocks (single, line, plane) | Prefix and postfix on `MyCubeGrid.BuildBlocks(Vector3 color, MyStringHash skin, HashSet<MyBlockLocation>, long, long)` and on the `ref MyBlockBuildArea` overload. The prefix notes which requested cells are empty; where the server is local the request has run by the postfix, so only blocks that exist then count. A client records what it asked for | `BuildBlocks(grid, placements, color, skin)` | `RazeBlocks(grid, positions)` | A placement is definition, min and orientation; max and center are computed again with `MySlimBlock.ComputeMax` and `ComputePositionInGrid`. An area build is recorded as the list of blocks it produced. Redo reuses the recorded entity id while it is free, so a toolbar slot set on the block in between still works. Positions stay the stable reference |
| Place a block into empty space (new grid) | Local server: postfix on the protected static `MyCubeBuilder.AfterGridBuild(builder, grid, ...)`, the callback of the spawn `RequestGridSpawn` starts, when `builder` is the local character; it hands over the new grid itself. Client: postfix on the private `AddBlocksToBuildQueueOrSpawn` overload with visuals when no grid is targeted (or a small grid goes onto a large static one); the grid is matched in `OnEntityAdd` by one block and the placement position, any name, since the server names it | `PasteGrids(store entry)` | `CloseGrids(grid)` | One block, cheap. Store reason "placed". A large block placed in the air is a ship and falls; the test undoes it within two seconds |
| Remove blocks with the cube builder | Prefix on `MyCubeGrid.RazeBlocks(List<Vector3I>, long, ulong)`, `RazeBlocks(ref Vector3I, ref Vector3UByte, long)`, `RazeBlocksDelayed` (the cube builder's area removal) and `OnClosedMessageBox` (the same removal after the "remove the pilot too" question). `RazeBlock`, which grinders use for a fully ground block, is excluded. Snapshot each block with `MySlimBlock.GetObjectBuilder()` (not the copy variant, the block comes back as itself) into one `MyObjectBuilder_CubeGrid`, plus the association record of section 7. The node is committed once the splits settled: on a local server when `m_disconnectsDirty` is clear again and two frames passed, on a client after the pending timeout. Only blocks that are gone by then count. A removal that closes the grid (its last block) is not recorded; bringing a grid back is the grid paste op's job | `RazeBlocks` | `RestoreBlocks(grid, blocksBuilder)` | Server: `BuildBlockRequestInternal(visuals, location, builder, ...)` per block with the saved builder and the original entity id, so toolbars and other references to the block work again; replicated by `BuildBlockSucess`. Blocks that only connect through other removed blocks are retried until no more progress is made. The build request always shares ownership with the faction, so a different saved share mode is put back with `ChangeOwnerRequest`. Client: `PasteBlocksToGrid` with the one grid builder moved to the grid's current transform, which keeps names, settings and integrity but gets new ids. Without creative mode or creative tools, or with the full state option off: `BuildBlocks` from definition, orientation, color, skin |
| Removal that splits the grid | `OnGridSplit` of the grid, subscribed while the removal waits to settle; the handler only collects the pieces, since it can run on a parallel update thread. Each piece gets a handle and a key, the position of one of its blocks, which is the same in the piece and in the main grid | `RazeBlocks` again, which on redo rebinds each piece handle to the new piece containing its key | Server: `MergeBack(mainGrid, pieces)` then `RestoreBlocks`. Client: close each piece with `OnGridClosedRequest` and paste its builder into the main grid with `PasteBlocksToGrid`, then `RestoreBlocks` | `MergeBack` is the live merge `MergeGrid_MergeBlock(piece, Vector3I.Zero, checkMergeOrder: false)`; the piece's block entities are the same objects the split moved, so ids, toolbars, block groups and controller references all survive. A split piece keeps the grid positions and starts with the main grid's transform, so the merge offset is zero; the server stops a dynamic piece and sets its world matrix to the main grid's first, because the merge transform takes the orientation from the world matrices. The client path captures the piece builders when the removal settles and re-pastes them; references crossing the piece boundary are lost there, section 7. If capture fails the node is not recorded |
| Paste grids (free placement) | Local server: prefix on `MyCubeGrid.TryPasteGrid_Implementation` when locally invoked; it chains a callback into `MyPasteGridParameters.OnPasteFinished`, which gets the pasted grids (section 6). That covers the clipboard and every other local paste request, Remote's blueprint paste included. Client: prefix and postfix on the private `MyGridClipboard.PasteInternal`, expected grids from `m_copiedGrids` and `m_previewGrids`, matched in `OnEntityAdd` | `PasteGrids(store entry)` | `CloseGrids(handles)` | Server: the forward builders are read back from the pasted grids so a redo re-creates them with `CreateFromObjectBuilderAndAdd` under the same ids. Client: the paste request the clipboard sends, `RaiseStaticEvent(TryPasteGrid_Implementation, MyPasteGridParameters)`. A static paste that merges into a touching static grid right away is not recorded |
| Paste blocks into an existing grid | Local server: prefix and postfix on `MyCubeGrid.PasteBlocksToGrid`; the server request runs inside it, so the new blocks are the grid's blocks after minus before. Client: the `PasteBlocksToGrid` prefix notes the grid, and a prefix and postfix on `PasteBlocksToGridClient_Implementation` take the same difference when the broadcast arrives | `RestoreBlocks(grid, merged blocks)` | `RazeBlocks(grid, positions)` | The broadcast never runs on a local server: `ShouldServerInvokeLocally` does not invoke a broadcast-only event there. The forward op is the removed block restore with the merged blocks read back from the grid, so a server redo keeps their ids; on a client it is the `PasteBlocksToGrid` request. The game merges only the first clipboard grid and adds the others as grids of their own, which are not recorded |
| Delete grid or group (clipboard Delete, Cut, Remote's `grid_close`) | Prefix on `MyCubeGrid.SendGridCloseRequest`, the player's close request, which the clipboard's `DeleteGrid` sends and Remote's `grid_close` sends on both sides since CometWorks/remote#28; a prefix on `MyGridClipboard.DeleteGroup` captures the whole group as one node and keeps the per grid requests inside it from becoming nodes of their own. Snapshot `grid.GetObjectBuilder(true)` for each grid (same as `CopyGridInternal`: clear pilots and turret shooting; the copy variant keeps the entity ids). Only grids that really closed count: at once on a local server, when they are gone or the pending timeout passed on a client | `CloseGrids` | `PasteGrids(store entry at original position and velocity)` | Server: re-created without remapping, ids and references to other grids survive. Client: paste request, new ids, handles rebound |
| Paint or skin blocks, area or whole grid | Prefix and postfix on `MyCubeGrid.ChangeColorAndSkin(MySlimBlock, Vector3?, MyStringHash?)` record old and new per block; only when the change was initiated locally: a prefix on `SkinBlocks`, `SkinGrid`, `ColorBlocks` or `ColorGrid` opens a "paint stroke" for that grid, and `ChangeColorAndSkin` calls on it while the stroke is open are attributed to it | `Paint(grid, [(pos, hsv, skin)])` | `Paint(grid, [(pos, oldHsv, oldSkin)])` | Holding the mouse button calls `SkinBlocks` every frame. Calls are coalesced into one node until 300 ms pass without a call or a change; undo and redo commit an open stroke first. Color and skin are only applied when the stroke changed them, so a skin the player does not own is never sent. Apply groups blocks of equal (hsv, skin) into runs of adjacent block min positions along X and calls `SkinBlocks(min, max, hsv, skin, false)` per run; a run only covers cells that are min positions of its own blocks |
| Terminal property change | Prefix, postfix and finalizer on `MyTerminalValueControl<TBlock, TValue>.SetValue(TBlock, TValue)` and its overrides, found through `MyTerminalControlFactory.GetControls(Type)` for the block type of every cube block definition whose controls exist (`AreControlsCreated`, risk R1). The prefix reads `GetValue(block)` as the old value, the postfix reads it again as the new one, since setters clamp. A per thread depth counter makes only the outermost call count: the checkbox and combo box overrides call the base method. While the terminal is open only blocks in the control's `TargetBlocks` are recorded, so a script that sets some other block's property meanwhile is left out; with "record outside the terminal" on every call counts | `SetProperty(block, controlId, value)` | `SetProperty(block, controlId, oldValue)` | Values are stored as text: bool, float, long, Color (packed), StringBuilder, enums, MyStringId. A control with another value type is logged once and neither patched nor recorded. Apply resolves the control by id via `MyTerminalControlFactory.GetControls` and calls `SetValue` through reflection, which triggers the normal sync. Changes arrive in bursts (a slider drag every frame, a multi selection once per block), so calls for the same control are collected until the text coalescing window passes without one, then become one node with one op per block. A block whose value ends where it started is dropped. Undo and redo commit an open burst first |
| Block custom name | The terminal's Name box is a property control ("Name") and goes through `SetValue` on every text change. Prefixes on both `MyTerminalBlock.SetCustomName` overloads catch the other callers (mod API, Remote); they skip while a `SetValue` call is on the stack, which is the deduplication | `SetProperty(block, "Name", name)` | `SetProperty(block, "Name", oldName)` | No op type of its own. The Name box renames on every keystroke, not when the field loses focus; the burst rule above makes that one node per rename |
| Grid name | Prefix on `MyCubeGrid.ChangeDisplayNameRequest(string)`; old value is `DisplayName` | `SetGridName` | `SetGridName(old)` | The Info tab sends it from the OK button and on Enter. Apply sends the same request; on a client it is pending until the broadcast changed `DisplayName` |
| PB program | Prefix on the private `MyProgrammableBlock.SaveCode()`, where the editor's OK button and its "save changes?" question both end; old value is `m_programData`, new value the editor text. Also a prefix on the mod API setter `IMyProgrammableBlock.ProgramData` | `SetProgram(pb, source)` | `SetProgram(pb, oldSource)` | The design first hooked `SendUpdateProgramRequest` (client) and `UpdateProgram(string)` (server). Neither works: `SaveCode` stores the new source in the block before it sends the request, so the old one is gone by then, and where the server is local `SaveCode` calls `Recompile` directly and never reaches `UpdateProgram`, which only runs for another player's request. Apply sets `IMyProgrammableBlock.ProgramData`, which recompiles or sends the request. The setter recompiles on a server without telling its clients, so a lobby host raises the editor's request (`SendUpdateProgramRequest`) instead, which runs locally and is broadcast; "no program at all" cannot be sent that way and stays on the setter. Runtime state and Storage are lost, accepted. Sources are stored gzip compressed; a block that never had a program stores none |
| Single line text box edits | Postfix on the private `MyGuiControlTextbox.OnTextChangedInternal`, which every text change goes through and which raises `TextChanged`. With focus: a snapshot of (text, caret), replacing the previous one while typing continues within 500 ms. Without focus the screen set the text, which resets the history. The `HandleInput` prefix takes the starting snapshot of a focused box, the postfix refreshes the caret, because paste and the arrow keys move it after the change | n/a | n/a | Transient per control, keyed by a `ConditionalWeakTable<MyGuiControlTextbox, TextHistory>`. Default 100 snapshots per control. Restoring a snapshot calls `SetText`, so listeners follow: an undo in the terminal's Name box renames the block, which the terminal history records as part of that rename. `MyGuiControlMultilineEditableText` is another class and is not touched |

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

- Server local (offline, hosting): a prefix on `TryPasteGrid_Implementation`, when
  locally invoked, chains a callback into the request's `OnPasteFinished`. The game
  calls it from `PasteGridData.Callback` on the main thread, after `AfterPaste` added
  the grids, with exactly the grids of that request, or with a failure. The design
  first read `___m_pastedGrids` in a postfix on the nested `PasteGridData.TryPasteGrid`,
  but that runs on a parallel worker before the physics placement test, and
  `Callback` may still close the grids; it would also need the pastes matched by
  order. The callback needs neither. While it is outstanding the build history is
  busy, for at most the pending timeout.
- Client: the server never reports the new ids. `MyEntities.OnEntityAdd` is watched
  for the paste match window after the request; a candidate matches when its
  `DisplayName` and position (within the tolerance of the requested
  `PositionAndOrientation`) match a pending paste and it has no more blocks than
  asked for; fewer are accepted, since the server strips blocks (R5). The same matcher (`PasteMatch`)
  serves the recording of the player's paste and the replay of a `PasteGrids` op. If
  some of the expected grids arrive and others do not, the node is recorded in a
  "reference lost" state: undoing it is refused with a notification, and the node is
  kept. If none arrive the paste most likely failed on the server and nothing is
  recorded.

The registry is saved with the history as `(handle, entityId)` pairs. On load, where
the plugin is the server, entity ids that no longer exist are marked lost; a node whose
ops reference a lost handle is refused, not dropped, because a later node may re-create
the grid and reattach the handle. A client marks nothing at load: its grids stream in
after the session started and come and go with the sync distance, so a handle is
resolved against the entities that are there when an op needs it.

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
| Block groups | `MyGridTerminalSystem` removes the block from its groups and deletes empty groups | Names of the terminal system's groups the block was in | A group with the current members plus the block, through `MyGridTerminalSystem.AddUpdateGroup(group, fireEvent: true, modify: true)`, the call the terminal's group button makes. The event reaches `MyCubeGrid.ModifyGroup`, which raises the synced `OnModifyGroupSuccess`, so a client would take the same path (not tried yet) |
| Turret controller tools | Not dropped: `BlockRemovedTool` sets the entry of the id to null and keeps the key, and `TerminalSystemOnBlockAdded` binds the tool again when a block with that id appears in the logical group | Turret controllers of the terminal system whose `m_boundTools` has the block's id | Nothing in the normal case. If the id is missing the controller gets `AddTool`, its synced request; if the entry is still null, `RecacheTools` |
| Event controller selected blocks | `OnBlockClosing` removes the block from `m_selectedBlocks` | Event controllers of the terminal system whose `m_selectedBlocks` has the block's id | The controller's `AddBlocks` event, the one its Select button raises |

Verified in game on 2026-09-27, offline: after removing a light that a cockpit toolbar
slot, a turret controller tool list, an event controller selection and a block group
point at, the saved world had lost the group membership and the selection; after the
undo all four pointed at the light again, which was back under its old entity id.

The scan for referencing controllers covers the terminal blocks of the logical group
at removal time and only turret and event controllers, so it is cheap. Other block
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
positions, plus the handles of the current grids. Apply: send the close request for
the current grids, wait until they are gone, then create the saved builders again;
on the server with `CreateFromObjectBuilderAndAdd` without remapping, so ids and cross
references come back, on a client through the paste request, with the losses above.
The wait matters on the server too: a closed grid releases its entity ids only when
the close completes at the end of the frame, and creating an entity under an id still
taken crashes the game. For the same reason the server path of `PasteGrids` remaps
the builders when any of their ids is taken, and says so in the log.

It is used in one situation only: an op applied on a client completed with an unknown
result (section 10). Before the executor sends ops that change existing grids from a
client, it captures the groups of those grids in memory, drops the capture on success
and, on timeout or a reported failure, writes it to the grid store and attaches the
op to the node. The next step across that node, in either direction, applies the
snapshot instead of the node's ops: the snapshot is the state before the step that
went wrong, and the current node already moved past it, so restoring it is what the
step in the other direction leads to. Server side ops complete synchronously and
never take one. An exception while applying ops leaves the current node where it was,
so no snapshot is attached then; the node is refused as before.

### Removals that take a mechanical connection apart

Added on 2026-10-01 (SE1-0081). Removing the base of a rotor, hinge, piston or wheel
suspension, or the part on its other end, lets go of whatever it held. That subgrid
falls, drifts or collides within a second, and a restored base does not take it back:
it was found lying in the base's cells, which also kept the base from being placed.
Connecting the two again by hand (moving the subgrid, attaching the top) has too many
ways to fail, so the plugin does not try.

`RazeCapture` checks the blocks of a removal before the request. If one of them is a
`MyMechanicalConnectionBlockBase` or a `MyAttachableTopBlockBase`, it captures the
builders of the grid's whole group first (`StoredGroups.GroupOf`, the link type of
`GroupLinkTypeForSnapshots`, Logical by default, so ships docked on connectors are in
it). The node's reverse is then one `GroupSnapshotOp` instead of `RestoreBlocksOp` and
`MergeBackOp`: it closes the group's current grids and the pieces the removal split
off, and creates the group again from the backup, on a local server under the old
ids. The backup goes through the grid store like any other, with reason Snapshot, the
budget question included. Such a removal is recorded even when it closed its grid.

A ship may have moved between the removal and the undo. The op has the index of the
grid the removal happened on (`Anchor`) and moves the stored group to where that grid
is now, with its velocity, before creating it.

The other direction: placing one of these bases makes the game create the top part
as a grid of its own. The undo of a placement (`RazeBlocksOp.WithTopParts`) closes a
top grid that still consists of that one block before it removes the base.

Costs: every removal of such a block stores the group, which for a large ship is a
few hundred KB compressed, and the undo re-creates the whole group. A player seated
in the group is thrown out of the seat by that, like on the undo of a delete.

### What a restored block keeps, and what is restored as a whole group

Reworked on 2026-10-02 (SE1-0078). The test for all of it is the saved world: a grid
whose blocks point at each other in every way the game has is saved, a block removed,
the removal undone, the world saved again, and the grid's part of the sector file
compared (`tests/test_block_links.py`).

A single block is restored on a local server from its own builder under its old
entity id. That brings back its settings, its toolbar, its lists and its inventory,
and everything that refers to it by id finds it again: toolbar slots of other blocks
(checked by triggering a timer), a remote control's bound camera, a turret
controller's camera. What other blocks drop when it closes is captured and put back
by `BlockLinks`: block groups, turret controller tools, event controller selections.
The build request shares every block with the faction; the saved share mode is set
again afterwards, also on a block nobody owns.

Inventory. The game drops what a removed block holds into the world, as a container
bag or, in a world without temporary containers, as loose items. They lie where the
block was and keep the game from placing it again, which is why a container with
items did not come back. `SpillWatch` collects what appears at the removed blocks
during the ten frames after the removal; the restore takes those entities out of the
world first, physics body included, since closing an entity only takes effect at the
end of the frame. The restored block has the items in its inventory again, so nothing
is doubled. A replayed removal (redo, or the undo of a placement) empties the
inventories before it removes the blocks, so it drops nothing.

Refusal instead of a silent miss. The game's build request returns without a word
when something is in the way. `RestoreBlocksOp` and `BuildBlocksOp` ask
`MyCubeGrid.CanPlaceBlock` for every block before they change anything and refuse
the step with "something is in the way" (`OpRefusedException`, handled by the
executor like a failed validation). Blocks the game still does not place are
reported with the step: "(1 block could not be placed)".

Redo of a placement. The undo of a placement removes blocks the player may have
changed since: renamed, configured, filled. Before the step is applied every op gets
`Prepare` with the ops of the other direction, and `RazeBlocksOp` saves the blocks as
they are into the placement's `BuildBlocksOp` (`Restore`, or `Snapshot` with the whole
group for the blocks named below). Redo then puts those blocks back instead of new
ones from the definition. Without creative rights or with the full state option off
redo builds from the definition as before.

Blocks that hold another grid are not restored one by one. `RazeCapture.NeedsGroupBackup`
is true for the base of a rotor, hinge, piston or suspension, the part on its other
end, and a connector that has a ship on it; their removal goes the group way of the
previous section.

The Sections plugin keeps the same kinds of references across a cut and paste
(`se-sections/ClientPlugin/Logic/Reference.cs`). It has to find the blocks again by
a GUID in the mod storage because a paste gives them new ids; here the ids stay, so
nothing is stored on the blocks.

## 8. Persistence

File: `Undo.xml.gz` in the world folder, one top level file. The root element is the
plugin's own `UndoDocument` (version, both persisted histories, the grid registry),
serialized with `System.Xml.Serialization.XmlSerializer` and gzip compressed. Block
snapshots (removed blocks, blocks pasted into a grid) are stored as XML text produced by
`MyObjectBuilderSerializerKeen.SerializeXML(Stream, ob)`, nested as element text; grid
groups go to the grid store of section 9 and the document keeps only their ids. The
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
  carry it. This runs on the save worker thread, hence the snapshot step above. The
  bytes stay until the next save or the unload; the write does not consume them, so
  a save queued behind another one gets the file too.
- Load: `MySessionComponentBase.BeforeStart` of the plugin's session component
  (`[MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]`, registered automatically
  from plugin assemblies by `MySession.RegisterComponentsFromAssembly`). Entities
  exist and `CurrentPath` is set. Missing file or version mismatch means an empty
  history, never an error dialog.
- Unload: `MySession.OnUnloading` writes the client side file described below and
  clears the in-memory histories. Text histories are keyed weakly by their text box
  and go with it.

Backups and restores need no extra code: `MySessionSnapshot.Backup` copies all top
level files of the world folder, and `MyGuiScreenLoadSandbox.CopyBackupUpALevel`
deletes the top level files and copies the backup's files back, including ours. Save
As from within the game writes a fresh snapshot through the same staging folder, so
the file follows. Save As from the Load menu copies top level files, so it follows too.

Confirmed in game on 2026-10-01, offline: after a save the file is in the world
folder and, byte for byte, in the newest `Backup/<timestamp>/` folder; a reload has
the same nodes and cursor and undo works on them; a backup picked on the load menu's
Backups screen comes back with its own history; Save As from the pause menu and
Save As from the load menu both produce a world folder with the file. Backup folders
are named by the second, so two saves within one second share a folder.

A world copied with Save As has the history but not the grid store, which is keyed by
the save folder name (below): in the copy, nodes that need a stored grid are refused
until the player continues in the original. See R8.

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

A client learns its identity id from the server after the session started, so the
file is loaded in the first frame in which `LocalPlayerId` is set, and nothing is
written before that.

It is written on `OnUnloading`, when the `OnServerSaving(true)` RPC arrives, and at
most once per the configured interval after a change (default one minute). This history
is not tied to the server's own backups; if the server restores an older world the
plugin refuses nodes whose grids are gone, as in section 6. At plugin start a world
folder under `Servers/` is deleted whole, history and grid store, when nothing in it
was written for the configured retention (default 90 days), so the folder does not
grow with every server ever visited; server and player folders left empty go with
it. The config has a switch to turn client side persistence off, which stops both
the loading and the writing.

## 9. Grid store and recovery dialog

Every grid group builder the plugin has to capture anyway (delete, paste, split pieces
on a client, the pre-op snapshot of section 7) goes into one store instead of into the
history nodes. Nodes reference an entry by id. Keeping those entries longer than the
history needs them, under a retention policy, gives a grid recovery feature with no
extra backup work: the player opens a dialog, picks a backed up grid group, and gets it
on the clipboard to paste wherever they want.

Implemented in `GridStore/`: `GridStoreFolder` is the file and index side and has no
game references, so the unit tests cover it; `StoredGroups` captures groups, builds
the blueprint document and computes the row. Split pieces captured on a client are
entries too (reason "split"), referenced from the raze and merge back ops.

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
XML, so an unchanged group deleted twice is stored once and indexed twice. The XML is
Keen's serializer output without the byte order mark. An entry is written to a `.tmp`
file and renamed, the index likewise; an index that does not parse is copied to
`index.xml.bad` and the store starts a new one rather than overwriting it.

Index row: id, UTC timestamp, reason (deleted, pasted, placed, split, snapshot), main grid name
(the grid with the most blocks), grid count, total block count, PCU (sum of definition PCU), grid
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

As built (`StoreRetention`): cleanup runs after every entry that was committed. An
entry indexed twice counts once and its file stays while a row needs it. For the
total budget the grid of a row is its world folder plus entity id plus name, since
copies of a world share entity ids. The total is first summed from the entry files on
disk; the other worlds' indexes are opened only when something has to go.

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

As built (`StoredGroups.Save`): the lock is the recorder's busy state, which the
executor checks for both contexts. A box closed in any other way than through its
buttons counts as No. An entry whose file is in the store already takes no new space
and does not ask. Two captures cannot wait for an answer, the group snapshot of
section 7 and a split piece captured on a client: they follow the config option when
it is "Always raise" and are refused otherwise.

Dialog. Opened by a configurable binding (default Ctrl-H) in the Build context, and by
a button in the plugin's config dialog. No vanilla game control uses Ctrl-H, but
`MyDX9Gui.HandleInput` toggles the render profiler (on its "Statistics" graph) on H
with any Ctrl held, Shift or not, through `MyGeneralStats.ToggleProfiler`, its only
caller. A prefix there skips the toggle while the Build context is active and the
grid history binding matches exactly, so Ctrl-Shift-H keeps opening the profiler and
Ctrl-H outside gameplay still does too. It is a `MyGuiScreenBase` with a
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
The key list is saved in the config so the dialog reopens the way it was left. A
column clicked for the first time sorts ascending; one that comes back to the front
keeps the direction it had. The config file is written when the dialog closes.

Double click (`ItemDoubleClicked`) or the Paste button loads the entry's builders onto
the clipboard with the method above, closes the dialog and calls
`MyClipboardComponent.Paste()` the way the blueprint screen does, which switches the
paste preview on; the player then places it
with the normal paste flow, so the server's paste permissions apply unchanged. A
Delete button removes the selected entry (and its file when no other index row shares
the id), with a confirmation. Entries referenced by an undo node can be deleted too;
the node is then refused as described above.

The button in the config dialog comes last there, since the generated dialog lists
methods after properties, and tells the player to load a world when there is none.
The binding stayed Ctrl-H: Ctrl-Shift-H was considered and clashes with the render
profiler in the same way, since vanilla toggles it on H with any Ctrl held, and the
prefix described above already frees Ctrl-H in the Build context. Plain H is the
vanilla "toggle signals" control (`TOGGLE_SIGNALS`), the only default binding on that
key; the key handler marks H as consumed for the frame, the same way as for undo, so
opening the dialog does not also switch the signal mode.

## 10. Applying an action

`Executor.Undo()` / `Redo()` run on the main thread from the key handlers:

1. Pick the node. If there is none, notify "Nothing to undo" and stop.
2. Validate every op of the node with its predicate and reference resolution. If any
   fails, notify why ("Undo not available: needs creative tools", "... the grid no longer
   exists") and stop. Nothing
   is applied partially by the plugin's own choice.
3. Set the re-entrancy flag, apply the ops in order, clear the flag.
4. Ops that complete asynchronously (paste, close, grid name, paint on a client) are
   tracked by the `Pending` list: the op registers what it expects (a grid added, a
   grid removed, a broadcast arrived) and the executor keeps the history locked for
   further undo or redo until they complete or a timeout of 5 seconds passes. A close
   is checked on a local server too, since the game refuses an economy station there
   without an error. On timeout the node is marked "unknown result": the next step
   across it goes through the group snapshot path if a snapshot exists (section 7),
   otherwise it is refused.
5. Move `current`, show a HUD notification with the node label.

Failures reported by the server (`BuildBlocksFailedNotify`, `OnColorGridBlockFailed`,
`ShowPasteFailedOperation`, `StationClosingDenied`) are already shown by the game. A
postfix on each ends a pending op at once with an unknown result, which also attaches
the group snapshot. `ValidationFailed` has no client side to hook; the server may kick
the sender, which is why the client predicates of section 2 check first.

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
| Separate text undo in the terminal | on | On: a single line text box of the terminal has its own undo while the cursor is in it, section 3. Off: Ctrl-Z and Ctrl-Y act on the terminal history there too, and those boxes get no text undo. Multi line fields keep the vanilla undo either way |
| Max nodes: Build, Terminal | 200 | Node cap per persisted history |
| Max nodes: Text | 100 | Per text box |
| Grid store budget per world MB | 512 | Section 9 retention, per world folder |
| Grid store budget total MB | 2048 | Section 9 retention, whole storage root |
| Budget raise step MB | 64 | Granularity when a budget is raised for an oversized entry |
| Oversized grid backups | Ask | Ask, Always raise, Never store; section 9 |
| Grid history binding | Ctrl-H | Opens the recovery dialog in the Build context; takes Ctrl-H from the vanilla render profiler there, which stays on Ctrl-Shift-H |
| Grid history sort keys | Time descending | Saved column sort history of the dialog, stored as comma separated column names with a leading `-` for descending (`-Time`); no control in the config dialog |
| Undo tree | off | Keep abandoned branches |
| Group link type for snapshots | Logical | `GridLinkTypeEnum` used to collect the group snapshot of section 7; Physical also follows connectors. A clipboard delete takes the group the game closes |
| Record terminal changes outside the terminal | off | Toolbar and script driven property changes |
| Restore removed blocks with full state | on | Restore with names, settings and ids in creative or with creative tools; off always rebuilds from the definition |
| Paint stroke timeout ms | 300 | Coalescing window for held mouse painting |
| Text coalescing window ms | 500 | Typing pauses shorter than this stay in one text snapshot. Also the window that collects terminal changes of one control into one node |
| Pending operation timeout s | 5 | How long the executor waits for an asynchronous op before marking the node unknown |
| Paste match window s | 5 | How long `OnEntityAdd` candidates are matched to a pending paste on a client |
| Paste match position tolerance m | 0.5 | Position tolerance for that match |
| Persist in the world save | on | Section 8, offline and hosting |
| Persist on multiplayer client | on | Section 8, client side storage |
| Client storage root | `<UserDataPath>/Undo` | Root of the grid store (`Worlds/`) and the client histories (`Servers/`); empty in the config means the default |
| Client autosave interval s | 60 | Minimum time between client side writes after a change |
| Client history retention days | 90 | A client session's world folder with nothing written for this long is deleted at plugin start |
| Notifications | on | HUD text on undo, redo and refusals |
| Notification duration ms | 2000 | HUD text lifetime |
| Debug status file | off | Writes `<Client storage root>/status.json` after every history change, for the tests: both histories, the last notification, and the rows of the grid history dialog while it is open |
| Log level | Info | Plugin log verbosity in the game log |

## 12. Code structure

```
ClientPlugin/
  Plugin.cs                 IPlugin: Harmony PatchAll, config change hook, per frame update
  Config.cs                 options above
  Feedback.cs               log with the configured level, HUD notifications
  Session/                  UndoSession (MySessionComponentBase): per world state, loading
                            the document, the save snapshot, the client side file, debug
                            status file. WorldSavePatches: the write into the save's
                            staging folder and the server saving RPC
  History/                  Node, UndoHistory (tree, node cap, pending lock), Op base,
                            GridRegistry, Replay (the re-entrancy flag)
  Ops/                      one file per op kind: BuildBlocks, RazeBlocks, RestoreBlocks,
                            MergeBack, Paint; GridOps holds PasteGrids, GroupSnapshot and
                            CloseGrids; TerminalOps holds SetProperty, SetGridName and
                            SetProgram, TerminalValues reads and writes control values
                            as text. Also BlockLinks (section 7), SplitWatch,
                            PasteMatch (section 6), GameAccess (grid handles, builder
                            XML, placements). A paste into a grid replays as RestoreBlocks
  Record/                   BuildContextPatches, GridContextPatches and
                            TerminalContextPatches, the Recorder, the captures that
                            settle over frames (RazeCapture, GridCaptures), PaintStroke
                            and TerminalStroke
  Apply/                    Executor (also collects the ops' remarks for the notification),
                            Permissions: session mode and the predicates of section 2
  Input/                    key handlers of the build and terminal contexts, IsControl
                            rewrite
  Text/                     TextHistory (snapshots, no game references) and
                            TextHistories, the text box patches with one history per box
  Storage/                  UndoDocument and its serializer, StatusFile, Gz,
                            ClientRetention (old client histories); no game references
  GridStore/                GridStoreFolder (entry files, index, hashing, staged entries),
                            StoreRetention (budgets and cleanup), SortKeys (the dialog's
                            sort history), all three without game references;
                            StoredGroups (the game side, the oversized question)
  Gui/                      GridHistoryScreen
  Settings/                 template config dialog, plus a Note element for the option notes
UndoTests/                  xunit tests of History, Storage, the game free GridStore files and TextHistory, compiled
                            from the plugin sources, no game needed (`dotnet test UndoTests`)
tests/                      pytest suite and the isolated client rig, section 13; tests/ds
                            the dedicated server rig and its tests, run on their own
```

The history class is `UndoHistory`, since a class named like its `History` namespace
would shadow it everywhere else; `GridStoreFolder` is named that way for the same reason.

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

As built (`tests/rig.py`, setup in `tests/README.md`): Pulsar folder `~/.se-test/undo`
with a renamed launcher `UndoInterim.bin`, user data `~/.se-test/undo-data`, Remote
port 24176, offline (`--no-steam`). The world copy is switched to creative and gets
"unsupported stations" on, so a split station part stays where it is instead of
falling onto the base 300 m below (section 1). Two grids are injected into the sector
XML: a static test station above the player, whose cockpit toolbar, turret
controller, event controller and block group point at one light and whose second
part hangs on a one block bridge; and a dynamic ship far out in space whose outer
part carries a battery and a switched off thruster, so a removal can make a part
drift. The tree option is on for the whole run because the plugin reads its config
once at start; the linear behavior is covered by the unit tests. Block links are read
from the sector file after `POST /v1/game/save`, since the Remote API has no endpoint
for toolbars, controller lists or groups. The status file also carries the size of the
serialized undo document, which runs the XML serialization of the real ops in game,
and per node the "reference lost" and barrier flags and the store entries it refers
to. While the grid history dialog is open it also has the dialog's sort keys and rows.
The run's `Undo.cfg` sets the per world grid store budget to 1 MB, below the slider's
minimum in the config dialog, for the retention and oversized rows.

The grid tests need a clear line of sight, and the player spawns inside the Earth
base, where every ray ends at a wall within 15 m. They teleport the character onto
the free edge of the test station (`POST /v1/character/teleport`) and paste their test
grid 22 m out into the open air: a static 3x3 armor wall flagged as an unsupported
station (section 1). The wall is wide because the look-at aim lands up to about a
block off at that range; the tests re-aim until the Remote target API reports that
grid, which needs CometWorks/remote#28 for armor blocks (SE1-0070). The world has
copy and paste enabled, and each run removes the grid store of the previous one.

Coverage, one test per row, each followed by redo where it applies:

| Area | Drive | Verify |
|---|---|---|
| Build then undo | `POST /v1/character/build-block`, `POST /v1/input/key` Ctrl-Z | `CubeExists` call op on the position |
| Raze then undo | `POST /v1/character/grid-event` raze | `CubeExists`, and block detail (custom name, color) survives the restore |
| Raze a referenced block then undo | test world has a cockpit toolbar slot, a turret controller tool list, an event controller selection and a block group pointing at the block | after undo the block has the same entity id, the toolbar slot, tool list, selection and group still contain it |
| Raze that splits the grid then undo | remove the single connecting block of a two part test grid; then redo and undo again. The same on the drift ship with its thruster switched on first | one grid again with the original id, all block ids unchanged, cross part toolbar slot intact. The drifting part was 70 m off its place when the undo put it back |
| Paint then undo | grid-event color (fixed HSV differs from default), which goes through `ColorBlocks` | `colorMask` in block detail |
| Paste then undo | `POST /v1/blueprints/paste` | the store row of the node's entry (reason, name, block count, entity id); grid gone from `grid_list`; redo brings it back with the same name, entity id and every cell |
| Close grid then undo | `grid_close` set op, which sends the player's close request since CometWorks/remote#28 (SE1-0069) | the store row; grid back by name, entity id and every cell; redo removes it again |
| Clipboard delete | look at the grid, Ctrl-Delete, Yes in the confirmation box; this deletes the group | exactly one new node; undo brings the grid back under its id |
| Paste into a grid then undo | look at the wall, Ctrl-C, Ctrl-V, aim at the wall so the preview snaps onto its face, left button as raw gameplay input (`/v1/input/state`; the GUI click endpoint does not reach the clipboard) | the 9 merged cells exist; undo removes only them, redo puts them back |
| New grid from one block then undo | armor block into toolbar slot 1, `D1`, look into open air, left button | the new grid; undo removes it, redo brings it back under the same entity id |
| Terminal property | open the turret controller's terminal with the injected F key, Tab out of the block search box, `control/set` on its target locking checkbox and, three times like a drag, on a slider; also the `property` set op with the terminal closed, undone after opening it | `property` get op, the checkbox control following the undo, one node for the three slider values |
| Block name, grid name | terminal Name text box via `control/set`, twice, and the `custom_name` set op; grid name via the Info tab's text box and OK button. Renaming on the Info tab needs the Remote fix of SE1-0071 | block detail, `grid` get op, one node per rename |
| Text box | type two words into the terminal's block search box via `input/type`, with a pause longer than the coalescing window between them, then Ctrl-Z and Ctrl-Y with the box focused, more of them than there are steps; then Tab and Ctrl-Z | `properties.text` of the control after each step; the terminal history and the last notification unchanged while the box has the cursor, the terminal answering after the Tab |
| Limits | 210 builds, expect 200 nodes in the status file and the oldest gone | status file |
| Tree option | build twice, undo twice, build again, undo, redo | status file: the abandoned branch is kept next to the new one, redo follows the branch visited last; world state |
| Displaced vanilla keys | leave the cryo chamber, plain Z to switch the character dampeners off, Ctrl-Y and Ctrl-Z, then Ctrl-Shift-Z | dampeners stay off under undo and redo, come on with Ctrl-Shift-Z and stay on |
| Terminal context | build, open the terminal with K, Ctrl-Z, close it, Ctrl-Z | the terminal history answers "Nothing to undo" and the block stays; back in gameplay the build is undone |
| Grid store retention | per world budget 1 MB for the whole run; two block stations whose backup is 300 KB each, a programmable block holding random text. Paste A, change it, delete it, paste B, C and D | after C the pasted copy of A is gone and its deleted copy stays, as does B, which is older but the only backup of its grid; after D only newest copies were left, so the oldest goes, A's; undo down to the delete of A is refused with "backup was cleaned up" |
| Oversized entry | paste a station whose backup is 1.2 MB, answer No through the message box, Ctrl-Z; delete it, No, Ctrl-Z; paste again, Yes; change it, delete, Ctrl-Z | No: a barrier node, the refusal text, no row, no temporary file, the config unchanged. Yes: the config budget is 64 MB, the entry is in the index, the delete no longer asks and undo restores the grid |
| Grid history dialog | open with Ctrl-H, table size through `/v1/ui/screens/{i}/controls`, row texts from the status file (Remote reports no cells, SE1-0072), click the headers Time then Name with real mouse clicks, select a row and Delete with Yes, double click a row, left click to place | newest first at the start; Time flips to ascending; then by name, oldest first inside each name; the deleted row and its file are gone; the sort keys are in the config file; the dialog closed, the paste preview is on and the click places a grid with that name, which undo removes |
| Persistence | build, `POST /v1/game/save`, read `Undo.xml.gz` in the save folder and in the newest `Backup/` folder, reload from disk | node labels in the file, the backup's copy identical, same nodes and cursor after the reload, undo and redo of the build work |
| Backup restore | save, build more, save again; delete the world folder's files and copy the older backup's files in their place, the way the game does; reload | nodes and cursor of the older save, its block there and the later one not, undo works |
| Save As | `POST /v1/game/save` with a name; then from the title menu: Load Game, Save As | both new world folders have the file |
| Backup restore by the game | title menu, Load Game, Backups, a backup whose history differs from the current one, Load | the world folder has that backup's file, the loaded history matches it |
| Permissions | survival copy of the world, the character out of the cryo chamber: build a block, Ctrl-Z; `settings/admin-flag` creative tools on, Ctrl-Z. Raze a light with the tools on, switch them off, Ctrl-Z without and with components (`character/inventory/add`), Ctrl-Y. Paste with the tools on, Ctrl-Z and Ctrl-Y with them off | "needs creative tools" and the block stays, then it goes; "missing components", then "restored as construction sites", the block below full build level with a new name; the redo refused until the tools are back, then the undo restores the light under its old id; the pasted grid stays, and after its undo "copy and paste is disabled" |
| Dedicated server client | Magnetar DS with DirectTransport and one client (`notes/game-test-instance-modes`, mode A), survival world, the client its administrator with creative tools: Ctrl-C on a small station, Ctrl-V, click in the open air, Ctrl-Z, Ctrl-Y, twice; raze the light the cockpit toolbar, the turret controller and the block group point at, Ctrl-Z; a property and a block name set from outside, undone in the terminal. Then the server restarts without administrators and the same player joins again: Ctrl-Z, Ctrl-Y, a paint and its undo | the paste recorded by `OnEntityAdd` matching, the redone grid under a new id and closed by the next undo; "restored, some block links lost", the light back by name with a new id, and in the world the server saved: the toolbar slot and the tool list still on the old id, the group without the light; `Servers/<server>/<player>/<world>/history.xml.gz` with the nodes and the grid store next to it; after the rejoin the same nodes and cursor, both steps refused with "needs creative tools", the client still connected, the paint undone |

Implemented so far: the build, raze, referenced raze, split, paint, limits and tree
option rows, plus two rows for the displaced vanilla keys and the terminal context
(2026-09-27); the paste, close, clipboard delete, paste into a grid and new grid rows
(2026-10-01). Fifteen tests, about 70 seconds per run including client start and world
load. The grid tests need the Remote fixes of CometWorks/remote#28 in the Remote
working copy the rig loads.

The terminal and text rows followed on 2026-10-01 (`tests/test_terminal.py`): 21
tests and one skipped, about 90 seconds per run. The game offers "use" only when the
view ray passes a terminal detector of the block before it hits the model, so the
turret controller, an open frame with its console inside, is turned to face the
floor and the character looks at its screen from one cell away. The station also has
a programmable block, turned the same way, and the world has scripts enabled. The PB
row is a skipped test until SE1-0060; it was checked by hand, see section 16.

The persistence, retention, oversized and dialog rows followed on 2026-10-01
(`tests/test_grid_store.py`, `tests/test_world_save.py`): 30 tests and one skipped,
about two and a half minutes per run. `test_world_save.py` runs last, because it reloads the
world, leaves the session through the title menu and ends in a restored backup.

The permission and dedicated server rows followed on 2026-10-01
(`tests/test_z_survival.py`, `tests/ds/`). The survival file sorts after
`test_world_save.py` and loads its own world, `UndoTestSurvival`. The offline suite
is 34 tests and one skipped, about three minutes.

The dedicated server rig (`tests/ds/ds_rig.py`, `uv run pytest tests/ds`, about four
minutes, 8 tests) is kept out of the default run by `norecursedirs`. Server: the
machine's Magnetar launcher with its own `-config` and `-path` folders under
`~/.se-test/undo-ds`, `SE_DIRECT_TRANSPORT=1`, `-noimplicitmod`, UDP port 27116, a
profile with DirectTransport only. Its world is the survival test world with
`OnlineMode` set to `PUBLIC`, because in an offline world the game takes every player
for the owner, and a 3x3 station beside the test station as the copy source. Client:
Pulsar folder `~/.se-test/undo-mp` with Remote, Undo and DirectTransport, user data
`~/.se-test/undo-mp-data`, Remote port 24177, `--connect`, `--client-id
76561199500000131`, `--client-name`. The client is the server's only administrator in
the first half. For the second half the rig stops both, writes the server config
again without administrators and starts them on the world the server saved, so the
same player comes back as a regular one with the history of the first half. The
client's autosave interval is 5 seconds in this rig.

What the DS rig had to work around: a joining client crashed in
`MyEventControllerBlock.ProcessSelectedBlocks` while the station's event controller
had a selected block in the world file (SE1-0074), so the server world has none and
the event controller part of the link loss is not checked; Remote's target endpoint
names no grid on a client and its Info page crashes there (SE1-0075), so the aim is
checked by distance and the grid name is not renamed.

The suite was split and built out on 2026-10-01 (SE1-0077). Every test file is now
a session of its own with its own client and a fresh world, so the files no longer
run in name order or share state. `tests/run_pieces.py` runs them side by side on
client slots cloned from the first Pulsar folder: 19 files, about six minutes
with six clients. `test_z_survival.py` is `test_survival.py`. A file sets
its client's plugin options with a module level `UNDO_CONFIG`. `tests/README.md`
lists the files.

Rows added then, one test each unless noted:

| Area | Drive | Verify |
|---|---|---|
| Cube builder by hand | armor block in the hand, look at the station floor, left and right mouse button as raw gameplay input; Ctrl and the left button held while the mouse position of the held input moves; rotation keys with a slope; an interior light | the cell the block landed in, from the cube list before and after; "placed N blocks" as one node whose undo removes the whole line; orientation of the slope after redo and after a removal's undo; the light's entity id after redo |
| Paint by hand | build color slot set through `settings/build-color`, middle button; Ctrl-Shift and the middle button | the block's color in the cube list; a second paint with the same color records nothing; "painted 274 blocks" as one node, its undo gives every block its own color back, one of them painted before |
| Cut | Ctrl-X on the pasted wall, Yes, Ctrl-Z, Ctrl-V into the open air, click | one "deleted" node, the wall back under its id, the copy pasted and undone |
| Linear history | tree option at its default: build two, undo one, build a third; walk to both ends; five builds, then five Ctrl-Z with nothing waited for in between | the undone node is gone, "Nothing to redo", "Nothing to undo"; all five steps taken |
| HUD text | undo, redo | `GET /v1/hud/notifications` has the texts in order |
| From a seat | the character still in the cryo chamber it starts in | build, undo, redo |
| Mixed steps | place, paint, remove another block, paste a grid; undo all, redo all | the world after every step |
| Steps across a re-created grid | paste a wall, add a block, paint it, delete the wall; undo all four, redo all four | the wall comes back with the block and its paint; the redone steps land on the wall the redo created |
| Damage | a placed block destroyed with the `DoDamage` call op; a light damaged, removed, restored | no node for the destruction, the placement still goes both ways; the restored light has the damaged integrity |
| Block state | a light switched off, removed, restored | enabled state, name, color, entity id |
| Split into four | a plus sign pasted, its middle removed | "removed 1 block, 3 parts split off", one grid again with every cell after undo |
| Cargo | a pasted grid with a small cargo container holding components and a large one | the deleted grid comes back with the items; the removed container comes back with them; the large container, 3x3x3 cells, comes back by its min cell with its id |
| Several grids | a blueprint of two grids pasted; the same one grid blueprint pasted twice; the drift ship deleted | "pasted X and 1 more grid" undone and redone as a whole under the old ids; the two pastes are told apart; the ship comes back dynamic with its block ids |
| Other screens | toolbar config (G) and the pause menu over gameplay, Ctrl-Z | the plugin does not react, the block stays; after closing the screen the undo works |
| Terminal value kinds | float, bool and color properties set from outside; toolbar actions (`IncreaseRadius`, `OnOff_Off`) through the `action` set op; the block group selected in the block list and one slider moved | undo and redo in the terminal; "changed Radius of 2 blocks" as one node whose undo gives each light its own radius back |
| Coalescing | three color changes without a pause, one after a pause, then two controls without a pause | one node, one node, two nodes |
| Program | two programs saved from the editor's OK button (`MyGuiScreenEditor`, typed with `input/type`); two set through the mod API (`SetProgram` call op) | `<Program>` of the block in the saved sector after each undo and redo, down to no program; Ctrl-Z inside the editor does not reach the plugin |
| Block gone | a property changed, the block removed, another block built in its place | "the block no longer exists" both times; after the build history restored the block the terminal undo works |
| Other grid | a light of the drift ship switched, undone in the station's terminal | property value |
| Combobox | the turret controller's Content combobox, three changes on a fresh client, and one after other controls were used | a node each, undo and redo |
| Many slider changes | twelve bursts of four radius changes | a node per burst, past the 30 calls where the recording used to stop (SE1-0079) |
| Text boxes elsewhere | the search box of the toolbar config screen and the chat box: two words with a pause, Ctrl-Z and Ctrl-Y past both ends; typing without a pause; typing after an undo; the screen opened again | the text after each step; the build history unchanged; a new box has no history |
| Options | one client with the bindings on Ctrl-Alt-Z, Ctrl-Alt-Y and Ctrl-Alt-H, 10 nodes per history, full state off, recording outside the terminal off, separate text undo off, notifications off, persistence off, "never store", another storage root | Ctrl-Z is vanilla relative dampeners again; 12 builds leave 10 nodes; a removed light comes back complete under its id without its name; a property set from outside leaves no node, the slider in the terminal does, and Ctrl-Alt-Z with the cursor in the search box undoes it; no HUD notification; no `Undo.xml.gz` in the save; an oversized paste becomes a barrier without a question; status file and grid store under the other root |
| Contexts off | all three contexts disabled | no node for a build, a paint, a paste, a slider or typed text; Ctrl-Z switches the relative dampeners; Ctrl-H opens no dialog |
| Always raise | oversized paste with that option | no question, the config budget is 64 MB, undo and redo work |
| Dialog buttons | the dialog in a world with no backups; Paste, Delete answered with No, Close, Escape, the key pressed twice | no rows; the backup stays; one dialog; the Paste button puts the backup on the clipboard and a click places it |
| Budget question closed | Escape on the question | a barrier node like on No |
| Mechanical connections | a rotor, a hinge (placed with the cube builder, the build request cannot turn it) and a piston base built on the station, one armor block on the top part; the base removed, undo, redo, undo; the rotor head removed instead; older steps undone after the restore; a rotor on the drift ship, removed while the ship coasts | base and top part back under their ids, the top grid at its old distance and still there three seconds later, `TopBlockId` of the base in the saved sector; the station and its other blocks keep their ids; the ship is restored where it is now, with its speed; undoing the placement of a rotor leaves no top part behind |
| A restored block is the block it was | the links rig: lights, a camera, a timer, a button panel, a sensor, an event controller, a remote control, a turret controller, a cargo container with items, a programmable block, a cockpit and a battery, with toolbar slots, a bound camera, tool and block lists, button names and two block groups between them; each of the 13 blocks removed and restored; the light removed, undone, redone, undone; the timer triggered before and after the light and the timer were restored; the whole grid deleted and restored; the character standing in a removed block's cell | the rig's part of the saved sector is the same as before, block by block and group by group; the trigger switches the light; "something is in the way" and the step stays, then works |
| Inventory | the container removed and restored twice over undo and redo; a container placed, filled from another one through `inventory_transfer`, the placement undone and redone; the same removal in a world with `TemporaryContainers` off | the items are in the container, the cell is free after a replayed removal, nothing is left lying in it |
| Redo of a placement | a timer placed, renamed, its delay changed; undo, redo | the same entity id and name, the saved sector as before the undo |
| Ctrl-Z with nothing to undo | empty build history, dampeners off, Ctrl-Z; then a step to undo, Ctrl-Z | the dampeners come on and the plugin says nothing; with a step the key undoes it and the dampeners stay off |
| Load edge cases | terminal change and a removed block saved and loaded; a paste undone, saved, loaded, redone; garbage in `Undo.xml.gz`; `<Version>` 999 in it; another world loaded in between; the pasted grid cut out of the sector file | both histories back, the block restored under its id; the grid from the store; an empty history and the log line for each broken file, then a good file from the next save; the other world starts empty and the first has its saved history again; the steps of the missing grid are refused and stay |

Two defects came out of this, SE1-0078 and SE1-0079. Both were fixed on 2026-10-02
and their tests pass without an expected failure mark.

Screenshots were tried and dropped. A frame rarely shows what a step changed, so
each file instead keeps what the plugin logged per test, the recorded steps and the
notifications, in `tests/artifacts/<file>.log`.

Still not driven by a test, offline: plane builds and area removal (Ctrl-Shift drag,
Ctrl and the right button), skins (the offline client owns none), symmetry, the
config dialog itself and its Grid history button, a rename typed into the Name box
with its text undo, "restored with changes", the total budget across worlds in game.

Friends (lobby) mode was not run, neither as host nor as joined client. A lobby is a
Steam lobby (`MyMultiplayer.HostLobby`, `MyGameService.CreateLobby`); a `--no-steam`
client has no lobby service, DirectTransport only replaces the transport towards a
dedicated server, and a second client would need a second Steam account. Steam was
not running on the test machine. What stands in for it: the host runs the offline
code paths (`Sync.IsServer`), all tested offline, and a lobby client is a
`MyMultiplayerClientBase` like the dedicated server client, tested there. Not seen
at all: that the results of the host's server entry points (`BuildBlockRequestInternal`,
`MergeGrid_MergeBlock`, `CreateFromObjectBuilderAndAdd`, the group and controller
fix-ups) reach a joined client, and the program request a host sends. SE1-0076 asks
for a way to host without Steam.

Gaps that need Remote plugin work first (SE1-0060): no endpoint to read a PB program
(the tests read it from the saved sector), `grid-event` paints one fixed color (the
cube builder tests paint by hand with a chosen one), no host lobby endpoint. Table cells and
header clicks (SE1-0072) and the save browser's selection (SE1-0073) have workarounds
in the tests.

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

- R1, reopened and closed again on 2026-10-02 (SE1-0079). What follows describes a
  Harmony patch on `MyTerminalValueControl<TBlock, TValue>.SetValue`, which worked for
  about 30 calls per control class and then stopped recording, when the runtime
  compiled the shared generic method again. The plugin no longer patches these
  methods. `TerminalContextPatches.PatchControls` replaces the `Setter` delegate of
  every value control with one that records around the original; every `SetValue`
  ends in that delegate. A weak table keeps track of the wrapped controls, the scan
  runs at the same moments as before. The rest of this entry is history.
- R1 as first closed on 2026-10-01, confirmed in game on .NET 10 (Pulsar Interim, Linux).
  `MyTerminalValueControl<TBlock, TValue>.SetValue` is patched per closed type, and
  `TBlock` is always a reference type, so the runtime shares one method body among
  all block types. `MethodBase.MethodHandle` is the same for every such
  instantiation, which makes the dedupe by handle exact: a world with 89 terminal
  block types and 1700 value controls ends up with 6 patched methods (the on/off
  switch, checkbox, text box, combo box, color and slider classes), in about 20 ms.
  The patch on one instantiation runs for all of them, with the real control as
  `__instance` and the real block as the first argument; the hooks take both as
  `object`. Controls a mod adds later use the same six classes and are covered
  without another pass. `MyTerminalControlProperty<TBlock, TValue>` would add one
  method per value type; the vanilla game registers none. The patcher runs at
  every session start, because the factory forgets its controls on unload, and
  skips handles it has patched. The `Sync<T>.ValueChangedFromTo` fallback was not
  needed and is not implemented. Corrected on 2026-10-01, later: the patcher must
  not ask the factory about a type that has no controls yet. The game creates the
  controls of a type in the constructor of its first block, behind
  `AreControlsCreated<T>()`; `GetControls(Type)` registers an empty list for an
  unknown type, which makes that check true, and the type then has only the
  inherited controls for the rest of the session. The first version did this for
  every type missing from the world at session start, which on a server client is
  all of them. `EnsureControlsAreCreated` does not help, it only calls a static
  `CreateTerminalControls` and the game has none. The patcher now skips types
  without controls and runs again after a grid arrived, after a build and when the
  terminal opens, at most once a second. Not run: the .NET Framework 4.8 build (Pulsar
  Legacy on Windows). If handles differ per instantiation there, the same body
  gets the hooks more than once; the depth counter of section 5 keeps that to one
  recorded change per call.
- R2, paste correlation on DS clients is a heuristic (section 6). Acceptable for the
  stated use case; the companion ticket removes it.
- R3, `RazeBlocks` reverse in survival without creative tools only rebuilds skeleton
  blocks. Documented in the notification text ("restored as construction sites"),
  seen in game offline.
- R7, closed on 2026-09-27, confirmed in game (offline, creative). `BuildBlockRequestInternal`
  with the saved builder, `instantBuild` and the local Steam id as sender brings a
  removed interior light back with its old entity id, custom name, color, enabled
  state and full integrity; with a builder, `BuildBlock` takes the id from the
  builder's `EntityId`, which the plugin keeps equal to `location.EntityId`, and the
  id is free again by the time the removal is recorded. The live merge works for a
  static piece (undo, redo, undo on the same removal, the piece handle rebound in
  between) and for a dynamic piece pushed 70 m away by its own thruster before the
  undo: after the world matrix reset and `MergeGrid_MergeBlock(piece, Vector3I.Zero,
  checkMergeOrder: false)` it was one grid again with every block id unchanged. The
  merge transform only takes the piece's orientation from its world matrix, so the
  reset is what keeps a rotated piece from merging at a wrong orientation. The
  fallback was not needed.
- R4, disk and memory: a group snapshot of a large ship is several MB uncompressed.
  Entries are written gzip compressed and read only when applied or pasted; the store
  budgets bound the total on disk.
- R5, the game's paste strips blocks with missing DLC or skins and scripts for non
  scripters, so an undo of a delete can come back slightly different on a server. The
  plugin reports "restored with changes" when the block count differs. Implemented,
  never triggered: the test client owns no DLC blocks to lose.
- R6, `ChangeColorAndSkin` also fires for changes received from other players. The paint
  stroke attribution in section 5 filters those; a change that arrives during a local
  stroke on the same grid could be misattributed. Rare, and the reverse just repaints
  those blocks to their pre-stroke color.

- R8, the grid store is keyed by the save folder name plus the world id, and Save As
  gives the copy a new folder name. The copy carries the history but finds no
  entries under its own key, so undoing a delete or redoing a paste there is refused
  with "backup was cleaned up" while everything else works. Copying the entries
  along would multiply the store with every Save As, which is what the key avoids.
  Looking the entry up in the other world folders of the same world id would fix it
  if it turns out to matter.

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

Status on 2026-10-01, slice 5: step 8 is done on the `impl-5-multiplayer` branch;
the tree option of that step was covered in slice 1. Run in game by the test suites:
offline survival (section 13, permissions row) and a client of a dedicated server
as administrator and as regular player (dedicated server row), which was the first
time any client path ran: recording a clipboard paste, `OnEntityAdd` matching, the
paste request replay, close requests, block restore through `PasteBlocksToGrid`,
the fixed settle wait for splits, a property and a block name through the server,
paint, the client history file with its autosave and reload, and the refusals.
Three defects of earlier slices showed up there and are fixed: the terminal control
scan (R1), the client history loaded before the identity was known (section 8), and
grid handles marked lost at load on a client (section 6).

Still not run after this slice: both lobby modes (section 13); on a client a split
with its merge back by re-paste, the group snapshot and its restore, "reference
lost", a paste into a grid, a new grid from one block, the server failure
notifications, the grid name, a program sent as a request, the writes on unload and
on the server saving RPC (the interval write is what the test waits for), "restored
with changes"; offline the rebuild from definition with the full state option off
(the same code as the survival fallback).

Status on 2026-10-01, slice 4: steps 7 and 9 are done on the `impl-4-persistence`
branch. Run in game, offline, by the test suite: everything in the persistence,
backup, Save As, retention, oversized and dialog rows of section 13. Unit tested
only: the total budget across several world folders, the retention of client
histories, the sort key rules beyond the two clicks the dialog test makes, the
budget raise arithmetic. Not run, because the rig has no server: the client side
file (its path keys, the write on unload, on the server saving RPC and on the
interval, loading it back) and the "persist on multiplayer client" switch. Not run
either: the "Always raise" and "Never store" settings, the total budget in game, a
closed message box counting as No, the Paste button of the dialog (it calls the same
method as the double click), the button in the config dialog, a version mismatch
in game (the serializer's answer is unit tested), cloud saves.

Status on 2026-10-01, later: steps 5 and 6 are done on the `impl-3-terminal` branch.
Run in game, offline, by the test suite: a checkbox and a slider through the terminal
UI, a property, a block name and a grid name set from outside with "record outside
the terminal" on, the block name through the Name box, each with undo and redo, and
typing in a text box with Ctrl-Z and Ctrl-Y. Checked by hand in a windowed client,
driven through the Remote API with screenshots: three programs saved from the PB
editor's OK button each made a node; Ctrl-Z in the terminal put the previous source
back, and at the first node the editor showed the default template again, as for a
block without a program; Ctrl-Y brought the last program back, and the saved world
had it. The block had no power, so no program ran. Unit tested only: the text
snapshot rules (coalescing, cap, caret) and the gzip helper. Not run: every client
path (the grid name pending check, a program sent as a request), multi selection in
the terminal (the per block ops are the same code as one block), the color, combo
box, enum and `MyStringId` value types, the "save changes?" question of the PB
editor, the refusals of section 2, and the .NET Framework build. Open for the
multiplayer step: on a hosting server `ProgramData` recompiles without telling the
clients, whose copies of the source go stale; raising the update request there
would broadcast it.

Status on 2026-10-01: step 4 and the store part of step 9 are done on the
`impl-2-grids` branch. Run in game, offline: paste, delete through the clipboard,
paste into a grid through the clipboard, and a new grid from one block, each with
undo and redo, on the local server paths. Unit tested only: the grid store index and
hashing, and the pending op failure. Not run at all, because the rig is offline: every
client path of this step (paste request replay, `OnEntityAdd` matching, "reference
lost", recording a client's clipboard paste, merge and new grid, the group snapshot
and its restore, the server failure notifications) and the server path of
`GroupSnapshot`, which only a client creates.

Status on 2026-09-27: steps 1 to 3 are done on the `impl-1-core` branch. Not run in
game yet, because the Remote API places and removes single blocks and paints one
fixed color only: area builds and area removal from the cube builder (including the
piloted cockpit question), skins, strokes opened by `SkinBlocks` and `SkinGrid`, the
rebuild from definition when creative rights are missing or full state is off. None of
the client paths ran either (restore by paste, merge back by re-paste, recording on a
client, the fixed settle wait for client splits); they belong to the multiplayer client
step.

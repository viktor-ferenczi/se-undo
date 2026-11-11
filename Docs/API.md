# Undo API for plugins and mods

Other plugins and mods can put their own steps into a player's undo history. Undo
then takes them back on Ctrl-Z and does them again on Ctrl-Y, like the player's own
building. There is no assembly reference: you ask for the API with a mod message,
which works the same from a plugin and from a mod script, and without Undo loaded
nothing answers and nothing happens. The [Sections](https://github.com/viktor-ferenczi/se-sections)
plugin uses it for its cut and delete.

## Getting the functions

```csharp
const long UndoApiMessageId = 487710606L;
Dictionary<string, Delegate> undo;

MyAPIGateway.Utilities.SendModMessage(
    UndoApiMessageId,
    (Action<Dictionary<string, Delegate>>)(api => undo = api));
```

The callback runs inside the call when Undo is loaded. Ask in `BeforeStart` or
later. On a dedicated server Undo starts with the first update after the world
loaded, so if `undo` is still null, ask again later, for example right before you
need it; the call is cheap.

Cast the delegates you need once:

```csharp
var recordRemoval = (Func<IMyCubeGrid, List<Vector3I>, List<IMyCubeGrid>, string, ulong, bool>)
    undo["RecordRemoval"];
```

## Where steps are recorded

Undo records where the world changes: offline, on the host of a friends game, and
on a dedicated server that runs the Undo companion. Call the API there, from the
code that changes the world, on the main thread. Every function takes the Steam id
of the player the step belongs to; 0 means the local player. A player served by a
server or a lobby host (a client running Undo that connected to a server with the
companion) has the history on that server, so pass that player's Steam id there.

Every function returns false and does nothing when Undo does not record for that
player now: no creative tools (or not a creative world), the player's client does
not run Undo, the build context is off in the player's options, or the call came
from another thread. Steps are never recorded on a client of a server.

## Functions

| Name | Type | What it does |
|---|---|---|
| `Version` | `Func<int>` | The API version, 1. A later version only adds functions |
| `IsRecording` | `Func<ulong, bool>` | Whether a step of the player would be recorded now |
| `RecordRemoval` | `Func<IMyCubeGrid, List<Vector3I>, List<IMyCubeGrid>, string, ulong, bool>` | Grid, block positions (any cell of each block), grids you close along with them, label, Steam id. See below |
| `AddStep` | `Func<string, string, string, string, ulong, bool>` | Owner, label, undo data, redo data, Steam id. See below |
| `RegisterStepHandler` | `Action<string, Func<string, ulong, string>>` | Owner and the handler that applies its steps; null removes it |

### RecordRemoval

Call it right before you remove blocks yourself, by whatever means: `RemoveBlock`,
`RazeBlocks`, anything. Undo takes the blocks with their full state at that moment,
and once the removal and the grid splits it causes have settled (a few frames), it
adds one step with your label, for example "cut 12 blocks with Sections". Undo puts
the blocks back as they were, with their entity ids, so toolbars and other blocks
that refer to them work again, and merges the parts that split off back. Redo
removes the same blocks again.

Pass the grids you close along with the blocks as the third argument, such as
subgrids your removal would leave without a connection. A removal with such grids,
one that takes a rotor, hinge, piston or connector apart, and one that removes
every block of the grid is undone by putting the grid's whole group back from a
backup, the same way as Undo's own removals of mechanical blocks.

### AddStep and RegisterStepHandler

For a change Undo cannot capture by itself, you apply undo and redo yourself:

```csharp
// Once per session, before the first step
var register = (Action<string, Func<string, ulong, string>>)undo["RegisterStepHandler"];
register("MyPlugin", (data, steamId) =>
{
    // Apply what the data says for this player. Return null when done, or why it
    // could not be done, without having changed anything; the player sees it.
    return Apply(data, steamId) ? null : "the block is gone";
});

// After each change of yours
var addStep = (Func<string, string, string, string, ulong, bool>)undo["AddStep"];
addStep("MyPlugin", "painted 5 blocks", undoData, redoData, steamId);
```

The data is yours, any text; it is saved with the history in the world, so keep
it small. Undo calls the owner's handler with the undo data on Ctrl-Z and the redo
data on Ctrl-Y. A step whose owner has no handler in the session is refused with
"<owner> is not loaded". An exception from the handler marks the step's result as
unknown, and Undo does not step across it again. Handlers are dropped when the
session unloads, so register again in each session.

## Mods

A mod script can use the API too: the mod message and the delegate types are
whitelisted. A mod runs on clients as well as on the server, and only the server
side records, so call it where `MyAPIGateway.Multiplayer.IsServer` is true and pass
the player's Steam id. Undo does not run inside a mod, so nothing happens for the
players of a server without the Undo companion.

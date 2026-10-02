using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.Apply;
using ClientPlugin.GridStore;
using ClientPlugin.History;
using ClientPlugin.Session;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.Network;
using VRageMath;

namespace ClientPlugin.Ops;

// Creates a stored grid group again: the redo of a paste, the undo of a delete.
// Server: re-created without id remapping, so the grids and blocks come back as
// themselves and references to them survive; the game replicates the new grids.
// Client: the clipboard's paste request, new ids, matched when the grids arrive.
public class PasteGridsOp : Op
{
    public const string BackupGone = "backup was cleaned up";

    // Grid store entry holding the builders
    public string Entry;

    // One handle per stored grid, in the entry's order
    public List<int> Grids = new List<int>();

    public override IEnumerable<string> StoreRefs() => new[] { Entry };

    public override string Validate(GridRegistry grids)
    {
        if (!UndoSession.Store.Has(Entry))
            return BackupGone;
        if (!Permissions.CanPasteGrids)
            return Permissions.NoCopyPaste;
        return Grids.Any(h => grids.ResolveGrid(h) != null) ? "the grid exists already" : null;
    }

    public override Func<bool> Apply(GridRegistry grids) =>
        Recreate(StoredGroups.Load(Entry), grids);

    protected Func<bool> Recreate(List<MyObjectBuilder_CubeGrid> builders, GridRegistry grids)
    {
        if (Sync.IsServer)
        {
            // Something else took an id meanwhile; a duplicate id would crash the game
            if (builders.Any(IdsTaken))
            {
                Log.Warning(
                    $"Ids of {builders[0].DisplayName} are taken, it comes back with new ids"
                );
                MyEntities.RemapObjectBuilderCollection(builders);
            }

            for (var i = 0; i < builders.Count; i++)
            {
                builders[i].CreatePhysics = true;
                var grid = (MyCubeGrid)
                    MyEntities.CreateFromObjectBuilderAndAdd(builders[i], fadeIn: false);
                grids.Rebind(Grids[i], grid.EntityId);
                if (grid.BlocksCount != builders[i].CubeBlocks.Count)
                    Executor.Remark(Permissions.WithChanges);
            }
            return null;
        }

        var match = PasteMatch.For(builders);
        MyMultiplayer.RaiseStaticEvent(
            (IMyEventOwner _) => MyCubeGrid.TryPasteGrid_Implementation,
            new MyCubeGrid.MyPasteGridParameters(
                builders,
                detectDisconnects: false,
                builders[0].LinearVelocity,
                MySession.Static.CreativeToolsEnabled(Sync.MyId),
                new MyCubeGrid.RelativeOffset { Use = false },
                MySession.Static.GetComponent<MySessionComponentDLC>().GetAvailableClientDLCsIds()
            )
        );
        return () =>
        {
            var done = match.Done;
            for (var i = 0; i < Grids.Count; i++)
            {
                if (match.Grids[i] == null)
                    continue;
                grids.Rebind(Grids[i], match.Grids[i].EntityId);

                // The server strips blocks with a missing DLC or skin, and scripts
                if (done && match.Grids[i].BlocksCount != builders[i].CubeBlocks.Count)
                    Executor.Remark(Permissions.WithChanges);
            }
            return done && match.AllMatched;
        };
    }

    private static bool IdsTaken(MyObjectBuilder_CubeGrid builder) =>
        MyEntities.EntityExists(builder.EntityId)
        || builder.CubeBlocks.Any(b => b.EntityId != 0 && MyEntities.EntityExists(b.EntityId));
}

// The grid group as it was before an asynchronous replay on a client, design section
// 7. Closes the group's current grids, then creates the snapshot again once they are gone.
//
// It is also the undo of a removal that takes a rotor, hinge, piston or wheel
// suspension apart (RazeCapture). The part that came loose is not connected again,
// which fails in too many ways; the whole group is put back as it was instead.
public class GroupSnapshotOp : PasteGridsOp
{
    // Grids that are not in the snapshot and go with the current ones: the pieces a
    // removal split off, which the snapshot holds as part of their grid
    public List<int> Closing = new List<int>();

    // What the removed blocks' inventories dropped into the world (SpillWatch); the
    // restored group has those items in the blocks again
    public List<long> Spilled = new List<long>();

    // Index of the stored grid the group follows when it moved since the snapshot,
    // -1 to put the group back exactly where it was
    public int Anchor = -1;

    private IEnumerable<int> Current => Grids.Concat(Closing);

    public override string Validate(GridRegistry grids)
    {
        if (!UndoSession.Store.Has(Entry))
            return BackupGone;
        if (!Permissions.CanPasteGrids)
            return Permissions.NoCopyPaste;
        return Current
            .Select(grids.ResolveGrid)
            .Where(g => g != null)
            .Select(Permissions.CloseRefusal)
            .FirstOrDefault(r => r != null);
    }

    public override Func<bool> Apply(GridRegistry grids)
    {
        var builders = StoredGroups.Load(Entry);
        if (Anchor >= 0 && Anchor < Grids.Count)
            Follow(builders, grids.ResolveGrid(Grids[Anchor]));

        Record.SpillWatch.Remove(Spilled);
        var current = Current.Select(grids.ResolveGrid).Where(g => g != null).ToList();
        var ids = current.Select(g => g.EntityId).ToList();
        foreach (var grid in current)
            grid.SendGridCloseRequest();

        Func<bool> created = null;
        return () =>
        {
            if (created == null)
            {
                if (ids.Any(MyEntities.EntityExists))
                    return false;
                created = Recreate(builders, grids) ?? (() => true);
            }
            return created();
        };
    }

    // Moves the stored group to where its anchor grid is now and gives it the
    // anchor's velocity, so a ship that flew on does not jump back
    private void Follow(List<MyObjectBuilder_CubeGrid> builders, MyCubeGrid anchor)
    {
        var stored = builders[Anchor].PositionAndOrientation;
        if (anchor == null || stored == null)
            return;

        var move = MatrixD.Invert(stored.Value.GetMatrix()) * anchor.WorldMatrix;
        foreach (var builder in builders)
        {
            if (builder.PositionAndOrientation == null)
                continue;
            builder.PositionAndOrientation = new MyPositionAndOrientation(
                builder.PositionAndOrientation.Value.GetMatrix() * move
            );
            if (anchor.Physics != null)
            {
                builder.LinearVelocity = anchor.Physics.LinearVelocity;
                builder.AngularVelocity = anchor.Physics.AngularVelocity;
            }
        }
    }

    // Taken before ops that change these grids are sent from a client. Returns what
    // writes the snapshot to the grid store once the result turns out unknown.
    public static Func<Op> Prepare(IEnumerable<Op> ops, GridRegistry grids)
    {
        var group = ops.SelectMany(op => op.GridHandles())
            .Select(grids.ResolveGrid)
            .Where(g => g != null)
            .SelectMany(StoredGroups.GroupOf)
            .Distinct()
            .ToList();
        if (group.Count == 0)
            return null;

        var builders = StoredGroups.Capture(group);
        var handles = group.Select(g => grids.GetOrAdd(g.EntityId)).ToList();
        return () =>
            new GroupSnapshotOp
            {
                Entry = StoredGroups.SaveNow(builders, StoreReason.Snapshot).Id,
                Grids = handles,
            };
    }
}

// Removes grids through the player's close request, the one the clipboard sends
public class CloseGridsOp : Op
{
    public List<int> Grids = new List<int>();

    public override IEnumerable<int> GridHandles() => Grids;

    public override string Validate(GridRegistry grids)
    {
        foreach (var handle in Grids)
        {
            var grid = grids.ResolveGrid(handle);
            if (grid == null)
                return GameAccess.GridMissing;
            var refusal = Permissions.CloseRefusal(grid);
            if (refusal != null)
                return refusal;
        }
        return null;
    }

    // Also checked on a local server: an economy station is refused without an error
    public override Func<bool> Apply(GridRegistry grids)
    {
        var closing = Grids.Select(grids.ResolveGrid).ToList();
        foreach (var grid in closing)
            grid.SendGridCloseRequest();
        return () => closing.All(g => g.MarkedForClose);
    }
}

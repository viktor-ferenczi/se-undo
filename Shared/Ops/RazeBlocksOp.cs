using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Multiplayer;
using Shared.Apply;
using Shared.GridStore;
using Shared.History;
using Shared.Record;
using Shared.Session;
using VRageMath;

namespace Shared.Ops;

// A grid split off by a removal. The key is the position of one of its blocks, which
// is the same in the piece and in the grid it came from, so a replayed removal can
// find the new piece and rebind the handle to it.
public class SplitPiece
{
    public int Grid;
    public Vector3I Key;
}

public class RazeBlocksOp : Op
{
    public int Grid;
    public List<Vector3I> Positions = new List<Vector3I>();
    public List<SplitPiece> Pieces = new List<SplitPiece>();

    // Set on the undo of a placement. A rotor, hinge, piston or suspension brings
    // its top part along as a grid of its own when it is placed; removing the base
    // alone would leave that part lying around.
    public bool WithTopParts;

    public override string Validate(GridRegistry grids) =>
        grids.ResolveGrid(Grid) == null ? GameAccess.GridMissing : null;

    // The undo of a placement: saves the blocks as they are now into the placement
    // op, so its redo brings them back with what was set on them and put into them
    // since.
    public override void Prepare(GridRegistry grids, List<Op> opposite)
    {
        var build = opposite.OfType<BuildBlocksOp>().FirstOrDefault(o => o.Grid == Grid);
        var grid = grids.ResolveGrid(Grid);
        if (!WithTopParts || build == null || grid == null)
            return;

        build.Restore = null;
        build.Snapshot = null;
        var blocks = Positions.Select(grid.BlockAt).Where(b => b != null).ToList();
        if (blocks.Count == 0)
            return;

        try
        {
            if (blocks.Any(RazeCapture.NeedsGroupBackup))
            {
                var group = StoredGroups.GroupOf(grid);
                build.Snapshot = new GroupSnapshotOp
                {
                    Entry = StoredGroups
                        .SaveNow(StoredGroups.Capture(group), StoreReason.Snapshot)
                        .Id,
                    Grids = group.Select(g => grids.GetOrAdd(g.EntityId)).ToList(),
                    Anchor = group.IndexOf(grid),
                };
            }
            else
            {
                build.Restore = new RestoreBlocksOp
                {
                    Grid = Grid,
                    BlocksXml = BuilderXml.Write(
                        GameAccess.BlocksBuilder(
                            grid,
                            blocks.Select(b => b.GetObjectBuilder()).ToList()
                        )
                    ),
                    Links = BlockLinks.Capture(grid, blocks),
                };
            }
        }
        catch (Exception e)
        {
            // Redo then builds the blocks from their definition
            Log.Warning($"Saving the blocks before their removal failed: {e.Message}");
        }
    }

    public override Func<bool> Apply(GridRegistry grids)
    {
        var grid = grids.ResolveGrid(Grid);
        var watch = Pieces.Count == 0 ? null : new SplitWatch(grid);
        if (WithTopParts)
            CloseTopParts(grid);
        EmptyInventories(grid);
        grid.RazeBlocks(
            new List<Vector3I>(Positions),
            Actor.Current.CharacterId,
            Actor.Current.SteamId
        );

        if (watch == null)
            return null;

        return () =>
        {
            if (!watch.Settled)
                return false;
            foreach (var piece in watch.Close())
            {
                foreach (var split in Pieces.Where(s => piece.CubeExists(s.Key)))
                    grids.Rebind(split.Grid, piece.EntityId);
            }
            return true;
        };
    }

    // The game drops what a removed block holds into the world. Whatever brings the
    // blocks back has their items, so on a replay they are taken out first: nothing
    // is left lying where the blocks were, and nothing is in the way of the restore.
    private void EmptyInventories(MyCubeGrid grid)
    {
        foreach (var position in Positions)
        {
            var block = grid.GetCubeBlock(position)?.FatBlock;
            if (!SpillWatch.HasItems(block))
                continue;
            for (var i = 0; i < block.InventoryCount; i++)
                block.GetInventory(i)?.Clear();
        }
    }

    // Only a top part that is still alone on its grid. Anything built onto it since
    // was a later step, which is undone before this one.
    private void CloseTopParts(MyCubeGrid grid)
    {
        foreach (var position in Positions)
        {
            if (
                grid.GetCubeBlock(position)?.FatBlock is MyMechanicalConnectionBlockBase block
                && block.TopGrid != null
                && block.TopGrid.BlocksCount == 1
            )
                GameAccess.Close(block.TopGrid);
        }
    }
}

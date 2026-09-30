using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.Apply;
using ClientPlugin.History;
using Sandbox.Game.Multiplayer;
using VRageMath;

namespace ClientPlugin.Ops;

// A grid split off by a removal. The key is the position of one of its blocks, which
// is the same in the piece and in the grid it came from, so a replayed removal can
// find the new piece and rebind the handle to it.
public class SplitPiece
{
    public int Grid;
    public Vector3I Key;

    // Grid store entry of the piece as it was right after the split, captured on
    // multiplayer clients only
    public string Entry;
}

public class RazeBlocksOp : Op
{
    public int Grid;
    public List<Vector3I> Positions = new List<Vector3I>();
    public List<SplitPiece> Pieces = new List<SplitPiece>();

    public override IEnumerable<int> GridHandles() => new[] { Grid };

    public override IEnumerable<string> StoreRefs() =>
        Pieces.Select(p => p.Entry).Where(id => id != null);

    public override string Validate(GridRegistry grids)
    {
        if (grids.ResolveGrid(Grid) == null)
            return GameAccess.GridMissing;
        return Permissions.CanRemoveBlocks ? null : Permissions.NeedsCreativeTools;
    }

    public override Func<bool> Apply(GridRegistry grids)
    {
        var grid = grids.ResolveGrid(Grid);
        var watch = Pieces.Count == 0 ? null : new SplitWatch(grid);
        grid.RazeBlocks(new List<Vector3I>(Positions), GameAccess.LocalCharacterId, Sync.MyId);

        if (watch == null)
        {
            if (Sync.IsServer)
                return null;
            return () => Positions.All(p => grid.GetCubeBlock(p) == null);
        }

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
}

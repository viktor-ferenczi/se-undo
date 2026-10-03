using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.Apply;
using ClientPlugin.GridStore;
using ClientPlugin.History;
using ClientPlugin.Session;
using VRage;
using VRage.Game;
using VRageMath;

namespace ClientPlugin.Ops;

// Joins the pieces a removal split off back into the grid they came from with a
// live merge. The pieces' block entities are the same objects the split moved, so
// ids, toolbars, groups and controller references all survive.
public class MergeBackOp : Op
{
    public int Grid;
    public List<SplitPiece> Pieces = new List<SplitPiece>();

    public override IEnumerable<int> GridHandles() => Pieces.Select(p => p.Grid).Append(Grid);

    public override string Validate(GridRegistry grids)
    {
        if (grids.ResolveGrid(Grid) == null)
            return GameAccess.GridMissing;

        return Pieces.All(p => grids.ResolveGrid(p.Grid) != null)
            ? null
            : "a split off part no longer exists";
    }

    public override Func<bool> Apply(GridRegistry grids)
    {
        var grid = grids.ResolveGrid(Grid);
        foreach (var piece in Pieces.Select(p => grids.ResolveGrid(p.Grid)))
        {
            // A split starts with the transform of its original grid. A dynamic piece
            // may have drifted since, so it is put back there before merging.
            if (piece.Physics != null)
            {
                piece.Physics.LinearVelocity = Vector3.Zero;
                piece.Physics.AngularVelocity = Vector3.Zero;
            }
            piece.WorldMatrix = grid.WorldMatrix;
            grid.MergeGrid_MergeBlock(piece, Vector3I.Zero, checkMergeOrder: false);
        }
        return null;
    }
}

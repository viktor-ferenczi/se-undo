using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.Apply;
using ClientPlugin.GridStore;
using ClientPlugin.History;
using ClientPlugin.Session;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Multiplayer;
using VRage;
using VRage.Game;
using VRageMath;

namespace ClientPlugin.Ops;

// Joins the pieces a removal split off back into the grid they came from.
// Server: live merge, the pieces' block entities are the same objects the split
// moved, so ids, toolbars, groups and controller references all survive.
// Client: close the pieces and paste their captured builders into the grid; ids change.
public class MergeBackOp : Op
{
    public int Grid;
    public List<SplitPiece> Pieces = new List<SplitPiece>();

    public override IEnumerable<int> GridHandles() => Pieces.Select(p => p.Grid).Append(Grid);

    public override IEnumerable<string> StoreRefs() =>
        Pieces.Select(p => p.Entry).Where(id => id != null);

    public override string Validate(GridRegistry grids)
    {
        if (grids.ResolveGrid(Grid) == null)
            return GameAccess.GridMissing;

        // Joining grids for free is nothing a regular survival player can do
        if (!Permissions.Creative)
            return Permissions.NeedsCreativeTools;

        if (!Sync.IsServer)
            return Pieces.All(p => UndoSession.Store.Has(p.Entry)) ? null : PasteGridsOp.BackupGone;

        return Pieces.All(p => grids.ResolveGrid(p.Grid) != null)
            ? null
            : "a split off part no longer exists";
    }

    public override Func<bool> Apply(GridRegistry grids)
    {
        var grid = grids.ResolveGrid(Grid);
        if (Sync.IsServer)
        {
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

        // The pieces come back as new blocks; links across their boundary are gone
        Executor.Remark(Permissions.LinksLost);
        foreach (var split in Pieces)
        {
            var piece = grids.ResolveGrid(split.Grid);
            if (piece != null)
                MyMultiplayer.RaiseEvent(piece, x => x.OnGridClosedRequest);

            var builder = StoredGroups.Load(split.Entry)[0];
            builder.PositionAndOrientation = new MyPositionAndOrientation(grid.WorldMatrix);
            grid.PasteBlocksToGrid(
                new List<MyObjectBuilder_CubeGrid> { builder },
                GameAccess.LocalCharacterId,
                instantBuild: true
            );
        }
        return () => Pieces.All(p => grid.GetCubeBlock(p.Key) != null);
    }
}

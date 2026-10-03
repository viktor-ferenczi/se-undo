using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.Apply;
using ClientPlugin.History;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using VRage.Utils;
using VRageMath;

namespace ClientPlugin.Ops;

// Places blocks through the cube builder's own request
public class BuildBlocksOp : Op
{
    public int Grid;
    public Vector3 ColorHsv;
    public string Skin;
    public List<BlockPlacement> Blocks = new List<BlockPlacement>();

    // The blocks as they were when the placement was last undone, saved by that undo
    // (RazeBlocksOp.Prepare): what the player set on them since they were placed, and
    // what they held. Redo puts these back instead of new blocks from the definition.
    // Snapshot stands in for Restore where a block is one the single block restore
    // cannot handle, a rotor base for one; it holds the whole grid group.
    public RestoreBlocksOp Restore;
    public GroupSnapshotOp Snapshot;

    public override IEnumerable<int> GridHandles() => new[] { Grid };

    public override IEnumerable<string> StoreRefs() =>
        Snapshot == null ? Enumerable.Empty<string>() : Snapshot.StoreRefs();

    // The saved state, unless the full state option is off; then the blocks are
    // built from their definition
    private Op Saved =>
        !RestoreBlocksOp.FullState ? null
        : Snapshot != null ? Snapshot
        : Restore;

    public override string Validate(GridRegistry grids)
    {
        var grid = grids.ResolveGrid(Grid);
        if (grid == null)
            return GameAccess.GridMissing;
        return Saved?.Validate(grids);
    }

    public override Func<bool> Apply(GridRegistry grids)
    {
        if (Saved != null)
            return Saved.Apply(grids);

        var grid = grids.ResolveGrid(Grid);
        if (Blocks.Any(b => Blocked(grid, b)))
            throw new OpRefusedException(Permissions.InTheWay);

        // The local server builds inside the request
        Build(grid, Blocks, ColorHsv, Skin);
        var missing = Blocks.Count(b => grid.BlockAt(b.Min) == null);
        if (missing != 0)
            Executor.Remark($"{Record.Recorder.Plural(missing, "block")} could not be placed");
        return null;
    }

    // The build request returns without a word when something is in the way
    private static bool Blocked(MyCubeGrid grid, BlockPlacement block)
    {
        var location = block.ToLocation(GameAccess.LocalIdentityId);
        MyDefinitionManager.Static.TryGetCubeBlockDefinition(
            location.BlockDefinition,
            out var definition
        );
        return definition == null
            || !grid.CanPlaceBlock(location.Min, location.Max, location.Orientation, definition);
    }

    public static HashSet<MyCubeGrid.MyBlockLocation> Locations(
        IEnumerable<BlockPlacement> blocks
    ) =>
        new HashSet<MyCubeGrid.MyBlockLocation>(
            blocks.Select(b => b.ToLocation(GameAccess.LocalIdentityId))
        );

    // The cube builder's request
    public static void Build(
        MyCubeGrid grid,
        IEnumerable<BlockPlacement> blocks,
        Vector3 colorHsv,
        string skin
    )
    {
        // The request checks limits and DLC by the first location's definition
        foreach (var group in blocks.GroupBy(b => b.Definition))
            grid.BuildBlocks(
                colorHsv,
                MyStringHash.GetOrCompute(skin),
                Locations(group),
                GameAccess.LocalCharacterId,
                GameAccess.LocalIdentityId
            );
    }
}

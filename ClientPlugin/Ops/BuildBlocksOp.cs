using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.Apply;
using ClientPlugin.History;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using VRage.Utils;
using VRageMath;

namespace ClientPlugin.Ops;

// Places blocks through the cube builder's own request. In survival without
// creative tools they come back as construction sites, which a player could do by hand.
public class BuildBlocksOp : Op
{
    public int Grid;
    public Vector3 ColorHsv;
    public string Skin;
    public List<BlockPlacement> Blocks = new List<BlockPlacement>();

    public override IEnumerable<int> GridHandles() => new[] { Grid };

    public override string Validate(GridRegistry grids)
    {
        var grid = grids.ResolveGrid(Grid);
        if (grid == null)
            return GameAccess.GridMissing;
        return Permissions.HasComponentsFor(grid, Locations(Blocks))
            ? null
            : Permissions.MissingComponents;
    }

    public override Func<bool> Apply(GridRegistry grids)
    {
        var grid = grids.ResolveGrid(Grid);
        Build(grid, Blocks, ColorHsv, Skin);

        if (Sync.IsServer)
            return null;
        return () => Blocks.All(b => grid.BlockAt(b.Min) != null);
    }

    public static HashSet<MyCubeGrid.MyBlockLocation> Locations(
        IEnumerable<BlockPlacement> blocks
    ) =>
        new HashSet<MyCubeGrid.MyBlockLocation>(
            blocks.Select(b => b.ToLocation(GameAccess.LocalIdentityId))
        );

    // The cube builder's request. Without creative tools in survival the server
    // builds construction sites and takes the components from the character.
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

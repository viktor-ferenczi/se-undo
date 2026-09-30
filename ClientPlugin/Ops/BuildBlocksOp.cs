using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.History;
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

    public override string Validate(GridRegistry grids) =>
        grids.ResolveGrid(Grid) == null ? GameAccess.GridMissing : null;

    public override Func<bool> Apply(GridRegistry grids)
    {
        var grid = grids.ResolveGrid(Grid);
        var owner = GameAccess.LocalIdentityId;

        // The request checks limits and DLC by the first location's definition
        foreach (var group in Blocks.GroupBy(b => b.Definition))
        {
            var locations = new HashSet<Sandbox.Game.Entities.MyCubeGrid.MyBlockLocation>(
                group.Select(b => b.ToLocation(owner))
            );
            grid.BuildBlocks(
                ColorHsv,
                MyStringHash.GetOrCompute(Skin),
                locations,
                GameAccess.LocalCharacterId,
                owner
            );
        }

        if (Sync.IsServer)
            return null;
        return () => Blocks.All(b => grid.BlockAt(b.Min) != null);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.Apply;
using ClientPlugin.History;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace ClientPlugin.Ops;

// Puts removed blocks back. With creative rights and the full state option on:
// server, one BuildBlockRequestInternal per block with the saved builder and entity
// id, then the block links; client, one paste into the grid, which keeps settings but
// assigns new ids. Otherwise the blocks are rebuilt from their definition.
public class RestoreBlocksOp : Op
{
    public int Grid;

    // MyObjectBuilder_CubeGrid holding the removed blocks at their grid positions
    public string BlocksXml;

    public List<BlockLinks> Links = new List<BlockLinks>();

    public override string Validate(GridRegistry grids) =>
        grids.ResolveGrid(Grid) == null ? GameAccess.GridMissing : null;

    public override Func<bool> Apply(GridRegistry grids)
    {
        var grid = grids.ResolveGrid(Grid);
        var saved = BuilderXml.Read<MyObjectBuilder_CubeGrid>(BlocksXml);
        var mins = saved.CubeBlocks.Select(b => (Vector3I)b.Min).ToList();
        Func<bool> allBack = () => mins.All(min => grid.BlockAt(min) != null);

        if (Config.Current.RestoreRemovedBlocksWithFullState && Permissions.HasCreativeRights)
        {
            if (Sync.IsServer)
            {
                RestoreOnServer(grid, saved.CubeBlocks);
                BlockLinks.Reapply(grid, Links);
                return null;
            }

            saved.PositionAndOrientation = new MyPositionAndOrientation(grid.WorldMatrix);
            grid.PasteBlocksToGrid(
                new List<MyObjectBuilder_CubeGrid> { saved },
                GameAccess.LocalCharacterId,
                instantBuild: true
            );
            if (Links.Count != 0)
                Notify.Show("Restored on a server: some block links are lost");
            return allBack;
        }

        foreach (var group in saved.CubeBlocks.GroupBy(b => (b.ColorMaskHSV, b.SkinSubtypeId)))
        {
            var owner = GameAccess.LocalIdentityId;
            var locations = new HashSet<MyCubeGrid.MyBlockLocation>(
                group.Select(b => Placement(b).ToLocation(owner))
            );
            grid.BuildBlocks(
                group.Key.ColorMaskHSV,
                MyStringHash.GetOrCompute(group.Key.SkinSubtypeId),
                locations,
                GameAccess.LocalCharacterId,
                owner
            );
        }
        if (!MySession.Static.CreativeMode && !Permissions.HasCreativeRights)
            Notify.Show("Removed blocks restored as construction sites");
        return Sync.IsServer ? null : allBack;
    }

    // Blocks that connect only through other removed blocks fail until those are
    // back, so this retries until no more progress is made
    private static void RestoreOnServer(MyCubeGrid grid, List<MyObjectBuilder_CubeBlock> blocks)
    {
        var pending = blocks.ToList();
        var progress = true;
        while (pending.Count != 0 && progress)
        {
            progress = false;
            foreach (var builder in pending.ToList())
            {
                var location = Placement(builder).ToLocation(builder.Owner);
                builder.EntityId = location.EntityId;
                var visuals = new MyCubeGrid.MyBlockVisuals(
                    ((Vector3)builder.ColorMaskHSV).PackHSVToUint(),
                    MyStringHash.GetOrCompute(builder.SkinSubtypeId)
                );
                grid.BuildBlockRequestInternal(
                    visuals,
                    location,
                    builder,
                    GameAccess.LocalCharacterId,
                    instantBuild: true,
                    builder.Owner,
                    Sync.MyId
                );

                var block = grid.BlockAt(builder.Min);
                if (block == null)
                    continue;
                pending.Remove(builder);
                progress = true;

                // The build request always shares with the faction
                if (
                    block.FatBlock != null
                    && builder.Owner != 0
                    && builder.ShareMode != MyOwnershipShareModeEnum.Faction
                )
                    grid.ChangeOwnerRequest(grid, block.FatBlock, builder.Owner, builder.ShareMode);
            }
        }

        if (pending.Count != 0)
            Log.Warning($"{pending.Count} removed blocks could not be restored");
    }

    private static BlockPlacement Placement(MyObjectBuilder_CubeBlock builder) =>
        new BlockPlacement
        {
            Definition = builder.GetId().ToString(),
            Min = builder.Min,
            Forward = builder.BlockOrientation.Forward,
            Up = builder.BlockOrientation.Up,
            EntityId = builder.EntityId,
        };
}

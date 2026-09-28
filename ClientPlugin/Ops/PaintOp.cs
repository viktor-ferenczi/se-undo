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

public class BlockPaint
{
    public Vector3I Min;
    public Vector3 ColorHsv;
    public string Skin;
}

// Sets color and skin per block through the paint request, one request per run of
// adjacent blocks along X that get the same paint
public class PaintOp : Op
{
    public int Grid;
    public bool ApplyColor;
    public bool ApplySkin;
    public List<BlockPaint> Blocks = new List<BlockPaint>();

    public override string Validate(GridRegistry grids)
    {
        var grid = grids.ResolveGrid(Grid);
        if (grid == null)
            return GameAccess.GridMissing;
        return Permissions.CanPaint(grid) ? null : Permissions.NotYourGrid;
    }

    public override Func<bool> Apply(GridRegistry grids)
    {
        var grid = grids.ResolveGrid(Grid);
        foreach (var group in Blocks.GroupBy(b => (b.ColorHsv, b.Skin)))
        {
            Vector3? color = ApplyColor ? group.Key.ColorHsv : null;
            MyStringHash? skin = ApplySkin ? MyStringHash.GetOrCompute(group.Key.Skin) : null;
            foreach (var (min, max) in RunsAlongX(group.Select(b => b.Min)))
                grid.SkinBlocks(min, max, color, skin, playSound: false);
        }

        if (Sync.IsServer)
            return null;
        return () => Blocks.All(b => IsPainted(grid, b));
    }

    private bool IsPainted(MyCubeGrid grid, BlockPaint paint)
    {
        var block = grid.GetCubeBlock(paint.Min);
        return block == null
            || (!ApplyColor || block.ColorMaskHSV == paint.ColorHsv)
                && (!ApplySkin || block.SkinSubtypeId.String == paint.Skin);
    }

    // Only block min corners are merged, so a run never touches a block outside the group
    private static IEnumerable<(Vector3I, Vector3I)> RunsAlongX(IEnumerable<Vector3I> mins)
    {
        var sorted = mins.Distinct().OrderBy(p => p.Z).ThenBy(p => p.Y).ThenBy(p => p.X).ToList();
        var i = 0;
        while (i < sorted.Count)
        {
            var start = sorted[i];
            var end = start;
            while (
                ++i < sorted.Count
                && sorted[i].Y == end.Y
                && sorted[i].Z == end.Z
                && sorted[i].X == end.X + 1
            )
                end = sorted[i];
            yield return (start, end);
        }
    }
}

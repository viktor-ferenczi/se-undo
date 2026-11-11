using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Shared.Apply;
using Shared.History;
using Shared.Session;
using VRage.Utils;
using VRageMath;

namespace Shared.Ops;

public class BlockPaint
{
    public Vector3I Min;
    public Vector3 ColorHsv;
    public string Skin;
}

// Sets color and skin per block through the paint request, one request per run of
// adjacent blocks along X that get the same paint, within the frame budget of
// PaintBudget
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

    // Requests left to send by a replay spread over frames, see PaintBudget
    private Queue<(Vector3I Min, Vector3I Max, MyCubeGrid.MyBlockVisuals Visuals)> queue;

    public override Func<bool> Apply(GridRegistry grids)
    {
        var grid = grids.ResolveGrid(Grid);
        var actor = Actor.Current;
        queue = new Queue<(Vector3I, Vector3I, MyCubeGrid.MyBlockVisuals)>();
        foreach (var group in Blocks.GroupBy(b => (b.ColorHsv, b.Skin)))
        {
            // What SkinBlocks sends, run as the player's request: the server checks
            // the skin and the grid against the player, not against itself
            var visuals = new MyCubeGrid.MyBlockVisuals(
                group.Key.ColorHsv.PackHSVToUint(),
                ApplySkin ? MyStringHash.GetOrCompute(group.Key.Skin) : MyStringHash.NullOrEmpty,
                ApplyColor,
                ApplySkin
            );
            foreach (var (min, max) in RunsAlongX(group.Select(b => b.Min)))
                queue.Enqueue((min, max, visuals));
        }

        Send(grid, actor);
        if (queue.Count == 0)
            return null;

        // The rest in the next frames, as replays like the first part
        return () =>
        {
            if (queue.Count != 0 && !grid.Closed)
            {
                using (Replay.Begin())
                    Send(grid, actor);
            }
            return queue.Count == 0 || grid.Closed;
        };
    }

    // Messages a request of this op's grid costs, see PaintBudget
    private int cost;

    // How long the rest of the replay may take on top of the pending timeout: the
    // frames the requests left need, at 10 frames a second
    public override TimeSpan PendingTime()
    {
        var left = queue?.Count ?? 0;
        var frames = Math.Max(
            Math.Ceiling(left / (double)PaintBudget.RequestsPerFrame),
            Math.Ceiling(left * (double)cost / PaintBudget.MessagesPerFrame)
        );
        return TimeSpan.FromSeconds(frames / 10);
    }

    private void Send(MyCubeGrid grid, Actor actor)
    {
        cost = 1 + (grid.GridSystems.EmissiveSystem?.EmissiveBlocks?.Count ?? 0);
        while (queue.Count != 0 && PaintBudget.TryTake(cost))
        {
            var (min, max, visuals) = queue.Dequeue();
            GameAccess.AsRequestOf(
                actor,
                () => grid.SkinBlockRequest(min, max, visuals, playSound: false)
            );
        }
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

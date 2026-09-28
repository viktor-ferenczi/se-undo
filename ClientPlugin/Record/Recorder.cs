using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.History;
using ClientPlugin.Ops;
using ClientPlugin.Session;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using VRage.Utils;
using VRageMath;

namespace ClientPlugin.Record;

// Turns what the record patches see into history nodes. Never calls game mutation
// methods; the patches check CanRecord first, which is false while ops are replayed.
public static class Recorder
{
    private static readonly List<RazeCapture> settling = new List<RazeCapture>();
    private static PaintStroke stroke;

    public static bool CanRecord =>
        UndoSession.Document != null && !Replay.Active && Config.Current.EnableBuildContext;

    // A removal waits a frame or two for grid splits before it becomes a node
    public static bool IsBusy => settling.Count != 0;

    public static void Reset()
    {
        foreach (var capture in settling)
            capture.Watch.Close();
        settling.Clear();
        stroke = null;
    }

    public static void Update()
    {
        if (stroke != null && stroke.Expired)
            Flush();

        foreach (var capture in settling.Where(c => c.Watch.Settled).ToList())
        {
            settling.Remove(capture);
            try
            {
                capture.Finish();
            }
            catch (Exception e)
            {
                Log.Error($"Recording a block removal failed, it cannot be undone: {e}");
            }
        }
    }

    // Commits the paint stroke being coalesced, if any
    public static void Flush()
    {
        var finished = stroke;
        stroke = null;
        finished?.Commit();
    }

    public static void Commit(string label, List<Op> forward, List<Op> reverse)
    {
        UndoSession.Document.Build.Record(label, forward, reverse, DateTime.UtcNow);
        Log.Debug($"Recorded: {label}");
        UndoSession.Changed();
    }

    public static int Handle(MyCubeGrid grid) => UndoSession.Document.Grids.GetOrAdd(grid.EntityId);

    public static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    // Blocks the player asked for. Where the server is local the request already ran,
    // so only blocks that exist now count; a client records what it asked for.
    public static void RecordBuild(
        MyCubeGrid grid,
        Vector3 colorHsv,
        MyStringHash skin,
        List<BlockPlacement> requested
    )
    {
        var built = requested;
        if (Sync.IsServer)
        {
            built = new List<BlockPlacement>();
            foreach (var placement in requested)
            {
                var block = grid.BlockAt(placement.Min);
                if (block == null || block.BlockDefinition.Id.ToString() != placement.Definition)
                    continue;
                placement.EntityId = block.FatBlock?.EntityId ?? 0;
                built.Add(placement);
            }
        }
        if (built.Count == 0)
            return;

        var handle = Handle(grid);
        Commit(
            $"placed {Plural(built.Count, "block")}",
            new List<Op>
            {
                new BuildBlocksOp
                {
                    Grid = handle,
                    ColorHsv = colorHsv,
                    Skin = skin.String,
                    Blocks = built,
                },
            },
            new List<Op>
            {
                new RazeBlocksOp { Grid = handle, Positions = built.Select(b => b.Min).ToList() },
            }
        );
    }

    public static void BeginRaze(MyCubeGrid grid, IEnumerable<Vector3I> positions)
    {
        var capture = RazeCapture.Begin(grid, positions);
        if (capture != null)
            settling.Add(capture);
    }

    public static void OpenStroke(MyCubeGrid grid)
    {
        if (stroke != null && stroke.Grid != grid)
            Flush();
        stroke ??= new PaintStroke(grid);
        stroke.Touch();
    }

    // The stroke a color or skin change of this grid belongs to, if one is open
    public static PaintStroke StrokeFor(MyCubeGrid grid) =>
        !Replay.Active && stroke?.Grid == grid ? stroke : null;
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ClientPlugin.GridStore;
using ClientPlugin.History;
using ClientPlugin.Ops;
using ClientPlugin.Session;
using ClientPlugin.Storage;
using Sandbox;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Gui;
using Sandbox.Game.Multiplayer;
using VRage.Utils;
using VRageMath;

namespace ClientPlugin.Record;

// Turns what the record patches see into history nodes. Never calls game mutation
// methods; the patches check CanRecord first, which is false while ops are replayed.
public static class Recorder
{
    private static readonly List<ICapture> settling = new List<ICapture>();
    private static PaintStroke stroke;
    private static TerminalStroke terminalStroke;

    // Grids a client asked to paste blocks into, until the server's broadcast arrives
    private static readonly Dictionary<MyCubeGrid, DateTime> expectedMerges =
        new Dictionary<MyCubeGrid, DateTime>();

    public static bool CanRecord =>
        UndoSession.Document != null && !Replay.Active && Config.Current.EnableBuildContext;

    // Terminal changes are recorded while the terminal is open, or always with the
    // option on. Main thread only: mods may set properties from worker threads.
    public static bool CanRecordTerminal =>
        UndoSession.Document != null
        && !Replay.Active
        && Config.Current.EnableTerminalContext
        && (Config.Current.RecordTerminalChangesOutsideTerminal || MyGuiScreenTerminal.IsOpen)
        && Thread.CurrentThread == MySandboxGame.Static.UpdateThread;

    // A removal waits a frame or two for grid splits, a paste for its grids, before
    // it becomes a node; so does a backup over the budget for the player's answer
    public static bool IsBusy => settling.Count != 0 || held != 0;

    private static int held;

    public static void Hold() => held++;

    // Not below zero: an answer can arrive after the session and its holds are gone
    public static void Release() => held = Math.Max(0, held - 1);

    public static void Reset()
    {
        foreach (var capture in settling)
            capture.Abort();
        settling.Clear();
        held = 0;
        expectedMerges.Clear();
        stroke = null;
        terminalStroke = null;
    }

    public static void Begin(ICapture capture) => settling.Add(capture);

    public static void Update()
    {
        if (stroke != null && stroke.Expired)
            FlushPaint();
        if (terminalStroke != null && terminalStroke.Expired)
            FlushTerminal();

        foreach (var capture in settling.Where(c => c.Settled).ToList())
        {
            settling.Remove(capture);
            try
            {
                capture.Finish();
            }
            catch (Exception e)
            {
                Log.Error($"Recording a {capture.GetType().Name} failed, it cannot be undone: {e}");
            }
        }

        var now = DateTime.UtcNow;
        foreach (var grid in expectedMerges.Where(e => e.Value < now).Select(e => e.Key).ToList())
            expectedMerges.Remove(grid);
    }

    // Commits the paint stroke and the terminal changes being coalesced, if any
    public static void Flush()
    {
        FlushPaint();
        FlushTerminal();
    }

    private static void FlushPaint()
    {
        var finished = stroke;
        stroke = null;
        finished?.Commit();
    }

    private static void FlushTerminal()
    {
        var finished = terminalStroke;
        terminalStroke = null;
        finished?.Commit();
    }

    public static Node Commit(
        string label,
        List<Op> forward,
        List<Op> reverse,
        UndoHistory history = null
    )
    {
        history ??= UndoSession.Document.Build;
        var node = history.Record(label, forward, reverse, DateTime.UtcNow);
        Log.Debug($"Recorded: {label}");
        UndoSession.Changed();
        return node;
    }

    // In place of an action whose grid backup was dropped, design section 9
    public static void CommitBarrier(string label)
    {
        if (UndoSession.Document == null)
            return;

        UndoSession.Document.Build.RecordBarrier(label, DateTime.UtcNow);
        Log.Debug($"Recorded a barrier: {label}");
        UndoSession.Changed();
    }

    // Main grid name, plus how many grids came with it
    public static string Describe(StoreRow row) =>
        row.GridCount == 1
            ? row.MainGridName
            : $"{row.MainGridName} and {Plural(row.GridCount - 1, "more grid")}";

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

    // Grids a paste or a single block placement created. Redo creates them again from
    // the store, undo closes them. The label gets the grid description at {0}.
    public static void RecordCreated(
        List<MyCubeGrid> grids,
        StoreReason reason,
        string label,
        bool referenceLost
    )
    {
        var handles = grids.Select(Handle).ToList();
        StoredGroups.Save(
            StoredGroups.Capture(grids),
            reason,
            row =>
            {
                var node = Commit(
                    string.Format(label, Describe(row)),
                    new List<Op>
                    {
                        new PasteGridsOp { Entry = row.Id, Grids = handles },
                    },
                    new List<Op> { new CloseGridsOp { Grids = handles } }
                );
                if (referenceLost)
                {
                    node.ReferenceLost = true;
                    UndoSession.Changed();
                }
            },
            row => CommitBarrier(string.Format(label, Describe(row)))
        );
    }

    public static void ExpectMerge(MyCubeGrid grid) =>
        expectedMerges[grid] = DateTime.UtcNow.AddSeconds(Config.Current.PendingOperationTimeoutS);

    public static bool TakeMergeExpectation(MyCubeGrid grid) => expectedMerges.Remove(grid);

    // Blocks a paste put into an existing grid: the grid's blocks now, minus the ones
    // it had before. Redo restores them like removed blocks, which keeps their ids
    // on a local server; undo removes them.
    public static void RecordMerge(MyCubeGrid grid, HashSet<MySlimBlock> before)
    {
        var added = grid.CubeBlocks.Where(b => !before.Contains(b)).ToList();
        if (added.Count == 0)
            return;

        var handle = Handle(grid);
        Commit(
            $"pasted {Plural(added.Count, "block")} into {grid.DisplayName}",
            new List<Op>
            {
                new RestoreBlocksOp
                {
                    Grid = handle,
                    BlocksXml = BuilderXml.Write(
                        GameAccess.BlocksBuilder(
                            grid,
                            added.Select(b => b.GetObjectBuilder()).ToList()
                        )
                    ),
                },
            },
            new List<Op>
            {
                new RazeBlocksOp { Grid = handle, Positions = added.Select(b => b.Min).ToList() },
            }
        );
    }

    // One change of a terminal value. Changes of the same kind coalesce into one node,
    // see TerminalStroke.
    private static void RecordTerminal(
        string kind,
        string target,
        string oldValue,
        string newValue,
        Func<string, Op> makeOp,
        Func<int, string, string, string> label
    )
    {
        if (terminalStroke != null && terminalStroke.Kind != kind)
            FlushTerminal();
        terminalStroke ??= new TerminalStroke(kind);
        terminalStroke.Add(target, oldValue, newValue, makeOp, label);
    }

    public static void RecordProperty(
        MyTerminalBlock block,
        string controlId,
        string oldValue,
        string newValue
    )
    {
        var blockRef = BlockRef.From(block, UndoSession.Document.Grids);
        var name = block.DisplayNameText;
        RecordTerminal(
            $"property {controlId}",
            blockRef.Key,
            oldValue,
            newValue,
            value => new SetPropertyOp
            {
                Block = blockRef,
                Control = controlId,
                Value = value,
            },
            (count, old, changed) =>
                count != 1 ? $"changed {controlId} of {Plural(count, "block")}"
                : controlId == "Name" ? $"renamed block {old} to {changed}"
                : $"changed {controlId} of {name}"
        );
    }

    public static void RecordGridName(MyCubeGrid grid, string oldName, string newName)
    {
        var handle = Handle(grid);
        RecordTerminal(
            "grid name",
            handle.ToString(),
            oldName,
            newName,
            value => new SetGridNameOp { Grid = handle, Name = value },
            (_, old, changed) => $"renamed grid {old} to {changed}"
        );
    }

    public static void RecordProgram(MyProgrammableBlock block, string oldSource, string newSource)
    {
        var blockRef = BlockRef.From(block, UndoSession.Document.Grids);
        var name = block.DisplayNameText;
        RecordTerminal(
            "program",
            blockRef.Key,
            oldSource,
            newSource,
            value => new SetProgramOp { Block = blockRef, Source = Gz.Compress(value) },
            (_, _, _) => $"changed the program of {name}"
        );
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

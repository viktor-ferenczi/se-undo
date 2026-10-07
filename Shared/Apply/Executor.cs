using System;
using System.Collections.Generic;
using System.Linq;
using Shared.History;
using Shared.Ops;
using Shared.Record;
using Shared.Session;

namespace Shared.Apply;

// Replays nodes on undo and redo, design section 10. Runs on the main thread from
// the key handlers. Never records: the replay flag is set while ops are applied.
public static class Executor
{
    public static void Undo(UndoHistory history, GridRegistry grids) => Step(history, grids, true);

    public static void Redo(UndoHistory history, GridRegistry grids) => Step(history, grids, false);

    // No step to take back and none on its way: a stroke still being coalesced is
    // committed first, and a recording or replay in progress counts as something
    public static bool NothingToUndo(UndoHistory history)
    {
        Recorder.Flush();
        return !history.IsLocked && !Recorder.IsBusy && history.UndoTarget == null;
    }

    // What an op has to say about its result, shown with the step's notification:
    // "restored with changes", "2 blocks could not be placed" and the like
    private static readonly List<string> remarks = new List<string>();

    public static void Remark(string text)
    {
        if (!remarks.Contains(text))
            remarks.Add(text);
    }

    private static string TakeRemarks()
    {
        var text = remarks.Count == 0 ? "" : $" ({string.Join(", ", remarks)})";
        remarks.Clear();
        return text;
    }

    private static void Step(UndoHistory history, GridRegistry grids, bool undo)
    {
        var verb = undo ? "Undo" : "Redo";

        // A paint stroke still being coalesced becomes its own node first
        Recorder.Flush();

        if (history.IsLocked || Recorder.IsBusy)
        {
            Notify.Show($"{verb} not available: the last operation is still in progress");
            return;
        }

        var node = undo ? history.UndoTarget : history.RedoTarget;
        if (node == null)
        {
            Notify.Show(undo ? "Nothing to undo" : "Nothing to redo");
            return;
        }

        if (node.Barrier)
        {
            Notify.Show($"{verb} not available: backup was too large for the budget");
            return;
        }

        // A replay that ended with an unknown result is not repeated
        if (node.UnknownResult)
        {
            Notify.Show($"{verb} not available: the result of {node.Label} is unknown");
            return;
        }

        var ops = undo ? Enumerable.Reverse(node.Reverse).ToList() : node.Forward;
        foreach (var op in ops)
        {
            var reason = op.Validate(grids);
            if (reason != null)
            {
                Notify.Show($"{verb} not available: {reason}");
                return;
            }
        }

        var checks = new List<Func<bool>>();
        remarks.Clear();
        try
        {
            var opposite = undo ? node.Forward : node.Reverse;
            foreach (var op in ops)
                op.Prepare(grids, opposite);

            using (Replay.Begin())
            {
                foreach (var op in ops)
                {
                    var check = op.Apply(grids);
                    if (check != null)
                        checks.Add(check);
                }
            }
        }
        catch (OpRefusedException e)
        {
            remarks.Clear();
            Notify.Show($"{verb} not available: {e.Message}");
            return;
        }
        catch (Exception e)
        {
            Log.Error($"{verb} of {node.Label} failed: {e}");
            node.UnknownResult = true;
            Notify.Show($"{verb} failed: {node.Label}");
            return;
        }

        if (undo)
            history.MarkUndone(node);
        else
            history.MarkRedone(node);

        node.UnknownResult = false;
        // Prepare may have stored a backup the other direction reads
        foreach (var id in node.Forward.Concat(node.Reverse).SelectMany(op => op.StoreRefs()))
        {
            if (!node.StoreRefs.Contains(id))
                node.StoreRefs.Add(id);
        }
        if (checks.Count != 0)
        {
            history.Pending = new PendingOp
            {
                Node = node,
                IsUndo = undo,
                IsDone = () => checks.All(check => check()),
                DeadlineUtc = DateTime.UtcNow.AddSeconds(Options.Current.PendingOperationTimeoutS),
            };
        }

        Notify.Show($"{verb}: {node.Label}{TakeRemarks()}");
    }

    public static void Update(UndoHistory history)
    {
        var finished = history.PollPending(DateTime.UtcNow);
        if (finished == null)
            return;

        var node = finished.Node;
        var verb = finished.IsUndo ? "Undo" : "Redo";
        if (!finished.TimedOut)
        {
            // What the op learned while it completed
            if (remarks.Count != 0)
                Notify.Show($"{verb}: {node.Label}{TakeRemarks()}");
            Actor.Current.Changed();
            return;
        }
        remarks.Clear();
        Notify.Show($"{verb} of {node.Label}: result unknown");
    }

    // A failure notification of the server; only matters while a replay is pending
    public static void OnServerFailure()
    {
        var document = Actors.Local?.Document;
        document?.Build.FailPending();
        document?.Terminal.FailPending();
    }
}

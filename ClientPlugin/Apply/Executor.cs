using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.History;
using ClientPlugin.Record;

namespace ClientPlugin.Apply;

// Replays nodes on undo and redo, design section 10. Runs on the main thread from
// the key handlers. Never records: the replay flag is set while ops are applied.
public static class Executor
{
    public static void Undo(UndoHistory history, GridRegistry grids) => Step(history, grids, true);

    public static void Redo(UndoHistory history, GridRegistry grids) => Step(history, grids, false);

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
        try
        {
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

        if (checks.Count != 0)
        {
            history.Pending = new PendingOp
            {
                Node = node,
                IsUndo = undo,
                IsDone = () => checks.All(check => check()),
                DeadlineUtc = DateTime.UtcNow.AddSeconds(Config.Current.PendingOperationTimeoutS),
            };
        }

        Notify.Show($"{verb}: {node.Label}");
    }

    public static void Update(UndoHistory history)
    {
        var finished = history.PollPending(DateTime.UtcNow);
        if (finished == null)
            return;

        if (finished.TimedOut)
            Notify.Show(
                $"{(finished.IsUndo ? "Undo" : "Redo")} of {finished.Node.Label}: result unknown"
            );
        else
            Session.UndoSession.Changed();
    }
}

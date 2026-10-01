using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.History;
using ClientPlugin.Ops;
using ClientPlugin.Record;
using Sandbox.Game.Multiplayer;

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

        if (node.Barrier)
        {
            Notify.Show($"{verb} not available: backup was too large for the budget");
            return;
        }

        if (node.ReferenceLost)
        {
            Notify.Show($"{verb} not available: the grids of {node.Label} could not be identified");
            return;
        }

        // A replay that ended with an unknown result is not repeated. Its snapshot
        // puts the grids back the way they were before it, which is the state this
        // step leads to, since the step goes the other way across the node.
        var viaSnapshot = node.UnknownResult;
        if (viaSnapshot && node.Snapshot == null)
        {
            Notify.Show($"{verb} not available: the result of {node.Label} is unknown");
            return;
        }

        var ops =
            viaSnapshot ? new List<Op> { node.Snapshot }
            : undo ? Enumerable.Reverse(node.Reverse).ToList()
            : node.Forward;
        foreach (var op in ops)
        {
            var reason = op.Validate(grids);
            if (reason != null)
            {
                Notify.Show($"{verb} not available: {reason}");
                return;
            }
        }

        // ponytail: the snapshot is taken before every client replay that touches existing
        // grids, cheap for a ship, costly for a large station
        Func<Op> saveSnapshot = null;
        var checks = new List<Func<bool>>();
        try
        {
            if (!Sync.IsServer && !viaSnapshot)
                saveSnapshot = GroupSnapshotOp.Prepare(ops, grids);

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

        node.UnknownResult = false;
        if (checks.Count != 0)
        {
            history.Pending = new PendingOp
            {
                Node = node,
                IsUndo = undo,
                IsDone = () => checks.All(check => check()),
                DeadlineUtc = DateTime.UtcNow.AddSeconds(Config.Current.PendingOperationTimeoutS),
                ViaSnapshot = viaSnapshot,
                SaveSnapshot = saveSnapshot,
            };
        }
        else if (viaSnapshot)
            DropSnapshot(node);

        Notify.Show($"{verb}: {node.Label}");
    }

    public static void Update(UndoHistory history)
    {
        var finished = history.PollPending(DateTime.UtcNow);
        if (finished == null)
            return;

        var node = finished.Node;
        if (!finished.TimedOut)
        {
            if (finished.ViaSnapshot)
                DropSnapshot(node);
            Session.UndoSession.Changed();
            return;
        }

        // A failed snapshot restore keeps its snapshot for the next try
        if (finished.SaveSnapshot != null)
        {
            try
            {
                node.Snapshot = finished.SaveSnapshot();
                node.StoreRefs.AddRange(node.Snapshot.StoreRefs());
            }
            catch (Exception e)
            {
                Log.Error($"Saving the group snapshot of {node.Label} failed: {e}");
            }
        }
        Notify.Show($"{(finished.IsUndo ? "Undo" : "Redo")} of {node.Label}: result unknown");
    }

    private static void DropSnapshot(Node node)
    {
        foreach (var id in node.Snapshot.StoreRefs())
            node.StoreRefs.Remove(id);
        node.Snapshot = null;
    }

    // A failure notification of the server; only matters while a replay is pending
    public static void OnServerFailure()
    {
        var document = Session.UndoSession.Document;
        document?.Build.FailPending();
        document?.Terminal.FailPending();
    }
}

using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Shared.Apply;
using Shared.History;
using Shared.Ops;
using Shared.Record;
using Shared.Session;

namespace Shared.GridStore;

// Puts a backed up group back where it was, in place of the grids of it that exist
// now, design section 9. One step of the build history: undo puts the grids back as
// they were before the restore, from a backup taken right before it.
public static class GridRestore
{
    private const string Refused = "Restore not available: ";

    public static void Restore(string id)
    {
        var actor = Actor.Current;
        if (!Recorder.CanRecord)
        {
            actor.Notify(Refused + "needs creative tools and the build history");
            return;
        }

        var row = actor.Store.Index.Rows.LastOrDefault(r => r.Id == id);
        var builders = row == null ? null : StoredGroups.Load(id);
        if (builders == null)
        {
            actor.Notify(Refused + PasteGridsOp.BackupGone);
            return;
        }

        Recorder.Flush();
        var history = actor.Document.Build;
        if (history.IsLocked || Recorder.IsBusy)
        {
            actor.Notify(Refused + "the last operation is still in progress");
            return;
        }

        // The grids of the backup that exist now, with whatever joined their group
        var current = builders
            .Select(b => MyEntities.GetEntityById(b.EntityId) as MyCubeGrid)
            .Where(g => g != null && !g.MarkedForClose)
            .SelectMany(StoredGroups.GroupOf)
            .Distinct()
            .ToList();

        var grids = actor.Document.Grids;
        var stored = builders.Select(b => grids.GetOrAdd(b.EntityId)).ToList();
        var now = current.Select(g => grids.GetOrAdd(g.EntityId)).ToList();
        var forward = new GroupSnapshotOp
        {
            Entry = id,
            Grids = stored,
            Closing = now.Except(stored).ToList(),
        };
        var refusal = forward.Validate(grids);
        if (refusal != null)
        {
            actor.Notify(Refused + refusal);
            return;
        }

        var label =
            $"restored {Recorder.Describe(row)} from {row.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        if (current.Count == 0)
        {
            Do(label, forward, new CloseGridsOp { Grids = stored });
            return;
        }

        StoredGroups.Save(
            StoredGroups.Capture(current),
            StoreReason.Snapshot,
            saved =>
                Do(
                    label,
                    forward,
                    new GroupSnapshotOp
                    {
                        Entry = saved.Id,
                        Grids = now,
                        Closing = stored.Except(now).ToList(),
                    }
                ),
            _ =>
                actor.Notify(Refused + "the backup of the grids as they are now is over the budget")
        );
    }

    // Records the step as undone, then redoes it, so it is applied, checked and
    // reported like any other step
    private static void Do(string label, Op forward, Op reverse)
    {
        var actor = Actor.Current;
        var history = actor.Document.Build;
        var node = Recorder.Commit(label, new List<Op> { forward }, new List<Op> { reverse });
        history.MarkUndone(node);
        Executor.Redo(history, actor.Document.Grids, "Grid history");
    }
}

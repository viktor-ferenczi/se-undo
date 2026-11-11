using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Sandbox;
using Sandbox.Game.Entities;
using Shared.Record;
using Shared.Session;

namespace Shared.GridStore;

// A backup of the whole group before the player edits a grid, design section 9:
// on the first edit of the grid and on the first after a pause longer than the
// interval. Every grid the player touches then has a recent backup in the grid
// history, for damage undo cannot take back. Only the builders are taken on the
// game thread; the blueprint XML, its hash and the file are made in the background.
public static class GridSnapshots
{
    // Before the current actor edits the grid; the caller checked CanRecord
    public static void BeforeEdit(MyCubeGrid grid)
    {
        try
        {
            var actor = Actor.Current;
            var config = Options.Current;
            if (
                grid == null
                || grid.MarkedForClose
                || config.GridSnapshotIntervalS <= 0
                || actor?.Document == null
            )
                return;

            var group = StoredGroups.GroupOf(grid);
            var due = actor.Recording.Snapshots.Edit(
                grid.EntityId,
                group.Select(g => g.EntityId),
                DateTime.UtcNow,
                TimeSpan.FromSeconds(config.GridSnapshotIntervalS)
            );
            if (!due)
                return;

            var watch = Stopwatch.StartNew();
            var builders = StoredGroups.Capture(group);
            var row = StoredGroups.RowOf(builders, StoreReason.Edited);
            var captureMs = watch.ElapsedMilliseconds;
            var store = actor.Store;

            Task.Run(() =>
            {
                try
                {
                    var background = Stopwatch.StartNew();
                    store.Stage(StoredGroups.XmlOf(builders), row);
                    var stageMs = background.ElapsedMilliseconds;
                    MySandboxGame.Static.Invoke(
                        () => Commit(actor, store, row, captureMs, stageMs),
                        "UndoGridSnapshot"
                    );
                }
                catch (Exception e)
                {
                    Log.Error($"The snapshot of {row.MainGridName} failed: {e}");
                }
            });
        }
        catch (Exception e)
        {
            // The player's edit goes on without it
            Log.Error($"The snapshot before an edit of {grid?.DisplayName} failed: {e}");
        }
    }

    private static void Commit(
        Actor actor,
        GridStoreFolder store,
        StoreRow row,
        long captureMs,
        long stageMs
    )
    {
        try
        {
            // The session ended meanwhile; its store may be open again in a new one
            if (!Actors.All.Contains(actor) || actor.Store != store)
            {
                store.Discard(row);
                return;
            }

            using (Actor.Use(actor))
            {
                var who = actor.IsLocal ? "" : $" for {actor.SteamId}";
                if (!StoredGroups.CommitQuietly(row))
                {
                    Log.Info(
                        $"The snapshot of {Recorder.Describe(row)}{who} was dropped, it is over the budget"
                    );
                    return;
                }
                var trimmed = store.TrimEdited(
                    row.MainGridEntityId,
                    Options.Current.GridSnapshotsPerGrid
                );
                Log.Info(
                    $"Snapshot of {Recorder.Describe(row)}{who} before an edit, "
                        + $"{Recorder.Plural(row.BlockCount, "block")}, {row.Bytes:N0} bytes: "
                        + $"{captureMs} ms on the game thread, {stageMs} ms in the background"
                        + (
                            trimmed == 0
                                ? ""
                                : $", {Recorder.Plural(trimmed, "older snapshot")} removed"
                        )
                );
                actor.Changed();
            }
        }
        catch (Exception e)
        {
            Log.Error($"Storing the snapshot of {row.MainGridName} failed: {e}");
        }
    }
}

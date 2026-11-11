using System;
using System.IO;
using System.Linq;
using Shared.GridStore;
using Xunit;

namespace UndoTests;

// The snapshots taken before edits: when one is due, and how many a grid keeps
public sealed class SnapshotTests : IDisposable
{
    private static readonly DateTime Start = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly string folder = Path.Combine(
        Path.GetTempPath(),
        "UndoSnapshotTests",
        Guid.NewGuid().ToString("N"),
        "grids"
    );

    public void Dispose()
    {
        var root = Path.GetDirectoryName(folder)!;
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void FirstEditIsDueThenOnlyAfterAPauseLongerThanTheInterval()
    {
        var schedule = new SnapshotSchedule();
        Assert.True(schedule.Edit(1, new long[] { 1 }, Start, Interval));
        Assert.False(schedule.Edit(1, new long[] { 1 }, Start.AddMinutes(10), Interval));
        // 15 minutes since the last edit, not since the snapshot
        Assert.False(schedule.Edit(1, new long[] { 1 }, Start.AddMinutes(24), Interval));
        Assert.False(schedule.Edit(1, new long[] { 1 }, Start.AddMinutes(39), Interval));
        Assert.True(schedule.Edit(1, new long[] { 1 }, Start.AddMinutes(54.1), Interval));
    }

    [Fact]
    public void AnEditCountsForTheWholeGroup()
    {
        var schedule = new SnapshotSchedule();
        Assert.True(schedule.Edit(1, new long[] { 1, 2, 3 }, Start, Interval));
        // A subgrid of the group backed up a minute ago
        Assert.False(schedule.Edit(3, new long[] { 1, 2, 3 }, Start.AddMinutes(1), Interval));
        // A ship that docked since is due on its own first edit
        Assert.True(schedule.Edit(4, new long[] { 1, 2, 3, 4 }, Start.AddMinutes(2), Interval));
    }

    [Fact]
    public void ClearMakesEveryGridDueAgain()
    {
        var schedule = new SnapshotSchedule();
        schedule.Edit(1, new long[] { 1 }, Start, Interval);
        schedule.Clear();
        Assert.True(schedule.Edit(1, new long[] { 1 }, Start.AddSeconds(1), Interval));
    }

    private static StoreRow Row(StoreReason reason, long grid, int minute) =>
        new StoreRow
        {
            TimestampUtc = Start.AddMinutes(minute),
            Reason = reason,
            MainGridName = "Ship",
            MainGridEntityId = grid,
        };

    [Fact]
    public void TrimKeepsTheNewestSnapshotsOfEachGrid()
    {
        var store = new GridStoreFolder(folder);
        for (var minute = 0; minute < 5; minute++)
        {
            store.Add($"<a>{minute}</a>", Row(StoreReason.Edited, 1, minute));
            store.Add($"<b>{minute}</b>", Row(StoreReason.Edited, 2, minute));
        }
        // Other reasons are left to the byte budgets
        store.Add("<deleted/>", Row(StoreReason.Deleted, 1, -10));

        Assert.Equal(2, store.TrimEdited(1, 3));
        Assert.Equal(0, store.TrimEdited(1, 3));

        var kept = store
            .Index.Rows.Where(r => r.MainGridEntityId == 1 && r.Reason == StoreReason.Edited)
            .Select(r => r.TimestampUtc.Minute)
            .OrderBy(m => m)
            .ToArray();
        Assert.Equal(new[] { 2, 3, 4 }, kept);
        Assert.Equal(5, store.Index.Rows.Count(r => r.MainGridEntityId == 2));
        Assert.Contains(store.Index.Rows, r => r.Reason == StoreReason.Deleted);

        // The files of the removed rows are gone, and the index on disk agrees
        Assert.Equal(store.Index.Rows.Count, Directory.GetFiles(folder, "*.sbc.gz").Length);
        Assert.Equal(store.Index.Rows.Count, new GridStoreFolder(folder).Index.Rows.Count);
    }
}

using System;
using System.Collections.Generic;

namespace Shared.GridStore;

// When a grid is due for a snapshot before an edit, design section 9: the first
// edit of a grid in the session, and the first after a pause longer than the
// interval. Knows nothing about the game.
public sealed class SnapshotSchedule
{
    private readonly Dictionary<long, DateTime> lastEdit = new Dictionary<long, DateTime>();

    // True when the grid had no edit within the interval. Marks the grid and the
    // others of its group as edited now either way.
    public bool Edit(long grid, IEnumerable<long> group, DateTime utcNow, TimeSpan interval)
    {
        var due = !lastEdit.TryGetValue(grid, out var last) || utcNow - last > interval;
        lastEdit[grid] = utcNow;
        foreach (var id in group)
            lastEdit[id] = utcNow;
        return due;
    }

    public void Clear() => lastEdit.Clear();
}

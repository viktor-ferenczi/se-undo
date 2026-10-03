using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ClientPlugin.GridStore;

// Byte budgets of the grid store, design section 9. Knows nothing about the game.
public static class StoreRetention
{
    // The grids folders of every world under the storage root: Worlds/<world>/grids
    public static List<string> GridFolders(string storageRoot)
    {
        var worlds = Path.Combine(storageRoot, "Worlds");
        if (!Directory.Exists(worlds))
            return new List<string>();
        return Directory
            .GetDirectories(worlds)
            .Select(world => Path.Combine(world, "grids"))
            .Where(Directory.Exists)
            .ToList();
    }

    // Entry bytes on disk, without opening any index
    public static long BytesOnDisk(IEnumerable<string> gridFolders) =>
        gridFolders
            .SelectMany(f => new DirectoryInfo(f).GetFiles("*" + GridStoreFolder.EntryExtension))
            .Sum(f => f.Length);

    // Removes entries oldest first until the folders together fit the budget. The
    // first pass spares the newest entry of each grid, the second pass runs only
    // if that was not enough and spares nothing. Returns the number of rows removed.
    public static int Enforce(IReadOnlyCollection<GridStoreFolder> folders, long budgetBytes)
    {
        var used = folders.Sum(f => f.Bytes);
        if (used <= budgetBytes)
            return 0;

        // Stable sort, so rows with the same timestamp keep their index order
        var entries = folders
            .SelectMany(f => f.Index.Rows.Select(r => (Folder: f, Row: r)))
            .OrderBy(e => e.Row.TimestampUtc)
            .ToList();

        // A grid is its main grid's entity id plus name, so a renamed grid keeps its line
        var newest = new HashSet<StoreRow>(
            entries
                .GroupBy(e => (e.Folder, e.Row.MainGridEntityId, e.Row.MainGridName))
                .Select(g => g.Last().Row)
        );

        var changed = new HashSet<GridStoreFolder>();
        var removed = 0;
        foreach (var sparing in new[] { true, false })
        {
            foreach (var (folder, row) in entries)
            {
                if (used <= budgetBytes)
                    break;
                if (sparing == !newest.Contains(row))
                {
                    used -= folder.Remove(row, saveIndex: false);
                    changed.Add(folder);
                    removed++;
                }
            }
        }

        foreach (var folder in changed)
            folder.SaveIndex();
        return removed;
    }

    // The smallest multiple of the step that holds the entry
    public static int RaisedBudgetMb(long entryBytes, int stepMb)
    {
        const long mb = 1024 * 1024;
        stepMb = Math.Max(stepMb, 1);
        var steps = (entryBytes + stepMb * mb - 1) / (stepMb * mb);
        return (int)Math.Max(steps, 1) * stepMb;
    }
}

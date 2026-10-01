using System;
using System.Collections.Generic;
using System.Linq;

namespace ClientPlugin.GridStore;

// Columns of the grid history dialog, in display order
public enum GridColumn
{
    Time,
    Name,
    Blocks,
    Grids,
    Pcu,
    Size,
    Reason,
}

// Sort history of the grid history dialog, design section 9: the columns the
// player clicked, most recent first. Rows are compared key by key in that order.
// Stored in the config as comma separated column names, '-' in front for descending.
public sealed class SortKeys
{
    public const string Default = "-Time";

    public readonly List<KeyValuePair<GridColumn, bool>> Keys =
        new List<KeyValuePair<GridColumn, bool>>();

    public static SortKeys Parse(string text)
    {
        var keys = new SortKeys();
        foreach (var part in (text ?? "").Split(','))
        {
            var name = part.Trim();
            var descending = name.StartsWith("-");
            if (
                Enum.TryParse(name.TrimStart('-'), out GridColumn column)
                && Enum.IsDefined(typeof(GridColumn), column)
                && keys.Keys.All(k => k.Key != column)
            )
                keys.Keys.Add(new KeyValuePair<GridColumn, bool>(column, descending));
        }
        return keys.Keys.Count == 0 && text != Default ? Parse(Default) : keys;
    }

    public override string ToString() =>
        string.Join(",", Keys.Select(k => (k.Value ? "-" : "") + k.Key));

    // Clicking a column moves it to the front; clicking the first one flips it
    public void Click(GridColumn column)
    {
        var index = Keys.FindIndex(k => k.Key == column);
        var descending = index >= 0 && Keys[index].Value;
        if (index == 0)
            descending = !descending;
        if (index >= 0)
            Keys.RemoveAt(index);
        Keys.Insert(0, new KeyValuePair<GridColumn, bool>(column, descending));
    }

    public int Compare(StoreRow a, StoreRow b)
    {
        foreach (var key in Keys)
        {
            var order = Compare(key.Key, a, b);
            if (order != 0)
                return key.Value ? -order : order;
        }
        return 0;
    }

    private static int Compare(GridColumn column, StoreRow a, StoreRow b)
    {
        switch (column)
        {
            case GridColumn.Time:
                return a.TimestampUtc.CompareTo(b.TimestampUtc);
            case GridColumn.Name:
                return string.Compare(
                    a.MainGridName,
                    b.MainGridName,
                    StringComparison.CurrentCultureIgnoreCase
                );
            case GridColumn.Blocks:
                return a.BlockCount.CompareTo(b.BlockCount);
            case GridColumn.Grids:
                return a.GridCount.CompareTo(b.GridCount);
            case GridColumn.Pcu:
                return a.Pcu.CompareTo(b.Pcu);
            case GridColumn.Size:
                return a.Size.CompareTo(b.Size);
            default:
                return a.Reason.CompareTo(b.Reason);
        }
    }
}

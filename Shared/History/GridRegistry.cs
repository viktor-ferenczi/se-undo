using System;
using System.Collections.Generic;
using System.Xml.Serialization;

namespace Shared.History;

// Ops refer to grids by a plugin assigned handle, which survives the grid being
// re-created under a new entity id. Blocks are referred to by handle plus position.
public class GridRegistry
{
    public List<GridEntry> Grids = new List<GridEntry>();
    public int NextHandle = 1;

    public int GetOrAdd(long entityId)
    {
        var entry = Grids.Find(g => g.EntityId == entityId && !g.Lost);
        if (entry != null)
            return entry.Handle;

        entry = new GridEntry { Handle = NextHandle++, EntityId = entityId };
        Grids.Add(entry);
        return entry.Handle;
    }

    // Zero when the handle is unknown or lost
    public long EntityIdOf(int handle)
    {
        var entry = Find(handle);
        return entry == null || entry.Lost ? 0 : entry.EntityId;
    }

    public void Rebind(int handle, long entityId)
    {
        var entry = Find(handle) ?? throw new ArgumentException($"Unknown grid handle {handle}");
        entry.EntityId = entityId;
        entry.Lost = false;
    }

    // Marks every handle whose grid no longer exists as lost. A later node may
    // re-create the grid and rebind the handle, so nothing is removed here.
    public void MarkMissing(Func<long, bool> exists)
    {
        foreach (var entry in Grids)
        {
            if (!entry.Lost && !exists(entry.EntityId))
                entry.Lost = true;
        }
    }

    private GridEntry Find(int handle) => Grids.Find(g => g.Handle == handle);
}

public class GridEntry
{
    [XmlAttribute]
    public int Handle;

    [XmlAttribute]
    public long EntityId;

    [XmlAttribute]
    public bool Lost;
}

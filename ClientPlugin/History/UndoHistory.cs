using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Serialization;

namespace ClientPlugin.History;

// History of one context. A tree of nodes under a root sentinel; with the tree option
// off every node has at most one child, which gives the usual linear undo.
public class UndoHistory
{
    public const int RootId = 0;

    // Includes the root sentinel once it has been touched
    public List<Node> Nodes = new List<Node>();

    // The node whose forward action is applied, RootId when nothing is
    public int CurrentId = RootId;
    public int NextId = 1;

    [XmlIgnore]
    public int MaxNodes = 200;

    // Keep abandoned branches when a new action is recorded
    [XmlIgnore]
    public bool Tree;

    // Set while an asynchronous replay runs; undo and redo are refused meanwhile
    [XmlIgnore]
    public PendingOp Pending;

    public bool IsLocked => Pending != null;

    // Nodes without the root sentinel
    public int Count => Nodes.Count(n => n.Id != RootId);

    public Node Current => Get(CurrentId);

    public Node UndoTarget => CurrentId == RootId ? null : Current;

    public Node RedoTarget
    {
        get
        {
            var current = Current;
            if (current.ChildIds.Count == 0)
                return null;
            var id = current.ChildIds.Contains(current.RedoChildId)
                ? current.RedoChildId
                : current.ChildIds[current.ChildIds.Count - 1];
            return Get(id);
        }
    }

    public Node Get(int id)
    {
        var node = Nodes.Find(n => n.Id == id);
        if (node != null || id != RootId)
            return node;

        // Created lazily, so XmlSerializer does not end up with two roots
        node = new Node { Id = RootId, Label = "root" };
        Nodes.Insert(0, node);
        return node;
    }

    // Records a new player action as a child of the current node and makes it current
    public Node Record(string label, List<Op> forward, List<Op> reverse, DateTime utcNow)
    {
        var parent = Current;
        if (!Tree)
        {
            foreach (var childId in parent.ChildIds.ToList())
                DropSubtree(Get(childId));
        }

        var node = new Node
        {
            Id = NextId++,
            ParentId = parent.Id,
            CreatedUtc = utcNow,
            Label = label,
            Forward = forward,
            Reverse = reverse,
            StoreRefs = forward
                .Concat(reverse)
                .SelectMany(op => op.StoreRefs())
                .Distinct()
                .ToList(),
        };
        Nodes.Add(node);
        parent.ChildIds.Add(node.Id);
        parent.RedoChildId = node.Id;
        CurrentId = node.Id;

        Trim();
        return node;
    }

    public void MarkUndone(Node node)
    {
        var parent = Get(node.ParentId);
        parent.RedoChildId = node.Id;
        CurrentId = parent.Id;
    }

    public void MarkRedone(Node node)
    {
        Get(node.ParentId).RedoChildId = node.Id;
        CurrentId = node.Id;
    }

    // Drops the oldest nodes from the root side until the cap holds. A dropped node's
    // descendants on other branches than the current one are dropped with it.
    private void Trim()
    {
        while (Count > MaxNodes)
        {
            var root = Get(RootId);
            var oldestId = root.ChildIds.Where(id => id != CurrentId).DefaultIfEmpty(-1).Min();
            if (oldestId < 0)
                return;

            var oldest = Get(oldestId);
            var path = PathToCurrent();
            if (!path.Contains(oldestId))
            {
                DropSubtree(oldest);
                continue;
            }

            // Splice the node out of the current path, its path child moves up to the root
            var pathChild = Get(oldest.ChildIds.First(path.Contains));
            foreach (var childId in oldest.ChildIds.Where(id => id != pathChild.Id).ToList())
                DropSubtree(Get(childId));

            pathChild.ParentId = RootId;
            root.ChildIds[root.ChildIds.IndexOf(oldestId)] = pathChild.Id;
            if (root.RedoChildId == oldestId)
                root.RedoChildId = pathChild.Id;
            Nodes.Remove(oldest);
        }
    }

    private HashSet<int> PathToCurrent()
    {
        var path = new HashSet<int>();
        for (var node = Current; node != null && path.Add(node.Id); node = Get(node.ParentId))
        {
            if (node.Id == RootId)
                break;
        }
        return path;
    }

    private void DropSubtree(Node node)
    {
        foreach (var childId in node.ChildIds.ToList())
            DropSubtree(Get(childId));
        Get(node.ParentId).ChildIds.Remove(node.Id);
        Nodes.Remove(node);
    }

    // Returns the pending op once it completed or timed out, null while it still runs
    // A failure the server reported while the op was pending; the next poll ends it
    // with an unknown result
    public void FailPending()
    {
        if (Pending != null)
            Pending.Failed = true;
    }

    public PendingOp PollPending(DateTime utcNow)
    {
        var pending = Pending;
        if (pending == null)
            return null;

        if (pending.Failed || !pending.IsDone())
        {
            if (!pending.Failed && utcNow < pending.DeadlineUtc)
                return null;
            pending.TimedOut = true;
            pending.Node.UnknownResult = true;
        }

        Pending = null;
        return pending;
    }
}

public sealed class PendingOp
{
    public Node Node;
    public bool IsUndo;
    public Func<bool> IsDone;
    public DateTime DeadlineUtc;

    // Set by a failure notification of the server
    public bool Failed;

    // The op did not complete in time or failed: the node's result is unknown
    public bool TimedOut;

    // The op restores the node's group snapshot instead of replaying its ops
    public bool ViaSnapshot;

    // Writes the group snapshot taken before the replay to the grid store and returns
    // the op that restores it; called only when the result turned out unknown
    public Func<Op> SaveSnapshot;
}

using System;
using System.Collections.Generic;

namespace ClientPlugin.History;

public class Node
{
    public int Id;
    public int ParentId;
    public List<int> ChildIds = new List<int>();

    // The child redo walks into, the most recently visited one. Zero means the newest child.
    public int RedoChildId;

    public DateTime CreatedUtc;

    // Short text for HUD notifications: "placed 3 blocks"
    public string Label;

    // Set when an asynchronous replay of this node timed out, section 10 of the design
    public bool UnknownResult;

    // Group snapshot taken before that replay. The next step across the node restores
    // it instead of replaying the ops, which puts the world back where it was known.
    public Op Snapshot;

    // A paste on a client whose grids could not all be found; undo is refused, section 6
    public bool ReferenceLost;

    // Ids of the grid store entries the ops and the snapshot need, section 9
    public List<string> StoreRefs = new List<string>();

    // What the player did, replayed on redo in this order
    public List<Op> Forward = new List<Op>();

    // Replayed on undo, last op first
    public List<Op> Reverse = new List<Op>();
}

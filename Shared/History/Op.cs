using System;
using System.Collections.Generic;
using System.Linq;

namespace Shared.History;

// One step of a recorded action. Ops are plain data for XmlSerializer; only Validate
// and Apply talk to the game, so the history and storage code runs without it.
public abstract class Op
{
    // Null when the op can be applied now, otherwise the reason shown to the player.
    public abstract string Validate(GridRegistry grids);

    // Applies the op. Returns null when it completed synchronously, otherwise a check
    // that turns true once the asynchronous rest (a grid split, a server broadcast) is done.
    public abstract Func<bool> Apply(GridRegistry grids);

    // Called on every op of a step before any of them is applied, with the ops of
    // the other direction. An op that removes something saves here what the other
    // direction needs to bring it back complete.
    public virtual void Prepare(GridRegistry grids, List<Op> opposite) { }

    // Grid store entries the op reads
    public virtual IEnumerable<string> StoreRefs() => Enumerable.Empty<string>();
}

// Thrown by Apply when the game would not take the change and nothing was changed
// yet. The step is refused with the reason, like a failed Validate.
public class OpRefusedException : Exception
{
    public OpRefusedException(string reason)
        : base(reason) { }
}

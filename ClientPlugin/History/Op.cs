using System;

namespace ClientPlugin.History;

// One step of a recorded action. Ops are plain data for XmlSerializer; only Validate
// and Apply talk to the game, so the history and storage code runs without it.
public abstract class Op
{
    // Null when the op can be applied now, otherwise the reason shown to the player.
    public abstract string Validate(GridRegistry grids);

    // Applies the op. Returns null when it completed synchronously, otherwise a check
    // that turns true once the asynchronous rest (a grid split, a server broadcast) is done.
    public abstract Func<bool> Apply(GridRegistry grids);
}

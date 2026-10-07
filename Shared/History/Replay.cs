using System;

namespace Shared.History;

// Re-entrancy flag: set while the executor applies ops, so the record hooks
// don't record the plugin's own replays as new player actions.
public static class Replay
{
    public static bool Active { get; private set; }

    public static Scope Begin()
    {
        if (Active)
            throw new InvalidOperationException("Replay is already active");
        Active = true;
        return new Scope();
    }

    public readonly struct Scope : IDisposable
    {
        public void Dispose() => Active = false;
    }
}

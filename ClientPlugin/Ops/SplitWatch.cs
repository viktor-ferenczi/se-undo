using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;

namespace ClientPlugin.Ops;

// Collects the grids split off a grid after blocks were removed from it. Splits are
// detected in the grid's after simulation update, a frame after the removal.
public sealed class SplitWatch
{
    private readonly MyCubeGrid grid;
    private readonly int openedFrame;
    private readonly DateTime openedUtc;
    private readonly List<MyCubeGrid> pieces = new List<MyCubeGrid>();
    private bool closed;

    public SplitWatch(MyCubeGrid grid)
    {
        this.grid = grid;
        openedFrame = MySession.Static.GameplayFrameCounter;
        openedUtc = DateTime.UtcNow;
        grid.OnGridSplit += OnSplit;
    }

    // Split callbacks can come from the parallel update threads
    private void OnSplit(MyCubeGrid original, MyCubeGrid piece)
    {
        lock (pieces)
            pieces.Add(piece);
    }

    public bool Settled
    {
        get
        {
            if (grid.MarkedForClose)
                return true;

            // A client learns about splits from the server, with no signal when none come
            // ponytail: fixed wait on clients, a server companion could report the splits
            if (!Sync.IsServer)
                return DateTime.UtcNow - openedUtc
                    >= TimeSpan.FromSeconds(Config.Current.PendingOperationTimeoutS);

            return MySession.Static.GameplayFrameCounter >= openedFrame + 2
                && grid.m_disconnectsDirty == MyCubeGrid.MyTestDisconnectsReason.NoReason;
        }
    }

    public List<MyCubeGrid> Close()
    {
        if (!closed)
        {
            grid.OnGridSplit -= OnSplit;
            closed = true;
        }

        lock (pieces)
            return pieces.Where(p => !p.MarkedForClose).ToList();
    }
}

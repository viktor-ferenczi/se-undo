using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.GridStore;
using ClientPlugin.Ops;
using Sandbox.Game.Entities;
using VRage.Game;

namespace ClientPlugin.Record;

// A player action whose result arrives a frame or more after the request. The
// recorder polls it and turns it into a node once it settled.
public interface ICapture
{
    bool Settled { get; }

    void Finish();

    // The session ends before the capture settled
    void Abort();
}

// A paste request. Its OnPasteFinished callback hands over the
// grids exactly as the paste created them, on the main thread.
public sealed class PasteCapture : ICapture
{
    private readonly DateTime deadlineUtc = DateTime.UtcNow.AddSeconds(
        Config.Current.PendingOperationTimeoutS
    );
    private bool finished;
    private List<MyCubeGrid> pasted;

    public void Finished(bool success, List<MyCubeGrid> grids)
    {
        finished = true;
        if (success && grids != null)
            pasted = grids.ToList();
    }

    public bool Settled => finished || DateTime.UtcNow >= deadlineUtc;

    // ponytail: a static paste merged into a touching static grid right away is
    // not recorded, it would need a merge capture on the grid it went into
    public void Finish()
    {
        var grids = pasted?.Where(g => !g.MarkedForClose).ToList();
        if (grids != null && grids.Count != 0)
            Recorder.RecordCreated(grids, StoreReason.Pasted, "pasted {0}");
    }

    public void Abort() { }
}

// Grids closed by the player's close request, snapshot before the request the way
// the clipboard copies them. The local server closes them inside the request; only
// grids that really closed are recorded.
public sealed class DeleteCapture : ICapture
{
    private readonly List<MyCubeGrid> grids;
    private readonly List<MyObjectBuilder_CubeGrid> builders;

    public DeleteCapture(List<MyCubeGrid> grids)
    {
        this.grids = grids;
        builders = StoredGroups.Capture(grids);
    }

    public bool Settled => true;

    public void Finish()
    {
        var closed = Enumerable.Range(0, grids.Count).Where(i => grids[i].MarkedForClose).ToList();
        if (closed.Count == 0)
            return;

        var handles = closed.Select(i => Recorder.Handle(grids[i])).ToList();
        StoredGroups.Save(
            closed.Select(i => builders[i]).ToList(),
            StoreReason.Deleted,
            row =>
                Recorder.Commit(
                    $"deleted {Recorder.Describe(row)}",
                    new List<History.Op> { new CloseGridsOp { Grids = handles } },
                    new List<History.Op>
                    {
                        new PasteGridsOp { Entry = row.Id, Grids = handles },
                    }
                ),
            row => Recorder.CommitBarrier($"deleted {Recorder.Describe(row)}")
        );
    }

    public void Abort() { }
}

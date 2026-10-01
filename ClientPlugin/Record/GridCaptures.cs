using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.GridStore;
using ClientPlugin.Ops;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
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

// A paste request on a local server. Its OnPasteFinished callback hands over the
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
            Recorder.RecordCreated(grids, StoreReason.Pasted, "pasted {0}", referenceLost: false);
    }

    public void Abort() { }
}

// Grids a client asked for, recognized when they arrive, design section 6
public sealed class MatchCapture : ICapture
{
    private readonly PasteMatch match;
    private readonly StoreReason reason;
    private readonly string label;

    public MatchCapture(PasteMatch match, StoreReason reason, string label)
    {
        this.match = match;
        this.reason = reason;
        this.label = label;
    }

    public bool Settled => match.Done;

    // Nothing arrived: the paste most likely failed on the server, nothing to undo.
    // Some arrived: recorded, but undo is refused, it cannot remove the rest.
    public void Finish()
    {
        var grids = match.Grids.Where(g => g != null && !g.MarkedForClose).ToList();
        if (grids.Count == 0)
        {
            Log.Info("No grid arrived for a paste request, it is not recorded");
            return;
        }
        Recorder.RecordCreated(grids, reason, label, referenceLost: !match.AllMatched);
    }

    public void Abort() { }
}

// Grids closed by the clipboard's delete or cut, snapshot before the request the
// way the clipboard copies them. Only grids that really closed are recorded.
public sealed class DeleteCapture : ICapture
{
    private readonly List<MyCubeGrid> grids;
    private readonly List<MyObjectBuilder_CubeGrid> builders;
    private readonly DateTime deadlineUtc = DateTime.UtcNow.AddSeconds(
        Config.Current.PendingOperationTimeoutS
    );

    public DeleteCapture(List<MyCubeGrid> grids)
    {
        this.grids = grids;
        builders = StoredGroups.Capture(grids);
    }

    // A local server closes the grids inside the request
    public bool Settled =>
        Sync.IsServer || grids.All(g => g.MarkedForClose) || DateTime.UtcNow >= deadlineUtc;

    public void Finish()
    {
        var closed = Enumerable.Range(0, grids.Count).Where(i => grids[i].MarkedForClose).ToList();
        if (closed.Count == 0)
            return;

        var row = StoredGroups.Save(closed.Select(i => builders[i]).ToList(), StoreReason.Deleted);
        var handles = closed.Select(i => Recorder.Handle(grids[i])).ToList();
        Recorder.Commit(
            $"deleted {Recorder.Describe(row)}",
            new List<History.Op> { new CloseGridsOp { Grids = handles } },
            new List<History.Op>
            {
                new PasteGridsOp { Entry = row.Id, Grids = handles },
            }
        );
    }

    public void Abort() { }
}

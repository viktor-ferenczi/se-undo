using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Shared.GridStore;
using Shared.Ops;
using Shared.Session;
using VRage.Game;

namespace Shared.Record;

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
        Options.Current.PendingOperationTimeoutS
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

    // The frame of the request; requests of the same frame belong together
    public readonly int Frame = MySession.Static.GameplayFrameCounter;

    public DeleteCapture(List<MyCubeGrid> grids)
    {
        this.grids = grids;
        builders = StoredGroups.Capture(grids);
    }

    // A served player's group delete arrives as one close request per grid
    public void Add(MyCubeGrid grid)
    {
        if (grids.Contains(grid))
            return;
        grids.Add(grid);
        builders.AddRange(StoredGroups.Capture(new[] { grid }));
    }

    public bool Settled => MySession.Static.GameplayFrameCounter > Frame;

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

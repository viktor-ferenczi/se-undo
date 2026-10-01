using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.History;
using Xunit;

namespace UndoTests;

public class FakeOp : Op
{
    public string Name;
    public string Refusal;

    public override string Validate(GridRegistry grids) => Refusal;

    public override Func<bool> Apply(GridRegistry grids) => null;
}

public class HistoryTests
{
    private static readonly DateTime Now = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

    private static Node Record(UndoHistory history, string label) =>
        history.Record(
            label,
            new List<Op> { new FakeOp { Name = label } },
            new List<Op> { new FakeOp { Name = "undo " + label } },
            Now
        );

    [Fact]
    public void EmptyHistoryHasNothingToUndoOrRedo()
    {
        var history = new UndoHistory();
        Assert.Null(history.UndoTarget);
        Assert.Null(history.RedoTarget);
        Assert.Equal(0, history.Count);
    }

    [Fact]
    public void UndoAndRedoWalkTheLinearHistory()
    {
        var history = new UndoHistory();
        var a = Record(history, "a");
        var b = Record(history, "b");

        Assert.Same(b, history.UndoTarget);
        history.MarkUndone(b);
        Assert.Same(a, history.UndoTarget);
        Assert.Same(b, history.RedoTarget);

        history.MarkUndone(a);
        Assert.Null(history.UndoTarget);
        Assert.Same(a, history.RedoTarget);

        history.MarkRedone(a);
        history.MarkRedone(b);
        Assert.Same(b, history.UndoTarget);
        Assert.Null(history.RedoTarget);
    }

    [Fact]
    public void NewActionDropsTheRedoBranchWithoutTree()
    {
        var history = new UndoHistory();
        var a = Record(history, "a");
        var b = Record(history, "b");
        history.MarkUndone(b);
        history.MarkUndone(a);

        var c = Record(history, "c");

        Assert.Equal(1, history.Count);
        Assert.Null(history.Get(a.Id));
        Assert.Null(history.Get(b.Id));
        Assert.Same(c, history.UndoTarget);
    }

    [Fact]
    public void TreeKeepsAbandonedBranchesAndRedoFollowsTheLastVisited()
    {
        var history = new UndoHistory { Tree = true };
        var a = Record(history, "a");
        var b = Record(history, "b");
        history.MarkUndone(b);
        history.MarkUndone(a);

        var c = Record(history, "c");
        Assert.Equal(3, history.Count);
        Assert.Equal(new[] { a.Id, c.Id }, history.Get(UndoHistory.RootId).ChildIds);

        history.MarkUndone(c);
        Assert.Same(c, history.RedoTarget);

        // Visiting the old branch makes it the redo branch again
        history.MarkRedone(a);
        history.MarkUndone(a);
        Assert.Same(a, history.RedoTarget);
        history.MarkRedone(a);
        Assert.Same(b, history.RedoTarget);
    }

    [Fact]
    public void CapDropsTheOldestNodesFromTheRootSide()
    {
        var history = new UndoHistory { MaxNodes = 200 };
        for (var i = 1; i <= 210; i++)
            Record(history, "n" + i);

        Assert.Equal(200, history.Count);
        Assert.Equal("n11", history.Get(history.Get(UndoHistory.RootId).ChildIds.Single()).Label);
        Assert.Equal("n210", history.UndoTarget.Label);

        // All 200 remaining nodes can still be undone
        var undone = 0;
        for (var node = history.UndoTarget; node != null; node = history.UndoTarget)
        {
            history.MarkUndone(node);
            undone++;
        }
        Assert.Equal(200, undone);
    }

    [Fact]
    public void CapDropsTheBranchesOfADroppedNode()
    {
        var history = new UndoHistory { Tree = true, MaxNodes = 3 };
        var a = Record(history, "a");
        var b = Record(history, "b");
        history.MarkUndone(b);
        var c = Record(history, "c");
        Record(history, "d");

        // a is dropped, its branch b goes with it, c moves up to the root
        Assert.Equal(2, history.Count);
        Assert.Null(history.Get(a.Id));
        Assert.Null(history.Get(b.Id));
        Assert.Equal(UndoHistory.RootId, history.Get(c.Id).ParentId);
    }

    [Fact]
    public void CapDropsWholeBranchesOffTheCurrentPathFirstWhenOlder()
    {
        var history = new UndoHistory { Tree = true, MaxNodes = 2 };
        var a = Record(history, "a");
        history.MarkUndone(a);
        var b = Record(history, "b");
        var c = Record(history, "c");

        Assert.Equal(2, history.Count);
        Assert.Null(history.Get(a.Id));
        Assert.NotNull(history.Get(b.Id));
        Assert.Same(c, history.UndoTarget);
    }

    [Fact]
    public void PendingOpLocksUntilDone()
    {
        var history = new UndoHistory();
        var node = Record(history, "a");
        var done = false;
        history.Pending = new PendingOp
        {
            Node = node,
            IsDone = () => done,
            DeadlineUtc = Now.AddSeconds(5),
        };

        Assert.True(history.IsLocked);
        Assert.Null(history.PollPending(Now.AddSeconds(1)));

        done = true;
        var finished = history.PollPending(Now.AddSeconds(2));
        Assert.False(finished.TimedOut);
        Assert.False(history.IsLocked);
        Assert.False(node.UnknownResult);
    }

    [Fact]
    public void PendingOpTimeoutMarksTheNodeUnknown()
    {
        var history = new UndoHistory();
        var node = Record(history, "a");
        history.Pending = new PendingOp
        {
            Node = node,
            IsDone = () => false,
            DeadlineUtc = Now.AddSeconds(5),
        };

        var finished = history.PollPending(Now.AddSeconds(5));
        Assert.True(finished.TimedOut);
        Assert.True(node.UnknownResult);
        Assert.False(history.IsLocked);
    }

    [Fact]
    public void ReportedFailureEndsThePendingOpAtOnce()
    {
        var history = new UndoHistory();
        var node = Record(history, "a");
        history.Pending = new PendingOp
        {
            Node = node,
            IsDone = () => false,
            DeadlineUtc = Now.AddSeconds(5),
        };

        history.FailPending();
        var finished = history.PollPending(Now);
        Assert.True(finished.TimedOut);
        Assert.True(node.UnknownResult);
        Assert.False(history.IsLocked);
    }

    [Fact]
    public void RecordListsTheStoreEntriesOfItsOps()
    {
        var history = new UndoHistory();
        var node = history.Record(
            "deleted",
            new List<Op> { new FakeOp { Name = "close" } },
            new List<Op>
            {
                new FakeSnapshotOp { Entry = "abc" },
                new FakeSnapshotOp { Entry = "abc" },
                new FakeSnapshotOp { Entry = "def" },
            },
            Now
        );
        Assert.Equal(new[] { "abc", "def" }, node.StoreRefs);
    }

    [Fact]
    public void ReplayFlagIsScoped()
    {
        Assert.False(Replay.Active);
        using (Replay.Begin())
        {
            Assert.True(Replay.Active);
            Assert.Throws<InvalidOperationException>(() => Replay.Begin());
        }
        Assert.False(Replay.Active);
    }

    [Fact]
    public void RegistryKeepsHandlesAcrossRebinds()
    {
        var registry = new GridRegistry();
        var handle = registry.GetOrAdd(1001);
        Assert.Equal(handle, registry.GetOrAdd(1001));
        Assert.NotEqual(handle, registry.GetOrAdd(1002));

        registry.MarkMissing(id => id == 1002);
        Assert.Equal(0, registry.EntityIdOf(handle));

        registry.Rebind(handle, 2001);
        Assert.Equal(2001, registry.EntityIdOf(handle));
        Assert.Equal(handle, registry.GetOrAdd(2001));
    }
}

using System;
using System.IO;
using System.Linq;
using ClientPlugin.GridStore;
using ClientPlugin.History;
using ClientPlugin.Storage;
using Xunit;

namespace UndoTests;

public sealed class RetentionTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "UndoRetentionTests",
        Guid.NewGuid().ToString("N")
    );

    private static readonly DateTime Start = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Random Noise = new Random(1);

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private GridStoreFolder World(string name) =>
        new GridStoreFolder(Path.Combine(root, "Worlds", name, "grids"));

    // An entry of about 10 KB compressed: random text does not compress much
    private static StoreRow Add(GridStoreFolder store, string grid, int minute, long gridId = 0)
    {
        var bytes = new byte[10000];
        Noise.NextBytes(bytes);
        return store.Add(
            Convert.ToBase64String(bytes),
            new StoreRow
            {
                TimestampUtc = Start.AddMinutes(minute),
                MainGridName = grid,
                MainGridEntityId = gridId == 0 ? grid.GetHashCode() : gridId,
            }
        );
    }

    private static string[] Names(GridStoreFolder store) =>
        store.Index.Rows.Select(r => $"{r.MainGridName}@{r.TimestampUtc.Minute}").ToArray();

    [Fact]
    public void FirstPassSparesTheNewestEntryOfEachGrid()
    {
        var store = World("a");
        Add(store, "Ship", 0);
        Add(store, "Rover", 1);
        Add(store, "Ship", 2);
        Add(store, "Ship", 3);
        var budget = store.Bytes - 1;

        // One entry has to go: the oldest that is not the newest of its grid
        Assert.Equal(1, StoreRetention.Enforce(new[] { store }, budget));
        Assert.Equal(new[] { "Rover@1", "Ship@2", "Ship@3" }, Names(store));

        // Rover is older than both ships, but it is the only copy of its grid
        Assert.Equal(1, StoreRetention.Enforce(new[] { store }, store.Bytes - 1));
        Assert.Equal(new[] { "Rover@1", "Ship@3" }, Names(store));

        // The removed entries' files are gone, the index on disk agrees
        Assert.Equal(2, Directory.GetFiles(store.Folder, "*.sbc.gz").Length);
        Assert.Equal(2, new GridStoreFolder(store.Folder).Index.Rows.Count);
    }

    [Fact]
    public void SecondPassRemovesOldestFirstWhenOnlyNewestCopiesAreLeft()
    {
        var store = World("a");
        Add(store, "Ship", 0);
        Add(store, "Rover", 1);
        Add(store, "Ship", 2);
        Add(store, "Base", 3);
        var one = store.Index.Rows[0].Bytes;

        // Room for two entries: the old ship copy goes in the first pass, then the
        // oldest newest copy, the rover
        Assert.Equal(2, StoreRetention.Enforce(new[] { store }, 2 * one + one / 2));
        Assert.Equal(new[] { "Ship@2", "Base@3" }, Names(store));
    }

    [Fact]
    public void ARenamedGridIsAnotherLine()
    {
        var store = World("a");
        Add(store, "Ship", 0, gridId: 7);
        Add(store, "Ship Mk2", 1, gridId: 7);
        Add(store, "Ship Mk2", 2, gridId: 7);

        StoreRetention.Enforce(new[] { store }, store.Bytes - 1);
        Assert.Equal(new[] { "Ship@0", "Ship Mk2@2" }, Names(store));
    }

    [Fact]
    public void TotalBudgetTakesFromOlderWorldsFirst()
    {
        var old = World("old");
        var current = World("current");
        Add(old, "Ship", 0);
        Add(old, "Ship", 1);
        Add(current, "Ship", 2);
        Add(current, "Ship", 3);
        var folders = new[] { old, current };
        Assert.Equal(
            folders.Sum(f => f.Bytes),
            StoreRetention.BytesOnDisk(StoreRetention.GridFolders(root))
        );

        // Room for two: both first pass victims go, one from each world
        var one = old.Index.Rows[0].Bytes;
        StoreRetention.Enforce(folders, 2 * one + one / 2);
        Assert.Equal(new[] { "Ship@1" }, Names(old));
        Assert.Equal(new[] { "Ship@3" }, Names(current));

        // Room for one: the old world's last copy gives way to the current one
        StoreRetention.Enforce(folders, one + one / 2);
        Assert.Empty(old.Index.Rows);
        Assert.Equal(new[] { "Ship@3" }, Names(current));
    }

    [Fact]
    public void AnEntryIndexedTwiceCountsOnceAndStaysWhileARowNeedsIt()
    {
        var store = World("a");
        var first = store.Add("same", new StoreRow { TimestampUtc = Start, MainGridName = "Ship" });
        var second = store.Add(
            "same",
            new StoreRow { TimestampUtc = Start.AddMinutes(1), MainGridName = "Ship" }
        );
        Assert.Equal(first.Bytes, store.Bytes);

        Assert.Equal(0, store.Remove(first));
        Assert.True(store.Has(second.Id));
        Assert.Equal(second.Bytes, store.Remove(second));
        Assert.False(store.Has(second.Id));
    }

    [Fact]
    public void AStagedEntryIsATemporaryFileUntilCommitted()
    {
        var store = World("a");
        var row = store.Stage("big", new StoreRow { TimestampUtc = Start });
        Assert.True(store.IsStaged(row));
        Assert.True(row.Bytes > 0);
        Assert.False(store.Has(row.Id));
        Assert.Empty(store.Index.Rows);

        store.Discard(row);
        Assert.Empty(Directory.GetFiles(store.Folder));

        row = store.Commit(store.Stage("big", new StoreRow { TimestampUtc = Start }));
        Assert.False(store.IsStaged(row));
        Assert.Equal("big", store.Read(row.Id));

        // Stored already: staging it again takes no new space
        Assert.False(store.IsStaged(store.Stage("big", new StoreRow { TimestampUtc = Start })));

        // A staged entry left behind by a game that ended is removed on the next start
        var left = store.Stage("left behind", new StoreRow { TimestampUtc = Start });
        Assert.False(new GridStoreFolder(store.Folder).IsStaged(left));
    }

    [Theory]
    [InlineData(1, 64, 64)]
    [InlineData(64 * 1024 * 1024, 64, 64)]
    [InlineData(64 * 1024 * 1024 + 1, 64, 128)]
    [InlineData(700L * 1024 * 1024, 64, 704)]
    [InlineData(3 * 1024 * 1024, 2, 4)]
    public void BudgetIsRaisedToTheNextMultipleOfTheStep(long bytes, int step, int expected)
    {
        Assert.Equal(expected, StoreRetention.RaisedBudgetMb(bytes, step));
    }

    [Fact]
    public void ABarrierNodeSurvivesTheDocumentRoundTrip()
    {
        var serializer = new UndoDocumentSerializer(new[] { typeof(FakeOp) });
        var document = new UndoDocument();
        document.Build.RecordBarrier("deleted Ship", Start);

        var node = serializer.Load(serializer.Save(document)).Build.UndoTarget;
        Assert.True(node.Barrier);
        Assert.Equal("deleted Ship", node.Label);
        Assert.Empty(node.Reverse);
    }
}

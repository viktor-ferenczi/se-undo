using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ClientPlugin.GridStore;
using Xunit;

namespace UndoTests;

public sealed class GridStoreTests : IDisposable
{
    private const string Xml =
        "<?xml version=\"1.0\"?>\n<Definitions><ShipBlueprints><ShipBlueprint><CubeGrids><CubeGrid><DisplayName>Ship</DisplayName></CubeGrid></CubeGrids></ShipBlueprint></ShipBlueprints></Definitions>";

    private readonly string folder = Path.Combine(
        Path.GetTempPath(),
        "UndoGridStoreTests",
        Guid.NewGuid().ToString("N"),
        "grids"
    );

    public void Dispose()
    {
        var root = Path.GetDirectoryName(folder)!;
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static StoreRow Row(StoreReason reason) =>
        new StoreRow
        {
            TimestampUtc = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
            Reason = reason,
            MainGridName = "Ship & \"Co\"",
            GridCount = 2,
            BlockCount = 42,
            Pcu = 1234,
            Size = GridSizeClass.Mixed,
            IsStatic = true,
            MainGridEntityId = 123456789012345678,
        };

    [Fact]
    public void IdIsTheSha256OfTheUncompressedXml()
    {
        var expected = BitConverter
            .ToString(SHA256.HashData(Encoding.UTF8.GetBytes(Xml)))
            .Replace("-", "")
            .ToLowerInvariant();

        Assert.Equal(expected, GridStore.IdOf(Xml));
        Assert.Equal(64, GridStore.IdOf(Xml).Length);
        Assert.NotEqual(GridStore.IdOf(Xml), GridStore.IdOf(Xml + " "));
    }

    [Fact]
    public void AddWritesTheEntryAndReadsItBack()
    {
        var store = new GridStore(folder);
        var row = store.Add(Xml, Row(StoreReason.Deleted));

        Assert.Equal(GridStore.IdOf(Xml), row.Id);
        Assert.True(File.Exists(Path.Combine(folder, row.Id + ".sbc.gz")));
        Assert.Equal(new FileInfo(store.EntryPath(row.Id)).Length, row.Bytes);
        Assert.True(row.Bytes > 0);
        Assert.Equal(Xml, store.Read(row.Id));
        Assert.Null(store.Read(GridStore.IdOf("missing")));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
    }

    [Fact]
    public void TheSameGroupStoredTwiceIsOneFileWithTwoRows()
    {
        var store = new GridStore(folder);
        var first = store.Add(Xml, Row(StoreReason.Deleted));
        var second = store.Add(Xml, Row(StoreReason.Pasted));

        Assert.Equal(first.Id, second.Id);
        Assert.Single(Directory.GetFiles(folder, "*.sbc.gz"));
        Assert.Equal(2, store.Index.Rows.Count);
    }

    [Fact]
    public void IndexKeepsEveryRowFieldAcrossReload()
    {
        var written = new GridStore(folder).Add(Xml, Row(StoreReason.Split));

        var reloaded = new GridStore(folder);
        var row = Assert.Single(reloaded.Index.Rows);
        Assert.Equal(written.Id, row.Id);
        Assert.Equal(written.TimestampUtc, row.TimestampUtc);
        Assert.Equal(DateTimeKind.Utc, row.TimestampUtc.Kind);
        Assert.Equal(StoreReason.Split, row.Reason);
        Assert.Equal("Ship & \"Co\"", row.MainGridName);
        Assert.Equal(2, row.GridCount);
        Assert.Equal(42, row.BlockCount);
        Assert.Equal(1234, row.Pcu);
        Assert.Equal(GridSizeClass.Mixed, row.Size);
        Assert.True(row.IsStatic);
        Assert.Equal(123456789012345678, row.MainGridEntityId);
        Assert.Equal(written.Bytes, row.Bytes);
        Assert.Equal(Xml, reloaded.Read(row.Id));
    }

    [Fact]
    public void ACorruptIndexIsSetAsideNotOverwritten()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, GridStore.IndexFileName), "<StoreIndex><Rows>");

        var store = new GridStore(folder);
        Assert.Empty(store.Index.Rows);
        store.Add(Xml, Row(StoreReason.Deleted));

        Assert.Equal(
            "<StoreIndex><Rows>",
            File.ReadAllText(Path.Combine(folder, GridStore.IndexFileName + ".bad"))
        );
        Assert.Single(new GridStore(folder).Index.Rows);
    }
}

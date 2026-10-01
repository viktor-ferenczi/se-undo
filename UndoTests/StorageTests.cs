using System;
using System.Collections.Generic;
using System.Text;
using ClientPlugin.History;
using ClientPlugin.Storage;
using Xunit;

namespace UndoTests;

// Stands in for an op that carries a grid builder as nested XML text
public class FakeSnapshotOp : FakeOp
{
    public int Grid;
    public string BuilderXml;
    public string Entry;

    public override IEnumerable<string> StoreRefs() =>
        Entry == null ? Array.Empty<string>() : new[] { Entry };
}

public class StorageTests
{
    private static readonly UndoDocumentSerializer Serializer = new UndoDocumentSerializer(
        new[] { typeof(FakeOp), typeof(FakeSnapshotOp) }
    );

    private static UndoDocument MakeDocument()
    {
        var document = new UndoDocument();
        var handle = document.Grids.GetOrAdd(123456789012345678);
        var builder =
            "<?xml version=\"1.0\"?>\n<MyObjectBuilder_CubeGrid><DisplayName>Ship &amp; \"Co\"</DisplayName></MyObjectBuilder_CubeGrid>";
        var node = document.Build.Record(
            "removed 1 block",
            new List<Op> { new FakeOp { Name = "raze" } },
            new List<Op>
            {
                new FakeSnapshotOp { Grid = handle, BuilderXml = builder },
            },
            new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc)
        );
        node.UnknownResult = true;
        node.ReferenceLost = true;
        node.Snapshot = new FakeSnapshotOp { Grid = handle, Entry = "0123abcd" };
        node.StoreRefs.Add("0123abcd");
        document.Build.MarkUndone(node);
        return document;
    }

    [Fact]
    public void DocumentRoundTripsThroughGzippedXml()
    {
        var original = MakeDocument();
        var data = Serializer.Save(original);
        Assert.Equal(0x1f, data[0]);
        Assert.Equal(0x8b, data[1]);

        var loaded = Serializer.Load(data);

        Assert.Equal(1, loaded.Build.Count);
        Assert.Equal(UndoHistory.RootId, loaded.Build.CurrentId);
        var node = loaded.Build.RedoTarget;
        Assert.Equal("removed 1 block", node.Label);
        var snapshot = Assert.IsType<FakeSnapshotOp>(node.Reverse[0]);
        Assert.Equal(
            ((FakeSnapshotOp)original.Build.Get(node.Id).Reverse[0]).BuilderXml,
            snapshot.BuilderXml
        );
        Assert.Equal(123456789012345678, loaded.Grids.EntityIdOf(snapshot.Grid));
        Assert.True(node.UnknownResult);
        Assert.True(node.ReferenceLost);
        Assert.Equal("0123abcd", Assert.IsType<FakeSnapshotOp>(node.Snapshot).Entry);
        Assert.Equal(new[] { "0123abcd" }, node.StoreRefs);
    }

    [Fact]
    public void LoadedDocumentHasASingleRoot()
    {
        var loaded = Serializer.Load(Serializer.Save(MakeDocument()));
        loaded.Build.Get(UndoHistory.RootId);
        Assert.Single(loaded.Build.Nodes, n => n.Id == UndoHistory.RootId);
    }

    [Fact]
    public void OtherVersionLoadsAsNull()
    {
        var document = MakeDocument();
        document.Version = UndoDocument.CurrentVersion + 1;
        Assert.Null(Serializer.Load(Serializer.Save(document)));
    }

    [Fact]
    public void StatusFileListsNodesAndEscapesText()
    {
        var document = MakeDocument();
        var json = StatusFile.ToJson(
            new[] { new KeyValuePair<string, UndoHistory>("build", document.Build) },
            "Undo: removed \"1\" block\n",
            42
        );

        Assert.Contains("\"count\":1", json);
        Assert.Contains("\"current\":0", json);
        Assert.Contains("\"label\":\"removed 1 block\"", json);
        Assert.Contains("\"referenceLost\":true,\"storeRefs\":[\"0123abcd\"]", json);
        Assert.Contains("\"lastMessage\":\"Undo: removed \\\"1\\\" block\\u000a\"", json);
        Assert.Contains("\"documentBytes\":42", json);
        Assert.Equal(Encoding.UTF8.GetByteCount(json), Encoding.ASCII.GetByteCount(json));
    }
}

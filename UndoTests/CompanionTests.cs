using System;
using System.Collections.Generic;
using System.IO;
using Shared;
using Shared.Companion;
using Shared.GridStore;
using Xunit;

namespace UndoTests;

public class CompanionTests
{
    [Fact]
    public void Options_survive_the_handshake()
    {
        var sent = new PlayerOptions
        {
            EnableBuildContext = false,
            EnableTerminalContext = true,
            RecordTerminalChangesOutsideTerminal = true,
            RestoreRemovedBlocksWithFullState = false,
            UndoTree = true,
            MaxNodesBuild = 12,
            MaxNodesTerminal = 34,
        };
        var message = Protocol.Write(
            MessageType.Hello,
            w =>
            {
                w.Write(Protocol.Version);
                Protocol.WriteOptions(w, sent);
            }
        );

        var reader = Protocol.Read(message, out var type);
        Assert.Equal(MessageType.Hello, type);
        Assert.Equal(Protocol.Version, reader.ReadInt32());
        var received = Protocol.ReadOptions(reader);
        Assert.False(received.EnableBuildContext);
        Assert.True(received.EnableTerminalContext);
        Assert.True(received.RecordTerminalChangesOutsideTerminal);
        Assert.False(received.RestoreRemovedBlocksWithFullState);
        Assert.True(received.UndoTree);
        Assert.Equal(12, received.MaxNodesBuild);
        Assert.Equal(34, received.MaxNodesTerminal);
        Assert.True(message.Length <= Protocol.MaxClientMessageBytes);
    }

    [Fact]
    public void Rows_survive_the_trip_to_the_dialog()
    {
        var row = new StoreRow
        {
            Id = "abc",
            TimestampUtc = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc),
            Reason = StoreReason.Deleted,
            MainGridName = "Ship",
            GridCount = 2,
            BlockCount = 30,
            Pcu = 400,
            Size = GridSizeClass.Mixed,
            IsStatic = true,
            MainGridEntityId = 123456789,
            Bytes = 4096,
        };
        var message = Protocol.Write(
            MessageType.Backups,
            w => Protocol.WriteRows(w, new List<StoreRow> { row })
        );

        var received = Protocol.ReadRows(Protocol.Read(message, out _));
        var copy = Assert.Single(received);
        Assert.Equal(row.Id, copy.Id);
        Assert.Equal(row.TimestampUtc, copy.TimestampUtc);
        Assert.Equal(DateTimeKind.Utc, copy.TimestampUtc.Kind);
        Assert.Equal(row.Reason, copy.Reason);
        Assert.Equal(row.MainGridName, copy.MainGridName);
        Assert.Equal(row.GridCount, copy.GridCount);
        Assert.Equal(row.BlockCount, copy.BlockCount);
        Assert.Equal(row.Pcu, copy.Pcu);
        Assert.Equal(row.Size, copy.Size);
        Assert.Equal(row.IsStatic, copy.IsStatic);
        Assert.Equal(row.MainGridEntityId, copy.MainGridEntityId);
        Assert.Equal(row.Bytes, copy.Bytes);
    }

    [Fact]
    public void A_truncated_message_throws_instead_of_reading_garbage()
    {
        var message = Protocol.Write(MessageType.Step, w => w.Write((byte)StepContext.Build));
        var reader = Protocol.Read(message, out _);
        reader.ReadByte();
        Assert.Throws<EndOfStreamException>(() => reader.ReadBoolean());
    }

    [Fact]
    public void The_limiter_refuses_a_burst_and_refills()
    {
        var limiter = new RequestLimiter(size: 3, refillPerSecond: 2);
        var start = new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc);

        Assert.True(limiter.Take(1, 1, start));
        Assert.True(limiter.Take(1, 1, start));
        Assert.True(limiter.Take(1, 1, start));
        Assert.False(limiter.Take(1, 1, start));

        // Another player has a bucket of their own
        Assert.True(limiter.Take(2, 3, start));

        // Half a second gives one token back, never more than the bucket holds
        Assert.True(limiter.Take(1, 1, start.AddSeconds(0.5)));
        Assert.False(limiter.Take(1, 1, start.AddSeconds(0.5)));
        Assert.True(limiter.Take(1, 3, start.AddSeconds(60)));
        Assert.False(limiter.Take(1, 1, start.AddSeconds(60)));
    }

    [Fact]
    public void A_refused_request_takes_nothing()
    {
        var limiter = new RequestLimiter(size: 5, refillPerSecond: 0);
        var now = DateTime.UtcNow;
        Assert.True(limiter.Take(1, 3, now));
        Assert.False(limiter.Take(1, 3, now));
        Assert.True(limiter.Take(1, 2, now));
    }
}

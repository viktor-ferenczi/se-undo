using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Shared.GridStore;

namespace Shared.Companion;

// What the client plugin and a server running the companion say to each other,
// design section 14. Mod messages on one channel; the server takes the sender's
// Steam id from the network, never from the message.
public enum MessageType : byte
{
    // Client: the protocol version it speaks and the player's options
    Hello = 1,

    // Server: whether it serves this client
    Welcome = 2,

    // Client: undo or redo in a context
    Step = 3,

    // Server: a text for the player's HUD, the answer to a step
    Notice = 4,

    // Server: what the client needs to route its keys, after every history change
    State = 5,

    // Client: its terminal screen opened or closed
    Terminal = 6,

    // Client: the rows of the grid history dialog; server: the rows
    Backups = 7,

    // Client: one backup for the clipboard; server: a part of its file
    Fetch = 8,
    Part = 9,

    // Client: removes a backup, the server answers with the rows
    Delete = 10,
}

public enum StepContext : byte
{
    Build,
    Terminal,
}

public static class Protocol
{
    // A channel of its own among the mod message ids
    public const ushort Channel = 48771;

    // Raised when a message changes in a way the other side would misread
    public const int Version = 1;

    // Nothing a client sends needs more; larger messages are dropped unread
    public const int MaxClientMessageBytes = 1024;

    // A backup file goes to the client in parts of this size
    public const int PartBytes = 60000;

    public static byte[] Write(MessageType type, Action<BinaryWriter> body = null)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8))
        {
            writer.Write((byte)type);
            body?.Invoke(writer);
        }
        return buffer.ToArray();
    }

    public static BinaryReader Read(byte[] message, out MessageType type)
    {
        var reader = new BinaryReader(new MemoryStream(message), Encoding.UTF8);
        type = (MessageType)reader.ReadByte();
        return reader;
    }

    public static void WriteOptions(BinaryWriter writer, PlayerOptions options)
    {
        writer.Write(options.EnableBuildContext);
        writer.Write(options.EnableTerminalContext);
        writer.Write(options.RecordTerminalChangesOutsideTerminal);
        writer.Write(options.RestoreRemovedBlocksWithFullState);
        writer.Write(options.UndoTree);
        writer.Write(options.MaxNodesBuild);
        writer.Write(options.MaxNodesTerminal);
    }

    public static PlayerOptions ReadOptions(BinaryReader reader) =>
        new PlayerOptions
        {
            EnableBuildContext = reader.ReadBoolean(),
            EnableTerminalContext = reader.ReadBoolean(),
            RecordTerminalChangesOutsideTerminal = reader.ReadBoolean(),
            RestoreRemovedBlocksWithFullState = reader.ReadBoolean(),
            UndoTree = reader.ReadBoolean(),
            MaxNodesBuild = reader.ReadInt32(),
            MaxNodesTerminal = reader.ReadInt32(),
        };

    public static void WriteRows(BinaryWriter writer, IList<StoreRow> rows)
    {
        writer.Write(rows.Count);
        foreach (var row in rows)
        {
            writer.Write(row.Id);
            writer.Write(row.TimestampUtc.Ticks);
            writer.Write((byte)row.Reason);
            writer.Write(row.MainGridName ?? "");
            writer.Write(row.GridCount);
            writer.Write(row.BlockCount);
            writer.Write(row.Pcu);
            writer.Write((byte)row.Size);
            writer.Write(row.IsStatic);
            writer.Write(row.MainGridEntityId);
            writer.Write(row.Bytes);
        }
    }

    public static List<StoreRow> ReadRows(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        var rows = new List<StoreRow>(count);
        for (var i = 0; i < count; i++)
            rows.Add(
                new StoreRow
                {
                    Id = reader.ReadString(),
                    TimestampUtc = new DateTime(reader.ReadInt64(), DateTimeKind.Utc),
                    Reason = (StoreReason)reader.ReadByte(),
                    MainGridName = reader.ReadString(),
                    GridCount = reader.ReadInt32(),
                    BlockCount = reader.ReadInt32(),
                    Pcu = reader.ReadInt32(),
                    Size = (GridSizeClass)reader.ReadByte(),
                    IsStatic = reader.ReadBoolean(),
                    MainGridEntityId = reader.ReadInt64(),
                    Bytes = reader.ReadInt64(),
                }
            );
        return rows;
    }
}

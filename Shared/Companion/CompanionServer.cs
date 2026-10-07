using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Shared.GridStore;
using Shared.History;
using Shared.Ops;
using Shared.Record;
using Shared.Session;
using Shared.Storage;
using VRage.Game;
using VRage.GameServices;

namespace Shared.Companion;

// The histories of the players a server serves, saved with the world
public class ServedHistories
{
    public int Version = UndoDocument.CurrentVersion;
    public List<ServedHistory> Players = new List<ServedHistory>();
}

public class ServedHistory
{
    [XmlAttribute]
    public ulong SteamId;

    public UndoDocument Document;
}

// The server half of the companion, design section 14: a dedicated server with the
// server plugin, or a lobby host with the client plugin. It records what the players
// it serves do (ServedRecordPatches), keeps their histories and grid stores, and
// replays their undo and redo steps here, where the id preserving paths exist.
// Nothing a client sends is trusted: it only asks for steps and backups, every check
// runs here for the sending player.
public static class CompanionServer
{
    public const string WorldFileName = "UndoPlayers.xml.gz";
    public const string StatusFileName = "status-players.json";

    // Token bucket per player: a step costs one token, the bucket holds this many
    // and refills at this rate per second
    private const double BucketSize = 20;
    private const double RefillPerSecond = 10;

    private static readonly Dictionary<ulong, UndoDocument> documents =
        new Dictionary<ulong, UndoDocument>();
    private static readonly HashSet<ulong> connected = new HashSet<ulong>();
    private static readonly RequestLimiter limiter = new RequestLimiter(
        BucketSize,
        RefillPerSecond
    );
    private static readonly Dictionary<ulong, bool> sentIdle = new Dictionary<ulong, bool>();
    private static readonly Dictionary<ulong, DateTime> refusedUtc =
        new Dictionary<ulong, DateTime>();

    private static XmlSerializer serializer;
    private static bool running;

    private static XmlSerializer Serializer =>
        serializer ??= new XmlSerializer(typeof(ServedHistories), OpTypes.All);

    public static bool Running => running;

    // At session start, where this process is a server with clients
    public static void Start()
    {
        if (running || MyMultiplayer.Static == null || MyAPIGateway.Multiplayer == null)
            return;

        running = true;
        documents.Clear();
        connected.Clear();
        limiter.Clear();
        refusedUtc.Clear();
        sentIdle.Clear();
        Load();
        MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(Protocol.Channel, OnMessage);
        MyMultiplayer.Static.ClientLeft += OnClientLeft;
        MySession.Static.OnSavingCheckpoint += OnSavingCheckpoint;
        MySession.OnUnloading += Stop;
        Log.Info($"Serving undo to the players of this world, protocol {Protocol.Version}");
    }

    public static void Stop()
    {
        if (!running)
            return;

        running = false;
        MySession.OnUnloading -= Stop;
        MyAPIGateway.Multiplayer?.UnregisterSecureMessageHandler(Protocol.Channel, OnMessage);
        if (MyMultiplayer.Static != null)
            MyMultiplayer.Static.ClientLeft -= OnClientLeft;
        if (MySession.Static != null)
            MySession.Static.OnSavingCheckpoint -= OnSavingCheckpoint;
        WorldSavePatches.Set(WorldFileName, null);
        foreach (var steamId in Actors.Served.Keys.ToList())
            Actors.Served[steamId].Recording.Reset();
        Actors.Served.Clear();
        documents.Clear();
        connected.Clear();
    }

    // --- persistence ---------------------------------------------------------------

    private static string WorldFilePath() =>
        Path.Combine(MySession.Static.CurrentPath, WorldFileName);

    // A missing, unreadable or differently versioned file is no history
    private static void Load()
    {
        if (!Options.Current.PersistInTheWorldSave)
            return;

        var path = WorldFilePath();
        if (!File.Exists(path))
            return;

        try
        {
            using var gzip = new System.IO.Compression.GZipStream(
                File.OpenRead(path),
                System.IO.Compression.CompressionMode.Decompress
            );
            var saved = (ServedHistories)Serializer.Deserialize(gzip);
            if (saved?.Version != UndoDocument.CurrentVersion)
            {
                Log.Info($"{path} has another format version, the players start over");
                return;
            }
            foreach (var player in saved.Players.Where(p => p.Document != null))
            {
                player.Document.Grids.MarkMissing(MyEntities.EntityExists);
                documents[player.SteamId] = player.Document;
            }
            Log.Info($"Loaded the histories of {documents.Count} players from {path}");
        }
        catch (Exception e)
        {
            Log.Warning($"Loading the players' histories failed, they start over: {e}");
        }
    }

    // Main thread, inside MySession.Save; the save worker writes the bytes
    private static void OnSavingCheckpoint(MyObjectBuilder_Checkpoint checkpoint)
    {
        if (!Options.Current.PersistInTheWorldSave || documents.Count == 0)
        {
            WorldSavePatches.Set(WorldFileName, null);
            return;
        }

        try
        {
            // Strokes still being coalesced belong to the saved state
            foreach (var actor in Actors.Served.Values)
            {
                using (Actor.Use(actor))
                    Recorder.Flush();
            }

            var saved = new ServedHistories
            {
                Players = documents
                    .Select(d => new ServedHistory { SteamId = d.Key, Document = d.Value })
                    .ToList(),
            };
            using var buffer = new MemoryStream();
            using (
                var gzip = new System.IO.Compression.GZipStream(
                    buffer,
                    System.IO.Compression.CompressionLevel.Optimal
                )
            )
                Serializer.Serialize(gzip, saved);
            WorldSavePatches.Set(WorldFileName, buffer.ToArray());
        }
        catch (Exception e)
        {
            Log.Error($"Serializing the players' histories for the save failed: {e}");
        }
    }

    // --- messages ------------------------------------------------------------------

    private static void OnClientLeft(ulong steamId, MyChatMemberStateChangeEnum _)
    {
        connected.Remove(steamId);
        sentIdle.Remove(steamId);
    }

    private static void Send(ulong steamId, byte[] message)
    {
        if (connected.Contains(steamId))
            MyAPIGateway.Multiplayer.SendMessageTo(Protocol.Channel, message, steamId, true);
    }

    private static void OnMessage(ushort channel, byte[] message, ulong sender, bool fromServer)
    {
        if (fromServer || !running)
            return;

        if (
            message == null
            || message.Length == 0
            || message.Length > Protocol.MaxClientMessageBytes
        )
        {
            Log.Warning($"Dropped a message of {message?.Length ?? 0} bytes from {sender}");
            return;
        }

        try
        {
            var reader = Protocol.Read(message, out var type);
            var now = DateTime.UtcNow;
            if (!limiter.Take(sender, Cost(type), now))
            {
                // Once a second at most, a burst must not turn into a burst of answers
                if (
                    Actors.Served.TryGetValue(sender, out var limited)
                    && (
                        !refusedUtc.TryGetValue(sender, out var told)
                        || (now - told).TotalSeconds >= 1
                    )
                )
                {
                    refusedUtc[sender] = now;
                    limited.Notify("Undo not available: too many requests, wait a moment");
                }
                return;
            }

            if (type == MessageType.Hello)
            {
                OnHello(sender, reader);
                return;
            }

            // Everything else needs the handshake first
            if (!Actors.Served.TryGetValue(sender, out var actor) || !connected.Contains(sender))
                return;

            using (Actor.Use(actor))
                Handle(actor, type, reader);
        }
        catch (Exception e)
        {
            Log.Warning($"A message from {sender} could not be handled: {e.Message}");
        }
    }

    private static double Cost(MessageType type) =>
        type switch
        {
            MessageType.Hello => 5,
            MessageType.Backups => 5,
            MessageType.Fetch => 10,
            _ => 1,
        };

    private static void OnHello(ulong sender, BinaryReader reader)
    {
        var version = reader.ReadInt32();
        if (version != Protocol.Version)
        {
            Log.Info($"{sender} speaks undo protocol {version}, not served");
            connected.Add(sender);
            Send(
                sender,
                Protocol.Write(
                    MessageType.Welcome,
                    w =>
                    {
                        w.Write(Protocol.Version);
                        w.Write(false);
                        w.Write(
                            $"the server runs another version of Undo (protocol {Protocol.Version})"
                        );
                    }
                )
            );
            connected.Remove(sender);
            return;
        }

        var options = Protocol.ReadOptions(reader);
        options.MaxNodesBuild = Clamp(options.MaxNodesBuild, Options.Current.MaxNodesBuild);
        options.MaxNodesTerminal = Clamp(
            options.MaxNodesTerminal,
            Options.Current.MaxNodesTerminal
        );

        if (!documents.TryGetValue(sender, out var document))
            documents[sender] = document = new UndoDocument();

        if (!Actors.Served.TryGetValue(sender, out var actor))
        {
            actor = new Actor(sender, isLocal: false) { Document = document };
            actor.Show = text =>
                Send(sender, Protocol.Write(MessageType.Notice, w => w.Write(text)));
            actor.Changed = () => Changed(actor);
            Actors.Served[sender] = actor;
        }
        actor.Options = options;
        document.Build.MaxNodes = options.MaxNodesBuild;
        document.Build.Tree = options.UndoTree;
        document.Terminal.MaxNodes = options.MaxNodesTerminal;
        document.Terminal.Tree = options.UndoTree;

        connected.Add(sender);
        sentIdle.Remove(sender);
        Send(
            sender,
            Protocol.Write(
                MessageType.Welcome,
                w =>
                {
                    w.Write(Protocol.Version);
                    w.Write(true);
                    w.Write("");
                }
            )
        );
        Log.Info(
            $"Serving undo to {sender}, {document.Build.Count} build and {document.Terminal.Count} terminal nodes"
        );
        Changed(actor);
    }

    private static int Clamp(int asked, int limit) => Math.Max(1, Math.Min(asked, limit));

    private static void Handle(Actor actor, MessageType type, BinaryReader reader)
    {
        switch (type)
        {
            case MessageType.Step:
                var context = (StepContext)reader.ReadByte();
                var undo = reader.ReadBoolean();
                Step(actor, context, undo);
                break;

            case MessageType.Terminal:
                actor.TerminalOpen = reader.ReadBoolean();
                // The changes made in the terminal belong together
                if (!actor.TerminalOpen)
                    actor.Recording.FlushTerminal();
                break;

            case MessageType.Backups:
                SendRows(actor);
                break;

            case MessageType.Fetch:
                SendBackup(actor, reader.ReadString());
                break;

            case MessageType.Delete:
                var id = reader.ReadString();
                foreach (var row in actor.Store.Index.Rows.Where(r => r.Id == id).ToList())
                    actor.Store.Remove(row);
                SendRows(actor);
                break;
        }
    }

    private static void Step(Actor actor, StepContext context, bool undo)
    {
        var verb = undo ? "Undo" : "Redo";
        var options = actor.Options;
        var enabled =
            context == StepContext.Build
                ? options.EnableBuildContext
                : options.EnableTerminalContext;
        if (!enabled)
            return;
        if (!actor.Creative)
        {
            actor.Notify($"{verb} not available: needs creative tools");
            return;
        }

        var document = actor.Document;
        var history = context == StepContext.Build ? document.Build : document.Terminal;
        if (undo)
            Apply.Executor.Undo(history, document.Grids);
        else
            Apply.Executor.Redo(history, document.Grids);
    }

    // What the client needs to know right away: whether Ctrl-Z in gameplay has
    // anything to do, so an idle key goes to the game's relative dampeners
    private static void Changed(Actor actor)
    {
        var history = actor.Document.Build;
        var idle =
            !history.IsLocked
            && !actor.Recording.IsBusy
            && actor.Recording.Stroke == null
            && history.UndoTarget == null;
        if (!sentIdle.TryGetValue(actor.SteamId, out var sent) || sent != idle)
        {
            sentIdle[actor.SteamId] = idle;
            Send(actor.SteamId, Protocol.Write(MessageType.State, w => w.Write(idle)));
        }
        WriteStatus();
    }

    private static void SendRows(Actor actor)
    {
        var rows = actor.Store.Index.Rows.ToList();
        Send(actor.SteamId, Protocol.Write(MessageType.Backups, w => Protocol.WriteRows(w, rows)));
    }

    // The entry file as it is on disk, gzip compressed, in parts
    private static void SendBackup(Actor actor, string id)
    {
        var store = actor.Store;
        if (!store.Has(id))
        {
            actor.Notify("The backup is gone from the grid store");
            SendRows(actor);
            return;
        }

        var bytes = File.ReadAllBytes(store.EntryPath(id));
        var count = Math.Max(1, (bytes.Length + Protocol.PartBytes - 1) / Protocol.PartBytes);
        for (var i = 0; i < count; i++)
        {
            var offset = i * Protocol.PartBytes;
            var length = Math.Min(Protocol.PartBytes, bytes.Length - offset);
            var index = i;
            Send(
                actor.SteamId,
                Protocol.Write(
                    MessageType.Part,
                    w =>
                    {
                        w.Write(id);
                        w.Write(index);
                        w.Write(count);
                        w.Write(length);
                        w.Write(bytes, offset, length);
                    }
                )
            );
        }
    }

    // --- debug status file -------------------------------------------------------

    private static void WriteStatus()
    {
        if (!Options.Current.DebugStatusFile)
            return;

        try
        {
            var players = Actors.Served.Values.Select(actor =>
                StatusFile.Quote(actor.SteamId.ToString())
                + ":"
                + StatusFile.ToJson(
                    new[]
                    {
                        new KeyValuePair<string, UndoHistory>("build", actor.Document.Build),
                        new KeyValuePair<string, UndoHistory>("terminal", actor.Document.Terminal),
                    },
                    actor.LastMessage,
                    0,
                    "\"connected\":" + (connected.Contains(actor.SteamId) ? "true" : "false")
                )
            );
            var root = Options.Current.StorageRoot;
            Directory.CreateDirectory(root);
            File.WriteAllText(
                Path.Combine(root, StatusFileName),
                "{\"mode\":"
                    + StatusFile.Quote(Apply.Permissions.Mode.ToString())
                    + ",\"players\":{"
                    + string.Join(",", players)
                    + "}}"
            );
        }
        catch (Exception e)
        {
            Log.Error($"Writing the players' status file failed: {e}");
        }
    }
}

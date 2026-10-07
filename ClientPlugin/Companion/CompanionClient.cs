using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using ClientPlugin.Gui;
using ClientPlugin.Session;
using Sandbox.Game.Gui;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Shared;
using Shared.Companion;
using Shared.GridStore;
using Shared.Ops;
using VRage.Game;

namespace ClientPlugin.Companion;

// The client half of the companion, design section 14. On a client of a server,
// undo works only when the server answers the handshake: it records what the player
// does and replays the steps the keys ask for. The client keeps no history of its own.
public static class CompanionClient
{
    // How long the client waits for the server's answer before it gives up
    private const double HelloTimeoutS = 15;

    public static bool Connected { get; private set; }

    // Whether the build history on the server has nothing to undo, so Ctrl-Z in
    // gameplay goes to the game
    public static bool BuildIdle { get; private set; } = true;

    // The rows of the grid history dialog, null until the server sent them
    public static List<StoreRow> Rows { get; private set; }

    private static bool started;
    private static DateTime? waitingSinceUtc;
    private static bool helloDue;
    private static DateTime helloUtc;
    private static bool terminalOpen;

    // The backup being received, part by part
    private static string fetchId;
    private static byte[][] fetchParts;

    // Asked live like on a host: creative tools can be switched any time. The server
    // checks again.
    public static bool Active =>
        Connected
        && (MySession.Static.CreativeMode || MySession.Static.CreativeToolsEnabled(Sync.MyId));

    public static void Start()
    {
        if (started || MyAPIGateway.Multiplayer == null)
            return;

        started = true;
        Connected = false;
        BuildIdle = true;
        Rows = null;
        terminalOpen = false;
        MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(Protocol.Channel, OnMessage);
        Log.Info("Asking the server for the Undo companion");
        SendHello();
    }

    public static void Stop()
    {
        if (!started)
            return;

        started = false;
        Connected = false;
        waitingSinceUtc = null;
        fetchId = null;
        MyAPIGateway.Multiplayer?.UnregisterSecureMessageHandler(Protocol.Channel, OnMessage);
    }

    // The player changed the options; a dragged slider changes them every frame, so
    // they go to the server at most once a second
    public static void OptionsChanged() => helloDue = started;

    private static void SendHello()
    {
        helloDue = false;
        helloUtc = DateTime.UtcNow;
        waitingSinceUtc ??= helloUtc;
        Send(
            Protocol.Write(
                MessageType.Hello,
                w =>
                {
                    w.Write(Protocol.Version);
                    Protocol.WriteOptions(w, Config.Current.PlayerOptions());
                }
            )
        );
    }

    public static void Update()
    {
        if (!started)
            return;

        if (helloDue && (DateTime.UtcNow - helloUtc).TotalSeconds >= 1)
            SendHello();

        if (waitingSinceUtc is { } since && (DateTime.UtcNow - since).TotalSeconds > HelloTimeoutS)
        {
            waitingSinceUtc = null;
            if (!Connected)
                Log.Info("The server does not run the Undo companion, undo is off in this world");
        }

        // The server records terminal changes while the terminal is open
        var open = MyGuiScreenTerminal.IsOpen;
        if (Connected && open != terminalOpen)
        {
            terminalOpen = open;
            Send(Protocol.Write(MessageType.Terminal, w => w.Write(open)));
        }
    }

    public static void Step(StepContext context, bool undo) =>
        Send(
            Protocol.Write(
                MessageType.Step,
                w =>
                {
                    w.Write((byte)context);
                    w.Write(undo);
                }
            )
        );

    public static void RequestRows() => Send(Protocol.Write(MessageType.Backups));

    public static void Fetch(string id)
    {
        fetchId = id;
        fetchParts = null;
        Send(Protocol.Write(MessageType.Fetch, w => w.Write(id)));
    }

    public static void Delete(string id) =>
        Send(Protocol.Write(MessageType.Delete, w => w.Write(id)));

    private static void Send(byte[] message) =>
        MyAPIGateway.Multiplayer?.SendMessageToServer(Protocol.Channel, message, true);

    private static void OnMessage(ushort channel, byte[] message, ulong sender, bool fromServer)
    {
        if (!fromServer || !started)
            return;

        try
        {
            var reader = Protocol.Read(message, out var type);
            switch (type)
            {
                case MessageType.Welcome:
                    OnWelcome(reader);
                    break;

                case MessageType.Notice:
                    UndoSession.Show(reader.ReadString());
                    break;

                case MessageType.State:
                    BuildIdle = reader.ReadBoolean();
                    UndoSession.Changed();
                    break;

                case MessageType.Backups:
                    Rows = Protocol.ReadRows(reader);
                    GridHistoryScreen.RowsArrived();
                    break;

                case MessageType.Part:
                    OnPart(reader);
                    break;
            }
        }
        catch (Exception e)
        {
            Log.Warning($"A message from the server could not be handled: {e.Message}");
        }
    }

    private static void OnWelcome(BinaryReader reader)
    {
        var version = reader.ReadInt32();
        var accepted = reader.ReadBoolean();
        var reason = reader.ReadString();
        waitingSinceUtc = null;
        if (!accepted)
        {
            Connected = false;
            Log.Info($"Undo is off in this world: {reason}");
            return;
        }

        if (!Connected)
            Log.Info($"The server runs the Undo companion, protocol {version}");
        Connected = true;
        UndoSession.Changed();
    }

    // The parts of a backup file; the whole file is the blueprint, gzip compressed
    private static void OnPart(BinaryReader reader)
    {
        var id = reader.ReadString();
        var index = reader.ReadInt32();
        var count = reader.ReadInt32();
        var bytes = reader.ReadBytes(reader.ReadInt32());
        if (id != fetchId || count <= 0 || index < 0 || index >= count)
            return;

        fetchParts ??= new byte[count][];
        if (fetchParts.Length != count)
            return;
        fetchParts[index] = bytes;
        if (Array.Exists(fetchParts, p => p == null))
            return;

        var parts = fetchParts;
        fetchId = null;
        fetchParts = null;

        using var file = new MemoryStream();
        foreach (var part in parts)
            file.Write(part, 0, part.Length);
        file.Position = 0;
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var text = new StreamReader(gzip, Encoding.UTF8);
        GridHistoryScreen.PasteArrived(
            BuilderXml.Read<MyObjectBuilder_Definitions>(text.ReadToEnd())
        );
    }
}

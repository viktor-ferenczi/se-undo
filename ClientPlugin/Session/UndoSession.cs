using System;
using System.Collections.Generic;
using System.IO;
using ClientPlugin.Companion;
using ClientPlugin.Gui;
using ClientPlugin.Record;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using Shared;
using Shared.Apply;
using Shared.Companion;
using Shared.History;
using Shared.Ops;
using Shared.Record;
using Shared.Session;
using Shared.Storage;
using VRage.Game;
using VRage.Game.Components;

namespace ClientPlugin.Session;

// Per world state, and where the history is loaded and saved, design section 8.
// Where the plugin is the host it records and replays for the local player (the
// local actor) and serves its joined players through the companion; on a client of
// a server it asks the server's companion instead (design section 14).
[MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
public class UndoSession : MySessionComponentBase
{
    public const string WorldFileName = "Undo.xml.gz";

    public static string LastMessage;

    private static UndoDocumentSerializer serializer;

    private static UndoDocumentSerializer Serializer =>
        serializer ??= new UndoDocumentSerializer(OpTypes.All);

    // The local player's history, null on a client of a server
    public static UndoDocument Document => Actors.Local?.Document;

    // Undo works for the local player: as the host, or through the server's
    // companion, and in survival only with creative tools on (design section 2). The
    // tools can be switched any time, so this is asked live.
    public static bool Active => Actors.Local?.Active == true || CompanionClient.Active;

    public override void LoadData()
    {
        Clear();
        LastMessage = null;
        MyEntities.OnEntityAdd += OnEntityAdd;
        MySession.Static.OnSavingCheckpoint += OnSavingCheckpoint;
        MySession.OnUnloading += OnUnloading;
    }

    // Entities exist by now, so most terminal controls do too, and the session
    // knows its save folder
    public override void BeforeStart()
    {
        Log.Info($"Session mode: {Permissions.Mode}");
        if (!Sync.IsServer)
        {
            CompanionClient.Start();
            Changed();
            return;
        }

        TerminalContextPatches.PatchControls();
        Actors.Local = new Actor(Sync.MyId, isLocal: true)
        {
            Document = Load(),
            Show = Hud,
            Changed = Changed,
        };
        Configure();

        // A host serves the players who joined it the same way a server does
        CompanionServer.Start();
        Changed();
    }

    protected override void UnloadData()
    {
        MySession.OnUnloading -= OnUnloading;
        MyEntities.OnEntityAdd -= OnEntityAdd;
        Clear();
    }

    // A grid brings the terminal controls of its block types along
    private static void OnEntityAdd(VRage.Game.Entity.MyEntity entity)
    {
        if (entity is MyCubeGrid)
            TerminalContextPatches.ControlsMayHaveChanged();
    }

    // Before the game takes anything down
    private static void OnUnloading()
    {
        MySession.OnUnloading -= OnUnloading;
        Clear();
    }

    private static void Clear()
    {
        CompanionClient.Stop();
        CompanionServer.Stop();
        Actors.Clear();
        WorldSavePatches.Clear();
    }

    // In the save folder, null when the config says not to keep the history
    private static string HistoryPath() =>
        Config.Current.PersistInTheWorldSave
            ? Path.Combine(MySession.Static.CurrentPath, WorldFileName)
            : null;

    // A missing, unreadable or differently versioned file is an empty history
    private static UndoDocument Load()
    {
        UndoDocument document = null;
        try
        {
            var path = HistoryPath();
            if (path != null && File.Exists(path))
            {
                document = Serializer.Load(File.ReadAllBytes(path));
                Log.Info(
                    document == null
                        ? $"{path} has another format version, starting with an empty history"
                        : $"Loaded {document.Build.Count} build and {document.Terminal.Count} terminal nodes from {path}"
                );
            }
        }
        catch (Exception e)
        {
            Log.Warning($"Loading the history failed, starting with an empty one: {e}");
        }

        document ??= new UndoDocument();
        document.Grids.MarkMissing(MyEntities.EntityExists);
        return document;
    }

    // Main thread, inside MySession.Save, while the game state is consistent
    private static void OnSavingCheckpoint(MyObjectBuilder_Checkpoint checkpoint)
    {
        WorldSavePatches.Set(WorldFileName, null);
        if (Document == null || !Config.Current.PersistInTheWorldSave)
            return;

        try
        {
            // A stroke still being coalesced belongs to the saved state
            Recorder.Flush();
            WorldSavePatches.Set(WorldFileName, Serializer.Save(Document));
        }
        catch (Exception e)
        {
            Log.Error($"Serializing the history for the save failed: {e}");
        }
    }

    // Applies the options of the config, also called when the config changes
    public static void Configure()
    {
        var options = Config.Current.PlayerOptions();
        CompanionClient.OptionsChanged();

        var local = Actors.Local;
        if (local == null)
            return;

        local.Options = options;
        local.Document.Build.MaxNodes = options.MaxNodesBuild;
        local.Document.Build.Tree = options.UndoTree;
        local.Document.Terminal.MaxNodes = options.MaxNodesTerminal;
        local.Document.Terminal.Tree = options.UndoTree;
    }

    // Called every frame from the plugin's update
    public static void Update()
    {
        CompanionClient.Update();
        if (Document != null)
            TerminalContextPatches.Update();
        Actors.Update();
    }

    // What the server's companion answered to a step. Logged and written to the
    // status file, so tests can read it.
    public static void Show(string text)
    {
        Log.Info(text);
        Hud(text);
        Changed();
    }

    // HUD text for undo, redo and refusals
    private static void Hud(string text)
    {
        LastMessage = text;
        var config = Config.Current;
        if (config.Notifications)
            MyAPIGateway.Utilities?.ShowNotification(text, config.NotificationDurationMs);
    }

    // After every history change
    public static void Changed()
    {
        if (!Config.Current.DebugStatusFile || Document == null && !CompanionClient.Connected)
            return;

        try
        {
            // Serializing on every change is only affordable in debug mode, and it
            // exercises the XML serialization of the real ops in game. On a client
            // of a server the histories are the server's, see its status file.
            var histories = new List<KeyValuePair<string, UndoHistory>>();
            var size = 0;
            if (Document != null)
            {
                size = Serializer.Save(Document).Length;
                histories.Add(new KeyValuePair<string, UndoHistory>("build", Document.Build));
                histories.Add(new KeyValuePair<string, UndoHistory>("terminal", Document.Terminal));
            }

            var root = Options.Current.StorageRoot;
            Directory.CreateDirectory(root);
            File.WriteAllText(
                Path.Combine(root, "status.json"),
                StatusFile.ToJson(
                    histories,
                    LastMessage,
                    size,
                    GridHistoryScreen.StatusJson()
                        + ",\"companion\":"
                        + (CompanionClient.Connected ? "true" : "false"),
                    Permissions.Mode.ToString()
                )
            );
        }
        catch (Exception e)
        {
            Log.Error($"Writing the status file failed: {e}");
        }
    }
}

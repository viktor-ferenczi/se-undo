using System;
using System.Collections.Generic;
using System.IO;
using ClientPlugin.Apply;
using ClientPlugin.GridStore;
using ClientPlugin.Gui;
using ClientPlugin.History;
using ClientPlugin.Ops;
using ClientPlugin.Record;
using ClientPlugin.Storage;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.FileSystem;
using VRage.Game;
using VRage.Game.Components;

namespace ClientPlugin.Session;

// Per world state, and where the history is loaded and saved, design section 8
[MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
public class UndoSession : MySessionComponentBase
{
    public const string WorldFileName = "Undo.xml.gz";
    public const string ClientFileName = "history.xml.gz";

    public static UndoDocument Document { get; private set; }
    public static string LastMessage;

    private static UndoDocumentSerializer serializer;
    private static GridStoreFolder store;

    // The document as it was when the game took its save snapshot. The save worker
    // writes it next to the checkpoint, see WorldSavePatches.
    public static volatile byte[] SavedDocument;

    // Client sessions: the history changed since it was last written
    private static bool unsaved;
    private static DateTime lastClientWriteUtc;

    private static UndoDocumentSerializer Serializer =>
        serializer ??= new UndoDocumentSerializer(OpTypes.All);

    // Opened on first use, when the session knows its world and server
    public static GridStoreFolder Store => store ??= new GridStoreFolder(StoredGroups.Folder());

    public static string StorageRoot
    {
        get
        {
            var root = Config.Current.ClientStorageRoot;
            return string.IsNullOrWhiteSpace(root)
                ? Path.Combine(MyFileSystem.UserDataPath, Plugin.Name)
                : root;
        }
    }

    public override void LoadData()
    {
        Document = new UndoDocument();
        LastMessage = null;
        store = null;
        SavedDocument = null;
        unsaved = false;
        lastClientWriteUtc = DateTime.UtcNow;
        MyEntities.OnEntityAdd += PasteMatch.OnEntityAdd;
        MySession.Static.OnSavingCheckpoint += OnSavingCheckpoint;
        MySession.OnUnloading += OnUnloading;
        Configure();
    }

    // Entities exist by now, so most terminal controls do too, and the session
    // knows its save folder
    public override void BeforeStart()
    {
        TerminalContextPatches.PatchControls();
        Document = Load();
        Configure();
        Changed();
        unsaved = false;
    }

    protected override void UnloadData()
    {
        MySession.OnUnloading -= OnUnloading;
        MyEntities.OnEntityAdd -= PasteMatch.OnEntityAdd;
        Clear();
    }

    // Before the game takes anything down. A client has no save event, so its
    // history is written here.
    private static void OnUnloading()
    {
        MySession.OnUnloading -= OnUnloading;
        SaveClientHistory();
        Clear();
    }

    private static void Clear()
    {
        PasteMatch.Reset();
        Recorder.Reset();
        Document = null;
        store = null;
        SavedDocument = null;
    }

    // Where this session keeps its history, null when the config says not to:
    // in the save folder where the plugin is the server, otherwise in the plugin's
    // own storage, since a client has no save folder
    private static string HistoryPath()
    {
        var config = Config.Current;
        if (Sync.IsServer)
            return config.PersistInTheWorldSave
                ? Path.Combine(MySession.Static.CurrentPath, WorldFileName)
                : null;
        return config.PersistOnMultiplayerClient
            ? Path.Combine(StoredGroups.WorldFolder(), ClientFileName)
            : null;
    }

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
        SavedDocument = null;
        if (Document == null || !Sync.IsServer || !Config.Current.PersistInTheWorldSave)
            return;

        try
        {
            // A stroke still being coalesced belongs to the saved state
            Recorder.Flush();
            SavedDocument = Serializer.Save(Document);
        }
        catch (Exception e)
        {
            Log.Error($"Serializing the history for the save failed: {e}");
        }
    }

    // Client sessions only: on unload, when the server saves, and after changes
    public static void SaveClientHistory()
    {
        unsaved = false;
        lastClientWriteUtc = DateTime.UtcNow;
        if (Document == null || Sync.IsServer || !Config.Current.PersistOnMultiplayerClient)
            return;

        try
        {
            var path = HistoryPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, Serializer.Save(Document));
            File.Delete(path);
            File.Move(temporary, path);
        }
        catch (Exception e)
        {
            Log.Error($"Writing the client side history failed: {e}");
        }
    }

    // Applies the history limits of the config, also called when the config changes
    public static void Configure()
    {
        if (Document == null)
            return;

        var config = Config.Current;
        Document.Build.MaxNodes = config.MaxNodesBuild;
        Document.Build.Tree = config.UndoTree;
        Document.Terminal.MaxNodes = config.MaxNodesTerminal;
        Document.Terminal.Tree = config.UndoTree;
    }

    // Called every frame from the plugin's update
    public static void Update()
    {
        if (Document == null)
            return;

        Recorder.Update();
        Executor.Update(Document.Build);
        Executor.Update(Document.Terminal);

        if (
            unsaved
            && (DateTime.UtcNow - lastClientWriteUtc).TotalSeconds
                >= Config.Current.ClientAutosaveIntervalS
        )
            SaveClientHistory();
    }

    // After every history change
    public static void Changed()
    {
        if (Document == null)
            return;

        unsaved = true;
        if (!Config.Current.DebugStatusFile)
            return;

        try
        {
            // Serializing on every change is only affordable in debug mode, and it
            // exercises the XML serialization of the real ops in game.
            var size = Serializer.Save(Document).Length;

            var histories = new[]
            {
                new KeyValuePair<string, UndoHistory>("build", Document.Build),
                new KeyValuePair<string, UndoHistory>("terminal", Document.Terminal),
            };
            Directory.CreateDirectory(StorageRoot);
            File.WriteAllText(
                Path.Combine(StorageRoot, "status.json"),
                StatusFile.ToJson(histories, LastMessage, size, GridHistoryScreen.StatusJson())
            );
        }
        catch (Exception e)
        {
            Log.Error($"Writing the status file failed: {e}");
        }
    }
}

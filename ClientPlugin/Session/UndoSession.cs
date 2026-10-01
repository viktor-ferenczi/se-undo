using System;
using System.Collections.Generic;
using System.IO;
using ClientPlugin.Apply;
using ClientPlugin.GridStore;
using ClientPlugin.History;
using ClientPlugin.Ops;
using ClientPlugin.Record;
using ClientPlugin.Storage;
using Sandbox.Game.Entities;
using VRage.FileSystem;
using VRage.Game.Components;

namespace ClientPlugin.Session;

// Per world state. Loading and saving the document with the world is the
// persistence slice; until then every session starts with an empty history.
[MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
public class UndoSession : MySessionComponentBase
{
    public static UndoDocument Document { get; private set; }
    public static string LastMessage;

    private static UndoDocumentSerializer serializer;
    private static GridStoreFolder store;

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
        MyEntities.OnEntityAdd += PasteMatch.OnEntityAdd;
        Configure();
        Changed();
    }

    // Entities exist by now, so most terminal controls do too
    public override void BeforeStart() => TerminalContextPatches.PatchControls();

    protected override void UnloadData()
    {
        MyEntities.OnEntityAdd -= PasteMatch.OnEntityAdd;
        PasteMatch.Reset();
        Recorder.Reset();
        Document = null;
        store = null;
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
    }

    // After every history change
    public static void Changed()
    {
        if (Document == null || !Config.Current.DebugStatusFile)
            return;

        try
        {
            // Serializing on every change is only affordable in debug mode, and it
            // exercises the XML serialization of the real ops in game.
            serializer ??= new UndoDocumentSerializer(OpTypes.All);
            var size = serializer.Save(Document).Length;

            var histories = new[]
            {
                new KeyValuePair<string, UndoHistory>("build", Document.Build),
                new KeyValuePair<string, UndoHistory>("terminal", Document.Terminal),
            };
            Directory.CreateDirectory(StorageRoot);
            File.WriteAllText(
                Path.Combine(StorageRoot, "status.json"),
                StatusFile.ToJson(histories, LastMessage, size)
            );
        }
        catch (Exception e)
        {
            Log.Error($"Writing the status file failed: {e}");
        }
    }
}

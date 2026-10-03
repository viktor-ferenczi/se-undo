using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using HarmonyLib;
using Sandbox.Engine.Networking;
using Sandbox.Game.World;
using VRage.Game;
using VRage.GameServices;

namespace ClientPlugin.Session;

// The write half of design section 8. A file survives the save, its backups and a
// restore only when it is written into the staging folder <world>/.new together
// with the checkpoint: the game copies the staging folder's files into the world
// folder and every Backup/<timestamp>/ folder, and deletes other top level files.
[SuppressMessage("ReSharper", "UnusedMember.Local")]
public static class WorldSavePatches
{
    [HarmonyPatch(
        typeof(MyLocalCache),
        nameof(MyLocalCache.SaveCheckpoint),
        new[]
        {
            typeof(MyObjectBuilder_Checkpoint),
            typeof(string),
            typeof(ulong),
            typeof(List<MyCloudFile>),
        },
        new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out, ArgumentType.Normal }
    )]
    private static class SaveCheckpointPatch
    {
        // Runs on the save worker thread, so it only writes the bytes the main
        // thread serialized in OnSavingCheckpoint. Other callers (world settings,
        // Save As from the load menu) pass the world folder itself and are skipped.
        private static void Postfix(string sessionPath, List<MyCloudFile> fileList)
        {
            var document = UndoSession.SavedDocument;
            if (
                document == null
                || !sessionPath.TrimEnd('/', '\\').EndsWith(".new", StringComparison.Ordinal)
            )
                return;

            try
            {
                var path = Path.Combine(sessionPath, UndoSession.WorldFileName);
                File.WriteAllBytes(path, document);
                fileList?.Add(new MyCloudFile(path));
            }
            catch (Exception e)
            {
                Log.Error($"Writing the history into the save failed: {e}");
            }
        }
    }
}

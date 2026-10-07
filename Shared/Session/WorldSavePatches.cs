using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using HarmonyLib;
using Sandbox.Engine.Networking;
using Sandbox.Game.World;
using VRage.Game;
using VRage.GameServices;

namespace Shared.Session;

// The write half of design section 8. A file survives the save, its backups and a
// restore only when it is written into the staging folder <world>/.new together
// with the checkpoint: the game copies the staging folder's files into the world
// folder and every Backup/<timestamp>/ folder, and deletes other top level files.
[SuppressMessage("ReSharper", "UnusedMember.Local")]
public static class WorldSavePatches
{
    // The files to write next to the checkpoint, by file name: what the main thread
    // serialized when the game took its save snapshot. The bytes stay until the next
    // snapshot or the unload, so a save queued behind another one gets them too.
    private static readonly Dictionary<string, byte[]> files = new Dictionary<string, byte[]>();

    // Null removes the file from the next saves
    public static void Set(string fileName, byte[] bytes)
    {
        lock (files)
        {
            if (bytes == null)
                files.Remove(fileName);
            else
                files[fileName] = bytes;
        }
    }

    public static void Clear()
    {
        lock (files)
            files.Clear();
    }

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
            if (!sessionPath.TrimEnd('/', '\\').EndsWith(".new", StringComparison.Ordinal))
                return;

            List<KeyValuePair<string, byte[]>> pending;
            lock (files)
                pending = files.ToList();

            foreach (var file in pending)
            {
                try
                {
                    var path = Path.Combine(sessionPath, file.Key);
                    File.WriteAllBytes(path, file.Value);
                    fileList?.Add(new MyCloudFile(path));
                }
                catch (Exception e)
                {
                    Log.Error($"Writing {file.Key} into the save failed: {e}");
                }
            }
        }
    }
}

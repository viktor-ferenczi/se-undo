using System;
using System.IO;
using System.Linq;

namespace ClientPlugin.Storage;

// Histories of server sessions pile up, one folder per server, player and world.
// Whole world folders go once nothing in them was written for the retention time.
public static class ClientRetention
{
    // Returns the number of world folders deleted
    public static int Clean(string storageRoot, DateTime cutoffUtc)
    {
        var servers = Path.Combine(storageRoot, "Servers");
        if (!Directory.Exists(servers))
            return 0;

        var deleted = 0;
        foreach (var server in Directory.GetDirectories(servers))
        {
            foreach (var player in Directory.GetDirectories(server))
            {
                foreach (var world in Directory.GetDirectories(player))
                {
                    if (NewestWriteUtc(world) >= cutoffUtc)
                        continue;
                    Directory.Delete(world, recursive: true);
                    deleted++;
                }
                DeleteIfEmpty(player);
            }
            DeleteIfEmpty(server);
        }
        return deleted;
    }

    private static DateTime NewestWriteUtc(string folder) =>
        new DirectoryInfo(folder)
            .GetFiles("*", SearchOption.AllDirectories)
            .Select(f => f.LastWriteTimeUtc)
            .DefaultIfEmpty(Directory.GetLastWriteTimeUtc(folder))
            .Max();

    private static void DeleteIfEmpty(string folder)
    {
        if (!Directory.EnumerateFileSystemEntries(folder).Any())
            Directory.Delete(folder);
    }
}

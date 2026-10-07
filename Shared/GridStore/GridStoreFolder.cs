using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;

namespace Shared.GridStore;

public enum StoreReason
{
    Deleted,
    Pasted,
    Placed,
    Snapshot,
}

public enum GridSizeClass
{
    Large,
    Small,
    Mixed,
}

// One index row per stored group, with everything the grid history dialog shows,
// so the dialog never opens an entry file until the player picks one
public class StoreRow
{
    [XmlAttribute]
    public string Id;

    [XmlAttribute]
    public DateTime TimestampUtc;

    [XmlAttribute]
    public StoreReason Reason;

    // The grid with the most blocks
    [XmlAttribute]
    public string MainGridName;

    [XmlAttribute]
    public int GridCount;

    [XmlAttribute]
    public int BlockCount;

    [XmlAttribute]
    public int Pcu;

    [XmlAttribute]
    public GridSizeClass Size;

    [XmlAttribute]
    public bool IsStatic;

    [XmlAttribute]
    public long MainGridEntityId;

    // Compressed size of the entry file
    [XmlAttribute]
    public long Bytes;
}

public class StoreIndex
{
    public List<StoreRow> Rows = new List<StoreRow>();
}

// Grid group builders the history needs, design section 9. Each entry is a gzip
// compressed blueprint file named by the SHA-256 of its XML, so an unchanged group
// stored twice is one file with two index rows. Knows nothing about the game: the
// caller passes the blueprint XML and the row it computed from the builders.
public sealed class GridStoreFolder
{
    public const string IndexFileName = "index.xml";
    public const string EntryExtension = ".sbc.gz";

    private static readonly XmlSerializer IndexSerializer = new XmlSerializer(typeof(StoreIndex));

    public readonly string Folder;
    public readonly StoreIndex Index;

    public GridStoreFolder(string folder)
    {
        Folder = folder;
        Index = LoadIndex();

        // Staged entries nobody committed: the game ended while a question was open
        if (Directory.Exists(folder))
        {
            foreach (var stale in Directory.GetFiles(folder, "*" + EntryExtension + ".tmp"))
                File.Delete(stale);
        }
    }

    private string IndexPath => Path.Combine(Folder, IndexFileName);

    public static string IdOf(string xml)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(xml));
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    public string EntryPath(string id) => Path.Combine(Folder, id + EntryExtension);

    public bool Has(string id) => id != null && File.Exists(EntryPath(id));

    // Compressed bytes on disk; an entry indexed more than once counts once
    public long Bytes
    {
        get
        {
            var ids = new HashSet<string>();
            long bytes = 0;
            foreach (var row in Index.Rows)
            {
                if (ids.Add(row.Id))
                    bytes += row.Bytes;
            }
            return bytes;
        }
    }

    private string StagedPath(string id) => EntryPath(id) + ".tmp";

    // Writes the entry as a temporary file, unless the same XML is stored already,
    // and fills in the row's id and size. Commit or Discard follows.
    public StoreRow Stage(string xml, StoreRow row)
    {
        Directory.CreateDirectory(Folder);
        row.Id = IdOf(xml);

        var path = EntryPath(row.Id);
        if (!File.Exists(path))
        {
            path = StagedPath(row.Id);
            using var gzip = new GZipStream(File.Create(path), CompressionLevel.Optimal);
            var bytes = Encoding.UTF8.GetBytes(xml);
            gzip.Write(bytes, 0, bytes.Length);
        }

        row.Bytes = new FileInfo(path).Length;
        return row;
    }

    // True while the row's entry is a temporary file, which means it takes new space
    public bool IsStaged(StoreRow row) => File.Exists(StagedPath(row.Id));

    public StoreRow Commit(StoreRow row)
    {
        if (IsStaged(row))
            File.Move(StagedPath(row.Id), EntryPath(row.Id));
        Index.Rows.Add(row);
        SaveIndex();
        return row;
    }

    public void Discard(StoreRow row) => File.Delete(StagedPath(row.Id));

    public StoreRow Add(string xml, StoreRow row) => Commit(Stage(xml, row));

    // Removes the row, and the entry file when no other row shares its id. Returns
    // the bytes that freed.
    public long Remove(StoreRow row, bool saveIndex = true)
    {
        if (!Index.Rows.Remove(row))
            return 0;

        long freed = 0;
        if (!Index.Rows.Exists(r => r.Id == row.Id))
        {
            File.Delete(EntryPath(row.Id));
            freed = row.Bytes;
        }
        if (saveIndex)
            SaveIndex();
        return freed;
    }

    // Null when the entry file is gone
    public string Read(string id)
    {
        if (!Has(id))
            return null;

        using var gzip = new GZipStream(File.OpenRead(EntryPath(id)), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private StoreIndex LoadIndex()
    {
        if (!File.Exists(IndexPath))
            return new StoreIndex();

        try
        {
            using var stream = File.OpenRead(IndexPath);
            return (StoreIndex)IndexSerializer.Deserialize(stream) ?? new StoreIndex();
        }
        catch (InvalidOperationException)
        {
            // Kept for a look by hand instead of being overwritten by the next write
            File.Copy(IndexPath, IndexPath + ".bad", overwrite: true);
            return new StoreIndex();
        }
    }

    public void SaveIndex()
    {
        var temporary = IndexPath + ".tmp";
        using (var stream = File.Create(temporary))
            IndexSerializer.Serialize(stream, Index);
        if (File.Exists(IndexPath))
            File.Replace(temporary, IndexPath, null);
        else
            File.Move(temporary, IndexPath);
    }
}

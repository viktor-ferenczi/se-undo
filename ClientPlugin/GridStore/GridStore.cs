using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;

namespace ClientPlugin.GridStore;

public enum StoreReason
{
    Deleted,
    Pasted,
    Placed,
    Split,
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
public sealed class GridStore
{
    public const string IndexFileName = "index.xml";
    public const string EntryExtension = ".sbc.gz";

    private static readonly XmlSerializer IndexSerializer = new XmlSerializer(typeof(StoreIndex));

    public readonly string Folder;
    public readonly StoreIndex Index;

    public GridStore(string folder)
    {
        Folder = folder;
        Index = LoadIndex();
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

    // Writes the entry unless the same XML is stored already, then adds the row
    public StoreRow Add(string xml, StoreRow row)
    {
        Directory.CreateDirectory(Folder);
        row.Id = IdOf(xml);

        var path = EntryPath(row.Id);
        if (!File.Exists(path))
        {
            var temporary = path + ".tmp";
            using (var gzip = new GZipStream(File.Create(temporary), CompressionLevel.Optimal))
            {
                var bytes = Encoding.UTF8.GetBytes(xml);
                gzip.Write(bytes, 0, bytes.Length);
            }
            File.Move(temporary, path);
        }

        row.Bytes = new FileInfo(path).Length;
        Index.Rows.Add(row);
        SaveIndex();
        return row;
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

    private void SaveIndex()
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

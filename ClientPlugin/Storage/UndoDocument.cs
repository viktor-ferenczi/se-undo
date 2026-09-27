using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Serialization;
using ClientPlugin.History;

namespace ClientPlugin.Storage;

// Everything the plugin persists for one world: both persisted histories and the
// grid registry. Grid and block snapshots live inside the ops as nested XML text.
public class UndoDocument
{
    public const int CurrentVersion = 1;

    public int Version = CurrentVersion;
    public UndoHistory Build = new UndoHistory();
    public UndoHistory Terminal = new UndoHistory();
    public GridRegistry Grids = new GridRegistry();
}

// XmlSerializer plus gzip. The concrete op types are passed in, so the plugin
// registers its game facing ops and the unit tests their fakes.
public sealed class UndoDocumentSerializer
{
    private readonly XmlSerializer serializer;

    public UndoDocumentSerializer(IEnumerable<Type> opTypes)
    {
        // Created once, XmlSerializer with extra types is not cached by the runtime
        serializer = new XmlSerializer(typeof(UndoDocument), opTypes.ToArray());
    }

    public byte[] Save(UndoDocument document)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal))
            serializer.Serialize(gzip, document);
        return buffer.ToArray();
    }

    // Null when the document was written by another format version. A corrupt
    // file throws; the caller treats both as an empty history.
    public UndoDocument Load(byte[] data)
    {
        using var gzip = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        var document = (UndoDocument)serializer.Deserialize(gzip);
        return document?.Version == UndoDocument.CurrentVersion ? document : null;
    }
}

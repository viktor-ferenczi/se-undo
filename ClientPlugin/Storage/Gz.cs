using System.IO;
using System.IO.Compression;
using System.Text;

namespace ClientPlugin.Storage;

// Texts that would bloat the document, like programmable block sources
public static class Gz
{
    public static byte[] Compress(string text)
    {
        if (text == null)
            return null;

        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            gzip.Write(bytes, 0, bytes.Length);
        }
        return buffer.ToArray();
    }

    public static string Decompress(byte[] data)
    {
        if (data == null)
            return null;

        using var gzip = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

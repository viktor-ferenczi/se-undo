using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ClientPlugin.History;

namespace ClientPlugin.Storage;

// Debug status file for the tests: the shape of every history after each change.
// Hand written JSON, the .NET Framework build has no JSON serializer.
public static class StatusFile
{
    public static string ToJson(
        IEnumerable<KeyValuePair<string, UndoHistory>> histories,
        string lastMessage,
        int documentBytes,
        string extraJson = null,
        string mode = null
    )
    {
        var sb = new StringBuilder();
        sb.Append("{\"mode\":").Append(Quote(mode));
        sb.Append(",\"histories\":{");
        var first = true;
        foreach (var pair in histories)
        {
            if (!first)
                sb.Append(',');
            first = false;
            sb.Append(Quote(pair.Key)).Append(':');
            AppendHistory(sb, pair.Value);
        }
        sb.Append("},\"lastMessage\":").Append(Quote(lastMessage));
        sb.Append(",\"documentBytes\":").Append(documentBytes);
        if (extraJson != null)
            sb.Append(',').Append(extraJson);
        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendHistory(StringBuilder sb, UndoHistory history)
    {
        sb.Append("{\"count\":").Append(history.Count);
        sb.Append(",\"current\":").Append(history.CurrentId);
        sb.Append(",\"locked\":").Append(history.IsLocked ? "true" : "false");
        sb.Append(",\"nodes\":[");
        var nodes = history.Nodes.Where(n => n.Id != UndoHistory.RootId).OrderBy(n => n.Id);
        sb.Append(
            string.Join(
                ",",
                nodes.Select(n =>
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "{{\"id\":{0},\"parent\":{1},\"children\":[{2}],\"label\":{3},\"unknown\":{4},\"referenceLost\":{5},\"storeRefs\":[{6}],\"barrier\":{7}}}",
                        n.Id,
                        n.ParentId,
                        string.Join(",", n.ChildIds),
                        Quote(n.Label),
                        n.UnknownResult ? "true" : "false",
                        n.ReferenceLost ? "true" : "false",
                        string.Join(",", n.StoreRefs.Select(Quote)),
                        n.Barrier ? "true" : "false"
                    )
                )
            )
        );
        sb.Append("]}");
    }

    public static string Quote(string text)
    {
        if (text == null)
            return "null";

        var sb = new StringBuilder("\"");
        foreach (var c in text)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                default:
                    if (c < ' ')
                        sb.AppendFormat("\\u{0:x4}", (int)c);
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}

using System;
using System.Collections.Generic;

namespace ClientPlugin.Text;

public readonly struct TextSnapshot
{
    public readonly string Text;
    public readonly int Caret;

    public TextSnapshot(string text, int caret)
    {
        Text = text;
        Caret = caret;
    }
}

// Text and caret snapshots of one single line text box. No game references, the
// unit tests cover it.
public sealed class TextHistory
{
    private readonly List<TextSnapshot> snapshots = new List<TextSnapshot>();
    private int index = -1;
    private DateTime lastEditUtc;

    // The current snapshot was typed and still takes edits made within the window
    private bool open;

    public int Count => snapshots.Count;

    public TextSnapshot? Current => index < 0 ? (TextSnapshot?)null : snapshots[index];

    // The text was set from outside, or the box is seen for the first time
    public void Reset(string text, int caret)
    {
        snapshots.Clear();
        snapshots.Add(new TextSnapshot(text, caret));
        index = 0;
        open = false;
    }

    public void Edit(string text, int caret, DateTime utcNow, TimeSpan window, int maxSnapshots)
    {
        if (index < 0)
        {
            Reset(text, caret);
            return;
        }
        if (snapshots[index].Text == text)
        {
            SetCaret(caret);
            return;
        }

        snapshots.RemoveRange(index + 1, snapshots.Count - index - 1);
        if (open && utcNow - lastEditUtc <= window)
            snapshots[index] = new TextSnapshot(text, caret);
        else
        {
            snapshots.Add(new TextSnapshot(text, caret));
            index++;
            // Two at least, or there is nothing to go back to
            if (snapshots.Count > Math.Max(2, maxSnapshots))
            {
                snapshots.RemoveAt(0);
                index--;
            }
        }
        open = true;
        lastEditUtc = utcNow;
    }

    // The caret moved without a text change; redo puts it back where it was left
    public void SetCaret(int caret)
    {
        if (index >= 0)
            snapshots[index] = new TextSnapshot(snapshots[index].Text, caret);
    }

    public TextSnapshot? Undo() => index <= 0 ? (TextSnapshot?)null : Step(-1);

    public TextSnapshot? Redo() => index >= snapshots.Count - 1 ? (TextSnapshot?)null : Step(1);

    private TextSnapshot Step(int direction)
    {
        open = false;
        index += direction;
        return snapshots[index];
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using ClientPlugin.History;
using ClientPlugin.Session;

namespace ClientPlugin.Record;

// Terminal changes arrive in bursts: a slider drag sets the value every frame, the
// Name box on every keystroke, a multi selection once per block. Everything of one
// kind (one control, the grid name, the program) becomes one node once the
// coalescing window passes without another change.
public sealed class TerminalStroke
{
    private sealed class Change
    {
        public string Old;
        public string New;
        public Func<string, Op> MakeOp;
    }

    public readonly string Kind;
    private readonly Dictionary<string, Change> changes = new Dictionary<string, Change>();
    private Func<int, string, string, string> label;
    private DateTime lastUtc;

    public TerminalStroke(string kind)
    {
        Kind = kind;
    }

    public bool Expired =>
        DateTime.UtcNow - lastUtc
        > TimeSpan.FromMilliseconds(Config.Current.TextCoalescingWindowMs);

    // The first change of a target in the stroke keeps its original value. The label
    // gets the number of changed targets and the old and new value of the first one.
    public void Add(
        string target,
        string oldValue,
        string newValue,
        Func<string, Op> makeOp,
        Func<int, string, string, string> describe
    )
    {
        if (!changes.TryGetValue(target, out var change))
            changes[target] = change = new Change { Old = oldValue, MakeOp = makeOp };
        change.New = newValue;
        label = describe;
        lastUtc = DateTime.UtcNow;
    }

    public void Commit()
    {
        var changed = changes.Values.Where(c => c.Old != c.New).ToList();
        if (changed.Count == 0)
            return;

        Recorder.Commit(
            label(changed.Count, changed[0].Old, changed[0].New),
            changed.Select(c => c.MakeOp(c.New)).ToList(),
            changed.Select(c => c.MakeOp(c.Old)).ToList(),
            UndoSession.Document.Terminal
        );
    }
}

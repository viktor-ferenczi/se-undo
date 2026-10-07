using System;
using ClientPlugin.Text;
using Shared.Storage;
using Xunit;

namespace UndoTests;

public class TextHistoryTests
{
    private static readonly DateTime Start = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(500);

    private static DateTime At(int ms) => Start.AddMilliseconds(ms);

    [Fact]
    public void TypingWithinTheWindowIsOneStep()
    {
        var history = new TextHistory();
        history.Reset("", 0);
        history.Edit("a", 1, At(0), Window, 100);
        history.Edit("ab", 2, At(300), Window, 100);
        // The window slides with the typing
        history.Edit("abc", 3, At(700), Window, 100);

        Assert.Equal(2, history.Count);
        Assert.Equal("", history.Undo()?.Text);
        Assert.Null(history.Undo());
        Assert.Equal("abc", history.Redo()?.Text);
        Assert.Null(history.Redo());
    }

    [Fact]
    public void APauseStartsANewStep()
    {
        var history = new TextHistory();
        history.Reset("x", 1);
        history.Edit("xa", 2, At(0), Window, 100);
        history.Edit("xab", 3, At(600), Window, 100);

        Assert.Equal("xa", history.Undo()?.Text);
        Assert.Equal("x", history.Undo()?.Text);
    }

    [Fact]
    public void TypingAfterUndoDropsTheRedoStepsAndKeepsTheUndoneText()
    {
        var history = new TextHistory();
        history.Reset("", 0);
        history.Edit("a", 1, At(0), Window, 100);
        history.Edit("ab", 2, At(1000), Window, 100);
        Assert.Equal("a", history.Undo()?.Text);

        // Right after the undo, still inside the window: "a" must not be overwritten
        history.Edit("ac", 2, At(1100), Window, 100);
        Assert.Null(history.Redo());
        Assert.Equal("a", history.Undo()?.Text);
        Assert.Equal("", history.Undo()?.Text);
    }

    [Fact]
    public void TheCapDropsTheOldestSnapshots()
    {
        var history = new TextHistory();
        history.Reset("0", 1);
        for (var i = 1; i <= 10; i++)
            history.Edit(i.ToString(), 1, At(i * 1000), Window, 4);

        Assert.Equal(4, history.Count);
        Assert.Equal("9", history.Undo()?.Text);
        Assert.Equal("8", history.Undo()?.Text);
        Assert.Equal("7", history.Undo()?.Text);
        Assert.Null(history.Undo());
    }

    [Fact]
    public void TheCaretComesBackWithTheText()
    {
        var history = new TextHistory();
        history.Reset("abc", 3);
        history.SetCaret(1);
        history.Edit("aXbc", 2, At(0), Window, 100);

        Assert.Equal(1, history.Undo()?.Caret);
        Assert.Equal(2, history.Redo()?.Caret);
    }

    [Fact]
    public void ResetForgetsEverything()
    {
        var history = new TextHistory();
        history.Reset("a", 1);
        history.Edit("ab", 2, At(0), Window, 100);
        history.Reset("other", 5);

        Assert.Null(history.Undo());
        Assert.Equal("other", history.Current?.Text);
    }

    [Fact]
    public void GzipRoundTripsASourceAndKeepsNull()
    {
        var source = "public void Main() { Echo(\"árvíztűrő\"); }\r\n";
        Assert.Equal(source, Gz.Decompress(Gz.Compress(source)));
        Assert.Null(Gz.Compress(null));
        Assert.Null(Gz.Decompress(null));
    }
}

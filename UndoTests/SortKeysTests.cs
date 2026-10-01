using System;
using System.Linq;
using ClientPlugin.GridStore;
using Xunit;

namespace UndoTests;

public class SortKeysTests
{
    private static StoreRow Row(string name, int minute, int blocks = 1) =>
        new StoreRow
        {
            MainGridName = name,
            TimestampUtc = new DateTime(2026, 10, 1, 12, minute, 0, DateTimeKind.Utc),
            BlockCount = blocks,
        };

    private static readonly StoreRow[] Rows =
    {
        Row("Rover", 0, 30),
        Row("ship", 1, 10),
        Row("Rover", 2, 20),
        Row("Ship", 3, 10),
    };

    private static string Sorted(SortKeys keys)
    {
        var rows = Rows.ToList();
        rows.Sort(keys.Compare);
        return string.Join(" ", rows.Select(r => $"{r.MainGridName}{r.TimestampUtc.Minute}"));
    }

    [Fact]
    public void StartsNewestFirst()
    {
        var keys = SortKeys.Parse(null);
        Assert.Equal("-Time", keys.ToString());
        Assert.Equal("Ship3 Rover2 ship1 Rover0", Sorted(keys));
    }

    [Fact]
    public void AClickedColumnMovesToTheFrontAndTheFirstOneFlips()
    {
        var keys = SortKeys.Parse("-Time");

        // By name, newest first inside each name
        keys.Click(GridColumn.Name);
        Assert.Equal("Name,-Time", keys.ToString());
        Assert.Equal("Rover2 Rover0 Ship3 ship1", Sorted(keys));

        keys.Click(GridColumn.Name);
        Assert.Equal("-Name,-Time", keys.ToString());
        Assert.Equal("Ship3 ship1 Rover2 Rover0", Sorted(keys));

        // Time comes back to the front with the direction it had
        keys.Click(GridColumn.Time);
        Assert.Equal("-Time,-Name", keys.ToString());

        keys.Click(GridColumn.Blocks);
        Assert.Equal("Blocks,-Time,-Name", keys.ToString());
        Assert.Equal("Ship3 ship1 Rover2 Rover0", Sorted(keys));
    }

    [Theory]
    [InlineData("", "-Time")]
    [InlineData("Nonsense,,-", "-Time")]
    [InlineData("Name, -Pcu ,Name,7", "Name,-Pcu")]
    [InlineData("Reason,Size,Grids", "Reason,Size,Grids")]
    public void ParsingDropsWhatItDoesNotKnow(string text, string expected)
    {
        Assert.Equal(expected, SortKeys.Parse(text).ToString());
    }
}

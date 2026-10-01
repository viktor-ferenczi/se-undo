using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using VRage.Game;
using VRage.Game.Entity;
using VRageMath;

namespace ClientPlugin.Ops;

// A client learns nothing about the grids its paste request created, design section
// 6. New grids are matched to the expected ones by name, block count and position
// while the match window lasts. A grid may arrive with fewer blocks than asked for:
// the server strips blocks whose DLC or skin the player lacks. ponytail: a heuristic, a server companion could
// report the created ids instead.
public sealed class PasteMatch
{
    public struct Expected
    {
        // Null matches any name, for a new grid from one block whose name the server picks
        public string Name;
        public int Blocks;
        public Vector3D Position;
    }

    private static readonly List<PasteMatch> Active = new List<PasteMatch>();

    private readonly List<Expected> expected;
    private readonly DateTime deadlineUtc;

    // Same order as the expected grids, null until matched
    public readonly MyCubeGrid[] Grids;

    public PasteMatch(List<Expected> expected)
    {
        this.expected = expected;
        Grids = new MyCubeGrid[expected.Count];
        deadlineUtc = DateTime.UtcNow.AddSeconds(Config.Current.PasteMatchWindowS);
        Active.Add(this);
    }

    public static PasteMatch For(IEnumerable<MyObjectBuilder_CubeGrid> builders) =>
        new PasteMatch(
            builders
                .Select(b => new Expected
                {
                    Name = b.DisplayName,
                    Blocks = b.CubeBlocks.Count,
                    Position = b.PositionAndOrientation.Value.Position,
                })
                .ToList()
        );

    public bool AllMatched => Grids.All(g => g != null);

    public bool Expired => DateTime.UtcNow >= deadlineUtc;

    // Matching stops once everything matched or the window is over
    public bool Done
    {
        get
        {
            var done = AllMatched || Expired;
            if (done)
                Active.Remove(this);
            return done;
        }
    }

    public static void Reset() => Active.Clear();

    // Subscribed to MyEntities.OnEntityAdd for the session
    public static void OnEntityAdd(MyEntity entity)
    {
        if (entity is not MyCubeGrid grid || Active.Count == 0)
            return;

        var tolerance = Config.Current.PasteMatchPositionToleranceM;
        foreach (var match in Active)
        {
            for (var i = 0; i < match.expected.Count; i++)
            {
                var e = match.expected[i];
                if (
                    match.Grids[i] == null
                    && (e.Name == null || e.Name == grid.DisplayName)
                    && grid.BlocksCount <= e.Blocks
                    && Vector3D.Distance(e.Position, grid.PositionComp.GetPosition()) <= tolerance
                )
                {
                    match.Grids[i] = grid;
                    return;
                }
            }
        }
    }
}

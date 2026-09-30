using System.Collections.Generic;
using System.Linq;
using ClientPlugin.GridStore;
using ClientPlugin.History;
using ClientPlugin.Ops;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Multiplayer;
using VRage;
using VRage.Game;
using VRage.ObjectBuilders.Private;
using VRageMath;

namespace ClientPlugin.Record;

// Everything needed to put removed blocks back, taken before the removal request:
// the block builders, their links to other blocks, and the grids split off afterwards
public sealed class RazeCapture : ICapture
{
    private MyCubeGrid grid;
    private List<MySlimBlock> blocks;
    private List<MyObjectBuilder_CubeBlock> builders;
    private List<BlockLinks> links;
    private SplitWatch watch;

    public bool Settled => watch.Settled;

    public void Abort() => watch.Close();

    public static RazeCapture Begin(MyCubeGrid grid, IEnumerable<Vector3I> positions)
    {
        var blocks = positions
            .Select(grid.GetCubeBlock)
            .Where(b => b != null && b.FatBlock?.IsSubBlock != true)
            .Distinct()
            .ToList();
        if (blocks.Count == 0)
            return null;

        return new RazeCapture
        {
            grid = grid,
            blocks = blocks,
            // Not the copy variant: the block comes back as itself, entity name included
            builders = blocks.Select(b => b.GetObjectBuilder()).ToList(),
            links = BlockLinks.Capture(grid, blocks),
            watch = new SplitWatch(grid),
        };
    }

    // Once the splits settled. Removing the last block closes the grid; bringing
    // that back is the grid paste op's job, so such a removal is not recorded here.
    public void Finish()
    {
        var pieces = watch.Close();
        var removed = Enumerable
            .Range(0, blocks.Count)
            .Where(i => grid.GetCubeBlock(blocks[i].Min) != blocks[i])
            .ToList();
        if (removed.Count == 0 || grid.MarkedForClose)
            return;

        var saved = GameAccess.BlocksBuilder(grid, removed.Select(i => builders[i]).ToList());

        var removedIds = new HashSet<long>(removed.Select(i => builders[i].EntityId));
        var handle = Recorder.Handle(grid);
        var splits = pieces
            .Select(piece => new SplitPiece
            {
                Grid = Recorder.Handle(piece),
                Key = piece.CubeBlocks.First().Min,
                Entry = Sync.IsServer
                    ? null
                    : StoredGroups
                        .Save(StoredGroups.Capture(new[] { piece }), StoreReason.Split)
                        .Id,
            })
            .ToList();

        var reverse = new List<Op>
        {
            new RestoreBlocksOp
            {
                Grid = handle,
                BlocksXml = BuilderXml.Write(saved),
                Links = links.Where(l => removedIds.Contains(l.EntityId)).ToList(),
            },
        };
        if (splits.Count != 0)
            reverse.Add(new MergeBackOp { Grid = handle, Pieces = splits });

        var label = $"removed {Recorder.Plural(removed.Count, "block")}";
        if (splits.Count != 0)
            label += $", {Recorder.Plural(splits.Count, "part")} split off";

        Recorder.Commit(
            label,
            new List<Op>
            {
                new RazeBlocksOp
                {
                    Grid = handle,
                    Positions = removed.Select(i => blocks[i].Min).ToList(),
                    Pieces = splits,
                },
            },
            reverse
        );
    }
}

using System.Collections.Generic;
using System.Linq;
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
public sealed class RazeCapture
{
    private MyCubeGrid grid;
    private List<MySlimBlock> blocks;
    private List<MyObjectBuilder_CubeBlock> builders;
    private List<BlockLinks> links;
    public SplitWatch Watch { get; private set; }

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
            Watch = new SplitWatch(grid),
        };
    }

    // Once the splits settled. Removing the last block closes the grid; bringing
    // that back is the grid paste op's job, so such a removal is not recorded here.
    public void Finish()
    {
        var pieces = Watch.Close();
        var removed = Enumerable
            .Range(0, blocks.Count)
            .Where(i => grid.GetCubeBlock(blocks[i].Min) != blocks[i])
            .ToList();
        if (removed.Count == 0 || grid.MarkedForClose)
            return;

        var saved = (MyObjectBuilder_CubeGrid)
            MyObjectBuilderSerializerKeen.CreateNewObject(typeof(MyObjectBuilder_CubeGrid));
        saved.DisplayName = grid.DisplayName;
        saved.GridSizeEnum = grid.GridSizeEnum;
        saved.IsStatic = grid.IsStatic;
        saved.PositionAndOrientation = new MyPositionAndOrientation(grid.WorldMatrix);
        saved.CubeBlocks = removed.Select(i => builders[i]).ToList();

        var removedIds = new HashSet<long>(removed.Select(i => builders[i].EntityId));
        var handle = Recorder.Handle(grid);
        var splits = pieces
            .Select(piece => new SplitPiece
            {
                Grid = Recorder.Handle(piece),
                Key = piece.CubeBlocks.First().Min,
                BuilderXml = Sync.IsServer ? null : BuilderXml.Write(piece.GetObjectBuilder()),
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

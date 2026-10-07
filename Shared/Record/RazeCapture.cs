using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Shared.GridStore;
using Shared.History;
using Shared.Ops;
using VRage.Game;
using VRageMath;

namespace Shared.Record;

// Everything needed to put removed blocks back, taken before the removal request:
// the block builders, their links to other blocks, and the grids split off afterwards
public sealed class RazeCapture : ICapture
{
    private MyCubeGrid grid;
    private List<MySlimBlock> blocks;
    private List<MyObjectBuilder_CubeBlock> builders;
    private List<BlockLinks> links;
    private SplitWatch watch;
    private SpillWatch spill;

    // Set when a removed block is one end of a mechanical connection: the grid's
    // group (StoredGroups.GroupOf) and its builders as they were before the removal. Undo then
    // restores the whole group, see GroupSnapshotOp.
    private List<MyCubeGrid> group;
    private List<MyObjectBuilder_CubeGrid> groupBuilders;

    public bool Settled => watch.Settled && (spill == null || spill.Settled);

    public void Abort()
    {
        watch.Close();
        spill?.Close();
    }

    public static RazeCapture Begin(MyCubeGrid grid, IEnumerable<Vector3I> positions)
    {
        var blocks = positions
            .Select(grid.GetCubeBlock)
            .Where(b => b != null && b.FatBlock?.IsSubBlock != true)
            .Distinct()
            .ToList();
        if (blocks.Count == 0)
            return null;

        var group = blocks.Any(NeedsGroupBackup) ? StoredGroups.GroupOf(grid) : null;
        return new RazeCapture
        {
            grid = grid,
            blocks = blocks,
            group = group,
            groupBuilders = group == null ? null : StoredGroups.Capture(group),
            // Not the copy variant: the block comes back as itself, entity name included
            builders = blocks.Select(b => b.GetObjectBuilder()).ToList(),
            links = BlockLinks.Capture(grid, blocks),
            watch = new SplitWatch(grid),
            spill = SpillWatch.For(blocks),
        };
    }

    // Blocks the single block restore cannot bring back the way they were, because
    // they hold another grid: the base of a rotor, hinge, piston or suspension or the
    // part on its other end, and a connector with a ship on it. Their removal is
    // undone by restoring the whole group.
    public static bool NeedsGroupBackup(MySlimBlock block) =>
        block.FatBlock is MyMechanicalConnectionBlockBase
        || block.FatBlock is MyAttachableTopBlockBase
        || block.FatBlock is MyShipConnector connector && connector.InConstraint;

    // Once the splits settled. Removing the last block closes the grid; bringing
    // that back is the grid paste op's job, so such a removal is not recorded here.
    public void Finish()
    {
        var pieces = watch.Close();
        var spilled = spill?.Close() ?? new List<long>();
        var removed = Enumerable
            .Range(0, blocks.Count)
            .Where(i => grid.GetCubeBlock(blocks[i].Min) != blocks[i])
            .ToList();
        if (removed.Count == 0)
            return;
        if (group != null)
        {
            FinishWithGroup(pieces, removed, spilled);
            return;
        }
        if (grid.MarkedForClose)
            return;

        var saved = GameAccess.BlocksBuilder(grid, removed.Select(i => builders[i]).ToList());

        var removedIds = new HashSet<long>(removed.Select(i => builders[i].EntityId));
        var handle = Recorder.Handle(grid);
        var splits = pieces
            .Select(piece => new SplitPiece
            {
                Grid = Recorder.Handle(piece),
                Key = piece.CubeBlocks.First().Min,
            })
            .ToList();

        var reverse = new List<Op>
        {
            new RestoreBlocksOp
            {
                Grid = handle,
                BlocksXml = BuilderXml.Write(saved),
                Links = links.Where(l => removedIds.Contains(l.EntityId)).ToList(),
                Spilled = spilled,
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

    // The removal took a mechanical connection apart. What it held comes loose and
    // drifts, falls or collides, so there is nothing reliable to connect again: undo
    // closes what is left of the group and creates it again from the backup taken
    // before the removal. That also covers a removal that closed the grid.
    private void FinishWithGroup(List<MyCubeGrid> pieces, List<int> removed, List<long> spilled)
    {
        var handle = Recorder.Handle(grid);
        var handles = group.Select(Recorder.Handle).ToList();
        var splits = pieces
            .Select(piece => new SplitPiece
            {
                Grid = Recorder.Handle(piece),
                Key = piece.CubeBlocks.First().Min,
            })
            .ToList();

        var label = $"removed {Recorder.Plural(removed.Count, "block")}";
        if (splits.Count != 0)
            label += $", {Recorder.Plural(splits.Count, "part")} split off";

        var forward = new List<Op>
        {
            new RazeBlocksOp
            {
                Grid = handle,
                Positions = removed.Select(i => blocks[i].Min).ToList(),
                Pieces = splits,
            },
        };
        StoredGroups.Save(
            groupBuilders,
            StoreReason.Snapshot,
            row =>
                Recorder.Commit(
                    label,
                    forward,
                    new List<Op>
                    {
                        new GroupSnapshotOp
                        {
                            Entry = row.Id,
                            Grids = handles,
                            Closing = splits.Select(s => s.Grid).ToList(),
                            Spilled = spilled,
                            Anchor = group.IndexOf(grid),
                        },
                    }
                ),
            _ => Recorder.CommitBarrier(label)
        );
    }
}

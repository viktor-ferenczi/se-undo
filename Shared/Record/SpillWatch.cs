using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game.Entity;
using VRageMath;

namespace Shared.Record;

// A block removed with the cube builder drops what its inventories hold into the
// world, as floating objects or as a container bag, within a few frames. Those lie
// where the block was and keep the game from placing it again. The watch collects
// them, so the undo of the removal can take them away first; the restored block has
// its items in the inventory again.
public sealed class SpillWatch
{
    // The spawn queue of the floating objects takes a few frames
    private const int Frames = 10;

    // A bag is put beside the block, up to this far from it
    private const double Reach = 3.0;

    private readonly List<BoundingBoxD> boxes;
    private readonly List<MyEntity> added = new List<MyEntity>();
    private readonly int openedFrame = MySession.Static.GameplayFrameCounter;
    private bool closed;

    // Null when none of the blocks holds anything
    public static SpillWatch For(IEnumerable<MySlimBlock> blocks)
    {
        var boxes = blocks
            .Select(b => b.FatBlock)
            .Where(HasItems)
            .Select(b => b.PositionComp.WorldAABB.GetInflated(Reach))
            .ToList();
        return boxes.Count == 0 ? null : new SpillWatch(boxes);
    }

    public static bool HasItems(MyCubeBlock block)
    {
        if (block == null || !block.HasInventory)
            return false;
        for (var i = 0; i < block.InventoryCount; i++)
        {
            if (block.GetInventory(i)?.ItemCount > 0)
                return true;
        }
        return false;
    }

    private SpillWatch(List<BoundingBoxD> boxes)
    {
        this.boxes = boxes;
        MyEntities.OnEntityAdd += OnEntityAdd;
    }

    // A new entity has no position yet; where it is gets looked at in Close
    private void OnEntityAdd(MyEntity entity)
    {
        if (entity is MyFloatingObject || entity is IMyInventoryBag)
            added.Add(entity);
    }

    public bool Settled => MySession.Static.GameplayFrameCounter >= openedFrame + Frames;

    // Entity ids of what appeared at the blocks since the watch began
    public List<long> Close()
    {
        if (!closed)
        {
            MyEntities.OnEntityAdd -= OnEntityAdd;
            closed = true;
        }
        var spilled = added
            .Where(e => !e.MarkedForClose && boxes.Any(box => Inside(box, e)))
            .Select(e => e.EntityId)
            .ToList();
        if (spilled.Count != 0)
            Log.Debug($"{spilled.Count} spilled items lie where the blocks were");
        return spilled;
    }

    private static bool Inside(BoundingBoxD box, MyEntity entity) =>
        box.Contains(entity.PositionComp.GetPosition()) != ContainmentType.Disjoint;

    // Takes spilled items out of the world again, those that are still there
    public static void Remove(IEnumerable<long> spilled)
    {
        foreach (var id in spilled)
        {
            if (!MyEntities.TryGetEntityById(id, out MyEntity entity) || entity.MarkedForClose)
                continue;
            // Closing takes effect at the end of the frame. The blocks are placed
            // in this one, so the body goes out of the physics world right away.
            if (entity.Physics != null)
                entity.Physics.Enabled = false;
            if (entity is MyFloatingObject floating)
                MyFloatingObjects.RemoveFloatingObject(floating, sync: true);
            else if (entity is IMyInventoryBag)
                entity.Close();
        }
    }
}

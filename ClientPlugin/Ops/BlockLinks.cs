using System.Collections.Generic;
using System.Linq;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.GameSystems;
using SpaceEngineers.Game.Entities.Blocks;
using VRageMath;

namespace ClientPlugin.Ops;

// What other blocks drop when a block closes, design section 7: its block groups,
// turret controller tool lists and event controller selections. Toolbar slots,
// rotor tops and the like keep the entity id and find the block again by themselves.
public class BlockLinks
{
    public Vector3I Min;
    public long EntityId;
    public List<string> Groups = new List<string>();
    public List<long> TurretControllers = new List<long>();
    public List<long> EventControllers = new List<long>();

    public static List<BlockLinks> Capture(MyCubeGrid grid, IEnumerable<MySlimBlock> blocks)
    {
        var result = new List<BlockLinks>();
        var terminal = grid.GridSystems.TerminalSystem;
        if (terminal == null)
            return result;

        var turretControllers = terminal.Blocks.OfType<MyTurretControlBlock>().ToList();
        var eventControllers = terminal.Blocks.OfType<MyEventControllerBlock>().ToList();
        foreach (var block in blocks.Select(b => b.FatBlock).OfType<MyTerminalBlock>())
        {
            var id = block.EntityId;
            var links = new BlockLinks
            {
                Min = block.Min,
                EntityId = id,
                Groups = terminal
                    .BlockGroups.Where(g => g.Blocks.Contains(block))
                    .Select(g => g.Name.ToString())
                    .ToList(),
                TurretControllers = turretControllers
                    .Where(c => c.m_boundTools.ContainsKey(id))
                    .Select(c => c.EntityId)
                    .ToList(),
                EventControllers = eventControllers
                    .Where(c => c.m_selectedBlocks.ContainsKey(id))
                    .Select(c => c.EntityId)
                    .ToList(),
            };
            if (
                links.Groups.Count + links.TurretControllers.Count + links.EventControllers.Count
                != 0
            )
                result.Add(links);
        }
        return result;
    }

    // Puts the links back once the block exists again under its old entity id,
    // through the same requests the terminal uses, so they replicate
    public static void Reapply(MyCubeGrid grid, IEnumerable<BlockLinks> links)
    {
        var terminal = grid.GridSystems.TerminalSystem;
        foreach (var link in links)
        {
            // Only the block restored under its old id, not whatever took the id meanwhile
            if (
                !MyEntities.TryGetEntityById(link.EntityId, out MyTerminalBlock block)
                || block.CubeGrid != grid
                || block.Min != link.Min
            )
                continue;

            foreach (var name in link.Groups)
            {
                var existing = terminal.BlockGroups.Find(g => g.Name.ToString() == name);
                if (existing != null && existing.Blocks.Contains(block))
                    continue;

                var group = new MyBlockGroup();
                group.Name.Append(name);
                if (existing != null)
                    group.Blocks.UnionWith(existing.Blocks);
                group.Blocks.Add(block);
                terminal.AddUpdateGroup(group, fireEvent: true, modify: true);
            }

            // A closed tool stays in the list with a null block, it only needs a recache
            foreach (var id in link.TurretControllers)
            {
                if (!MyEntities.TryGetEntityById(id, out MyTurretControlBlock controller))
                    continue;
                if (!controller.m_boundTools.ContainsKey(link.EntityId))
                    controller.AddTool(block as Sandbox.ModAPI.Ingame.IMyFunctionalBlock);
                else if (controller.m_boundTools[link.EntityId] == null)
                    controller.RecacheTools();
            }

            foreach (var id in link.EventControllers)
            {
                if (
                    MyEntities.TryGetEntityById(id, out MyEventControllerBlock controller)
                    && !controller.m_selectedBlocks.ContainsKey(link.EntityId)
                )
                    MyMultiplayer.RaiseEvent(
                        controller,
                        x => x.AddBlocks,
                        new List<long> { link.EntityId }
                    );
            }
        }
    }
}

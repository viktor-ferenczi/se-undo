using System;
using ClientPlugin.Apply;
using ClientPlugin.History;
using ClientPlugin.Storage;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using VRageMath;

namespace ClientPlugin.Ops;

// A terminal block by grid handle and position, design section 6. The definition is
// checked at apply time, so an op never lands on another block built in its place.
public class BlockRef
{
    public const string Missing = "the block no longer exists";

    public int Grid;
    public Vector3I Min;
    public string Definition;

    public static BlockRef From(MyTerminalBlock block, GridRegistry grids) =>
        new BlockRef
        {
            Grid = grids.GetOrAdd(block.CubeGrid.EntityId),
            Min = block.SlimBlock.Min,
            Definition = block.BlockDefinition.Id.ToString(),
        };

    public string Key => $"{Grid}:{Min.X},{Min.Y},{Min.Z}";

    public MyTerminalBlock Resolve(GridRegistry grids)
    {
        var block = grids.ResolveGrid(Grid)?.BlockAt(Min);
        return block != null && block.BlockDefinition.Id.ToString() == Definition
            ? block.FatBlock as MyTerminalBlock
            : null;
    }
}

// Sets one terminal control of one block, the block's name included (control "Name").
// SetValue is what the terminal calls, so the change syncs the normal way.
public class SetPropertyOp : Op
{
    public BlockRef Block;
    public string Control;
    public string Value;

    public override string Validate(GridRegistry grids)
    {
        var block = Block.Resolve(grids);
        if (block == null)
            return BlockRef.Missing;
        if (TerminalValues.Find(block, Control) == null)
            return $"the block has no {Control} setting";
        return block.CanLocalPlayerChangeValue() ? null : Permissions.NotYourBlock;
    }

    public override Func<bool> Apply(GridRegistry grids)
    {
        var block = Block.Resolve(grids);
        TerminalValues.Write(TerminalValues.Find(block, Control), block, Value);
        return null;
    }
}

public class SetGridNameOp : Op
{
    public int Grid;
    public string Name;

    public override string Validate(GridRegistry grids)
    {
        var grid = grids.ResolveGrid(Grid);
        return grid == null ? GameAccess.GridMissing : null;
    }

    public override Func<bool> Apply(GridRegistry grids)
    {
        var grid = grids.ResolveGrid(Grid);
        grid.ChangeDisplayNameRequest(Name);
        return null;
    }
}

// Puts a program back and recompiles it. The program's fields start over, which the
// design accepts. Storage carries over from the replaced program: every recompile
// hands it to the new instance.
public class SetProgramOp : Op
{
    public BlockRef Block;

    // Gzip compressed source, null for a block that never had a program
    public byte[] Source;

    public override string Validate(GridRegistry grids)
    {
        if (!(Block.Resolve(grids) is MyProgrammableBlock block))
            return BlockRef.Missing;
        if (!block.CanLocalPlayerChangeValue())
            return Permissions.NotYourBlock;
        return Permissions.IsScripter ? null : Permissions.NeedsScripter;
    }

    public override Func<bool> Apply(GridRegistry grids)
    {
        var block = (MyProgrammableBlock)Block.Resolve(grids);
        var source = Gz.Decompress(Source);

        // The setter recompiles on a server without telling the clients. A host sends
        // the editor's request instead, which runs here and is broadcast.
        // ponytail: a block going back to no program at all stays stale on the
        // joined clients, the request cannot carry "no program"
        if (source != null && Permissions.Mode == SessionMode.LobbyHost)
            block.SendUpdateProgramRequest(source);
        else
            ((Sandbox.ModAPI.IMyProgrammableBlock)block).ProgramData = source;
        return null;
    }
}

// Puts a block's Custom Data back through the property the Custom Data dialog and
// the mod API set, which syncs it
public class SetCustomDataOp : Op
{
    public BlockRef Block;

    // Gzip compressed text, it can be tens of kilobytes
    public byte[] Data;

    public override string Validate(GridRegistry grids)
    {
        var block = Block.Resolve(grids);
        if (block == null)
            return BlockRef.Missing;
        return block.CanLocalPlayerChangeValue() ? null : Permissions.NotYourBlock;
    }

    public override Func<bool> Apply(GridRegistry grids)
    {
        Block.Resolve(grids).CustomData = Gz.Decompress(Data) ?? "";
        return null;
    }
}

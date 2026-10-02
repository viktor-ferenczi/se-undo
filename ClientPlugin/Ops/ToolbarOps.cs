using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ClientPlugin.Apply;
using ClientPlugin.History;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Screens.Helpers;
using VRage.Game;

namespace ClientPlugin.Ops;

// The toolbars a block holds: a cockpit's, a timer's, a button panel's and so on.
// A block class keeps each in a field; the field's name tells them apart where a
// block has more than one (a ship controller has its build toolbar too).
public static class BlockToolbars
{
    private static readonly Dictionary<Type, FieldInfo[]> Fields =
        new Dictionary<Type, FieldInfo[]>();

    private static FieldInfo[] FieldsOf(Type blockType)
    {
        lock (Fields)
        {
            if (Fields.TryGetValue(blockType, out var fields))
                return fields;

            var found = new List<FieldInfo>();
            for (var type = blockType; type != null && type != typeof(object); type = type.BaseType)
                found.AddRange(
                    type.GetFields(
                            BindingFlags.Instance
                                | BindingFlags.Public
                                | BindingFlags.NonPublic
                                | BindingFlags.DeclaredOnly
                        )
                        .Where(f => f.FieldType == typeof(MyToolbar))
                );
            return Fields[blockType] = found.ToArray();
        }
    }

    // Null when the toolbar is not one of the block's own fields
    public static string NameOf(MyTerminalBlock block, MyToolbar toolbar) =>
        FieldsOf(block.GetType()).FirstOrDefault(f => f.GetValue(block) == toolbar)?.Name;

    public static MyToolbar Find(MyTerminalBlock block, string name) =>
        FieldsOf(block.GetType()).FirstOrDefault(f => f.Name == name)?.GetValue(block) as MyToolbar;

    // One slot as text: a toolbar builder holding that slot alone, the way a save
    // has it, or an empty text for an empty slot
    public static string Write(MyToolbarItem item)
    {
        if (item == null)
            return "";

        var builder = new MyObjectBuilder_Toolbar();
        builder.Slots.Add(
            new MyObjectBuilder_Toolbar.Slot { Index = 0, Data = item.GetObjectBuilder() }
        );
        return BuilderXml.Write(builder);
    }

    public static MyToolbarItem Read(string text) =>
        string.IsNullOrEmpty(text)
            ? null
            : MyToolbarItemFactory.CreateToolbarItem(
                BuilderXml.Read<MyObjectBuilder_Toolbar>(text).Slots[0].Data
            );
}

// Sets one slot of a block's toolbar, through the call the toolbar screen makes
public class SetToolbarSlotOp : Op
{
    public BlockRef Block;

    // Name of the block's toolbar field, see BlockToolbars
    public string Toolbar;
    public int Index;
    public bool Gamepad;

    // BlockToolbars.Write of the item, empty for an empty slot
    public string Item;

    public override string Validate(GridRegistry grids)
    {
        var block = Block.Resolve(grids);
        if (block == null)
            return BlockRef.Missing;
        if (BlockToolbars.Find(block, Toolbar) == null)
            return "the block has no such toolbar";
        return block.CanLocalPlayerChangeValue() ? null : Permissions.NotYourBlock;
    }

    public override Func<bool> Apply(GridRegistry grids)
    {
        var toolbar = BlockToolbars.Find(Block.Resolve(grids), Toolbar);
        toolbar.SetItemAtIndex(Index, BlockToolbars.Read(Item), Gamepad);
        return null;
    }
}

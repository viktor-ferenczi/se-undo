using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using ClientPlugin.History;
using ClientPlugin.Ops;
using ClientPlugin.Session;
using HarmonyLib;
using Sandbox;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Gui;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.Screens.Terminal.Controls;
using Sandbox.Game.World;

namespace ClientPlugin.Record;

// Record hooks of the terminal context, design section 5
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class TerminalContextPatches
{
    // Nesting of setter calls on this thread: the Name box's setter calls
    // SetCustomName, which is hooked too. Only the outermost call is the player's change.
    [ThreadStatic]
    private static int depth;

    // Controls whose setter is wrapped already. Weak, the factory drops its controls
    // when a session unloads and creates new ones in the next.
    private static readonly ConditionalWeakTable<object, object> Wrapped =
        new ConditionalWeakTable<object, object>();

    private static readonly MethodInfo WrapMethod = typeof(TerminalContextPatches).GetMethod(
        nameof(Wrap),
        BindingFlags.NonPublic | BindingFlags.Static
    );

    // Hooks the value controls of every terminal block type that has them. Each
    // control holds its setter as a delegate (MyTerminalValueControl.Setter), which
    // SetValue of every control class ends in; the hook replaces that delegate with
    // one that records around the original. No Harmony patch is involved: a patch on
    // the generic SetValue methods got lost after about 30 calls, when the runtime
    // compiled the method again (SE1-0079).
    // The game creates the controls of a type with its first block, and the factory
    // forgets them when a session unloads, so this runs at every session start, after
    // grids arrived, after a build and when the terminal opens (ControlsMayHaveChanged);
    // a multiplayer client starts with no grids at all. It wraps each control once.
    // A type without controls is left alone: asking the factory for its controls
    // registers an empty list, and the game then never creates the real ones.
    private static bool controlsChanged;
    private static DateTime lastPatchUtc;

    public static void ControlsMayHaveChanged() => controlsChanged = true;

    // Every frame; scans at most once a second, and only after something changed
    public static void Update()
    {
        if (!controlsChanged || (DateTime.UtcNow - lastPatchUtc).TotalSeconds < 1)
            return;
        PatchControls();
    }

    [HarmonyPatch(typeof(MyGuiScreenTerminal), nameof(MyGuiScreenTerminal.Show))]
    private static class TerminalShowPatch
    {
        private static void Postfix() => ControlsMayHaveChanged();
    }

    public static void PatchControls()
    {
        controlsChanged = false;
        lastPatchUtc = DateTime.UtcNow;
        var watch = Stopwatch.StartNew();

        var blockTypes = MyDefinitionManager
            .Static.GetAllDefinitions()
            .OfType<MyCubeBlockDefinition>()
            .Select(d => MyCubeBlockFactory.GetProducedType(d.Id.TypeId))
            .Where(t => t != null && typeof(MyTerminalBlock).IsAssignableFrom(t))
            .Distinct()
            .ToList();

        var controls = 0;
        var wrapped = 0;
        foreach (var blockType in blockTypes)
        {
            try
            {
                if (!MyTerminalControlFactory.AreControlsCreated(blockType))
                    continue;
                foreach (var control in MyTerminalControlFactory.GetControls(blockType))
                {
                    var arguments = TerminalValues.ValueControlArguments(control.GetType());
                    if (arguments == null || TerminalValues.AccessorOf(control) == null)
                        continue;

                    controls++;
                    if (Wrapped.TryGetValue(control, out _))
                        continue;
                    if (
                        (bool)
                            WrapMethod
                                .MakeGenericMethod(arguments)
                                .Invoke(null, new object[] { control })
                    )
                    {
                        Wrapped.Add(control, null);
                        wrapped++;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warning($"Hooking the terminal controls of {blockType.Name} failed: {e}");
            }
        }
        var message =
            $"Terminal controls: {blockTypes.Count} block types, {controls} value controls, "
            + $"{wrapped} setters hooked in {watch.ElapsedMilliseconds} ms";
        if (wrapped != 0)
            Log.Info(message);
        else
            Log.Debug(message);
    }

    // False while the control has no setter yet; the next scan tries again
    private static bool Wrap<TBlock, TValue>(MyTerminalValueControl<TBlock, TValue> control)
        where TBlock : MyTerminalBlock
    {
        var original = control.Setter;
        if (original == null)
            return false;

        control.Setter = (block, value) =>
        {
            var before = BeforeSet(control, block);
            try
            {
                original(block, value);
            }
            finally
            {
                AfterSet(control, block, before);
            }
        };
        return true;
    }

    // The value before the change, null when the change is not to be recorded
    private static string BeforeSet(ITerminalControl control, MyTerminalBlock block)
    {
        if (depth++ != 0 || !Recorder.CanRecordTerminal || block == null)
            return null;

        // With the terminal open, only the blocks it shows: a script setting some
        // other block's property meanwhile is not the player's change
        if (
            !Config.Current.RecordTerminalChangesOutsideTerminal
            && Array.IndexOf(control.TargetBlocks, block) < 0
        )
            return null;

        return Read(control, block);
    }

    private static void AfterSet(ITerminalControl control, MyTerminalBlock block, string before)
    {
        depth--;
        if (before == null)
            return;

        var value = Read(control, block);
        if (value != null)
            Recorder.RecordProperty(block, control.Id, before, value);
    }

    private static string Read(ITerminalControl control, MyTerminalBlock block)
    {
        try
        {
            return TerminalValues.Read(control, block);
        }
        catch (Exception e)
        {
            Log.Debug($"Reading {control.Id} of {block.DisplayNameText} failed: {e.Message}");
            return null;
        }
    }

    // Renames that do not come through the Name control
    [HarmonyPatch(typeof(MyTerminalBlock), nameof(MyTerminalBlock.SetCustomName), typeof(string))]
    private static class SetCustomNamePatch
    {
        private static void Prefix(MyTerminalBlock __instance, string text) =>
            RecordName(__instance, text);
    }

    [HarmonyPatch(
        typeof(MyTerminalBlock),
        nameof(MyTerminalBlock.SetCustomName),
        typeof(StringBuilder)
    )]
    private static class SetCustomNameBuilderPatch
    {
        private static void Prefix(MyTerminalBlock __instance, StringBuilder text) =>
            RecordName(__instance, text?.ToString());
    }

    private static void RecordName(MyTerminalBlock block, string name)
    {
        if (depth == 0 && Recorder.CanRecordTerminal)
            Recorder.RecordProperty(block, "Name", block.CustomName.ToString(), name ?? "");
    }

    // The Custom Data dialog, the mod API and scripts all set this property. What
    // arrives from the server goes past the setter and is not recorded.
    [HarmonyPatch(typeof(MyTerminalBlock), nameof(MyTerminalBlock.CustomData), MethodType.Setter)]
    private static class CustomDataPatch
    {
        private static void Prefix(MyTerminalBlock __instance, string value)
        {
            if (Recorder.CanRecordTerminal)
                Recorder.RecordCustomData(__instance, __instance.CustomData, value);
        }
    }

    // A slot of a block's toolbar set or cleared: the toolbar screen's drag and drop
    // and its right click end here. The character's own toolbar has no block as
    // its owner and is left alone.
    [HarmonyPatch(
        typeof(MyToolbar),
        nameof(MyToolbar.SetItemAtIndex),
        typeof(int),
        typeof(MyToolbarItem),
        typeof(bool)
    )]
    private static class ToolbarSlotPatch
    {
        private static void Prefix(MyToolbar __instance, int i, bool gamepad, out string __state)
        {
            __state = CanRecordToolbar(__instance) ? Slot(__instance, i, gamepad) : null;
        }

        private static void Postfix(MyToolbar __instance, int i, bool gamepad, string __state)
        {
            if (__state == null)
                return;

            var block = (MyTerminalBlock)__instance.Owner;
            var toolbar = BlockToolbars.NameOf(block, __instance);
            var item = Slot(__instance, i, gamepad);
            if (toolbar != null && item != null && item != __state)
                Recorder.RecordToolbar(block, toolbar, i, gamepad, __state, item);
        }

        // Only while the toolbar screen is open, which is where a player changes a
        // block's toolbar. The game sets slots by itself too: it fills a cockpit's
        // toolbar when someone sits down.
        private static bool CanRecordToolbar(MyToolbar toolbar) =>
            toolbar.Owner is MyTerminalBlock
            && UndoSession.Document != null
            && !Replay.Active
            && Config.Current.EnableTerminalContext
            && MyGuiScreenToolbarConfigBase.Static != null
            && Thread.CurrentThread == MySandboxGame.Static.UpdateThread;

        private static string Slot(MyToolbar toolbar, int index, bool gamepad)
        {
            try
            {
                return BlockToolbars.Write(
                    gamepad ? toolbar.GetItemAtIndexGamepad(index) : toolbar.GetItemAtIndex(index)
                );
            }
            catch (Exception e)
            {
                Log.Debug($"Reading a toolbar slot failed: {e.Message}");
                return null;
            }
        }
    }

    [HarmonyPatch(typeof(MyCubeGrid), nameof(MyCubeGrid.ChangeDisplayNameRequest))]
    private static class GridNamePatch
    {
        private static void Prefix(MyCubeGrid __instance, string displayName)
        {
            if (Recorder.CanRecordTerminal)
                Recorder.RecordGridName(__instance, __instance.DisplayName, displayName);
        }
    }

    // The editor's OK button and the "save changes?" question both end here, on a
    // client and where the server is local. The block already holds the new source
    // by the time it sends the request or recompiles, so this is the last point
    // where the old one can be read.
    [HarmonyPatch(typeof(MyProgrammableBlock), nameof(MyProgrammableBlock.SaveCode), new Type[0])]
    private static class SaveCodePatch
    {
        private static void Prefix(MyProgrammableBlock __instance)
        {
            var editor = __instance.m_editorScreen;
            if (editor == null || editor.TextTooLong() || !Recorder.CanRecordTerminal)
                return;

            Recorder.RecordProgram(
                __instance,
                __instance.m_programData,
                editor.Description.Text.ToString()
            );
        }
    }

    // The mod API setter, which the plugin's own replay uses too
    [HarmonyPatch(
        typeof(MyProgrammableBlock),
        "Sandbox.ModAPI.IMyProgrammableBlock.set_ProgramData"
    )]
    private static class ProgramDataPatch
    {
        private static void Prefix(MyProgrammableBlock __instance, string value)
        {
            if (Recorder.CanRecordTerminal)
                Recorder.RecordProgram(__instance, __instance.m_programData, value);
        }
    }
}

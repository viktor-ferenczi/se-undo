using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using ClientPlugin.Ops;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Gui;

namespace ClientPlugin.Record;

// Record hooks of the terminal context, design section 5
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class TerminalContextPatches
{
    // Nesting of SetValue calls on this thread: the checkbox and combo box overrides
    // call the base method, and the Name box calls SetCustomName. Only the outermost
    // call is the player's change.
    [ThreadStatic]
    private static int depth;

    private static readonly HashSet<RuntimeMethodHandle> Patched =
        new HashSet<RuntimeMethodHandle>();

    // Patches MyTerminalValueControl<TBlock, TValue>.SetValue and its overrides for
    // the controls of every terminal block type that has them. The game creates the
    // controls of a type with its first block, and the factory forgets them when a
    // session unloads, so this runs at every session start, after grids arrived, after
    // a build and when the terminal opens (ControlsMayHaveChanged); a multiplayer
    // client starts with no grids at all. It patches each method once.
    // A type without controls is left alone: asking the factory for its controls
    // registers an empty list, and the game then never creates the real ones.
    // On .NET 10 all instantiations over reference types share one method handle,
    // so one patch per control class covers every block type, modded ones included.
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
        var harmony = new Harmony(Plugin.Name);
        var prefix = new HarmonyMethod(typeof(SetValuePatch), nameof(SetValuePatch.Prefix));
        var postfix = new HarmonyMethod(typeof(SetValuePatch), nameof(SetValuePatch.Postfix));
        var finalizer = new HarmonyMethod(typeof(SetValuePatch), nameof(SetValuePatch.Finalizer));

        var blockTypes = MyDefinitionManager
            .Static.GetAllDefinitions()
            .OfType<MyCubeBlockDefinition>()
            .Select(d => MyCubeBlockFactory.GetProducedType(d.Id.TypeId))
            .Where(t => t != null && typeof(MyTerminalBlock).IsAssignableFrom(t))
            .Distinct()
            .ToList();

        var controls = 0;
        var patched = 0;
        foreach (var blockType in blockTypes)
        {
            try
            {
                if (!MyTerminalControlFactory.AreControlsCreated(blockType))
                    continue;
                foreach (var control in MyTerminalControlFactory.GetControls(blockType))
                {
                    var accessor = TerminalValues.AccessorOf(control);
                    if (accessor == null)
                        continue;

                    // The method as its declaring class has it, not as seen from a subclass
                    controls++;
                    var method = accessor.Set.DeclaringType.GetMethod(
                        "SetValue",
                        accessor.Set.GetParameters().Select(p => p.ParameterType).ToArray()
                    );
                    if (!Patched.Add(method.MethodHandle))
                        continue;

                    harmony.Patch(method, prefix, postfix, finalizer: finalizer);
                    patched++;
                    Log.Debug($"Patched {method.DeclaringType}.SetValue");
                }
            }
            catch (Exception e)
            {
                // Harmony may refuse a method it sees as patched already; the hooks
                // count the nesting, so a method patched twice still records once
                Log.Warning($"Patching the terminal controls of {blockType.Name} failed: {e}");
            }
        }
        var message =
            $"Terminal controls: {blockTypes.Count} block types, {controls} value controls, "
            + $"{patched} methods patched in {watch.ElapsedMilliseconds} ms";
        if (patched != 0)
            Log.Info(message);
        else
            Log.Debug(message);
    }

    private static class SetValuePatch
    {
        public static void Prefix(object __instance, object __0, out string __state)
        {
            __state = null;
            if (depth++ != 0 || !Recorder.CanRecordTerminal)
                return;
            if (!(__instance is ITerminalControl control) || !(__0 is MyTerminalBlock block))
                return;

            // With the terminal open, only the blocks it shows: a script setting some
            // other block's property meanwhile is not the player's change
            if (
                !Config.Current.RecordTerminalChangesOutsideTerminal
                && Array.IndexOf(control.TargetBlocks, block) < 0
            )
                return;

            __state = Read(control, block);
        }

        public static void Postfix(object __instance, object __0, string __state)
        {
            if (__state == null)
                return;

            var control = (ITerminalControl)__instance;
            var block = (MyTerminalBlock)__0;
            var value = Read(control, block);
            if (value != null)
                Recorder.RecordProperty(block, control.Id, __state, value);
        }

        public static Exception Finalizer(Exception __exception)
        {
            depth--;
            return __exception;
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

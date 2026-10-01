using System.Diagnostics.CodeAnalysis;
using ClientPlugin.Apply;
using ClientPlugin.History;
using ClientPlugin.Session;
using ClientPlugin.Text;
using HarmonyLib;
using Sandbox.Engine;
using Sandbox.Game;
using Sandbox.Game.Gui;
using Sandbox.Graphics.GUI;
using VRage.Input;
using VRage.Utils;

namespace ClientPlugin.Input;

// Key handling of the three contexts, design section 3. The plugin's bindings are
// checked only while their context is active.
[SuppressMessage("ReSharper", "UnusedMember.Local")]
public static class InputPatches
{
    // Key taken by the plugin in this frame of gameplay input handling, and the
    // displaced vanilla control a replacement binding stands in for
    private static MyKeys consumedKey = MyKeys.None;
    private static MyStringId? replacedControl;

    // Returns the key of the binding it acted on, or None
    private static MyKeys HandleUndoRedo(UndoHistory history, GridRegistry grids)
    {
        var input = MyInput.Static;
        var config = Config.Current;
        if (config.UndoBinding.HasPressed(input))
        {
            Executor.Undo(history, grids);
            return config.UndoBinding.Key;
        }
        if (config.RedoBinding.HasPressed(input))
        {
            Executor.Redo(history, grids);
            return config.RedoBinding.Key;
        }
        return MyKeys.None;
    }

    [HarmonyPatch(typeof(MyGuiScreenGamePlay), nameof(MyGuiScreenGamePlay.HandleUnhandledInput))]
    private static class GamePlayPatch
    {
        private static void Prefix()
        {
            var document = UndoSession.Document;
            var config = Config.Current;
            if (
                document == null
                || MyGuiScreenGamePlay.ActiveGameplayScreen != null
                || !config.EnableBuildContext
            )
                return;

            consumedKey = HandleUndoRedo(document.Build, document.Grids);
            if (consumedKey != MyKeys.None)
                return;

            if (config.RelativeDampenersBinding.HasPressed(MyInput.Static))
            {
                consumedKey = config.RelativeDampenersBinding.Key;
                replacedControl = MyControlsSpace.DAMPING_RELATIVE;
            }
            else if (config.ToggleAllReactorsBinding.HasPressed(MyInput.Static))
            {
                consumedKey = config.ToggleAllReactorsBinding.Key;
                replacedControl = MyControlsSpace.TOGGLE_REACTORS_ALL;
            }
        }

        // A finalizer, so the key is released even if the vanilla handler throws
        private static void Finalizer()
        {
            consumedKey = MyKeys.None;
            replacedControl = null;
        }
    }

    // The vanilla code after the check (sound, SwitchDamping, SetDampeningEntity,
    // SwitchReactors, input recording) runs unchanged, only the answer is rewritten.
    [HarmonyPatch(typeof(MyControllerHelper), nameof(MyControllerHelper.IsControl))]
    private static class IsControlPatch
    {
        private static void Postfix(
            MyStringId controlId,
            MyControlStateType type,
            ref bool __result
        )
        {
            if (consumedKey == MyKeys.None)
                return;

            if (controlId == replacedControl)
            {
                if (type == MyControlStateType.NEW_PRESSED)
                    __result = true;
                return;
            }

            // Every other control on the taken key stays quiet, including plain Z
            // (dampeners) which would otherwise fire on Ctrl-Shift-Z
            var control = MyInput.Static.GetGameControl(controlId);
            if (
                control != null
                && (
                    control.GetKeyboardControl() == consumedKey
                    || control.GetSecondKeyboardControl() == consumedKey
                )
            )
                __result = false;
        }
    }

    // Vanilla MyDX9Gui.HandleInput toggles the render profiler on H with any Ctrl held,
    // and this is its only caller. In gameplay the grid history binding (Ctrl-H by
    // default) takes the key; Ctrl-Shift-H and the other combinations still reach it.
    [HarmonyPatch(typeof(MyGeneralStats), nameof(MyGeneralStats.ToggleProfiler))]
    private static class ProfilerTogglePatch
    {
        private static bool Prefix()
        {
            var config = Config.Current;
            var gridHistory =
                UndoSession.Document != null
                && config.EnableBuildContext
                && MyScreenManager.GetScreenWithFocus() is MyGuiScreenGamePlay
                && MyGuiScreenGamePlay.ActiveGameplayScreen == null
                && config.GridHistoryBinding.HasPressed(MyInput.Static);
            return !gridHistory;
        }
    }

    [HarmonyPatch(typeof(MyGuiScreenTerminal), nameof(MyGuiScreenTerminal.HandleUnhandledInput))]
    private static class TerminalPatch
    {
        private static void Prefix(MyGuiScreenTerminal __instance)
        {
            // A focused text box has its own context and consumed the key already
            var document = UndoSession.Document;
            if (
                document == null
                || !Config.Current.EnableTerminalContext
                || __instance.FocusedControl is MyGuiControlTextbox
            )
                return;

            HandleUndoRedo(document.Terminal, document.Grids);
        }
    }

    [HarmonyPatch(typeof(MyGuiControlTextbox), nameof(MyGuiControlTextbox.HandleInput))]
    private static class TextboxPatch
    {
        private static bool Prefix(MyGuiControlTextbox __instance, ref MyGuiControlBase __result)
        {
            if (!Config.Current.EnableTextContext || !__instance.HasFocus)
                return true;

            var grids = UndoSession.Document?.Grids ?? new GridRegistry();
            if (HandleUndoRedo(TextHistories.For(__instance), grids) == MyKeys.None)
                return true;

            __result = __instance;
            return false;
        }
    }
}

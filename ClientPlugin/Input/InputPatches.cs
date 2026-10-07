using System.Diagnostics.CodeAnalysis;
using ClientPlugin.Companion;
using ClientPlugin.Session;
using ClientPlugin.Text;
using HarmonyLib;
using Sandbox.Engine;
using Sandbox.Game;
using Sandbox.Game.Gui;
using Sandbox.Graphics.GUI;
using Shared.Apply;
using Shared.Companion;
using Shared.History;
using Shared.Session;
using VRage.Input;
using VRage.Utils;

namespace ClientPlugin.Input;

// Key handling of the build and terminal contexts, design section 3; the text
// context is in Text/TextHistories. The plugin's bindings are checked only while
// their context is active.
[SuppressMessage("ReSharper", "UnusedMember.Local")]
public static class InputPatches
{
    // Key taken by the plugin in this frame of gameplay input handling, and the
    // displaced vanilla control a replacement binding stands in for
    private static MyKeys consumedKey = MyKeys.None;
    private static MyStringId? replacedControl;

    // Returns the key of the binding it acted on, or None. On a client of a server
    // the step goes to the server's companion, which keeps the history.
    // In gameplay the undo key is left to the game while there is nothing to undo:
    // a player who is flying and not editing gets the vanilla action of the key,
    // relative dampeners on Ctrl-Z.
    private static MyKeys HandleUndoRedo(StepContext context, bool leaveIdleUndoToTheGame = false)
    {
        var input = MyInput.Static;
        var config = Config.Current;
        var document = UndoSession.Document;
        var history = context == StepContext.Build ? document?.Build : document?.Terminal;
        if (config.UndoBinding.HasPressed(input))
        {
            if (leaveIdleUndoToTheGame && NothingToUndo(history))
                return MyKeys.None;
            Step(context, history, undo: true);
            return config.UndoBinding.Key;
        }
        if (config.RedoBinding.HasPressed(input))
        {
            Step(context, history, undo: false);
            return config.RedoBinding.Key;
        }
        return MyKeys.None;
    }

    private static bool NothingToUndo(UndoHistory history) =>
        history == null ? CompanionClient.BuildIdle : Executor.NothingToUndo(history);

    private static void Step(StepContext context, UndoHistory history, bool undo)
    {
        if (history == null)
            CompanionClient.Step(context, undo);
        else if (undo)
            Executor.Undo(history, UndoSession.Document.Grids);
        else
            Executor.Redo(history, UndoSession.Document.Grids);
    }

    [HarmonyPatch(typeof(MyGuiScreenGamePlay), nameof(MyGuiScreenGamePlay.HandleUnhandledInput))]
    private static class GamePlayPatch
    {
        private static void Prefix()
        {
            var config = Config.Current;
            if (
                !UndoSession.Active
                || MyGuiScreenGamePlay.ActiveGameplayScreen != null
                || !config.EnableBuildContext
            )
                return;

            consumedKey = HandleUndoRedo(StepContext.Build, true);
            if (consumedKey != MyKeys.None)
                return;

            if (config.GridHistoryBinding.HasPressed(MyInput.Static))
            {
                consumedKey = config.GridHistoryBinding.Key;
                Gui.GridHistoryScreen.Open();
                return;
            }

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
                UndoSession.Active
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
            // With the cursor in a text field the keys are that field's own: the
            // plugin's text history in a single line box, the vanilla undo in a
            // multi line one. The terminal history takes them once focus is elsewhere,
            // or also from a single line box when the separate text undo option is off.
            if (
                !UndoSession.Active
                || !Config.Current.EnableTerminalContext
                || __instance.FocusedControl is MyGuiControlTextbox
                    && !TextHistories.HandedToTerminal
                || __instance.FocusedControl is MyGuiControlMultilineEditableText
            )
                return;

            HandleUndoRedo(StepContext.Terminal);
        }
    }
}

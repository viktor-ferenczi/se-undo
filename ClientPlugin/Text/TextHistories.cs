using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Sandbox.Graphics.GUI;
using VRage.Input;

namespace ClientPlugin.Text;

// Text context, design section 3: one transient history per single line text box,
// which dies with the control. MyGuiControlMultilineEditableText (the PB editor) is
// another class and keeps its vanilla undo.
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class TextHistories
{
    private static readonly ConditionalWeakTable<MyGuiControlTextbox, TextHistory> Histories =
        new ConditionalWeakTable<MyGuiControlTextbox, TextHistory>();

    private static readonly StringBuilder Buffer = new StringBuilder();

    // The caret setter is private
    private static readonly Action<MyGuiControlTextbox, int> SetCaret = AccessTools.MethodDelegate<
        Action<MyGuiControlTextbox, int>
    >(
        AccessTools.PropertySetter(
            typeof(MyGuiControlTextbox),
            nameof(MyGuiControlTextbox.CarriagePositionIndex)
        )
    );

    // Set while a snapshot is put back, which raises the change event too
    private static bool restoring;

    // A text box used the undo or redo key in this frame
    private static bool keyTaken;

    public static bool TakeKey()
    {
        var taken = keyTaken;
        keyTaken = false;
        return taken;
    }

    private static string TextOf(MyGuiControlTextbox textbox)
    {
        Buffer.Clear();
        textbox.GetText(Buffer);
        return Buffer.ToString();
    }

    private static void Restore(MyGuiControlTextbox textbox, TextSnapshot snapshot)
    {
        restoring = true;
        try
        {
            // Raises TextChanged, so whatever listens to the box follows the undo
            textbox.SetText(new StringBuilder(snapshot.Text));
            SetCaret(textbox, snapshot.Caret);
        }
        finally
        {
            restoring = false;
        }
    }

    [HarmonyPatch(typeof(MyGuiControlTextbox), nameof(MyGuiControlTextbox.HandleInput))]
    private static class HandleInputPatch
    {
        private static bool Prefix(MyGuiControlTextbox __instance, ref MyGuiControlBase __result)
        {
            var config = Config.Current;
            if (!config.EnableTextContext || !__instance.HasFocus)
                return true;

            // The text before the first edit; also a text that changed unseen
            var history = Histories.GetOrCreateValue(__instance);
            var text = TextOf(__instance);
            if (history.Current?.Text != text)
                history.Reset(text, __instance.CarriagePositionIndex);

            var input = MyInput.Static;
            var undo = config.UndoBinding.HasPressed(input);
            if (!undo && !config.RedoBinding.HasPressed(input))
                return true;

            // With nothing to go back to here, the key is left for the screen's own
            // context. The terminal opens with its search box focused, and Ctrl-Z after
            // flipping a switch there has to reach the terminal history.
            var snapshot = undo ? history.Undo() : history.Redo();
            if (snapshot == null)
                return true;

            Restore(__instance, snapshot.Value);
            keyTaken = true;
            __result = __instance;
            return false;
        }

        // Pasting and the arrow keys move the caret after the change event
        private static void Postfix(MyGuiControlTextbox __instance)
        {
            if (
                Config.Current.EnableTextContext
                && __instance.HasFocus
                && Histories.TryGetValue(__instance, out var history)
            )
                history.SetCaret(__instance.CarriagePositionIndex);
        }
    }

    // Every text change ends here: typing, paste, cut, SetText and the Text setter.
    // It is what raises TextChanged.
    [HarmonyPatch(typeof(MyGuiControlTextbox), "OnTextChangedInternal")]
    private static class TextChangedPatch
    {
        private static void Postfix(MyGuiControlTextbox __instance)
        {
            var config = Config.Current;
            if (restoring || !config.EnableTextContext)
                return;

            var text = TextOf(__instance);
            var caret = __instance.CarriagePositionIndex;
            if (__instance.HasFocus)
                Histories
                    .GetOrCreateValue(__instance)
                    .Edit(
                        text,
                        caret,
                        DateTime.UtcNow,
                        TimeSpan.FromMilliseconds(config.TextCoalescingWindowMs),
                        config.MaxNodesText
                    );
            // Set by the screen while the player is elsewhere: a new starting point
            else if (Histories.TryGetValue(__instance, out var history))
                history.Reset(text, caret);
        }
    }
}

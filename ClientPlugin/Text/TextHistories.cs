using System.Runtime.CompilerServices;
using ClientPlugin.History;
using Sandbox.Graphics.GUI;

namespace ClientPlugin.Text;

// One transient history per single line text box, it dies with the control.
// Recording text snapshots into it is the text context slice.
public static class TextHistories
{
    private static readonly ConditionalWeakTable<MyGuiControlTextbox, UndoHistory> Histories =
        new ConditionalWeakTable<MyGuiControlTextbox, UndoHistory>();

    public static UndoHistory For(MyGuiControlTextbox textbox)
    {
        var history = Histories.GetValue(textbox, _ => new UndoHistory());
        history.MaxNodes = Config.Current.MaxNodesText;
        return history;
    }
}

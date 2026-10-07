using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ClientPlugin.Companion;
using ClientPlugin.GridStore;
using ClientPlugin.Session;
using ClientPlugin.Settings;
using Sandbox;
using Sandbox.Game.Gui;
using Sandbox.Game.SessionComponents.Clipboard;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using Shared;
using Shared.GridStore;
using Shared.Session;
using Shared.Storage;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace ClientPlugin.Gui;

// Lists the grid groups the plugin backed up in this world and puts a chosen one on
// the clipboard, design section 9. The rows come from the store index alone; an
// entry file is opened only when the player picks it.
public sealed class GridHistoryScreen : MyGuiScreenBase
{
    public const string TableName = "GridHistoryTable";

    private static readonly string[] ColumnNames =
    {
        "Time",
        "Name",
        "Blocks",
        "Grids",
        "PCU",
        "Size",
        "Reason",
    };
    private static readonly float[] ColumnWidths =
    {
        0.25f,
        0.27f,
        0.09f,
        0.08f,
        0.08f,
        0.1f,
        0.13f,
    };
    private const int VisibleRows = 14;

    private static GridHistoryScreen current;

    private readonly SortKeys keys = SortKeys.Parse(Config.Current.GridHistorySortKeys);
    private List<StoreRow> rows = new List<StoreRow>();
    private MyGuiControlTable table;
    private bool sortChanged;

    public static void Open()
    {
        if (current != null)
            return;

        if (!UndoSession.Active)
        {
            var message =
                MySession.Static == null
                    ? "The grid history belongs to a world. Load one first."
                    : "Undo is off in this world. It needs the plugin on the host or the Undo companion on the server, and creative tools in survival.";
            MyGuiSandbox.AddScreen(
                MyGuiSandbox.CreateMessageBox(
                    MyMessageBoxStyleEnum.Info,
                    MyMessageBoxButtonsType.OK,
                    new StringBuilder(message),
                    new StringBuilder(Log.Name)
                )
            );
            return;
        }
        MyGuiSandbox.AddScreen(new GridHistoryScreen());
        // On a client of a server the store is the server's
        if (Remote)
            CompanionClient.RequestRows();
    }

    private static bool Remote => UndoSession.Document == null;

    // The server sent the rows the dialog asked for
    public static void RowsArrived() => current?.Fill();

    private GridHistoryScreen()
        : base(
            new Vector2(0.5f, 0.5f),
            MyGuiConstants.SCREEN_BACKGROUND_COLOR,
            new Vector2(0.8f, 0.8f),
            false,
            null,
            MySandboxGame.Config.UIBkOpacity,
            MySandboxGame.Config.UIOpacity
        )
    {
        EnabledBackgroundFade = true;
        m_closeOnEsc = true;
        CanHideOthers = true;
        CanBeHidden = true;
        CloseButtonEnabled = true;
        current = this;
    }

    public override string GetFriendlyName() => nameof(GridHistoryScreen);

    public override void LoadContent()
    {
        base.LoadContent();
        RecreateControls(true);
    }

    public override void OnRemoved()
    {
        current = null;
        if (sortChanged)
            ConfigStorage.Save(Config.Current);
        base.OnRemoved();
        UndoSession.Changed();
    }

    public override void RecreateControls(bool constructor)
    {
        base.RecreateControls(constructor);
        AddCaption("Grid history");

        table = new MyGuiControlTable
        {
            Name = TableName,
            Position = new Vector2(0f, -0.31f),
            Size = new Vector2(0.74f, 0.6f),
            OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_TOP,
            ColumnsCount = ColumnNames.Length,
            VisibleRowsCount = VisibleRows,
        };
        table.SetCustomColumnWidths(ColumnWidths);
        for (var i = 0; i < ColumnNames.Length; i++)
            table.SetColumnName(i, new StringBuilder(ColumnNames[i]));
        table.ColumnClicked += OnColumnClicked;
        table.ItemDoubleClicked += (_, _) => PasteSelected();
        Controls.Add(table);

        AddButton("Paste", -0.2f, "Puts the selected grids on the clipboard", PasteSelected);
        AddButton("Delete", 0f, "Removes the selected backup", DeleteSelected);
        AddButton("Close", 0.2f, null, () => CloseScreen());

        Fill();
    }

    private void AddButton(string text, float x, string toolTip, Action clicked)
    {
        var button = new MyGuiControlButton(
            new Vector2(x, 0.34f),
            text: new StringBuilder(text),
            toolTip: toolTip,
            originAlign: MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER
        )
        {
            Name = "GridHistory" + text,
        };
        button.ButtonClicked += _ => clicked();
        Controls.Add(button);
    }

    // The table's own sort knows one column, so the rows are sorted here and added again
    private void Fill()
    {
        var selected = table.SelectedRow?.UserData;

        rows = Remote
            ? CompanionClient.Rows?.ToList() ?? new List<StoreRow>()
            : Actors.Local.Store.Index.Rows.ToList();
        rows.Sort(keys.Compare);

        table.Clear();
        foreach (var row in rows)
        {
            var line = new MyGuiControlTable.Row(
                row,
                $"{row.Bytes:N0} bytes, {(row.IsStatic ? "station" : "ship")}"
            );
            foreach (var text in Cells(row))
                line.AddCell(new MyGuiControlTable.Cell(text));
            table.Add(line);
        }

        var index = rows.IndexOf(selected as StoreRow);
        if (index >= 0)
            table.SelectedRowIndex = index;

        // The arrow in the header shows the first key
        for (var i = 0; i < ColumnNames.Length; i++)
            table.Columns[i].SortState = MyGuiControlTable.SortStateEnum.Unsorted;
        if (keys.Keys.Count != 0)
            table.Columns[(int)keys.Keys[0].Key].SortState = keys.Keys[0].Value
                ? MyGuiControlTable.SortStateEnum.Descending
                : MyGuiControlTable.SortStateEnum.Ascending;

        UndoSession.Changed();
    }

    private static string[] Cells(StoreRow row) =>
        new[]
        {
            row.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            row.MainGridName ?? "",
            row.BlockCount.ToString(),
            row.GridCount.ToString(),
            row.Pcu.ToString(),
            row.Size.ToString(),
            row.Reason.ToString(),
        };

    private void OnColumnClicked(MyGuiControlTable _, int column)
    {
        keys.Click((GridColumn)column);
        Config.Current.GridHistorySortKeys = keys.ToString();
        sortChanged = true;
        Fill();
    }

    private StoreRow Selected => table.SelectedRow?.UserData as StoreRow;

    // The game's own blueprint path, which also sets the owner and the drag point.
    // The player then places the grids with the normal paste flow, so the server's
    // paste permissions apply.
    private void PasteSelected()
    {
        var row = Selected;
        if (row == null)
            return;

        if (Remote)
        {
            // The server sends the file, PasteArrived goes on
            CompanionClient.Fetch(row.Id);
            return;
        }

        var blueprint = StoredGroups.LoadBlueprint(row.Id);
        if (blueprint == null)
        {
            Notify.Show($"The backup of {row.MainGridName} is gone from the grid store");
            Fill();
            return;
        }
        Paste(blueprint);
    }

    // The backup the server sent for the selected row
    public static void PasteArrived(MyObjectBuilder_Definitions blueprint) =>
        current?.Paste(blueprint);

    private void Paste(MyObjectBuilder_Definitions blueprint)
    {
        if (!MySession.Static.IsCopyPastingEnabled)
        {
            MyClipboardComponent.ShowCannotPasteError();
            return;
        }

        var clipboard = MyClipboardComponent.Static;
        if (
            !MyGuiBlueprintScreen_Reworked.CopyBlueprintPrefabToClipboard(
                blueprint,
                clipboard.Clipboard
            )
        )
            return;

        CloseScreen();
        MySandboxGame.Static.Invoke(() => clipboard.Paste(), "UndoGridHistoryPaste");
    }

    private void DeleteSelected()
    {
        var row = Selected;
        if (row == null)
            return;

        MyGuiSandbox.AddScreen(
            MyGuiSandbox.CreateMessageBox(
                MyMessageBoxStyleEnum.Info,
                MyMessageBoxButtonsType.YES_NO,
                new StringBuilder(
                    $"Delete the backup of \"{row.MainGridName}\"? An undo step that needs it will no longer work."
                ),
                new StringBuilder(Log.Name),
                callback: result =>
                {
                    if (result != MyGuiScreenMessageBox.ResultEnum.YES)
                        return;
                    if (Remote)
                    {
                        CompanionClient.Delete(row.Id);
                        return;
                    }
                    Actors.Local.Store.Remove(row);
                    Fill();
                }
            )
        );
    }

    // For the debug status file: the rows as the table shows them. The Remote API
    // reports a table's row count and selection, not its cells.
    public static string StatusJson()
    {
        var screen = current;
        if (screen?.table == null)
            return "\"gridHistory\":null";

        var lines = screen.rows.Select(r =>
            "[" + string.Join(",", Cells(r).Select(StatusFile.Quote)) + "]"
        );
        return "\"gridHistory\":{\"sortKeys\":"
            + StatusFile.Quote(screen.keys.ToString())
            + ",\"rows\":["
            + string.Join(",", lines)
            + "]}";
    }
}

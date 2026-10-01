using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ClientPlugin.Settings;
using ClientPlugin.Settings.Elements;
using ClientPlugin.Settings.Tools;
using VRage.Game.ModAPI;
using VRage.Input;

namespace ClientPlugin;

public enum OversizedGridBackups
{
    Ask,
    AlwaysRaise,
    NeverStore,
}

public enum LogLevel
{
    Error,
    Warning,
    Info,
    Debug,
}

// Every number and key of the design is an option here, see design section 11.
public class Config : INotifyPropertyChanged
{
    #region Options

    private Binding undoBinding = new Binding(MyKeys.Z, ctrl: true);
    private Binding redoBinding = new Binding(MyKeys.Y, ctrl: true);
    private Binding relativeDampenersBinding = new Binding(MyKeys.Z, ctrl: true, shift: true);
    private Binding toggleAllReactorsBinding = new Binding(MyKeys.Y, ctrl: true, shift: true);

    private bool enableBuildContext = true;
    private bool enableTerminalContext = true;
    private bool enableTextContext = true;
    private bool separateTextUndoInTerminal = true;
    private int maxNodesBuild = 200;
    private int maxNodesTerminal = 200;
    private int maxNodesText = 100;
    private bool undoTree;

    private bool recordTerminalChangesOutsideTerminal;
    private bool restoreRemovedBlocksWithFullState = true;
    private int paintStrokeTimeoutMs = 300;
    private int textCoalescingWindowMs = 500;
    private int pendingOperationTimeoutS = 5;
    private int pasteMatchWindowS = 5;
    private float pasteMatchPositionToleranceM = 0.5f;
    private GridLinkTypeEnum groupLinkTypeForSnapshots = GridLinkTypeEnum.Logical;

    private int gridStoreBudgetPerWorldMb = 512;
    private int gridStoreBudgetTotalMb = 2048;
    private int budgetRaiseStepMb = 64;
    private OversizedGridBackups oversizedGridBackups = OversizedGridBackups.Ask;
    private Binding gridHistoryBinding = new Binding(MyKeys.H, ctrl: true);
    private string gridHistorySortKeys = "-Time";

    private bool persistInTheWorldSave = true;
    private bool persistOnMultiplayerClient = true;
    private string clientStorageRoot = "";
    private int clientAutosaveIntervalS = 60;
    private int clientHistoryRetentionDays = 90;

    private bool notifications = true;
    private int notificationDurationMs = 2000;
    private bool debugStatusFile;
    private LogLevel logLevel = LogLevel.Info;

    #endregion

    #region User interface

    public readonly string Title = "Undo";

    [Separator("Key bindings")]
    [Keybind(description: "Reverts the last operation in the current context")]
    public Binding UndoBinding
    {
        get => undoBinding;
        set => SetField(ref undoBinding, value);
    }

    [Keybind(description: "Applies the last undone operation again")]
    public Binding RedoBinding
    {
        get => redoBinding;
        set => SetField(ref redoBinding, value);
    }

    [Note(
        "Vanilla binds Ctrl-Z to relative dampeners. While undo holds that key, this one does it."
    )]
    [Keybind(
        description: "Replacement for the vanilla relative dampeners key. "
            + "Cleared, relative dampeners have no key while undo holds Ctrl-Z."
    )]
    public Binding RelativeDampenersBinding
    {
        get => relativeDampenersBinding;
        set => SetField(ref relativeDampenersBinding, value);
    }

    [Note(
        "Vanilla binds Ctrl-Y to toggle all reactors. While redo holds that key, this one does it."
    )]
    [Keybind(
        description: "Replacement for the vanilla toggle all reactors key. "
            + "Cleared, toggling all reactors has no key while redo holds Ctrl-Y."
    )]
    public Binding ToggleAllReactorsBinding
    {
        get => toggleAllReactorsBinding;
        set => SetField(ref toggleAllReactorsBinding, value);
    }

    [Separator("Contexts and history")]
    [Checkbox(
        description: "Gameplay: placing and removing blocks, painting, pasting and deleting grids"
    )]
    public bool EnableBuildContext
    {
        get => enableBuildContext;
        set => SetField(ref enableBuildContext, value);
    }

    [Checkbox(description: "Terminal screen: property changes, names, programmable block programs")]
    public bool EnableTerminalContext
    {
        get => enableTerminalContext;
        set => SetField(ref enableTerminalContext, value);
    }

    [Checkbox(description: "Single line text boxes in any screen")]
    public bool EnableTextContext
    {
        get => enableTextContext;
        set => SetField(ref enableTextContext, value);
    }

    [Checkbox(
        description: "On: a text box in the terminal has its own undo while the cursor is in it. Off: the keys always act on the terminal history there"
    )]
    public bool SeparateTextUndoInTerminal
    {
        get => separateTextUndoInTerminal;
        set => SetField(ref separateTextUndoInTerminal, value);
    }

    [Slider(
        10f,
        1000f,
        10f,
        SliderAttribute.SliderType.Integer,
        description: "Oldest nodes are dropped beyond this"
    )]
    public int MaxNodesBuild
    {
        get => maxNodesBuild;
        set => SetField(ref maxNodesBuild, value);
    }

    [Slider(
        10f,
        1000f,
        10f,
        SliderAttribute.SliderType.Integer,
        description: "Oldest nodes are dropped beyond this"
    )]
    public int MaxNodesTerminal
    {
        get => maxNodesTerminal;
        set => SetField(ref maxNodesTerminal, value);
    }

    [Slider(10f, 1000f, 10f, SliderAttribute.SliderType.Integer, description: "Per text box")]
    public int MaxNodesText
    {
        get => maxNodesText;
        set => SetField(ref maxNodesText, value);
    }

    [Checkbox(description: "Keep abandoned branches when a new action is recorded after undo")]
    public bool UndoTree
    {
        get => undoTree;
        set => SetField(ref undoTree, value);
    }

    [Separator("Recording and replay")]
    [Checkbox(description: "Also record toolbar, hotkey and script driven property changes")]
    public bool RecordTerminalChangesOutsideTerminal
    {
        get => recordTerminalChangesOutsideTerminal;
        set => SetField(ref recordTerminalChangesOutsideTerminal, value);
    }

    [Checkbox(
        description: "Restore removed blocks with their settings when creative rights allow it, "
            + "off always rebuilds them from the definition"
    )]
    public bool RestoreRemovedBlocksWithFullState
    {
        get => restoreRemovedBlocksWithFullState;
        set => SetField(ref restoreRemovedBlocksWithFullState, value);
    }

    [Slider(
        50f,
        2000f,
        50f,
        SliderAttribute.SliderType.Integer,
        description: "Held mouse painting within this window is one undo step"
    )]
    public int PaintStrokeTimeoutMs
    {
        get => paintStrokeTimeoutMs;
        set => SetField(ref paintStrokeTimeoutMs, value);
    }

    [Slider(
        100f,
        5000f,
        100f,
        SliderAttribute.SliderType.Integer,
        description: "Typing pauses shorter than this stay in one text snapshot; terminal changes of one control within it become one undo step"
    )]
    public int TextCoalescingWindowMs
    {
        get => textCoalescingWindowMs;
        set => SetField(ref textCoalescingWindowMs, value);
    }

    [Slider(
        1f,
        60f,
        1f,
        SliderAttribute.SliderType.Integer,
        description: "Wait this long for an asynchronous undo or redo before marking its result unknown"
    )]
    public int PendingOperationTimeoutS
    {
        get => pendingOperationTimeoutS;
        set => SetField(ref pendingOperationTimeoutS, value);
    }

    [Slider(
        1f,
        60f,
        1f,
        SliderAttribute.SliderType.Integer,
        description: "How long new grids are matched to a paste request on a multiplayer client"
    )]
    public int PasteMatchWindowS
    {
        get => pasteMatchWindowS;
        set => SetField(ref pasteMatchWindowS, value);
    }

    [Slider(
        0.1f,
        10f,
        0.1f,
        SliderAttribute.SliderType.Float,
        description: "Position tolerance of that match"
    )]
    public float PasteMatchPositionToleranceM
    {
        get => pasteMatchPositionToleranceM;
        set => SetField(ref pasteMatchPositionToleranceM, value);
    }

    [Dropdown(
        description: "Grid connections followed by the backup taken before an undo or redo on a server"
    )]
    public GridLinkTypeEnum GroupLinkTypeForSnapshots
    {
        get => groupLinkTypeForSnapshots;
        set => SetField(ref groupLinkTypeForSnapshots, value);
    }

    [Separator("Grid store")]
    [Slider(
        64f,
        16384f,
        64f,
        SliderAttribute.SliderType.Integer,
        description: "Backed up grids of one world, oldest removed first beyond this"
    )]
    public int GridStoreBudgetPerWorldMb
    {
        get => gridStoreBudgetPerWorldMb;
        set => SetField(ref gridStoreBudgetPerWorldMb, value);
    }

    [Slider(
        64f,
        65536f,
        64f,
        SliderAttribute.SliderType.Integer,
        description: "Backed up grids of all worlds together"
    )]
    public int GridStoreBudgetTotalMb
    {
        get => gridStoreBudgetTotalMb;
        set => SetField(ref gridStoreBudgetTotalMb, value);
    }

    [Slider(
        16f,
        1024f,
        16f,
        SliderAttribute.SliderType.Integer,
        description: "Step a budget is raised by to fit an oversized backup"
    )]
    public int BudgetRaiseStepMb
    {
        get => budgetRaiseStepMb;
        set => SetField(ref budgetRaiseStepMb, value);
    }

    [Dropdown(description: "What to do when one backup is larger than a budget")]
    public OversizedGridBackups OversizedGridBackups
    {
        get => oversizedGridBackups;
        set => SetField(ref oversizedGridBackups, value);
    }

    [Keybind(
        description: "Opens the grid history in gameplay. Vanilla Ctrl-H toggles the render "
            + "profiler, which stays on Ctrl-Shift-H."
    )]
    public Binding GridHistoryBinding
    {
        get => gridHistoryBinding;
        set => SetField(ref gridHistoryBinding, value);
    }

    // Sort history of the grid history dialog: comma separated column names, most
    // significant first, a leading '-' means descending. Kept by the dialog itself.
    public string GridHistorySortKeys
    {
        get => gridHistorySortKeys;
        set => SetField(ref gridHistorySortKeys, value);
    }

    [Separator("Persistence")]
    [Checkbox(
        description: "Save the history with offline and hosted worlds, it follows their backups"
    )]
    public bool PersistInTheWorldSave
    {
        get => persistInTheWorldSave;
        set => SetField(ref persistInTheWorldSave, value);
    }

    [Checkbox(description: "Keep the history of server sessions in the client storage folder")]
    public bool PersistOnMultiplayerClient
    {
        get => persistOnMultiplayerClient;
        set => SetField(ref persistOnMultiplayerClient, value);
    }

    [Textbox(
        description: "Folder of the grid store and client histories, empty means <game user data>/Undo"
    )]
    public string ClientStorageRoot
    {
        get => clientStorageRoot;
        set => SetField(ref clientStorageRoot, value);
    }

    [Slider(
        10f,
        600f,
        10f,
        SliderAttribute.SliderType.Integer,
        description: "Minimum time between client side history writes"
    )]
    public int ClientAutosaveIntervalS
    {
        get => clientAutosaveIntervalS;
        set => SetField(ref clientAutosaveIntervalS, value);
    }

    [Slider(
        1f,
        365f,
        1f,
        SliderAttribute.SliderType.Integer,
        description: "Client histories older than this are deleted at start"
    )]
    public int ClientHistoryRetentionDays
    {
        get => clientHistoryRetentionDays;
        set => SetField(ref clientHistoryRetentionDays, value);
    }

    [Separator("Feedback")]
    [Checkbox(description: "HUD text on undo, redo and refusals")]
    public bool Notifications
    {
        get => notifications;
        set => SetField(ref notifications, value);
    }

    [Slider(
        500f,
        10000f,
        100f,
        SliderAttribute.SliderType.Integer,
        description: "HUD text lifetime"
    )]
    public int NotificationDurationMs
    {
        get => notificationDurationMs;
        set => SetField(ref notificationDurationMs, value);
    }

    [Checkbox(
        description: "Write status.json into the storage folder after every history change, for tests"
    )]
    public bool DebugStatusFile
    {
        get => debugStatusFile;
        set => SetField(ref debugStatusFile, value);
    }

    [Dropdown(description: "Plugin log verbosity in the game log")]
    public LogLevel LogLevel
    {
        get => logLevel;
        set => SetField(ref logLevel, value);
    }

    #endregion

    #region Property change notification boilerplate

    public static readonly Config Default = new Config();
    public static readonly Config Current = ConfigStorage.Load();

    public event PropertyChangedEventHandler PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    #endregion
}

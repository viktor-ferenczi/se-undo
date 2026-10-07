using System.IO;
using PluginSdk.Config;
using Shared;
using VRage.FileSystem;
using VRage.Game.ModAPI;

namespace ServerPlugin;

[Section("history", caption: "History")]
[Section("store", caption: "Grid store")]
[Section("timing", caption: "Timing")]
[Section("debug", caption: "Diagnostics")]
public class UndoServerConfig : PluginConfig, IUndoOptions
{
    [IntOption(1, 1000, "Most build steps a player's history keeps", Parent = "history")]
    public int MaxNodesBuild
    {
        get;
        set => SetField(ref field, value);
    } = 200;

    [IntOption(1, 1000, "Most terminal steps a player's history keeps", Parent = "history")]
    public int MaxNodesTerminal
    {
        get;
        set => SetField(ref field, value);
    } = 200;

    [BoolOption(
        "Keep the players' histories in the world save, so they follow its backups",
        Parent = "history"
    )]
    public bool PersistInTheWorldSave
    {
        get;
        set => SetField(ref field, value);
    } = true;

    [IntOption(
        1,
        1000000,
        "Grid store budget per player, MB. Older backups go first when it is full",
        Parent = "store"
    )]
    public int GridStoreBudgetPerPlayerMb
    {
        get;
        set => SetField(ref field, value);
    } = 256;

    [IntOption(1, 10000000, "Grid store budget of all players together, MB", Parent = "store")]
    public int GridStoreBudgetTotalMb
    {
        get;
        set => SetField(ref field, value);
    } = 4096;

    [EnumOption(
        "Grids a removal backs up with a rotor, hinge, piston or connector it takes apart",
        Parent = "store"
    )]
    public GridLinkTypeEnum GroupLinkTypeForSnapshots
    {
        get;
        set => SetField(ref field, value);
    } = GridLinkTypeEnum.Logical;

    [StringOption(
        description: "Folder of the grid store and the status file, empty means <instance>/Undo",
        Parent = "store"
    )]
    public string StoreFolder
    {
        get;
        set => SetField(ref field, value);
    } = "";

    [IntOption(
        1,
        60,
        "Seconds a step that completes later may take before its result counts as unknown",
        Parent = "timing"
    )]
    public int PendingOperationTimeoutS
    {
        get;
        set => SetField(ref field, value);
    } = 5;

    [IntOption(
        50,
        5000,
        "Painting pauses shorter than this stay in one step, ms",
        Parent = "timing"
    )]
    public int PaintStrokeTimeoutMs
    {
        get;
        set => SetField(ref field, value);
    } = 300;

    [IntOption(
        50,
        5000,
        "Terminal changes of one control closer than this are one step, ms",
        Parent = "timing"
    )]
    public int TextCoalescingWindowMs
    {
        get;
        set => SetField(ref field, value);
    } = 500;

    [BoolOption(
        "Write status-players.json into the store folder after every change, for tests",
        Parent = "debug"
    )]
    public bool DebugStatusFile
    {
        get;
        set => SetField(ref field, value);
    }

    [EnumOption("Plugin log verbosity", Parent = "debug")]
    public LogLevel LogLevel
    {
        get;
        set => SetField(ref field, value);
    } = LogLevel.Info;

    // Where Plugin loaded the file from
    internal string Path;

    // The per world budget of a client is the per player budget here. A server never
    // raises its budgets for a player: an oversized backup is dropped.
    int IUndoOptions.GridStoreBudgetPerWorldMb
    {
        get => GridStoreBudgetPerPlayerMb;
        set { }
    }

    int IUndoOptions.GridStoreBudgetTotalMb
    {
        get => GridStoreBudgetTotalMb;
        set { }
    }

    int IUndoOptions.BudgetRaiseStepMb => 64;

    OversizedGridBackups IUndoOptions.OversizedGridBackups => OversizedGridBackups.NeverStore;

    string IUndoOptions.StorageRoot =>
        string.IsNullOrWhiteSpace(StoreFolder)
            ? System.IO.Path.Combine(MyFileSystem.UserDataPath, Log.Name)
            : StoreFolder;

    void IUndoOptions.Save()
    {
        if (Path != null)
            ConfigStorage.SaveXml(this, Path);
    }
}

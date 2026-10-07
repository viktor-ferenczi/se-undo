using VRage.Game.ModAPI;

namespace Shared;

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

// What the shared code reads from the configuration of the process it runs in: the
// client plugin's Config, or the server plugin's config on a dedicated server
public interface IUndoOptions
{
    int PaintStrokeTimeoutMs { get; }
    int TextCoalescingWindowMs { get; }
    int PendingOperationTimeoutS { get; }
    GridLinkTypeEnum GroupLinkTypeForSnapshots { get; }

    // Per world on a client; on a server, per player
    int GridStoreBudgetPerWorldMb { get; set; }
    int GridStoreBudgetTotalMb { get; set; }
    int BudgetRaiseStepMb { get; }
    OversizedGridBackups OversizedGridBackups { get; }

    // Upper limits of the node counts a client asks for, on a server
    int MaxNodesBuild { get; }
    int MaxNodesTerminal { get; }

    bool PersistInTheWorldSave { get; }

    // Root of the grid store and of the status file, never empty
    string StorageRoot { get; }
    bool DebugStatusFile { get; }
    LogLevel LogLevel { get; }

    // Writes the configuration after a budget was raised
    void Save();
}

public static class Options
{
    public static IUndoOptions Current;
}

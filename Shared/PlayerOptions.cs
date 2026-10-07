namespace Shared;

// The options that belong to a player: the local player's own on a host, and the
// ones a client sends with its handshake on a server
public class PlayerOptions
{
    public bool EnableBuildContext = true;
    public bool EnableTerminalContext = true;
    public bool RecordTerminalChangesOutsideTerminal;
    public bool RestoreRemovedBlocksWithFullState = true;
    public bool UndoTree;
    public int MaxNodesBuild = 200;
    public int MaxNodesTerminal = 200;
}

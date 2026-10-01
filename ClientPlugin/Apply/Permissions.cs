using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Game.Entity;

namespace ClientPlugin.Apply;

// Client side mirrors of the server checks, design section 2, so an undo is refused
// cleanly instead of sending a request the server rejects.
public static class Permissions
{
    public const string NeedsCreativeTools = "needs creative tools";
    public const string NotYourGrid = "the grid belongs to someone else";
    public const string NoCopyPaste = "copy and paste is disabled";
    public const string NotYourBlock = "the block belongs to someone else";
    public const string NeedsScripter = "needs scripter rights";

    // PB program
    public static bool IsScripter => MySession.Static.IsUserScripter(Sync.MyId);

    // Mirror of the BigOwner validation of MyCubeGrid.OnChangeDisplayNameRequest, which
    // the server skips for its own requests
    public static bool CanRenameGrid(MyCubeGrid grid) =>
        Sync.IsServer
        || grid.BigOwners.Count == 0
        || grid.BigOwners.Contains(MySession.Static.LocalPlayerId);

    // Raze blocks
    public static bool CanRemoveBlocks =>
        MySession.Static.CreativeMode || MySession.Static.CreativeToolsEnabled(Sync.MyId);

    // Restore blocks with their full state; otherwise they are rebuilt from the definition
    public static bool HasCreativeRights => MySession.Static.HasPlayerCreativeRights(Sync.MyId);

    // Ownership rule of MyCubeGrid.ColorGridOrBlockRequestValidation
    public static bool CanPaint(MyCubeGrid grid)
    {
        if (grid.BigOwners.Count == 0)
            return true;

        var session = MySession.Static;
        if (session.IsUserAdmin(Sync.MyId) || session.IsUserSpaceMaster(Sync.MyId))
            return true;

        foreach (var owner in grid.BigOwners)
        {
            var relation = MyIDModule.GetRelationPlayerPlayer(owner, session.LocalPlayerId);
            if (
                relation == MyRelationsBetweenPlayers.Self
                || relation == MyRelationsBetweenPlayers.Allies
            )
                return true;
        }
        return false;
    }

    // Paste grids and group snapshot restore
    public static bool CanPasteGrids => MySession.Static.IsCopyPastingEnabledForUser(Sync.MyId);

    // Mirror of MyCubeGrid.OnGridClosedRequest: creative rights unless the request is
    // local, then space master, no big owner, a big owner, or a faction leader of one.
    // A client must check first, the server answers a rights failure with a kick.
    public static string CloseRefusal(MyCubeGrid grid)
    {
        var session = MySession.Static;
        if (!Sync.IsServer && !session.HasPlayerCreativeRights(Sync.MyId))
            return NeedsCreativeTools;
        if (session.IsUserSpaceMaster(Sync.MyId) || grid.BigOwners.Count == 0)
            return null;

        var identity = session.LocalPlayerId;
        var faction = session.Factions.TryGetPlayerFaction(identity);
        var leader = faction != null && faction.IsLeader(identity);
        foreach (var owner in grid.BigOwners)
        {
            if (owner == identity)
                return null;
            if (
                leader
                && session.Factions.TryGetPlayerFaction(owner)?.FactionId == faction.FactionId
            )
                return null;
        }
        return NotYourGrid;
    }
}

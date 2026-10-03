using System.Collections.Generic;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Game.Entity;

namespace ClientPlugin.Apply;

// Design section 2
public enum SessionMode
{
    Offline,
    LobbyHost,
    LobbyClient,
    ServerClient,
}

// Mirrors of the server's checks, design section 2. The local server skips them for
// its own requests, so without these undo could do what the player cannot by hand.
public static class Permissions
{
    public static SessionMode Mode
    {
        get
        {
            var multiplayer = MyMultiplayer.Static;
            if (multiplayer == null)
                return SessionMode.Offline;
            if (multiplayer is MyMultiplayerLobbyClient)
                return SessionMode.LobbyClient;
            if (multiplayer is MyMultiplayerClientBase)
                return SessionMode.ServerClient;
            // Sync.IsServer and MyMultiplayerLobby. A plugin never runs on a dedicated
            // server, so no other server kind gets here.
            return SessionMode.LobbyHost;
        }
    }

    public const string WithChanges = "restored with changes";
    public const string InTheWay = "something is in the way";
    public const string NotYourGrid = "the grid belongs to someone else";
    public const string NoCopyPaste = "copy and paste is disabled";
    public const string NotYourBlock = "the block belongs to someone else";
    public const string NeedsScripter = "needs scripter rights";

    // PB program
    public static bool IsScripter => MySession.Static.IsUserScripter(Sync.MyId);

    // A creative world, or creative tools switched on: the plugin is off without it,
    // see UndoSession.Active. Stricter than HasPlayerCreativeRights, which is true for
    // everyone offline and for a space master with the tools off.
    public static bool Creative =>
        MySession.Static.CreativeMode || MySession.Static.CreativeToolsEnabled(Sync.MyId);

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

    // Mirror of the ownership rule of MyCubeGrid.OnGridClosedRequest: space master,
    // no big owner, a big owner, or a faction leader of one
    public static string CloseRefusal(MyCubeGrid grid)
    {
        var session = MySession.Static;
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

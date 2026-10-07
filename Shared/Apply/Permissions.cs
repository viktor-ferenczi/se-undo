using System.Collections.Generic;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Shared.Session;
using VRage.Game.Entity;

namespace Shared.Apply;

// Design section 2
public enum SessionMode
{
    Offline,
    LobbyHost,
    LobbyClient,
    ServerClient,
    DedicatedServer,
}

// Mirrors of the server's checks for the current actor, design section 2. The server
// skips them for its own requests, so without these undo could do what the player
// cannot by hand.
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
            return Sync.IsDedicated ? SessionMode.DedicatedServer : SessionMode.LobbyHost;
        }
    }

    public const string WithChanges = "restored with changes";
    public const string InTheWay = "something is in the way";
    public const string NotYourGrid = "the grid belongs to someone else";
    public const string NoCopyPaste = "copy and paste is disabled";
    public const string NotYourBlock = "the block belongs to someone else";
    public const string NeedsScripter = "needs scripter rights";

    private static Actor Actor => Actor.Current;

    // PB program
    public static bool IsScripter => MySession.Static.IsUserScripter(Actor.SteamId);

    // Ownership rule of MyCubeGrid.ColorGridOrBlockRequestValidation
    public static bool CanPaint(MyCubeGrid grid)
    {
        if (grid.BigOwners.Count == 0)
            return true;

        var session = MySession.Static;
        if (session.IsUserAdmin(Actor.SteamId) || session.IsUserSpaceMaster(Actor.SteamId))
            return true;

        foreach (var owner in grid.BigOwners)
        {
            var relation = MyIDModule.GetRelationPlayerPlayer(owner, Actor.IdentityId);
            if (
                relation == MyRelationsBetweenPlayers.Self
                || relation == MyRelationsBetweenPlayers.Allies
            )
                return true;
        }
        return false;
    }

    // Paste grids and group snapshot restore
    public static bool CanPasteGrids => MySession.Static.IsCopyPastingEnabledForUser(Actor.SteamId);

    // Mirror of the ownership rule of MyCubeGrid.OnGridClosedRequest: space master,
    // no big owner, a big owner, or a faction leader of one
    public static string CloseRefusal(MyCubeGrid grid)
    {
        var session = MySession.Static;
        if (session.IsUserSpaceMaster(Actor.SteamId) || grid.BigOwners.Count == 0)
            return null;

        var identity = Actor.IdentityId;
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

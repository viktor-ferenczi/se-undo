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

// Client side mirrors of the server checks, design section 2, so an undo is refused
// cleanly instead of sending a request the server rejects.
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

    public const string MissingComponents = "missing components";
    public const string ConstructionSites = "restored as construction sites";
    public const string LinksLost = "restored, some block links lost";
    public const string WithChanges = "restored with changes";
    public const string InTheWay = "something is in the way";
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

    // A creative world, or creative tools switched on. Everything a regular survival
    // player cannot do by hand needs this: raze, restore with full state, merge back,
    // close. Stricter than HasPlayerCreativeRights, which is true for everyone offline
    // and for a space master with the tools off, and never looser, so the server
    // accepts what this allows.
    public static bool Creative =>
        MySession.Static.CreativeMode || MySession.Static.CreativeToolsEnabled(Sync.MyId);

    // Mirror of the material check the cube builder makes before it sends a build
    // request. The server skips it for its own requests, so offline it is only here.
    public static bool HasComponentsFor(
        MyCubeGrid grid,
        HashSet<MyCubeGrid.MyBlockLocation> locations
    )
    {
        if (Creative)
            return true;
        var character = MySession.Static.LocalCharacter;
        if (character == null)
            return false;
        MyCubeBuilder.BuildComponent.GetBlocksPlacementMaterials(locations, grid);
        return MyCubeBuilder.BuildComponent.HasBuildingMaterials(character);
    }

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

    // Mirror of MyCubeGrid.OnGridClosedRequest: creative rights, then space master, no
    // big owner, a big owner, or a faction leader of one. The server skips the rights
    // check for its own requests; the plugin does not, or undo would remove blocks for
    // free in survival. A client must check first anyway, the server answers a rights
    // failure with a kick.
    public static string CloseRefusal(MyCubeGrid grid)
    {
        var session = MySession.Static;
        if (!Creative)
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

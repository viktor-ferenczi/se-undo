using Sandbox.Game.Entities;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Entity;

namespace ClientPlugin.Apply;

// Client side mirrors of the server checks, design section 2, so an undo is refused
// cleanly instead of sending a request the server rejects.
public static class Permissions
{
    public const string NeedsCreativeTools = "needs creative tools";
    public const string NotYourGrid = "the grid belongs to someone else";

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
}

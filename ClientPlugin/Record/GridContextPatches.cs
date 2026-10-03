using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using ClientPlugin.GridStore;
using ClientPlugin.Ops;
using ClientPlugin.Session;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Engine.Utils;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Network;
using VRageMath;

namespace ClientPlugin.Record;

// Record hooks of the grid operations of the build context, design sections 5 and 6
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class GridContextPatches
{
    private const string PlacedLabel = "placed 1 block, new grid {0}";

    // Every paste request of the local player, whether from the clipboard or not.
    // The request's own completion callback gets the grids.
    [HarmonyPatch(typeof(MyCubeGrid), nameof(MyCubeGrid.TryPasteGrid_Implementation))]
    private static class TryPasteGridPatch
    {
        private static void Prefix(ref MyCubeGrid.MyPasteGridParameters parameters)
        {
            if (!Recorder.CanRecord || !MyEventContext.Current.IsLocallyInvoked)
                return;

            var capture = new PasteCapture();
            Recorder.Begin(capture);
            var original = parameters.OnPasteFinished;
            parameters.OnPasteFinished = (success, grids) =>
            {
                capture.Finished(success, grids);
                original?.Invoke(success, grids);
            };
        }
    }

    // The player's close request: the clipboard's Delete and Cut send it, and so does
    // Remote's grid_close. A group delete sends one per grid and is captured as a whole.
    private static bool groupDelete;

    [HarmonyPatch(typeof(MyCubeGrid), nameof(MyCubeGrid.SendGridCloseRequest))]
    private static class CloseRequestPatch
    {
        private static void Prefix(MyCubeGrid __instance)
        {
            if (Recorder.CanRecord && !groupDelete)
                Recorder.Begin(new DeleteCapture(new List<MyCubeGrid> { __instance }));
        }
    }

    // The same grids DeleteGroup closes, one node for all of them
    [HarmonyPatch(typeof(MyGridClipboard), nameof(MyGridClipboard.DeleteGroup))]
    private static class DeleteGroupPatch
    {
        private static void Prefix(MyCubeGrid grid, GridLinkTypeEnum groupType)
        {
            if (!Recorder.CanRecord || grid == null)
                return;

            var grids = MyFakes.ENABLE_COPY_GROUP
                ? MyCubeGridGroups.Static.GetGroups(groupType).GetGroupNodes(grid).ToList()
                : new List<MyCubeGrid> { grid };
            Recorder.Begin(new DeleteCapture(grids));
            groupDelete = true;
        }

        private static void Finalizer() => groupDelete = false;
    }

    // Paste into an existing grid. The local server merges inside the request, so the
    // new blocks are there by the postfix.
    // ponytail: the game merges only the first clipboard grid and adds the others as
    // grids of their own, which are not recorded; a rare clipboard shape
    [HarmonyPatch(typeof(MyCubeGrid), nameof(MyCubeGrid.PasteBlocksToGrid))]
    private static class PasteBlocksToGridPatch
    {
        private static void Prefix(MyCubeGrid __instance, out HashSet<MySlimBlock> __state)
        {
            __state = Recorder.CanRecord ? new HashSet<MySlimBlock>(__instance.CubeBlocks) : null;
        }

        private static void Postfix(MyCubeGrid __instance, HashSet<MySlimBlock> __state)
        {
            if (__state != null)
                Recorder.RecordMerge(__instance, __state);
        }
    }

    // A block placed into empty space became a new grid. The spawn callback names
    // the builder, which is the local character for the player's own.
    [HarmonyPatch(typeof(MyCubeBuilder), nameof(MyCubeBuilder.AfterGridBuild))]
    private static class AfterGridBuildPatch
    {
        private static void Postfix(MyEntity builder, MyCubeGrid grid)
        {
            if (
                Recorder.CanRecord
                && builder != null
                && builder.EntityId == MySession.Static.LocalCharacterEntityId
                && grid != null
                && !grid.MarkedForClose
            )
                Recorder.RecordCreated(
                    new List<MyCubeGrid> { grid },
                    StoreReason.Placed,
                    PlacedLabel
                );
        }
    }

    // Failures the server reports to the sender. While a replay is pending they end
    // it with an unknown result, design section 10.
    [HarmonyPatch]
    private static class ServerFailurePatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(
                typeof(MyCubeGrid),
                nameof(MyCubeGrid.BuildBlocksFailedNotify)
            );
            yield return AccessTools.Method(
                typeof(MyCubeGrid),
                nameof(MyCubeGrid.OnColorGridBlockFailed)
            );
            yield return AccessTools.Method(
                typeof(MyCubeGrid),
                nameof(MyCubeGrid.ShowPasteFailedOperation)
            );
            yield return AccessTools.Method(
                typeof(MyCubeGrid),
                nameof(MyCubeGrid.StationClosingDenied)
            );
        }

        private static void Postfix() => Apply.Executor.OnServerFailure();
    }
}

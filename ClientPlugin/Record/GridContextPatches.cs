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
using Sandbox.Game.Multiplayer;
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

    // Local server: every paste request of the local player, whether from the
    // clipboard or not. The request's own completion callback gets the grids.
    [HarmonyPatch(typeof(MyCubeGrid), nameof(MyCubeGrid.TryPasteGrid_Implementation))]
    private static class TryPasteGridPatch
    {
        private static void Prefix(ref MyCubeGrid.MyPasteGridParameters parameters)
        {
            if (!Recorder.CanRecord || !Sync.IsServer || !MyEventContext.Current.IsLocallyInvoked)
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

    // Client: the clipboard's free placement. The expected grids are known before the
    // request; a paste into a grid goes through PasteBlocksToGrid instead.
    private static bool mergeRequested;

    [HarmonyPatch(typeof(MyGridClipboard), nameof(MyGridClipboard.PasteInternal))]
    private static class ClipboardPastePatch
    {
        private static void Prefix(
            MyGridClipboard __instance,
            out List<PasteMatch.Expected> __state
        )
        {
            mergeRequested = false;
            __state =
                Sync.IsServer || !Recorder.CanRecord
                    ? null
                    : __instance
                        .m_copiedGrids.Zip(
                            __instance.m_previewGrids,
                            (copied, preview) =>
                                new PasteMatch.Expected
                                {
                                    Name = copied.DisplayName,
                                    Blocks = copied.CubeBlocks.Count,
                                    Position = preview.WorldMatrix.Translation,
                                }
                        )
                        .ToList();
        }

        private static void Postfix(bool __result, List<PasteMatch.Expected> __state)
        {
            var session = MySession.Static;
            if (
                __result
                && __state != null
                && !mergeRequested
                && (session.CreativeMode || session.HasCreativeRights)
            )
                Recorder.Begin(
                    new MatchCapture(new PasteMatch(__state), StoreReason.Pasted, "pasted {0}")
                );
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

    // Paste into an existing grid. A local server merges inside the request, so the
    // new blocks are there by the postfix. A client waits for the server's broadcast.
    // ponytail: the game merges only the first clipboard grid and adds the others as
    // grids of their own, which are not recorded; a rare clipboard shape
    [HarmonyPatch(typeof(MyCubeGrid), nameof(MyCubeGrid.PasteBlocksToGrid))]
    private static class PasteBlocksToGridPatch
    {
        private static void Prefix(MyCubeGrid __instance, out HashSet<MySlimBlock> __state)
        {
            __state = null;
            if (!Recorder.CanRecord)
                return;

            mergeRequested = true;
            if (Sync.IsServer)
                __state = new HashSet<MySlimBlock>(__instance.CubeBlocks);
            else
                Recorder.ExpectMerge(__instance);
        }

        private static void Postfix(MyCubeGrid __instance, HashSet<MySlimBlock> __state)
        {
            if (__state != null)
                Recorder.RecordMerge(__instance, __state);
        }
    }

    // Broadcast to the clients only, a local server never runs it
    [HarmonyPatch(typeof(MyCubeGrid), nameof(MyCubeGrid.PasteBlocksToGridClient_Implementation))]
    private static class PasteBlocksToGridClientPatch
    {
        private static void Prefix(MyCubeGrid __instance, out HashSet<MySlimBlock> __state)
        {
            __state =
                UndoSession.Document != null && Recorder.TakeMergeExpectation(__instance)
                    ? new HashSet<MySlimBlock>(__instance.CubeBlocks)
                    : null;
        }

        private static void Postfix(MyCubeGrid __instance, HashSet<MySlimBlock> __state)
        {
            if (__state != null)
                Recorder.RecordMerge(__instance, __state);
        }
    }

    // Local server: a block placed into empty space became a new grid. The spawn
    // callback names the builder, which is the local character for the player's own.
    [HarmonyPatch(typeof(MyCubeBuilder), nameof(MyCubeBuilder.AfterGridBuild))]
    private static class AfterGridBuildPatch
    {
        private static void Postfix(MyEntity builder, MyCubeGrid grid)
        {
            if (
                Recorder.CanRecord
                && Sync.IsServer
                && builder != null
                && builder.EntityId == MySession.Static.LocalCharacterEntityId
                && grid != null
                && !grid.MarkedForClose
            )
                Recorder.RecordCreated(
                    new List<MyCubeGrid> { grid },
                    StoreReason.Placed,
                    PlacedLabel,
                    referenceLost: false
                );
        }
    }

    // Client: the same placement, matched by position when the grid arrives
    [HarmonyPatch(
        typeof(MyCubeBuilder),
        nameof(MyCubeBuilder.AddBlocksToBuildQueueOrSpawn),
        new[]
        {
            typeof(MyCubeBlockDefinition),
            typeof(MatrixD),
            typeof(Vector3I),
            typeof(Vector3I),
            typeof(Vector3I),
            typeof(Quaternion),
            typeof(MyCubeGrid.MyBlockVisuals),
        },
        new[]
        {
            ArgumentType.Normal,
            ArgumentType.Ref,
            ArgumentType.Normal,
            ArgumentType.Normal,
            ArgumentType.Normal,
            ArgumentType.Normal,
            ArgumentType.Normal,
        }
    )]
    private static class SpawnRequestPatch
    {
        private static void Prefix(MyCubeBuilder __instance, out bool __state)
        {
            __state =
                !Sync.IsServer
                && Recorder.CanRecord
                && (!__instance.GridAndBlockValid || __instance.PlacingSmallGridOnLargeStatic);
        }

        private static void Postfix(bool __result, bool __state, ref MatrixD worldMatrixAdd)
        {
            if (!__result || !__state)
                return;

            var expected = new PasteMatch.Expected
            {
                Blocks = 1,
                Position = worldMatrixAdd.Translation,
            };
            Recorder.Begin(
                new MatchCapture(
                    new PasteMatch(new List<PasteMatch.Expected> { expected }),
                    StoreReason.Placed,
                    PlacedLabel
                )
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

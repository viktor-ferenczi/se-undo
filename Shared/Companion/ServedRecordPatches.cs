using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Replication;
using Sandbox.Game.Replication.StateGroups;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using Shared.GridStore;
using Shared.Ops;
using Shared.Record;
using Shared.Session;
using VRage;
using VRage.Game;
using VRage.Network;
using VRageMath;

namespace Shared.Companion;

// Record hooks for the players the companion serves, design section 14. They sit on
// the server's request handlers, which run with the sending player in the event
// context; the client plugin's own hooks sit on the local entry points and record
// the local player. Each hook makes the sender the current actor while the request
// runs, so the shared recorder and the paint attribution work for that player.
// No hook may throw into a request handler: the server disconnects the player whose
// packet failed. A hook that fails records nothing and logs why (Try).
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class ServedRecordPatches
{
    // A request from another player is being handled
    public static bool RemoteRequest =>
        Sync.IsServer && MyEventContext.Current.IsValid && !MyEventContext.Current.IsLocallyInvoked;

    // The served player who sent the request being handled, null for the server's
    // own requests and for players without the client plugin
    public static Actor Sender() =>
        RemoteRequest
        && Actors.Served.TryGetValue(MyEventContext.Current.Sender.Value, out var actor)
            ? actor
            : null;

    private static Actor.Scope? Begin()
    {
        var actor = Sender();
        return actor == null ? null : Actor.Use(actor);
    }

    private static void End(Actor.Scope? scope) => scope?.Dispose();

    private static T Try<T>(Func<T> body)
    {
        try
        {
            return body();
        }
        catch (Exception e)
        {
            Log.Error($"Recording a served player's request failed: {e}");
            return default;
        }
    }

    private static void Try(Action body) =>
        Try(() =>
        {
            body();
            return 0;
        });

    // --- building and removing blocks ----------------------------------------------

    [HarmonyPatch(typeof(MyCubeGrid), nameof(MyCubeGrid.BuildBlocksRequest))]
    private static class BuildBlocksRequestPatch
    {
        private static void Prefix(
            MyCubeGrid __instance,
            HashSet<MyCubeGrid.MyBlockLocation> locations,
            out (Actor.Scope? Scope, List<BlockPlacement> Requested) __state
        )
        {
            var scope = Begin();
            __state = (
                scope,
                scope == null
                    ? null
                    : Try(() =>
                        Recorder.CanRecord
                            ? locations
                                .Where(l => __instance.GetCubeBlock(l.Min) == null)
                                .Select(l => new BlockPlacement
                                {
                                    Definition = ((MyDefinitionId)l.BlockDefinition).ToString(),
                                    Min = l.Min,
                                    Forward = l.Orientation.Forward,
                                    Up = l.Orientation.Up,
                                    EntityId = l.EntityId,
                                })
                                .ToList()
                            : null
                    )
            );
        }

        private static void Postfix(
            MyCubeGrid __instance,
            MyCubeGrid.MyBlockVisuals visuals,
            (Actor.Scope? Scope, List<BlockPlacement> Requested) __state
        )
        {
            if (__state.Requested != null)
                Try(() =>
                    Recorder.RecordBuild(
                        __instance,
                        ColorExtensions.UnpackHSVFromUint(visuals.ColorMaskHSV),
                        visuals.SkinId,
                        __state.Requested
                    )
                );
        }

        private static void Finalizer(
            (Actor.Scope? Scope, List<BlockPlacement> Requested) __state
        ) => End(__state.Scope);
    }

    [HarmonyPatch(typeof(MyCubeGrid), nameof(MyCubeGrid.BuildBlocksAreaRequest))]
    private static class BuildBlocksAreaRequestPatch
    {
        private static void Prefix(
            MyCubeGrid __instance,
            MyCubeGrid.MyBlockBuildArea area,
            out (Actor.Scope? Scope, List<BlockPlacement> Requested) __state
        )
        {
            var scope = Begin();
            __state = (
                scope,
                scope == null
                    ? null
                    : Try(() => Recorder.CanRecord ? Placements(__instance, area) : null)
            );
        }

        private static void Postfix(
            MyCubeGrid __instance,
            MyCubeGrid.MyBlockBuildArea area,
            (Actor.Scope? Scope, List<BlockPlacement> Requested) __state
        )
        {
            if (__state.Requested != null)
                Try(() =>
                    Recorder.RecordBuild(
                        __instance,
                        ColorExtensions.UnpackHSVFromUint(area.ColorMaskHSV),
                        area.SkinId,
                        __state.Requested
                    )
                );
        }

        private static void Finalizer(
            (Actor.Scope? Scope, List<BlockPlacement> Requested) __state
        ) => End(__state.Scope);
    }

    // The empty cells of an area build, the way the request steps through it
    public static List<BlockPlacement> Placements(MyCubeGrid grid, MyCubeGrid.MyBlockBuildArea area)
    {
        var definition = ((MyDefinitionId)area.DefinitionId).ToString();
        Vector3I step = area.StepDelta;
        Vector3I blockMin = area.BlockMin;
        var placements = new List<BlockPlacement>();
        for (var x = 0; x < area.BuildAreaSize.X; x++)
        for (var y = 0; y < area.BuildAreaSize.Y; y++)
        for (var z = 0; z < area.BuildAreaSize.Z; z++)
        {
            var min = area.PosInGrid + new Vector3I(x, y, z) * step + blockMin;
            if (grid.GetCubeBlock(min) == null)
                placements.Add(
                    new BlockPlacement
                    {
                        Definition = definition,
                        Min = min,
                        Forward = area.OrientationForward,
                        Up = area.OrientationUp,
                    }
                );
        }
        return placements;
    }

    // Same iteration as RazeBlocksAreaRequest, the size is inclusive
    public static IEnumerable<Vector3I> Area(Vector3I pos, Vector3UByte size)
    {
        for (var x = 0; x <= size.X; x++)
        for (var y = 0; y <= size.Y; y++)
        for (var z = 0; z <= size.Z; z++)
            yield return pos + new Vector3I(x, y, z);
    }

    [HarmonyPatch(typeof(MyCubeGrid), nameof(MyCubeGrid.RazeBlocksRequest))]
    private static class RazeBlocksRequestPatch
    {
        private static void Prefix(MyCubeGrid __instance, List<Vector3I> locations)
        {
            using var scope = Begin();
            if (scope != null)
                Try(() =>
                {
                    if (Recorder.CanRecord)
                        Recorder.BeginRaze(__instance, locations);
                });
        }
    }

    [HarmonyPatch(typeof(MyCubeGrid), nameof(MyCubeGrid.RazeBlocksAreaRequest))]
    private static class RazeBlocksAreaRequestPatch
    {
        private static void Prefix(MyCubeGrid __instance, Vector3I pos, Vector3UByte size)
        {
            using var scope = Begin();
            if (scope != null)
                Try(() =>
                {
                    if (Recorder.CanRecord)
                        Recorder.BeginRaze(__instance, Area(pos, size));
                });
        }
    }

    // --- grids ---------------------------------------------------------------------

    // The paste request's own completion callback gets the grids, on the main thread
    [HarmonyPatch(typeof(MyCubeGrid), nameof(MyCubeGrid.TryPasteGrid_Implementation))]
    private static class TryPasteGridPatch
    {
        private static void Prefix(ref MyCubeGrid.MyPasteGridParameters parameters)
        {
            using var scope = Begin();
            if (scope == null)
                return;

            var capture = Try(() =>
            {
                if (!Recorder.CanRecord)
                    return null;
                var started = new PasteCapture();
                Recorder.Begin(started);
                return started;
            });
            if (capture == null)
                return;

            var original = parameters.OnPasteFinished;
            parameters.OnPasteFinished = (success, grids) =>
            {
                capture.Finished(success, grids);
                original?.Invoke(success, grids);
            };
        }
    }

    // A clipboard delete of a group sends one close request per grid. Those of a
    // player that arrive in the same frame are captured together, as one node.
    [HarmonyPatch(typeof(MyCubeGrid), nameof(MyCubeGrid.OnGridClosedRequest))]
    private static class GridClosedRequestPatch
    {
        private static void Prefix(MyCubeGrid __instance)
        {
            using var scope = Begin();
            if (scope == null)
                return;

            Try(() =>
            {
                if (!Recorder.CanRecord || __instance.MarkedForClose)
                    return;

                var recording = Actor.Current.Recording;
                var frame = MySession.Static.GameplayFrameCounter;
                if (recording.Deleting is { } capture && capture.Frame == frame)
                {
                    capture.Add(__instance);
                    return;
                }
                recording.Deleting = new DeleteCapture(new List<MyCubeGrid> { __instance });
                Recorder.Begin(recording.Deleting);
            });
        }
    }

    [HarmonyPatch(typeof(MyCubeGrid), nameof(MyCubeGrid.PasteBlocksToGridServer_Implementation))]
    private static class PasteBlocksToGridPatch
    {
        private static void Prefix(
            MyCubeGrid __instance,
            out (Actor.Scope? Scope, HashSet<MySlimBlock> Before) __state
        )
        {
            var scope = Begin();
            __state = (
                scope,
                scope == null
                    ? null
                    : Try(() =>
                        Recorder.CanRecord ? new HashSet<MySlimBlock>(__instance.CubeBlocks) : null
                    )
            );
        }

        private static void Postfix(
            MyCubeGrid __instance,
            (Actor.Scope? Scope, HashSet<MySlimBlock> Before) __state
        )
        {
            if (__state.Before != null)
                Try(() => Recorder.RecordMerge(__instance, __state.Before));
        }

        private static void Finalizer((Actor.Scope? Scope, HashSet<MySlimBlock> Before) __state) =>
            End(__state.Scope);
    }

    // A block placed into empty space became a new grid; the spawn callback names
    // the player who asked for it
    [HarmonyPatch(typeof(MyCubeBuilder), nameof(MyCubeBuilder.AfterGridBuild))]
    private static class AfterGridBuildPatch
    {
        private static void Postfix(MyCubeGrid grid, ulong senderId)
        {
            if (
                senderId == Sync.MyId
                || grid == null
                || grid.MarkedForClose
                || !Actors.Served.TryGetValue(senderId, out var actor)
            )
                return;

            using (Actor.Use(actor))
                Try(() =>
                {
                    if (Recorder.CanRecord)
                        Recorder.RecordCreated(
                            new List<MyCubeGrid> { grid },
                            StoreReason.Placed,
                            Recorder.PlacedLabel
                        );
                });
        }
    }

    // --- paint ---------------------------------------------------------------------

    // The paint requests open a stroke; the color changes inside them are the
    // player's (ColorChangePatch)
    [HarmonyPatch]
    private static class PaintRequestPatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(
                typeof(MyCubeGrid),
                nameof(MyCubeGrid.ColorBlockRequest)
            );
            yield return AccessTools.Method(
                typeof(MyCubeGrid),
                nameof(MyCubeGrid.ColorGridFriendlyRequest)
            );
            yield return AccessTools.Method(
                typeof(MyCubeGrid),
                nameof(MyCubeGrid.SkinBlockRequest)
            );
            yield return AccessTools.Method(
                typeof(MyCubeGrid),
                nameof(MyCubeGrid.SkinGridFriendlyRequest)
            );
        }

        private static void Prefix(MyCubeGrid __instance, out Actor.Scope? __state)
        {
            __state = Begin();
            if (__state != null)
                Try(() =>
                {
                    if (Recorder.CanRecord)
                        Recorder.OpenStroke(__instance);
                });
        }

        private static void Finalizer(Actor.Scope? __state) => End(__state);
    }

    // --- terminal ------------------------------------------------------------------

    // A property a client changed arrives as a synced value. The values of the
    // block's terminal controls before and after tell which control it was.
    [HarmonyPatch(
        typeof(MyPropertySyncStateGroup),
        nameof(MyPropertySyncStateGroup.SyncPropertyChanged_Implementation)
    )]
    private static class SyncPropertyPatch
    {
        private static void Prefix(
            MyPropertySyncStateGroup __instance,
            out (
                Actor.Scope? Scope,
                MyTerminalBlock Block,
                Dictionary<string, string> Before
            ) __state
        )
        {
            __state = default;
            if (
                !(__instance.Owner is MyExternalReplicable<MySyncedBlock> replicable)
                || !(replicable.Instance is MyTerminalBlock block)
            )
                return;

            var scope = Begin();
            if (scope == null)
                return;
            var before = Try(() =>
                Recorder.CanRecordTerminal ? TerminalValues.ReadAll(block) : null
            );
            __state = (scope, block, before);
        }

        private static void Postfix(
            (Actor.Scope? Scope, MyTerminalBlock Block, Dictionary<string, string> Before) __state
        )
        {
            if (__state.Before == null)
                return;

            Try(() =>
            {
                var after = TerminalValues.ReadAll(__state.Block);
                foreach (var pair in __state.Before)
                {
                    if (after.TryGetValue(pair.Key, out var value) && value != pair.Value)
                        Recorder.RecordProperty(__state.Block, pair.Key, pair.Value, value);
                }
            });
        }

        private static void Finalizer(
            (Actor.Scope? Scope, MyTerminalBlock Block, Dictionary<string, string> Before) __state
        ) => End(__state.Scope);
    }

    [HarmonyPatch(typeof(MyTerminalBlock), nameof(MyTerminalBlock.SetCustomNameEvent))]
    private static class CustomNamePatch
    {
        private static void Prefix(MyTerminalBlock __instance, string name)
        {
            using var scope = Begin();
            if (scope != null)
                Try(() =>
                {
                    if (Recorder.CanRecordTerminal)
                        Recorder.RecordProperty(
                            __instance,
                            "Name",
                            __instance.CustomName.ToString(),
                            name ?? ""
                        );
                });
        }
    }

    [HarmonyPatch(typeof(MyTerminalBlock), nameof(MyTerminalBlock.OnCustomDataChanged))]
    private static class CustomDataPatch
    {
        private static void Prefix(MyTerminalBlock __instance, string data)
        {
            using var scope = Begin();
            if (scope != null)
                Try(() =>
                {
                    if (Recorder.CanRecordTerminal)
                        Recorder.RecordCustomData(__instance, __instance.CustomData, data);
                });
        }
    }

    [HarmonyPatch(typeof(MyCubeGrid), nameof(MyCubeGrid.OnChangeDisplayNameRequest))]
    private static class GridNamePatch
    {
        private static void Prefix(MyCubeGrid __instance, string displayName)
        {
            using var scope = Begin();
            if (scope != null)
                Try(() =>
                {
                    if (Recorder.CanRecordTerminal)
                        Recorder.RecordGridName(__instance, __instance.DisplayName, displayName);
                });
        }
    }

    // The editor's program request; refused requests (no scripter rights, too long)
    // are not recorded
    [HarmonyPatch(
        typeof(MyProgrammableBlock),
        nameof(MyProgrammableBlock.UpdateProgram),
        typeof(byte[])
    )]
    private static class ProgramPatch
    {
        private static void Prefix(MyProgrammableBlock __instance, byte[] program)
        {
            using var scope = Begin();
            if (scope != null)
                Try(() =>
                {
                    if (!Recorder.CanRecordTerminal)
                        return;
                    var source = StringCompressor.DecompressString(program);
                    if (
                        source.Length <= 100000
                        && MySession.Static.IsUserScripter(Actor.Current.SteamId)
                    )
                        Recorder.RecordProgram(__instance, __instance.m_programData, source);
                });
        }
    }

    // A block's toolbar slot set or cleared by a client: the blocks' toolbar requests
    // end in SetItemAtIndex while the event context has the sender
    [HarmonyPatch(
        typeof(MyToolbar),
        nameof(MyToolbar.SetItemAtIndex),
        typeof(int),
        typeof(MyToolbarItem),
        typeof(bool)
    )]
    private static class ToolbarSlotPatch
    {
        private static void Prefix(
            MyToolbar __instance,
            int i,
            bool gamepad,
            out (Actor.Scope? Scope, string Item) __state
        )
        {
            __state = default;
            if (!(__instance.Owner is MyTerminalBlock))
                return;
            var scope = Begin();
            if (scope == null)
                return;
            var item = Try(() =>
                Recorder.CanRecordTerminal ? BlockToolbars.Slot(__instance, i, gamepad) : null
            );
            __state = (scope, item);
        }

        private static void Postfix(
            MyToolbar __instance,
            int i,
            bool gamepad,
            (Actor.Scope? Scope, string Item) __state
        )
        {
            if (__state.Item == null)
                return;

            Try(() =>
            {
                var block = (MyTerminalBlock)__instance.Owner;
                var toolbar = BlockToolbars.NameOf(block, __instance);
                var item = BlockToolbars.Slot(__instance, i, gamepad);
                if (toolbar != null && item != null && item != __state.Item)
                    Recorder.RecordToolbar(block, toolbar, i, gamepad, __state.Item, item);
            });
        }

        private static void Finalizer((Actor.Scope? Scope, string Item) __state) =>
            End(__state.Scope);
    }
}

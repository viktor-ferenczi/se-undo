using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using ClientPlugin.Symmetry;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Shared.Companion;
using Shared.Ops;
using Shared.Record;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace ClientPlugin.Record;

// Record hooks of the build context, design section 5. They sit on the local entry
// points the cube builder calls, so requests of other players are never recorded.
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class BuildContextPatches
{
    [HarmonyPatch(
        typeof(MyCubeGrid),
        nameof(MyCubeGrid.BuildBlocks),
        typeof(Vector3),
        typeof(MyStringHash),
        typeof(HashSet<MyCubeGrid.MyBlockLocation>),
        typeof(long),
        typeof(long)
    )]
    private static class BuildBlocksPatch
    {
        private static bool Prefix(
            MyCubeGrid __instance,
            Vector3 colorMaskHsv,
            MyStringHash skinId,
            HashSet<MyCubeGrid.MyBlockLocation> locations,
            long builderEntityId,
            long ownerId,
            out List<BlockPlacement> __state
        )
        {
            __state = null;
            // The cube builder clears its set after the call
            var copy = new HashSet<MyCubeGrid.MyBlockLocation>(locations);
            if (
                EditGuard.Placing(
                    __instance,
                    () =>
                        copy.Select(l => (l.Min, l.Max, (MyDefinitionId)l.BlockDefinition))
                            .ToList(),
                    () =>
                        __instance.BuildBlocks(colorMaskHsv, skinId, copy, builderEntityId, ownerId)
                )
            )
                return false;

            SymmetryWatch.Placing(
                __instance,
                locations.Select(l => (l.Min, (MyDefinitionId)l.BlockDefinition))
            );
            __state = !Recorder.CanRecord
                ? null
                : locations
                    .Where(l => __instance.GetCubeBlock(l.Min) == null)
                    .Select(l => new BlockPlacement
                    {
                        Definition = ((MyDefinitionId)l.BlockDefinition).ToString(),
                        Min = l.Min,
                        Forward = l.Orientation.Forward,
                        Up = l.Orientation.Up,
                        EntityId = l.EntityId,
                    })
                    .ToList();
            if (__state is { Count: > 0 })
                Recorder.BeforeEdit(__instance);
            return true;
        }

        private static void Postfix(
            MyCubeGrid __instance,
            Vector3 colorMaskHsv,
            MyStringHash skinId,
            List<BlockPlacement> __state
        )
        {
            if (__state != null)
                Recorder.RecordBuild(__instance, colorMaskHsv, skinId, __state);
        }
    }

    // Lines and planes of the same block in creative
    [HarmonyPatch(
        typeof(MyCubeGrid),
        nameof(MyCubeGrid.BuildBlocks),
        new[] { typeof(MyCubeGrid.MyBlockBuildArea), typeof(long), typeof(long) },
        new[] { ArgumentType.Ref, ArgumentType.Normal, ArgumentType.Normal }
    )]
    private static class BuildBlocksAreaPatch
    {
        private static bool Prefix(
            MyCubeGrid __instance,
            ref MyCubeGrid.MyBlockBuildArea area,
            long builderEntityId,
            long ownerId,
            out List<BlockPlacement> __state
        )
        {
            __state = null;
            MyDefinitionId id = area.DefinitionId;
            var copy = area;
            Vector3I size = (Vector3I)area.BlockMax - (Vector3I)area.BlockMin;
            if (
                EditGuard.Placing(
                    __instance,
                    () =>
                        ServedRecordPatches
                            .Placements(__instance, copy)
                            .Select(p => (p.Min, p.Min + size, id))
                            .ToList(),
                    () => __instance.BuildBlocks(ref copy, builderEntityId, ownerId)
                )
            )
                return false;

            SymmetryWatch.Placing(
                __instance,
                ServedRecordPatches.Placements(__instance, area).Select(p => (p.Min, id))
            );
            __state = Recorder.CanRecord ? ServedRecordPatches.Placements(__instance, area) : null;
            if (__state is { Count: > 0 })
                Recorder.BeforeEdit(__instance);
            return true;
        }

        private static void Postfix(
            MyCubeGrid __instance,
            ref MyCubeGrid.MyBlockBuildArea area,
            List<BlockPlacement> __state
        )
        {
            if (__state != null)
                Recorder.RecordBuild(
                    __instance,
                    ColorExtensions.UnpackHSVFromUint(area.ColorMaskHSV),
                    area.SkinId,
                    __state
                );
        }
    }

    // Grinders remove fully ground blocks through RazeBlock; grinding is not recorded
    private static int grinderDepth;

    [HarmonyPatch(typeof(MyCubeGrid), nameof(MyCubeGrid.RazeBlock))]
    private static class RazeBlockPatch
    {
        private static void Prefix() => grinderDepth++;

        private static void Finalizer() => grinderDepth--;
    }

    [HarmonyPatch(
        typeof(MyCubeGrid),
        nameof(MyCubeGrid.RazeBlocks),
        typeof(List<Vector3I>),
        typeof(long),
        typeof(ulong)
    )]
    private static class RazeBlocksPatch
    {
        private static bool Prefix(
            MyCubeGrid __instance,
            List<Vector3I> locations,
            long builderEntityId,
            ulong user
        )
        {
            if (grinderDepth != 0)
                return true;

            // The cube builder clears its list after the call
            var copy = locations.ToList();
            if (
                EditGuard.Removing(
                    __instance,
                    copy,
                    () => __instance.RazeBlocks(copy, builderEntityId, user)
                )
            )
                return false;

            SymmetryWatch.Removing(__instance, locations);
            if (Recorder.CanRecord)
                Recorder.BeginRaze(__instance, locations);
            return true;
        }
    }

    [HarmonyPatch(
        typeof(MyCubeGrid),
        nameof(MyCubeGrid.RazeBlocks),
        new[] { typeof(Vector3I), typeof(Vector3UByte), typeof(long) },
        new[] { ArgumentType.Ref, ArgumentType.Ref, ArgumentType.Normal }
    )]
    private static class RazeAreaPatch
    {
        private static bool Prefix(
            MyCubeGrid __instance,
            ref Vector3I pos,
            ref Vector3UByte size,
            long builderEntityId
        )
        {
            Vector3I heldPos = pos;
            Vector3UByte heldSize = size;
            if (
                EditGuard.Removing(
                    __instance,
                    ServedRecordPatches.Area(pos, size),
                    () => __instance.RazeBlocks(ref heldPos, ref heldSize, builderEntityId)
                )
            )
                return false;

            SymmetryWatch.Removing(__instance, ServedRecordPatches.Area(pos, size));
            if (Recorder.CanRecord)
                Recorder.BeginRaze(__instance, ServedRecordPatches.Area(pos, size));
            return true;
        }
    }

    // The cube builder's area removal. With a piloted cockpit in the area it asks
    // first and removes later from OnClosedMessageBox, patched below.
    [HarmonyPatch(typeof(MyCubeGrid), nameof(MyCubeGrid.RazeBlocksDelayed))]
    private static class RazeDelayedPatch
    {
        private static bool Prefix(
            MyCubeGrid __instance,
            ref Vector3I pos,
            ref Vector3UByte size,
            long builderEntityId
        )
        {
            Vector3I heldPos = pos;
            Vector3UByte heldSize = size;
            if (
                EditGuard.Removing(
                    __instance,
                    ServedRecordPatches.Area(pos, size),
                    () => __instance.RazeBlocksDelayed(ref heldPos, ref heldSize, builderEntityId)
                )
            )
                return false;

            SymmetryWatch.Removing(__instance, ServedRecordPatches.Area(pos, size));
            if (Recorder.CanRecord)
                Recorder.BeginRaze(__instance, ServedRecordPatches.Area(pos, size));
            return true;
        }
    }

    [HarmonyPatch(typeof(MyCubeGrid), nameof(MyCubeGrid.OnClosedMessageBox))]
    private static class RazeAfterQuestionPatch
    {
        private static void Prefix(MyCubeGrid __instance)
        {
            var batch = __instance.m_delayedRazeBatch;
            if (!__instance.m_isRazeBatchDelayed || __instance.Closed)
                return;
            SymmetryWatch.Removing(__instance, ServedRecordPatches.Area(batch.Pos, batch.Size));
            if (Recorder.CanRecord)
                Recorder.BeginRaze(__instance, ServedRecordPatches.Area(batch.Pos, batch.Size));
        }
    }

    // Local paint entry points open a stroke; the color changes that follow on this
    // grid, synchronously on a local server or with the server's echo, belong to it
    [HarmonyPatch]
    private static class PaintEntryPatch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(MyCubeGrid), nameof(MyCubeGrid.SkinBlocks));
            yield return AccessTools.Method(typeof(MyCubeGrid), nameof(MyCubeGrid.SkinGrid));
            yield return AccessTools.Method(typeof(MyCubeGrid), nameof(MyCubeGrid.ColorBlocks));
            yield return AccessTools.Method(typeof(MyCubeGrid), nameof(MyCubeGrid.ColorGrid));
        }

        // A paint of blocks may be held back, see EditGuard; a paint of the whole
        // grid has no mirror images to miss
        private static bool Prefix(
            MyCubeGrid __instance,
            System.Reflection.MethodBase __originalMethod,
            object[] __args
        )
        {
            if (__args.Length > 1 && __args[0] is Vector3I min && __args[1] is Vector3I max)
            {
                var args = (object[])__args.Clone();
                if (
                    EditGuard.Painting(
                        __instance,
                        min,
                        max,
                        () => __originalMethod.Invoke(__instance, args)
                    )
                )
                    return false;

                SymmetryWatch.Painting(__instance, min, max);
            }
            if (Recorder.CanRecord)
                Recorder.OpenStroke(__instance);
            return true;
        }
    }
}

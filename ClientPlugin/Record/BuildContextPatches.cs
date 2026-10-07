using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
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
        private static void Prefix(
            MyCubeGrid __instance,
            HashSet<MyCubeGrid.MyBlockLocation> locations,
            out List<BlockPlacement> __state
        )
        {
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
        private static void Prefix(
            MyCubeGrid __instance,
            ref MyCubeGrid.MyBlockBuildArea area,
            out List<BlockPlacement> __state
        )
        {
            __state = Recorder.CanRecord ? ServedRecordPatches.Placements(__instance, area) : null;
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
        private static void Prefix(MyCubeGrid __instance, List<Vector3I> locations)
        {
            if (Recorder.CanRecord && grinderDepth == 0)
                Recorder.BeginRaze(__instance, locations);
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
        private static void Prefix(MyCubeGrid __instance, ref Vector3I pos, ref Vector3UByte size)
        {
            if (Recorder.CanRecord)
                Recorder.BeginRaze(__instance, ServedRecordPatches.Area(pos, size));
        }
    }

    // The cube builder's area removal. With a piloted cockpit in the area it asks
    // first and removes later from OnClosedMessageBox, patched below.
    [HarmonyPatch(typeof(MyCubeGrid), nameof(MyCubeGrid.RazeBlocksDelayed))]
    private static class RazeDelayedPatch
    {
        private static void Prefix(MyCubeGrid __instance, ref Vector3I pos, ref Vector3UByte size)
        {
            if (Recorder.CanRecord)
                Recorder.BeginRaze(__instance, ServedRecordPatches.Area(pos, size));
        }
    }

    [HarmonyPatch(typeof(MyCubeGrid), nameof(MyCubeGrid.OnClosedMessageBox))]
    private static class RazeAfterQuestionPatch
    {
        private static void Prefix(MyCubeGrid __instance)
        {
            var batch = __instance.m_delayedRazeBatch;
            if (Recorder.CanRecord && __instance.m_isRazeBatchDelayed && !__instance.Closed)
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

        private static void Prefix(MyCubeGrid __instance)
        {
            if (Recorder.CanRecord)
                Recorder.OpenStroke(__instance);
        }
    }
}

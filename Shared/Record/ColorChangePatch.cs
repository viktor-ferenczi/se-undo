using System.Diagnostics.CodeAnalysis;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using VRage.Utils;
using VRageMath;

namespace Shared.Record;

// Color and skin changes of a grid belong to the paint stroke the current actor has
// open on it: the local player's paint entry points open one on the client, a served
// player's paint request on the server. Changes from anyone else are not recorded.
[SuppressMessage("ReSharper", "UnusedMember.Local")]
[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class ColorChangePatch
{
    [HarmonyPatch(typeof(MyCubeGrid), nameof(MyCubeGrid.ChangeColorAndSkin))]
    private static class ChangeColorAndSkinPatch
    {
        private static void Prefix(
            MyCubeGrid __instance,
            MySlimBlock block,
            out (Vector3 color, MyStringHash skin) __state
        )
        {
            __state = (block.ColorMaskHSV, block.SkinSubtypeId);
        }

        private static void Postfix(
            MyCubeGrid __instance,
            MySlimBlock block,
            bool __result,
            (Vector3 color, MyStringHash skin) __state
        )
        {
            if (__result)
                Recorder.StrokeFor(__instance)?.Record(block, __state.color, __state.skin);
        }
    }
}

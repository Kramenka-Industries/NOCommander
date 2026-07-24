using HarmonyLib;
using UnityEngine;

namespace NuclearOptionCommander;

[HarmonyPatch(typeof(DynamicMap), "MapControls")]
internal static class CommanderTacticalMapControlsPatch
{
    private static bool Prefix(DynamicMap __instance)
    {
        CommanderTacticalMapService? tacticalMap = CommanderTacticalMapService.Instance;
        return tacticalMap?.IsOpen != true || __instance.IsCursorInMapRectangle();
    }
}

[HarmonyPatch(typeof(DynamicMap), "SelectFromMap")]
internal static class CommanderRallyMapSelectionPatch
{
    private static bool Prefix()
    {
        return CommanderSpawnService.Instance?.AwaitingRallyPointSelection != true;
    }
}

[HarmonyPatch(typeof(DynamicMap), "JumpCameraTo")]
internal static class CommanderDisableBaseMapJumpPatch
{
    private static bool Prefix()
    {
        return CommanderPlugin.Instance?.IsCommanderModeActive != true
            || CommanderTacticalMapService.AllowCommanderMapJump;
    }
}

[HarmonyPatch(typeof(ExtraUiInput), "Update")]
internal static class CommanderKeepTacticalMapOpenPatch
{
    private static bool Prefix()
    {
        if (CommanderPlugin.Instance?.IsCommanderModeActive != true)
        {
            return true;
        }

        if (CommanderTacticalMapService.Instance?.SuppressExtraUiThisFrame == true)
        {
            return false;
        }

        if (!CommanderGameInput.MapDown)
        {
            return true;
        }

        if (CommanderAirCommandUi.Instance?.HandleMapKey() == true)
        {
            return false;
        }

        return CommanderTacticalMapService.Instance?.HandleMapKey() != true;
    }
}

[HarmonyPatch(typeof(UnitMapIcon), nameof(UnitMapIcon.UpdateIcon))]
internal static class CommanderTacticalMapIconScalePatch
{
    private static void Postfix(UnitMapIcon __instance)
    {
        if (CommanderPlugin.Instance?.IsCommanderModeActive == true
            && CommanderTacticalMapService.Instance?.IsOpen == true
            && __instance.iconImage != null)
        {
            __instance.iconImage.transform.localScale *= 1.4f;
        }
    }
}

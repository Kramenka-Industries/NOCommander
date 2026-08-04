using HarmonyLib;
using NuclearOption.Networking;
using UnityEngine;

namespace NuclearOptionCommander;

[HarmonyPatch(typeof(DynamicMap), "MapControls")]
internal static class CommanderTacticalMapControlsPatch
{
    private static readonly AccessTools.FieldRef<DynamicMap, bool> FollowingCamera =
        AccessTools.FieldRefAccess<DynamicMap, bool>("followingCamera");
    private static readonly AccessTools.FieldRef<DynamicMap, Vector2> PositionOffset =
        AccessTools.FieldRefAccess<DynamicMap, Vector2>("positionOffset");
    private static readonly AccessTools.FieldRef<DynamicMap, Vector2> StationaryOffset =
        AccessTools.FieldRefAccess<DynamicMap, Vector2>("stationaryOffset");
    private static readonly AccessTools.FieldRef<DynamicMap, float> MapMoveMaxJumpSpeed =
        AccessTools.FieldRefAccess<DynamicMap, float>("mapMoveMaxJumpSpeed");

    private static bool Prefix(DynamicMap __instance)
    {
        CommanderTacticalMapService? tacticalMap = CommanderTacticalMapService.Instance;
        bool overCommanderUi = CommanderOverlayUi.Instance?.ContainsScreenPoint(Input.mousePosition) == true;
        if (overCommanderUi)
        {
            if (tacticalMap?.IsOpen == true)
            {
                UpdateCameraTracking(__instance);
            }
            return false;
        }

        if (tacticalMap?.IsOpen != true)
        {
            return true;
        }

        if (__instance.IsCursorInMapRectangle())
        {
            return true;
        }

        UpdateCameraTracking(__instance);
        return false;
    }

    private static void UpdateCameraTracking(DynamicMap map)
    {
        CameraStateManager? camera = SceneSingleton<CameraStateManager>.i;
        if (camera == null)
        {
            return;
        }

        Vector3 cameraPosition = camera.transform.position.ToGlobalPosition().AsVector3() * map.mapDisplayFactor;
        ref Vector2 positionOffset = ref PositionOffset(map);
        ref Vector2 stationaryOffset = ref StationaryOffset(map);
        if (FollowingCamera(map))
        {
            Vector2 target = new(cameraPosition.x, cameraPosition.z);
            Aircraft? aircraft = SceneSingleton<CombatHUD>.i?.aircraft;
            if (aircraft != null && !aircraft.disabled)
            {
                Vector3 forward = aircraft.transform.forward;
                stationaryOffset = target + 5000f * map.mapDisplayFactor * new Vector2(forward.x, forward.z);
            }
            else
            {
                stationaryOffset = Vector2.MoveTowards(stationaryOffset, target, MapMoveMaxJumpSpeed(map));
            }
        }

        map.mapImage.transform.localEulerAngles = Vector3.zero;
        map.mapScaleCenter.transform.localEulerAngles = Vector3.zero;
        Vector2 mapPosition = -stationaryOffset - positionOffset;
        ((RectTransform)map.mapBackground.transform).rect.ClampPos(ref mapPosition, 2f);
        positionOffset = -stationaryOffset - mapPosition;
        map.mapImage.transform.localPosition = mapPosition * map.mapImage.transform.localScale.x;
        map.viewIndicator.transform.localPosition = new Vector3(cameraPosition.x, cameraPosition.z, 0f);
        map.viewIndicator.transform.eulerAngles = new Vector3(
            0f,
            0f,
            map.mapImage.transform.eulerAngles.z - camera.transform.eulerAngles.y);
    }
}

[HarmonyPatch(typeof(DynamicMap), "SelectFromMap")]
internal static class CommanderRallyMapSelectionPatch
{
    private static bool Prefix()
    {
        return CommanderSpawnService.Instance?.AwaitingRallyPointSelection != true
            && CommanderNavalPurchaseService.Instance?.AwaitingRallySelection != true;
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

[HarmonyPatch(typeof(AirbaseMapIcon), nameof(AirbaseMapIcon.ClickIcon))]
internal static class CommanderAirbaseMapClickPatch
{
    private static bool Prefix(AirbaseMapIcon __instance)
    {
        if (CommanderPlugin.Instance?.IsCommanderModeActive != true)
        {
            return true;
        }

        CommanderAirCommandService? airCommand = CommanderAirCommandService.Instance;
        if (airCommand?.IsUiVisible == true)
        {
            airCommand.TrySelectAirbaseFromMap(__instance.airbase);
            return false;
        }

        // The compact Tactical Map is for command interaction and should never open
        // the Basegame aircraft-selection panel.
        return CommanderTacticalMapService.Instance?.IsOpen != true;
    }
}

[HarmonyPatch(typeof(AirbaseMapIcon), nameof(AirbaseMapIcon.UpdateIcon))]
internal static class CommanderAirCommandAirbaseIconPatch
{
    private static void Postfix(AirbaseMapIcon __instance)
    {
        CommanderAirCommandService? airCommand = CommanderAirCommandService.Instance;
        if (CommanderPlugin.Instance?.IsCommanderModeActive != true
            || airCommand?.IsUiVisible != true)
        {
            return;
        }

        bool selectable = airCommand.IsSelectableAirbase(__instance.airbase);
        __instance.gameObject.SetActive(DynamicMap.mapMaximized && selectable);
        if (selectable && __instance.iconImage != null && airCommand.IsSelectedAirbase(__instance.airbase))
        {
            __instance.iconImage.color = GameAssets.i.HUDFriendlySelected;
        }
    }
}

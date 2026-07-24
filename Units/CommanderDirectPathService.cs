using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace NuclearOptionCommander;

internal sealed class CommanderDirectPathService
{
    private static readonly FieldInfo? PathfindingUnitField = AccessTools.Field(typeof(PathfindingAgent), "unit");

    private readonly CommanderSelectionService selectionService;
    private readonly HashSet<GroundVehicle> directRouteVehicles = new();

    internal static CommanderDirectPathService? Instance { get; private set; }

    internal CommanderDirectPathService(CommanderSelectionService selectionService)
    {
        this.selectionService = selectionService;
        Instance = this;
    }

    internal bool CanConfigure(Unit? unit)
    {
        return unit is GroundVehicle vehicle
            && !vehicle.disabled
            && CommanderGameAccess.IsFriendlyUnit(vehicle, CommanderGameAccess.GetLocalHq())
            && !CommanderGameAccess.IsTrailerVehicleDefinition(vehicle.definition as VehicleDefinition);
    }

    internal bool IsEnabled(Unit? unit)
    {
        return unit is GroundVehicle vehicle && directRouteVehicles.Contains(vehicle);
    }

    internal void ToggleFocusedUnit()
    {
        if (selectionService.SelectedUnits.Count == 0)
        {
            return;
        }

        for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
        {
            Unit unit = selectionService.SelectedUnits[i];

            if (!CanConfigure(unit)
                || unit is not GroundVehicle vehicle)
            {
                continue;
            }

            GroundVehicle gveh = (GroundVehicle)unit;

            if (!directRouteVehicles.Remove(gveh))
            {
                directRouteVehicles.Add(gveh);
            }

            ReapplyCurrentDestination(gveh);
        }
    }

    internal void ResetSession()
    {
        directRouteVehicles.Clear();
    }

    internal void Deactivate()
    {
        directRouteVehicles.Clear();
    }

    internal static bool TryApplyShortcut(PathfindingAgent pathfinder, GlobalPosition target)
    {
        if (Instance == null
            || PathfindingUnitField?.GetValue(pathfinder) is not GroundVehicle vehicle
            || !Instance.directRouteVehicles.Contains(vehicle))
        {
            return false;
        }

        pathfinder.Shortcut(target);
        return true;
    }

    private static void ReapplyCurrentDestination(GroundVehicle vehicle)
    {
        UnitCommand? command = vehicle.UnitCommand;
        if (command == null)
        {
            return;
        }

        UnitCommand.Command current = command.GetCommandCached();
        if (current.time > 0f || current.player != null || !current.position.Equals(default(GlobalPosition)))
        {
            command.SetDestination(current.position, current.FromPlayer);
        }
    }
}

[HarmonyPatch(typeof(PathfindingAgent), nameof(PathfindingAgent.Pathfind))]
internal static class CommanderDirectPathPatch
{
    private static bool Prefix(PathfindingAgent __instance, GlobalPosition targetPos)
    {
        return !CommanderDirectPathService.TryApplyShortcut(__instance, targetPos);
    }
}

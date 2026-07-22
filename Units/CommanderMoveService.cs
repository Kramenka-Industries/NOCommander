using UnityEngine;
using System.Collections.Generic;
using NuclearOption.Networking;

namespace NuclearOptionCommander;

internal sealed class CommanderMoveService
{
    private readonly CommanderSelectionService selectionService;
    private readonly HashSet<Unit> stoppedUnits = new();
    private readonly Dictionary<Unit, GlobalPosition> playerDestinations = new();

    internal static CommanderMoveService? Instance { get; private set; }

    internal bool HasCommandableSelection
    {
        get
        {
            for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
            {
                if (CommanderGameAccess.ShouldAllowCommanderMove(selectionService.SelectedUnits[i]))
                {
                    return true;
                }
            }
            return false;
        }
    }

    internal CommanderMoveService(CommanderSelectionService selectionService)
    {
        this.selectionService = selectionService;
        Instance = this;
    }

    internal void TryIssueMoveOrder(Vector2 screenPosition)
    {
        if (selectionService.SelectedUnits.Count == 0)
        {
            return;
        }

        bool hasGroundDestination = CommanderGameAccess.TryRaycastWorldPosition(screenPosition, out GlobalPosition groundDestination);
        bool hasWaterDestination = CommanderGameAccess.TryRaycastWaterPosition(screenPosition, out GlobalPosition waterDestination);

        int commandableCount = 0;
        for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
        {
            if (CommanderGameAccess.ShouldAllowCommanderMove(selectionService.SelectedUnits[i]))
            {
                commandableCount++;
            }
        }

        float spacing = CommanderSettings.MoveSpacing;
        int assignedIndex = 0;

        for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
        {
            Unit unit = selectionService.SelectedUnits[i];
            if (!CommanderGameAccess.ShouldAllowCommanderMove(unit))
            {
                continue;
            }

            GlobalPosition destination;
            if (unit is Ship)
            {
                if (!hasWaterDestination) continue;
                destination = waterDestination;
            }
            else
            {
                if (!hasGroundDestination) continue;
                destination = groundDestination;
            }

            if (commandableCount > 1)
            {
                destination = ApplyFormationOffset(destination, assignedIndex, commandableCount, spacing);
                assignedIndex++;
            }

            UnitCommand? unitCommand = CommanderGameAccess.GetUnitCommand(unit);
            stoppedUnits.Remove(unit);
            CommanderGameAccess.SetUnitHoldPosition(unit, false);
            unitCommand?.SetDestination(destination, true);
        }
    }

    private static GlobalPosition ApplyFormationOffset(GlobalPosition center, int index, int count, float spacing)
    {
        string formation = CommanderSettings.MoveFormation;
        if (formation == "Line")
        {
            float offset = (index - (count - 1) / 2f) * spacing;
            return new GlobalPosition(center.x + offset, center.y, center.z);
        }
        else if (formation == "Quadratic")
        {
            int cols = Mathf.CeilToInt(Mathf.Sqrt(count));
            int row = index / cols;
            int col = index % cols;
            float offsetX = (col - (cols - 1) / 2f) * spacing;
            int rowsTotal = Mathf.CeilToInt((float)count / cols);
            float offsetZ = (row - (rowsTotal - 1) / 2f) * spacing;
            return new GlobalPosition(center.x + offsetX, center.y, center.z + offsetZ);
        }
        else
        {
            // Circular (default)
            if (count == 1) return center;
            float angle = 2f * Mathf.PI * index / count;
            float radius = count * spacing / (2f * Mathf.PI);
            float offsetX = Mathf.Cos(angle) * radius;
            float offsetZ = Mathf.Sin(angle) * radius;
            return new GlobalPosition(center.x + offsetX, center.y, center.z + offsetZ);
        }
    }

    internal void Tick()
    {
        stoppedUnits.RemoveWhere(static unit => unit == null || unit.disabled);
        List<Unit>? staleDestinations = null;
        foreach (KeyValuePair<Unit, GlobalPosition> entry in playerDestinations)
        {
            if (entry.Key != null
                && !entry.Key.disabled
                && CommanderGameAccess.HorizontalDistance(entry.Key.transform.position, entry.Value.ToLocalPosition()) > 25f)
            {
                continue;
            }

            staleDestinations ??= new List<Unit>();
            staleDestinations.Add(entry.Key!);
        }
        if (staleDestinations != null)
        {
            for (int i = 0; i < staleDestinations.Count; i++)
            {
                playerDestinations.Remove(staleDestinations[i]);
            }
        }
        if (stoppedUnits.Count == 0)
        {
            return;
        }
        foreach (Unit unit in stoppedUnits)
        {
            if (unit.rb == null)
            {
                continue;
            }

            unit.rb.velocity = Vector3.zero;
            unit.rb.angularVelocity = Vector3.zero;
        }
    }

    internal void StopSelectedUnits()
    {
        for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
        {
            Unit unit = selectionService.SelectedUnits[i];
            if (!CommanderGameAccess.ShouldAllowCommanderMove(unit))
            {
                continue;
            }

            CommanderGameAccess.SetUnitHoldPosition(unit, true);
            CommanderGameAccess.GetUnitCommand(unit)?.SetDestination(unit.transform.GlobalPosition(), false);
            stoppedUnits.Add(unit);
            playerDestinations.Remove(unit);
        }
    }

    internal void ResumeAiForSelectedUnits()
    {
        for (int i = 0; i < selectionService.SelectedUnits.Count; i++)
        {
            Unit unit = selectionService.SelectedUnits[i];
            if (!CommanderGameAccess.ShouldAllowCommanderMove(unit))
            {
                continue;
            }

            CommanderGameAccess.SetUnitHoldPosition(unit, false);
            stoppedUnits.Remove(unit);
            playerDestinations.Remove(unit);
            if (MissionPosition.TryGetClosestPosition(unit, out GlobalPosition destination))
            {
                CommanderGameAccess.GetUnitCommand(unit)?.SetDestination(destination, false);
            }
        }
    }

    internal bool TryGetPlayerDestination(Unit unit, out GlobalPosition destination)
    {
        return playerDestinations.TryGetValue(unit, out destination);
    }

    internal static void NotifyPlayerDestination(UnitCommand command, GlobalPosition destination, Player? player)
    {
        if (player == null || Instance == null)
        {
            return;
        }

        Unit? unit = command.GetComponent<Unit>();
        if (unit == null || !CommanderGameAccess.ShouldAllowCommanderMove(unit))
        {
            return;
        }

        Instance.stoppedUnits.Remove(unit);
        Instance.playerDestinations[unit] = destination;
    }
}

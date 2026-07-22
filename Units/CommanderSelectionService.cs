using System;
using System.Collections.Generic;
using UnityEngine;

namespace NuclearOptionCommander;

internal sealed class CommanderSelectionService
{
    private readonly List<Unit> selectedUnits = new();
    private readonly List<Unit> pinnedUnits = new();
    private readonly List<Unit> missionUnits = new();
    private readonly Dictionary<Unit, MissionPinInfo> missionInfo = new();
    private DynamicMap? boundMap;
    private Unit? commanderDetailUnit;

    internal IReadOnlyList<Unit> SelectedUnits => selectedUnits;
    internal IReadOnlyList<Unit> PinnedUnits => pinnedUnits;
    internal IReadOnlyList<Unit> MissionUnits => missionUnits;
    internal static CommanderSelectionService? Instance { get; private set; }

    private readonly List<Unit>[] controlGroups = new List<Unit>[9];

    internal CommanderSelectionService()
    {
        Instance = this;
        for (int i = 0; i < controlGroups.Length; i++)
        {
            controlGroups[i] = new List<Unit>();
        }
    }
    internal Unit? PrimarySelection => selectedUnits.Count > 0 ? selectedUnits[0] : null;
    internal Unit? FocusedSelection => GetDetailTargetUnit();

    internal void Activate()
    {
        commanderDetailUnit = null;
        BindMap();
        DeselectAll();
    }

    internal void Deactivate()
    {
        DeselectAll();
        ClearCommanderDetailUnit();
        CommanderGameAccess.RaiseFollowingUnitSet(null);
        UnbindMap();
    }

    internal void Tick()
    {
        BindMap();
        PruneDisabledUnits();
        SyncDetailUi();
    }

    internal void ResetSession()
    {
        DeselectAll();
        pinnedUnits.Clear();
        missionUnits.Clear();
        missionInfo.Clear();
        UnbindMap();
    }

    internal bool IsCurrentSelectionPinned
    {
        get
        {
            if (selectedUnits.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < selectedUnits.Count; i++)
            {
                if (!pinnedUnits.Contains(selectedUnits[i]) && !missionUnits.Contains(selectedUnits[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }

    internal bool CanDeleteSelection
    {
        get
        {
            FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
            if (selectedUnits.Count == 0 || localHq == null)
            {
                return false;
            }

            for (int i = 0; i < selectedUnits.Count; i++)
            {
                if (!CommanderGameAccess.IsFriendlyUnit(selectedUnits[i], localHq))
                {
                    return false;
                }
            }
            return true;
        }
    }

    internal void TogglePinSelected()
    {
        if (selectedUnits.Count == 0)
        {
            return;
        }

        bool remove = IsCurrentSelectionPinned;
        for (int i = 0; i < selectedUnits.Count; i++)
        {
            Unit unit = selectedUnits[i];
            if (remove)
            {
                pinnedUnits.Remove(unit);
                missionUnits.Remove(unit);
                missionInfo.Remove(unit);
            }
            else if (!pinnedUnits.Contains(unit))
            {
                pinnedUnits.Add(unit);
            }
        }
    }

    internal void SelectPinnedUnit(Unit unit)
    {
        if (unit == null || unit.disabled)
        {
            if (unit != null)
            {
                RemovePinnedUnit(unit);
            }
            return;
        }

        SelectUnit(unit, false);
    }

    internal void RemovePinnedUnit(Unit unit)
    {
        pinnedUnits.Remove(unit);
        missionUnits.Remove(unit);
        missionInfo.Remove(unit);
    }

    internal static void PinMissionUnit(Unit unit, string source, string mission)
    {
        if (Instance == null || unit == null || unit.disabled)
        {
            return;
        }
        if (!Instance.missionUnits.Contains(unit))
        {
            Instance.pinnedUnits.Remove(unit);
            Instance.missionUnits.Add(unit);
        }
        Instance.missionInfo[unit] = new MissionPinInfo(source, mission);
    }

    internal MissionPinInfo GetMissionInfo(Unit unit)
    {
        return missionInfo.TryGetValue(unit, out MissionPinInfo? info)
            ? info
            : new MissionPinInfo("MISSION", "Active mission");
    }

    internal void DeleteSelectedUnits()
    {
        if (selectedUnits.Count == 0)
        {
            return;
        }

        List<Unit> unitsToDelete = new(selectedUnits);
        DeselectAll();
        for (int i = 0; i < unitsToDelete.Count; i++)
        {
            Unit unit = unitsToDelete[i];
            if (!CommanderGameAccess.IsFriendlyUnit(unit, CommanderGameAccess.GetLocalHq()))
            {
                continue;
            }

            pinnedUnits.Remove(unit);
            missionUnits.Remove(unit);
            missionInfo.Remove(unit);
            unit.DisableUnit();
        }
    }

    internal bool IsSelected(Unit unit)
    {
        return selectedUnits.Contains(unit);
    }

    internal void SelectUnit(Unit unit, bool additive)
    {
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        if (!CommanderGameAccess.ShouldAllowCommanderSelection(unit, localHq))
        {
            return;
        }

        DynamicMap? dynamicMap = SceneSingleton<DynamicMap>.i;
        if (dynamicMap == null)
        {
            return;
        }

        if (!additive)
        {
            selectedUnits.Clear();
            dynamicMap.DeselectAllIcons();
        }

        dynamicMap.SelectIcon(unit);
    }

    internal void SelectUnitsInScreenRect(Rect screenRect)
    {
        Camera? camera = SceneSingleton<CameraStateManager>.i?.mainCamera;
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        if (camera == null || localHq == null || localHq.factionUnits == null)
        {
            return;
        }

        selectedUnits.Clear();
        SceneSingleton<DynamicMap>.i?.DeselectAllIcons();

        foreach (PersistentID unitId in localHq.factionUnits)
        {
            if (!unitId.TryGetUnit(out Unit unit))
            {
                continue;
            }

            if (!CommanderGameAccess.ShouldAllowCommanderSelection(unit, localHq))
            {
                continue;
            }

            Vector3 screenPos = camera.WorldToScreenPoint(unit.transform.position);
            if (screenPos.z <= 0f)
            {
                continue;
            }

            if (screenRect.Contains(new Vector2(screenPos.x, screenPos.y)))
            {
                SelectUnit(unit, true);
            }
        }
    }

    internal void AssignControlGroup(int groupIndex)
    {
        if (groupIndex < 0 || groupIndex >= controlGroups.Length)
        {
            return;
        }

        controlGroups[groupIndex].Clear();
        controlGroups[groupIndex].AddRange(selectedUnits);
    }

    internal void RecallControlGroup(int groupIndex)
    {
        if (groupIndex < 0 || groupIndex >= controlGroups.Length)
        {
            return;
        }

        List<Unit> group = controlGroups[groupIndex];
        for (int i = group.Count - 1; i >= 0; i--)
        {
            if (group[i] == null || group[i].disabled)
            {
                group.RemoveAt(i);
            }
        }

        if (group.Count == 0)
        {
            return;
        }

        DeselectAll();
        for (int i = 0; i < group.Count; i++)
        {
            SelectUnit(group[i], true);
        }

        CommanderCameraFollowService.Instance?.CenterOnSelectionIfFollowing();
    }

    internal void DeselectAll()
    {
        SceneSingleton<DynamicMap>.i?.DeselectAllIcons();
        selectedUnits.Clear();
        SyncDetailUi();
    }

    private void BindMap()
    {
        DynamicMap? dynamicMap = SceneSingleton<DynamicMap>.i;
        if (dynamicMap == boundMap || dynamicMap == null)
        {
            return;
        }

        UnbindMap();
        boundMap = dynamicMap;
        boundMap.onUnitSelected += OnUnitSelected;
        boundMap.onUnitDeselected += OnUnitDeselected;
        boundMap.onAllDeselected += OnAllDeselected;
    }

    private void UnbindMap()
    {
        if (boundMap == null)
        {
            return;
        }

        boundMap.onUnitSelected -= OnUnitSelected;
        boundMap.onUnitDeselected -= OnUnitDeselected;
        boundMap.onAllDeselected -= OnAllDeselected;
        boundMap = null;
        selectedUnits.Clear();
    }

    private void OnUnitSelected(Unit unit)
    {
        FactionHQ? localHq = CommanderGameAccess.GetLocalHq();
        if (!CommanderGameAccess.ShouldAllowCommanderSelection(unit, localHq))
        {
            return;
        }

        if (!selectedUnits.Contains(unit))
        {
            selectedUnits.Add(unit);
        }

        SyncDetailUi();
    }

    private void OnUnitDeselected(Unit unit)
    {
        selectedUnits.Remove(unit);
        SyncDetailUi();
    }

    private void OnAllDeselected()
    {
        selectedUnits.Clear();
        SyncDetailUi();
    }

    private void PruneDisabledUnits()
    {
        for (int i = selectedUnits.Count - 1; i >= 0; i--)
        {
            Unit unit = selectedUnits[i];
            if (unit == null || unit.disabled)
            {
                selectedUnits.RemoveAt(i);
            }
        }

        for (int i = pinnedUnits.Count - 1; i >= 0; i--)
        {
            Unit unit = pinnedUnits[i];
            if (unit == null || unit.disabled)
            {
                pinnedUnits.RemoveAt(i);
            }
        }

        for (int i = missionUnits.Count - 1; i >= 0; i--)
        {
            Unit unit = missionUnits[i];
            if (unit == null || unit.disabled)
            {
                missionUnits.RemoveAt(i);
                if (!ReferenceEquals(unit, null))
                {
                    missionInfo.Remove(unit);
                }
            }
        }
    }

    private void SyncDetailUi()
    {
        Unit? targetUnit = GetDetailTargetUnit();
        if (ReferenceEquals(targetUnit, commanderDetailUnit))
        {
            return;
        }

        ClearCommanderDetailUnit();
        commanderDetailUnit = targetUnit;

        if (commanderDetailUnit == null)
        {
            CommanderGameAccess.RaiseFollowingUnitSet(null);
            return;
        }

        CommanderGameAccess.RaiseFollowingUnitSet(commanderDetailUnit);
    }

    private Unit? GetDetailTargetUnit()
    {
        for (int i = selectedUnits.Count - 1; i >= 0; i--)
        {
            Unit unit = selectedUnits[i];
            if (unit != null && !unit.disabled)
            {
                return unit;
            }
        }

        return null;
    }

    private void ClearCommanderDetailUnit()
    {
        if (commanderDetailUnit == null)
        {
            return;
        }

        commanderDetailUnit = null;
    }

    internal sealed class MissionPinInfo
    {
        internal MissionPinInfo(string source, string mission)
        {
            Source = source;
            Mission = mission;
        }

        internal string Source { get; }
        internal string Mission { get; }
    }
}

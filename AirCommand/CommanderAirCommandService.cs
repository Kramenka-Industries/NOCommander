using System;
using System.Collections.Generic;
using System.Reflection;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace NuclearOptionCommander;

internal sealed partial class CommanderAirCommandService
{
    private const float PendingSpawnTimeoutSeconds = 45f;
    private const float StatusDurationSeconds = 6f;
    private const float MissionPruneIntervalSeconds = 2f;

    private readonly CommanderTacticalMapService tacticalMapService;
    private readonly CommanderMapClickTracker mapClickTracker = new();
    private readonly List<AirMissionOption> options = new();
    private readonly List<WeaponMount> weaponOptions = new();
    private readonly List<AirbaseOption> airbases = new();
    private readonly Dictionary<Aircraft, AirMission> missions = new();
    private readonly List<Aircraft> staleAircraft = new();

    private PendingAreaSelection? pendingAreaSelection;
    private PendingAircraftSpawn? pendingAircraftSpawn;
    private AirCommandMode selectedMode = AirCommandMode.AirGuard;
    private int selectedOptionIndex;
    private int selectedAirbaseIndex;
    private int selectedPrimaryWeaponIndex = -1;
    private int selectedSecondaryWeaponIndex = -1;
    private LoadoutBalance selectedLoadoutBalance = LoadoutBalance.Primary;
    private float selectedTargetAltitude;
    private float nextAirbaseRefreshAt;
    private float nextMissionPruneAt;
    private float statusUntil;
    private string statusText = string.Empty;
    private bool uiVisible;
    private Rect areaSelectionBlockingRect;
    private Rect areaSelectionSecondaryBlockingRect;
    private Aircraft? selectedMissionAircraft;

    internal static CommanderAirCommandService? Instance { get; private set; }

    internal CommanderAirCommandService(CommanderTacticalMapService tacticalMapService)
    {
        this.tacticalMapService = tacticalMapService;
        if (Enum.TryParse(CommanderSettings.AirCommandMode, out AirCommandMode savedMode)) selectedMode = savedMode;
        if (Enum.TryParse(CommanderSettings.AirLoadoutBalance, out LoadoutBalance savedBalance)) selectedLoadoutBalance = savedBalance;
        selectedTargetAltitude = CommanderSettings.AirTargetAltitude;
        Instance = this;
    }

    internal AirCommandMode SelectedMode => selectedMode;
    internal IReadOnlyList<AirMissionOption> Options => options;
    internal IReadOnlyList<AirbaseOption> Airbases => airbases;
    internal IReadOnlyList<WeaponMount> WeaponOptions => weaponOptions;
    internal int SelectedOptionIndex => selectedOptionIndex;
    internal int SelectedAirbaseIndex => selectedAirbaseIndex;
    internal int SelectedPrimaryWeaponIndex => selectedPrimaryWeaponIndex;
    internal int SelectedSecondaryWeaponIndex => selectedSecondaryWeaponIndex;
    internal LoadoutBalance SelectedLoadoutBalance => selectedLoadoutBalance;
    internal float SelectedTargetAltitude => selectedTargetAltitude;
    internal bool TargetOrdnance { get => CommanderSettings.AirGuardTargetOrdnance; set => CommanderSettings.AirGuardTargetOrdnance = value; }
    internal bool SaturationAttack { get => CommanderSettings.AradSaturationAttack; set => CommanderSettings.AradSaturationAttack = value; }
    internal bool IncludeInternalCannons
    {
        get => CommanderSettings.AirIncludeInternalCannons;
        set
        {
            if (CommanderSettings.AirIncludeInternalCannons == value) return;
            CommanderSettings.AirIncludeInternalCannons = value;
            ApplySelectedWeaponsAndSort();
        }
    }
    internal bool AwaitingAreaSelection => pendingAreaSelection != null;
    internal float PendingMissionRadius => pendingAreaSelection != null ? GetMissionRadius(pendingAreaSelection.Option.Mode) : 0f;
    internal int ActiveMissionCount => missions.Count;
    internal bool CanLaunchSelected => SelectedOption != null
        && SelectedPrimaryWeapon != null
        && GetPrimaryWeaponCount(SelectedOption) > 0
        && SelectedAirbase != null;
    internal string StatusText => Time.unscaledTime <= statusUntil ? statusText : string.Empty;
    internal WeaponMount? SelectedPrimaryWeapon => selectedPrimaryWeaponIndex >= 0 && selectedPrimaryWeaponIndex < weaponOptions.Count
        ? weaponOptions[selectedPrimaryWeaponIndex]
        : null;
    internal WeaponMount? SelectedSecondaryWeapon => selectedSecondaryWeaponIndex >= 0 && selectedSecondaryWeaponIndex < weaponOptions.Count
        ? weaponOptions[selectedSecondaryWeaponIndex]
        : null;

    internal AirMissionOption? SelectedOption => options.Count == 0
        ? null
        : options[Mathf.Clamp(selectedOptionIndex, 0, options.Count - 1)];

    internal AirbaseOption? SelectedAirbase => airbases.Count == 0
        ? null
        : airbases[Mathf.Clamp(selectedAirbaseIndex, 0, airbases.Count - 1)];

    internal void Activate()
    {
        nextMissionPruneAt = CommanderScheduler.Stagger("air-command.prune", MissionPruneIntervalSeconds, 0.7f);
        nextAirbaseRefreshAt = Time.unscaledTime;
        RefreshMissionMapVisuals();
    }

    internal void Deactivate()
    {
        uiVisible = false;
        CancelAreaSelection(showStatus: false);
        ClearMissionMapVisuals();
    }

    internal void ResetSession()
    {
        uiVisible = false;
        pendingAreaSelection = null;
        pendingAircraftSpawn = null;
        options.Clear();
        weaponOptions.Clear();
        airbases.Clear();
        ClearMissionMapVisuals();
        missions.Clear();
        staleAircraft.Clear();
        mapClickTracker.Reset();
        selectedPrimaryWeaponIndex = -1;
        selectedSecondaryWeaponIndex = -1;
        statusText = string.Empty;
    }

    internal void TickActive()
    {
        if (pendingAreaSelection != null)
        {
            if (CommanderGameInput.CancelDown)
            {
                CancelAreaSelection(showStatus: true);
            }
            else
            {
                TryHandleTacticalMapClick();
            }
        }

        if (uiVisible && Time.unscaledTime >= nextAirbaseRefreshAt)
        {
            nextAirbaseRefreshAt = Time.unscaledTime + 0.5f;
            RefreshAirbases();
        }
    }

    internal void TickPersistent()
    {
        if (pendingAircraftSpawn != null && Time.unscaledTime > pendingAircraftSpawn.ExpiresAt)
        {
            SetStatus($"Aircraft assignment timed out for {GetAircraftLabel(pendingAircraftSpawn.Option.Definition)}.");
            pendingAircraftSpawn = null;
        }

        if (CommanderScheduler.IsDue(ref nextMissionPruneAt, MissionPruneIntervalSeconds))
        {
            PruneMissions();
            RefreshMissionMapVisuals();
            ProcessReturningMissions();
        }

    }

    internal void SetUiVisible(bool visible)
    {
        uiVisible = visible;
        if (visible)
        {
            RefreshOptions();
        }
    }

    internal void SelectMode(AirCommandMode mode)
    {
        if (selectedMode == mode && options.Count > 0)
        {
            return;
        }

        selectedMode = mode;
        CommanderSettings.AirCommandMode = mode.ToString();
        selectedOptionIndex = 0;
        selectedAirbaseIndex = 0;
        selectedPrimaryWeaponIndex = -1;
        selectedSecondaryWeaponIndex = -1;
        RefreshOptions();
    }

    internal void SelectLoadoutBalance(LoadoutBalance balance)
    {
        if (selectedLoadoutBalance == balance)
        {
            return;
        }

        selectedLoadoutBalance = balance;
        CommanderSettings.AirLoadoutBalance = balance.ToString();
        ApplySelectedWeaponsAndSort();
    }

    internal void CancelAreaSelection()
    {
        CancelAreaSelection(showStatus: true);
    }

    internal void SetTargetAltitude(float altitude)
    {
        selectedTargetAltitude = SupportsTargetAltitude(selectedMode) ? Mathf.Max(altitude, 0f) : 0f;
        CommanderSettings.AirTargetAltitude = selectedTargetAltitude;
    }

    internal bool SupportsTargetAltitude() => SupportsTargetAltitude(selectedMode);

    internal void SetAreaSelectionBlockingRects(Rect primary, Rect secondary)
    {
        areaSelectionBlockingRect = primary;
        areaSelectionSecondaryBlockingRect = secondary;
    }

    internal static bool SupportsTargetAltitude(AirCommandMode mode)
    {
        return mode == AirCommandMode.AwacsJammer || mode == AirCommandMode.AirGuard;
    }

    internal void SelectOption(int index)
    {
        if (index < 0 || index >= options.Count)
        {
            return;
        }

        selectedOptionIndex = index;
        RefreshAirbases();
    }

    internal void SelectAirbase(int index)
    {
        if (index >= 0 && index < airbases.Count)
        {
            selectedAirbaseIndex = index;
        }
    }

    internal float SelectedMissionRadiusKm => GetMissionRadius(selectedMode) / 1000f;

    internal void StepMissionRadius(float deltaKm)
    {
        SetMissionRadius(selectedMode, Mathf.Clamp(SelectedMissionRadiusKm + deltaKm, 5f, 150f));
    }

    internal void CollectMissionAircraft(List<Aircraft> aircraft)
    {
        aircraft.Clear();
        foreach (Aircraft unit in missions.Keys)
        {
            if (unit != null && !unit.disabled) aircraft.Add(unit);
        }
        aircraft.Sort((left, right) => string.Compare(
            CommanderGameAccess.GetUnitLabel(left), CommanderGameAccess.GetUnitLabel(right), StringComparison.OrdinalIgnoreCase));
    }

    internal bool IsMissionAircraftSelected(Aircraft aircraft) => ReferenceEquals(selectedMissionAircraft, aircraft);

    internal void ToggleMissionAircraft(Aircraft aircraft)
    {
        selectedMissionAircraft = ReferenceEquals(selectedMissionAircraft, aircraft) ? null : aircraft;
        RefreshMissionMapVisuals();
    }

    internal void ClearMissionAircraftSelection()
    {
        selectedMissionAircraft = null;
        RefreshMissionMapVisuals();
    }

    internal string GetMissionAircraftLabel(Aircraft aircraft)
    {
        return missions.TryGetValue(aircraft, out AirMission mission)
            ? $"{CommanderGameAccess.GetUnitLabel(aircraft)}\n{GetModeLabel(mission.Mode)}  |  {(mission.Returning ? "RTB" : "ACTIVE")}"
            : CommanderGameAccess.GetUnitLabel(aircraft);
    }

    internal void RequestReturnToBase(Aircraft aircraft)
    {
        if (!missions.TryGetValue(aircraft, out AirMission mission)) return;
        mission.Returning = true;
        IssueReturnToBase(aircraft, mission);
        SetStatus($"{CommanderGameAccess.GetUnitLabel(aircraft)} ordered to RTB.");
    }

    internal void SelectPrimaryWeapon(int index)
    {
        selectedPrimaryWeaponIndex = index >= 0 && index < weaponOptions.Count ? index : -1;
        if (SelectedPrimaryWeapon != null && SameMountType(SelectedPrimaryWeapon, SelectedSecondaryWeapon))
        {
            selectedSecondaryWeaponIndex = -1;
        }
        ApplySelectedWeaponsAndSort();
    }

    internal void SelectSecondaryWeapon(int index)
    {
        WeaponMount? candidate = index >= 0 && index < weaponOptions.Count ? weaponOptions[index] : null;
        selectedSecondaryWeaponIndex = candidate != null && !SameMountType(candidate, SelectedPrimaryWeapon) ? index : -1;
        ApplySelectedWeaponsAndSort();
    }

    internal void BeginAreaSelection()
    {
        AirMissionOption? option = SelectedOption;
        AirbaseOption? airbase = SelectedAirbase;
        if (option == null)
        {
            SetStatus("No aircraft with configurable hardpoints is available.");
            return;
        }

        if (SelectedPrimaryWeapon == null || GetPrimaryWeaponCount(option) <= 0)
        {
            SetStatus("Select a primary weapon supported by the selected aircraft.");
            return;
        }

        if (airbase == null)
        {
            SetStatus("No compatible friendly airbase is available.");
            return;
        }

        pendingAreaSelection = new PendingAreaSelection(option, airbase.Airbase);
        tacticalMapService.OpenFullscreen();
        tacticalMapService.SuppressMapFollow = true;
        mapClickTracker.Reset();
        UpdatePendingAreaPreview();
        SetStatus("Select the mission area on the tactical map or in the 3D world. The game's Cancel binding cancels.");
    }

    internal bool TrySetAreaFromWorld(Vector2 screenPosition)
    {
        if (pendingAreaSelection == null)
        {
            return false;
        }

        if (!CommanderGameAccess.TryRaycastWorldPosition(screenPosition, out GlobalPosition target))
        {
            SetStatus("No valid mission area was found under the cursor.");
            return true;
        }

        CompleteAreaSelection(target);
        return true;
    }

    internal string GetOptionLabel(AirMissionOption option)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        int supply = hq?.GetUnitSupply(option.Definition) ?? 0;
        string cost = UnitConverter.ValueReading(option.Definition.value) ?? option.Definition.value.ToString("F1");
        return $"{GetAircraftLabel(option.Definition)}  |  PRI {GetPrimaryWeaponCount(option)} / SEC {GetSecondaryWeaponCount(option)}\nSupply {supply}  |  Cost {cost}";
    }

    internal string GetAirbaseLabel(AirbaseOption option)
    {
        string distance = UnitConverter.DistanceReading(option.Distance) ?? $"{option.Distance:F0} m";
        return $"{(option.Ready ? "READY" : "BUSY")}  |  {option.Label}  |  {distance}";
    }

    internal static void NotifyFactionUnitRegistered(FactionHQ hq, Unit unit)
    {
        Instance?.TryAssignPendingAircraft(hq, unit);
    }

    internal static void NotifyAircraftReturned(Aircraft aircraft)
    {
        Instance?.RemoveMission(aircraft);
    }

    internal static void NotifyUnitDisabled(Unit unit)
    {
        if (unit is Aircraft aircraft)
        {
            Instance?.RemoveMission(aircraft);
        }
    }

    internal static bool TryChooseMissionTarget(
        Unit searcher,
        List<WeaponStation> stations,
        out CombatAI.TargetSearchResults result)
    {
        result = default;
        if (Instance == null
            || searcher is not Aircraft aircraft
            || !Instance.missions.TryGetValue(aircraft, out AirMission mission))
        {
            return false;
        }

        if (KeepsStationInMissionArea(mission.Mode)
            && mission.Mode != AirCommandMode.AwacsJammer
            && !FastMath.InRange(aircraft.GlobalPosition(), mission.AreaCenter, mission.Radius))
        {
            result = new CombatAI.TargetSearchResults(null!, null!, 0f, false);
            return true;
        }

        if (mission.Returning)
        {
            result = new CombatAI.TargetSearchResults(null!, null!, 0f, true);
            return true;
        }

        result = Instance.ChooseMissionTarget(aircraft, stations, mission);
        if (result.outOfAmmo)
        {
            mission.Returning = true;
        }
        return true;
    }

    internal static bool TryBuildAradSaturationTargets(
        Aircraft aircraft,
        WeaponStation station,
        out int targetCount)
    {
        targetCount = 0;
        if (Instance == null
            || aircraft == null
            || station == null
            || !Instance.missions.TryGetValue(aircraft, out AirMission mission)
            || mission.Returning
            || mission.Mode != AirCommandMode.Arad
            || !mission.SaturationAttack)
        {
            return false;
        }

        WeaponManager manager = aircraft.weaponManager;
        if (station.SalvoInProgress)
        {
            targetCount = manager.GetTargetList().Count;
            return true;
        }

        FactionHQ? hq = aircraft.NetworkHQ;
        WeaponInfo? info = station.WeaponInfo;
        if (hq == null || info == null || station.Ammo <= 0)
        {
            manager.ClearTargetList();
            return true;
        }

        List<Unit> candidates = new();
        foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
        {
            TrackingInfo tracking = entry.Value;
            if (!tracking.TryGetUnit(out Unit target)
                || target == null
                || target.disabled
                || target.NetworkHQ == null
                || target.NetworkHQ == hq
                || !IsTargetEligible(target, AirCommandMode.Arad, targetOrdnance: false)
                || !FastMath.InRange(tracking.GetPosition(), mission.AreaCenter, mission.Radius)
                || !hq.IsTargetPositionAccurate(target, 1000f))
            {
                continue;
            }

            float range = FastMath.Distance(tracking.GetPosition(), aircraft.GlobalPosition());
            Vector3 direction = tracking.GetPosition() - aircraft.GlobalPosition();
            if (range < info.targetRequirements.minRange
                || range > info.targetRequirements.maxRange
                || Vector3.Angle(direction, aircraft.transform.forward) >= info.targetRequirements.minAlignment
                || (info.targetRequirements.lineOfSight && !target.LineOfSight(aircraft.transform.position, 1000f))
                || station.CalcOpportunityThreat(target.definition, aircraft).opportunity <= 0f)
            {
                continue;
            }

            candidates.Add(target);
        }

        manager.ClearTargetList();
        if (candidates.Count == 0)
        {
            return true;
        }

        int missileCount = Mathf.Min(station.Ammo, 32);
        for (int i = 0; i < missileCount; i++)
        {
            manager.AddTargetList(candidates[i % candidates.Count]);
        }

        targetCount = missileCount;
        return true;
    }

    internal static bool TryGetMissionHoldPoint(AIPilotCombatModes state, out GlobalPosition point)
    {
        point = default;
        if (Instance == null || Instance.missions.Count == 0)
        {
            return false;
        }

        Aircraft? aircraft = CommanderAirCommandPatches.GetStateAircraft(state);
        if (aircraft == null || !Instance.missions.TryGetValue(aircraft, out AirMission mission))
        {
            return false;
        }

        if (mission.Returning) return false;
        point = mission.AreaCenter;
        return true;
    }

    internal static void ApplyMissionTargetAltitude(AIPilotCombatModes state, FieldInfo? targetHeightField)
    {
        if (Instance == null || Instance.missions.Count == 0 || targetHeightField == null)
        {
            return;
        }

        Aircraft? aircraft = CommanderAirCommandPatches.GetStateAircraft(state);
        if (aircraft == null
            || !Instance.missions.TryGetValue(aircraft, out AirMission mission)
            || mission.TargetAltitude <= 0f)
        {
            return;
        }

        targetHeightField.SetValue(state, mission.TargetAltitude);
    }

    internal static void ConstrainMissionDestination(AIPilotCombatModes state, FieldInfo? destinationField)
    {
        if (Instance == null || destinationField == null)
        {
            return;
        }

        Aircraft? aircraft = CommanderAirCommandPatches.GetStateAircraft(state);
        if (aircraft == null
            || !Instance.missions.TryGetValue(aircraft, out AirMission mission)
            || mission.Returning
            || !KeepsStationInMissionArea(mission.Mode))
        {
            return;
        }

        object? rawDestination = destinationField.GetValue(state);
        if (rawDestination is not GlobalPosition destination)
        {
            return;
        }

        float radius = Mathf.Max(mission.Radius, 1000f);
        Vector3 destinationOffset = destination - mission.AreaCenter;
        destinationOffset.y = 0f;
        Vector3 aircraftOffset = aircraft.GlobalPosition() - mission.AreaCenter;
        aircraftOffset.y = 0f;

        float destinationLimit = radius * 0.75f;
        if (aircraftOffset.magnitude > radius * 0.9f)
        {
            destination = mission.AreaCenter;
        }
        else if (destinationOffset.magnitude > destinationLimit)
        {
            destination = mission.AreaCenter + destinationOffset.normalized * destinationLimit;
        }
        else
        {
            return;
        }

        destinationField.SetValue(state, destination);
    }

    private CombatAI.TargetSearchResults ChooseMissionTarget(
        Aircraft aircraft,
        List<WeaponStation> stations,
        AirMission mission)
    {
        Unit? bestTarget = null;
        WeaponStation? bestStation = null;
        float bestOpportunity = 0f;
        float bestScore = 0f;
        bool outOfAmmo = mission.Mode != AirCommandMode.AwacsJammer || aircraft.radar == null;
        FactionHQ? hq = aircraft.NetworkHQ;
        if (hq == null)
        {
            return new CombatAI.TargetSearchResults(null!, null!, 0f, true);
        }

        for (int stationIndex = 0; stationIndex < stations.Count; stationIndex++)
        {
            WeaponStation station = stations[stationIndex];
            if (station == null || station.Cargo || !IsStationEligible(station, mission.Mode))
            {
                continue;
            }

            if (station.Ammo <= 0)
            {
                continue;
            }

            outOfAmmo = false;
            foreach (KeyValuePair<PersistentID, TrackingInfo> entry in hq.trackingDatabase)
            {
                TrackingInfo tracking = entry.Value;
                if (!tracking.TryGetUnit(out Unit target)
                    || target == null
                    || target.disabled
                    || target.NetworkHQ == null
                    || target.NetworkHQ == hq
                    || !IsTargetEligible(target, mission.Mode, mission.TargetOrdnance)
                    || (mission.Mode != AirCommandMode.AwacsJammer
                        && !FastMath.InRange(tracking.GetPosition(), mission.AreaCenter, mission.Radius)))
                {
                    continue;
                }

                float range = Mathf.Max(FastMath.Distance(tracking.GetPosition(), aircraft.GlobalPosition()), 100f);
                if (mission.Mode == AirCommandMode.AwacsJammer
                    && (range > station.WeaponInfo.targetRequirements.maxRange
                        || !target.LineOfSight(aircraft.transform.position, 1000f)))
                {
                    continue;
                }

                OpportunityThreat assessment = CombatAI.AnalyzeTarget(station, aircraft, tracking, 0f, range, mobile: true);
                float score = assessment.GetCombinedScore() / range;
                float requiredAccuracy = mission.Mode == AirCommandMode.AwacsJammer ? 100f : 1000f;
                if (score <= bestScore || !hq.IsTargetPositionAccurate(target, requiredAccuracy))
                {
                    continue;
                }

                bestScore = score;
                bestOpportunity = assessment.opportunity;
                bestTarget = target;
                bestStation = station;
            }
        }

        return new CombatAI.TargetSearchResults(bestTarget!, bestStation!, bestOpportunity, outOfAmmo);
    }

    private void RefreshOptions()
    {
        options.Clear();
        airbases.Clear();
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (!uiVisible || hq == null)
        {
            return;
        }

        List<AircraftDefinition> definitions = new();
        HashSet<AircraftDefinition> seen = new();
        Encyclopedia encyclopedia = Encyclopedia.i;
        if (encyclopedia?.aircraft != null)
        {
            for (int i = 0; i < encyclopedia.aircraft.Count; i++)
            {
                AircraftDefinition definition = encyclopedia.aircraft[i];
                if (definition != null && seen.Add(definition))
                {
                    definitions.Add(definition);
                }
            }
        }

        AircraftDefinition[] resourceDefinitions = Resources.FindObjectsOfTypeAll<AircraftDefinition>();
        for (int i = 0; i < resourceDefinitions.Length; i++)
        {
            AircraftDefinition definition = resourceDefinitions[i];
            if (definition != null && seen.Add(definition))
            {
                definitions.Add(definition);
            }
        }

        for (int i = 0; i < definitions.Count; i++)
        {
            AircraftDefinition definition = definitions[i];
            if (definition == null
                || definition.unitPrefab == null
                || definition.aircraftParameters == null)
            {
                continue;
            }

            if (!HasPlanePilot(definition))
            {
                continue;
            }

            AirMissionOption? option = CreateVariableLoadoutOption(definition, hq, selectedMode);
            if (option == null)
            {
                continue;
            }

            bool supported = false;
            foreach (Airbase airbase in hq.GetAirbases())
            {
                if (IsCompatibleAirbase(airbase, hq, definition))
                {
                    supported = true;
                    break;
                }
            }

            if (supported)
            {
                options.Add(option);
            }
        }

        BuildWeaponOptions();
        if (selectedPrimaryWeaponIndex < 0)
        {
            selectedPrimaryWeaponIndex = FindFirstSuitableWeaponIndex();
        }
        ApplySelectedWeaponsAndSort();
        RefreshAirbases();
    }

    private void RefreshAirbases()
    {
        Airbase? previouslySelected = SelectedAirbase?.Airbase;
        airbases.Clear();
        AirMissionOption? option = SelectedOption;
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (option == null || hq == null)
        {
            selectedAirbaseIndex = 0;
            return;
        }

        Camera? camera = SceneSingleton<CameraStateManager>.i?.mainCamera;
        Vector3 cameraPosition = camera != null ? camera.transform.position : Vector3.zero;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (!IsCompatibleAirbase(airbase, hq, option.Definition))
            {
                continue;
            }

            Transform position = airbase.center != null ? airbase.center : airbase.transform;
            airbases.Add(new AirbaseOption(
                airbase,
                GetAirbaseName(airbase),
                Vector3.Distance(cameraPosition, position.position),
                airbase.CanSpawnAircraft(option.Definition)));
        }

        airbases.Sort(static (left, right) => left.Distance.CompareTo(right.Distance));
        int preservedIndex = previouslySelected == null
            ? -1
            : airbases.FindIndex(option => ReferenceEquals(option.Airbase, previouslySelected));
        selectedAirbaseIndex = preservedIndex >= 0
            ? preservedIndex
            : Mathf.Clamp(selectedAirbaseIndex, 0, Mathf.Max(airbases.Count - 1, 0));
    }

    private void TryHandleTacticalMapClick()
    {
        if (!DynamicMap.mapMaximized)
        {
            return;
        }

        DynamicMap? map = SceneSingleton<DynamicMap>.i;
        UpdatePendingAreaPreview();
        Vector2 guiMouse = CommanderUiScale.ScreenToGui(Input.mousePosition);
        if (areaSelectionBlockingRect.Contains(guiMouse) || areaSelectionSecondaryBlockingRect.Contains(guiMouse))
        {
            mapClickTracker.Reset();
            return;
        }
        if (map != null && mapClickTracker.Tick(map, out GlobalPosition target))
        {
            CompleteAreaSelection(target);
        }
    }

    private void CompleteAreaSelection(GlobalPosition target)
    {
        PendingAreaSelection? selection = pendingAreaSelection;
        pendingAreaSelection = null;
        DestroyPendingAreaPreview();
        tacticalMapService.SuppressMapFollow = false;
        mapClickTracker.Reset();
        if (!uiVisible) tacticalMapService.CloseFullscreen();
        if (selection == null)
        {
            return;
        }

        SpawnMission(selection.Option, selection.Airbase, target);
    }

    private void CancelAreaSelection(bool showStatus)
    {
        if (pendingAreaSelection == null)
        {
            return;
        }

        pendingAreaSelection = null;
        DestroyPendingAreaPreview();
        tacticalMapService.SuppressMapFollow = false;
        mapClickTracker.Reset();
        if (!uiVisible && tacticalMapService.IsFullscreenOpen)
        {
            tacticalMapService.CloseFullscreen();
        }
        if (showStatus)
        {
            SetStatus("Air mission area selection cancelled.");
        }
    }

    private void SpawnMission(AirMissionOption option, Airbase airbase, GlobalPosition target)
    {
        if (NetworkManagerNuclearOption.i == null || !NetworkManagerNuclearOption.i.Server.Active)
        {
            SetStatus("Air Command is host-only.");
            return;
        }

        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq == null || !IsCompatibleAirbase(airbase, hq, option.Definition))
        {
            SetStatus("The selected airbase is no longer compatible.");
            return;
        }

        if (!airbase.CanSpawnAircraft(option.Definition))
        {
            SetStatus("The selected airbase is busy. Retry when a compatible hangar is free.");
            return;
        }

        if (pendingAircraftSpawn != null)
        {
            SetStatus("Wait for the previous Air Command aircraft to finish spawning.");
            return;
        }

        bool purchased = false;
        if (hq.GetUnitSupply(option.Definition) <= 0)
        {
            if (hq.factionFunds < option.Definition.value)
            {
                SetStatus("The faction cannot afford this aircraft.");
                return;
            }

            hq.AddFunds(-option.Definition.value);
            hq.ModifyUnitSupply(option.Definition, 1);
            purchased = true;
        }

        pendingAircraftSpawn = new PendingAircraftSpawn(
            hq,
            option,
            target,
            GetMissionRadius(option.Mode),
            SupportsTargetAltitude(option.Mode) ? selectedTargetAltitude : 0f,
            option.Mode == AirCommandMode.AirGuard && TargetOrdnance,
            option.Mode == AirCommandMode.Arad && SaturationAttack,
            Time.unscaledTime + PendingSpawnTimeoutSeconds);

        int liveryIndex = option.Definition.aircraftParameters.GetRandomLiveryForFaction(hq.faction);
        Loadout loadout = option.BuildLoadout();
        NormalizeLoadoutLength(loadout, option.Definition);
        if (!ValidateSelectedLoadout(option, loadout, airbase, hq, out string loadoutError))
        {
            pendingAircraftSpawn = null;
            if (purchased)
            {
                hq.ModifyUnitSupply(option.Definition, -1);
                hq.AddFunds(option.Definition.value);
            }
            SetStatus(loadoutError);
            return;
        }
        Airbase.TrySpawnResult result = airbase.TrySpawnAircraft(
            null,
            option.Definition,
            new LiveryKey(liveryIndex),
            loadout,
            option.Definition.aircraftParameters.DefaultFuelLevel);
        if (!result.Allowed)
        {
            pendingAircraftSpawn = null;
            if (purchased)
            {
                hq.ModifyUnitSupply(option.Definition, -1);
                hq.AddFunds(option.Definition.value);
            }
            SetStatus("The selected airbase rejected the aircraft spawn.");
            return;
        }

        SetStatus($"{GetModeLabel(option.Mode)} mission launched: {GetAircraftLabel(option.Definition)} / {option.LoadoutName}.");
    }

    private void TryAssignPendingAircraft(FactionHQ hq, Unit unit)
    {
        PendingAircraftSpawn? pending = pendingAircraftSpawn;
        if (pending == null
            || !ReferenceEquals(hq, pending.Hq)
            || unit is not Aircraft aircraft
            || aircraft.Player != null
            || !ReferenceEquals(aircraft.definition, pending.Option.Definition))
        {
            return;
        }

        AirMission mission = new(
            pending.Option.Mode,
            pending.AreaCenter,
            pending.Radius,
            pending.TargetAltitude,
            pending.TargetOrdnance,
            pending.SaturationAttack);
        missions[aircraft] = mission;
        CommanderSelectionService.PinMissionUnit(
            aircraft,
            "AIR COMMAND",
            GetModeLabel(pending.Option.Mode));
        pendingAircraftSpawn = null;
    }

    private void PruneMissions()
    {
        staleAircraft.Clear();
        foreach (KeyValuePair<Aircraft, AirMission> entry in missions)
        {
            if (entry.Key == null || entry.Key.disabled)
            {
                staleAircraft.Add(entry.Key!);
            }
        }

        for (int i = 0; i < staleAircraft.Count; i++)
        {
            RemoveMission(staleAircraft[i]);
        }
    }

    private static bool TryFindBestLoadout(
        AircraftDefinition definition,
        FactionHQ hq,
        AirCommandMode mode,
        out StandardLoadout loadout,
        out float score)
    {
        loadout = null!;
        score = 0f;
        StandardLoadout[] standardLoadouts = definition.aircraftParameters.StandardLoadouts;
        Aircraft? aircraftPrefab = definition.unitPrefab.GetComponent<Aircraft>();
        if (standardLoadouts == null || aircraftPrefab?.weaponManager == null)
        {
            return false;
        }

        for (int i = 0; i < standardLoadouts.Length; i++)
        {
            StandardLoadout candidate = standardLoadouts[i];
            if (candidate == null || candidate.disabled || candidate.loadout == null)
            {
                continue;
            }

            float candidateScore = ScoreLoadout(candidate.loadout, mode, definition);
            if (candidateScore > score)
            {
                score = candidateScore;
                loadout = candidate;
            }
        }

        return loadout != null && score > 0f;
    }

    private static float ScoreLoadout(
        Loadout loadout,
        AirCommandMode mode,
        AircraftDefinition? aircraftDefinition = null)
    {
        float score = 0f;
        bool hasRadar = false;
        bool hasJammer = false;
        float radarRange = 0f;
        float jammerRange = 0f;
        int remainingLaserTargets = GetLaserTargetCapacity(loadout, aircraftDefinition);
        for (int i = 0; i < loadout.weapons.Count; i++)
        {
            WeaponMount? mount = loadout.weapons[i];
            if (mount == null)
            {
                continue;
            }

            WeaponInfo? info = mount.info;
            switch (mode)
            {
                case AirCommandMode.AwacsJammer:
                    SpecialAirSystem specialSystem = GetSpecialAirSystem(mount);
                    if (specialSystem == SpecialAirSystem.Radar)
                    {
                        hasRadar = true;
                        radarRange = Mathf.Max(radarRange, GetSpecialSystemRange(mount));
                    }
                    if (specialSystem == SpecialAirSystem.RadarJammer)
                    {
                        hasJammer = true;
                        jammerRange = Mathf.Max(jammerRange, GetSpecialSystemRange(mount));
                    }
                    break;

                case AirCommandMode.Cas:
                    if (info != null
                        && !info.nuclear
                        && !info.jammer
                        && !IsStrategicStrikeWeapon(info, mount)
                        && info.effectiveness.antiSurface > 0.05f)
                    {
                        score += ScoreConventionalWeapon(
                            info.effectiveness.antiSurface, mount, info, ref remainingLaserTargets);
                    }
                    break;

                case AirCommandMode.AirGuard:
                    if (info != null
                        && !info.jammer
                        && info.effectiveness.antiAir > 0.05f
                        && info.effectiveness.antiSurface <= 0.05f)
                    {
                        score += ScoreConventionalWeapon(
                            info.effectiveness.antiAir, mount, info, ref remainingLaserTargets);
                    }
                    break;

                case AirCommandMode.Arad:
                    if (info != null && IsAradWeapon(info))
                    {
                        score += ScoreConventionalWeapon(
                            Mathf.Max(info.effectiveness.antiRadar, 0.5f), mount, info, ref remainingLaserTargets);
                    }
                    break;

                case AirCommandMode.StrategicStrike:
                    if (info != null && IsStrategicStrikeWeapon(info, mount))
                    {
                        score += 20f + ScoreConventionalWeapon(
                            Mathf.Max(info.effectiveness.antiSurface, info.effectiveness.antiRadar, 0.5f),
                            mount,
                            info,
                            ref remainingLaserTargets);
                    }
                    break;
            }
        }

        if (mode == AirCommandMode.AwacsJammer)
        {
            if (hasRadar) score += 16f + radarRange / 10000f;
            if (hasJammer) score += 8f + jammerRange / 10000f;
        }

        return score;
    }

    private static float ScoreConventionalWeapon(
        float effectiveness,
        WeaponMount mount,
        WeaponInfo info,
        ref int remainingLaserTargets)
    {
        int usefulStores = Mathf.Max(mount.ammo, 1);
        if (info.laserGuided)
        {
            usefulStores = Mathf.Min(usefulStores, Mathf.Max(remainingLaserTargets, 0));
            remainingLaserTargets = Mathf.Max(remainingLaserTargets - usefulStores, 0);
            if (usefulStores == 0)
            {
                return 0f;
            }
        }

        // Guns use round counts several orders of magnitude above discrete stores.
        float quantity = info.gun ? 1f : Mathf.Sqrt(usefulStores);
        float delivery = 1f;
        if (info.gun) delivery *= 0.5f;
        if (info.missile) delivery *= 1.12f;
        if (info.bomb) delivery *= 0.88f;
        if (info.glideBomb) delivery *= 1.28f;
        if (info.overHorizon) delivery *= 1.2f;
        if (info.laserGuided) delivery *= 0.78f;

        if (info.missile)
        {
            float speed = info.GetMaxSpeed();
            if (speed > 0f)
            {
                delivery *= speed switch
                {
                    < 250f => Mathf.Lerp(0.4f, 0.58f, speed / 250f),
                    < 400f => Mathf.Lerp(0.58f, 0.82f, (speed - 250f) / 150f),
                    < 700f => Mathf.Lerp(0.82f, 1.08f, (speed - 400f) / 300f),
                    _ => Mathf.Clamp(1.08f + (speed - 700f) / 3000f, 1.08f, 1.25f),
                };
            }
        }

        float maxRange = Mathf.Max(info.targetRequirements.maxRange, 1000f);
        float rangeFactor = Mathf.Clamp(Mathf.Sqrt(maxRange / 10000f), 0.72f, 1.45f);
        string identity = GetWeaponIdentity(mount, info);
        if (ContainsWeaponToken(identity, "AGM-48", "AGM48", "AGM-68", "AGM68")) delivery *= 1.42f;
        if (ContainsWeaponToken(identity, "AGM-99", "AGM99")) delivery *= 0.4f;
        if (ContainsWeaponToken(identity, "KINGPIN")) delivery *= 1.35f;
        if (info.glideBomb && ContainsWeaponToken(identity, "CLUSTER")) delivery *= 1.22f;

        return effectiveness * quantity * delivery * rangeFactor;
    }

    private static int GetLaserTargetCapacity(Loadout loadout, AircraftDefinition? definition)
    {
        int capacity = 0;
        LaserDesignator? builtIn = definition?.unitPrefab?.GetComponentInChildren<LaserDesignator>(true);
        if (builtIn != null)
        {
            capacity = Mathf.Max(capacity, builtIn.GetMaxTargets());
        }
        for (int i = 0; i < loadout.weapons.Count; i++)
        {
            LaserDesignator? mounted = loadout.weapons[i]?.prefab?.GetComponentInChildren<LaserDesignator>(true);
            if (mounted != null)
            {
                capacity = Mathf.Max(capacity, mounted.GetMaxTargets());
            }
        }
        return Mathf.Max(capacity, 1);
    }

    private static bool IsStrategicStrikeWeapon(WeaponInfo info, WeaponMount? mount = null)
    {
        if (info.strategic)
        {
            return true;
        }
        GameObject? prefab = info.weaponPrefab;
        if (prefab != null
            && (prefab.GetComponent<OpticalSeekerCruiseMissile>() != null
                || prefab.GetComponent<BallisticMissileGuidance>() != null))
        {
            return true;
        }
        return ContainsWeaponToken(
            GetWeaponIdentity(mount, info),
            "CRUISE",
            "TBM",
            "BALLISTIC",
            "TUSKO-B",
            "TUSKO B",
            "TUSKOB");
    }

    private static string GetWeaponIdentity(WeaponMount? mount, WeaponInfo info)
    {
        return string.Join("|", new[]
        {
            info.weaponName,
            info.shortName,
            info.name,
            mount?.mountName,
            mount?.jsonKey,
            mount?.name,
        });
    }

    private static bool ContainsWeaponToken(string identity, params string[] tokens)
    {
        for (int i = 0; i < tokens.Length; i++)
        {
            if (identity.IndexOf(tokens[i], StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsStationEligible(WeaponStation station, AirCommandMode mode)
    {
        WeaponInfo? info = station.WeaponInfo;
        if (info == null)
        {
            return false;
        }

        return mode switch
        {
            AirCommandMode.AwacsJammer => info.jammer,
            AirCommandMode.Cas => !info.nuclear
                && !info.jammer
                && !IsStrategicStrikeWeapon(info)
                && info.effectiveness.antiSurface > 0.05f,
            // Dual-role weapons remain manually usable, but ScoreLoadout keeps
            // them out of the recommended Air Superiority section.
            AirCommandMode.AirGuard => !info.jammer && info.effectiveness.antiAir > 0.05f,
            AirCommandMode.Arad => IsAradWeapon(info),
            AirCommandMode.StrategicStrike => IsStrategicStrikeWeapon(info),
            _ => false,
        };
    }

    private static bool IsTargetEligible(Unit target, AirCommandMode mode, bool targetOrdnance)
    {
        return mode switch
        {
            AirCommandMode.AwacsJammer => target.HasRadarEmission(),
            AirCommandMode.Cas => target is GroundVehicle || target is Ship || target is Building,
            AirCommandMode.AirGuard => target is Aircraft || (targetOrdnance && target is Missile),
            AirCommandMode.Arad => target is not Aircraft && target.HasRadarEmission(),
            AirCommandMode.StrategicStrike => target is GroundVehicle || target is Ship || target is Building,
            _ => false,
        };
    }

    private static bool IsAradWeapon(WeaponInfo info)
    {
        return info.targetRequirements.minRadar > 0f
            || info.effectiveness.antiRadar > Mathf.Max(info.effectiveness.antiAir, 0.05f)
            || info.weaponPrefab?.GetComponentInChildren<ARMSeeker>(true) != null
            || info.weaponName?.IndexOf("ARAD", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void NormalizeLoadoutLength(Loadout loadout, AircraftDefinition definition)
    {
        Aircraft? aircraft = definition.unitPrefab != null
            ? definition.unitPrefab.GetComponent<Aircraft>()
            : null;
        int hardpointCount = aircraft?.weaponManager?.hardpointSets?.Length ?? loadout.weapons.Count;
        while (loadout.weapons.Count < hardpointCount)
        {
            loadout.weapons.Add(null!);
        }
        if (loadout.weapons.Count > hardpointCount)
        {
            loadout.weapons.RemoveRange(hardpointCount, loadout.weapons.Count - hardpointCount);
        }
    }

    private static bool HasPlanePilot(AircraftDefinition definition)
    {
        Aircraft? aircraft = definition.unitPrefab.GetComponent<Aircraft>();
        if (aircraft?.pilots == null)
        {
            return false;
        }

        for (int i = 0; i < aircraft.pilots.Length; i++)
        {
            if (aircraft.pilots[i] != null && aircraft.pilots[i].pilotType == Pilot.PilotType.Plane)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCompatibleAirbase(Airbase? airbase, FactionHQ hq, AircraftDefinition definition)
    {
        if (airbase == null || airbase.disabled || !airbase.GetAvailableAircraft().Contains(definition))
        {
            return false;
        }

        foreach (Airbase ownedAirbase in hq.GetAirbases())
        {
            if (ReferenceEquals(ownedAirbase, airbase))
            {
                return true;
            }
        }

        return false;
    }

    private static float GetMissionRadius(AirCommandMode mode)
    {
        return mode switch
        {
            AirCommandMode.Cas => CommanderSettings.CasRadiusKm * 1000f,
            AirCommandMode.AirGuard => CommanderSettings.AirGuardRadiusKm * 1000f,
            AirCommandMode.Arad => CommanderSettings.AradRadiusKm * 1000f,
            AirCommandMode.StrategicStrike => CommanderSettings.StrikeRadiusKm * 1000f,
            _ => CommanderSettings.AwacsRadiusKm * 1000f,
        };
    }

    private static void SetMissionRadius(AirCommandMode mode, float radiusKm)
    {
        switch (mode)
        {
            case AirCommandMode.Cas: CommanderSettings.CasRadiusKm = radiusKm; break;
            case AirCommandMode.AirGuard: CommanderSettings.AirGuardRadiusKm = radiusKm; break;
            case AirCommandMode.Arad: CommanderSettings.AradRadiusKm = radiusKm; break;
            case AirCommandMode.StrategicStrike: CommanderSettings.StrikeRadiusKm = radiusKm; break;
            default: CommanderSettings.AwacsRadiusKm = radiusKm; break;
        }
    }

    private void ProcessReturningMissions()
    {
        foreach (KeyValuePair<Aircraft, AirMission> entry in missions)
        {
            if (entry.Value.Returning && !entry.Value.RtbIssued) IssueReturnToBase(entry.Key, entry.Value);
        }
    }

    private static void IssueReturnToBase(Aircraft aircraft, AirMission mission)
    {
        if (aircraft == null || aircraft.disabled || aircraft.pilots == null) return;
        for (int i = 0; i < aircraft.pilots.Length; i++)
        {
            Pilot pilot = aircraft.pilots[i];
            if (pilot == null) continue;
            if (pilot.AILandingState == null) pilot.AILandingState = new AIPilotLandingState();
            pilot.SwitchState(pilot.AILandingState);
            mission.RtbIssued = true;
            return;
        }
    }

    private static bool KeepsStationInMissionArea(AirCommandMode mode)
    {
        return mode == AirCommandMode.AwacsJammer || mode == AirCommandMode.AirGuard;
    }

    internal static string GetModeLabel(AirCommandMode mode)
    {
        return mode switch
        {
            AirCommandMode.AwacsJammer => "AWACS / JAMMER",
            AirCommandMode.Cas => "CAS",
            AirCommandMode.AirGuard => "AIR SUPERIORITY",
            AirCommandMode.Arad => "ARAD",
            AirCommandMode.StrategicStrike => "STRATEGIC STRIKE",
            _ => mode.ToString().ToUpperInvariant(),
        };
    }

    private static string GetAircraftLabel(AircraftDefinition definition)
    {
        return !string.IsNullOrWhiteSpace(definition.unitName)
            ? definition.unitName
            : !string.IsNullOrWhiteSpace(definition.code)
                ? definition.code
                : definition.name;
    }

    private static string GetAirbaseName(Airbase airbase)
    {
        if (airbase.SavedAirbase != null)
        {
            if (!string.IsNullOrWhiteSpace(airbase.SavedAirbase.DisplayName))
            {
                return airbase.SavedAirbase.DisplayName;
            }
            if (!string.IsNullOrWhiteSpace(airbase.SavedAirbase.UniqueName))
            {
                return airbase.SavedAirbase.UniqueName;
            }
        }

        return airbase.name;
    }

    private void SetStatus(string text)
    {
        statusText = text;
        statusUntil = Time.unscaledTime + StatusDurationSeconds;
    }

    internal enum AirCommandMode
    {
        AwacsJammer,
        Cas,
        AirGuard,
        Arad,
        StrategicStrike,
    }

    internal enum LoadoutBalance
    {
        Primary,
        Mixed,
    }

    internal sealed class AirMissionOption
    {
        internal AirMissionOption(
            AircraftDefinition definition,
            AirCommandMode mode,
            HardpointSet[] hardpointSets,
            List<AirHardpointGroup> hardpointGroups)
        {
            Definition = definition;
            Mode = mode;
            HardpointSets = hardpointSets;
            HardpointGroups = hardpointGroups;
        }

        internal AircraftDefinition Definition { get; }
        internal HardpointSet[] HardpointSets { get; }
        internal List<AirHardpointGroup> HardpointGroups { get; }
        internal string LoadoutName => "Custom hardpoints";
        internal float Score => ScoreLoadout(BuildLoadout(), Mode, Definition);
        internal AirCommandMode Mode { get; }

        internal Loadout BuildLoadout()
        {
            Loadout loadout = new();
            for (int i = 0; i < HardpointSets.Length; i++)
            {
                loadout.weapons.Add(null!);
            }
            for (int i = 0; i < HardpointGroups.Count; i++)
            {
                AirHardpointGroup group = HardpointGroups[i];
                WeaponMount? mount = group.SelectedMount;
                for (int index = 0; index < group.HardpointIndices.Count; index++)
                {
                    loadout.weapons[group.HardpointIndices[index]] = mount!;
                }
            }
            return loadout;
        }
    }

    internal sealed class AirHardpointGroup
    {
        private int selectedIndex = -1;

        internal AirHardpointGroup(string label, List<int> hardpointIndices, List<WeaponMount> mounts, int physicalMountCount)
        {
            Label = label;
            HardpointIndices = hardpointIndices;
            Mounts = mounts;
            PhysicalMountCount = physicalMountCount;
        }

        internal string Label { get; }
        internal List<int> HardpointIndices { get; }
        internal List<WeaponMount> Mounts { get; }
        internal int PhysicalMountCount { get; }
        internal WeaponMount? SelectedMount => selectedIndex >= 0 && selectedIndex < Mounts.Count ? Mounts[selectedIndex] : null;

        internal void Select(int index) => selectedIndex = index >= 0 && index < Mounts.Count ? index : -1;
        internal void Clear() => selectedIndex = -1;
    }

    internal sealed class AirbaseOption
    {
        internal AirbaseOption(Airbase airbase, string label, float distance, bool ready)
        {
            Airbase = airbase;
            Label = label;
            Distance = distance;
            Ready = ready;
        }

        internal Airbase Airbase { get; }
        internal string Label { get; }
        internal float Distance { get; }
        internal bool Ready { get; }
    }

    private sealed class PendingAreaSelection
    {
        internal PendingAreaSelection(AirMissionOption option, Airbase airbase)
        {
            Option = option;
            Airbase = airbase;
        }

        internal AirMissionOption Option { get; }
        internal Airbase Airbase { get; }
    }

    private sealed class PendingAircraftSpawn
    {
        internal PendingAircraftSpawn(FactionHQ hq, AirMissionOption option, GlobalPosition areaCenter, float radius, float targetAltitude, bool targetOrdnance, bool saturationAttack, float expiresAt)
        {
            Hq = hq;
            Option = option;
            AreaCenter = areaCenter;
            Radius = radius;
            TargetAltitude = targetAltitude;
            TargetOrdnance = targetOrdnance;
            SaturationAttack = saturationAttack;
            ExpiresAt = expiresAt;
        }

        internal FactionHQ Hq { get; }
        internal AirMissionOption Option { get; }
        internal GlobalPosition AreaCenter { get; }
        internal float Radius { get; }
        internal float TargetAltitude { get; }
        internal bool TargetOrdnance { get; }
        internal bool SaturationAttack { get; }
        internal float ExpiresAt { get; }
    }

    private sealed class AirMission
    {
        internal AirMission(AirCommandMode mode, GlobalPosition areaCenter, float radius, float targetAltitude, bool targetOrdnance, bool saturationAttack)
        {
            Mode = mode;
            AreaCenter = areaCenter;
            Radius = radius;
            TargetAltitude = targetAltitude;
            TargetOrdnance = targetOrdnance;
            SaturationAttack = saturationAttack;
        }

        internal AirCommandMode Mode { get; }
        internal GlobalPosition AreaCenter { get; }
        internal float Radius { get; }
        internal float TargetAltitude { get; }
        internal bool TargetOrdnance { get; }
        internal bool SaturationAttack { get; }
        internal GameObject? MapVisual { get; set; }
        internal bool Returning { get; set; }
        internal bool RtbIssued { get; set; }
    }
}

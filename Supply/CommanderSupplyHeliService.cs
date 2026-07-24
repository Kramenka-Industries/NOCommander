using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace NuclearOptionCommander;

internal sealed partial class CommanderSupplyHeliService
{
    private const float StatusDurationSeconds = 5f;
    private const float PendingSpawnTimeoutSeconds = 90f;

    private static readonly FieldInfo? AircraftField = AccessTools.Field(typeof(PilotBaseState), "aircraft");
    private static readonly FieldInfo? PilotField = AccessTools.Field(typeof(PilotBaseState), "pilot");
    private static readonly FieldInfo? LastLandingSpotCheckField = AccessTools.Field(typeof(AIHeloTransportState), "lastLandingSpotCheck");
    private static readonly FieldInfo? TouchedDownTimeField = AccessTools.Field(typeof(AIHeloTransportState), "touchedDownTime");
    private static readonly FieldInfo? TimeWithoutMissionField = AccessTools.Field(typeof(AIHeloTransportState), "timeWithoutMission");
    private static readonly FieldInfo? AirdropField = AccessTools.Field(typeof(AIHeloTransportState), "airdrop");
    private static readonly FieldInfo? TransportModeField = AccessTools.Field(typeof(AIHeloTransportState), "transportMode");
    private static readonly FieldInfo? TransportDestinationField = AccessTools.Field(typeof(AIHeloTransportState), "transportDestination");
    private static readonly Type? TransportDestinationType = typeof(AIHeloTransportState).GetNestedType("TransportDestination", BindingFlags.NonPublic);
    private static readonly FieldInfo? DestinationValidMissionField = AccessTools.Field(TransportDestinationType, "validMission");
    private static readonly FieldInfo? DestinationDropConditionsField = AccessTools.Field(TransportDestinationType, "dropConditionsMet");
    private static readonly FieldInfo? DestinationTouchdownField = AccessTools.Field(TransportDestinationType, "touchdownPoint");
    private static readonly FieldInfo? DestinationEnemyPositionField = AccessTools.Field(TransportDestinationType, "enemyPosition");
    private static readonly FieldInfo? DestinationLzField = AccessTools.Field(TransportDestinationType, "LZ");
    private static readonly FieldInfo? DestinationSlopeField = AccessTools.Field(TransportDestinationType, "slope");
    private static readonly FieldInfo? DestinationAttemptsField = AccessTools.Field(TransportDestinationType, "touchdownPointAttempts");
    private static readonly MethodInfo? UpdateTouchdownPointMethod = AccessTools.Method(TransportDestinationType, "UpdateTouchdownPoint");
    private static readonly MethodInfo? UpdateLzForUnitMethod = AccessTools.Method(
        TransportDestinationType,
        "UpdateLZ",
        new[] { typeof(Aircraft), typeof(Unit) });
    private static readonly FieldInfo? GroundVehicleParachuteField = AccessTools.Field(typeof(GroundVehicle), "parachuteSystem");
    private static readonly FieldInfo? ContainerParachuteField = AccessTools.Field(typeof(Container), "parachuteSystem");

    private readonly List<CargoAircraftOption> aircraftOptions = new();
    private readonly List<AirbaseOption> airbaseOptions = new();
    private readonly Queue<QueuedCargoSpawn> queuedCargoSpawns = new();
    private readonly Dictionary<Aircraft, CargoMission> assignedMissions = new();
    private readonly Dictionary<Autopilot, float> terrainClearanceAutopilots = new();
    private PendingTargetSelection? pendingTargetSelection;
    private PendingAircraftSpawn? pendingAircraftSpawn;
    private Airbase? selectedAirbase;
    private int selectedAircraftIndex;
    private bool highTerrainClearance;
    private float terrainClearanceMeters = 250f;
    private bool airdropDelivery;
    private bool includeEcm = true;
    private bool includeCountermeasures = true;
    private bool fillRemainingHardpoints;
    private bool useOtherAirfields = true;
    private bool uiVisible;
    private float nextAirbaseRefreshAt;
    private float nextMissionPruneAt;
    private float statusUntil;
    private string statusText = string.Empty;

    internal CommanderSupplyHeliService()
    {
        Instance = this;
    }

    internal static CommanderSupplyHeliService? Instance { get; private set; }
    internal IReadOnlyList<CargoAircraftOption> AircraftOptions => aircraftOptions;
    internal IReadOnlyList<AirbaseOption> AirbaseOptions => airbaseOptions;
    internal bool AwaitingTargetSelection => pendingTargetSelection != null;
    internal int QueuedSpawnCount => queuedCargoSpawns.Count;

    internal void CopyActiveDeliveryTargets(List<GlobalPosition> targets)
    {
        targets.Clear();
        foreach (KeyValuePair<Aircraft, CargoMission> entry in assignedMissions)
        {
            if (entry.Key != null && !entry.Key.disabled && entry.Value.TargetOverrideActive)
            {
                targets.Add(entry.Value.Target);
            }
        }
    }
    internal string StatusText => AwaitingTargetSelection
        ? pendingTargetSelection!.GetTargetPrompt()
        : Time.unscaledTime <= statusUntil
            ? statusText
            : queuedCargoSpawns.Count > 0
                ? $"Waiting for an available supply hangar ({queuedCargoSpawns.Count} queued)."
                : string.Empty;
    internal int SelectedAircraftIndex => selectedAircraftIndex;
    internal bool HighTerrainClearance
    {
        get => highTerrainClearance;
        set => highTerrainClearance = value;
    }

    internal float TerrainClearanceMeters
    {
        get => terrainClearanceMeters;
        set => terrainClearanceMeters = Mathf.Max(value, 0f);
    }

    internal bool AirdropDelivery
    {
        get => airdropDelivery;
        set => airdropDelivery = value && SelectedCargoSupportsAirdrop;
    }

    internal bool IncludeEcm
    {
        get => includeEcm;
        set => includeEcm = value;
    }

    internal bool IncludeCountermeasures
    {
        get => includeCountermeasures;
        set => includeCountermeasures = value;
    }

    internal bool FillRemainingHardpoints
    {
        get => fillRemainingHardpoints;
        set => fillRemainingHardpoints = value;
    }

    internal bool UseOtherAirfields
    {
        get => useOtherAirfields;
        set => useOtherAirfields = value;
    }

    internal bool SelectedCargoSupportsAirdrop => SelectedAircraft != null
        && SelectedAircraft.CargoSlots.Exists(slot => slot.SelectedMount != null)
        && SelectedAircraft.CargoSlots.TrueForAll(slot => slot.SelectedMount == null || CargoMountSupportsAirdrop(slot.SelectedMount));
    internal bool HasSelectedCargo => SelectedAircraft != null
        && SelectedAircraft.CargoSlots.Exists(slot => slot.SelectedMount != null);

    internal Airbase? SelectedAirbase => selectedAirbase;

    internal CargoAircraftOption? SelectedAircraft => aircraftOptions.Count == 0
        ? null
        : aircraftOptions[Mathf.Clamp(selectedAircraftIndex, 0, aircraftOptions.Count - 1)];

    internal void Activate()
    {
        if (aircraftOptions.Count == 0)
        {
            RefreshOptions();
        }
    }

    internal void Deactivate()
    {
        uiVisible = false;
        CancelTargetSelection(showStatus: false);
    }

    internal void SetUiVisible(bool visible)
    {
        uiVisible = visible;
    }

    internal void CancelDeploymentSelection()
    {
        CancelTargetSelection(showStatus: false);
        SetStatus("Deployment selection cleared.");
    }

    internal void TickActive()
    {
        if (AwaitingTargetSelection && CommanderGameInput.CancelDown)
        {
            CancelTargetSelection(showStatus: true);
        }
    }

    internal void TickPersistent()
    {
        if (pendingAircraftSpawn != null && Time.unscaledTime > pendingAircraftSpawn.ExpiresAt)
        {
            CommanderPlugin.Log.LogWarning($"Supply cargo run assignment timed out: aircraft={pendingAircraftSpawn.Definition.name}");
            pendingAircraftSpawn = null;
        }

        if (CommanderScheduler.IsDue(ref nextAirbaseRefreshAt, 1f))
        {
            if (uiVisible)
            {
                RefreshAirbaseOptions();
            }

            if (queuedCargoSpawns.Count > 0)
            {
                TryProcessQueuedCargoSpawns();
            }
        }

        if (CommanderScheduler.IsDue(ref nextMissionPruneAt, 2f))
        {
            PruneFinishedMissions();
        }
    }

    internal void ResetSession()
    {
        aircraftOptions.Clear();
        airbaseOptions.Clear();
        assignedMissions.Clear();
        terrainClearanceAutopilots.Clear();
        pendingTargetSelection = null;
        pendingAircraftSpawn = null;
        uiVisible = false;
        queuedCargoSpawns.Clear();
        selectedAirbase = null;
        selectedAircraftIndex = 0;
        highTerrainClearance = false;
        airdropDelivery = false;
        includeEcm = true;
        includeCountermeasures = true;
        fillRemainingHardpoints = false;
        useOtherAirfields = true;
        nextAirbaseRefreshAt = CommanderScheduler.Stagger("supply.airbases", 1f, 0.5f);
        nextMissionPruneAt = CommanderScheduler.Stagger("supply.prune", 2f, 0.8f);
        statusText = string.Empty;
    }

    internal void RefreshOptions()
    {
        aircraftOptions.Clear();
        AircraftDefinition[] definitions = Resources.FindObjectsOfTypeAll<AircraftDefinition>();
        HashSet<AircraftDefinition> seenDefinitions = new();
        for (int i = 0; i < definitions.Length; i++)
        {
            AircraftDefinition definition = definitions[i];
            if (!seenDefinitions.Add(definition))
            {
                continue;
            }

            CargoAircraftOption? option = CreateAircraftOption(definition);
            if (option != null)
            {
                aircraftOptions.Add(option);
            }
        }

        aircraftOptions.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        selectedAircraftIndex = Mathf.Clamp(selectedAircraftIndex, 0, Mathf.Max(aircraftOptions.Count - 1, 0));
        RefreshAirbaseOptions();
    }

    internal void SelectAircraft(int index)
    {
        if (index < 0 || index >= aircraftOptions.Count)
        {
            return;
        }

        selectedAircraftIndex = index;
        RefreshAirbaseOptions();
    }

    internal void SelectAirbase(int index)
    {
        if (index < 0 || index >= airbaseOptions.Count)
        {
            return;
        }

        selectedAirbase = airbaseOptions[index].Airbase;
    }

    internal void CycleCargoSlot(int slotIndex)
    {
        CargoAircraftOption? aircraft = SelectedAircraft;
        if (aircraft == null || slotIndex < 0 || slotIndex >= aircraft.CargoSlots.Count)
        {
            return;
        }

        CargoSlotOption selectedSlot = aircraft.CargoSlots[slotIndex];
        selectedSlot.CycleSelection();
        if (selectedSlot.SelectedMount == null)
        {
            return;
        }

        for (int i = 0; i < aircraft.CargoSlots.Count; i++)
        {
            CargoSlotOption other = aircraft.CargoSlots[i];
            if (!ReferenceEquals(other, selectedSlot)
                && other.SelectedMount != null
                && SetsConflict(aircraft.HardpointSets, selectedSlot.HardpointIndex, other.HardpointIndex))
            {
                other.Clear();
            }
        }

        airdropDelivery &= SelectedCargoSupportsAirdrop;
    }

    internal void SelectCargoMount(int slotIndex, int mountIndex)
    {
        CargoAircraftOption? aircraft = SelectedAircraft;
        if (aircraft == null || slotIndex < 0 || slotIndex >= aircraft.CargoSlots.Count)
        {
            return;
        }

        CargoSlotOption selectedSlot = aircraft.CargoSlots[slotIndex];
        selectedSlot.Select(mountIndex);
        if (selectedSlot.SelectedMount != null)
        {
            for (int i = 0; i < aircraft.CargoSlots.Count; i++)
            {
                CargoSlotOption other = aircraft.CargoSlots[i];
                if (!ReferenceEquals(other, selectedSlot)
                    && other.SelectedMount != null
                    && SetsConflict(aircraft.HardpointSets, selectedSlot.HardpointIndex, other.HardpointIndex))
                {
                    other.Clear();
                }
            }
        }

        airdropDelivery &= SelectedCargoSupportsAirdrop;
    }

    internal void ClearSelectedCargo()
    {
        CargoAircraftOption? aircraft = SelectedAircraft;
        if (aircraft == null)
        {
            return;
        }

        for (int i = 0; i < aircraft.CargoSlots.Count; i++)
        {
            aircraft.CargoSlots[i].Clear();
        }

        airdropDelivery = false;
    }

    internal void RandomizeSelectedCargo()
    {
        CargoAircraftOption? aircraft = SelectedAircraft;
        if (aircraft == null)
        {
            return;
        }

        ClearSelectedCargo();
        List<int> slotOrder = new();
        for (int i = 0; i < aircraft.CargoSlots.Count; i++)
        {
            slotOrder.Add(i);
        }

        Shuffle(slotOrder);
        for (int i = 0; i < slotOrder.Count; i++)
        {
            CargoSlotOption slot = aircraft.CargoSlots[slotOrder[i]];
            bool blocked = false;
            for (int otherIndex = 0; otherIndex < aircraft.CargoSlots.Count; otherIndex++)
            {
                CargoSlotOption other = aircraft.CargoSlots[otherIndex];
                if (other.SelectedMount != null
                    && SetsConflict(aircraft.HardpointSets, slot.HardpointIndex, other.HardpointIndex))
                {
                    blocked = true;
                    break;
                }
            }

            if (!blocked && slot.Mounts.Count > 0)
            {
                slot.Select(UnityEngine.Random.Range(0, slot.Mounts.Count));
            }
        }

        airdropDelivery &= SelectedCargoSupportsAirdrop;
    }

    internal string GetCargoSlotButtonLabel(CargoSlotOption slot)
    {
        string cargo = slot.SelectedMount != null
            ? GetCargoLabel(slot.SelectedMount, string.Empty)
            : "None";
        return $"{slot.Label}\n{cargo}";
    }

    internal string GetCargoMountLabel(WeaponMount mount)
    {
        string suffix = CargoMountSupportsAirdrop(mount) ? " [Airdrop]" : string.Empty;
        return GetCargoLabel(mount, string.Empty) + suffix;
    }

    internal string GetAircraftButtonLabel(CargoAircraftOption option)
    {
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        int supply = hq?.GetUnitSupply(option.Definition) ?? 0;
        string cost = UnitConverter.ValueReading(option.Definition.value) ?? option.Definition.value.ToString("F1");
        return $"{option.Label} | Supply {supply} | {cost}";
    }

    internal string GetAirbaseButtonLabel(AirbaseOption option)
    {
        string distance = UnitConverter.DistanceReading(option.Distance) ?? $"{option.Distance:F0} m";
        return $"{(option.Ready ? "READY" : "WAIT")} | {option.Label} | {distance}";
    }

    internal void BeginSelectedCargoRun()
    {
        CargoAircraftOption? aircraftOption = SelectedAircraft;
        if (aircraftOption == null)
        {
            SetStatus("No cargo-capable aircraft is selected.");
            return;
        }

        if (!CanHostSpawn(out FactionHQ? hq, out string error))
        {
            SetStatus(error);
            return;
        }

        Airbase? airbase = selectedAirbase;
        if (!IsCompatibleAirbase(airbase, hq!, aircraftOption.Definition))
        {
            SetStatus("Select a friendly airbase that supports this aircraft.");
            return;
        }

        Loadout cargoLoadout = BuildSelectedCargoLoadout(
            aircraftOption,
            airbase!,
            hq!,
            includeEcm,
            includeCountermeasures,
            fillRemainingHardpoints,
            out int cargoCount,
            out string cargoLabel,
            out string supportSummary);
        if (cargoCount <= 0)
        {
            SetStatus("Select cargo for at least one cargo bay.");
            return;
        }

        if (airdropDelivery && !SelectedCargoSupportsAirdrop)
        {
            airdropDelivery = false;
            SetStatus("Airdrop is unavailable because selected cargo has no parachute system.");
            return;
        }

        if (hq!.GetUnitSupply(aircraftOption.Definition) <= 0 && hq.factionFunds < aircraftOption.Definition.value)
        {
            SetStatus("The faction cannot afford this supply aircraft.");
            return;
        }

        pendingTargetSelection = new PendingTargetSelection(
            aircraftOption,
            cargoLoadout,
            cargoLabel,
            airbase!,
            highTerrainClearance,
            terrainClearanceMeters,
            airdropDelivery,
            supportSummary,
            useOtherAirfields);
        SetStatus(pendingTargetSelection.GetTargetPrompt());
    }

    internal bool TrySpawnAtWorldPoint(Vector2 screenPosition)
    {
        if (pendingTargetSelection == null)
        {
            return false;
        }

        if (!CommanderGameAccess.TryRaycastWorldPosition(screenPosition, out GlobalPosition target))
        {
            SetStatus("No valid terrain point was found. Click visible terrain or a surface.");
            return true;
        }

        PendingTargetSelection selection = pendingTargetSelection;
        selection.Targets.Add(target);

        bool repeatSelection = CommanderSettings.RepeatDeployment.IsPressed();
        if (!repeatSelection)
        {
            pendingTargetSelection = null;
        }
        SpawnCargoRun(
            selection.Aircraft,
            selection.Loadout,
            selection.CargoLabel,
            selection.Airbase,
            selection.HighTerrainClearance,
            selection.TerrainClearanceMeters,
            selection.Airdrop,
            selection.SupportSummary,
            selection.UseOtherAirfields,
            selection.Targets);
        if (repeatSelection)
        {
            selection.Targets.Clear();
            SetStatus("Supply run queued. Hold Shift and click another destination, or release Shift for the final run.");
        }
        return true;
    }

    internal static void NotifyFactionUnitRegistered(FactionHQ hq, Unit unit)
    {
        Instance?.TryAssignPendingAircraft(hq, unit);
    }

    internal static bool TryOverrideTransportTarget(AIHeloTransportState state)
    {
        try
        {
            return Instance != null && Instance.OverrideTransportTarget(state);
        }
        catch (Exception exception)
        {
            CommanderPlugin.Log.LogError($"Supply cargo target hook failed; returning this aircraft to Basegame cargo logic: {exception}");
            Instance?.EndTargetOverrideForState(state);
            return false;
        }
    }

    internal static void NotifyTransportStateLeft(AIHeloTransportState state)
    {
        Instance?.EndTargetOverrideForState(state);
    }

    internal static bool ShouldDelayCargoTakeoff(Pilot pilot, PilotBaseState requestedState)
    {
        return Instance != null && Instance.ShouldDelayAssignedCargoTakeoff(pilot, requestedState);
    }

    internal static bool TryDeployAssignedCargo(AIHeloTransportState state)
    {
        return Instance != null && Instance.DeployNextAssignedCargo(state);
    }

    internal static void NotifyAircraftReturned(Aircraft aircraft)
    {
        Instance?.HandleAircraftReturned(aircraft);
    }

    internal static void NotifyCargoActivated(Aircraft aircraft, Unit cargoUnit)
    {
        Instance?.HoldDeployedCargo(aircraft, cargoUnit);
    }

    internal static void RaiseAssignedTerrainClearance(Autopilot autopilot, ref float altitudeHold, bool followTerrain)
    {
        if (followTerrain
            && Instance != null
            && Instance.terrainClearanceAutopilots.TryGetValue(autopilot, out float clearance))
        {
            altitudeHold = Mathf.Max(altitudeHold, clearance);
        }
    }

    private static bool CanHostSpawn(out FactionHQ? hq, out string error)
    {
        hq = null;
        if (NetworkManagerNuclearOption.i == null || !NetworkManagerNuclearOption.i.Server.Active)
        {
            error = "Supply aircraft can only be spawned by the host.";
            return false;
        }

        hq = CommanderGameAccess.GetLocalHq();
        if (hq == null)
        {
            error = "No local faction HQ is available.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private void RefreshAirbaseOptions()
    {
        Airbase? previousSelection = selectedAirbase;
        airbaseOptions.Clear();

        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        CargoAircraftOption? aircraft = SelectedAircraft;
        if (hq == null || aircraft == null)
        {
            selectedAirbase = null;
            return;
        }

        Camera? camera = SceneSingleton<CameraStateManager>.i?.mainCamera;
        Vector3 cameraPosition = camera != null ? camera.transform.position : Vector3.zero;
        foreach (Airbase airbase in hq.GetAirbases())
        {
            if (!IsCompatibleAirbase(airbase, hq, aircraft.Definition))
            {
                continue;
            }

            Transform positionTransform = airbase.center != null ? airbase.center : airbase.transform;
            float distance = Vector3.Distance(cameraPosition, positionTransform.position);
            bool ready = IsAvailableAirbase(airbase, hq, aircraft.Definition);
            airbaseOptions.Add(new AirbaseOption(airbase, GetAirbaseLabel(airbase), distance, ready));
        }

        airbaseOptions.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        selectedAirbase = null;
        for (int i = 0; i < airbaseOptions.Count; i++)
        {
            if (ReferenceEquals(airbaseOptions[i].Airbase, previousSelection))
            {
                selectedAirbase = previousSelection;
                break;
            }
        }

        if (selectedAirbase == null && airbaseOptions.Count > 0)
        {
            selectedAirbase = airbaseOptions[0].Airbase;
        }
    }

    private static bool IsAvailableAirbase(Airbase? airbase, FactionHQ hq, AircraftDefinition definition)
    {
        return airbase != null
            && !airbase.disabled
            && airbase.CurrentHQ == hq
            && airbase.CanSpawnAircraft(definition);
    }

    private static bool IsCompatibleAirbase(Airbase? airbase, FactionHQ hq, AircraftDefinition definition)
    {
        if (airbase == null || airbase.disabled || airbase.CurrentHQ != hq)
        {
            return false;
        }

        List<AircraftDefinition> availableAircraft = airbase.GetAvailableAircraft();
        return availableAircraft != null && availableAircraft.Contains(definition);
    }

    private static string GetAirbaseLabel(Airbase airbase)
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

    private static Loadout CloneLoadout(Loadout source)
    {
        return new Loadout
        {
            weapons = source.weapons != null ? new List<WeaponMount>(source.weapons) : new List<WeaponMount>()
        };
    }

    private static void EnsureLoadoutLength(Loadout loadout, int count)
    {
        while (loadout.weapons.Count < count)
        {
            loadout.weapons.Add(null!);
        }

        if (loadout.weapons.Count > count)
        {
            loadout.weapons.RemoveRange(count, loadout.weapons.Count - count);
        }
    }

    private int CountCargoSlots()
    {
        int count = 0;
        for (int i = 0; i < aircraftOptions.Count; i++)
        {
            count += aircraftOptions[i].CargoSlots.Count;
        }

        return count;
    }

    private void CancelTargetSelection(bool showStatus)
    {
        if (pendingTargetSelection == null)
        {
            return;
        }

        pendingTargetSelection = null;
        if (showStatus)
        {
            SetStatus("Cargo run target selection cancelled.");
        }
    }

    private void SetStatus(string text)
    {
        statusText = text;
        statusUntil = Time.unscaledTime + StatusDurationSeconds;
    }


}

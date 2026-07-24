using System;
using System.Collections;
using System.Collections.Generic;
using NuclearOption.SavedMission;
using UnityEngine;

namespace NuclearOptionCommander;

internal sealed partial class CommanderSupplyHeliService
{
    private void SpawnCargoRun(
        CargoAircraftOption aircraftOption,
        Loadout cargoLoadout,
        string cargoLabel,
        Airbase airbase,
        bool useHighTerrainClearance,
        float terrainClearanceMeters,
        bool useAirdrop,
        string supportSummary,
        bool useOtherAirfields,
        IReadOnlyList<GlobalPosition> targets)
    {
        if (!CanHostSpawn(out FactionHQ? hq, out string error))
        {
            SetStatus(error);
            return;
        }

        if (!IsCompatibleAirbase(airbase, hq!, aircraftOption.Definition))
        {
            SetStatus("The selected airbase no longer supports this aircraft.");
            return;
        }

        QueuedCargoSpawn request = new(
            aircraftOption,
            CloneLoadout(cargoLoadout),
            cargoLabel,
            airbase,
            useHighTerrainClearance,
            terrainClearanceMeters,
            useAirdrop,
            supportSummary,
            useOtherAirfields,
            targets);
        Airbase? spawnAirbase = ResolveSpawnAirbase(request, hq!);
        if (spawnAirbase == null || pendingAircraftSpawn != null)
        {
            queuedCargoSpawns.Enqueue(request);
            SetStatus(useOtherAirfields
                ? $"Supply run queued. Waiting for a compatible friendly airfield ({queuedCargoSpawns.Count} queued)."
                : $"Supply run queued for {GetAirbaseLabel(airbase)} ({queuedCargoSpawns.Count} queued).");
            return;
        }

        TrySpawnCargoRunAtAirbase(request, spawnAirbase, hq!);
    }

    private void SpawnCargoRun(
        CargoAircraftOption aircraftOption,
        Loadout cargoLoadout,
        string cargoLabel,
        Airbase airbase,
        bool useHighTerrainClearance,
        float terrainClearanceMeters,
        bool useAirdrop,
        string supportSummary,
        bool useOtherAirfields,
        GlobalPosition target)
    {
        SpawnCargoRun(
            aircraftOption,
            cargoLoadout,
            cargoLabel,
            airbase,
            useHighTerrainClearance,
            terrainClearanceMeters,
            useAirdrop,
            supportSummary,
            useOtherAirfields,
            new[] { target });
    }

    private void TryProcessQueuedCargoSpawns()
    {
        if (pendingAircraftSpawn != null || queuedCargoSpawns.Count == 0)
        {
            return;
        }

        if (!CanHostSpawn(out FactionHQ? hq, out _))
        {
            return;
        }

        QueuedCargoSpawn request = queuedCargoSpawns.Peek();
        if (!IsCompatibleAirbase(request.RequestedAirbase, hq!, request.Aircraft.Definition)
            && !request.UseOtherAirfields)
        {
            queuedCargoSpawns.Dequeue();
            SetStatus("A queued supply run was cancelled because its airbase is no longer friendly or compatible.");
            return;
        }

        Airbase? spawnAirbase = ResolveSpawnAirbase(request, hq!);
        if (spawnAirbase == null)
        {
            return;
        }

        queuedCargoSpawns.Dequeue();
        TrySpawnCargoRunAtAirbase(request, spawnAirbase, hq!);
    }

    private static Airbase? ResolveSpawnAirbase(QueuedCargoSpawn request, FactionHQ hq)
    {
        if (IsAvailableAirbase(request.RequestedAirbase, hq, request.Aircraft.Definition))
        {
            return request.RequestedAirbase;
        }

        if (!request.UseOtherAirfields)
        {
            return null;
        }

        Camera? camera = SceneSingleton<CameraStateManager>.i?.mainCamera;
        Vector3 cameraPosition = camera != null ? camera.transform.position : Vector3.zero;
        Airbase? nearest = null;
        float nearestDistance = float.MaxValue;
        foreach (Airbase candidate in hq.GetAirbases())
        {
            if (!IsAvailableAirbase(candidate, hq, request.Aircraft.Definition))
            {
                continue;
            }

            Transform positionTransform = candidate.center != null ? candidate.center : candidate.transform;
            float distance = Vector3.SqrMagnitude(positionTransform.position - cameraPosition);
            if (distance < nearestDistance)
            {
                nearest = candidate;
                nearestDistance = distance;
            }
        }

        return nearest;
    }

    private void TrySpawnCargoRunAtAirbase(QueuedCargoSpawn request, Airbase airbase, FactionHQ hq)
    {
        CargoAircraftOption aircraftOption = request.Aircraft;

        bool purchased = false;
        if (hq.GetUnitSupply(aircraftOption.Definition) <= 0)
        {
            float cost = aircraftOption.Definition.value;
            if (hq.factionFunds < cost)
            {
                SetStatus("The faction cannot afford this supply aircraft.");
                return;
            }

            hq.AddFunds(-cost);
            hq.ModifyUnitSupply(aircraftOption.Definition, 1);
            purchased = true;
        }

        pendingAircraftSpawn = new PendingAircraftSpawn(
            hq,
            aircraftOption.Definition,
            request.CargoLabel,
            request.Target,
            request.HighTerrainClearance,
            request.TerrainClearanceMeters,
            request.Airdrop,
            request.SupportSummary,
            purchased,
            purchased ? aircraftOption.Definition.value : 0f,
            request.Targets,
            Time.unscaledTime + PendingSpawnTimeoutSeconds);

        AircraftDefinition definition = aircraftOption.Definition;
        int liveryIndex = definition.aircraftParameters.GetRandomLiveryForFaction(hq.faction);
        Loadout loadout = CloneLoadout(request.Loadout);
        Airbase.TrySpawnResult result = airbase.TrySpawnAircraft(
            null,
            definition,
            new LiveryKey(liveryIndex),
            loadout,
            definition.aircraftParameters.DefaultFuelLevel);

        if (!result.Allowed)
        {
            pendingAircraftSpawn = null;
            if (purchased)
            {
                hq.ModifyUnitSupply(definition, -1);
                hq.AddFunds(definition.value);
            }

            SetStatus("The airbase rejected the supply aircraft spawn.");
            return;
        }

        string purchaseLabel = purchased ? " Purchased from faction funds." : string.Empty;
        SetStatus($"Spawned {aircraftOption.Label} with {request.CargoLabel}.{purchaseLabel}");
    }

    private void TryAssignPendingAircraft(FactionHQ hq, Unit unit)
    {
        PendingAircraftSpawn? pending = pendingAircraftSpawn;
        if (pending == null
            || !ReferenceEquals(hq, pending.Hq)
            || unit is not Aircraft aircraft
            || aircraft.Player != null
            || !ReferenceEquals(aircraft.definition, pending.Definition))
        {
            return;
        }

        assignedMissions[aircraft] = new CargoMission(
            pending.Hq,
            pending.Target,
            pending.CargoLabel,
            pending.HighTerrainClearance,
            pending.TerrainClearanceMeters,
            pending.Airdrop,
            pending.PurchasedWithFunds,
            pending.PurchaseCost,
            CountDeployableCargo(aircraft),
            pending.Targets,
            pending.NavalTarget);
        string missionLabel = pending.NavalTarget != null
            ? "Naval Supply"
            : pending.Airdrop
                ? $"Airdrop: {pending.CargoLabel}"
                : $"Cargo Delivery: {pending.CargoLabel}";
        CommanderSelectionService.PinMissionUnit(aircraft, "SUPPLY", missionLabel);
        if (pending.HighTerrainClearance && aircraft.autopilot != null)
        {
            terrainClearanceAutopilots[aircraft.autopilot] = pending.TerrainClearanceMeters;
        }
        pendingAircraftSpawn = null;
    }

    private bool OverrideTransportTarget(AIHeloTransportState state)
    {
        Aircraft? aircraft = AircraftField?.GetValue(state) as Aircraft;
        if (aircraft == null
            || !assignedMissions.TryGetValue(aircraft, out CargoMission mission)
            || !mission.TargetOverrideActive)
        {
            return false;
        }

        float lastCheck = LastLandingSpotCheckField?.GetValue(state) is float value ? value : 0f;
        if (Time.timeSinceLevelLoad - lastCheck < 3f)
        {
            return true;
        }

        if (mission.NavalTarget != null)
        {
            return OverrideNavalSupplyTarget(state, aircraft, mission);
        }

        if (!TrySelectCargoStation(aircraft))
        {
            if (PilotField?.GetValue(state) is Pilot unloadingPilot
                && unloadingPilot.flightInfo.LastCargoDelivery > 0f)
            {
                LastLandingSpotCheckField?.SetValue(state, Time.timeSinceLevelLoad);
                TimeWithoutMissionField?.SetValue(state, 0f);
                state.stateDisplayName = "Unloading cargo";
                return true;
            }

            CommanderPlugin.Log.LogWarning($"Assigned supply aircraft has no active cargo station: {CommanderGameAccess.GetUnitLabel(aircraft)}");
            return false;
        }

        LastLandingSpotCheckField?.SetValue(state, Time.timeSinceLevelLoad);
        TimeWithoutMissionField?.SetValue(state, 0f);
        AirdropField?.SetValue(state, mission.Airdrop);
        TransportModeField?.SetValue(state, AIHeloTransportState.TransportMode.LandSuppy);
        state.stateDisplayName = $"Delivering {mission.CargoLabel}";

        if (PilotField?.GetValue(state) is Pilot pilot)
        {
            pilot.flightInfo.EnemyContact = true;
        }

        object? destination = TransportDestinationField?.GetValue(state);
        if (destination == null)
        {
            return false;
        }

        DestinationValidMissionField?.SetValue(destination, true);
        DestinationDropConditionsField?.SetValue(destination, false);
        DestinationEnemyPositionField?.SetValue(destination, mission.Target);
        DestinationLzField?.SetValue(destination, mission.Target);

        if (!mission.Initialized)
        {
            DestinationTouchdownField?.SetValue(destination, mission.Target);
            DestinationSlopeField?.SetValue(destination, 90f);
            DestinationAttemptsField?.SetValue(destination, 0);
            mission.Initialized = true;
        }

        UpdateTouchdownPointMethod?.Invoke(destination, new object[] { 150f, aircraft });
        TransportDestinationField?.SetValue(state, destination);
        if (!mission.Airdrop
            && DestinationTouchdownField?.GetValue(destination) is GlobalPosition touchdown
            && CommanderGameAccess.HorizontalDistance(aircraft.transform.position, touchdown.ToLocalPosition()) < 1000f
            && !aircraft.gearDeployed)
        {
            aircraft.SetGear(true);
        }
        return true;
    }

    private void EndTargetOverrideForState(AIHeloTransportState state)
    {
        if (AircraftField?.GetValue(state) is Aircraft aircraft
            && assignedMissions.TryGetValue(aircraft, out CargoMission mission)
            && mission.TargetOverrideActive)
        {
            mission.TargetOverrideActive = false;
        }
    }

    private bool ShouldDelayAssignedCargoTakeoff(Pilot pilot, PilotBaseState requestedState)
    {
        if (requestedState == pilot.currentState
            || pilot.currentState != pilot.AIHeloTransportState
            || pilot.aircraft == null
            || !assignedMissions.TryGetValue(pilot.aircraft, out CargoMission mission)
            || !mission.TargetOverrideActive)
        {
            return false;
        }

        // Do not block the initial departure from the airfield. After the first
        // release, keep an airdrop in transport state until every cargo station
        // has fired; otherwise Basegame leaves the state after only one load.
        if (mission.Airdrop)
        {
            return mission.ReleasedCargoCount > 0 && HasDeployableCargo(pilot.aircraft);
        }
        return mission.CargoClearancePending
            || (mission.LastCargoReleasedAt > 0f
                && Time.timeSinceLevelLoad - mission.LastCargoReleasedAt < 8f);
    }

    private void HoldDeployedCargo(Aircraft aircraft, Unit cargoUnit)
    {
        if (!assignedMissions.TryGetValue(aircraft, out CargoMission mission))
        {
            return;
        }

        mission.ActivatedCargoCount++;
        bool cargoStillOnAircraft = HasDeployableCargo(aircraft);
        if (cargoUnit is GroundVehicle groundVehicle)
        {
            if (cargoStillOnAircraft && !mission.Airdrop)
            {
                mission.CargoClearancePending = true;
                CommanderPlugin.Instance?.StartCoroutine(ClearGroundVehicleFromRamp(aircraft, groundVehicle, mission));
            }
            else
            {
                groundVehicle.SetHoldPosition(true);
                groundVehicle.UnitCommand?.SetDestination(groundVehicle.GlobalPosition(), playerCommand: false);
            }
        }
        else if (cargoStillOnAircraft
            && !mission.Airdrop
            && !mission.CargoClearancePending)
        {
            mission.CargoClearancePending = true;
            CommanderPlugin.Instance?.StartCoroutine(ClearStaticCargoFromRamp(aircraft, cargoUnit, mission));
        }

    }

    private bool DeployNextAssignedCargo(AIHeloTransportState state)
    {
        Aircraft? aircraft = AircraftField?.GetValue(state) as Aircraft;
        if (aircraft == null || !assignedMissions.TryGetValue(aircraft, out CargoMission mission))
        {
            return false;
        }

        HoldLandingTimerWhileCargoProcesses(state, aircraft, mission);

        if (Time.timeSinceLevelLoad < mission.NextCargoReleaseAt)
        {
            return true;
        }

        if (!mission.Airdrop
            && (mission.ReleasedCargoCount > mission.ActivatedCargoCount || mission.CargoClearancePending))
        {
            return true;
        }

        if (!TrySelectNextCargoWeapon(aircraft, out WeaponStation station, out Weapon cargoWeapon))
        {
            return true;
        }

        Pilot? pilot = PilotField?.GetValue(state) as Pilot;
        if (pilot == null)
        {
            return false;
        }

        aircraft.weaponManager.currentWeaponStation = station;
        cargoWeapon.Fire(aircraft, null!, aircraft.rb.velocity, station, default);
        station.UpdateLastFired(1);
        mission.ReleasedCargoCount++;
        mission.LastCargoReleasedAt = Time.timeSinceLevelLoad;
        pilot.flightInfo.LastCargoDelivery = Time.timeSinceLevelLoad;
        pilot.flightInfo.EnemyContact = true;
        mission.NextCargoReleaseAt = Time.timeSinceLevelLoad + (mission.Airdrop ? 1.5f : 2.5f);
        return true;
    }

    private static void HoldLandingTimerWhileCargoProcesses(
        AIHeloTransportState state,
        Aircraft aircraft,
        CargoMission mission)
    {
        if (mission.Airdrop)
        {
            return;
        }

        bool waitingForReleasedCargo = mission.ActivatedCargoCount < mission.ReleasedCargoCount;
        bool moreCargoAtThisLz = HasDeployableCargo(aircraft)
            || mission.ReleasedCargoCount < mission.ExpectedCargoLoads;
        if (waitingForReleasedCargo || moreCargoAtThisLz || mission.CargoClearancePending)
        {
            TouchedDownTimeField?.SetValue(state, 0f);
        }
    }

    private static IEnumerator ClearGroundVehicleFromRamp(
        Aircraft aircraft,
        GroundVehicle vehicle,
        CargoMission mission)
    {
        float earliestMoveAt = mission.LastCargoReleasedAt + 3f;
        while (vehicle != null && aircraft != null && Time.timeSinceLevelLoad < earliestMoveAt)
        {
            yield return new WaitForSeconds(0.1f);
        }
        if (vehicle == null || aircraft == null
            || !TryFindCargoClearanceDirection(aircraft, vehicle, 10f, out Vector3 direction))
        {
            if (vehicle != null)
            {
                vehicle.SetHoldPosition(true);
            }
            mission.CargoClearancePending = false;
            yield break;
        }

        GlobalPosition clearPoint = (vehicle.transform.position + direction * 6f).ToGlobalPosition();
        vehicle.SetHoldPosition(false);
        vehicle.UnitCommand?.SetDestination(clearPoint, playerCommand: true);
        float timeout = Time.timeSinceLevelLoad + 10f;
        while (vehicle != null
            && !vehicle.disabled
            && Time.timeSinceLevelLoad < timeout
            && CommanderGameAccess.HorizontalDistance(vehicle.transform.position, clearPoint.ToLocalPosition()) > 1.25f)
        {
            yield return new WaitForSeconds(0.25f);
        }

        if (vehicle != null && !vehicle.disabled)
        {
            vehicle.SetHoldPosition(true);
            vehicle.UnitCommand?.SetDestination(vehicle.GlobalPosition(), playerCommand: false);
        }
        mission.CargoClearancePending = false;
    }

    private static IEnumerator ClearStaticCargoFromRamp(Aircraft aircraft, Unit cargo, CargoMission mission)
    {
        float earliestMoveAt = mission.LastCargoReleasedAt + 3f;
        while (cargo != null && aircraft != null && Time.timeSinceLevelLoad < earliestMoveAt)
        {
            yield return new WaitForSeconds(0.1f);
        }

        if (cargo == null || aircraft == null)
        {
            mission.CargoClearancePending = false;
            yield break;
        }

        if (!TryFindCargoClearanceDirection(aircraft, cargo, 10f, out Vector3 direction))
        {
            CommanderPlugin.Log.LogWarning(
                $"Supply cargo could not find 10m ramp clearance: {CommanderGameAccess.GetUnitLabel(cargo)}");
            mission.CargoClearancePending = false;
            yield break;
        }

        Vector3 start = cargo.transform.position;
        Vector3 target = start + direction.normalized * 6f;
        const float moveDuration = 5f;
        float startedAt = Time.timeSinceLevelLoad;
        while (cargo != null && Time.timeSinceLevelLoad - startedAt < moveDuration)
        {
            float t = Mathf.SmoothStep(0f, 1f, (Time.timeSinceLevelLoad - startedAt) / moveDuration);
            Vector3 position = Vector3.Lerp(start, target, t);
            if (cargo.rb != null)
            {
                cargo.rb.MovePosition(position);
                cargo.rb.velocity = Vector3.zero;
                cargo.rb.angularVelocity = Vector3.zero;
            }
            else
            {
                cargo.transform.position = position;
            }
            yield return new WaitForFixedUpdate();
        }
        mission.CargoClearancePending = false;
    }

    private static bool TryFindCargoClearanceDirection(
        Aircraft aircraft,
        Unit cargo,
        float scanDistance,
        out Vector3 direction)
    {
        Vector3 rear = -aircraft.transform.forward;
        rear.y = 0f;
        rear = rear.sqrMagnitude > 0.01f ? rear.normalized : Vector3.back;
        Vector3[] candidates =
        {
            Quaternion.AngleAxis(-70f, Vector3.up) * rear,
            Quaternion.AngleAxis(70f, Vector3.up) * rear,
            rear,
        };
        Vector3 origin = cargo.transform.position + Vector3.up * Mathf.Max(cargo.maxRadius * 0.25f, 0.5f);
        for (int i = 0; i < candidates.Length; i++)
        {
            Vector3 candidate = candidates[i].normalized;
            if (!Physics.Raycast(origin, candidate, scanDistance, 2112, QueryTriggerInteraction.Ignore))
            {
                direction = candidate;
                return true;
            }
        }

        direction = Vector3.zero;
        return false;
    }

    private void HandleAircraftReturned(Aircraft aircraft)
    {
        if (!assignedMissions.TryGetValue(aircraft, out CargoMission mission)
            || !mission.PurchasedWithFunds
            || mission.PurchaseRefunded
            || mission.Hq == null)
        {
            return;
        }

        mission.Hq.ModifyUnitSupply(aircraft.definition, -1);
        mission.Hq.AddFunds(mission.PurchaseCost);
        mission.PurchaseRefunded = true;
    }

    private void PruneFinishedMissions()
    {
        if (assignedMissions.Count == 0)
        {
            return;
        }

        List<Aircraft>? stale = null;
        foreach (KeyValuePair<Aircraft, CargoMission> entry in assignedMissions)
        {
            if (entry.Key != null && !entry.Key.disabled)
            {
                continue;
            }

            stale ??= new List<Aircraft>();
            stale.Add(entry.Key!);
        }

        if (stale == null)
        {
            return;
        }

        for (int i = 0; i < stale.Count; i++)
        {
            Aircraft aircraft = stale[i];
            if (ReferenceEquals(aircraft, null))
            {
                continue;
            }

            if (aircraft.autopilot != null)
            {
                terrainClearanceAutopilots.Remove(aircraft.autopilot);
            }

            assignedMissions.Remove(aircraft);
        }
    }

    private static bool TrySelectCargoStation(Aircraft aircraft)
    {
        for (int i = 0; i < aircraft.weaponStations.Count; i++)
        {
            WeaponStation station = aircraft.weaponStations[i];
            if (station != null && station.Cargo && station.WeaponInfo != null && station.WeaponInfo.cargo && HasDeployableCargo(station))
            {
                aircraft.weaponManager.currentWeaponStation = station;
                return true;
            }
        }

        return false;
    }

    private static bool TrySelectNextCargoWeapon(
        Aircraft aircraft,
        out WeaponStation station,
        out Weapon cargoWeapon)
    {
        for (int stationIndex = 0; stationIndex < aircraft.weaponStations.Count; stationIndex++)
        {
            WeaponStation candidate = aircraft.weaponStations[stationIndex];
            if (candidate == null || !candidate.Cargo)
            {
                continue;
            }

            for (int weaponIndex = 0; weaponIndex < candidate.Weapons.Count; weaponIndex++)
            {
                Weapon weapon = candidate.Weapons[weaponIndex];
                if (weapon != null && weapon.GetAmmoLoaded() > 0)
                {
                    station = candidate;
                    cargoWeapon = weapon;
                    return true;
                }
            }
        }

        station = null!;
        cargoWeapon = null!;
        return false;
    }

    private static bool HasDeployableCargo(Aircraft aircraft)
    {
        return CountDeployableCargo(aircraft) > 0;
    }

    private static int CountDeployableCargo(Aircraft aircraft)
    {
        int count = 0;
        for (int i = 0; i < aircraft.weaponStations.Count; i++)
        {
            WeaponStation station = aircraft.weaponStations[i];
            if (station == null || !station.Cargo)
            {
                continue;
            }

            for (int weaponIndex = 0; weaponIndex < station.Weapons.Count; weaponIndex++)
            {
                Weapon? weapon = station.Weapons[weaponIndex];
                if (weapon != null && weapon.GetAmmoLoaded() > 0)
                {
                    count += weapon.GetAmmoLoaded();
                }
            }
        }

        return count;
    }

    private static bool HasDeployableCargo(WeaponStation station)
    {
        for (int i = 0; i < station.Weapons.Count; i++)
        {
            if (station.Weapons[i] != null && station.Weapons[i].GetAmmoLoaded() > 0)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class PendingTargetSelection
    {
        internal PendingTargetSelection(
            CargoAircraftOption aircraft,
            Loadout loadout,
            string cargoLabel,
            Airbase airbase,
            bool highTerrainClearance,
            float terrainClearanceMeters,
            bool airdrop,
            string supportSummary,
            bool useOtherAirfields)
        {
            Aircraft = aircraft;
            Loadout = loadout;
            CargoLabel = cargoLabel;
            Airbase = airbase;
            HighTerrainClearance = highTerrainClearance;
            TerrainClearanceMeters = terrainClearanceMeters;
            Airdrop = airdrop;
            SupportSummary = supportSummary;
            UseOtherAirfields = useOtherAirfields;
        }

        internal CargoAircraftOption Aircraft { get; }
        internal Loadout Loadout { get; }
        internal string CargoLabel { get; }
        internal Airbase Airbase { get; }
        internal bool HighTerrainClearance { get; }
        internal float TerrainClearanceMeters { get; }
        internal bool Airdrop { get; }
        internal string SupportSummary { get; }
        internal bool UseOtherAirfields { get; }
        internal List<GlobalPosition> Targets { get; } = new();

        internal string GetTargetPrompt()
        {
            string targetType = Airdrop ? "airdrop point" : "landing point";
            return $"Click a {targetType} in the 3D world. The game's Cancel binding cancels.";
        }
    }

    private sealed class QueuedCargoSpawn
    {
        internal QueuedCargoSpawn(
            CargoAircraftOption aircraft,
            Loadout loadout,
            string cargoLabel,
            Airbase requestedAirbase,
            bool highTerrainClearance,
            float terrainClearanceMeters,
            bool airdrop,
            string supportSummary,
            bool useOtherAirfields,
            IReadOnlyList<GlobalPosition> targets)
        {
            Aircraft = aircraft;
            Loadout = loadout;
            CargoLabel = cargoLabel;
            RequestedAirbase = requestedAirbase;
            HighTerrainClearance = highTerrainClearance;
            TerrainClearanceMeters = terrainClearanceMeters;
            Airdrop = airdrop;
            SupportSummary = supportSummary;
            UseOtherAirfields = useOtherAirfields;
            Targets = new List<GlobalPosition>(targets);
        }

        internal CargoAircraftOption Aircraft { get; }
        internal Loadout Loadout { get; }
        internal string CargoLabel { get; }
        internal Airbase RequestedAirbase { get; }
        internal bool HighTerrainClearance { get; }
        internal float TerrainClearanceMeters { get; }
        internal bool Airdrop { get; }
        internal string SupportSummary { get; }
        internal bool UseOtherAirfields { get; }
        internal List<GlobalPosition> Targets { get; }
        internal GlobalPosition Target => Targets[0];
    }

    private sealed class PendingAircraftSpawn
    {
        internal PendingAircraftSpawn(
            FactionHQ hq,
            AircraftDefinition definition,
            string cargoLabel,
            GlobalPosition target,
            bool highTerrainClearance,
            float terrainClearanceMeters,
            bool airdrop,
            string supportSummary,
            bool purchasedWithFunds,
            float purchaseCost,
            IReadOnlyList<GlobalPosition> targets,
            float expiresAt,
            Ship? navalTarget = null)
        {
            Hq = hq;
            Definition = definition;
            CargoLabel = cargoLabel;
            Target = target;
            HighTerrainClearance = highTerrainClearance;
            TerrainClearanceMeters = terrainClearanceMeters;
            Airdrop = airdrop;
            SupportSummary = supportSummary;
            PurchasedWithFunds = purchasedWithFunds;
            PurchaseCost = purchaseCost;
            Targets = new List<GlobalPosition>(targets);
            ExpiresAt = expiresAt;
            NavalTarget = navalTarget;
        }

        internal FactionHQ Hq { get; }
        internal AircraftDefinition Definition { get; }
        internal string CargoLabel { get; }
        internal GlobalPosition Target { get; }
        internal bool HighTerrainClearance { get; }
        internal float TerrainClearanceMeters { get; }
        internal bool Airdrop { get; }
        internal string SupportSummary { get; }
        internal bool PurchasedWithFunds { get; }
        internal float PurchaseCost { get; }
        internal List<GlobalPosition> Targets { get; }
        internal float ExpiresAt { get; }
        internal Ship? NavalTarget { get; }
    }

    private sealed class CargoMission
    {
        internal CargoMission(
            FactionHQ hq,
            GlobalPosition target,
            string cargoLabel,
            bool highTerrainClearance,
            float terrainClearanceMeters,
            bool airdrop,
            bool purchasedWithFunds,
            float purchaseCost,
            int expectedCargoLoads,
            IReadOnlyList<GlobalPosition> deliveryTargets,
            Ship? navalTarget = null)
        {
            Hq = hq;
            DeliveryTargets = new List<GlobalPosition>(deliveryTargets);
            CargoLabel = cargoLabel;
            HighTerrainClearance = highTerrainClearance;
            TerrainClearanceMeters = terrainClearanceMeters;
            Airdrop = airdrop;
            PurchasedWithFunds = purchasedWithFunds;
            PurchaseCost = purchaseCost;
            ExpectedCargoLoads = expectedCargoLoads;
            NavalTarget = navalTarget;
        }

        internal FactionHQ Hq { get; }
        internal GlobalPosition Target => DeliveryTargets[0];
        internal List<GlobalPosition> DeliveryTargets { get; }
        internal string CargoLabel { get; }
        internal bool HighTerrainClearance { get; }
        internal float TerrainClearanceMeters { get; }
        internal bool Airdrop { get; }
        internal bool PurchasedWithFunds { get; }
        internal float PurchaseCost { get; }
        internal int ExpectedCargoLoads { get; }
        internal Ship? NavalTarget { get; }
        internal bool PurchaseRefunded { get; set; }
        internal bool Initialized { get; set; }
        internal bool TargetOverrideActive { get; set; } = true;
        internal int ActivatedCargoCount { get; set; }
        internal int ReleasedCargoCount { get; set; }
        internal float NextCargoReleaseAt { get; set; }
        internal float LastCargoReleasedAt { get; set; }
        internal bool CargoClearancePending { get; set; }
    }}

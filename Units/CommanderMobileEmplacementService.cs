using System;
using System.Collections.Generic;
using NuclearOption.Networking;
using UnityEngine;

namespace NuclearOptionCommander;

internal sealed class CommanderMobileEmplacementService
{
    private const float CommandTruckRange = 300f;
    private const float TractorRange = 300f;
    private const float ArrivalDistance = 12f;
    private const float StagingDistance = 18f;
    private const float HandlingSeconds = 10f;
    private const float UpdateIntervalSeconds = 0.5f;
    private const float StatusDurationSeconds = 5f;

    private readonly CommanderSelectionService selectionService;
    private readonly List<RelocationJob> jobs = new();
    private readonly HashSet<GroundVehicle> reservedHaulers = new();

    private PendingRelocation? pendingRelocation;
    private float nextUpdateAt;
    private float statusUntil;
    private string statusText = string.Empty;

    private bool issuingInternalHaulerCommand;

    internal static CommanderMobileEmplacementService? Instance { get; private set; }

    internal CommanderMobileEmplacementService(CommanderSelectionService selectionService)
    {
        this.selectionService = selectionService;
        Instance = this;
    }

    internal bool AwaitingDestination => pendingRelocation != null;
    internal string StatusText => Time.unscaledTime <= statusUntil ? statusText : string.Empty;

    internal void Activate()
    {
        nextUpdateAt = CommanderScheduler.Stagger("mobile-emplacements", UpdateIntervalSeconds, 0.2f);
    }

    internal void Deactivate()
    {
        pendingRelocation = null;
        statusText = string.Empty;
    }

    internal void ResetSession()
    {
        pendingRelocation = null;
        jobs.Clear();
        reservedHaulers.Clear();
        statusText = string.Empty;
    }

    internal void TickActive()
    {
        if (AwaitingDestination && Input.GetKeyDown(KeyCode.Escape))
        {
            pendingRelocation = null;
            SetStatus("Trailer relocation cancelled.");
        }
    }

    internal void TickPersistent()
    {
        if (!CommanderScheduler.IsDue(ref nextUpdateAt, UpdateIntervalSeconds))
        {
            return;
        }

        for (int i = jobs.Count - 1; i >= 0; i--)
        {
            RelocationJob job = jobs[i];
            if (!UpdateJob(job))
            {
                continue;
            }

            if (job.Tractor != null && !job.Tractor.disabled)
            {
                CommanderGameAccess.SetUnitHoldPosition(job.Tractor, hold: false);
            }
            reservedHaulers.Remove(job.Tractor!);
            jobs.RemoveAt(i);
        }
    }

    internal bool IsMoveableTrailer(Unit? unit)
    {
        return CommanderSettings.EnableMobileEmplacements
            && unit is GroundVehicle vehicle
            && CommanderGameAccess.IsFriendlyUnit(vehicle, CommanderGameAccess.GetLocalHq())
            && CommanderGameAccess.IsTrailerVehicleDefinition(vehicle.definition as VehicleDefinition);
    }

    internal bool IsRelocating(Unit? trailer)
    {
        for (int i = 0; i < jobs.Count; i++)
        {
            if (ReferenceEquals(jobs[i].Trailer, trailer))
            {
                return true;
            }
        }

        return pendingRelocation != null && ReferenceEquals(pendingRelocation.Trailer, trailer);
    }

    internal void BeginRelocation()
    {
        if (!CommanderSettings.EnableMobileEmplacements)
        {
            SetStatus("Mobile Emplacements is disabled in Commander settings.");
            return;
        }

        if (!CommanderNetworkHelper.HasServerAuthority)
        {
            SetStatus("Mobile emplacements require host authority (the host manages trailer lifecycle).");
            return;
        }

        Unit? selected = selectionService.FocusedSelection;
        if (!IsMoveableTrailer(selected) || selected is not GroundVehicle trailer)
        {
            SetStatus("Select a friendly static trailer first.");
            return;
        }

        if (IsRelocating(trailer))
        {
            SetStatus("This trailer already has a relocation order.");
            return;
        }

        if (!TryFindCommandTruckAndTractor(trailer, out GroundVehicle commandTruck, out GroundVehicle tractor))
        {
            SetStatus("Requires a nearby fire-control truck and an idle HLT/MSV Tractor or Flatbed within 300 m.");
            return;
        }

        pendingRelocation = new PendingRelocation(trailer, commandTruck, tractor);
        SetStatus("Click the new emplacement position in the 3D world. Esc cancels.");
    }

    internal bool TrySetDestinationFromWorld(Vector2 screenPosition)
    {
        PendingRelocation? pending = pendingRelocation;
        if (pending == null)
        {
            return false;
        }

        if (!CommanderGameAccess.TryRaycastWorldPosition(screenPosition, out GlobalPosition destination))
        {
            SetStatus("No valid terrain position was found.");
            return true;
        }

        pendingRelocation = null;
        if (pending.Trailer == null || pending.Trailer.disabled || pending.Tractor == null || pending.Tractor.disabled)
        {
            SetStatus("The trailer or tractor is no longer available.");
            return true;
        }

        RelocationJob job = new(pending.Trailer, pending.CommandTruck, pending.Tractor, destination);
        jobs.Add(job);
        reservedHaulers.Add(pending.Tractor);
        CommanderGameAccess.SetUnitHoldPosition(pending.Tractor, hold: true);
        IssueHaulerDestination(pending.Tractor, job.PickupStagingPoint);
        SetStatus($"{CommanderGameAccess.GetUnitLabel(pending.Tractor)} dispatched to load the trailer.");
        return true;
    }

    private bool UpdateJob(RelocationJob job)
    {
        if (job.Tractor == null || job.Tractor.disabled)
        {
            RestoreTrailer(job, job.GetSafeRestorePosition());
            SetStatus("Relocation failed: tractor lost. The trailer was restored.");
            return true;
        }

        switch (job.Stage)
        {
            case RelocationStage.ApproachingPickup:
                if (job.Trailer == null || job.Trailer.disabled)
                {
                    SetStatus("Relocation cancelled: trailer unavailable.");
                    return true;
                }

                if (HasArrived(job.Tractor, job.PickupStagingPoint))
                {
                    job.Stage = RelocationStage.Loading;
                    job.StageCompleteAt = Time.unscaledTime + HandlingSeconds;
                    IssueHaulerDestination(job.Tractor, job.Tractor.GlobalPosition());
                    SetStatus("Loading trailer (10 seconds).");
                }
                break;

            case RelocationStage.Loading:
                if (job.Trailer == null || job.Trailer.disabled)
                {
                    SetStatus("Relocation cancelled: trailer unavailable.");
                    return true;
                }

                if (Time.unscaledTime < job.StageCompleteAt)
                {
                    break;
                }

                job.CaptureTrailerState();
                RemoveTransportedTrailer(job);
                job.Stage = RelocationStage.Transporting;
                IssueHaulerDestination(job.Tractor, job.DeliveryStagingPoint);
                SetStatus("Trailer loaded. Tractor moving to destination.");
                break;

            case RelocationStage.Transporting:
                if (HasArrived(job.Tractor, job.DeliveryStagingPoint))
                {
                    job.Stage = RelocationStage.Unloading;
                    job.StageCompleteAt = Time.unscaledTime + HandlingSeconds;
                    IssueHaulerDestination(job.Tractor, job.Tractor.GlobalPosition());
                    SetStatus("Deploying trailer (10 seconds).");
                }
                break;

            case RelocationStage.Unloading:
                if (Time.unscaledTime < job.StageCompleteAt)
                {
                    break;
                }

                if (!RestoreTrailer(job, job.Destination))
                {
                    SetStatus("Trailer deployment failed; it will be restored when the order is cancelled.");
                    return false;
                }

                Vector3 clearDirection = job.Tractor.transform.forward;
                clearDirection.y = 0f;
                if (clearDirection.sqrMagnitude < 0.1f)
                {
                    clearDirection = Vector3.forward;
                }
                CommanderGameAccess.SetUnitHoldPosition(job.Tractor, hold: false);
                IssueHaulerDestination(job.Tractor,
                    (job.Tractor.transform.position + clearDirection.normalized * 25f).ToGlobalPosition());
                SetStatus("Trailer deployed. Tractor released to Basegame AI.");
                return true;
        }

        return false;
    }

    private bool TryFindCommandTruckAndTractor(
        GroundVehicle trailer,
        out GroundVehicle commandTruck,
        out GroundVehicle tractor)
    {
        commandTruck = null!;
        tractor = null!;
        FactionHQ? hq = CommanderGameAccess.GetLocalHq();
        if (hq?.factionUnits == null)
        {
            return false;
        }

        float commandDistance = float.MaxValue;
        foreach (PersistentID unitId in hq.factionUnits)
        {
            if (!unitId.TryGetUnit(out Unit unit)
                || unit is not GroundVehicle vehicle
                || vehicle.disabled
                || !IsCommandTruck(vehicle))
            {
                continue;
            }

            float distance = CommanderGameAccess.HorizontalDistance(vehicle.transform.position, trailer.transform.position);
            if (distance <= CommandTruckRange && distance < commandDistance)
            {
                commandDistance = distance;
                commandTruck = vehicle;
            }
        }

        if (commandTruck == null)
        {
            return false;
        }

        float tractorDistance = float.MaxValue;
        foreach (PersistentID unitId in hq.factionUnits)
        {
            if (!unitId.TryGetUnit(out Unit unit)
                || unit is not GroundVehicle vehicle
                || vehicle.disabled
                || reservedHaulers.Contains(vehicle)
                || !IsHauler(vehicle)
                || !IsIdle(vehicle))
            {
                continue;
            }

            float distance = CommanderGameAccess.HorizontalDistance(vehicle.transform.position, commandTruck.transform.position);
            if (distance <= TractorRange && distance < tractorDistance)
            {
                tractorDistance = distance;
                tractor = vehicle;
            }
        }

        return tractor != null;
    }

    private static bool IsCommandTruck(GroundVehicle vehicle)
    {
        string code = vehicle.definition?.code ?? string.Empty;
        string name = vehicle.definition?.unitName ?? vehicle.unitName ?? string.Empty;
        return string.Equals(code, "CMD", StringComparison.OrdinalIgnoreCase)
            || name.IndexOf("Fire Control", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("Command", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsHauler(GroundVehicle vehicle)
    {
        string name = vehicle.definition?.unitName ?? vehicle.unitName ?? string.Empty;
        string key = vehicle.definition?.jsonKey ?? string.Empty;
        return name.EndsWith("Tractor", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Flatbed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "HLT-T", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "Truck2-T", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "HLT-L", StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, "Truck2-L", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsReservedHauler(Unit? unit)
    {
        return Instance != null
            && unit is GroundVehicle vehicle
            && Instance.reservedHaulers.Contains(vehicle);
    }

    internal static bool ShouldBlockDestination(UnitCommand command)
    {
        CommanderMobileEmplacementService? service = Instance;
        if (service == null || service.issuingInternalHaulerCommand)
        {
            return false;
        }

        return IsReservedHauler(command.GetComponent<Unit>());
    }

    private void IssueHaulerDestination(GroundVehicle hauler, GlobalPosition destination)
    {
        issuingInternalHaulerCommand = true;
        try
        {
            hauler.UnitCommand?.SetDestination(destination, playerCommand: false);
        }
        finally
        {
            issuingInternalHaulerCommand = false;
        }
    }

    private static bool IsIdle(GroundVehicle vehicle)
    {
        if (Mathf.Abs(vehicle.speed) > 1.5f)
        {
            return false;
        }

        return !CommanderGameAccess.TryGetCurrentCommandPosition(vehicle, out GlobalPosition command)
            || CommanderGameAccess.HorizontalDistance(vehicle.transform.position, command.ToLocalPosition()) <= 20f;
    }

    private static bool HasArrived(GroundVehicle vehicle, GlobalPosition destination)
    {
        return CommanderGameAccess.HorizontalDistance(vehicle.transform.position, destination.ToLocalPosition()) <= ArrivalDistance
            && Mathf.Abs(vehicle.speed) <= 2f;
    }

    private static void RemoveTransportedTrailer(RelocationJob job)
    {
        GroundVehicle? trailer = job.Trailer;
        if (trailer == null)
        {
            return;
        }

        job.LastTrailerPosition = trailer.GlobalPosition();
        UnityEngine.Object.Destroy(trailer.gameObject);
        job.Trailer = null;
    }

    private static bool RestoreTrailer(RelocationJob job, GlobalPosition position)
    {
        if (job.Trailer != null && !job.Trailer.disabled)
        {
            return true;
        }

        if (!job.TrailerRemoved || job.Definition?.unitPrefab == null || job.Hq == null || NetworkSceneSingleton<Spawner>.i == null)
        {
            return !job.TrailerRemoved;
        }

        Quaternion rotation = Quaternion.Euler(0f, job.Tractor != null ? job.Tractor.transform.eulerAngles.y : job.OriginalRotation.eulerAngles.y, 0f);
        Vector3 requestedLocal = position.ToLocalPosition();
        Vector3 groundLocal = requestedLocal - Vector3.up * job.Definition.spawnOffset.y;
        if (Physics.Raycast(
            requestedLocal + Vector3.up * 250f,
            Vector3.down,
            out RaycastHit groundHit,
            1000f,
            8256,
            QueryTriggerInteraction.Ignore))
        {
            groundLocal = groundHit.point;
        }

        GroundVehicle restored = NetworkSceneSingleton<Spawner>.i.SpawnVehicle(
            job.Definition.unitPrefab,
            (groundLocal + Vector3.up * job.Definition.spawnOffset.y).ToGlobalPosition(),
            rotation,
            Vector3.zero,
            job.Hq,
            job.UniqueName,
            job.Skill,
            holdPosition: true,
            null);
        if (restored == null)
        {
            return false;
        }

        int stationCount = Mathf.Min(restored.weaponStations.Count, job.Ammo.Length);
        for (int i = 0; i < stationCount; i++)
        {
            restored.weaponStations[i].Ammo = Mathf.Clamp(job.Ammo[i], 0, restored.weaponStations[i].FullAmmo);
            restored.weaponStations[i].Updated();
        }

        Radar[] radars = restored.GetComponentsInChildren<Radar>();
        for (int i = 0; i < radars.Length; i++)
        {
            radars[i].activated = job.RadarOnline;
            radars[i].enabled = job.RadarOnline;
        }

        job.Trailer = restored;
        job.TrailerRemoved = false;
        job.LastTrailerPosition = restored.GlobalPosition();
        return true;
    }

    private void SetStatus(string text)
    {
        statusText = text;
        statusUntil = Time.unscaledTime + StatusDurationSeconds;
    }

    private sealed class PendingRelocation
    {
        internal PendingRelocation(GroundVehicle trailer, GroundVehicle commandTruck, GroundVehicle tractor)
        {
            Trailer = trailer;
            CommandTruck = commandTruck;
            Tractor = tractor;
        }

        internal GroundVehicle Trailer { get; }
        internal GroundVehicle CommandTruck { get; }
        internal GroundVehicle Tractor { get; }
    }

    private sealed class RelocationJob
    {
        internal RelocationJob(GroundVehicle trailer, GroundVehicle commandTruck, GroundVehicle tractor, GlobalPosition destination)
        {
            Trailer = trailer;
            CommandTruck = commandTruck;
            Tractor = tractor;
            Destination = destination;
            LastTrailerPosition = trailer.GlobalPosition();
            OriginalRotation = trailer.transform.rotation;
            Hq = trailer.NetworkHQ;
            Definition = trailer.definition as VehicleDefinition;
            UniqueName = trailer.UniqueName;
            Skill = trailer.skill;

            Vector3 pickupDirection = commandTruck.transform.position - trailer.transform.position;
            pickupDirection.y = 0f;
            if (pickupDirection.sqrMagnitude < 0.1f)
            {
                pickupDirection = trailer.transform.forward;
            }
            PickupStagingPoint = (trailer.transform.position + pickupDirection.normalized * StagingDistance).ToGlobalPosition();

            Vector3 destinationLocal = destination.ToLocalPosition();
            Vector3 deliveryDirection = tractor.transform.position - destinationLocal;
            deliveryDirection.y = 0f;
            if (deliveryDirection.sqrMagnitude < 0.1f)
            {
                deliveryDirection = -tractor.transform.forward;
            }
            DeliveryStagingPoint = (destinationLocal + deliveryDirection.normalized * StagingDistance).ToGlobalPosition();
        }

        internal GroundVehicle? Trailer { get; set; }
        internal GroundVehicle CommandTruck { get; }
        internal GroundVehicle Tractor { get; }
        internal GlobalPosition Destination { get; }
        internal GlobalPosition PickupStagingPoint { get; }
        internal GlobalPosition DeliveryStagingPoint { get; }
        internal GlobalPosition LastTrailerPosition { get; set; }
        internal Quaternion OriginalRotation { get; }
        internal FactionHQ? Hq { get; }
        internal VehicleDefinition? Definition { get; }
        internal string UniqueName { get; }
        internal float Skill { get; }
        internal RelocationStage Stage { get; set; }
        internal float StageCompleteAt { get; set; }
        internal int[] Ammo { get; private set; } = Array.Empty<int>();
        internal bool RadarOnline { get; private set; } = true;
        internal bool TrailerRemoved { get; set; }

        internal void CaptureTrailerState()
        {
            if (Trailer == null)
            {
                return;
            }

            Ammo = new int[Trailer.weaponStations.Count];
            for (int i = 0; i < Ammo.Length; i++)
            {
                Ammo[i] = Trailer.weaponStations[i].Ammo;
            }

            Radar[] radars = Trailer.GetComponentsInChildren<Radar>();
            RadarOnline = radars.Length == 0;
            for (int i = 0; i < radars.Length; i++)
            {
                RadarOnline |= radars[i] != null && radars[i].activated;
            }

            TrailerRemoved = true;
        }

        internal GlobalPosition GetSafeRestorePosition()
        {
            if (Tractor != null && !Tractor.disabled && TrailerRemoved)
            {
                return Tractor.GlobalPosition();
            }

            return LastTrailerPosition;
        }
    }

    private enum RelocationStage
    {
        ApproachingPickup,
        Loading,
        Transporting,
        Unloading,
    }
}

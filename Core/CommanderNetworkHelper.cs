using System;
using System.Collections.Generic;
using NuclearOption.Networking;
using UnityEngine;

namespace NuclearOptionCommander;

/// <summary>
/// Provides network-aware helpers for multiplayer compatibility.
/// In Nuclear Option's Mirage-based networking, the host runs the server.
/// Game APIs like VehicleDepot.TrySpawnVehicle and Airbase.TrySpawnAircraft
/// are server-authoritative. This class routes spawn calls correctly whether
/// running on the server (host/SP) or on a client.
///
/// For clients: spawn calls are forwarded to the game APIs directly.
/// Nuclear Option's VehicleDepot and Airbase inherit from NetworkBehaviour and
/// their spawn methods internally handle server routing. The mod previously blocked
/// clients with explicit host-only guards that prevented the game's own networking
/// from functioning.
///
/// For operations that truly require direct server access (e.g., Spawner.SpawnVehicle),
/// use HasServerAuthority to gate those calls.
/// </summary>
internal static class CommanderNetworkHelper
{
    /// <summary>
    /// Returns true when the local instance is the server (host or singleplayer).
    /// </summary>
    internal static bool IsServer
    {
        get
        {
            if (GameManager.gameState == GameState.SinglePlayer)
            {
                return true;
            }

            return NetworkManagerNuclearOption.i != null && NetworkManagerNuclearOption.i.Server.Active;
        }
    }

    /// <summary>
    /// Returns true when the game is in a multiplayer session (regardless of host/client).
    /// </summary>
    internal static bool IsMultiplayer => GameManager.gameState == GameState.Multiplayer;

    /// <summary>
    /// Returns true when this client is NOT the server in a multiplayer session.
    /// </summary>
    internal static bool IsClientOnly => IsMultiplayer && !IsServer;

    /// <summary>
    /// Returns true if the local machine can perform server-authoritative operations
    /// such as direct Spawner calls or Object.Destroy on networked objects.
    /// </summary>
    internal static bool HasServerAuthority => IsServer;

    /// <summary>
    /// Returns true if the network is available (either singleplayer or multiplayer connected).
    /// </summary>
    internal static bool IsNetworkReady
    {
        get
        {
            if (GameManager.gameState == GameState.SinglePlayer)
            {
                return true;
            }

            return NetworkManagerNuclearOption.i != null;
        }
    }

    /// <summary>
    /// Attempts to spawn a vehicle at the specified depot. Works for both host and client.
    /// VehicleDepot.TrySpawnVehicle is a game API that handles server authority internally.
    /// </summary>
    internal static bool RequestDepotSpawn(VehicleDepot depot, VehicleDefinition definition)
    {
        if (depot == null || definition == null)
        {
            return false;
        }

        return depot.TrySpawnVehicle(definition);
    }

    /// <summary>
    /// Attempts to spawn an aircraft at the specified airbase. Works for both host and client.
    /// Airbase.TrySpawnAircraft is a game API that handles server authority internally.
    /// </summary>
    internal static Airbase.TrySpawnResult RequestAircraftSpawn(
        Airbase airbase,
        AircraftDefinition definition,
        LiveryKey liveryKey,
        Loadout loadout,
        float fuelLevel)
    {
        if (airbase == null || definition == null)
        {
            return new Airbase.TrySpawnResult { Allowed = false };
        }

        return airbase.TrySpawnAircraft(null, definition, liveryKey, loadout, fuelLevel);
    }

    /// <summary>
    /// Modifies unit supply on the HQ. FactionHQ is a NetworkBehaviour and synchronizes
    /// supply state across the network.
    /// </summary>
    internal static void RequestModifyUnitSupply(FactionHQ hq, UnitDefinition definition, int delta)
    {
        if (hq == null || definition == null)
        {
            return;
        }

        hq.ModifyUnitSupply(definition, delta);
    }

    /// <summary>
    /// Modifies faction funds on the HQ. FactionHQ is a NetworkBehaviour and synchronizes
    /// funds state across the network.
    /// </summary>
    internal static void RequestAddFunds(FactionHQ hq, float amount)
    {
        if (hq == null)
        {
            return;
        }

        hq.AddFunds(amount);
    }

    /// <summary>
    /// Resets state on session change.
    /// </summary>
    internal static void ResetSession()
    {
    }
}

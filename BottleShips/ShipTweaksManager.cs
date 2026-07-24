using System;
using System.Collections.Generic;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace BottleShips;

internal static class ShipTweaksManager
{
    private const string ConfigGroup = "20 - Ship Tweaks";
    private const string RockTheBoatGuid = "shudnal.RockTheBoat";

    private static readonly HashSet<string> SupportedShipPrefabs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Raft",
        "Karve",
        "VikingShip",
        "VikingShip_Ashlands"
    };

    private static ConfigEntry<ShipScope> _scope = null!;
    private static ConfigEntry<float> _cameraMaxDistance = null!;
    private static ConfigEntry<float> _exploreRadiusMultiplier = null!;
    private static ConfigEntry<float> _sailingForceMultiplier = null!;
    private static ConfigEntry<float> _paddlingForceMultiplier = null!;
    private static ConfigEntry<float> _steeringMultiplier = null!;
    private static ConfigEntry<float> _paddlingBonusPerPassenger = null!;
    private static bool _rockTheBoatChecked;
    private static bool _rockTheBoatInstalled;
    private static bool _rockTheBoatWarningLogged;

    internal enum ShipScope
    {
        SupportedVanillaShips,
        AllShips
    }

    internal static void BindConfig(BottleShipsPlugin plugin)
    {
        _scope = plugin.config(
            ConfigGroup,
            "Scope",
            ShipScope.SupportedVanillaShips,
            "Ships affected by these settings. SupportedVanillaShips includes Raft, Karve, Longship, and Drakkar. AllShips also includes ships added by other mods.",
            order: 1000);
        _cameraMaxDistance = plugin.config(
            ConfigGroup,
            "Camera Max Distance",
            6f,
            new ConfigDescription(
                "Maximum camera zoom distance while controlling, standing on, or inside an affected ship. The vanilla value is 6.",
                new AcceptableValueRange<float>(6f, 100f)),
            synchronizedSetting: false,
            order: 990);
        _exploreRadiusMultiplier = plugin.config(
            ConfigGroup,
            "Explore Radius Multiplier",
            1f,
            new ConfigDescription(
                "Multiplier for the minimap exploration radius while controlling, standing on, or inside an affected ship.",
                new AcceptableValueRange<float>(0.1f, 10f)),
            order: 980);
        _sailingForceMultiplier = plugin.config(
            ConfigGroup,
            "Sailing Force Multiplier",
            1f,
            new ConfigDescription(
                "Multiplier for wind-driven force while sailing at Half or Full. This mainly changes acceleration and does not scale top speed directly. The passenger bonus does not apply. 1 is vanilla; 0 removes sail force.",
                new AcceptableValueRange<float>(0f, 5f)),
            order: 970);
        _paddlingForceMultiplier = plugin.config(
            ConfigGroup,
            "Paddling Force Multiplier",
            1f,
            new ConfigDescription(
                "Multiplier for manual forward paddling at Slow and reverse paddling at Back. The passenger factor is also applied to this force. 1 is vanilla; 0 removes paddling force.",
                new AcceptableValueRange<float>(0f, 5f)),
            order: 960);
        _steeringMultiplier = plugin.config(
            ConfigGroup,
            "Steering Multiplier",
            1f,
            new ConfigDescription(
                "Multiplier for speed-based steering while sailing and paddle steering at Slow or Back. The passenger factor applies only to the Slow or Back paddle steering portion. 1 is vanilla; 0 removes steering force.",
                new AcceptableValueRange<float>(0f, 5f)),
            order: 950);
        _paddlingBonusPerPassenger = plugin.config(
            ConfigGroup,
            "Paddling Bonus Per Passenger",
            0f,
            new ConfigDescription(
                "Additive bonus for each other player aboard. The factor is 1 + this value times the passenger count and affects only Slow or Back paddling propulsion and paddle steering, not sail force or speed-based steering. 0 disables the bonus.",
                new AcceptableValueRange<float>(0f, 1f)),
            order: 940);

    }

    internal static bool TryApplyCameraDistance(GameCamera camera, out CameraDistanceState state)
    {
        state = default;
        float maxDistance = GetFiniteValue(_cameraMaxDistance, 6f);
        if (maxDistance <= camera.m_minDistance || !TryGetAffectedLocalShip(Player.m_localPlayer, out _))
        {
            return false;
        }

        state = new CameraDistanceState(camera.m_maxDistance, camera.m_maxDistanceBoat);
        camera.m_maxDistance = maxDistance;
        camera.m_maxDistanceBoat = maxDistance;
        return true;
    }

    internal static void RestoreCameraDistance(GameCamera camera, ref CameraDistanceState state)
    {
        if (!state.Active)
        {
            return;
        }

        camera.m_maxDistance = state.MaxDistance;
        camera.m_maxDistanceBoat = state.MaxDistanceBoat;
        state.Active = false;
    }

    internal static bool TryApplyExploreRadius(Minimap minimap, Player player, out ExploreRadiusState state)
    {
        state = default;
        float multiplier = GetFiniteValue(_exploreRadiusMultiplier, 1f);
        if (Mathf.Approximately(multiplier, 1f)
            || player == null
            || player != Player.m_localPlayer
            || !TryGetAffectedLocalShip(player, out _))
        {
            return false;
        }

        state = new ExploreRadiusState(minimap.m_exploreRadius);
        minimap.m_exploreRadius *= multiplier;
        return true;
    }

    internal static void RestoreExploreRadius(Minimap minimap, ref ExploreRadiusState state)
    {
        if (!state.Active)
        {
            return;
        }

        minimap.m_exploreRadius = state.ExploreRadius;
        state.Active = false;
    }

    internal static bool TryApplyHandling(Ship ship, out ShipHandlingState state)
    {
        state = default;
        if (!ShouldAffect(ship)
            || ship.m_nview == null
            || !ship.m_nview.IsValid()
            || !ship.m_nview.IsOwner())
        {
            return false;
        }

        float sailingMultiplier = GetFiniteValue(_sailingForceMultiplier, 1f);
        float paddlingMultiplier = GetFiniteValue(_paddlingForceMultiplier, 1f);
        float steeringMultiplier = GetFiniteValue(_steeringMultiplier, 1f);
        float passengerBonus = GetFiniteValue(_paddlingBonusPerPassenger, 0f);

        bool changesHandling = !Mathf.Approximately(sailingMultiplier, 1f)
                               || !Mathf.Approximately(paddlingMultiplier, 1f)
                               || !Mathf.Approximately(steeringMultiplier, 1f)
                               || passengerBonus > 0f;
        if (!changesHandling)
        {
            return false;
        }

        float passengerFactor = passengerBonus > 0f
            ? 1f + passengerBonus * CountPassengers(ship)
            : 1f;

        state = new ShipHandlingState(
            ship.m_sailForceFactor,
            ship.m_backwardForce,
            ship.m_stearVelForceFactor,
            ship.m_stearForce);

        ship.m_sailForceFactor *= sailingMultiplier;
        ship.m_backwardForce *= paddlingMultiplier * passengerFactor;
        ship.m_stearVelForceFactor *= steeringMultiplier;
        ship.m_stearForce *= steeringMultiplier * passengerFactor;
        return true;
    }

    internal static void RestoreHandling(Ship ship, ref ShipHandlingState state)
    {
        if (!state.Active)
        {
            return;
        }

        ship.m_sailForceFactor = state.SailForceFactor;
        ship.m_backwardForce = state.BackwardForce;
        ship.m_stearVelForceFactor = state.SteeringVelocityForceFactor;
        ship.m_stearForce = state.SteeringForce;
        state.Active = false;
    }

    private static bool TryGetAffectedLocalShip(Player? player, out Ship? ship)
    {
        ship = null;
        if (player == null)
        {
            return false;
        }

        ship = player.GetControlledShip();
        if (ship == null)
        {
            ship = player.GetStandingOnShip();
        }

        if (ship == null && player == Player.m_localPlayer)
        {
            ship = Ship.GetLocalShip();
        }

        return ship != null && ShouldAffect(ship);
    }

    private static bool ShouldAffect(Ship? ship)
    {
        if (ship == null || CheckForRockTheBoat())
        {
            return false;
        }

        if (_scope.Value == ShipScope.AllShips)
        {
            return true;
        }

        string prefabName = Utils.GetPrefabName(ship.gameObject);
        return SupportedShipPrefabs.Contains(prefabName);
    }

    private static bool CheckForRockTheBoat()
    {
        if (_rockTheBoatChecked)
        {
            return _rockTheBoatInstalled;
        }

        _rockTheBoatChecked = true;
        _rockTheBoatInstalled = Chainloader.PluginInfos.ContainsKey(RockTheBoatGuid);

        if (_rockTheBoatInstalled && !_rockTheBoatWarningLogged)
        {
            _rockTheBoatWarningLogged = true;
            BottleShipsPlugin.BottleShipsLogger.LogWarning(
                "RockTheBoat is installed. BottleShips ship tweaks are disabled to prevent duplicate camera, minimap, and ship physics patches.");
        }

        return _rockTheBoatInstalled;
    }

    private static int CountPassengers(Ship ship)
    {
        long controllerId = ship.m_shipControlls != null ? ship.m_shipControlls.GetUser() : 0L;
        int passengerCount = 0;

        for (int i = 0; i < ship.m_players.Count; ++i)
        {
            Player player = ship.m_players[i];
            if (player == null)
            {
                continue;
            }

            long playerId = player.GetPlayerID();
            if (playerId == controllerId || IsDuplicatePassenger(ship.m_players, i, playerId, player))
            {
                continue;
            }

            ++passengerCount;
        }

        return passengerCount;
    }

    private static bool IsDuplicatePassenger(IReadOnlyList<Player> players, int index, long playerId, Player player)
    {
        for (int i = 0; i < index; ++i)
        {
            Player previous = players[i];
            if (previous != null
                && (previous == player || playerId != 0L && previous.GetPlayerID() == playerId))
            {
                return true;
            }
        }

        return false;
    }

    private static float GetFiniteValue(ConfigEntry<float> entry, float fallback)
    {
        float value = entry.Value;
        return float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;
    }

    internal struct CameraDistanceState
    {
        internal bool Active;
        internal readonly float MaxDistance;
        internal readonly float MaxDistanceBoat;

        internal CameraDistanceState(float maxDistance, float maxDistanceBoat)
        {
            Active = true;
            MaxDistance = maxDistance;
            MaxDistanceBoat = maxDistanceBoat;
        }
    }

    internal struct ExploreRadiusState
    {
        internal bool Active;
        internal readonly float ExploreRadius;

        internal ExploreRadiusState(float exploreRadius)
        {
            Active = true;
            ExploreRadius = exploreRadius;
        }
    }

    internal struct ShipHandlingState
    {
        internal bool Active;
        internal readonly float SailForceFactor;
        internal readonly float BackwardForce;
        internal readonly float SteeringVelocityForceFactor;
        internal readonly float SteeringForce;

        internal ShipHandlingState(
            float sailForceFactor,
            float backwardForce,
            float steeringVelocityForceFactor,
            float steeringForce)
        {
            Active = true;
            SailForceFactor = sailForceFactor;
            BackwardForce = backwardForce;
            SteeringVelocityForceFactor = steeringVelocityForceFactor;
            SteeringForce = steeringForce;
        }
    }
}

[HarmonyPatch(typeof(GameCamera), nameof(GameCamera.UpdateCamera), typeof(float))]
internal static class BottleShipsGameCameraUpdateCameraPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Prefix(GameCamera __instance, out ShipTweaksManager.CameraDistanceState __state)
    {
        ShipTweaksManager.TryApplyCameraDistance(__instance, out __state);
    }

    [HarmonyPriority(Priority.First)]
    private static void Postfix(GameCamera __instance, ref ShipTweaksManager.CameraDistanceState __state)
    {
        ShipTweaksManager.RestoreCameraDistance(__instance, ref __state);
    }

    [HarmonyPriority(Priority.First)]
    private static Exception? Finalizer(
        GameCamera __instance,
        Exception? __exception,
        ref ShipTweaksManager.CameraDistanceState __state)
    {
        ShipTweaksManager.RestoreCameraDistance(__instance, ref __state);
        return __exception;
    }
}

[HarmonyPatch(typeof(Minimap), nameof(Minimap.UpdateExplore), typeof(float), typeof(Player))]
internal static class BottleShipsMinimapUpdateExplorePatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Prefix(
        Minimap __instance,
        Player player,
        out ShipTweaksManager.ExploreRadiusState __state)
    {
        ShipTweaksManager.TryApplyExploreRadius(__instance, player, out __state);
    }

    [HarmonyPriority(Priority.First)]
    private static void Postfix(Minimap __instance, ref ShipTweaksManager.ExploreRadiusState __state)
    {
        ShipTweaksManager.RestoreExploreRadius(__instance, ref __state);
    }

    [HarmonyPriority(Priority.First)]
    private static Exception? Finalizer(
        Minimap __instance,
        Exception? __exception,
        ref ShipTweaksManager.ExploreRadiusState __state)
    {
        ShipTweaksManager.RestoreExploreRadius(__instance, ref __state);
        return __exception;
    }
}

[HarmonyPatch(typeof(Ship), nameof(Ship.CustomFixedUpdate), typeof(float))]
internal static class BottleShipsShipCustomFixedUpdatePatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Prefix(Ship __instance, out ShipTweaksManager.ShipHandlingState __state)
    {
        ShipTweaksManager.TryApplyHandling(__instance, out __state);
    }

    [HarmonyPriority(Priority.First)]
    private static void Postfix(Ship __instance, ref ShipTweaksManager.ShipHandlingState __state)
    {
        ShipTweaksManager.RestoreHandling(__instance, ref __state);
    }

    [HarmonyPriority(Priority.First)]
    private static Exception? Finalizer(
        Ship __instance,
        Exception? __exception,
        ref ShipTweaksManager.ShipHandlingState __state)
    {
        ShipTweaksManager.RestoreHandling(__instance, ref __state);
        return __exception;
    }
}

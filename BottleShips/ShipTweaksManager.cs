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
    private const string PowerPaddlingRpc = "sighsorry.BottleShips.SetPowerPaddling";
    private const float PowerPaddlingStaminaPerSecond = 10f;
    private const float PowerPaddlingFovIncrease = 10f;
    private const float PowerPaddlingHeartbeatInterval = 0.2f;
    private const float PowerPaddlingHeartbeatTimeout = 0.75f;
    private const float PowerPaddlingFovRiseSeconds = 0.4f;
    private const float PowerPaddlingFovFallSeconds = 0.25f;

    private static readonly Dictionary<Ship, Dictionary<long, PowerPaddlingRequest>> PowerPaddlingRequests = new();
    private static readonly Dictionary<Ship, PowerPaddlingPhysicsContext> PowerPaddlingPhysicsContexts = new();
    private static readonly List<long> InvalidPowerPaddlingSenders = new();

    private static ConfigEntry<float> _exploreRadiusMultiplier = null!;
    private static ConfigEntry<float> _shipPowerMultiplier = null!;
    private static ConfigEntry<float> _powerPaddlingBonusPerPlayer = null!;
    private static bool _rockTheBoatChecked;
    private static bool _rockTheBoatInstalled;
    private static bool _rockTheBoatWarningLogged;
    private static Ship? _localPowerPaddlingShip;
    private static bool _localPowerPaddlingRequested;
    private static float _nextPowerPaddlingHeartbeat;
    private static float _powerPaddlingFovOffset;

    internal static void BindConfig(BottleShipsPlugin plugin)
    {
        _exploreRadiusMultiplier = plugin.config(
            ConfigGroup,
            "Explore Radius Multiplier",
            1f,
            new ConfigDescription(
                "Multiplier for the minimap exploration radius while controlling, standing on, or inside a ship.",
                new AcceptableValueRange<float>(0.1f, 10f)),
            order: 1000);
        _shipPowerMultiplier = plugin.config(
            ConfigGroup,
            "Ship Power Multiplier",
            1f,
            new ConfigDescription(
                "Multiplier for wind-driven force at Half or Full and manual propulsion at Slow or Back. It also scales paddle steering at Slow or Back, but not speed-based steering or rudder response. This mainly changes acceleration rather than setting top speed directly. 1 is vanilla; 0 removes these propulsion forces and paddle steering.",
                new AcceptableValueRange<float>(0f, 5f)),
            order: 990);
        _powerPaddlingBonusPerPlayer = plugin.config(
            ConfigGroup,
            "Power Paddling Bonus Per Player",
            0.5f,
            new ConfigDescription(
                "Additional paddling-force ratio contributed by each helmsman or seated passenger who holds the Run input and spends a base 10 stamina per second. 0.5 adds 50% of the ship's globally scaled paddling force per active player at Back or Slow; merely sitting aboard adds nothing. Power Paddling is unavailable at Half or Full while the sails are deployed. 0 disables Power Paddling. Each active player's own camera field of view smoothly increases by up to 10 degrees; other paddlers do not stack additional FOV on that player.",
                new AcceptableValueRange<float>(0f, 1f)),
            order: 980);
        _powerPaddlingBonusPerPlayer.SettingChanged += (_, _) => HandlePowerPaddlingConfigChanged();
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
        PowerPaddlingPhysicsContexts.Remove(ship);
        if (!ShouldAffect(ship)
            || ship.m_nview == null
            || !ship.m_nview.IsValid())
        {
            return false;
        }

        if (!ship.m_nview.IsOwner())
        {
            PowerPaddlingRequests.Remove(ship);
            return false;
        }

        float shipPowerMultiplier = GetFiniteValue(_shipPowerMultiplier, 1f);
        int activePaddlers = CountActivePowerPaddlers(ship);
        float powerPaddlingBonus =
            activePaddlers * GetFiniteValue(_powerPaddlingBonusPerPlayer, 0.5f);
        if (Mathf.Approximately(shipPowerMultiplier, 1f) && powerPaddlingBonus <= 0f)
        {
            return false;
        }

        state = new ShipHandlingState(
            ship.m_sailForceFactor,
            ship.m_backwardForce,
            ship.m_stearForce);

        ship.m_sailForceFactor *= shipPowerMultiplier;
        ship.m_backwardForce *= shipPowerMultiplier;
        ship.m_stearForce *= shipPowerMultiplier;

        if (powerPaddlingBonus > 0f)
        {
            PowerPaddlingPhysicsContexts[ship] = new PowerPaddlingPhysicsContext(
                powerPaddlingBonus,
                ship.m_backwardForce,
                ship.m_stearForce);
        }

        return true;
    }

    internal static void RestoreHandling(Ship ship, ref ShipHandlingState state)
    {
        PowerPaddlingPhysicsContexts.Remove(ship);
        if (!state.Active)
        {
            return;
        }

        ship.m_sailForceFactor = state.SailForceFactor;
        ship.m_backwardForce = state.BackwardForce;
        ship.m_stearForce = state.SteeringForce;
        state.Active = false;
    }

    internal static void ApplyPowerPaddlingForce(Ship ship, float fixedDeltaTime)
    {
        if (!PowerPaddlingPhysicsContexts.TryGetValue(
                ship,
                out PowerPaddlingPhysicsContext context)
            || context.Applied
            || context.Bonus <= 0f
            || fixedDeltaTime <= 0f
            || !IsPowerPaddlingGear(ship)
            || ship.m_nview == null
            || !ship.m_nview.IsValid()
            || !ship.m_nview.IsOwner()
            || ship.m_body == null)
        {
            return;
        }

        context.Applied = true;
        Transform shipTransform = ship.transform;
        float direction = ship.m_speed == Ship.Speed.Back ? -1f : 1f;
        Vector3 paddleForce =
            shipTransform.forward
            * (context.BackwardForce * (1f - Mathf.Abs(ship.m_rudderValue)));
        paddleForce +=
            shipTransform.right
            * (context.SteeringForce * -ship.m_rudderValue);
        paddleForce *= direction * context.Bonus;

        Vector3 forcePoint =
            shipTransform.position
            + shipTransform.forward * ship.m_stearForceOffset;
        ship.m_body.AddForceAtPosition(
            paddleForce * (ship.m_body.mass * fixedDeltaTime),
            forcePoint,
            ForceMode.Impulse);
    }

    internal static void RegisterPowerPaddlingRpc(Ship ship)
    {
        if (ship == null || ship.m_nview == null)
        {
            return;
        }

        PowerPaddlingRequests.Remove(ship);
        ship.m_nview.Register<bool>(
            PowerPaddlingRpc,
            (sender, requested) => HandlePowerPaddlingRequest(ship, sender, requested));
    }

    internal static void UpdatePowerPaddlingInput(PlayerController input)
    {
        if (input == null || input.m_character != Player.m_localPlayer)
        {
            return;
        }

        Player player = input.m_character;
        Ship? ship = null;
        bool requested = PowerPaddlingEnabled
                         && TryGetLocalPowerPaddlingShip(player, out ship)
                         && ship != null
                         && IsPowerPaddlingGear(ship)
                         && !PlayerController.HasInputDelay
                         && input.TakeInput()
                         && input.m_runPressedWhileStamina
                         && (ZInput.GetButton("Run") || ZInput.GetButton("JoyRun"))
                         && player.HaveStamina();

        if (requested)
        {
            player.UseStamina(
                PowerPaddlingStaminaPerSecond * Time.fixedDeltaTime * Game.m_moveStaminaRate);
        }

        SetLocalPowerPaddlingRequest(ship, requested);
    }

    internal static void StopLocalPowerPaddling(Player player)
    {
        if (player == Player.m_localPlayer)
        {
            SetLocalPowerPaddlingRequest(null, requested: false);
        }
    }

    internal static void RemovePowerPaddlingState(Ship ship)
    {
        PowerPaddlingRequests.Remove(ship);
        PowerPaddlingPhysicsContexts.Remove(ship);
        if (_localPowerPaddlingShip == ship)
        {
            SetLocalPowerPaddlingRequest(null, requested: false);
            _powerPaddlingFovOffset = 0f;
        }
    }

    internal static void ApplyPowerPaddlingFov(GameCamera camera, float deltaTime)
    {
        bool active = IsLocalPowerPaddlingActive();
        if (!active && _localPowerPaddlingRequested)
        {
            SetLocalPowerPaddlingRequest(null, requested: false);
        }

        float target = active ? PowerPaddlingFovIncrease : 0f;
        float transitionSeconds = active ? PowerPaddlingFovRiseSeconds : PowerPaddlingFovFallSeconds;
        float maxDelta = PowerPaddlingFovIncrease * Mathf.Max(0f, deltaTime) / transitionSeconds;
        _powerPaddlingFovOffset = Mathf.MoveTowards(_powerPaddlingFovOffset, target, maxDelta);

        if (camera == null
            || camera.m_freeFly
            || _powerPaddlingFovOffset <= 0f)
        {
            return;
        }

        if (camera.m_camera != null)
        {
            camera.m_camera.fieldOfView =
                Mathf.Clamp(camera.m_fov + _powerPaddlingFovOffset, 0.5f, 165f);
        }

        if (camera.m_skyCamera != null)
        {
            camera.m_skyCamera.fieldOfView =
                Mathf.Clamp(camera.m_fov + _powerPaddlingFovOffset, 0.5f, 165f);
        }
    }

    internal static void Shutdown()
    {
        SetLocalPowerPaddlingRequest(null, requested: false);
        PowerPaddlingRequests.Clear();
        PowerPaddlingPhysicsContexts.Clear();
        InvalidPowerPaddlingSenders.Clear();
        _powerPaddlingFovOffset = 0f;
    }

    private static bool PowerPaddlingEnabled =>
        _powerPaddlingBonusPerPlayer != null
        && GetFiniteValue(_powerPaddlingBonusPerPlayer, 0.5f) > 0f;

    private static void HandlePowerPaddlingConfigChanged()
    {
        if (PowerPaddlingEnabled)
        {
            return;
        }

        ClearPowerPaddlingState();
    }

    private static void ClearPowerPaddlingState()
    {
        SetLocalPowerPaddlingRequest(null, requested: false);
        PowerPaddlingRequests.Clear();
        PowerPaddlingPhysicsContexts.Clear();
    }

    private static void HandlePowerPaddlingRequest(Ship ship, long sender, bool requested)
    {
        if (ship == null
            || ship.m_nview == null
            || !ship.m_nview.IsValid()
            || !ship.m_nview.IsOwner())
        {
            return;
        }

        if (!requested)
        {
            RemovePowerPaddlingRequest(ship, sender);
            return;
        }

        if (!PowerPaddlingEnabled || !ShouldAffect(ship))
        {
            PowerPaddlingRequests.Remove(ship);
            return;
        }

        if (sender == 0L
            || !IsPowerPaddlingGear(ship)
            || !ship.HaveControllingPlayer()
            || !TryFindUniqueAboardPlayer(ship, sender, out Player? player)
            || player == null)
        {
            RemovePowerPaddlingRequest(ship, sender);
            return;
        }

        if (!PowerPaddlingRequests.TryGetValue(
                ship,
                out Dictionary<long, PowerPaddlingRequest> requests))
        {
            requests = new Dictionary<long, PowerPaddlingRequest>();
            PowerPaddlingRequests.Add(ship, requests);
        }

        if (!requests.TryGetValue(sender, out PowerPaddlingRequest request))
        {
            request = new PowerPaddlingRequest();
            requests.Add(sender, request);
        }

        request.PlayerId = player.GetPlayerID();
        request.LastHeartbeat = Time.unscaledTime;
    }

    private static int CountActivePowerPaddlers(Ship ship)
    {
        if (!PowerPaddlingEnabled
            || !IsPowerPaddlingGear(ship)
            || !ship.HaveControllingPlayer())
        {
            PowerPaddlingRequests.Remove(ship);
            return 0;
        }

        if (!PowerPaddlingRequests.TryGetValue(
                ship,
                out Dictionary<long, PowerPaddlingRequest> requests))
        {
            return 0;
        }

        InvalidPowerPaddlingSenders.Clear();
        int activeCount = 0;
        float now = Time.unscaledTime;
        foreach (KeyValuePair<long, PowerPaddlingRequest> entry in requests)
        {
            bool valid = now - entry.Value.LastHeartbeat <= PowerPaddlingHeartbeatTimeout
                         && TryFindUniqueAboardPlayer(ship, entry.Key, out Player? player)
                         && player != null
                         && player.GetPlayerID() == entry.Value.PlayerId;
            if (valid)
            {
                ++activeCount;
            }
            else
            {
                InvalidPowerPaddlingSenders.Add(entry.Key);
            }
        }

        for (int i = 0; i < InvalidPowerPaddlingSenders.Count; ++i)
        {
            requests.Remove(InvalidPowerPaddlingSenders[i]);
        }

        InvalidPowerPaddlingSenders.Clear();
        if (requests.Count == 0)
        {
            PowerPaddlingRequests.Remove(ship);
        }

        return activeCount;
    }

    private static bool IsLocalPowerPaddlingActive()
    {
        Player? player = Player.m_localPlayer;
        return _localPowerPaddlingRequested
               && _localPowerPaddlingShip != null
               && player != null
               && PowerPaddlingEnabled
               && TryGetLocalPowerPaddlingShip(player, out Ship? ship)
               && ship == _localPowerPaddlingShip
               && IsPowerPaddlingGear(_localPowerPaddlingShip);
    }

    private static bool IsPowerPaddlingGear(Ship ship)
    {
        return ship.m_speed == Ship.Speed.Back
               || ship.m_speed == Ship.Speed.Slow;
    }

    private static void SetLocalPowerPaddlingRequest(Ship? ship, bool requested)
    {
        if (_localPowerPaddlingShip != null
            && (_localPowerPaddlingShip != ship || !requested)
            && _localPowerPaddlingRequested)
        {
            SendPowerPaddlingRequest(_localPowerPaddlingShip, requested: false);
        }

        if (!requested || ship == null)
        {
            _localPowerPaddlingShip = null;
            _localPowerPaddlingRequested = false;
            _nextPowerPaddlingHeartbeat = 0f;
            return;
        }

        bool changedShip = _localPowerPaddlingShip != ship;
        bool shouldSend = changedShip
                          || !_localPowerPaddlingRequested
                          || Time.unscaledTime >= _nextPowerPaddlingHeartbeat;

        _localPowerPaddlingShip = ship;
        _localPowerPaddlingRequested = true;
        if (shouldSend)
        {
            SendPowerPaddlingRequest(ship, requested: true);
            _nextPowerPaddlingHeartbeat = Time.unscaledTime + PowerPaddlingHeartbeatInterval;
        }
    }

    private static void SendPowerPaddlingRequest(Ship ship, bool requested)
    {
        if (ship != null
            && ship.m_nview != null
            && ship.m_nview.IsValid()
            && ZRoutedRpc.instance != null)
        {
            ship.m_nview.InvokeRPC(PowerPaddlingRpc, requested);
        }
    }

    private static void RemovePowerPaddlingRequest(Ship ship, long sender)
    {
        if (!PowerPaddlingRequests.TryGetValue(
                ship,
                out Dictionary<long, PowerPaddlingRequest> requests))
        {
            return;
        }

        requests.Remove(sender);
        if (requests.Count == 0)
        {
            PowerPaddlingRequests.Remove(ship);
        }
    }

    private static bool TryFindUniqueAboardPlayer(Ship ship, long owner, out Player? player)
    {
        player = null;
        for (int i = 0; i < ship.m_players.Count; ++i)
        {
            Player candidate = ship.m_players[i];
            if (candidate == null || candidate.GetOwner() != owner)
            {
                continue;
            }

            if (player != null
                && player != candidate
                && player.GetPlayerID() != candidate.GetPlayerID())
            {
                player = null;
                return false;
            }

            player = candidate;
        }

        return player != null;
    }

    private static bool TryGetLocalPowerPaddlingShip(Player player, out Ship? ship)
    {
        ship = player.GetControlledShip();
        if (ship == null)
        {
            if (!player.IsAttachedToShip())
            {
                return false;
            }

            Transform? attachPoint = player.GetAttachPoint();
            if (attachPoint != null)
            {
                ship = attachPoint.GetComponentInParent<Ship>();
            }

            if (ship == null && !TryFindSingleLocalShip(player, out ship))
            {
                return false;
            }
        }

        return ship != null
               && ship.IsPlayerInBoat(player)
               && ship.HaveControllingPlayer()
               && ShouldAffect(ship);
    }

    private static bool TryFindSingleLocalShip(Player player, out Ship? ship)
    {
        ship = null;
        for (int i = 0; i < Ship.s_currentShips.Count; ++i)
        {
            Ship candidate = Ship.s_currentShips[i];
            if (candidate == null || !candidate.IsPlayerInBoat(player))
            {
                continue;
            }

            if (ship != null && ship != candidate)
            {
                ship = null;
                return false;
            }

            ship = candidate;
        }

        return ship != null;
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
        return ship != null && !CheckForRockTheBoat();
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
                "RockTheBoat is installed. BottleShips minimap, handling, and Power Paddling tweaks are disabled to prevent duplicate ship patches.");
        }

        return _rockTheBoatInstalled;
    }

    private static float GetFiniteValue(ConfigEntry<float> entry, float fallback)
    {
        float value = entry.Value;
        return float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;
    }

    private sealed class PowerPaddlingRequest
    {
        internal long PlayerId;
        internal float LastHeartbeat;
    }

    private sealed class PowerPaddlingPhysicsContext
    {
        internal readonly float Bonus;
        internal readonly float BackwardForce;
        internal readonly float SteeringForce;
        internal bool Applied;

        internal PowerPaddlingPhysicsContext(
            float bonus,
            float backwardForce,
            float steeringForce)
        {
            Bonus = bonus;
            BackwardForce = backwardForce;
            SteeringForce = steeringForce;
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
        internal readonly float SteeringForce;

        internal ShipHandlingState(
            float sailForceFactor,
            float backwardForce,
            float steeringForce)
        {
            Active = true;
            SailForceFactor = sailForceFactor;
            BackwardForce = backwardForce;
            SteeringForce = steeringForce;
        }
    }
}

[HarmonyPatch(typeof(GameCamera), nameof(GameCamera.UpdateCamera), typeof(float))]
internal static class BottleShipsGameCameraUpdateCameraPowerPaddlingPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(GameCamera __instance, float dt)
    {
        ShipTweaksManager.ApplyPowerPaddlingFov(__instance, dt);
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

[HarmonyPatch(typeof(Ship), nameof(Ship.ApplyEdgeForce), typeof(float))]
internal static class BottleShipsShipApplyEdgeForcePowerPaddlingPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Prefix(Ship __instance, float dt)
    {
        ShipTweaksManager.ApplyPowerPaddlingForce(__instance, dt);
    }
}

[HarmonyPatch(typeof(Ship), nameof(Ship.Start))]
internal static class BottleShipsShipStartPowerPaddlingPatch
{
    private static void Postfix(Ship __instance)
    {
        ShipTweaksManager.RegisterPowerPaddlingRpc(__instance);
    }
}

[HarmonyPatch(typeof(Ship), nameof(Ship.OnDisable))]
internal static class BottleShipsShipOnDisablePowerPaddlingPatch
{
    private static void Postfix(Ship __instance)
    {
        ShipTweaksManager.RemovePowerPaddlingState(__instance);
    }
}

[HarmonyPatch(typeof(PlayerController), nameof(PlayerController.FixedUpdate))]
internal static class BottleShipsPlayerControllerFixedUpdatePowerPaddlingPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(PlayerController __instance)
    {
        ShipTweaksManager.UpdatePowerPaddlingInput(__instance);
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.StopDoodadControl))]
internal static class BottleShipsPlayerStopDoodadControlPowerPaddlingPatch
{
    private static void Prefix(Player __instance)
    {
        ShipTweaksManager.StopLocalPowerPaddling(__instance);
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.AttachStop))]
internal static class BottleShipsPlayerAttachStopPowerPaddlingPatch
{
    private static void Prefix(Player __instance)
    {
        ShipTweaksManager.StopLocalPowerPaddling(__instance);
    }
}

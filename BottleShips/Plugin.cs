using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LocalizationManager;
using ServerSync;
using UnityEngine;

namespace BottleShips
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    public class BottleShipsPlugin : BaseUnityPlugin
    {
        internal const string ModName = "BottleShips";
        internal const string ModVersion = "1.1.8";
        internal const string Author = "sighsorry";
        private const string ModGUID = Author + "." + ModName;
        private static string ConfigFileName = ModGUID + ".cfg";
        private static string ConfigFileFullPath = Paths.ConfigPath + Path.DirectorySeparatorChar + ConfigFileName;
        internal const float QuickCartDistance = 4f;
        private const float QuickCartAttachWindowSeconds = 1.5f;

        private readonly Harmony _harmony = new(ModGUID);
        private FileSystemWatcher? _watcher;
        internal static BottleShipsPlugin Instance = null!;
        private static Vagon? _quickCartAttachWindowTarget;
        private static float _quickCartAttachWindowUntil = float.NegativeInfinity;

        public static readonly ManualLogSource BottleShipsLogger =
            BepInEx.Logging.Logger.CreateLogSource(ModName);

        private static readonly ConfigSync ConfigSync = new(ModGUID)
        {
            DisplayName = ModName,
            CurrentVersion = ModVersion,
            MinimumRequiredVersion = ModVersion,
            ModRequired = true
        };

        public enum Toggle
        {
            On = 1,
            Off = 0
        }

        public void Awake()
        {
            Instance = this;
            Localizer.Load(this);
            
            _serverConfigLocked = config("01 - General", "Lock Configuration", Toggle.On, "If on, the configuration is locked and can be changed by server admins only.", order: 1000);
            _ = ConfigSync.AddLockingConfigEntry(_serverConfigLocked);
            _extendedCartInteraction = config(
                "01 - General",
                "Extended Cart Interaction",
                Toggle.On,
                "Client-only. If on, Vagon-based vehicles such as carts, battering rams, and catapults can use their normal attach/detach interaction from a wider nearby area without adding a separate tooltip.",
                synchronizedSetting: false,
                order: 990);
            _trollTrapAutoReloadSeconds = config(
                "01 - General",
                "Troll Trap Auto Reload Seconds",
                5,
                new ConfigDescription(
                    "Seconds after triggering before piece_trap_troll automatically rearms. 0 disables auto reload.",
                    new AcceptableValueRange<int>(0, 10)),
                order: 980);
            _ballistaTargetingTweaks = config(
                "01 - General",
                "Ballista Targeting Tweaks",
                Toggle.On,
                "If on, ballista trophies prioritize related targets instead of excluding other targets, and players, tamed creatures, and PlayerSpawned-faction creatures are never selected as targets.",
                order: 970);

            foreach (string bottleItem in BottleShipsManager.BottleItemNames)
            {
                BottleAssetManager.RegisterBottleItem("bottleasset", bottleItem);
            }

            ShipTweaksManager.BindConfig(this);
            ShipRepairManager.BindConfig(this);
            BottleShipsManager.BindConfig(this);

            Assembly assembly = Assembly.GetExecutingAssembly();
            _harmony.PatchAll(assembly);
            BottleShipsManager.ApplyDefaultsOrRetry();
            SetupWatcher();
        }

        private void OnDestroy()
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Changed -= ReadConfigValues;
                _watcher.Created -= ReadConfigValues;
                _watcher.Renamed -= ReadConfigValues;
                _watcher.Dispose();
                _watcher = null;
            }

            ShipTweaksManager.Shutdown();
            ShipRepairManager.Shutdown();
            _harmony.UnpatchSelf();
            _quickCartAttachWindowTarget = null;
            _quickCartAttachWindowUntil = float.NegativeInfinity;
            Config.Save();
        }

        private void SetupWatcher()
        {
            _watcher = new FileSystemWatcher(Paths.ConfigPath, ConfigFileName);
            _watcher.Changed += ReadConfigValues;
            _watcher.Created += ReadConfigValues;
            _watcher.Renamed += ReadConfigValues;
            _watcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
            _watcher.EnableRaisingEvents = true;
        }

        private void ReadConfigValues(object sender, FileSystemEventArgs e)
        {
            if (!File.Exists(ConfigFileFullPath)) return;
            try
            {
                BottleShipsLogger.LogDebug("ReadConfigValues called");
                Config.Reload();
            }
            catch (Exception exception)
            {
                BottleShipsLogger.LogError($"There was an issue loading your {ConfigFileName}");
                BottleShipsLogger.LogError("Please check your config entries for spelling and format!");
                BottleShipsLogger.LogError(exception);
            }
        }


        #region ConfigOptions

        private static ConfigEntry<Toggle> _serverConfigLocked = null!;
        private static ConfigEntry<Toggle> _extendedCartInteraction = null!;
        private static ConfigEntry<int> _trollTrapAutoReloadSeconds = null!;
        private static ConfigEntry<Toggle> _ballistaTargetingTweaks = null!;

        internal static bool ExtendedCartInteractionEnabled =>
            _extendedCartInteraction != null && _extendedCartInteraction.Value == Toggle.On;

        internal static bool BallistaTargetingTweaksEnabled =>
            _ballistaTargetingTweaks != null && _ballistaTargetingTweaks.Value == Toggle.On;

        internal static bool IsProtectedBallistaTarget(Character? character)
        {
            return character != null &&
                   (character.IsPlayer() ||
                    character.IsTamed() ||
                    character.GetFaction() == Character.Faction.PlayerSpawned);
        }

        internal static void RejectProtectedBallistaTarget(ref ZDOID character)
        {
            if (!BallistaTargetingTweaksEnabled || character == ZDOID.None || ZNetScene.instance == null)
            {
                return;
            }

            GameObject targetObject = ZNetScene.instance.FindInstance(character);
            Character? target = targetObject != null
                ? targetObject.GetComponent<Character>()
                : null;
            if (IsProtectedBallistaTarget(target))
            {
                character = ZDOID.None;
            }
        }

        internal static void TryAutoReloadTrollTrap(Trap trap)
        {
            if (_trollTrapAutoReloadSeconds == null
                || _trollTrapAutoReloadSeconds.Value <= 0
                || trap == null
                || trap.m_nview == null
                || !trap.m_nview.IsValid())
            {
                return;
            }

            if (!IsTrollTrap(trap))
            {
                return;
            }

            if (!trap.m_nview.IsOwner())
            {
                return;
            }

            if (ZNet.instance == null)
            {
                return;
            }

            if (trap.IsArmed())
            {
                return;
            }

            double triggeredAt = trap.m_nview.GetZDO().GetFloat(ZDOVars.s_triggered);
            if (triggeredAt <= 0)
            {
                return;
            }

            double now = ZNet.instance.GetTimeSeconds();
            double dueAt = triggeredAt + _trollTrapAutoReloadSeconds.Value;
            if (dueAt > now)
            {
                return;
            }

            trap.RequestStateChange(Trap.TrapState.Armed);
        }

        private static bool IsTrollTrap(Trap trap)
        {
            return trap.m_nview.GetPrefabName() == "piece_trap_troll"
                   || Utils.GetPrefabName(trap.gameObject) == "piece_trap_troll";
        }

        internal static bool TryApplyExtendedCartDistance(Vagon vagon, out QuickCartDistanceState state)
        {
            state = default;
            Player player = Player.m_localPlayer;
            if (!ExtendedCartInteractionEnabled
                || player == null
                || vagon == null
                || vagon.m_nview == null
                || !vagon.m_nview.IsValid()
                || !vagon.m_nview.IsOwner())
            {
                return false;
            }

            bool requestPending = vagon.m_useRequester == player;
            bool settling = _quickCartAttachWindowTarget == vagon
                            && Time.time <= _quickCartAttachWindowUntil
                            && vagon.IsAttached(player);
            if (!requestPending && !settling)
            {
                if (_quickCartAttachWindowTarget == vagon && Time.time > _quickCartAttachWindowUntil)
                {
                    _quickCartAttachWindowTarget = null;
                }

                return false;
            }

            if (requestPending &&
                (!IsQuickCartCandidate(vagon, player, out float distance) || distance > QuickCartDistance))
            {
                return false;
            }

            if (requestPending)
            {
                _quickCartAttachWindowTarget = vagon;
                _quickCartAttachWindowUntil = Time.time + QuickCartAttachWindowSeconds;
            }

            state = new QuickCartDistanceState(vagon.m_detachDistance);
            vagon.m_detachDistance = Mathf.Max(vagon.m_detachDistance, QuickCartDistance);
            return true;
        }

        internal static void RestoreExtendedCartDistance(Vagon vagon, ref QuickCartDistanceState state)
        {
            if (!state.Active)
            {
                return;
            }

            vagon.m_detachDistance = state.DetachDistance;
            state.Active = false;
        }

        internal static bool PlayerCanQuickCart(Player player)
        {
            return player != null
                   && !player.IsDead()
                   && !player.IsTeleporting()
                   && !player.InDodge()
                   && !player.InPlaceMode();
        }

        internal static bool IsUiBlockingQuickCart()
        {
            return InventoryGui.IsVisible()
                   || Menu.IsVisible()
                   || Minimap.IsOpen()
                   || TextInput.IsVisible()
                   || global::Console.IsVisible()
                   || StoreGui.IsVisible()
                   || Hud.IsPieceSelectionVisible()
                   || Hud.InRadial()
                   || Chat.instance != null && Chat.instance.HasFocus()
                   || TextViewer.instance != null && TextViewer.instance.IsVisible();
        }

        internal static bool IsQuickCartCandidate(Vagon vagon, Player player, out float distance)
        {
            distance = float.PositiveInfinity;
            if (vagon == null
                || !vagon.isActiveAndEnabled
                || vagon.m_nview == null
                || !vagon.m_nview.IsValid()
                || vagon.m_attachPoint == null
                || vagon.transform.up.y < 0.1f)
            {
                return false;
            }

            if (!vagon.IsAttached(player) && vagon.InUse())
            {
                return false;
            }

            distance = Vector3.Distance(player.transform.position + vagon.m_attachOffset, vagon.m_attachPoint.position);
            return true;
        }

        internal static void HandleExtendedCartUse(Player player, bool hadDoodadController)
        {
            if (!ExtendedCartInteractionEnabled || hadDoodadController || player == null)
            {
                return;
            }

            if (!(ZInput.GetButtonDown("Use") || ZInput.GetButtonDown("JoyUse")))
            {
                return;
            }

            if (player.m_hovering != null || player.m_doodadController != null)
            {
                return;
            }

            if (!PlayerCanQuickCart(player) || IsUiBlockingQuickCart())
            {
                return;
            }

            if (TryGetNearbyQuickCart(player, out Vagon? vagon) && vagon != null)
            {
                vagon.Interact(player, hold: false, alt: false);
            }
        }

        private static bool TryGetNearbyQuickCart(Player player, out Vagon? closestVagon)
        {
            closestVagon = null;
            float closestDistance = float.PositiveInfinity;

            foreach (Vagon vagon in Vagon.m_instances)
            {
                if (vagon == null || !IsQuickCartCandidate(vagon, player, out float distance))
                {
                    continue;
                }

                if (vagon.IsAttached(player))
                {
                    closestVagon = vagon;
                    return true;
                }

                if (distance <= QuickCartDistance && distance < closestDistance)
                {
                    closestDistance = distance;
                    closestVagon = vagon;
                }
            }

            return closestVagon != null;
        }

        internal ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description,
            bool synchronizedSetting = true, int? order = null)
        {
            object[] tags = description.Tags ?? Array.Empty<object>();
            object[] orderedTags = new object[tags.Length + 1];
            Array.Copy(tags, orderedTags, tags.Length);
            string category = GetConfigurationManagerCategory(group);
            orderedTags[tags.Length] = new ConfigurationManagerAttributes
            {
                Category = category,
                CategoryOrder = GetConfigurationManagerCategoryOrder(category),
                Order = order
            };
            tags = orderedTags;

            ConfigDescription extendedDescription =
                new(
                    description.Description +
                    (synchronizedSetting ? " [Synced with Server]" : " [Not Synced with Server]"),
                    description.AcceptableValues, tags);
            ConfigEntry<T> configEntry = Config.Bind(group, name, value, extendedDescription);

            SyncedConfigEntry<T> syncedConfigEntry = ConfigSync.AddConfigEntry(configEntry);
            syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

            return configEntry;
        }

        internal ConfigEntry<T> config<T>(string group, string name, T value, string description,
            bool synchronizedSetting = true, int? order = null)
        {
            return config(group, name, value, new ConfigDescription(description), synchronizedSetting, order);
        }

        private static string GetConfigurationManagerCategory(string group)
        {
            if (string.Equals(group, "20 - Ship Tweaks", StringComparison.Ordinal))
            {
                return "02 - Ship Tweaks";
            }

            if (group.Length >= 2
                && int.TryParse(group.Substring(0, 2), out int section)
                && section >= 2
                && section <= 12)
            {
                return $"{section + 1:00}{group.Substring(2)}";
            }

            return group;
        }

        private static int? GetConfigurationManagerCategoryOrder(string category)
        {
            return category.Length >= 2
                   && int.TryParse(category.Substring(0, 2), out int section)
                ? 1000 - section
                : null;
        }

        private sealed class ConfigurationManagerAttributes
        {
            public string? Category { get; set; }
            public int? CategoryOrder { get; set; }
            public int? Order { get; set; }
        }

        internal struct QuickCartDistanceState
        {
            internal bool Active;
            internal readonly float DetachDistance;

            internal QuickCartDistanceState(float detachDistance)
            {
                Active = true;
                DetachDistance = detachDistance;
            }
        }

        #endregion
    }

    [HarmonyPatch(typeof(Player), nameof(Player.Update))]
    internal static class BottleShipsPlayerUpdateExtendedCartPatch
    {
        private static void Prefix(Player __instance, out bool __state)
        {
            __state = __instance == Player.m_localPlayer && __instance.m_doodadController != null;
        }

        private static void Postfix(Player __instance, bool __state)
        {
            if (__instance == Player.m_localPlayer)
            {
                BottleShipsPlugin.HandleExtendedCartUse(__instance, __state);
            }
        }
    }

    [HarmonyPatch(typeof(Vagon), "FixedUpdate")]
    internal static class BottleShipsVagonFixedUpdatePatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Prefix(
            Vagon __instance,
            out BottleShipsPlugin.QuickCartDistanceState __state)
        {
            BottleShipsPlugin.TryApplyExtendedCartDistance(__instance, out __state);
        }

        [HarmonyPriority(Priority.First)]
        private static void Postfix(
            Vagon __instance,
            ref BottleShipsPlugin.QuickCartDistanceState __state)
        {
            BottleShipsPlugin.RestoreExtendedCartDistance(__instance, ref __state);
        }

        [HarmonyPriority(Priority.First)]
        private static Exception? Finalizer(
            Vagon __instance,
            Exception? __exception,
            ref BottleShipsPlugin.QuickCartDistanceState __state)
        {
            BottleShipsPlugin.RestoreExtendedCartDistance(__instance, ref __state);
            return __exception;
        }
    }

    [HarmonyPatch(typeof(Trap), nameof(Trap.Update))]
    internal static class BottleShipsTrapUpdatePatch
    {
        private static void Postfix(Trap __instance)
        {
            BottleShipsPlugin.TryAutoReloadTrollTrap(__instance);
        }
    }

    [HarmonyPatch(typeof(Turret), nameof(Turret.UpdateTarget))]
    internal static class BottleShipsTurretUpdateTargetPatch
    {
        [ThreadStatic]
        private static int _selectionDepth;

        internal static bool IsSelectingTarget => _selectionDepth > 0;

        [HarmonyPriority(Priority.Last)]
        private static void Prefix(Turret __instance, out TargetingState __state)
        {
            __state = default;
            if (!BottleShipsPlugin.BallistaTargetingTweaksEnabled)
            {
                return;
            }

            __state = new TargetingState(__instance.m_targetPlayers);
            __instance.m_targetPlayers = false;
            ++_selectionDepth;
        }

        [HarmonyPriority(Priority.First)]
        private static void Postfix(Turret __instance, ref TargetingState __state)
        {
            try
            {
                if (__state.Active &&
                    __instance.m_haveTarget &&
                    BottleShipsPlugin.IsProtectedBallistaTarget(__instance.m_target) &&
                    __instance.m_nview != null &&
                    __instance.m_nview.IsValid())
                {
                    __instance.m_nview.InvokeRPC(ZNetView.Everybody, "RPC_SetTarget", ZDOID.None);
                    __instance.m_lostTargetEffect.Create(__instance.transform.position, __instance.transform.rotation);
                }
            }
            finally
            {
                Restore(__instance, ref __state);
            }
        }

        [HarmonyPriority(Priority.First)]
        private static Exception? Finalizer(
            Turret __instance,
            Exception? __exception,
            ref TargetingState __state)
        {
            Restore(__instance, ref __state);
            return __exception;
        }

        private static void Restore(Turret turret, ref TargetingState state)
        {
            if (!state.Active)
            {
                return;
            }

            turret.m_targetPlayers = state.TargetPlayers;
            if (_selectionDepth > 0)
            {
                --_selectionDepth;
            }

            state.Active = false;
        }

        private struct TargetingState
        {
            internal bool Active;
            internal readonly bool TargetPlayers;

            internal TargetingState(bool targetPlayers)
            {
                Active = true;
                TargetPlayers = targetPlayers;
            }
        }
    }

    [HarmonyPatch(typeof(BaseAI), nameof(BaseAI.FindClosestCreature))]
    internal static class BottleShipsBallistaFindClosestCreaturePatch
    {
        private static void Postfix(
            Transform me,
            Vector3 eyePoint,
            float hearRange,
            float viewRange,
            float viewAngle,
            bool alerted,
            bool mistVision,
            bool passiveAggresive,
            bool includePlayers,
            bool includeTamed,
            bool includeEnemies,
            List<Character> onlyTargets,
            ref Character? __result)
        {
            if (!BottleShipsTurretUpdateTargetPatch.IsSelectingTarget ||
                !BottleShipsPlugin.BallistaTargetingTweaksEnabled)
            {
                return;
            }

            if (BottleShipsPlugin.IsProtectedBallistaTarget(__result))
            {
                __result = FindClosestSafeCreature(
                    me,
                    eyePoint,
                    hearRange,
                    viewRange,
                    viewAngle,
                    alerted,
                    mistVision,
                    passiveAggresive,
                    includePlayers,
                    includeTamed,
                    includeEnemies,
                    onlyTargets);
            }

            if (__result == null &&
                includeEnemies &&
                onlyTargets != null &&
                onlyTargets.Count > 0)
            {
                __result = FindClosestSafeCreature(
                    me,
                    eyePoint,
                    hearRange,
                    viewRange,
                    viewAngle,
                    alerted,
                    mistVision,
                    passiveAggresive,
                    includePlayers: false,
                    includeTamed: false,
                    includeEnemies: true,
                    onlyTargets: null);
            }
        }

        private static Character? FindClosestSafeCreature(
            Transform me,
            Vector3 eyePoint,
            float hearRange,
            float viewRange,
            float viewAngle,
            bool alerted,
            bool mistVision,
            bool passiveAggresive,
            bool includePlayers,
            bool includeTamed,
            bool includeEnemies,
            List<Character>? onlyTargets)
        {
            if (!includeEnemies &&
                ZoneSystem.instance.GetGlobalKey(GlobalKeys.PassiveMobs))
            {
                WearNTear? wear = me.GetComponent<WearNTear>();
                if (wear != null && wear.GetHealthPercentage() == 1f)
                {
                    return null;
                }
            }

            Character? closest = null;
            float closestDistance = 99999f;
            foreach (Character candidate in Character.GetAllCharacters())
            {
                bool isPlayer = candidate is Player;
                if ((!includePlayers && isPlayer) ||
                    (!includeEnemies && !isPlayer) ||
                    (!includeTamed && candidate.IsTamed()) ||
                    BottleShipsPlugin.IsProtectedBallistaTarget(candidate))
                {
                    continue;
                }

                if (onlyTargets != null && onlyTargets.Count > 0)
                {
                    bool configuredTarget = false;
                    foreach (Character allowedTarget in onlyTargets)
                    {
                        if (candidate.m_name == allowedTarget.m_name)
                        {
                            configuredTarget = true;
                            break;
                        }
                    }

                    if (!configuredTarget)
                    {
                        continue;
                    }
                }

                if (candidate.IsDead())
                {
                    continue;
                }

                BaseAI? candidateAi = candidate.GetBaseAI();
                if ((candidateAi != null && candidateAi.IsSleeping()) ||
                    !BaseAI.CanSenseTarget(
                        me,
                        eyePoint,
                        hearRange,
                        viewRange,
                        viewAngle,
                        alerted,
                        mistVision,
                        candidate,
                        passiveAggresive,
                        isTamed: false))
                {
                    continue;
                }

                float distance = Vector3.Distance(candidate.transform.position, me.position);
                if (distance < closestDistance || closest == null)
                {
                    closest = candidate;
                    closestDistance = distance;
                }
            }

            return closest;
        }
    }

    [HarmonyPatch(typeof(Turret), nameof(Turret.RPC_SetTarget))]
    internal static class BottleShipsTurretRpcSetTargetPatch
    {
        private static void Prefix(ref ZDOID character)
        {
            BottleShipsPlugin.RejectProtectedBallistaTarget(ref character);
        }
    }

    [HarmonyPatch(typeof(Turret), nameof(Turret.RPC_AddAmmo))]
    internal static class BottleShipsTurretRpcAddAmmoCapacityPatch
    {
        private static bool Prefix(Turret __instance)
        {
            return BottleShipsManager.CanReceiveConfiguredBallistaAmmo(__instance);
        }
    }

    [HarmonyPatch(typeof(Turret), nameof(Turret.OnDestroyed))]
    internal static class BottleShipsTurretOnDestroyedAmmoPatch
    {
        private static bool Prefix(Turret __instance)
        {
            return !BottleShipsManager.TryDropStackedBallistaAmmo(__instance);
        }
    }
}

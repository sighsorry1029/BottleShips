using System;
using System.Collections;
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
        internal const string ModVersion = "1.1.2";
        internal const string Author = "sighsorry";
        private const string ModGUID = Author + "." + ModName;
        private static string ConfigFileName = ModGUID + ".cfg";
        private static string ConfigFileFullPath = Paths.ConfigPath + Path.DirectorySeparatorChar + ConfigFileName;
        internal const float QuickCartDistance = 4f;
        private const float QuickCartAttachWindowSeconds = 1.5f;

        private readonly Harmony _harmony = new(ModGUID);
        private FileSystemWatcher? _watcher;
        internal static BottleShipsPlugin Instance = null!;
        private static float _quickCartAttachWindowUntil;

        public static readonly ManualLogSource BottleShipsLogger =
            BepInEx.Logging.Logger.CreateLogSource(ModName);

        private static readonly ConfigSync ConfigSync = new(ModGUID)
            { DisplayName = ModName, CurrentVersion = ModVersion, MinimumRequiredVersion = ModVersion };

        public enum Toggle
        {
            On = 1,
            Off = 0
        }

        public void Awake()
        {
            Instance = this;
            Localizer.Load();
            
            _serverConfigLocked = config("01 - General", "Lock Configuration", Toggle.On, "If on, the configuration is locked and can be changed by server admins only.", order: 1000);
            _ = ConfigSync.AddLockingConfigEntry(_serverConfigLocked);
            _extendedCartInteraction = config(
                "01 - General",
                "Extended Cart Interaction",
                Toggle.On,
                "Client-only. If on, the cart's normal Use interaction works from a wider nearby area without adding a separate tooltip.",
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
                "If on, ballista trophies prioritize related targets instead of excluding other targets, and players are never selected as targets.",
                order: 970);

            foreach (string bottleItem in BottleShipsManager.BottleItemNames)
            {
                BottleAssetManager.RegisterBottleItem("bottleasset", bottleItem);
            }

            BottleShipsManager.BindConfig(this);

            Assembly assembly = Assembly.GetExecutingAssembly();
            _harmony.PatchAll(assembly);
            StartCoroutine(BottleShipsManager.ApplyWhenReady());
            SetupWatcher();
        }

        private void OnDestroy()
        {
            _watcher?.Dispose();
            Config.Save();
        }

        private void SetupWatcher()
        {
            _watcher = new FileSystemWatcher(Paths.ConfigPath, ConfigFileName);
            _watcher.Changed += ReadConfigValues;
            _watcher.Created += ReadConfigValues;
            _watcher.Renamed += ReadConfigValues;
            _watcher.IncludeSubdirectories = true;
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
                BottleShipsManager.ApplyIfReady();
            }
            catch
            {
                BottleShipsLogger.LogError($"There was an issue loading your {ConfigFileName}");
                BottleShipsLogger.LogError("Please check your config entries for spelling and format!");
            }
        }


        #region ConfigOptions

        private static ConfigEntry<Toggle> _serverConfigLocked = null!;
        private static ConfigEntry<Toggle> _extendedCartInteraction = null!;
        private static ConfigEntry<int> _trollTrapAutoReloadSeconds = null!;
        private static ConfigEntry<Toggle> _ballistaTargetingTweaks = null!;

        internal static bool ExtendedCartInteractionEnabled =>
            _extendedCartInteraction != null && _extendedCartInteraction.Value == Toggle.On;

        internal static bool QuickCartAttachWindowActive =>
            ExtendedCartInteractionEnabled && Time.time <= _quickCartAttachWindowUntil;

        internal static bool BallistaTargetingTweaksEnabled =>
            _ballistaTargetingTweaks != null && _ballistaTargetingTweaks.Value == Toggle.On;

        internal static void UpdateBallistaTarget(Turret turret, float dt)
        {
            if (turret == null || turret.m_nview == null || !turret.m_nview.IsValid())
            {
                return;
            }

            if (!turret.HasAmmo())
            {
                if (turret.m_haveTarget)
                {
                    turret.m_nview.InvokeRPC(ZNetView.Everybody, "RPC_SetTarget", ZDOID.None);
                }

                return;
            }

            turret.m_updateTargetTimer -= dt;
            if (turret.m_updateTargetTimer <= 0f)
            {
                turret.m_updateTargetTimer = Character.IsCharacterInRange(turret.transform.position, 40f)
                    ? turret.m_updateTargetIntervalNear
                    : turret.m_updateTargetIntervalFar;

                Character? target = FindBallistaTarget(turret);
                if (target != turret.m_target)
                {
                    if (target != null)
                    {
                        turret.m_newTargetEffect.Create(turret.transform.position, turret.transform.rotation);
                    }
                    else
                    {
                        turret.m_lostTargetEffect.Create(turret.transform.position, turret.transform.rotation);
                    }

                    turret.m_nview.InvokeRPC(ZNetView.Everybody, "RPC_SetTarget",
                        target != null ? target.GetZDOID() : ZDOID.None);
                }
            }

            if (turret.m_haveTarget && (turret.m_target == null || turret.m_target.IsDead() || turret.m_target.IsPlayer()))
            {
                turret.m_nview.InvokeRPC(ZNetView.Everybody, "RPC_SetTarget", ZDOID.None);
                turret.m_lostTargetEffect.Create(turret.transform.position, turret.transform.rotation);
            }
        }

        private static Character? FindBallistaTarget(Turret turret)
        {
            if (!turret.m_targetEnemies)
            {
                return null;
            }

            bool includeTamed = turret.m_targetItems.Count > 0 ? turret.m_targetTamedConfig : turret.m_targetTamed;
            Character? preferredTarget = null;
            Character? fallbackTarget = null;
            float preferredDistance = float.PositiveInfinity;
            float fallbackDistance = float.PositiveInfinity;

            foreach (Character candidate in Character.GetAllCharacters())
            {
                if (!IsValidBallistaTargetCandidate(turret, candidate, includeTamed))
                {
                    continue;
                }

                float distance = Vector3.Distance(candidate.transform.position, turret.transform.position);
                if (MatchesBallistaTrophyTarget(turret, candidate))
                {
                    if (distance < preferredDistance)
                    {
                        preferredDistance = distance;
                        preferredTarget = candidate;
                    }
                }
                else if (distance < fallbackDistance)
                {
                    fallbackDistance = distance;
                    fallbackTarget = candidate;
                }
            }

            return preferredTarget != null ? preferredTarget : fallbackTarget;
        }

        private static bool IsValidBallistaTargetCandidate(Turret turret, Character candidate, bool includeTamed)
        {
            if (candidate == null || candidate.IsPlayer() || candidate.IsDead() || candidate.m_aiSkipTarget)
            {
                return false;
            }

            if (!includeTamed && candidate.IsTamed())
            {
                return false;
            }

            BaseAI baseAI = candidate.GetBaseAI();
            if (baseAI != null && baseAI.IsSleeping())
            {
                return false;
            }

            return BaseAI.CanSenseTarget(
                turret.transform,
                turret.m_eye.transform.position,
                0f,
                turret.m_viewDistance,
                turret.m_horizontalAngle,
                alerted: false,
                mistVision: false,
                candidate,
                passiveAggresive: true,
                isTamed: false);
        }

        private static bool MatchesBallistaTrophyTarget(Turret turret, Character candidate)
        {
            if (turret.m_targetCharacters.Count == 0)
            {
                return false;
            }

            foreach (Character target in turret.m_targetCharacters)
            {
                if (target != null && candidate.m_name == target.m_name)
                {
                    return true;
                }
            }

            return false;
        }

        internal static void RejectBallistaPlayerTarget(ref ZDOID character)
        {
            if (!BallistaTargetingTweaksEnabled || character == ZDOID.None || ZNetScene.instance == null)
            {
                return;
            }

            GameObject targetObject = ZNetScene.instance.FindInstance(character);
            if (targetObject != null && targetObject.GetComponent<Player>() != null)
            {
                character = ZDOID.None;
            }
        }

        internal static void ScheduleTrollTrapAutoReload(Trap trap)
        {
            if (_trollTrapAutoReloadSeconds == null || _trollTrapAutoReloadSeconds.Value <= 0)
            {
                return;
            }

            if (!IsTrollTrap(trap))
            {
                return;
            }

            trap.StartCoroutine(AutoReloadTrollTrapAfterDelay(trap, _trollTrapAutoReloadSeconds.Value));
        }

        private static IEnumerator AutoReloadTrollTrapAfterDelay(Trap trap, int seconds)
        {
            yield return new WaitForSeconds(seconds);

            TryRequestTrollTrapAutoReload(trap);
        }

        internal static void TryAutoReloadTrollTrapFallback(Trap trap)
        {
            if (_trollTrapAutoReloadSeconds == null || _trollTrapAutoReloadSeconds.Value <= 0)
            {
                return;
            }

            TryRequestTrollTrapAutoReload(trap);
        }

        private static void TryRequestTrollTrapAutoReload(Trap trap)
        {
            if (trap == null || trap.m_nview == null || !trap.m_nview.IsValid())
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

            if (trap.IsCoolingDown())
            {
                return;
            }

            double triggeredAt = GetTrapTriggeredAt(trap);
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

        private static double GetTrapTriggeredAt(Trap trap)
        {
            return trap.m_nview != null && trap.m_nview.IsValid()
                ? trap.m_nview.GetZDO().GetFloat(ZDOVars.s_triggered)
                : 0;
        }

        private static bool IsTrollTrap(Trap trap)
        {
            if (trap == null || trap.m_nview == null || !trap.m_nview.IsValid())
            {
                return false;
            }

            return trap.m_nview.GetPrefabName() == "piece_trap_troll"
                   || Utils.GetPrefabName(trap.gameObject) == "piece_trap_troll";
        }

        internal static void OpenQuickCartAttachWindow()
        {
            if (ExtendedCartInteractionEnabled)
            {
                _quickCartAttachWindowUntil = Time.time + QuickCartAttachWindowSeconds;
            }
        }

        internal static bool PlayerCanQuickCart()
        {
            Player player = Player.m_localPlayer;
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
            if (vagon.m_attachPoint == null || vagon.transform.up.y < 0.1f)
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

            if (Hud.InRadial() || player.m_hovering != null || player.m_doodadController != null)
            {
                return;
            }

            if (!PlayerCanQuickCart() || IsUiBlockingQuickCart())
            {
                return;
            }

            if (TryGetNearbyQuickCart(player, out Vagon? vagon) && vagon != null)
            {
                OpenQuickCartAttachWindow();
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
            if (order != null)
            {
                object[] orderedTags = new object[tags.Length + 1];
                Array.Copy(tags, orderedTags, tags.Length);
                orderedTags[tags.Length] = new ConfigurationManagerAttributes { Order = order.Value };
                tags = orderedTags;
            }

            ConfigDescription extendedDescription =
                new(
                    description.Description +
                    (synchronizedSetting ? " [Synced with Server]" : " [Not Synced with Server]"),
                    description.AcceptableValues, tags);
            ConfigEntry<T> configEntry = Config.Bind(group, name, value, extendedDescription);
            //var configEntry = Config.Bind(group, name, value, description);

            SyncedConfigEntry<T> syncedConfigEntry = ConfigSync.AddConfigEntry(configEntry);
            syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

            return configEntry;
        }

        internal ConfigEntry<T> config<T>(string group, string name, T value, string description,
            bool synchronizedSetting = true, int? order = null)
        {
            return config(group, name, value, new ConfigDescription(description), synchronizedSetting, order);
        }

        private sealed class ConfigurationManagerAttributes
        {
            public int? Order = null;
        }

        #endregion
    }

    [HarmonyPatch(typeof(Player), nameof(Player.Update))]
    internal static class BottleShipsPlayerUpdateExtendedCartPatch
    {
        private static bool _hadDoodadController;

        private static void Prefix(Player __instance)
        {
            if (__instance == Player.m_localPlayer)
            {
                _hadDoodadController = __instance.m_doodadController != null;
            }
        }

        private static void Postfix(Player __instance)
        {
            if (__instance == Player.m_localPlayer)
            {
                BottleShipsPlugin.HandleExtendedCartUse(__instance, _hadDoodadController);
            }
        }
    }

    [HarmonyPatch(typeof(Vagon), nameof(Vagon.Interact))]
    internal static class BottleShipsVagonInteractPatch
    {
        private static void Prefix(Vagon __instance, Humanoid character, bool hold, bool alt)
        {
            if (hold || alt || !BottleShipsPlugin.ExtendedCartInteractionEnabled)
            {
                return;
            }

            Player player = Player.m_localPlayer;
            if (player == null || character != player)
            {
                return;
            }

            if (!BottleShipsPlugin.PlayerCanQuickCart() || BottleShipsPlugin.IsUiBlockingQuickCart())
            {
                return;
            }

            if (BottleShipsPlugin.IsQuickCartCandidate(__instance, player, out float distance) &&
                (distance <= BottleShipsPlugin.QuickCartDistance || __instance.IsAttached(player)))
            {
                BottleShipsPlugin.OpenQuickCartAttachWindow();
            }
        }
    }

    [HarmonyPatch(typeof(Vagon), nameof(Vagon.CanAttach))]
    internal static class BottleShipsVagonCanAttachPatch
    {
        private static bool Prefix(Vagon __instance, GameObject go, ref bool __result)
        {
            if (!BottleShipsPlugin.QuickCartAttachWindowActive)
            {
                return true;
            }

            Player player = Player.m_localPlayer;
            if (player == null || go != player.gameObject)
            {
                return true;
            }

            if (__instance.transform.up.y < 0.1f)
            {
                __result = false;
                return false;
            }

            __result = !player.IsTeleporting()
                       && !player.InDodge()
                       && __instance.m_attachPoint != null
                       && Vector3.Distance(go.transform.position + __instance.m_attachOffset,
                           __instance.m_attachPoint.position) < BottleShipsPlugin.QuickCartDistance;
            return false;
        }
    }

    [HarmonyPatch(typeof(Trap), nameof(Trap.TriggerTrap))]
    internal static class BottleShipsTrapTriggerTrapPatch
    {
        private static void Postfix(Trap __instance)
        {
            BottleShipsPlugin.ScheduleTrollTrapAutoReload(__instance);
        }
    }

    [HarmonyPatch(typeof(Trap), nameof(Trap.Update))]
    internal static class BottleShipsTrapUpdatePatch
    {
        private static void Postfix(Trap __instance)
        {
            BottleShipsPlugin.TryAutoReloadTrollTrapFallback(__instance);
        }
    }

    [HarmonyPatch(typeof(Turret), nameof(Turret.UpdateTarget))]
    internal static class BottleShipsTurretUpdateTargetPatch
    {
        private static bool Prefix(Turret __instance, float dt)
        {
            if (!BottleShipsPlugin.BallistaTargetingTweaksEnabled)
            {
                return true;
            }

            BottleShipsPlugin.UpdateBallistaTarget(__instance, dt);
            return false;
        }
    }

    [HarmonyPatch(typeof(Turret), nameof(Turret.RPC_SetTarget))]
    internal static class BottleShipsTurretRpcSetTargetPatch
    {
        private static void Prefix(ref ZDOID character)
        {
            BottleShipsPlugin.RejectBallistaPlayerTarget(ref character);
        }
    }
}

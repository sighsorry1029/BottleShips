using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BottleShips;

internal static class ShipRepairManager
{
    private const string ConfigGroup = "20 - Ship Tweaks";
    private const string WoodPrefabName = "Wood";
    private const string WorkbenchPrefabName = "piece_workbench";
    private const string RequirementWidgetName = "BottleShips_ShipRepairCost";
    private const int DefaultHealthPerWood = 200;
    private const float CostBoundaryEpsilon = 0.000001f;
    private static readonly Vector3 RequirementOffset = new(0f, -38f, 0f);

    private static readonly Dictionary<WearNTear, float> ObservedHealth = new();
    private static ConfigEntry<int> _healthPerWood = null!;

    [ThreadStatic]
    private static RepairAttempt? _activeRepair;

    private static ObjectDB? _woodObjectDb;
    private static ItemDrop? _wood;
    private static Piece.Requirement? _woodRequirement;
    private static ZNetScene? _workbenchScene;
    private static CraftingStation? _workbench;
    private static Hud? _repairHud;
    private static Hud? _widgetUnavailableHud;
    private static GameObject? _requirementWidget;
    private static bool _woodWarningLogged;
    private static bool _workbenchWarningLogged;
    private static bool _widgetWarningLogged;

    internal static void BindConfig(BottleShipsPlugin plugin)
    {
        _healthPerWood = plugin.config(
            ConfigGroup,
            "Ship Repair Durability Per Wood",
            DefaultHealthPerWood,
            new ConfigDescription(
                "0 disables field ship repair and preserves vanilla repair behavior. Otherwise, this is the missing ship health repaired per Wood when the repairing player is outside the ship's required build-station range. Range is checked from the player's position. Ships with Build Station set to None use a Workbench as their repair-station fallback. Repairs within range are free, and the ship is still repaired to full health in one action.",
                new AcceptableValueRange<int>(0, 1000)),
            order: 970);
        _healthPerWood.SettingChanged += (_, _) => HandleConfigChanged();
    }

    internal static bool BeginRepair(Player player, out RepairAttempt? state)
    {
        state = null;
        if (!TryGetFieldRepairQuote(player, player.GetHoveringPiece(), out RepairQuote quote))
        {
            return true;
        }

        Inventory inventory = player.GetInventory();
        string woodName = "";
        if (quote.WoodCost > 0)
        {
            if (quote.Wood == null)
            {
                player.Message(MessageHud.MessageType.Center, "$msg_missingrequirement");
                return false;
            }

            woodName = quote.Wood.m_itemData.m_shared.m_name;
            if (inventory.CountItems(woodName) < quote.WoodCost)
            {
                player.Message(MessageHud.MessageType.Center, "$msg_missingrequirement");
                return false;
            }
        }

        RepairAttempt attempt = new(
            player,
            quote.Piece,
            quote.WearNTear,
            inventory,
            woodName,
            quote.WoodCost,
            quote.WearNTear.m_lastRepair,
            _activeRepair);
        state = attempt;
        _activeRepair = attempt;
        return true;
    }

    internal static bool TryBypassStationCheck(Player player, Piece piece, ref bool result)
    {
        RepairAttempt? attempt = _activeRepair;
        if (attempt == null
            || !ReferenceEquals(attempt.Player, player)
            || !ReferenceEquals(attempt.Piece, piece))
        {
            return false;
        }

        result = true;
        return true;
    }

    internal static void RecordRepairResult(WearNTear wearNTear, bool repaired)
    {
        RepairAttempt? attempt = _activeRepair;
        if (attempt == null
            || attempt.RepairAccepted
            || !ReferenceEquals(attempt.WearNTear, wearNTear)
            || !repaired
            || wearNTear.m_lastRepair == attempt.LastRepairBefore)
        {
            return;
        }

        attempt.RepairAccepted = true;
    }

    internal static void RecordHealthChanged(WearNTear wearNTear, float health)
    {
        if (FieldRepairEnabled
            && wearNTear != null
            && Player.m_localPlayer != null
            && !float.IsNaN(health)
            && !float.IsInfinity(health))
        {
            ObservedHealth[wearNTear] = health;
        }
    }

    internal static void RemoveWearNTear(WearNTear wearNTear)
    {
        ObservedHealth.Remove(wearNTear);
    }

    internal static void FinishRepair(ref RepairAttempt? state)
    {
        RepairAttempt? attempt = state;
        if (attempt == null)
        {
            return;
        }

        state = null;
        if (ReferenceEquals(_activeRepair, attempt))
        {
            _activeRepair = attempt.Previous;
        }

        if (!attempt.RepairAccepted
            || attempt.WoodCost <= 0)
        {
            return;
        }

        try
        {
            attempt.Inventory.RemoveItem(attempt.WoodName, attempt.WoodCost);
        }
        catch (Exception exception)
        {
            BottleShipsPlugin.BottleShipsLogger.LogError(
                $"Failed to consume field ship-repair Wood: {exception}");
        }
    }

    internal static void InitializeHud(Hud hud)
    {
        if (hud == null || ReferenceEquals(_repairHud, hud))
        {
            return;
        }

        if (_requirementWidget != null)
        {
            UnityEngine.Object.Destroy(_requirementWidget);
        }

        _repairHud = hud;
        _widgetUnavailableHud = null;
        _requirementWidget = null;
    }

    internal static void UpdateRequirementWidget(Hud hud, Player player)
    {
        InitializeHud(hud);
        HideRequirementWidget();
        if (hud == null
            || player == null
            || !FieldRepairEnabled
            || player.GetSelectedPiece() is not { m_repairPiece: true }
            || !TryGetFieldRepairQuote(
                player,
                player.GetHoveringPiece(),
                out RepairQuote quote)
            || quote.WoodCost <= 0
            || quote.Wood == null
            || !EnsureRequirementWidget(hud))
        {
            return;
        }

        _woodRequirement ??= new Piece.Requirement();
        _woodRequirement.m_resItem = quote.Wood;
        _woodRequirement.m_amount = quote.WoodCost;
        _woodRequirement.m_amountPerLevel = 0;
        _woodRequirement.m_recover = false;

        bool shown;
        try
        {
            shown = InventoryGui.SetupRequirement(
                _requirementWidget!.transform,
                _woodRequirement,
                player,
                craft: false,
                quality: 1);
        }
        catch (Exception exception)
        {
            DisableRequirementWidget(hud, exception);
            return;
        }

        if (!shown)
        {
            return;
        }

        Transform resourceName = _requirementWidget.transform.Find("res_name");
        if (resourceName != null)
        {
            resourceName.gameObject.SetActive(false);
        }

        SynchronizeRequirementWidgetTransform(hud);
        _requirementWidget.SetActive(true);
    }

    internal static void ClearHud(Hud hud)
    {
        if (!ReferenceEquals(_repairHud, hud))
        {
            return;
        }

        _repairHud = null;
        _widgetUnavailableHud = null;
        _requirementWidget = null;
    }

    internal static void Shutdown()
    {
        _activeRepair = null;
        if (_requirementWidget != null)
        {
            UnityEngine.Object.Destroy(_requirementWidget);
        }

        _repairHud = null;
        _widgetUnavailableHud = null;
        _requirementWidget = null;
        _woodObjectDb = null;
        _wood = null;
        _woodRequirement = null;
        _workbenchScene = null;
        _workbench = null;
        ObservedHealth.Clear();
    }

    private static bool FieldRepairEnabled =>
        _healthPerWood != null && _healthPerWood.Value > 0;

    private static void HandleConfigChanged()
    {
        HideRequirementWidget();
        if (!FieldRepairEnabled)
        {
            ObservedHealth.Clear();
        }
    }

    private static bool TryGetFieldRepairQuote(
        Player? player,
        Piece? piece,
        out RepairQuote quote)
    {
        quote = default;
        if (!FieldRepairEnabled
            || player == null
            || piece == null
            || piece.GetComponentInChildren<Ship>() == null
            || !piece.TryGetComponent(out WearNTear wearNTear)
            || wearNTear.m_nview == null
            || !wearNTear.m_nview.IsValid()
            || wearNTear.m_nview.GetZDO() == null
            || ZoneSystem.instance == null
            || player.NoCostCheat()
            || ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoWorkbench)
            || HasRepairStation(player, piece))
        {
            return false;
        }

        float maxHealth = wearNTear.m_health;
        float currentHealth;
        if (wearNTear.m_nview.IsOwner())
        {
            currentHealth = wearNTear.m_nview.GetZDO()
                .GetFloat(ZDOVars.s_health, maxHealth);
            ObservedHealth[wearNTear] = currentHealth;
        }
        else if (!ObservedHealth.TryGetValue(wearNTear, out currentHealth))
        {
            currentHealth =
                Mathf.Clamp01(wearNTear.GetHealthPercentage()) * maxHealth;
        }

        if (float.IsNaN(maxHealth)
            || float.IsInfinity(maxHealth)
            || float.IsNaN(currentHealth)
            || float.IsInfinity(currentHealth))
        {
            return false;
        }

        float missingHealth = Mathf.Max(0f, maxHealth - currentHealth);
        int woodCost = ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey())
            ? 0
            : CalculateWoodCost(missingHealth, _healthPerWood.Value);

        ItemDrop? wood = null;
        if (woodCost > 0)
        {
            TryGetWood(out wood);
        }

        quote = new RepairQuote(piece, wearNTear, wood, woodCost);
        return true;
    }

    private static int CalculateWoodCost(float missingHealth, int healthPerWood)
    {
        if (missingHealth <= 0f || healthPerWood <= 0)
        {
            return 0;
        }

        float units = missingHealth / healthPerWood;
        return Mathf.Max(
            1,
            Mathf.CeilToInt(units - CostBoundaryEpsilon));
    }

    private static bool HasRepairStation(Player player, Piece piece)
    {
        CraftingStation? station = piece.m_craftingStation;
        if (station == null && !TryGetWorkbench(out station))
        {
            return false;
        }

        return station != null
               && CraftingStation.HaveBuildStationInRange(
                   station.m_name,
                   player.transform.position) != null;
    }

    private static bool TryGetWorkbench(out CraftingStation? workbench)
    {
        ZNetScene? scene = ZNetScene.instance;
        if (!ReferenceEquals(_workbenchScene, scene))
        {
            _workbenchScene = scene;
            _workbench = null;
        }

        if (_workbench != null)
        {
            workbench = _workbench;
            return true;
        }

        GameObject? prefab = scene?.GetPrefab(WorkbenchPrefabName);
        if (prefab != null && prefab.TryGetComponent(out CraftingStation resolved))
        {
            _workbench = resolved;
            workbench = resolved;
            return true;
        }

        if (!_workbenchWarningLogged)
        {
            _workbenchWarningLogged = true;
            BottleShipsPlugin.BottleShipsLogger.LogWarning(
                "Could not resolve piece_workbench for the Build Station=None ship-repair fallback. Treating the repair as outside station range.");
        }

        workbench = null;
        return false;
    }

    private static bool TryGetWood(out ItemDrop? wood)
    {
        ObjectDB? objectDb = ObjectDB.instance;
        if (!ReferenceEquals(_woodObjectDb, objectDb))
        {
            _woodObjectDb = objectDb;
            _wood = null;
            _woodRequirement = null;
        }

        if (_wood != null)
        {
            wood = _wood;
            return true;
        }

        GameObject? prefab = objectDb?.GetItemPrefab(WoodPrefabName);
        if (prefab != null && prefab.TryGetComponent(out ItemDrop resolved))
        {
            _wood = resolved;
            wood = resolved;
            return true;
        }

        if (!_woodWarningLogged)
        {
            _woodWarningLogged = true;
            BottleShipsPlugin.BottleShipsLogger.LogWarning(
                "Could not resolve the Wood item for field ship repair. Repair requests that require Wood will be blocked.");
        }

        wood = null;
        return false;
    }

    private static bool EnsureRequirementWidget(Hud hud)
    {
        InitializeHud(hud);

        if (ReferenceEquals(_widgetUnavailableHud, hud))
        {
            return false;
        }

        if (_requirementWidget != null)
        {
            SynchronizeRequirementWidgetTransform(hud);
            return true;
        }

        Transform healthRoot = hud.m_pieceHealthRoot.transform;
        Transform widgetParent = healthRoot.parent;
        Transform existing = widgetParent.Find(RequirementWidgetName);
        if (existing == null)
        {
            existing = healthRoot.Find(RequirementWidgetName);
        }

        if (existing != null)
        {
            _requirementWidget = existing.gameObject;
            if (IsRequirementWidgetValid(_requirementWidget))
            {
                ConfigureRequirementWidget(hud, _requirementWidget);
                return true;
            }

            UnityEngine.Object.Destroy(_requirementWidget);
            _requirementWidget = null;
            DisableRequirementWidget(hud);
            return false;
        }

        if (hud.m_requirementItems == null
            || hud.m_requirementItems.Length == 0
            || hud.m_requirementItems[0] == null)
        {
            DisableRequirementWidget(hud);
            return false;
        }

        GameObject widget = UnityEngine.Object.Instantiate(
            hud.m_requirementItems[0],
            widgetParent,
            worldPositionStays: false);
        widget.name = RequirementWidgetName;
        if (!IsRequirementWidgetValid(widget))
        {
            UnityEngine.Object.Destroy(widget);
            DisableRequirementWidget(hud);
            return false;
        }

        if (widget.transform is not RectTransform)
        {
            UnityEngine.Object.Destroy(widget);
            DisableRequirementWidget(hud);
            return false;
        }

        ConfigureRequirementWidget(hud, widget);
        widget.SetActive(false);
        _requirementWidget = widget;
        return true;
    }

    private static void ConfigureRequirementWidget(Hud hud, GameObject widget)
    {
        RectTransform rect = (RectTransform)widget.transform;
        rect.sizeDelta = new Vector2(42f, 42f);
        rect.localRotation = Quaternion.identity;
        rect.localScale = Vector3.one;

        Transform iconTransform = widget.transform.Find("res_icon");
        if (iconTransform is RectTransform iconRect)
        {
            iconRect.anchorMin = new Vector2(0.5f, 0.5f);
            iconRect.anchorMax = new Vector2(0.5f, 0.5f);
            iconRect.pivot = new Vector2(0.5f, 0.5f);
            iconRect.anchoredPosition = Vector2.zero;
            iconRect.sizeDelta = new Vector2(38f, 38f);
            iconRect.localRotation = Quaternion.identity;
            iconRect.localScale = Vector3.one;
            if (iconRect.TryGetComponent(out Image image))
            {
                image.preserveAspect = true;
                image.raycastTarget = false;
            }
        }

        Transform amountTransform = widget.transform.Find("res_amount");
        if (amountTransform is RectTransform amountRect)
        {
            amountRect.anchorMin = new Vector2(0.5f, 0.5f);
            amountRect.anchorMax = new Vector2(0.5f, 0.5f);
            amountRect.pivot = new Vector2(0.5f, 0.5f);
            amountRect.anchoredPosition = Vector2.zero;
            amountRect.sizeDelta = new Vector2(40f, 40f);
            amountRect.localRotation = Quaternion.identity;
            amountRect.localScale = Vector3.one;
            amountRect.SetAsLastSibling();
            if (amountRect.TryGetComponent(out TMP_Text amount))
            {
                amount.alignment = TextAlignmentOptions.Center;
                amount.fontStyle |= FontStyles.Bold;
                amount.enableAutoSizing = true;
                amount.fontSizeMin = 12f;
                amount.fontSizeMax = 24f;
                amount.raycastTarget = false;
            }
        }

        Transform resourceName = widget.transform.Find("res_name");
        if (resourceName != null)
        {
            resourceName.gameObject.SetActive(false);
        }

        SynchronizeRequirementWidgetTransform(hud, widget);
    }

    private static void SynchronizeRequirementWidgetTransform(
        Hud hud,
        GameObject? widget = null)
    {
        widget ??= _requirementWidget;
        if (widget == null
            || widget.transform is not RectTransform rect)
        {
            return;
        }

        Transform healthRoot = hud.m_pieceHealthRoot.transform;
        Transform parent = healthRoot.parent;
        if (rect.parent != parent)
        {
            rect.SetParent(parent, worldPositionStays: false);
        }

        if (healthRoot is RectTransform healthRect)
        {
            rect.anchorMin = healthRect.anchorMin;
            rect.anchorMax = healthRect.anchorMax;
        }

        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.localPosition = healthRoot.localPosition + RequirementOffset;
        rect.localRotation = Quaternion.identity;
        rect.localScale = Vector3.one;
    }

    private static bool IsRequirementWidgetValid(GameObject widget)
    {
        return widget.transform.Find("res_icon") != null
               && widget.transform.Find("res_name") != null
               && widget.transform.Find("res_amount") != null
               && widget.GetComponent<UITooltip>() != null;
    }

    private static void DisableRequirementWidget(
        Hud hud,
        Exception? exception = null)
    {
        _widgetUnavailableHud = hud;
        if (_requirementWidget != null)
        {
            _requirementWidget.SetActive(false);
            UnityEngine.Object.Destroy(_requirementWidget);
            _requirementWidget = null;
        }

        if (_widgetWarningLogged)
        {
            return;
        }

        _widgetWarningLogged = true;
        BottleShipsPlugin.BottleShipsLogger.LogWarning(
            exception == null
                ? "Could not create the field ship-repair requirement widget because the vanilla requirement UI is unavailable."
                : $"Disabled the field ship-repair requirement widget after the vanilla requirement UI failed: {exception}");
    }

    private static void HideRequirementWidget()
    {
        if (_requirementWidget != null)
        {
            _requirementWidget.SetActive(false);
        }
    }

    internal sealed class RepairAttempt
    {
        internal readonly Player Player;
        internal readonly Piece Piece;
        internal readonly WearNTear WearNTear;
        internal readonly Inventory Inventory;
        internal readonly string WoodName;
        internal readonly int WoodCost;
        internal readonly float LastRepairBefore;
        internal readonly RepairAttempt? Previous;
        internal bool RepairAccepted;

        internal RepairAttempt(
            Player player,
            Piece piece,
            WearNTear wearNTear,
            Inventory inventory,
            string woodName,
            int woodCost,
            float lastRepairBefore,
            RepairAttempt? previous)
        {
            Player = player;
            Piece = piece;
            WearNTear = wearNTear;
            Inventory = inventory;
            WoodName = woodName;
            WoodCost = woodCost;
            LastRepairBefore = lastRepairBefore;
            Previous = previous;
        }
    }

    private readonly struct RepairQuote
    {
        internal readonly Piece Piece;
        internal readonly WearNTear WearNTear;
        internal readonly ItemDrop? Wood;
        internal readonly int WoodCost;

        internal RepairQuote(
            Piece piece,
            WearNTear wearNTear,
            ItemDrop? wood,
            int woodCost)
        {
            Piece = piece;
            WearNTear = wearNTear;
            Wood = wood;
            WoodCost = woodCost;
        }
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.Repair))]
internal static class BottleShipsPlayerRepairPatch
{
    [HarmonyPriority(Priority.Last)]
    private static bool Prefix(
        Player __instance,
        out ShipRepairManager.RepairAttempt? __state)
    {
        return ShipRepairManager.BeginRepair(__instance, out __state);
    }

    [HarmonyPriority(Priority.First)]
    private static void Postfix(ref ShipRepairManager.RepairAttempt? __state)
    {
        ShipRepairManager.FinishRepair(ref __state);
    }

    [HarmonyPriority(Priority.First)]
    private static Exception? Finalizer(
        Exception? __exception,
        ref ShipRepairManager.RepairAttempt? __state)
    {
        ShipRepairManager.FinishRepair(ref __state);
        return __exception;
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.CheckCanRemovePiece))]
internal static class BottleShipsPlayerCheckCanRemovePiecePatch
{
    [HarmonyPriority(Priority.Last)]
    private static bool Prefix(
        Player __instance,
        Piece piece,
        ref bool __result)
    {
        return !ShipRepairManager.TryBypassStationCheck(
            __instance,
            piece,
            ref __result);
    }
}

[HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Repair))]
internal static class BottleShipsWearNTearRepairPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Postfix(WearNTear __instance, bool __result)
    {
        ShipRepairManager.RecordRepairResult(__instance, __result);
    }
}

[HarmonyPatch(typeof(WearNTear), nameof(WearNTear.RPC_HealthChanged))]
internal static class BottleShipsWearNTearHealthChangedShipRepairPatch
{
    private static void Postfix(WearNTear __instance, float health)
    {
        ShipRepairManager.RecordHealthChanged(__instance, health);
    }
}

[HarmonyPatch(typeof(WearNTear), nameof(WearNTear.OnDestroy))]
internal static class BottleShipsWearNTearOnDestroyShipRepairPatch
{
    private static void Prefix(WearNTear __instance)
    {
        ShipRepairManager.RemoveWearNTear(__instance);
    }
}

[HarmonyPatch(typeof(Hud), nameof(Hud.UpdateCrosshair))]
internal static class BottleShipsHudUpdateCrosshairShipRepairPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(Hud __instance, Player player)
    {
        ShipRepairManager.UpdateRequirementWidget(__instance, player);
    }
}

[HarmonyPatch(typeof(Hud), nameof(Hud.OnDestroy))]
internal static class BottleShipsHudOnDestroyShipRepairPatch
{
    private static void Postfix(Hud __instance)
    {
        ShipRepairManager.ClearHud(__instance);
    }
}

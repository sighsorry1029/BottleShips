using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace BottleShips;

internal static class BottleShipsManager
{
    private const string OriginalStationValue = "Original";
    private const string BatteringRamPrefab = "BatteringRam";
    private static readonly BottleTarget[] Targets =
    {
        new("Raft", "Raft", "Raft_bottle", "piece_workbench", false, 45f, 2, true, "piece_workbench", 1, "Wood:20, LeatherScraps:6, Resin:6"),
        new("Karve", "Karve", "Karve_bottle", "piece_workbench", false, 116f, 1, true, "forge", 1, "FineWood:30, DeerHide:10, Resin:20, BronzeNails:80"),
        new("Longship", "VikingShip", "VikingShip_bottle", "piece_workbench", false, 220f, 1, true, "forge", 1, "IronNails:100, DeerHide:10, FineWood:40, ElderBark:40"),
        new("Drakkar", "VikingShip_Ashlands", "VikingShip_Ashlands_bottle", "piece_workbench", false, 260f, 1, true, "piece_artisanstation", 2, "IronNails:100, CeramicPlate:30, FineWood:50, YggdrasilWood:25"),
        new("Cart", "Cart", "Cart_bottle", "None", false, 45f, 1, true, "forge", 1, "Wood:20, BronzeNails:10"),
        new("Battering Ram", "BatteringRam", "BatteringRam_bottle", "None", false, 170f, 1, false, "piece_artisanstation", 2, "Wood:10, Blackwood:20, FlametalNew:10, SurtlingCore:2"),
        new("Catapult", "Catapult", "Catapult_bottle", "None", false, 165f, 1, false, "piece_artisanstation", 2, "Blackwood:20, FlametalNew:10, CharredCogwheel:1"),
        new("Wood Portal", "portal_wood", "portal_wood_bottle", "None", true, 52f, 2, true, "piece_workbench", 1, "GreydwarfEye:10, FineWood:20, SurtlingCore:2"),
        new("Stone Portal", "portal_stone", "portal_stone_bottle", "None", true, 119f, 1, false, "piece_artisanstation", 2, "GreydwarfEye:10, Grausten:30, MoltenCore:2, Iron:2"),
        new("Troll Trap", "piece_trap_troll", "piece_trap_troll_bottle", "None", true, 70f, 6, false, "piece_artisanstation", 1, "BlackMetal:5, BronzeNails:10, MechanicalSpring:1"),
        new("Ballista", "piece_turret", "piece_turret_bottle", "None", true, 155f, 3, false, "piece_artisanstation", 1, "BlackMetal:10, YggdrasilWood:10, MechanicalSpring:3"),
    };

    private static readonly Dictionary<string, PieceBaseline> PieceBaselines = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Recipe> OwnedRecipes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> WarnedMessages = new(StringComparer.OrdinalIgnoreCase);
    private static bool ConfigBound;
    private static ZNetScene? BaselineScene;
    private static bool ApplyPending;
    private static bool ApplyWorkerRunning;
    private static int ApplyNotBeforeFrame;
    private static uint ApplyRequestVersion;

    internal static IEnumerable<string> BottleItemNames => Targets.Select(target => target.BottlePrefab);

    internal static void BindConfig(BottleShipsPlugin plugin)
    {
        if (ConfigBound)
        {
            return;
        }

        for (int index = 0; index < Targets.Length; index++)
        {
            BottleTarget target = Targets[index];
            target.Config = BottleConfig.Bind(plugin, target, index + 2);
            target.Config.AddChangedHandler(QueueApply);
        }

        ConfigBound = true;
    }

    private static IEnumerator ApplyWhenReady()
    {
        try
        {
            int attempt = 0;
            uint observedVersion = ApplyRequestVersion;
            while (attempt < 3600)
            {
                yield return null;

                if (!ApplyPending)
                {
                    yield break;
                }

                if (Time.frameCount < ApplyNotBeforeFrame)
                {
                    continue;
                }

                if (observedVersion != ApplyRequestVersion)
                {
                    observedVersion = ApplyRequestVersion;
                    attempt = 0;
                }

                ++attempt;
                ApplyPending = false;
                if (!TryApplyIfReady(out bool applied))
                {
                    yield break;
                }

                if (applied && !ApplyPending)
                {
                    yield break;
                }

                if (!applied)
                {
                    ApplyPending = true;
                }
            }

            if (observedVersion != ApplyRequestVersion)
            {
                yield break;
            }

            ApplyPending = false;
            BottleShipsPlugin.BottleShipsLogger.LogDebug("BottleShips could not apply configuration yet because ObjectDB, ZNetScene, or bottle item prefabs were not ready.");
        }
        finally
        {
            ApplyWorkerRunning = false;
            if (ApplyPending)
            {
                TryStartApplyWorker();
            }
        }
    }

    private static bool TryApplyIfReady(out bool applied)
    {
        try
        {
            applied = ApplyIfReady();
            return true;
        }
        catch (Exception exception)
        {
            applied = false;
            BottleShipsPlugin.BottleShipsLogger.LogError(
                "BottleShips could not apply its configuration. A later config or game-database change will retry.");
            BottleShipsPlugin.BottleShipsLogger.LogError(exception);
            return false;
        }
    }

    internal static void QueueApply()
    {
        ApplyPending = true;
        ApplyNotBeforeFrame = Time.frameCount + 1;
        unchecked
        {
            ++ApplyRequestVersion;
        }

        TryStartApplyWorker();
    }

    private static void TryStartApplyWorker()
    {
        if (ApplyWorkerRunning || BottleShipsPlugin.Instance == null)
        {
            return;
        }

        ApplyWorkerRunning = true;
        try
        {
            BottleShipsPlugin.Instance.StartCoroutine(ApplyWhenReady());
        }
        catch (Exception exception)
        {
            ApplyWorkerRunning = false;
            ApplyPending = false;
            BottleShipsPlugin.BottleShipsLogger.LogError(
                "BottleShips could not start its configuration apply worker.");
            BottleShipsPlugin.BottleShipsLogger.LogError(exception);
        }
    }

    private static bool ApplyIfReady()
    {
        if (!ConfigBound || ObjectDB.instance == null || ZNetScene.instance == null)
        {
            return false;
        }

        if (!AllBottleItemsReady())
        {
            return false;
        }

        CaptureBaselinesIfNeeded();

        Piece[] livePieces = Piece.s_allPieces.ToArray();
        foreach (BottleTarget target in Targets)
        {
            ApplyBottleItem(target);
            ApplyBottleRecipe(target);
            ApplyPiece(target, livePieces);
        }

        return true;
    }

    private static bool AllBottleItemsReady()
    {
        foreach (BottleTarget target in Targets)
        {
            if (ResolveItemDrop(target.BottlePrefab) == null)
            {
                return false;
            }
        }

        return true;
    }

    private static void CaptureBaselinesIfNeeded()
    {
        ZNetScene currentScene = ZNetScene.instance;
        if (BaselineScene != currentScene)
        {
            PieceBaselines.Clear();
            WarnedMessages.Clear();
            BaselineScene = currentScene;
        }

        foreach (BottleTarget target in Targets)
        {
            if (PieceBaselines.ContainsKey(target.PiecePrefab))
            {
                continue;
            }

            Piece? piece = ResolvePiece(target.PiecePrefab);
            if (piece == null)
            {
                WarnOnce($"Could not capture baseline for piece '{target.PiecePrefab}': prefab was not found.");
                continue;
            }

            PieceBaselines[target.PiecePrefab] = PieceBaseline.From(piece);
        }
    }

    private static void ApplyBottleItem(BottleTarget target)
    {
        ItemDrop? itemDrop = ResolveItemDrop(target.BottlePrefab);
        if (itemDrop?.m_itemData?.m_shared == null || target.Config == null)
        {
            return;
        }

        ItemDrop.ItemData.SharedData shared = itemDrop.m_itemData.m_shared;
        shared.m_weight = Math.Max(0f, target.Config.BottleWeight.Value);
        shared.m_maxStackSize = Math.Max(1, target.Config.BottleStack.Value);
        shared.m_teleportable = target.Config.BottleTeleportable.Value == BottleShipsPlugin.Toggle.On;
    }

    private static void ApplyBottleRecipe(BottleTarget target)
    {
        BottleConfig? config = target.Config;
        if (config == null)
        {
            return;
        }

        string recipeName = "Recipe_" + target.BottlePrefab;
        if (config.BottleRecipeEnabled.Value != BottleShipsPlugin.Toggle.On)
        {
            DisableRecipe(recipeName);
            return;
        }

        ItemDrop? item = ResolveItemDrop(target.BottlePrefab);
        if (item == null)
        {
            WarnOnce($"Could not apply recipe '{recipeName}': bottle item '{target.BottlePrefab}' was not found.");
            return;
        }

        if (!TryParseCraftingStationWithLevel(config.BottleRecipeStation.Value, out string recipeStation, out int recipeStationLevel))
        {
            WarnOnce($"Could not apply recipe '{recipeName}': invalid recipe station '{config.BottleRecipeStation.Value}'.");
            return;
        }

        if (!TryResolveCraftingStation(recipeStation, out CraftingStation? station))
        {
            WarnOnce($"Could not apply recipe '{recipeName}': crafting station '{recipeStation}' was not found.");
            return;
        }

        if (!TryParseRequirements(config.BottleRecipeResources.Value, out Piece.Requirement[] requirements))
        {
            WarnOnce($"Could not apply recipe '{recipeName}': invalid resources '{config.BottleRecipeResources.Value}'.");
            return;
        }

        Recipe? recipe = GetOrCreateRecipe(recipeName);
        if (recipe == null)
        {
            return;
        }

        recipe.m_item = item;
        recipe.m_amount = 1;
        recipe.m_enabled = true;
        recipe.m_craftingStation = station;
        recipe.m_minStationLevel = recipeStationLevel;
        recipe.m_requireOnlyOneIngredient = false;
        recipe.m_qualityResultAmountMultiplier = 1f;
        recipe.m_resources = requirements;
    }

    private static void ApplyPiece(BottleTarget target, IReadOnlyList<Piece> livePieces)
    {
        Piece? piece = ResolvePiece(target.PiecePrefab);
        if (piece == null || target.Config == null)
        {
            return;
        }

        ApplyPieceConfig(target, piece);
        ApplyBatteringRamPrefabSize(target, piece);

        foreach (Piece instance in livePieces)
        {
            if (instance == null || !PrefabNameEquals(instance.gameObject, target.PiecePrefab))
            {
                continue;
            }

            ApplyPieceConfig(target, instance);
        }
    }

    private static void ApplyPieceConfig(BottleTarget target, Piece piece)
    {
        if (target.Config == null)
        {
            return;
        }

        PieceBaseline? baseline = PieceBaselines.TryGetValue(target.PiecePrefab, out PieceBaseline captured)
            ? captured
            : null;

        if (baseline != null)
        {
            piece.m_resources = CloneRequirements(baseline.Resources);
            piece.m_craftingStation = baseline.CraftingStation;
            piece.m_canBeRemoved = baseline.CanBeRemoved;
        }

        piece.m_canBeRemoved = target.Config.CanBeRemoved.Value == BottleShipsPlugin.Toggle.On;

        if (!ApplyBuildStation(piece, target.Config.BuildStation.Value, baseline))
        {
            WarnOnce($"Could not apply piece '{target.PiecePrefab}': build station '{target.Config.BuildStation.Value}' was not found.");
        }

        if (target.Config.UseOriginalBuildRecipe.Value == BottleShipsPlugin.Toggle.On)
        {
            return;
        }

        if (!TryParseRequirements(target.Config.BuildResources.Value, out Piece.Requirement[] requirements))
        {
            WarnOnce($"Could not apply piece '{target.PiecePrefab}': invalid build resources '{target.Config.BuildResources.Value}'.");
            return;
        }

        piece.m_resources = requirements;
    }

    private static void ApplyBatteringRamPrefabSize(BottleTarget target, Piece prefabPiece)
    {
        if (!string.Equals(target.PiecePrefab, BatteringRamPrefab, StringComparison.OrdinalIgnoreCase) ||
            target.Config?.BatteringRamSize == null ||
            !PieceBaselines.TryGetValue(target.PiecePrefab, out PieceBaseline baseline) ||
            baseline.BatteringRam == null)
        {
            return;
        }

        float size = Mathf.Clamp(target.Config.BatteringRamSize.Value, 0.5f, 1f);
        baseline.BatteringRam.Apply(prefabPiece.gameObject, size);
    }

    private static bool ApplyBuildStation(Piece piece, string value, PieceBaseline? baseline)
    {
        string normalized = NormalizePrefabName(value);
        if (string.Equals(normalized, OriginalStationValue, StringComparison.OrdinalIgnoreCase))
        {
            if (baseline == null)
            {
                return false;
            }

            piece.m_craftingStation = baseline.CraftingStation;
            return true;
        }

        if (IsNone(normalized))
        {
            piece.m_craftingStation = null;
            return true;
        }

        if (!TryResolveCraftingStation(normalized, out CraftingStation? station))
        {
            return false;
        }

        piece.m_craftingStation = station;
        return true;
    }

    private static Recipe? GetOrCreateRecipe(string recipeName)
    {
        OwnedRecipes.TryGetValue(recipeName, out Recipe? ownedRecipe);
        if (ownedRecipe == null)
        {
            OwnedRecipes.Remove(recipeName);
        }

        Recipe? collision = ObjectDB.instance.m_recipes.FirstOrDefault(recipe =>
            recipe != null &&
            recipe != ownedRecipe &&
            string.Equals(recipe.name, recipeName, StringComparison.OrdinalIgnoreCase));
        if (collision != null)
        {
            WarnOnce(
                $"Could not create recipe '{recipeName}': another recipe with the same name is already registered.");
            return null;
        }

        if (ownedRecipe != null)
        {
            if (!ObjectDB.instance.m_recipes.Contains(ownedRecipe))
            {
                ObjectDB.instance.m_recipes.Add(ownedRecipe);
            }

            return ownedRecipe;
        }

        Recipe recipe = ScriptableObject.CreateInstance<Recipe>();
        recipe.name = recipeName;
        ObjectDB.instance.m_recipes.Add(recipe);
        OwnedRecipes[recipeName] = recipe;
        return recipe;
    }

    private static void DisableRecipe(string recipeName)
    {
        if (OwnedRecipes.TryGetValue(recipeName, out Recipe? recipe) && recipe != null)
        {
            recipe.m_enabled = false;
        }
    }

    private static bool TryParseRequirements(string definition, out Piece.Requirement[] requirements)
    {
        List<Piece.Requirement> parsed = new();
        requirements = Array.Empty<Piece.Requirement>();

        foreach (string rawToken in definition.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string token = rawToken.Trim();
            if (token.Length == 0)
            {
                continue;
            }

            string[] parts = token.Split(':');
            if (parts.Length is < 2 or > 4)
            {
                return false;
            }

            string itemName = NormalizePrefabName(parts[0]);
            if (!int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount) || amount < 0)
            {
                return false;
            }

            int amountPerLevel = 1;
            if (parts.Length >= 3 &&
                parts[2].Trim().Length > 0 &&
                !int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out amountPerLevel))
            {
                return false;
            }

            bool recover = true;
            if (parts.Length >= 4 &&
                parts[3].Trim().Length > 0 &&
                !bool.TryParse(parts[3].Trim(), out recover))
            {
                return false;
            }

            ItemDrop? item = ResolveItemDrop(itemName);
            if (item == null)
            {
                WarnOnce($"Could not resolve resource item '{itemName}'.");
                return false;
            }

            parsed.Add(new Piece.Requirement
            {
                m_resItem = item,
                m_amount = amount,
                m_amountPerLevel = amountPerLevel,
                m_recover = recover
            });
        }

        requirements = parsed.ToArray();
        return true;
    }

    private static bool TryResolveCraftingStation(string value, out CraftingStation? station)
    {
        station = null;
        string normalized = NormalizePrefabName(value);
        if (IsNone(normalized))
        {
            return true;
        }

        GameObject? prefab = ResolvePrefab(normalized);
        if (prefab == null || !prefab.TryGetComponent(out CraftingStation resolved))
        {
            return false;
        }

        station = resolved;
        return true;
    }

    private static bool TryParseCraftingStationWithLevel(string value, out string station, out int level)
    {
        station = "None";
        level = 1;

        string[] parts = (value ?? "").Split(',');
        if (parts.Length == 0 || parts.Length > 2)
        {
            return false;
        }

        station = NormalizePrefabName(parts[0]);
        if (station.Length == 0)
        {
            station = "None";
        }

        if (parts.Length == 2 &&
            (!int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out level) ||
             level < 1))
        {
            return false;
        }

        return true;
    }

    private static ItemDrop? ResolveItemDrop(string itemName)
    {
        string normalized = NormalizePrefabName(itemName);
        GameObject? prefab = ObjectDB.instance?.GetItemPrefab(normalized);
        if (prefab == null)
        {
            prefab = ResolvePrefab(normalized);
        }

        return prefab != null && prefab.TryGetComponent(out ItemDrop itemDrop) ? itemDrop : null;
    }

    private static Piece? ResolvePiece(string prefabName)
    {
        GameObject? prefab = ResolvePrefab(prefabName);
        return prefab != null && prefab.TryGetComponent(out Piece piece) ? piece : null;
    }

    private static GameObject? ResolvePrefab(string prefabName)
    {
        string normalized = NormalizePrefabName(prefabName);
        if (ZNetScene.instance != null)
        {
            GameObject prefab = ZNetScene.instance.GetPrefab(normalized);
            if (prefab != null)
            {
                return prefab;
            }
        }

        return ObjectDB.instance?.GetItemPrefab(normalized);
    }

    private static Piece.Requirement[] CloneRequirements(Piece.Requirement[] requirements)
    {
        return requirements
            .Where(requirement => requirement != null)
            .Select(requirement => new Piece.Requirement
            {
                m_resItem = requirement.m_resItem,
                m_amount = requirement.m_amount,
                m_extraAmountOnlyOneIngredient = requirement.m_extraAmountOnlyOneIngredient,
                m_amountPerLevel = requirement.m_amountPerLevel,
                m_recover = requirement.m_recover
            })
            .ToArray();
    }

    private static bool PrefabNameEquals(GameObject gameObject, string prefabName)
    {
        return string.Equals(Utils.GetPrefabName(gameObject), prefabName, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePrefabName(string value)
    {
        string normalized = (value ?? "").Trim();
        if (normalized.StartsWith("$", StringComparison.Ordinal))
        {
            normalized = normalized.Substring(1);
        }

        if (normalized.Equals("piece_forge", StringComparison.OrdinalIgnoreCase))
        {
            return "forge";
        }

        if (normalized.Equals("piece_artisantable", StringComparison.OrdinalIgnoreCase))
        {
            return "piece_artisanstation";
        }

        return normalized;
    }

    private static bool IsNone(string value)
    {
        return string.IsNullOrWhiteSpace(value) ||
               value.Equals("None", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("null", StringComparison.OrdinalIgnoreCase);
    }

    private static void WarnOnce(string message)
    {
        if (WarnedMessages.Add(message))
        {
            BottleShipsPlugin.BottleShipsLogger.LogWarning(message);
        }
    }

    private static string GetTransformPath(Transform root, Transform transform)
    {
        List<string> parts = new();
        Transform? current = transform;
        while (current != null && current != root)
        {
            parts.Add(current.name);
            current = current.parent;
        }

        parts.Reverse();
        return string.Join("/", parts);
    }

    private sealed class BottleTarget
    {
        internal readonly string DisplayName;
        internal readonly string PiecePrefab;
        internal readonly string BottlePrefab;
        internal readonly string DefaultBuildStation;
        internal readonly bool DefaultCanBeRemoved;
        internal readonly float DefaultBottleWeight;
        internal readonly int DefaultBottleStack;
        internal readonly bool DefaultBottleTeleportable;
        internal readonly string DefaultRecipeStation;
        internal readonly int DefaultRecipeStationLevel;
        internal readonly string DefaultRecipeResources;
        internal BottleConfig? Config;

        internal BottleTarget(
            string displayName,
            string piecePrefab,
            string bottlePrefab,
            string defaultBuildStation,
            bool defaultCanBeRemoved,
            float defaultBottleWeight,
            int defaultBottleStack,
            bool defaultBottleTeleportable,
            string defaultRecipeStation,
            int defaultRecipeStationLevel,
            string defaultRecipeResources)
        {
            DisplayName = displayName;
            PiecePrefab = piecePrefab;
            BottlePrefab = bottlePrefab;
            DefaultBuildStation = defaultBuildStation;
            DefaultCanBeRemoved = defaultCanBeRemoved;
            DefaultBottleWeight = defaultBottleWeight;
            DefaultBottleStack = defaultBottleStack;
            DefaultBottleTeleportable = defaultBottleTeleportable;
            DefaultRecipeStation = defaultRecipeStation;
            DefaultRecipeStationLevel = defaultRecipeStationLevel;
            DefaultRecipeResources = defaultRecipeResources;
        }
    }

    private sealed class BottleConfig
    {
        internal ConfigEntry<BottleShipsPlugin.Toggle> UseOriginalBuildRecipe = null!;
        internal ConfigEntry<string> BuildStation = null!;
        internal ConfigEntry<BottleShipsPlugin.Toggle> CanBeRemoved = null!;
        internal ConfigEntry<string> BuildResources = null!;
        internal ConfigEntry<float> BottleWeight = null!;
        internal ConfigEntry<int> BottleStack = null!;
        internal ConfigEntry<BottleShipsPlugin.Toggle> BottleTeleportable = null!;
        internal ConfigEntry<BottleShipsPlugin.Toggle> BottleRecipeEnabled = null!;
        internal ConfigEntry<string> BottleRecipeStation = null!;
        internal ConfigEntry<string> BottleRecipeResources = null!;
        internal ConfigEntry<float>? BatteringRamSize;

        internal static BottleConfig Bind(BottleShipsPlugin plugin, BottleTarget target, int sectionNumber)
        {
            string group = $"{sectionNumber:00} - {target.DisplayName}";
            BottleConfig config = new BottleConfig
            {
                UseOriginalBuildRecipe = plugin.config(
                    group,
                    "Use Original Build Recipe",
                    BottleShipsPlugin.Toggle.Off,
                    "If on, this piece uses the original upstream build resources captured at startup. If off, Build Resources is used.",
                    order: 1000),
                BuildStation = plugin.config(
                    group,
                    "Build Station",
                    target.DefaultBuildStation,
                    $"Crafting station required to build the piece. Use None for no station or {OriginalStationValue} for the captured upstream station.",
                    order: 980),
                CanBeRemoved = plugin.config(
                    group,
                    "Can Be Removed",
                    target.DefaultCanBeRemoved ? BottleShipsPlugin.Toggle.On : BottleShipsPlugin.Toggle.Off,
                    "If on, this piece can be removed by the hammer.",
                    order: 970),
                BuildResources = plugin.config(
                    group,
                    "Build Resources",
                    $"{target.BottlePrefab}:1",
                    "Resources required to build the piece when Use Original Build Recipe is off. Format: Item:Amount, OtherItem:Amount.",
                    order: 990),
                BottleWeight = plugin.config(
                    group,
                    "Weight",
                    target.DefaultBottleWeight,
                    "Bottle item weight.",
                    order: 920),
                BottleStack = plugin.config(
                    group,
                    "Stack",
                    target.DefaultBottleStack,
                    "Bottle item maximum stack size.",
                    order: 910),
                BottleTeleportable = plugin.config(
                    group,
                    "Teleportable",
                    target.DefaultBottleTeleportable ? BottleShipsPlugin.Toggle.On : BottleShipsPlugin.Toggle.Off,
                    "If on, this bottle item can be teleported.",
                    order: 900),
                BottleRecipeEnabled = plugin.config(
                    group,
                    "Recipe Enabled",
                    BottleShipsPlugin.Toggle.On,
                    "If on, BottleShips creates or updates the bottle item recipe.",
                    order: 960),
                BottleRecipeStation = plugin.config(
                    group,
                    "Recipe Station",
                    $"{target.DefaultRecipeStation}, {target.DefaultRecipeStationLevel}",
                    "Crafting station and minimum level for the bottle item recipe. Format: StationPrefab, Level. Use None, 1 for hand crafting.",
                    order: 940),
                BottleRecipeResources = plugin.config(
                    group,
                    "Recipe Resources",
                    target.DefaultRecipeResources,
                    "Resources required to craft the bottle item. Format: Item:Amount, OtherItem:Amount.",
                    order: 950)
            };

            if (string.Equals(target.PiecePrefab, BatteringRamPrefab, StringComparison.OrdinalIgnoreCase))
            {
                config.BatteringRamSize = plugin.config(
                    "01 - General",
                    "Battering Ram Size",
                    0.75f,
                    new ConfigDescription(
                        "Overall size multiplier for newly placed battering rams. Existing placed battering rams are not changed.",
                        new AcceptableValueRange<float>(0.5f, 1f)),
                    order: 960);
            }

            return config;
        }

        internal void AddChangedHandler(Action handler)
        {
            UseOriginalBuildRecipe.SettingChanged += (_, _) => handler();
            BuildStation.SettingChanged += (_, _) => handler();
            CanBeRemoved.SettingChanged += (_, _) => handler();
            BuildResources.SettingChanged += (_, _) => handler();
            BottleWeight.SettingChanged += (_, _) => handler();
            BottleStack.SettingChanged += (_, _) => handler();
            BottleTeleportable.SettingChanged += (_, _) => handler();
            BottleRecipeEnabled.SettingChanged += (_, _) => handler();
            BottleRecipeStation.SettingChanged += (_, _) => handler();
            BottleRecipeResources.SettingChanged += (_, _) => handler();
            if (BatteringRamSize != null)
            {
                BatteringRamSize.SettingChanged += (_, _) => handler();
            }
        }
    }

    private sealed class PieceBaseline
    {
        internal Piece.Requirement[] Resources = Array.Empty<Piece.Requirement>();
        internal CraftingStation? CraftingStation;
        internal bool CanBeRemoved;
        internal BatteringRamSizeBaseline? BatteringRam;

        internal static PieceBaseline From(Piece piece)
        {
            return new PieceBaseline
            {
                Resources = CloneRequirements(piece.m_resources ?? Array.Empty<Piece.Requirement>()),
                CraftingStation = piece.m_craftingStation,
                CanBeRemoved = piece.m_canBeRemoved,
                BatteringRam = PrefabNameEquals(piece.gameObject, BatteringRamPrefab)
                    ? BatteringRamSizeBaseline.From(piece.gameObject)
                    : null
            };
        }
    }

    private sealed class BatteringRamSizeBaseline
    {
        private readonly Dictionary<string, ChildTransformSnapshot> _children = new(StringComparer.Ordinal);
        private readonly List<JointSnapshot> _joints = new();
        private readonly Vector3 _rootScale;
        private readonly VagonSnapshot? _vagon;
        private readonly SiegeMachineSnapshot? _siegeMachine;

        private BatteringRamSizeBaseline(GameObject gameObject)
        {
            _rootScale = gameObject.transform.localScale;
            foreach (Transform child in gameObject.transform)
            {
                _children[GetTransformPath(gameObject.transform, child)] = new ChildTransformSnapshot(child);
            }

            foreach (ConfigurableJoint joint in gameObject.GetComponentsInChildren<ConfigurableJoint>(includeInactive: true))
            {
                _joints.Add(new JointSnapshot(gameObject.transform, joint));
            }

            Vagon vagon = gameObject.GetComponent<Vagon>();
            if (vagon != null)
            {
                _vagon = new VagonSnapshot(vagon);
            }

            SiegeMachine siegeMachine = gameObject.GetComponent<SiegeMachine>();
            if (siegeMachine != null)
            {
                _siegeMachine = new SiegeMachineSnapshot(siegeMachine);
            }
        }

        internal static BatteringRamSizeBaseline From(GameObject gameObject)
        {
            return new BatteringRamSizeBaseline(gameObject);
        }

        internal void Apply(GameObject gameObject, float size)
        {
            gameObject.transform.localScale = _rootScale;

            foreach (Transform child in gameObject.transform)
            {
                string path = GetTransformPath(gameObject.transform, child);
                if (_children.TryGetValue(path, out ChildTransformSnapshot snapshot))
                {
                    snapshot.Apply(child, size);
                }
            }

            foreach (JointSnapshot snapshot in _joints)
            {
                snapshot.Apply(gameObject.transform, size);
            }

            _vagon?.Apply(gameObject.GetComponent<Vagon>(), size);
            _siegeMachine?.Apply(gameObject.GetComponent<SiegeMachine>(), size);
        }

    }

    private sealed class ChildTransformSnapshot
    {
        private readonly Vector3 _localPosition;
        private readonly Vector3 _localScale;

        internal ChildTransformSnapshot(Transform transform)
        {
            _localPosition = transform.localPosition;
            _localScale = transform.localScale;
        }

        internal void Apply(Transform transform, float size)
        {
            transform.localPosition = _localPosition * size;
            transform.localScale = _localScale * size;
        }
    }

    private sealed class JointSnapshot
    {
        private readonly string _path;
        private readonly Vector3 _anchor;
        private readonly Vector3 _connectedAnchor;

        internal JointSnapshot(Transform root, ConfigurableJoint joint)
        {
            _path = GetTransformPath(root, joint.transform);
            _anchor = joint.anchor;
            _connectedAnchor = joint.connectedAnchor;
        }

        internal void Apply(Transform root, float size)
        {
            Transform target = root.Find(_path);
            if (target == null || !target.TryGetComponent(out ConfigurableJoint joint))
            {
                return;
            }

            joint.anchor = _anchor * size;
            joint.connectedAnchor = _connectedAnchor * size;
        }

    }

    private sealed class VagonSnapshot
    {
        private readonly float _detachDistance;
        private readonly Vector3 _attachOffset;
        private readonly Vector3 _lineAttachOffset;

        internal VagonSnapshot(Vagon vagon)
        {
            _detachDistance = vagon.m_detachDistance;
            _attachOffset = vagon.m_attachOffset;
            _lineAttachOffset = vagon.m_lineAttachOffset;
        }

        internal void Apply(Vagon? vagon, float size)
        {
            if (vagon == null)
            {
                return;
            }

            vagon.m_detachDistance = _detachDistance * size;
            vagon.m_attachOffset = _attachOffset * size;
            vagon.m_lineAttachOffset = _lineAttachOffset * size;
        }
    }

    private sealed class SiegeMachineSnapshot
    {
        private readonly float _chargeOffsetDistance;

        internal SiegeMachineSnapshot(SiegeMachine siegeMachine)
        {
            _chargeOffsetDistance = siegeMachine.m_chargeOffsetDistance;
        }

        internal void Apply(SiegeMachine? siegeMachine, float size)
        {
            if (siegeMachine == null)
            {
                return;
            }

            siegeMachine.m_chargeOffsetDistance = _chargeOffsetDistance * size;
        }
    }
}

[HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.Awake))]
internal static class BottleShipsObjectDbAwakePatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix()
    {
        BottleShipsManager.QueueApply();
    }
}

[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
internal static class BottleShipsZNetSceneAwakePatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix()
    {
        BottleShipsManager.QueueApply();
    }
}

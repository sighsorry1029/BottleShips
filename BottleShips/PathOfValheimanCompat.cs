using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace BottleShips;

internal static class PathOfValheimanCompat
{
    internal const string PluginGuid = "treextr.pathofvalheiman";
    private const string SavedKey = "sighsorry.BottleShips.pov.buildSaved";
    private const string PaidFactorKey = "sighsorry.BottleShips.pov.buildPaidFactor";
    private static readonly AccessTools.FieldRef<Ship, ZNetView> ShipView =
        AccessTools.FieldRefAccess<Ship, ZNetView>("m_nview");
    private static readonly HashSet<string> Warnings = new();
    private static readonly List<(MethodInfo Original, MethodInfo Patch)> InstalledPatches = new();
    private static Func<bool>? _hasFreeShipRepair;
    private static Func<ItemDrop.ItemData?>? _placingCrate;
    private static Harmony? _harmony;
    [ThreadStatic] private static int _massRepairDepth;

    internal static bool IsLoaded { get; private set; }
    internal static bool IsMassRepairActive => _massRepairDepth > 0;

    internal static void Initialize(Harmony harmony)
    {
        if (_harmony != null) return;
        _harmony = harmony;
        if (!Chainloader.PluginInfos.TryGetValue(PluginGuid, out var plugin) || plugin.Instance == null) return;

        IsLoaded = true;
        Assembly assembly = plugin.Instance.GetType().Assembly;
        try
        {
            _hasFreeShipRepair = CreateFreeRepairReader(assembly);
        }
        catch (Exception error)
        {
            WarnOnce("repair-api", "Free ship repair could not be detected; BottleShips repair behavior is unchanged.", error);
        }

        try
        {
            MethodInfo massRepair = RequireMethod(assembly, "PassiveTree.Conveniences+Player_Repair_Mass", "Postfix",
                typeof(void), typeof(Player), typeof(ItemDrop.ItemData), typeof(Piece));
            Install(massRepair, nameof(BeginMassRepair), prefix: true);
            Install(massRepair, nameof(EndMassRepair), finalizer: true);
        }
        catch (Exception error)
        {
            RemovePatches(nameof(BeginMassRepair), nameof(EndMassRepair));
            WarnOnce("mass-repair", "The mass repair boundary could not be installed.", error);
        }

        try
        {
            MethodInfo pack = RequireMethod(assembly, "PassiveTree.ShipCrate", "Pack", typeof(bool),
                typeof(Player), typeof(Piece), typeof(Ship));
            MethodInfo created = RequireMethod(assembly, "PassiveTree.ShipCrate+Created", "Postfix",
                typeof(void), typeof(Piece));
            FieldInfo? placing = assembly.GetType("PassiveTree.ShipCrate")?.GetField("placing",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (placing == null || placing.FieldType != typeof(ItemDrop.ItemData))
                throw new MissingFieldException("PassiveTree.ShipCrate.placing");

            var readPlacing = AccessTools.StaticFieldRefAccess<ItemDrop.ItemData>(placing);
            _placingCrate = () => readPlacing();
            // Install the restore side first. If the exact packing pattern does not match,
            // remove both patches: never retain only half of the preservation contract.
            Install(created, nameof(RestoreCratePayment));
            Install(pack, nameof(PreserveCratePayment), transpiler: true);
        }
        catch (Exception error)
        {
            RemovePatches(nameof(PreserveCratePayment), nameof(RestoreCratePayment));
            _placingCrate = null;
            WarnOnce("ship-crate", "Ship crate payment preservation could not be installed; the original PathOfValheiman crate behavior is unchanged.", error);
        }
    }

    internal static void Shutdown()
    {
        if (_harmony != null)
        {
            for (int i = InstalledPatches.Count - 1; i >= 0; --i)
                _harmony.Unpatch(InstalledPatches[i].Original, InstalledPatches[i].Patch);
        }
        InstalledPatches.Clear();
        _harmony = null;
        _hasFreeShipRepair = null;
        _placingCrate = null;
        _massRepairDepth = 0;
        IsLoaded = false;
        Warnings.Clear();
    }

    internal static bool HasFreeShipRepair(Player player)
    {
        if (!IsLoaded || player == null || player != Player.m_localPlayer || _hasFreeShipRepair == null) return false;
        try
        {
            return _hasFreeShipRepair();
        }
        catch (Exception error)
        {
            _hasFreeShipRepair = null;
            WarnOnce("repair-api", "Free ship repair detection failed; BottleShips repair behavior is unchanged.", error);
            return false;
        }
    }

    internal static bool IsAnchored(Ship ship)
    {
        if (!IsLoaded || ship == null) return false;
        ZNetView view = ShipView(ship);
        return view != null && view.IsValid() && (view.GetZDO()?.GetBool("pt.anchor", false) ?? false);
    }

    private static Func<bool> CreateFreeRepairReader(Assembly assembly)
    {
        Type progress = assembly.GetType("PassiveTree.Progress", throwOnError: true)!;
        Type stats = assembly.GetType("PassiveTree.StatSet", throwOnError: true)!;
        Type stat = assembly.GetType("PassiveTree.Stat", throwOnError: true)!;
        MethodInfo? current = progress.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();
        FieldInfo? statsField = progress.GetField("Stats", BindingFlags.Public | BindingFlags.Instance);
        MethodInfo? has = stats.GetMethod("Has", BindingFlags.Public | BindingFlags.Instance, null, new[] { stat }, null);
        if (current?.ReturnType != progress || statsField?.FieldType != stats || has?.ReturnType != typeof(bool) || !stat.IsEnum)
            throw new MissingMemberException("PassiveTree.Progress.Current / Stats / StatSet.Has(Stat)");

        object flag = Enum.Parse(stat, "ShipRepairAnywhere", ignoreCase: false);
        var value = Expression.Variable(progress, "progress");
        var set = Expression.Variable(stats, "stats");
        return Expression.Lambda<Func<bool>>(Expression.Block(new[] { value, set },
            Expression.Assign(value, Expression.Call(current)),
            Expression.AndAlso(Expression.NotEqual(value, Expression.Constant(null, progress)),
                Expression.Block(Expression.Assign(set, Expression.Field(value, statsField)),
                    Expression.AndAlso(Expression.NotEqual(set, Expression.Constant(null, stats)),
                        Expression.Call(set, has, Expression.Constant(flag, stat))))))).Compile();
    }

    private static MethodInfo RequireMethod(Assembly assembly, string typeName, string name, Type result, params Type[] parameters)
    {
        MethodInfo? method = assembly.GetType(typeName)?.GetMethod(name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, parameters, null);
        if (method == null || method.ReturnType != result || method.ContainsGenericParameters || method.GetMethodBody() == null)
            throw new MissingMethodException(typeName, name);
        return method;
    }

    private static void Install(MethodInfo original, string name, bool prefix = false, bool finalizer = false, bool transpiler = false)
    {
        MethodInfo method = AccessTools.DeclaredMethod(typeof(PathOfValheimanCompat), name);
        var patch = new HarmonyMethod(method) { priority = prefix ? Priority.First : Priority.Last };
        // Track before Patch so a failure after registration can still be rolled back.
        InstalledPatches.Add((original, method));
        _harmony!.Patch(original, prefix: prefix ? patch : null,
            postfix: !prefix && !finalizer && !transpiler ? patch : null,
            transpiler: transpiler ? patch : null, finalizer: finalizer ? patch : null, ilmanipulator: null);
    }

    private static void RemovePatches(params string[] names)
    {
        for (int i = InstalledPatches.Count - 1; i >= 0; --i)
        {
            var patch = InstalledPatches[i];
            if (!names.Contains(patch.Patch.Name)) continue;
            _harmony!.Unpatch(patch.Original, patch.Patch);
            InstalledPatches.RemoveAt(i);
        }
    }

    private static void BeginMassRepair(out int __state)
    {
        __state = _massRepairDepth;
        _massRepairDepth++;
    }

    private static void EndMassRepair(int __state)
    {
        _massRepairDepth = __state;
    }

    private static IEnumerable<CodeInstruction> PreserveCratePayment(IEnumerable<CodeInstruction> instructions)
    {
        var code = new List<CodeInstruction>(instructions);
        MethodInfo clone = AccessTools.DeclaredMethod(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.Clone));
        MethodInfo getZdo = AccessTools.DeclaredMethod(typeof(ZNetView), nameof(ZNetView.GetZDO));
        MethodInfo removeAll = AccessTools.DeclaredMethod(typeof(Inventory), nameof(Inventory.RemoveAll));
        MethodInfo addItem = AccessTools.DeclaredMethod(typeof(Inventory), nameof(Inventory.AddItem), new[] { typeof(ItemDrop.ItemData) });
        int cloneIndex = SingleCallIndex(code, clone);
        int getZdoIndex = SingleCallIndex(code, getZdo);
        int removeIndex = SingleCallIndex(code, removeAll);
        int addIndex = SingleCallIndex(code, addItem);
        if (cloneIndex < 0 || getZdoIndex < 0 || removeIndex < 0 || addIndex < 0 ||
            !(getZdoIndex < cloneIndex && cloneIndex < removeIndex && removeIndex < addIndex) ||
            cloneIndex + 1 >= code.Count || !code[cloneIndex + 1].IsStloc() || code.Any(instruction => instruction.blocks.Count != 0))
            throw new InvalidOperationException("PathOfValheiman ShipCrate.Pack payment preservation IL pattern did not match.");

        // Clone's result is still on the stack. Add only metadata to that item,
        // before any hold clearing, inventory insertion or world drop occurs.
        var loadPiece = new CodeInstruction(OpCodes.Ldarg_1);
        loadPiece.labels.AddRange(code[cloneIndex + 1].labels);
        code[cloneIndex + 1].labels.Clear();
        code.InsertRange(cloneIndex + 1, new[]
        {
            loadPiece,
            new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(PathOfValheimanCompat), nameof(CaptureCratePayment)))
        });
        return code;
    }

    private static int SingleCallIndex(List<CodeInstruction> code, MethodInfo method)
    {
        int found = -1;
        for (int i = 0; i < code.Count; ++i)
        {
            if (!code[i].Calls(method)) continue;
            if (found >= 0) return -1;
            found = i;
        }
        return found;
    }

    private static ItemDrop.ItemData CaptureCratePayment(ItemDrop.ItemData item, Piece piece)
    {
        ZNetView view = piece.GetComponent<ZNetView>();
        ZDO? zdo = view != null && view.IsValid() ? view.GetZDO() : null;
        if (zdo == null) throw new InvalidOperationException("The packed ship has no valid ZDO for payment preservation.");
        item.m_customData ??= new Dictionary<string, string>();
        item.m_customData[SavedKey] = zdo.GetBool("PT_BuildSaved", false) ? "1" : "0";
        item.m_customData[PaidFactorKey] = zdo.GetFloat("PT_BuildPaidFactor", 1f).ToString("R", CultureInfo.InvariantCulture);
        return item;
    }

    private static void RestoreCratePayment(Piece __0)
    {
        try
        {
            ItemDrop.ItemData? crate = _placingCrate?.Invoke();
            if (__0 == null || crate?.m_customData == null || __0.GetComponent<Ship>() == null ||
                !crate.m_customData.TryGetValue("pt.pack.ship", out string prefab) || prefab != Utils.GetPrefabName(__0.gameObject) ||
                !crate.m_customData.TryGetValue(SavedKey, out string saved) ||
                !crate.m_customData.TryGetValue(PaidFactorKey, out string factor)) return;
            // Old crates have no payment metadata. Never infer payment from their
            // creator or from the current player's skills.
            if ((saved != "0" && saved != "1") || !float.TryParse(factor, NumberStyles.Float, CultureInfo.InvariantCulture, out float paid) ||
                float.IsNaN(paid) || float.IsInfinity(paid))
            {
                WarnOnce("crate-data", "A ship crate has invalid payment metadata; its saved values were not applied.");
                return;
            }
            ZNetView view = __0.GetComponent<ZNetView>();
            if (view == null || !view.IsValid() || !view.IsOwner())
            {
                WarnOnce("crate-owner", "A restored ship was not locally owned; its payment metadata could not be restored.");
                return;
            }
            ZDO zdo = view.GetZDO();
            zdo.Set("PT_BuildSaved", saved == "1");
            zdo.Set("PT_BuildPaidFactor", paid);
        }
        catch (Exception error)
        {
            // The original handler has already restored the hull and cargo.
            // Do not interrupt the remaining placement path that consumes the crate.
            WarnOnce("crate-restore", "A restored ship's payment metadata could not be applied.", error);
        }
    }

    private static void WarnOnce(string key, string message, Exception? error = null)
    {
        if (!Warnings.Add(key)) return;
        BottleShipsPlugin.BottleShipsLogger.LogWarning("PathOfValheiman compatibility: " + message +
            (error == null ? "" : " " + error.GetType().Name + ": " + error.Message));
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

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
    private static Func<string, bool>? _hasStat;
    private static MethodInfo? _mapScan;
    private static MethodInfo? _mapStatHas;
    private static bool _harborMapReady;
    private static bool _harborBranchVerified;
    private static int _harborStat;
    private static int _shipMarkKind;
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
        InitializeVehicleMap(assembly);
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
        // Restore the original PoV condition even if patch removal fails partway through.
        _harborMapReady = false;
        _harborBranchVerified = false;
        if (_harmony != null)
        {
            for (int i = InstalledPatches.Count - 1; i >= 0; --i)
                _harmony.Unpatch(InstalledPatches[i].Original, InstalledPatches[i].Patch);
        }
        InstalledPatches.Clear();
        _harmony = null;
        _hasFreeShipRepair = null;
        _hasStat = null;
        _mapScan = null;
        _mapStatHas = null;
        _placingCrate = null;
        _massRepairDepth = 0;
        IsLoaded = false;
        Warnings.Clear();
    }

    private static void InitializeVehicleMap(Assembly assembly)
    {
        try
        {
            MethodInfo atLeast = RequirePublicApi(assembly, "Valheiman.Api.Pov", "AtLeast", typeof(int), typeof(int));
            MethodInfo feature = RequirePublicApi(assembly, "Valheiman.Api.Pov", "Has", typeof(string));
            if (!((Func<int, int, bool>)Delegate.CreateDelegate(typeof(Func<int, int, bool>), atLeast))(1, 0) ||
                !((Func<string, bool>)Delegate.CreateDelegate(typeof(Func<string, bool>), feature))("stats"))
                throw new MissingMemberException("PathOfValheiman public stats API is unavailable.");
            _hasStat = (Func<string, bool>)Delegate.CreateDelegate(typeof(Func<string, bool>),
                RequirePublicApi(assembly, "Valheiman.Api.Stats", "Has", typeof(string)));
            Type stat = assembly.GetType("PassiveTree.Stat", throwOnError: true)!;
            Type stats = assembly.GetType("PassiveTree.StatSet", throwOnError: true)!;
            Type kind = assembly.GetType("PassiveTree.MapSight+Kind", throwOnError: true)!;
            if (!stat.IsEnum || !kind.IsEnum) throw new MissingMemberException("PoV map enum contract changed.");
            _harborStat = Convert.ToInt32(Enum.Parse(stat, "HarborMark"), CultureInfo.InvariantCulture);
            _shipMarkKind = Convert.ToInt32(Enum.Parse(kind, "Ship"), CultureInfo.InvariantCulture);
            _mapStatHas = stats.GetMethod("Has", BindingFlags.Public | BindingFlags.Instance, null, new[] { stat }, null);
            if (_mapStatHas?.ReturnType != typeof(bool)) throw new MissingMethodException("StatSet.Has(Stat)");
            _mapScan = RequireMethod(assembly, "PassiveTree.MapSight", "Scan", typeof(void), typeof(Minimap), typeof(Player), stats);
            RequireMethod(assembly, "PassiveTree.MapSight", "AskFar", typeof(void), typeof(Player));
            RequireMethod(assembly, "PassiveTree.MapSight", "ShipIcon", typeof(Sprite));
            RequireMethod(assembly, "PassiveTree.MapSight", "Show", typeof(void), typeof(Minimap), kind,
                typeof(Vector3), typeof(Sprite), typeof(bool), typeof(string), typeof(string));
            Install(_mapScan, nameof(SuppressHarborBranch), transpiler: true);
            // The transpiler must have completed before either implementation changes ownership.
            if (!_harborBranchVerified || !HasExclusiveMapPatch())
                throw new InvalidOperationException("PoV map suppression did not install as a complete, exclusive patch.");
            _harborMapReady = true;
        }
        catch (Exception error)
        {
            _harborMapReady = false;
            _hasStat = null;
            try
            {
                RemovePatches(nameof(SuppressHarborBranch));
            }
            catch (Exception rollbackError)
            {
                // The inert filter remains tracked for shutdown; map failure must
                // not prevent the independent repair and crate integrations loading.
                WarnOnce("harbor-rollback", "An inactive map patch could not be removed during initialization.", rollbackError);
            }
            WarnOnce("harbor-map", "Vehicle map integration is unavailable; BottleShips vehicle pins are disabled and PathOfValheiman map behavior is retained.", error);
        }
    }

    private static MethodInfo RequirePublicApi(Assembly assembly, string typeName, string name, params Type[] parameters)
    {
        MethodInfo method = RequireMethod(assembly, typeName, name, typeof(bool), parameters);
        if (!method.IsPublic || method.DeclaringType?.IsPublic != true)
            throw new MissingMethodException(typeName, name);
        return method;
    }

    internal static bool CanUseVehicleMap(Player? player)
    {
        if (!IsLoaded) return true;
        if (!_harborMapReady || _hasStat == null || player == null || player != Player.m_localPlayer) return false;
        try
        {
            // Read the current effect, not a node id or cached selection. Server talent
            // switches can recompute stats without raising the public StatsChanged event.
            return _hasStat("HarborMark");
        }
        catch (Exception error)
        {
            DisableVehicleMap(error);
            return false;
        }
    }

    internal static bool ValidateVehicleMapIntegration()
    {
        if (!IsLoaded) return true;
        if (!_harborMapReady) return false;
        try
        {
            if (HasExclusiveMapPatch()) return true;
            DisableVehicleMap(new InvalidOperationException("The active PoV map patch contract changed."));
        }
        catch (Exception error)
        {
            DisableVehicleMap(error);
        }
        return false;
    }

    private static bool HasExclusiveMapPatch()
    {
        if (_mapScan == null || !_harborBranchVerified) return false;
        Patches? patches = Harmony.GetPatchInfo(_mapScan);
        return patches != null && patches.Prefixes.Count == 0 && patches.Postfixes.Count == 0 &&
               patches.Finalizers.Count == 0 && patches.ILManipulators.Count == 0 && patches.Transpilers.Count == 1 &&
               patches.Transpilers[0].owner == _harmony?.Id &&
               patches.Transpilers[0].PatchMethod == AccessTools.DeclaredMethod(typeof(PathOfValheimanCompat), nameof(SuppressHarborBranch));
    }

    private static void DisableVehicleMap(Exception error)
    {
        // Do not unpatch while PoV may be inside this method. The injected condition
        // immediately becomes transparent, and our normal shutdown removes the patch.
        _harborMapReady = false;
        _hasStat = null;
        WarnOnce("harbor-map", "Vehicle map integration stopped; BottleShips vehicle pins are disabled and PathOfValheiman map behavior is retained.", error);
    }

    private static bool KeepPovHarborMark(bool original) => original && !_harborMapReady;

    private static IEnumerable<CodeInstruction> SuppressHarborBranch(IEnumerable<CodeInstruction> instructions)
    {
        // Harmony also re-evaluates transpilers for inspection. A successful
        // reevaluation retains the handoff; failure leaves the original path enabled.
        bool wasReady = _harborMapReady;
        _harborMapReady = false;
        _harborBranchVerified = false;
        var code = new List<CodeInstruction>(instructions);
        int predicate = -1;
        for (int i = 1; i + 2 < code.Count; ++i)
        {
            if (!LoadsInt(code[i], _harborStat) || !code[i + 1].Calls(_mapStatHas!)) continue;
            if (predicate >= 0) throw new InvalidOperationException("PoV HarborMark has multiple scan predicates.");
            predicate = i;
        }
        if (predicate < 1 || code[predicate - 1].opcode != OpCodes.Ldarg_2 ||
            (code[predicate + 2].opcode != OpCodes.Brfalse && code[predicate + 2].opcode != OpCodes.Brfalse_S) ||
            code[predicate + 2].operand is not Label endLabel || code[predicate + 2].blocks.Count != 0)
            throw new InvalidOperationException("PoV HarborMark scan predicate changed.");
        int start = predicate + 3;
        int end = code.FindIndex(start, instruction => instruction.labels.Contains(endLabel));
        if (end <= start || code[end].opcode != OpCodes.Ldarg_2 || end + 2 >= code.Count ||
            !code[end + 2].Calls(_mapStatHas!) || code[start].opcode != OpCodes.Ldarg_1 ||
            !IsMapCall(code[start + 1], "AskFar"))
            throw new InvalidOperationException("PoV HarborMark is no longer an isolated map branch.");
        List<CodeInstruction> branch = code.GetRange(start, end - start);
        // PoV also draws live, player-owned ships before the distant reply in 6.0.7.
        // Its local set only prevents drawing the same ZDO twice. Both reviewed
        // variants remain ship-only; new effects or persistent writes need review.
        bool liveShips = branch.Any(instruction => instruction.operand is MethodInfo method &&
            method.DeclaringType == typeof(Ship) && method.Name == "get_Instances");
        string[] expectedCalls = liveShips
            ? new[] { "AskFar", "GetPlayerID", "get_Instances", "GetEnumerator", "get_Current", "op_Inequality",
                "GetComponentInParent", "op_Inequality", "GetComponent", "op_Equality", "op_Equality", "IsValid",
                "GetCreator", "GetZDO", "ToString", "Add", "get_transform", "get_position", "ShipIcon", "Concat",
                "Show", "MoveNext", "Dispose", "GetEnumerator", "get_Current", "Contains", "ShipIcon", "Concat",
                "Show", "MoveNext", "Dispose" }
            : new[] { "AskFar", "GetEnumerator", "get_Current", "ShipIcon", "Concat", "Show", "MoveNext", "Dispose" };
        MethodInfo?[] calls = branch.Where(instruction => instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
            .Select(instruction => instruction.operand as MethodInfo).ToArray();
        if (calls.Length != expectedCalls.Length || calls.Where((method, index) =>
                method == null || method.Name != expectedCalls[index] || !IsHarborCall(method, liveShips)).Any())
            throw new InvalidOperationException("PoV HarborMark scan calls changed.");
        int show = branch.FindLastIndex(instruction => IsMapCall(instruction, "Show"));
        if (branch.Count(instruction => IsMapCall(instruction, "Show")) != (liveShips ? 2 : 1) || show < 11 ||
            branch[show - 11].opcode != OpCodes.Ldarg_0 || !LoadsInt(branch[show - 10], _shipMarkKind) ||
            !IsMapField(branch[show - 8], "PassiveTree.MapSight+Far", "Pos") ||
            !IsMapCall(branch[show - 7], "ShipIcon") || !LoadsInt(branch[show - 6], 0) ||
            branch[show - 5].opcode != OpCodes.Ldstr || (string?)branch[show - 5].operand != "s:" ||
            !IsMapField(branch[show - 3], "PassiveTree.MapSight+Far", "Id") || branch[show - 1].opcode != OpCodes.Ldnull ||
            branch.Count(instruction => IsMapCall(instruction, "AskFar")) != 1 ||
            branch.Count(instruction => IsMapCall(instruction, "ShipIcon")) != (liveShips ? 2 : 1) ||
            !branch.Any(instruction => IsMapField(instruction, "PassiveTree.MapSight+Far", "Ship")) ||
            branch.Any(instruction => instruction.Calls(_mapStatHas!) ||
                (instruction.opcode.Name!.StartsWith("st", StringComparison.Ordinal) && !instruction.IsStloc()) ||
                instruction.opcode == OpCodes.Calli || instruction.opcode == OpCodes.Jmp ||
                instruction.opcode == OpCodes.Initobj || instruction.opcode == OpCodes.Cpobj ||
                instruction.opcode == OpCodes.Cpblk || instruction.opcode == OpCodes.Initblk ||
                instruction.opcode == OpCodes.Newarr || instruction.opcode == OpCodes.Localloc ||
                instruction.opcode == OpCodes.Ret || instruction.opcode.FlowControl == FlowControl.Throw ||
                (!liveShips && instruction.opcode == OpCodes.Newobj)))
            throw new InvalidOperationException("PoV HarborMark ship-only scan contract changed.");
        ValidateHarborBoundary(code, start, end);
        if (liveShips) ValidateLiveHarborBranch(branch, show);
        // Keep the shared stale-mark removal path: skipping this branch must make old
        // ship pins unseen, so PoV removes them on its next normal scan (about one second).
        bool clearsSeen = false, checksSeen = false, removesPin = false, removesMark = false;
        for (int i = 0; i + 1 < code.Count; ++i)
        {
            if (i < start && IsMapField(code[i], "PassiveTree.MapSight", "seenNow") &&
                code[i + 1].operand is MethodInfo clear && clear.Name == "Clear") clearsSeen = true;
            if (i <= end) continue;
            if (IsMapField(code[i], "PassiveTree.MapSight", "seenNow")) checksSeen = true;
            if (code[i].operand is MethodInfo method)
            {
                if (method.DeclaringType == typeof(Minimap) && method.Name == "DestroyPinMarker") removesPin = true;
                if (method.Name == "Remove" && method.DeclaringType?.IsGenericType == true &&
                    method.DeclaringType.GetGenericArguments().Last().FullName == "PassiveTree.MapSight+Mark") removesMark = true;
            }
        }
        if (!clearsSeen || !checksSeen || !removesPin || !removesMark)
            throw new InvalidOperationException("PoV map stale-pin cleanup contract changed.");
        var filter = new CodeInstruction(OpCodes.Call,
            AccessTools.DeclaredMethod(typeof(PathOfValheimanCompat), nameof(KeepPovHarborMark)));
        filter.labels.AddRange(code[predicate + 2].labels);
        code[predicate + 2].labels.Clear();
        code.Insert(predicate + 2, filter);
        _harborBranchVerified = true;
        _harborMapReady = wasReady;
        return code;
    }

    private static bool IsMapCall(CodeInstruction instruction, string name) =>
        instruction.operand is MethodInfo method && method.DeclaringType == _mapScan?.DeclaringType &&
        method.Name == name && (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt);

    private static void ValidateHarborBoundary(List<CodeInstruction> code, int start, int end)
    {
        var labels = new HashSet<Label>(code.GetRange(start, end - start).SelectMany(instruction => instruction.labels));
        // Harmony can assign distinct labels to branches targeting the same instruction.
        var exitLabels = new HashSet<Label>(code[end].labels);
        for (int i = 0; i < code.Count; ++i)
        {
            var targets = code[i].operand is Label target ? new[] { target } : code[i].operand as Label[];
            if (targets == null) continue;
            bool inside = i >= start && i < end;
            if (targets.Any(label => inside ? !labels.Contains(label) && !exitLabels.Contains(label) : labels.Contains(label)))
                throw new InvalidOperationException("PoV HarborMark control flow crosses the ship branch boundary.");
        }
        int depth = 0;
        foreach (CodeInstruction instruction in code.GetRange(start, end - start))
        foreach (ExceptionBlock block in instruction.blocks)
        {
            if (block.blockType == ExceptionBlockType.BeginExceptionBlock) ++depth;
            else if (block.blockType == ExceptionBlockType.EndExceptionBlock && depth > 0) --depth;
            else if (block.blockType != ExceptionBlockType.BeginFinallyBlock || depth == 0)
                throw new InvalidOperationException("PoV HarborMark exception boundary changed.");
        }
        if (depth != 0) throw new InvalidOperationException("PoV HarborMark exception region leaves the ship branch.");
    }

    private static void ValidateLiveHarborBranch(List<CodeInstruction> branch, int farShow)
    {
        int Find(Type type, string name) => branch.FindIndex(instruction => instruction.operand is MethodInfo method &&
            method.DeclaringType == type && method.Name == name);
        bool At(int index, OpCode opcode) => index >= 0 && index < branch.Count && branch[index].opcode == opcode;
        bool Load(int index, int local) => index >= 0 && index < branch.Count && LocalSlot(branch[index], false) == local && local >= 0;
        bool Jump(int index, OpCode normal, OpCode shortForm, int target) =>
            (At(index, normal) || At(index, shortForm)) && branch[index].operand is Label label &&
            target >= 0 && target < branch.Count && branch[target].labels.Contains(label);
        int show = branch.FindIndex(instruction => IsMapCall(instruction, "Show"));
        int valid = Find(typeof(ZNetView), "IsValid"), creator = Find(typeof(Piece), "GetCreator");
        int zdo = Find(typeof(ZNetView), "GetZDO"), idText = Find(typeof(object), "ToString");
        int add = Find(typeof(HashSet<string>), "Add"), contains = Find(typeof(HashSet<string>), "Contains");
        int pieceCall = Find(typeof(Component), "GetComponentInParent"), viewCall = Find(typeof(Component), "GetComponent");
        int firstCurrent = branch.FindIndex(instruction => instruction.operand is MethodInfo method && method.Name == "get_Current");
        int lastCurrent = branch.FindLastIndex(instruction => instruction.operand is MethodInfo method && method.Name == "get_Current");
        int farStart = branch.FindIndex(instruction => IsMapField(instruction, "PassiveTree.MapSight", "far"));
        int setLocal = LocalSlot(branch[3], true), playerLocal = LocalSlot(branch[6], true);
        int shipLocal = LocalSlot(branch[show - 9], false), idLocal = LocalSlot(branch[idText + 1], true);
        int pieceLocal = LocalSlot(branch[pieceCall + 1], true), viewLocal = LocalSlot(branch[viewCall + 1], true);
        int farLocal = LocalSlot(branch[farShow - 9], false);
        int liveEnumerator = LocalSlot(branch[9], true), farEnumerator = LocalSlot(branch[farStart + 2], true);
        int[] locals = { setLocal, playerLocal, shipLocal, idLocal, pieceLocal, viewLocal, farLocal, liveEnumerator, farEnumerator };
        if (show < 11 || farShow < 11 || locals.Any(local => local < 0) || locals.Distinct().Count() != locals.Length ||
            branch.Count(instruction => instruction.opcode == OpCodes.Newobj) != 1 ||
            branch[2].opcode != OpCodes.Newobj || !Equals(branch[2].operand, typeof(HashSet<string>).GetConstructor(Type.EmptyTypes)) ||
            !At(4, OpCodes.Ldarg_1) || Find(typeof(Player), "GetPlayerID") != 5 ||
            Find(typeof(Ship), "get_Instances") != 7 || !branch[8].Calls(typeof(List<IMonoUpdater>).GetMethod("GetEnumerator")!) ||
            !branch[firstCurrent].Calls(typeof(List<IMonoUpdater>.Enumerator).GetProperty("Current")!.GetMethod!) ||
            LocalSlot(branch[firstCurrent - 1], false, true) != liveEnumerator ||
            !At(firstCurrent + 1, OpCodes.Isinst) || !Equals(branch[firstCurrent + 1].operand, typeof(Ship)) ||
            LocalSlot(branch[firstCurrent + 2], true) != shipLocal || LocalSlot(branch[lastCurrent + 1], true) != farLocal ||
            LocalSlot(branch[lastCurrent - 1], false, true) != farEnumerator ||
            branch[farStart].operand is not FieldInfo farField ||
            !branch[farStart + 1].Calls(farField.FieldType.GetMethod("GetEnumerator")!) ||
            !At(show - 11, OpCodes.Ldarg_0) || !LoadsInt(branch[show - 10], _shipMarkKind) ||
            Find(typeof(Component), "get_transform") != show - 8 || Find(typeof(Transform), "get_position") != show - 7 ||
            !IsMapCall(branch[show - 6], "ShipIcon") || !LoadsInt(branch[show - 5], 0) ||
            !At(show - 4, OpCodes.Ldstr) || (string?)branch[show - 4].operand != "s:" || !Load(show - 3, idLocal) ||
            !At(show - 1, OpCodes.Ldnull) || !Load(pieceCall - 1, shipLocal) || !Load(viewCall - 1, pieceLocal) ||
            !Load(valid - 1, viewLocal) || !Jump(valid + 1, OpCodes.Brfalse, OpCodes.Brfalse_S, show + 1) ||
            !Load(creator - 1, pieceLocal) || !Load(creator + 1, playerLocal) ||
            !Jump(creator + 2, OpCodes.Bne_Un, OpCodes.Bne_Un_S, show + 1) ||
            zdo != creator + 4 || !Load(zdo - 1, viewLocal) ||
            !At(zdo + 1, OpCodes.Ldflda) || branch[zdo + 1].operand is not FieldInfo uid || uid.DeclaringType != typeof(ZDO) ||
            uid.Name != "m_uid" || uid.FieldType != typeof(ZDOID) ||
            !At(zdo + 2, OpCodes.Constrained) || !Equals(branch[zdo + 2].operand, typeof(ZDOID)) || idText != zdo + 3 ||
            !Load(add - 2, setLocal) || !Load(add - 1, idLocal) || !At(add + 1, OpCodes.Pop) ||
            !Load(contains - 3, setLocal) || !Load(contains - 2, farLocal) ||
            !IsMapField(branch[contains - 1], "PassiveTree.MapSight+Far", "Id") ||
            !Jump(contains + 1, OpCodes.Brtrue, OpCodes.Brtrue_S, farShow + 1) ||
            !Load(contains - 6, farLocal) || !IsMapField(branch[contains - 5], "PassiveTree.MapSight+Far", "Ship") ||
            !Jump(contains - 4, OpCodes.Brfalse, OpCodes.Brfalse_S, farShow + 1) ||
            !Load(farShow - 4, farLocal) ||
            locals.Any(local => branch.Count(instruction => LocalSlot(instruction, true) == local) != 1))
            throw new InvalidOperationException("PoV HarborMark live-ship ownership or deduplication contract changed.");
        // Only the two null checks may skip a live ship before the validity and owner checks.
        foreach (int equal in Enumerable.Range(0, branch.Count).Where(i => branch[i].operand is MethodInfo method &&
                     method.DeclaringType == typeof(UnityEngine.Object) && method.Name == "op_Equality"))
            if (!At(equal - 1, OpCodes.Ldnull) || (!Load(equal - 2, pieceLocal) && !Load(equal - 2, viewLocal)) ||
                !Jump(equal + 1, OpCodes.Brtrue, OpCodes.Brtrue_S, show + 1))
                throw new InvalidOperationException("PoV HarborMark live-ship null guard changed.");
        // Retaining an owner check is insufficient if a new edge can bypass it.
        // Verify the two enumerator loops, null-coalescing paths and guard exits.
        int[] jumps = Enumerable.Range(0, branch.Count).Where(i => branch[i].operand is Label || branch[i].operand is Label[]).ToArray();
        OpCode[] normal = { OpCodes.Br, OpCodes.Brtrue, OpCodes.Br, OpCodes.Brtrue, OpCodes.Br, OpCodes.Brtrue,
            OpCodes.Brtrue, OpCodes.Brfalse, OpCodes.Bne_Un, OpCodes.Brtrue, OpCodes.Leave, OpCodes.Br,
            OpCodes.Brfalse, OpCodes.Brtrue, OpCodes.Brtrue, OpCodes.Leave };
        OpCode[] compact = { OpCodes.Br_S, OpCodes.Brtrue_S, OpCodes.Br_S, OpCodes.Brtrue_S, OpCodes.Br_S, OpCodes.Brtrue_S,
            OpCodes.Brtrue_S, OpCodes.Brfalse_S, OpCodes.Bne_Un_S, OpCodes.Brtrue_S, OpCodes.Leave_S, OpCodes.Br_S,
            OpCodes.Brfalse_S, OpCodes.Brtrue_S, OpCodes.Brtrue_S, OpCodes.Leave_S };
        int[] targets = { show + 1, pieceCall - 1, pieceCall + 1, viewCall - 1, viewCall + 1, show + 1,
            show + 1, show + 1, show + 1, firstCurrent - 1, farStart, farShow + 1,
            farShow + 1, farShow + 1, lastCurrent - 1 };
        if (jumps.Length != normal.Length || targets.Where((target, index) => !Jump(jumps[index], normal[index], compact[index], target)).Any() ||
            (!At(jumps[15], OpCodes.Leave) && !At(jumps[15], OpCodes.Leave_S)) ||
            branch[jumps[15]].operand is not Label exit || branch.Any(instruction => instruction.labels.Contains(exit)))
            throw new InvalidOperationException("PoV HarborMark live-ship loop or guard flow changed.");
    }

    private static int LocalSlot(CodeInstruction instruction, bool store, bool address = false)
    {
        OpCode code = instruction.opcode;
        if (address ? code == OpCodes.Ldloca || code == OpCodes.Ldloca_S : store ? instruction.IsStloc() : code == OpCodes.Ldloc || code == OpCodes.Ldloc_S ||
            code == OpCodes.Ldloc_0 || code == OpCodes.Ldloc_1 || code == OpCodes.Ldloc_2 || code == OpCodes.Ldloc_3)
        {
            short first = (store ? OpCodes.Stloc_0 : OpCodes.Ldloc_0).Value;
            if (code.Value >= first && code.Value <= first + 3) return code.Value - first;
            return instruction.operand is LocalBuilder local ? local.LocalIndex : Convert.ToInt32(instruction.operand, CultureInfo.InvariantCulture);
        }
        return -1;
    }

    private static bool IsHarborCall(MethodInfo method, bool liveShips)
    {
        if (method.DeclaringType == _mapScan?.DeclaringType)
        {
            Type[] parameters = method.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
            if (!method.IsStatic) return false;
            return method.Name == "AskFar" && method.ReturnType == typeof(void) && parameters.SequenceEqual(new[] { typeof(Player) }) ||
                   method.Name == "ShipIcon" && method.ReturnType == typeof(Sprite) && parameters.Length == 0 ||
                   method.Name == "Show" && method.ReturnType == typeof(void) && parameters.Length == 7 &&
                   parameters[1] == _mapScan.DeclaringType.GetNestedType("Kind", BindingFlags.Public | BindingFlags.NonPublic) &&
                   parameters.Where((_, index) => index != 1).SequenceEqual(new[] { typeof(Minimap), typeof(Vector3), typeof(Sprite), typeof(bool), typeof(string), typeof(string) });
        }
        if (method.DeclaringType == typeof(string))
            return method == typeof(string).GetMethod(nameof(string.Concat), new[] { typeof(string), typeof(string) });
        if (method.DeclaringType == typeof(IDisposable)) return method == typeof(IDisposable).GetMethod(nameof(IDisposable.Dispose));
        Type? type = method.DeclaringType;
        if (type?.IsGenericType == true &&
               (type.GetGenericTypeDefinition() == typeof(List<>) || type.GetGenericTypeDefinition() == typeof(List<>.Enumerator)) &&
               (type.GetGenericArguments().Single().FullName == "PassiveTree.MapSight+Far" ||
                (liveShips && type.GetGenericArguments().Single() == typeof(IMonoUpdater))) &&
               method.Name is "GetEnumerator" or "get_Current" or "MoveNext") return true;
        if (!liveShips) return false;
        if (type == typeof(Player)) return method == typeof(Player).GetMethod(nameof(Player.GetPlayerID), Type.EmptyTypes);
        if (type == typeof(Ship)) return method == typeof(Ship).GetProperty("Instances")?.GetMethod;
        if (type == typeof(Piece)) return method == typeof(Piece).GetMethod(nameof(Piece.GetCreator), Type.EmptyTypes);
        if (type == typeof(ZNetView)) return method == typeof(ZNetView).GetMethod(nameof(ZNetView.IsValid), Type.EmptyTypes) ||
            method == typeof(ZNetView).GetMethod(nameof(ZNetView.GetZDO), Type.EmptyTypes);
        if (type == typeof(UnityEngine.Object)) return method == typeof(UnityEngine.Object).GetMethod("op_Equality") ||
            method == typeof(UnityEngine.Object).GetMethod("op_Inequality");
        if (type == typeof(Component)) return method == typeof(Component).GetProperty("transform")?.GetMethod ||
            (method.IsGenericMethod && method.GetParameters().Length == 0 &&
             ((method.Name == "GetComponentInParent" && method.GetGenericArguments().SequenceEqual(new[] { typeof(Piece) })) ||
              (method.Name == "GetComponent" && method.GetGenericArguments().SequenceEqual(new[] { typeof(ZNetView) }))));
        if (type == typeof(Transform)) return method == typeof(Transform).GetProperty("position")?.GetMethod;
        if (type == typeof(object)) return method == typeof(object).GetMethod(nameof(ToString), Type.EmptyTypes);
        return type == typeof(HashSet<string>) &&
               (method == typeof(HashSet<string>).GetMethod("Add", new[] { typeof(string) }) ||
                method == typeof(HashSet<string>).GetMethod("Contains", new[] { typeof(string) }));
    }

    private static bool IsMapField(CodeInstruction instruction, string type, string name) =>
        instruction.operand is FieldInfo field && field.DeclaringType?.FullName == type && field.Name == name &&
        (instruction.opcode == OpCodes.Ldfld || instruction.opcode == OpCodes.Ldsfld);

    private static bool LoadsInt(CodeInstruction instruction, int value) =>
        instruction.opcode == OpCodes.Ldc_I4 ? (int)instruction.operand == value :
        instruction.opcode == OpCodes.Ldc_I4_S ? Convert.ToInt32(instruction.operand, CultureInfo.InvariantCulture) == value :
        instruction.opcode == OpCodes.Ldc_I4_M1 ? value == -1 :
        instruction.opcode.Value >= OpCodes.Ldc_I4_0.Value && instruction.opcode.Value <= OpCodes.Ldc_I4_8.Value &&
        instruction.opcode.Value - OpCodes.Ldc_I4_0.Value == value;

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

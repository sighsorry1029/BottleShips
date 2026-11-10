#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using UnityEngine;

namespace UnityEngine
{
    public class Object
    {
        public static bool operator ==(Object? left, Object? right) => ReferenceEquals(left, right);
        public static bool operator !=(Object? left, Object? right) => !ReferenceEquals(left, right);
        public override bool Equals(object? value) => ReferenceEquals(this, value);
        public override int GetHashCode() => base.GetHashCode();
    }
    public class Component : Object
    {
        public Transform transform => new();
        public T GetComponent<T>() where T : class => null!;
        public T GetComponentInParent<T>() where T : class => null!;
    }
    public class Transform : Component { public BottleShipsCompatChecks.Vector3 position => default; }
}

namespace BottleShipsCompatChecks
{
    // The real integration is compiled below; these are game, BepInEx and Harmony boundaries only.
    public sealed class GameObject
    {
        public string name = "Karve";
        public readonly Dictionary<Type, object> Components = new();
        public T GetComponent<T>() where T : class => Components.TryGetValue(typeof(T), out var value) ? (T)value : null!;
    }
    public class Piece : Component
    {
        public GameObject gameObject = new();
        public new T GetComponent<T>() where T : class => gameObject.GetComponent<T>();
        public long GetCreator() => 0;
    }
    public interface IMonoUpdater { }
    public sealed class Ship : Component, IMonoUpdater
    {
        public ZNetView m_nview = new();
        public static List<IMonoUpdater> Instances { get; } = new();
    }
    public sealed class Player { public static Player? m_localPlayer; public long GetPlayerID() => 0; }
    public sealed class Minimap { public void DestroyPinMarker(object pin) { } }
    public struct Vector3 { }
    public sealed class Sprite { }
    public struct ZDOID { }
    public sealed class ZDO
    {
        public ZDOID m_uid;
        public readonly Dictionary<string, object> Data = new();
        public bool GetBool(string name, bool fallback) => Data.TryGetValue(name, out var value) ? (bool)value : fallback;
        public float GetFloat(string name, float fallback) => Data.TryGetValue(name, out var value) ? (float)value : fallback;
        public void Set(string name, bool value) => Data[name] = value;
        public void Set(string name, float value) => Data[name] = value;
    }
    public sealed class ZNetView
    {
        public ZDO Data = new(); public bool Valid = true, Owner = true;
        public bool IsValid() => Valid;
        public bool IsOwner() => Owner;
        public ZDO GetZDO() => Data;
    }
    public sealed class ItemDrop
    {
        public sealed class ItemData
        {
            public Dictionary<string, string>? m_customData;
            public ItemData Clone() => new();
        }
    }
    public sealed class Inventory
    {
        public void RemoveAll() { }
        public bool AddItem(ItemDrop.ItemData item) => true;
    }
    public static class Utils { public static string GetPrefabName(GameObject obj) => obj.name; }
    public sealed class PluginInfo { public object? Instance; }
    public static class Chainloader { public static readonly Dictionary<string, PluginInfo> PluginInfos = new(); }
    public sealed class Logger
    {
        public readonly List<string> Warnings = new();
        public void LogWarning(object message) => Warnings.Add(message.ToString()!);
    }
    public static class BottleShipsPlugin { public static readonly Logger BottleShipsLogger = new(); }
    public static class AccessTools
    {
        public delegate ref F FieldRef<T, F>(T instance);
        public delegate ref F StaticFieldRef<F>();
        private static ref ZNetView ReadShip(Ship ship) => ref ship.m_nview;
        public static FieldRef<T, F> FieldRefAccess<T, F>(string field) =>
            (FieldRef<T, F>)(object)(FieldRef<Ship, ZNetView>)ReadShip;
        public static StaticFieldRef<F> StaticFieldRefAccess<F>(FieldInfo field) =>
            (StaticFieldRef<F>)(object)(StaticFieldRef<ItemDrop.ItemData>)PassiveTree.ShipCrate.ReadPlacing;
        public static MethodInfo DeclaredMethod(Type type, string name, Type[]? arguments = null) =>
            arguments == null ? type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)!
                : type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, arguments, null)!;
    }
    public static class Priority { public const int First = 800, Last = 0; }
    public sealed class HarmonyMethod { public MethodInfo Method; public int priority; public HarmonyMethod(MethodInfo method) => Method = method; }
    public sealed class Patch { public string owner = "checks"; public MethodInfo PatchMethod = null!; }
    public sealed class Patches
    {
        public readonly List<Patch> Prefixes = new(), Postfixes = new(), Transpilers = new(), Finalizers = new(), ILManipulators = new();
    }
    public sealed class Harmony
    {
        public string Id => "checks";
        public string? FailPatch;
        public string? FailUnpatch;
        public readonly List<MethodInfo> Active = new();
        private static readonly Dictionary<MethodBase, Patches> Registry = new();
        public static Patches? GetPatchInfo(MethodBase method) => Registry.TryGetValue(method, out var patches) ? patches : null;
        public void Patch(MethodInfo original, HarmonyMethod? prefix = null, HarmonyMethod? postfix = null,
            HarmonyMethod? transpiler = null, HarmonyMethod? finalizer = null, object? ilmanipulator = null)
        {
            var patch = prefix ?? postfix ?? transpiler ?? finalizer!;
            Active.Add(patch.Method);
            if (!Registry.TryGetValue(original, out var patches)) Registry[original] = patches = new();
            (prefix != null ? patches.Prefixes : postfix != null ? patches.Postfixes : transpiler != null ? patches.Transpilers : patches.Finalizers)
                .Add(new Patch { PatchMethod = patch.Method });
            if (patch.Method.Name == FailPatch) throw new InvalidOperationException("Simulated patch registration failure");
            if (patch.Method.Name == "SuppressHarborBranch") patch.Method.Invoke(null, new object[] { Checks.UseLiveMap ? Checks.LiveMapCode() : Checks.MapCode() });
        }
        public void Unpatch(MethodInfo original, MethodInfo patch)
        {
            if (patch.Name == FailUnpatch) throw new InvalidOperationException("Simulated rollback failure");
            Active.Remove(patch);
            if (Registry.TryGetValue(original, out var patches))
                foreach (var list in new[] { patches.Prefixes, patches.Postfixes, patches.Transpilers, patches.Finalizers, patches.ILManipulators })
                    list.RemoveAll(p => p.PatchMethod == patch);
        }
    }
    public enum ExceptionBlockType { BeginExceptionBlock, BeginFinallyBlock, EndExceptionBlock }
    public sealed class ExceptionBlock
    {
        public ExceptionBlockType blockType;
        public ExceptionBlock(ExceptionBlockType type) => blockType = type;
    }
    public sealed class CodeInstruction
    {
        public OpCode opcode; public object? operand; public List<Label> labels = new(); public List<ExceptionBlock> blocks = new();
        public CodeInstruction(OpCode opcode, object? operand = null) { this.opcode = opcode; this.operand = operand; }
        public bool Calls(MethodInfo method) => (opcode == OpCodes.Call || opcode == OpCodes.Callvirt) && Equals(operand, method);
        public bool IsStloc() => opcode == OpCodes.Stloc || opcode == OpCodes.Stloc_S || opcode == OpCodes.Stloc_0 ||
            opcode == OpCodes.Stloc_1 || opcode == OpCodes.Stloc_2 || opcode == OpCodes.Stloc_3;
    }

    public static class Checks
    {
        private const string Saved = "sighsorry.BottleShips.pov.buildSaved";
        private const string Factor = "sighsorry.BottleShips.pov.buildPaidFactor";
        private static int _checks;
        internal static bool UseLiveMap;
        private static void Check(bool ok, string label) { if (!ok) throw new Exception(label); _checks++; }
        private static MethodInfo Method(string name) => AccessTools.DeclaredMethod(typeof(PathOfValheimanCompat), name);
        private static object? Invoke(string name, params object?[] args) => Method(name).Invoke(null, args);
        private static void Set(string name, object value) => typeof(PathOfValheimanCompat).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, value);
        private static Harmony Reset(bool loaded = true, string? failPatch = null, bool apiCompatible = true, string? failUnpatch = null, bool liveMap = false)
        {
            PathOfValheimanCompat.Shutdown(); Chainloader.PluginInfos.Clear();
            BottleShipsPlugin.BottleShipsLogger.Warnings.Clear();
            Player.m_localPlayer = new Player(); PassiveTree.Progress.Current = new PassiveTree.Progress();
            Valheiman.Api.Stats.Throw = false; Valheiman.Api.Stats.Harbor = false;
            Valheiman.Api.Pov.Compatible = apiCompatible;
            UseLiveMap = liveMap;
            PassiveTree.ShipCrate.SetPlacing(null);
            if (loaded) Chainloader.PluginInfos[PathOfValheimanCompat.PluginGuid] = new PluginInfo { Instance = new PassiveTree.Plugin() };
            var harmony = new Harmony { FailPatch = failPatch, FailUnpatch = failUnpatch };
            PathOfValheimanCompat.Initialize(harmony);
            return harmony;
        }
        private static Piece ShipPiece(out ZDO zdo)
        {
            var piece = new Piece(); var view = new ZNetView(); zdo = view.Data;
            piece.gameObject.Components[typeof(ZNetView)] = view;
            piece.gameObject.Components[typeof(Ship)] = new Ship { m_nview = view };
            return piece;
        }
        private static ItemDrop.ItemData Crate(string? saved = "0", string? factor = "1")
        {
            var item = new ItemDrop.ItemData { m_customData = new() { ["pt.pack.ship"] = "Karve" } };
            if (saved != null) item.m_customData[Saved] = saved;
            if (factor != null) item.m_customData[Factor] = factor;
            return item;
        }
        private static void Restore(Piece piece, ItemDrop.ItemData crate)
        {
            PassiveTree.ShipCrate.SetPlacing(crate); Invoke("RestoreCratePayment", piece);
        }
        private static List<CodeInstruction> PackCode()
        {
            return new()
            {
                new(OpCodes.Callvirt, AccessTools.DeclaredMethod(typeof(ZNetView), nameof(ZNetView.GetZDO))),
                new(OpCodes.Stloc_0), new(OpCodes.Nop),
                new(OpCodes.Callvirt, AccessTools.DeclaredMethod(typeof(ItemDrop.ItemData), nameof(ItemDrop.ItemData.Clone))),
                new(OpCodes.Stloc_1), new(OpCodes.Nop),
                new(OpCodes.Callvirt, AccessTools.DeclaredMethod(typeof(Inventory), nameof(Inventory.RemoveAll))),
                new(OpCodes.Callvirt, AccessTools.DeclaredMethod(typeof(Inventory), nameof(Inventory.AddItem), new[] { typeof(ItemDrop.ItemData) })),
                new(OpCodes.Ret)
            };
        }
        private static List<CodeInstruction> Rewrite(List<CodeInstruction> code) =>
            ((IEnumerable<CodeInstruction>)Invoke("PreserveCratePayment", code)!).ToList();
        private static void Rejected(List<CodeInstruction> code, string label)
        {
            bool failed = false;
            try { Rewrite(code); } catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { failed = true; }
            Check(failed, label);
            Check(!code.Any(c => c.Calls(Method("CaptureCratePayment"))), label + ": no partial injection");
        }

        public static string Run()
        {
            var harmony = Reset(loaded: false);
            Check(!PathOfValheimanCompat.IsLoaded && !PathOfValheimanCompat.HasFreeShipRepair(Player.m_localPlayer!), "Missing plugin leaves compatibility inactive");
            Check(harmony.Active.Count == 0, "Missing plugin installs no dynamic patches");
            Check(PathOfValheimanCompat.CanUseVehicleMap(Player.m_localPlayer) && PathOfValheimanCompat.ValidateVehicleMapIntegration(), "Without PoV, original vehicle map is available");
            harmony = Reset();
            Check(PathOfValheimanCompat.IsLoaded && harmony.Active.Count == 5, "Present plugin registers mass/crate pairs and isolated map branch");
            CheckHarborMap();
            Reset();
            PassiveTree.Progress.Current!.Stats!.Value = 1;
            Check(PathOfValheimanCompat.HasFreeShipRepair(Player.m_localPlayer!), "Active stat grants local free repair");
            Check(!PathOfValheimanCompat.HasFreeShipRepair(new Player()) && !PathOfValheimanCompat.HasFreeShipRepair(null!), "Stat is never borrowed by remote or missing player");
            PassiveTree.Progress.Current.Stats.Value = 0;
            Check(!PathOfValheimanCompat.HasFreeShipRepair(Player.m_localPlayer!), "Disabled or refunded stat removes free repair immediately");
            PassiveTree.Progress.Current.Stats = null;
            Check(!PathOfValheimanCompat.HasFreeShipRepair(Player.m_localPlayer!), "Null stat set is inactive");
            PassiveTree.Progress.Current = null;
            Check(!PathOfValheimanCompat.HasFreeShipRepair(Player.m_localPlayer!), "Null progress is inactive");
            PassiveTree.Progress.Current = new() { Stats = new() { Value = 1 } };
            Check(PathOfValheimanCompat.HasFreeShipRepair(Player.m_localPlayer!), "Reader observes replacement progress rather than caching a character");
            Set("_hasFreeShipRepair", (Func<bool>)(() => throw new InvalidOperationException("API unavailable")));
            Check(!PathOfValheimanCompat.HasFreeShipRepair(Player.m_localPlayer!), "Reader failure falls back to paid repair");
            Check(!PathOfValheimanCompat.HasFreeShipRepair(Player.m_localPlayer!) && BottleShipsPlugin.BottleShipsLogger.Warnings.Count == 1, "Failed reader is disabled and warns once");
            Reset();
            object?[] outer = { 0 }; Invoke("BeginMassRepair", outer);
            Check(PathOfValheimanCompat.IsMassRepairActive && (int)outer[0]! == 0, "Mass scope records previous depth");
            object?[] inner = { 0 }; Invoke("BeginMassRepair", inner);
            Check((int)inner[0]! == 1, "Nested mass scope records outer depth");
            try { throw new ApplicationException("Original mass repair failed"); }
            catch (ApplicationException) { }
            finally { Invoke("EndMassRepair", inner[0]); }
            Check(PathOfValheimanCompat.IsMassRepairActive, "Exception finalizer restores outer scope");
            bool childActive = true;
            var thread = new Thread(() => childActive = PathOfValheimanCompat.IsMassRepairActive); thread.Start(); thread.Join();
            Check(!childActive, "Mass scope is thread-local");
            Invoke("EndMassRepair", outer[0]);
            Check(!PathOfValheimanCompat.IsMassRepairActive, "Outermost finalizer clears scope");

            CheckCrates(); CheckTranspiler();
            harmony = Reset(failPatch: "EndMassRepair");
            Check(harmony.Active.Count == 3 && harmony.Active.All(p => p.Name is "RestoreCratePayment" or "PreserveCratePayment" or "SuppressHarborBranch"), "Mass installation failure rolls back both scope patches only");
            harmony = Reset(failPatch: "PreserveCratePayment");
            Check(harmony.Active.Count == 3 && harmony.Active.All(p => p.Name is "BeginMassRepair" or "EndMassRepair" or "SuppressHarborBranch"), "Pack installation failure rolls back capture and restore together");
            PathOfValheimanCompat.Shutdown();
            Check(!PathOfValheimanCompat.IsLoaded && !PathOfValheimanCompat.IsMassRepairActive && harmony.Active.Count == 0, "Shutdown clears scopes, readers and owned dynamic patches");
            return $"{_checks} PathOfValheiman compatibility checks passed (complete production source; game, plugin and Harmony boundaries are test doubles; no actual game or third-party assembly execution).";
        }

        internal static List<CodeInstruction> MapCode()
        {
            var type = typeof(PassiveTree.MapSight);
            var far = type.GetNestedType("Far", BindingFlags.Public | BindingFlags.NonPublic)!;
            var end = new DynamicMethod("map-label", typeof(void), Type.EmptyTypes).GetILGenerator().DefineLabel();
            var code = new List<CodeInstruction>
            {
                new(OpCodes.Ldsfld, type.GetField("seenNow")),
                new(OpCodes.Callvirt, typeof(HashSet<string>).GetMethod("Clear")),
                new(OpCodes.Ldarg_2), new(OpCodes.Ldc_I4, 645),
                new(OpCodes.Callvirt, typeof(PassiveTree.StatSet).GetMethod("Has")), new(OpCodes.Brfalse, end),
                new(OpCodes.Ldarg_1), new(OpCodes.Call, type.GetMethod("AskFar")),
                new(OpCodes.Ldsfld, type.GetField("far")), new(OpCodes.Ldfld, far.GetField("Ship")),
                new(OpCodes.Ldarg_0), new(OpCodes.Ldc_I4_5), new(OpCodes.Ldloc_0), new(OpCodes.Ldfld, far.GetField("Pos")),
                new(OpCodes.Call, type.GetMethod("ShipIcon")), new(OpCodes.Ldc_I4_0), new(OpCodes.Ldstr, "s:"),
                new(OpCodes.Ldloc_0), new(OpCodes.Ldfld, far.GetField("Id")),
                new(OpCodes.Call, typeof(string).GetMethod("Concat", new[] { typeof(string), typeof(string) })),
                new(OpCodes.Ldnull), new(OpCodes.Call, type.GetMethod("Show")),
                new(OpCodes.Ldarg_2), new(OpCodes.Ldc_I4, 635),
                new(OpCodes.Callvirt, typeof(PassiveTree.StatSet).GetMethod("Has")),
                new(OpCodes.Ldsfld, type.GetField("seenNow")),
                new(OpCodes.Callvirt, typeof(Minimap).GetMethod("DestroyPinMarker")),
                new(OpCodes.Callvirt, typeof(Dictionary<string, PassiveTree.MapSight.Mark>).GetMethod("Remove", new[] { typeof(string) })),
                new(OpCodes.Ret)
            };
            code[22].labels.Add(end);
            code.InsertRange(22, new[]
            {
                new CodeInstruction(OpCodes.Call, typeof(List<PassiveTree.MapSight.Far>.Enumerator).GetMethod("MoveNext")),
                new CodeInstruction(OpCodes.Callvirt, typeof(IDisposable).GetMethod("Dispose"))
            });
            code.InsertRange(9, new[]
            {
                new CodeInstruction(OpCodes.Callvirt, typeof(List<PassiveTree.MapSight.Far>).GetMethod("GetEnumerator")),
                new CodeInstruction(OpCodes.Call, typeof(List<PassiveTree.MapSight.Far>.Enumerator).GetProperty("Current")!.GetMethod)
            });
            return code;
        }

        // Recorded from the reviewed original PoV 6.0.7 HarborMark body, with test-double members.
        internal static List<CodeInstruction> LiveMapCode()
        {
            var type = typeof(PassiveTree.MapSight);
            var far = typeof(PassiveTree.MapSight.Far);
            var generator = new DynamicMethod("live-map-label", typeof(void), Type.EmptyTypes).GetILGenerator();
            var l4 = generator.DefineLabel();
            var l8 = generator.DefineLabel();
            var l3 = generator.DefineLabel();
            var l5 = generator.DefineLabel();
            var l6 = generator.DefineLabel();
            var l2 = generator.DefineLabel();
            var l1 = generator.DefineLabel();
            var l7 = generator.DefineLabel();
            var l0 = generator.DefineLabel();
            var l9 = generator.DefineLabel();
            var branch = new List<CodeInstruction>
            {
                new(OpCodes.Ldarg_1),
                new(OpCodes.Call, typeof(PassiveTree.MapSight).GetMethod("AskFar")),
                new(OpCodes.Newobj, typeof(HashSet<string>).GetConstructor(Type.EmptyTypes)),
                new(OpCodes.Stloc_S, 30),
                new(OpCodes.Ldarg_1),
                new(OpCodes.Callvirt, typeof(Player).GetMethod("GetPlayerID")),
                new(OpCodes.Stloc_S, 31),
                new(OpCodes.Call, typeof(Ship).GetProperty("Instances")!.GetMethod),
                new(OpCodes.Callvirt, typeof(List<IMonoUpdater>).GetMethod("GetEnumerator")),
                new(OpCodes.Stloc_S, 32),
                new(OpCodes.Br, l0),
                new(OpCodes.Ldloca_S, 32),
                new(OpCodes.Call, typeof(List<IMonoUpdater>.Enumerator).GetProperty("Current")!.GetMethod),
                new(OpCodes.Isinst, typeof(Ship)),
                new(OpCodes.Stloc_S, 33),
                new(OpCodes.Ldloc_S, 33),
                new(OpCodes.Ldnull),
                new(OpCodes.Call, typeof(UnityEngine.Object).GetMethod("op_Inequality")),
                new(OpCodes.Brtrue_S, l1),
                new(OpCodes.Ldnull),
                new(OpCodes.Br_S, l2),
                new(OpCodes.Ldloc_S, 33),
                new(OpCodes.Callvirt, typeof(Component).GetMethods().Single(method => method.Name == "GetComponentInParent" && method.IsGenericMethod && method.GetParameters().Length == 0).MakeGenericMethod(typeof(Piece))),
                new(OpCodes.Stloc_S, 34),
                new(OpCodes.Ldloc_S, 34),
                new(OpCodes.Ldnull),
                new(OpCodes.Call, typeof(UnityEngine.Object).GetMethod("op_Inequality")),
                new(OpCodes.Brtrue_S, l3),
                new(OpCodes.Ldnull),
                new(OpCodes.Br_S, l4),
                new(OpCodes.Ldloc_S, 34),
                new(OpCodes.Callvirt, typeof(Component).GetMethods().Single(method => method.Name == "GetComponent" && method.IsGenericMethod && method.GetParameters().Length == 0).MakeGenericMethod(typeof(ZNetView))),
                new(OpCodes.Stloc_S, 35),
                new(OpCodes.Ldloc_S, 34),
                new(OpCodes.Ldnull),
                new(OpCodes.Call, typeof(UnityEngine.Object).GetMethod("op_Equality")),
                new(OpCodes.Brtrue_S, l0),
                new(OpCodes.Ldloc_S, 35),
                new(OpCodes.Ldnull),
                new(OpCodes.Call, typeof(UnityEngine.Object).GetMethod("op_Equality")),
                new(OpCodes.Brtrue_S, l0),
                new(OpCodes.Ldloc_S, 35),
                new(OpCodes.Callvirt, typeof(ZNetView).GetMethod("IsValid")),
                new(OpCodes.Brfalse_S, l0),
                new(OpCodes.Ldloc_S, 34),
                new(OpCodes.Callvirt, typeof(Piece).GetMethod("GetCreator")),
                new(OpCodes.Ldloc_S, 31),
                new(OpCodes.Bne_Un_S, l0),
                new(OpCodes.Ldloc_S, 35),
                new(OpCodes.Callvirt, typeof(ZNetView).GetMethod("GetZDO")),
                new(OpCodes.Ldflda, typeof(ZDO).GetField("m_uid")),
                new(OpCodes.Constrained, typeof(ZDOID)),
                new(OpCodes.Callvirt, typeof(object).GetMethod("ToString")),
                new(OpCodes.Stloc_S, 36),
                new(OpCodes.Ldloc_S, 30),
                new(OpCodes.Ldloc_S, 36),
                new(OpCodes.Callvirt, typeof(HashSet<string>).GetMethod("Add")),
                new(OpCodes.Pop),
                new(OpCodes.Ldarg_0),
                new(OpCodes.Ldc_I4_5),
                new(OpCodes.Ldloc_S, 33),
                new(OpCodes.Callvirt, typeof(Component).GetProperty("transform")!.GetMethod),
                new(OpCodes.Callvirt, typeof(Transform).GetProperty("position")!.GetMethod),
                new(OpCodes.Call, typeof(PassiveTree.MapSight).GetMethod("ShipIcon")),
                new(OpCodes.Ldc_I4_0),
                new(OpCodes.Ldstr, "s:"),
                new(OpCodes.Ldloc_S, 36),
                new(OpCodes.Call, typeof(string).GetMethod("Concat", new[] { typeof(string), typeof(string) })),
                new(OpCodes.Ldnull),
                new(OpCodes.Call, typeof(PassiveTree.MapSight).GetMethod("Show")),
                new(OpCodes.Ldloca_S, 32),
                new(OpCodes.Call, typeof(List<IMonoUpdater>.Enumerator).GetMethod("MoveNext")),
                new(OpCodes.Brtrue, l5),
                new(OpCodes.Leave_S, l6),
                new(OpCodes.Ldloca_S, 32),
                new(OpCodes.Constrained, typeof(List<IMonoUpdater>.Enumerator)),
                new(OpCodes.Callvirt, typeof(IDisposable).GetMethod("Dispose")),
                new(OpCodes.Endfinally),
                new(OpCodes.Ldsfld, typeof(PassiveTree.MapSight).GetField("far")),
                new(OpCodes.Callvirt, typeof(List<PassiveTree.MapSight.Far>).GetMethod("GetEnumerator")),
                new(OpCodes.Stloc_S, 37),
                new(OpCodes.Br_S, l7),
                new(OpCodes.Ldloca_S, 37),
                new(OpCodes.Call, typeof(List<PassiveTree.MapSight.Far>.Enumerator).GetProperty("Current")!.GetMethod),
                new(OpCodes.Stloc_S, 38),
                new(OpCodes.Ldloc_S, 38),
                new(OpCodes.Ldfld, typeof(PassiveTree.MapSight.Far).GetField("Ship")),
                new(OpCodes.Brfalse_S, l7),
                new(OpCodes.Ldloc_S, 30),
                new(OpCodes.Ldloc_S, 38),
                new(OpCodes.Ldfld, typeof(PassiveTree.MapSight.Far).GetField("Id")),
                new(OpCodes.Callvirt, typeof(HashSet<string>).GetMethod("Contains")),
                new(OpCodes.Brtrue_S, l7),
                new(OpCodes.Ldarg_0),
                new(OpCodes.Ldc_I4_5),
                new(OpCodes.Ldloc_S, 38),
                new(OpCodes.Ldfld, typeof(PassiveTree.MapSight.Far).GetField("Pos")),
                new(OpCodes.Call, typeof(PassiveTree.MapSight).GetMethod("ShipIcon")),
                new(OpCodes.Ldc_I4_0),
                new(OpCodes.Ldstr, "s:"),
                new(OpCodes.Ldloc_S, 38),
                new(OpCodes.Ldfld, typeof(PassiveTree.MapSight.Far).GetField("Id")),
                new(OpCodes.Call, typeof(string).GetMethod("Concat", new[] { typeof(string), typeof(string) })),
                new(OpCodes.Ldnull),
                new(OpCodes.Call, typeof(PassiveTree.MapSight).GetMethod("Show")),
                new(OpCodes.Ldloca_S, 37),
                new(OpCodes.Call, typeof(List<PassiveTree.MapSight.Far>.Enumerator).GetMethod("MoveNext")),
                new(OpCodes.Brtrue_S, l8),
                new(OpCodes.Leave_S, l9),
                new(OpCodes.Ldloca_S, 37),
                new(OpCodes.Constrained, typeof(List<PassiveTree.MapSight.Far>.Enumerator)),
                new(OpCodes.Callvirt, typeof(IDisposable).GetMethod("Dispose")),
                new(OpCodes.Endfinally),
            };
            branch[32].labels.Add(l4);
            branch[82].labels.Add(l8);
            branch[30].labels.Add(l3);
            branch[11].labels.Add(l5);
            branch[78].labels.Add(l6);
            branch[23].labels.Add(l2);
            branch[21].labels.Add(l1);
            branch[105].labels.Add(l7);
            branch[70].labels.Add(l0);
            branch[10].blocks.Add(new(ExceptionBlockType.BeginExceptionBlock));
            branch[74].blocks.Add(new(ExceptionBlockType.BeginFinallyBlock));
            branch[77].blocks.Add(new(ExceptionBlockType.EndExceptionBlock));
            branch[81].blocks.Add(new(ExceptionBlockType.BeginExceptionBlock));
            branch[109].blocks.Add(new(ExceptionBlockType.BeginFinallyBlock));
            branch[112].blocks.Add(new(ExceptionBlockType.EndExceptionBlock));
            var legacy = MapCode();
            var end = (Label)legacy[5].operand!;
            int tail = legacy.FindIndex(instruction => instruction.labels.Contains(end));
            legacy[5].operand = l9;
            legacy[tail].labels.Clear();
            legacy[tail].labels.Add(l9);
            return legacy.Take(6).Concat(branch).Concat(legacy.Skip(tail)).ToList();
        }

        private static void CheckHarborMap()
        {
            Check(PathOfValheimanCompat.ValidateVehicleMapIntegration(), "Complete map contract is active");
            Check(!PathOfValheimanCompat.CanUseVehicleMap(Player.m_localPlayer), "PoV without active Harbor Mark keeps BS pins locked");
            Valheiman.Api.Stats.Harbor = true;
            Check(PathOfValheimanCompat.CanUseVehicleMap(Player.m_localPlayer), "Public active Harbor Mark effect unlocks vehicle map");
            Check(!PathOfValheimanCompat.CanUseVehicleMap(new Player()) && !PathOfValheimanCompat.CanUseVehicleMap(null), "Local stat is never used for another player");
            Check(!(bool)Invoke("KeepPovHarborMark", true)! && !(bool)Invoke("KeepPovHarborMark", false)!, "Only PoV harbor rendering is suppressed while replacement is ready");
            Valheiman.Api.Stats.Harbor = false;
            Check(!PathOfValheimanCompat.CanUseVehicleMap(Player.m_localPlayer), "Refund and server-disabled talent take effect without an event");
            Valheiman.Api.Stats.Throw = true;
            Check(!PathOfValheimanCompat.CanUseVehicleMap(Player.m_localPlayer) && !PathOfValheimanCompat.ValidateVehicleMapIntegration(), "API failure disables replacement");
            Check((bool)Invoke("KeepPovHarborMark", true)! && !(bool)Invoke("KeepPovHarborMark", false)!, "API failure restores original PoV condition without unpatching inside it");
            Check(!PathOfValheimanCompat.CanUseVehicleMap(Player.m_localPlayer) && BottleShipsPlugin.BottleShipsLogger.Warnings.Count == 1, "API failure is logged once");
            var harmony = Reset();
            var scan = typeof(PassiveTree.MapSight).GetMethod("Scan")!;
            Harmony.GetPatchInfo(scan)!.Transpilers.Clear();
            Check(!PathOfValheimanCompat.ValidateVehicleMapIntegration() && !PathOfValheimanCompat.CanUseVehicleMap(Player.m_localPlayer), "Missing active suppression patch blocks replacement");
            Check((bool)Invoke("KeepPovHarborMark", true)!, "Incomplete patch returns rendering to PoV");
            Reset();
            Harmony.GetPatchInfo(scan)!.Prefixes.Add(new Patch { owner = "another-map-mod", PatchMethod = Method("BeginMassRepair") });
            Check(!PathOfValheimanCompat.ValidateVehicleMapIntegration(), "Unverified modifications to the same PoV scan disable only replacement");
            Harmony.GetPatchInfo(scan)!.Prefixes.Clear();
            harmony = Reset(failPatch: "SuppressHarborBranch");
            Check(harmony.Active.Count == 4 && !PathOfValheimanCompat.ValidateVehicleMapIntegration(), "Map installation failure rolls back only map patch");
            Check((bool)Invoke("KeepPovHarborMark", true)!, "Failed install never suppresses PoV map");
            harmony = Reset(failPatch: "SuppressHarborBranch", failUnpatch: "SuppressHarborBranch");
            Check(harmony.Active.Count == 5 && harmony.Active.Count(p => p.Name != "SuppressHarborBranch") == 4,
                "Map rollback failure still initializes independent repair and crate integrations");
            Check((bool)Invoke("KeepPovHarborMark", true)! && !PathOfValheimanCompat.CanUseVehicleMap(Player.m_localPlayer),
                "Unremoved filter stays inert and BS replacement stays disabled");
            harmony.FailUnpatch = null;
            harmony = Reset(apiCompatible: false);
            Check(harmony.Active.Count == 4 && !PathOfValheimanCompat.CanUseVehicleMap(Player.m_localPlayer), "Unavailable public API leaves BS replacement off while retaining other integrations");
            Check((bool)Invoke("KeepPovHarborMark", true)!, "Unavailable public API preserves original PoV map");
            Reset();
            var source = MapCode();
            var rewritten = ((IEnumerable<CodeInstruction>)Invoke("SuppressHarborBranch", source)!).ToList();
            Check(rewritten.Count == source.Count + 1 && rewritten[5].Calls(Method("KeepPovHarborMark")), "Filter decorates only Harbor Mark Has result before branch");
            Check(rewritten.Count(c => c.Calls(typeof(PassiveTree.StatSet).GetMethod("Has")!)) == 2, "Original stat queries and other feature branches are preserved");
            Valheiman.Api.Stats.Harbor = true;
            Check(PathOfValheimanCompat.CanUseVehicleMap(Player.m_localPlayer) && !(bool)Invoke("KeepPovHarborMark", true)!, "Successful Harmony inspection/recompilation retains active handoff");
            foreach (int changed in new[] { 3, 5, 7, 13, 16, 18, 23, 29, 30, 31 })
            {
                var bad = MapCode(); bad[changed] = new(OpCodes.Nop);
                bool failed = false;
                try { Invoke("SuppressHarborBranch", bad); }
                catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { failed = true; }
                Check(failed && !bad.Any(c => c.Calls(Method("KeepPovHarborMark"))), "Changed related predicate/show/cleanup rejects without partial injection");
            }
            Check(!PathOfValheimanCompat.CanUseVehicleMap(Player.m_localPlayer) && (bool)Invoke("KeepPovHarborMark", true)!, "Failed retranspile turns off the old handoff as well");
            var sideEffect = MapCode(); sideEffect.Insert(8, new(OpCodes.Call, Method("CaptureCratePayment")));
            bool extraRejected = false;
            try { Invoke("SuppressHarborBranch", sideEffect); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { extraRejected = true; }
            Check(extraRejected && !sideEffect.Any(c => c.Calls(Method("KeepPovHarborMark"))), "New helper side effects inside the suppressed branch require review");
            var unrelated = MapCode(); unrelated.Insert(0, new(OpCodes.Nop)); unrelated.Add(new(OpCodes.Nop));
            Check(((IEnumerable<CodeInstruction>)Invoke("SuppressHarborBranch", unrelated)!).Count() == unrelated.Count + 1, "Unrelated instructions and branch offsets do not gate compatibility");
            CheckLiveHarborMap();
            Reset(); PathOfValheimanCompat.Shutdown();
            Check((bool)Invoke("KeepPovHarborMark", true)!, "Shutdown releases PoV rendering");
        }

        private static void CheckLiveHarborMap()
        {
            Reset(liveMap: true);
            Check(PathOfValheimanCompat.ValidateVehicleMapIntegration(), "PoV 6.0.7 live and distant ship paths install as one complete map handoff");
            Valheiman.Api.Stats.Harbor = true;
            Check(PathOfValheimanCompat.CanUseVehicleMap(Player.m_localPlayer) && !(bool)Invoke("KeepPovHarborMark", true)!, "Live variant still uses public Harbor Mark unlock and suppresses both PoV ship paths");
            var source = LiveMapCode();
            var rewritten = ((IEnumerable<CodeInstruction>)Invoke("SuppressHarborBranch", source)!).ToList();
            Check(rewritten.Count == source.Count + 1 && rewritten.Count(c => c.Calls(Method("KeepPovHarborMark"))) == 1 &&
                source.All(rewritten.Contains), "Live variant adds only the Harbor condition filter and retains original body and cleanup");
            Valheiman.Api.Stats.Harbor = false;
            Check(!PathOfValheimanCompat.CanUseVehicleMap(Player.m_localPlayer), "Live variant refunds and disabled talents revoke BottleShips map access");
            int Call(List<CodeInstruction> code, Type type, string name) => code.FindIndex(c => c.operand is MethodInfo m && m.DeclaringType == type && m.Name == name);
            Label FreshLabel(List<CodeInstruction> code)
            {
                var generator = new DynamicMethod("extra-label", typeof(void), Type.EmptyTypes).GetILGenerator();
                Label label;
                do { label = generator.DefineLabel(); } while (code.Any(c => c.labels.Contains(label)));
                return label;
            }
            void Reject(Action<List<CodeInstruction>> mutate, string reason)
            {
                Reset(liveMap: true);
                var bad = LiveMapCode(); mutate(bad);
                bool failed = false;
                try { Invoke("SuppressHarborBranch", bad); }
                catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { failed = true; }
                Check(failed && !bad.Any(c => c.Calls(Method("KeepPovHarborMark"))) &&
                    !PathOfValheimanCompat.CanUseVehicleMap(Player.m_localPlayer) && (bool)Invoke("KeepPovHarborMark", true)!, reason);
            }
            Reject(code => code[Call(code, typeof(Piece), "GetCreator") + 2].opcode = OpCodes.Beq_S, "Changed creator comparison refuses suppression and restores PoV");
            Reject(code => code[Call(code, typeof(Piece), "GetCreator") + 1].operand = 98, "Creator must be compared to this scan's player ID");
            Reject(code => code.First(c => c.opcode == OpCodes.Isinst).operand = typeof(Piece), "Live object must be a Ship from the live instance enumeration");
            Reject(code => code[Call(code, typeof(List<PassiveTree.MapSight.Far>.Enumerator), "get_Current") + 1].operand = 98,
                "Distant position and ID must come from the current Far record");
            Reject(code => code[Call(code, typeof(ZNetView), "IsValid") + 1].opcode = OpCodes.Brtrue_S, "Reversed validity check refuses suppression");
            Reject(code => code[Call(code, typeof(HashSet<string>), "Contains") + 1].opcode = OpCodes.Brfalse_S, "Reversed live-ID deduplication refuses suppression");
            Reject(code => code[Call(code, typeof(HashSet<string>), "Contains") - 3].operand = 98, "Distant deduplication must use the locally created live-ID set");
            Reject(code => code[Call(code, typeof(HashSet<string>), "Add") - 1].operand = 98, "Live set must record the displayed ZDO ID");
            Reject(code => code[Call(code, typeof(PassiveTree.MapSight), "Show") - 10].opcode = OpCodes.Ldc_I4_4, "Live Show may only render ship marks");
            Reject(code => code[Call(code, typeof(PassiveTree.MapSight), "Show") - 4].operand = "t:", "Live ship mark key cannot become a different feature");
            Reject(code => code[Call(code, typeof(PassiveTree.MapSight), "Show") - 3].operand = 98, "Live Show must use the recorded ZDO ID");
            Reject(code => code[8].operand = typeof(List<string>).GetConstructor(Type.EmptyTypes), "Only a local HashSet<string> allocation is accepted");
            Reject(code => code.Insert(8, new(OpCodes.Stsfld, typeof(PassiveTree.MapSight).GetField("seenNow"))), "Persistent state writes reject the live branch");
            Reject(code => code.Insert(8, new(OpCodes.Newarr, typeof(string))), "Unreviewed allocations reject the live branch");
            Reject(code => code.Insert(8, new(OpCodes.Call, Method("CaptureCratePayment"))), "New live-branch helpers require review");
            Reject(code =>
            {
                var target = FreshLabel(code);
                code[Call(code, typeof(PassiveTree.MapSight), "Show") - 11].labels.Add(target);
                code.Insert(Call(code, typeof(ZNetView), "IsValid") - 1, new(OpCodes.Br, target));
            }, "An additional internal jump cannot bypass the retained owner and validity checks");
            Reject(code => code.Insert(0, new(OpCodes.Br, code.First(c => c.labels.Count > 0).labels[0])), "A foreign entry into the suppressed branch is rejected");
            Reject(code => code.Insert(8, new(OpCodes.Br, FreshLabel(code))), "A jump out of the branch is rejected");
            Reject(code => code.Last(c => c.opcode == OpCodes.Leave_S).operand = code[Call(code, typeof(HashSet<string>), "Contains") + 1].operand,
                "The distant loop's leave must reach the shared map continuation rather than reenter its own loop");
            Reject(code => code.First(c => c.blocks.Count > 0).blocks.Clear(), "A partial exception region is rejected");
            Reset(liveMap: true);
            var aliases = LiveMapCode();
            var boundary = (Label)aliases[5].operand!;
            var alias = FreshLabel(aliases);
            aliases.Single(c => c.labels.Contains(boundary)).labels.Add(alias);
            aliases.Last(c => c.opcode == OpCodes.Leave_S).operand = alias;
            Check(((IEnumerable<CodeInstruction>)Invoke("SuppressHarborBranch", aliases)!).Count() == aliases.Count + 1,
                "Distinct Harmony labels resolving to the same branch-end instruction are accepted");
            var shifted = LiveMapCode(); shifted.Insert(0, new(OpCodes.Nop)); shifted.Add(new(OpCodes.Nop));
            foreach (var instruction in shifted)
                if (instruction.opcode == OpCodes.Ldloc_S || instruction.opcode == OpCodes.Stloc_S || instruction.opcode == OpCodes.Ldloca_S)
                    instruction.operand = (int)instruction.operand! + 10;
            Check(((IEnumerable<CodeInstruction>)Invoke("SuppressHarborBranch", shifted)!).Count() == shifted.Count + 1,
                "Unrelated method edits, offsets and consistent local renumbering do not block the reviewed live contract");
        }

        private static void CheckCrates()
        {
            CultureInfo before = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                foreach (bool free in new[] { false, true })
                foreach (float paid in new[] { 1f, 0.75f, 0.5123457f })
                {
                    var source = ShipPiece(out var zdo); zdo.Set("PT_BuildSaved", free); zdo.Set("PT_BuildPaidFactor", paid);
                    var crate = new ItemDrop.ItemData();
                    Check(ReferenceEquals(crate, Invoke("CaptureCratePayment", crate, source)), "Capture preserves the exact item object");
                    Check(crate.m_customData![Saved] == (free ? "1" : "0") && !crate.m_customData[Factor].Contains(','), "Payment metadata uses invariant serialization");
                    crate.m_customData["pt.pack.ship"] = "Karve";
                    var target = ShipPiece(out var restored); Restore(target, crate);
                    Check(restored.GetBool("PT_BuildSaved", !free) == free && restored.GetFloat("PT_BuildPaidFactor", -1) == paid, "Paid/free crate roundtrip preserves both fields exactly");
                }
            }
            finally { CultureInfo.CurrentCulture = before; }
            var defaults = ShipPiece(out var original); var clean = new ItemDrop.ItemData();
            Invoke("CaptureCratePayment", clean, defaults);
            Check(clean.m_customData![Saved] == "0" && clean.m_customData[Factor] == "1", "Unmarked source defaults to fully paid, not free");
            foreach (var invalid in new[] { Crate(null, null), Crate(null, "1"), Crate("1", null), Crate("true"), Crate("0", "NaN"), Crate("0", "Infinity"), Crate("0", "0,5"), Crate("0", "garbage") })
            {
                var piece = ShipPiece(out var state); state.Set("PT_BuildSaved", true); state.Set("PT_BuildPaidFactor", 0.6f);
                Restore(piece, invalid);
                Check(state.GetBool("PT_BuildSaved", false) && state.GetFloat("PT_BuildPaidFactor", -1) == 0.6f, "Legacy or invalid metadata leaves existing state untouched");
            }
            var wrongPrefab = Crate(); wrongPrefab.m_customData!["pt.pack.ship"] = "Raft";
            var other = ShipPiece(out var otherState); Restore(other, wrongPrefab);
            Check(otherState.Data.Count == 0, "Crate cannot restore payment on a different prefab");
            var owner = ShipPiece(out var owned); owner.GetComponent<ZNetView>().Owner = false; Restore(owner, Crate("1", "0.75"));
            Check(owned.Data.Count == 0, "Nonowner cannot restore crate payment");
            owner.GetComponent<ZNetView>().Owner = true; owner.GetComponent<ZNetView>().Valid = false; Restore(owner, Crate("1", "0.75"));
            Check(owned.Data.Count == 0, "Invalid view cannot restore crate payment");
            var nonship = ShipPiece(out var nonshipState); nonship.gameObject.Components.Remove(typeof(Ship)); Restore(nonship, Crate("1", "0.75"));
            Check(nonshipState.Data.Count == 0, "Payment restoration excludes portals and other pieces");
            Set("_placingCrate", (Func<ItemDrop.ItemData?>)(() => throw new InvalidOperationException("Crate getter failed")));
            Invoke("RestoreCratePayment", other);
            Check(otherState.Data.Count == 0, "Restore boundary catches exceptions so original crate consumption can continue");
            bool failed = false;
            try { Invoke("CaptureCratePayment", new ItemDrop.ItemData(), new Piece()); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { failed = true; }
            Check(failed, "Missing source ZDO fails capture before clearing cargo or creating a packed ship");
            var ship = other.GetComponent<Ship>(); ship.m_nview.Data.Set("pt.anchor", true);
            Check(PathOfValheimanCompat.IsAnchored(ship), "Anchor reads current ship ZDO");
            ship.m_nview.Data.Set("pt.anchor", false);
            Check(!PathOfValheimanCompat.IsAnchored(ship), "Anchor release is observed without cached state");
        }

        private static void CheckTranspiler()
        {
            var source = PackCode(); var label = new DynamicMethod("label", typeof(void), Type.EmptyTypes).GetILGenerator().DefineLabel();
            source[4].labels.Add(label);
            var rewritten = Rewrite(source);
            Check(rewritten.Count == source.Count + 2 && rewritten[4].opcode == OpCodes.Ldarg_1 && rewritten[5].Calls(Method("CaptureCratePayment")), "Capture is inserted directly after clone, before local store and cargo clearing");
            Check(rewritten[4].labels.Contains(label) && rewritten[6].labels.Count == 0, "Branch labels transfer to the injected sequence");
            Check(rewritten.Count(c => c.Calls(Method("CaptureCratePayment"))) == 1, "One successful transformation inserts one capture");
            foreach (int missing in new[] { 0, 3, 6, 7 })
            {
                var code = PackCode(); code.RemoveAt(missing); Rejected(code, "Missing required call rejects the entire transformation");
                code = PackCode(); code.Add(code[missing]); Rejected(code, "Duplicate required call rejects the entire transformation");
            }
            var wrongOrder = PackCode(); (wrongOrder[0], wrongOrder[6]) = (wrongOrder[6], wrongOrder[0]);
            Rejected(wrongOrder, "Cargo clear before clone rejects unsafe insertion order");
            var noStore = PackCode(); noStore[4] = new(OpCodes.Nop);
            Rejected(noStore, "Unknown clone result flow rejects unsafe insertion");
            var exceptionBlock = PackCode(); exceptionBlock[4].blocks.Add(new(ExceptionBlockType.BeginExceptionBlock));
            Rejected(exceptionBlock, "Unrecognized exception layout rejects unsafe insertion");
        }
    }
}

namespace PassiveTree
{
    using BottleShipsCompatChecks;
    public sealed class Plugin { }
    public enum Stat { ShipRepairAnywhere, DungeonSight = 635, HarborMark = 645 }
    public sealed class StatSet { public float Value; public bool Has(Stat stat) => Value > 0; }
    public sealed class Progress { public static Progress? Current { get; set; } public StatSet? Stats = new(); }
    public static class Conveniences
    {
        private static class Player_Repair_Mass { private static void Postfix(Player player, ItemDrop.ItemData tool, Piece piece) { } }
    }
    public static class ShipCrate
    {
        private static ItemDrop.ItemData placing = null!;
        internal static ref ItemDrop.ItemData ReadPlacing() => ref placing;
        public static void SetPlacing(ItemDrop.ItemData? item) => placing = item!;
        private static bool Pack(Player player, Piece piece, Ship ship) => true;
        private static class Created { private static void Postfix(Piece piece) { } }
    }
    public static class MapSight
    {
        public enum Kind { Ship = 5 }
        public sealed class Far { public bool Ship; public Vector3 Pos; public string Id = ""; }
        public sealed class Mark { }
        public static HashSet<string> seenNow = new();
        public static List<Far> far = new();
        public static void Scan(Minimap map, Player player, StatSet stats) { }
        public static void AskFar(Player player) { }
        public static Sprite ShipIcon() => new();
        public static void Show(Minimap map, Kind kind, Vector3 pos, Sprite icon, bool spent, string key, string name) { }
    }
}

namespace Valheiman.Api
{
    public static class Pov
    {
        public static bool Compatible = true;
        public static bool AtLeast(int major, int minor) => Compatible && major == 1 && minor <= 6;
        public static bool Has(string feature) => Compatible && feature == "stats";
    }
    public static class Stats
    {
        public static bool Harbor, Throw;
        public static bool Has(string stat) => Throw ? throw new InvalidOperationException("API unavailable") : stat == "HarborMark" && Harbor;
    }
}

/* PRODUCTION_SOURCE */

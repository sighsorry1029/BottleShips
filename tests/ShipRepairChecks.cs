#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace BottleShipsRepairChecks
{
    // Only dependencies are simulated: the runner compiles ShipRepairManager.cs in full.
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class HarmonyPatch : Attribute { public HarmonyPatch(Type type, string method) { } }
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class HarmonyPriority : Attribute { public HarmonyPriority(int priority) { } }
    public static class Priority { public const int First = 800, Last = 0; }
    public static class AccessTools
    {
        public delegate ref F FieldRef<T, F>(T instance);
        private static ref ZNetView View(WearNTear wear) => ref wear.m_nview;
        private static ref float RepairTime(WearNTear wear) => ref wear.m_lastRepair;
        public static FieldRef<T, F> FieldRefAccess<T, F>(string field) => field switch
        {
            "m_nview" => (FieldRef<T, F>)(object)(FieldRef<WearNTear, ZNetView>)View,
            "m_lastRepair" => (FieldRef<T, F>)(object)(FieldRef<WearNTear, float>)RepairTime,
            _ => throw new ArgumentException(field)
        };
    }
    public sealed class ConfigDescription { public ConfigDescription(string description, object? range = null) { } }
    public sealed class AcceptableValueRange<T> { public AcceptableValueRange(T min, T max) { } }
    public sealed class ConfigEntry<T>
    {
        private T _value;
        public ConfigEntry(T value) { _value = value; }
        public event EventHandler? SettingChanged;
        public int Subscribers => SettingChanged?.GetInvocationList().Length ?? 0;
        public T Value { get => _value; set { _value = value; SettingChanged?.Invoke(this, EventArgs.Empty); } }
    }
    public sealed class BottleShipsPlugin
    {
        public readonly Dictionary<string, object> Config = new();
        public readonly Dictionary<string, bool> Synced = new();
        public static readonly Logger BottleShipsLogger = new();
        public ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description,
            bool synchronizedSetting = true, int? order = null)
        {
            if (!Config.TryGetValue(name, out object? entry)) Config[name] = entry = new ConfigEntry<T>(value);
            Synced[name] = synchronizedSetting;
            return (ConfigEntry<T>)entry;
        }
        public ConfigEntry<T> config<T>(string group, string name, T value, string description,
            bool synchronizedSetting = true, int? order = null) => config(group, name, value, new ConfigDescription(description), synchronizedSetting, order);
        public ConfigEntry<T> Get<T>(string name) => (ConfigEntry<T>)Config[name];
    }
    public sealed class Logger { public void LogError(object message) { } public void LogWarning(object message) { } }
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 zero => default;
    }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 one => new(1, 1, 1);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
    }
    public struct Quaternion { public static Quaternion identity => default; }
    public static class Mathf
    {
        public static float Max(float a, float b) => Math.Max(a, b);
        public static int Max(int a, int b) => Math.Max(a, b);
        public static int CeilToInt(float value) => (int)Math.Ceiling(value);
        public static float Clamp01(float value) => Math.Min(1, Math.Max(0, value));
    }
    public class GameObject
    {
        public string name = "";
        public bool activeSelf = true, Destroyed;
        public Transform transform;
        public readonly Dictionary<Type, object> Components = new();
        public GameObject() { transform = new RectTransform { gameObject = this }; }
        public T? GetComponent<T>() where T : class => Components.TryGetValue(typeof(T), out object? value) ? (T)value : null;
        public bool TryGetComponent<T>(out T component) where T : class { component = GetComponent<T>()!; return component != null; }
        public T? GetComponentInChildren<T>() where T : class => GetComponent<T>();
        public void SetActive(bool active) { activeSelf = active; }
    }
    public class Component
    {
        public GameObject gameObject = new();
        public Transform transform => gameObject.transform;
        public bool TryGetComponent<T>(out T component) where T : class => gameObject.TryGetComponent(out component);
        public T? GetComponentInChildren<T>() where T : class => gameObject.GetComponentInChildren<T>();
    }
    public class Transform
    {
        public GameObject gameObject = null!;
        public Transform parent = null!;
        public Vector3 position, localPosition, localScale;
        public Quaternion localRotation;
        public readonly Dictionary<string, Transform> Children = new();
        public Transform? Find(string name) => Children.TryGetValue(name, out Transform? child) ? child : null;
        public bool TryGetComponent<T>(out T component) where T : class => gameObject.TryGetComponent(out component);
        public void SetParent(Transform value, bool worldPositionStays) { parent = value; }
        public void SetAsLastSibling() { }
    }
    public sealed class RectTransform : Transform { public Vector2 sizeDelta, anchorMin, anchorMax, anchoredPosition, pivot; }
    public static class UnityObject
    {
        public static int Created;
        public static void Destroy(GameObject obj) { obj.Destroyed = true; }
        public static GameObject Instantiate(GameObject original, Transform parent, bool worldPositionStays)
        {
            Created++;
            GameObject result = new(); result.transform.parent = parent;
            result.Components[typeof(UITooltip)] = new UITooltip();
            foreach (string name in new[] { "res_icon", "res_amount", "res_name" })
            {
                GameObject child = new(); child.transform.parent = result.transform;
                child.Components[typeof(Image)] = new Image(); child.Components[typeof(TMP_Text)] = new TMP_Text();
                result.transform.Children[name] = child.transform;
            }
            return result;
        }
    }
    public sealed class Image { public bool preserveAspect, raycastTarget; }
    public enum FontStyles { Normal, Bold }
    public enum TextAlignmentOptions { Center }
    public sealed class TMP_Text { public TextAlignmentOptions alignment; public FontStyles fontStyle; public bool enableAutoSizing, raycastTarget; public float fontSizeMin, fontSizeMax; }
    public sealed class UITooltip { }
    public sealed class Hud
    {
        public GameObject m_pieceHealthRoot = new();
        public GameObject[] m_requirementItems = { new GameObject() };
        public Hud() { m_pieceHealthRoot.transform.parent = new GameObject().transform; }
    }
    public static class InventoryGui
    {
        public static int Calls;
        public static bool SetupRequirement(Transform widget, Piece.Requirement requirement, Player player, bool craft, int quality) { Calls++; return true; }
    }
    public sealed class Ship : Component { }
    public sealed class Piece : Component
    {
        public bool m_repairPiece;
        public CraftingStation? m_craftingStation;
        public string FreeBuildKey() => "FreeShip";
        public sealed class Requirement { public ItemDrop? m_resItem; public int m_amount, m_amountPerLevel; public bool m_recover; }
    }
    public sealed class ItemDrop : Component
    {
        public ItemData m_itemData = new();
        public sealed class ItemData { public Shared m_shared = new(); }
        public sealed class Shared { public string m_name = "$item_wood"; }
    }
    public sealed class ObjectDB
    {
        public static ObjectDB instance = new();
        public GameObject Wood = new();
        public ObjectDB() { Wood.Components[typeof(ItemDrop)] = new ItemDrop(); }
        public GameObject? GetItemPrefab(string name) => name == "Wood" ? Wood : null;
    }
    public sealed class CraftingStation
    {
        public string m_name = "$piece_workbench";
        public static Func<string, Vector3, CraftingStation?> InRange = (_, _) => null;
        public static CraftingStation? HaveBuildStationInRange(string name, Vector3 position) => InRange(name, position);
    }
    public sealed class ZNetScene
    {
        public static ZNetScene instance = new();
        public GameObject Workbench = new();
        public ZNetScene() { Workbench.Components[typeof(CraftingStation)] = new CraftingStation(); }
        public GameObject? GetPrefab(string name) => name == "piece_workbench" ? Workbench : null;
    }
    public static class ZDOVars { public const int s_health = 1; }
    public sealed class ZDO
    {
        public int Prefab; public float Health = 300;
        public int GetPrefab() => Prefab;
        public float GetFloat(int key, float fallback) => Health;
    }
    public sealed class ZNetView
    {
        public readonly ZDO Data = new(); public bool Valid = true, Owner = true;
        public bool IsValid() => Valid;
        public bool IsOwner() => Owner;
        public ZDO GetZDO() => Data;
    }
    public sealed class WearNTear : Component
    {
        public ZNetView m_nview = new(); public float m_lastRepair, m_health = 1000;
        public float GetHealthPercentage() => m_nview.Data.Health / m_health;
        public bool Repair() => throw new NotSupportedException("Tests do not simulate the game repair RPC.");
    }
    public sealed class ZoneSystem
    {
        public static ZoneSystem instance = new();
        public readonly HashSet<string> Keys = new();
        public bool GetGlobalKey(string key) => Keys.Contains(key);
    }
    public static class GlobalKeys { public const string NoWorkbench = "NoWorkbench"; }
    public sealed class WorldGenerator
    {
        public static WorldGenerator? instance = new();
        public static Func<Vector3, float> Gradient = _ => -1;
        public static int Calls;
        public static Vector3 LastPosition;
        public static float GetAshlandsOceanGradient(Vector3 position) { Calls++; LastPosition = position; return Gradient(position); }
    }
    public sealed class Inventory
    {
        public int Wood = 100, Consumed;
        public int CountItems(string name) => Wood;
        public void RemoveItem(string name, int count) { Wood -= count; Consumed += count; }
    }
    public static class MessageHud { public enum MessageType { Center } }
    public sealed class Player : Component
    {
        public static Player? m_localPlayer;
        public bool NoCost;
        public Piece? Hovering;
        public readonly Piece Selected = new() { m_repairPiece = true };
        public readonly Inventory Inventory = new();
        public readonly List<string> Messages = new();
        public Piece? GetHoveringPiece() => Hovering;
        public Piece GetSelectedPiece() => Selected;
        public Inventory GetInventory() => Inventory;
        public bool NoCostCheat() => NoCost;
        public void Message(MessageHud.MessageType type, string message, bool log) { Messages.Add(message); }
    }
    public static class StableHash
    {
        public static int GetStableHashCode(this string value)
        {
            unchecked
            {
                int first = 5381, second = first;
                for (int i = 0; i < value.Length && value[i] != 0; i += 2)
                {
                    first = ((first << 5) + first) ^ value[i];
                    if (i == value.Length - 1 || value[i + 1] == 0) break;
                    second = ((second << 5) + second) ^ value[i + 1];
                }
                return first + second * 1566083941;
            }
        }
    }
    public static class Checks
    {
        private const string ListKey = "Ashlands Field Repair Prefabs";
        private const string HealthKey = "Ship Repair Durability Per Wood";
        private static int _checks;
        private static BottleShipsPlugin _plugin = null!;
        private static Player _player = null!;
        private static Piece _piece = null!;
        private static WearNTear _wear = null!;
        private static object? ReadStatic(string field) => typeof(ShipRepairManager).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);
        private static void Check(bool condition, string label) { if (!condition) throw new Exception(label); _checks++; }
        private static void Reset(string prefab = "Karve", float gradient = 1)
        {
            ShipRepairManager.Shutdown();
            _plugin = new BottleShipsPlugin(); ShipRepairManager.BindConfig(_plugin);
            ZoneSystem.instance = new(); WorldGenerator.instance = new(); WorldGenerator.Gradient = _ => gradient; WorldGenerator.Calls = 0;
            ObjectDB.instance = new(); ZNetScene.instance = new(); CraftingStation.InRange = (_, _) => null;
            UnityObject.Created = InventoryGui.Calls = 0;
            _piece = new Piece(); _wear = new WearNTear { gameObject = _piece.gameObject };
            _piece.gameObject.Components[typeof(Ship)] = new Ship { gameObject = _piece.gameObject }; _piece.gameObject.Components[typeof(WearNTear)] = _wear;
            _wear.m_nview.Data.Prefab = prefab.GetStableHashCode();
            _player = new Player { Hovering = _piece }; Player.m_localPlayer = _player;
        }
        private static void Allowed(string label, bool field = true)
        {
            Check(ShipRepairManager.BeginRepair(_player, out var attempt), label);
            Check((attempt != null) == field, label + ": correct repair path");
            ShipRepairManager.FinishRepair(ref attempt);
            Check(ReadStatic("_activeRepair") == null && _player.Inventory.Consumed == 0, label + ": no unaccepted charge");
        }
        private static void Denied(string label)
        {
            Check(!ShipRepairManager.BeginRepair(_player, out var attempt) && attempt == null, label);
            Check(ReadStatic("_activeRepair") == null, label + ": no station bypass state");
            ShipRepairManager.FinishRepair(ref attempt);
            bool station = false;
            Check(!ShipRepairManager.TryBypassStationCheck(_player, _piece, ref station) && !station, label + ": station check not bypassed");
            Check(_player.Inventory.Consumed == 0 && _player.Inventory.Wood == 100, label + ": wood preserved");
            Check(_player.Messages.Last() == "$sighsorry_bottleships_ship_repair_boiling_blocked", label + ": explicit rejection");
        }
        public static string Run()
        {
            Reset(); Denied("Karve denied in boiling water");
            Check(_plugin.Get<string>(ListKey).Value == "VikingShip_Ashlands" && _plugin.Synced[ListKey], "Drakkar default uses server-synced setting");
            Reset("VikingShip_Ashlands"); Allowed("Default Drakkar allowed");
            Check(WorldGenerator.Calls == 0, "Whitelisted prefab avoids biome query");
            Reset(gradient: -0.0001f); Allowed("Cold side of boundary allowed");
            Reset(gradient: 0); Denied("Exact zero boiling boundary denied");
            Reset(gradient: 0.0001f); Denied("Positive boiling boundary denied");
            Reset(); _plugin.Get<string>(ListKey).Value = "  Karve , VikingShip_Ashlands,Karve, , "; Allowed("Whitespace and duplicate list entries accepted");
            Check(((HashSet<int>)ReadStatic("AshlandsFieldRepairPrefabs")!).Count == 2, "Duplicates and empty entries removed");
            _plugin.Get<string>(ListKey).Value = "karve"; Denied("Prefab matching is case-sensitive");
            _plugin.Get<string>(ListKey).Value = ""; Denied("Empty list denies every field repair in boiling water");
            _plugin.Get<string>(ListKey).Value = "Karve"; Allowed("Live list changes restore permission");
            Reset(); _piece.m_craftingStation = new CraftingStation(); CraftingStation.InRange = (_, _) => new CraftingStation();
            Allowed("Required station preserves ordinary repair", field: false); Check(WorldGenerator.Calls == 0, "Station repair does not query boiling state");
            Reset(); CraftingStation.InRange = (_, _) => new CraftingStation(); Allowed("Build Station=None workbench fallback preserved", field: false);
            Reset(); Denied("Build Station=None outside fallback explicitly denied");
            Reset(); _piece.transform.position = new Vector3(10, 0, 0); _player.transform.position = new Vector3(-10, 0, 0);
            WorldGenerator.Gradient = p => p.x; Denied("Boat boiling and player cold uses boat position");
            Check(WorldGenerator.LastPosition.x == 10, "Gradient queried at boat position");
            _piece.transform.position = new Vector3(-10, 0, 0); _player.transform.position = new Vector3(10, 0, 0);
            Allowed("Boat cold and player boiling still allowed");
            Ship childShip = new(); childShip.transform.position = new Vector3(15, 0, 0);
            _piece.gameObject.Components[typeof(Ship)] = childShip;
            Denied("Child Ship position overrides cold Piece root");
            Reset(); _piece.transform.position = new Vector3(10, 0, 0); _player.transform.position = new Vector3(-10, 0, 0);
            CraftingStation.InRange = (_, p) => p.x < 0 ? new CraftingStation() : null;
            Allowed("Station range still uses player position", field: false);
            Reset(); _player.NoCost = true; Allowed("No-cost cheat exception preserved", field: false);
            Reset(); ZoneSystem.instance.Keys.Add(GlobalKeys.NoWorkbench); Allowed("No-workbench exception preserved", field: false);
            Reset(); _plugin.Get<int>(HealthKey).Value = 0; Allowed("Disabled field repair preserves vanilla", field: false);
            Reset(); WorldGenerator.instance = null; Allowed("Missing world generator preserves field behavior");
            Reset(); _wear.m_nview.Owner = false; Denied("Remote-owned ship also denied before repair request");
            Reset(); var hud = new Hud(); ShipRepairManager.UpdateRequirementWidget(hud, _player);
            Check(UnityObject.Created == 0 && InventoryGui.Calls == 0, "Denied ship creates no requirement UI");
            _plugin.Get<string>(ListKey).Value = "Karve"; ShipRepairManager.UpdateRequirementWidget(hud, _player);
            var widget = (GameObject)ReadStatic("_requirementWidget")!;
            Check(widget.activeSelf && UnityObject.Created == 1 && InventoryGui.Calls == 1, "Allowed ship shows wood requirement");
            _plugin.Get<string>(ListKey).Value = "VikingShip_Ashlands";
            Check(!widget.activeSelf, "Live list update immediately hides old cost");
            ShipRepairManager.UpdateRequirementWidget(hud, _player);
            Check(!widget.activeSelf && UnityObject.Created == 1 && InventoryGui.Calls == 1, "Denied update stays hidden without rebuilding UI");
            Reset("VikingShip_Ashlands"); Check(ShipRepairManager.BeginRepair(_player, out var accepted) && accepted != null, "Allowed field repair creates attempt");
            bool bypass = false; Check(ShipRepairManager.TryBypassStationCheck(_player, _piece, ref bypass) && bypass, "Allowed attempt bypasses its own station check");
            _wear.m_lastRepair++; ShipRepairManager.RecordRepairResult(_wear, true);
            ShipRepairManager.FinishRepair(ref accepted); ShipRepairManager.FinishRepair(ref accepted);
            Check(_player.Inventory.Consumed == 4 && ReadStatic("_activeRepair") == null, "Accepted repair retains existing rounded cost and consumes once");
            var prefabs = _plugin.Get<string>(ListKey); var health = _plugin.Get<int>(HealthKey);
            ShipRepairManager.BindConfig(_plugin);
            Check(prefabs.Subscribers == 1 && health.Subscribers == 1, "Rebinding does not duplicate config handlers");
            ShipRepairManager.Shutdown();
            Check(prefabs.Subscribers == 0 && health.Subscribers == 0, "Shutdown removes both config handlers");
            prefabs.Value = "Karve";
            Check(((HashSet<int>)ReadStatic("AshlandsFieldRepairPrefabs")!).Count == 0, "Detached config no longer mutates cleared state");
            return $"{_checks} ship repair checks passed (complete production feature; game, Unity, Harmony and config boundaries are test doubles; no actual game execution).";
        }
    }
}

/* PRODUCTION_SOURCE */

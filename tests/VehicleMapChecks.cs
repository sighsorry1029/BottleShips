#nullable enable annotations
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Object = BottleShipsVehicleMapChecks.Object;

namespace BottleShipsVehicleMapChecks
{
    // Controlled boundaries only. The runner compiles both complete production feature files.
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class HarmonyPatch : Attribute { public HarmonyPatch(Type t, string name) { } }
    public class Object
    {
        public static GameObject Instantiate(GameObject template, Transform parent, bool world)
        {
            var copy = new GameObject(); copy.transform.parent = parent;
            copy.transform.Children["Label"] = new GameObject().transform;
            copy.transform.Children["Label"].gameObject.Components[typeof(TMP_Text)] = new TMP_Text();
            return copy;
        }
        public static void Destroy(GameObject obj) { obj.Destroyed = true; }
    }
    public class GameObject
    {
        public string name = "";
        public bool activeSelf = true, activeInHierarchy = true, Destroyed;
        public Transform transform;
        public readonly Dictionary<Type, object> Components = new();
        public GameObject() { transform = new Transform { gameObject = this }; }
        public T? GetComponent<T>() where T : class => Components.TryGetValue(typeof(T), out var value) ? (T)value : null;
        public T? GetComponentInChildren<T>(bool includeInactive) where T : class => GetComponent<T>();
        public void SetActive(bool value) { activeSelf = activeInHierarchy = value; }
    }
    public class Transform
    {
        public GameObject gameObject = null!;
        public Transform? parent;
        public readonly Dictionary<string, Transform> Children = new();
        public Transform? Find(string path) => Children.TryGetValue(path, out var child) ? child : null;
        public T? GetComponent<T>() where T : class => gameObject.GetComponent<T>();
        public int GetSiblingIndex() => 0;
        public void SetSiblingIndex(int index) { }
    }
    public class RectTransform : Transform { }
    public class TMP_Text { public string text = ""; public bool richText; }
    public static class LayoutRebuilder { public static void MarkLayoutForRebuild(RectTransform t) { } }
    public class Sprite { }
    public class Ship { }
    public class Vagon { }
    public class Piece { public Sprite? m_icon = new(); public string m_name = "Boat"; }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    }
    public struct ZDOID
    {
        public long UserID; public uint ID;
        public ZDOID(long user, uint id) { UserID = user; ID = id; }
        public bool IsNone() => UserID == 0 && ID == 0;
    }
    public static class ZDOVars { public const int s_creator = 1; public const int s_playerID = 2; }
    public class ZDO
    {
        public ZDOID m_uid;
        public long Creator, Owner;
        public int Prefab;
        public Vector3 Position;
        public bool Valid = true;
        public bool IsValid() => Valid;
        public int GetPrefab() => Prefab;
        public long PlayerId;
        public long GetLong(int key, long fallback) => key == ZDOVars.s_playerID ? PlayerId : Creator;
        public long GetOwner() => Owner;
        public Vector3 GetPosition() => Position;
    }
    public class ZDOMan
    {
        public static ZDOMan instance = new();
        public readonly Dictionary<ZDOID, ZDO> Objects = new();
        public int Scans;
        public bool ThrowOnScan;
        public ZDO? GetZDO(ZDOID id) => Objects.TryGetValue(id, out var zdo) ? zdo : null;
        public bool GetAllZDOsWithPrefabIterative(string prefab, List<ZDO> result, ref int index)
        {
            Scans++;
            if (ThrowOnScan) throw new InvalidOperationException("Injected failure");
            result.AddRange(Objects.Values.Where(z => z.Prefab == prefab.GetStableHashCode()));
            return index++ > 0; // Repeat results across batches, like the game's sector iterator can.
        }
    }
    public class ZNetScene
    {
        public static ZNetScene instance = new();
        public readonly List<GameObject> m_prefabs = new();
        public GameObject? GetPrefab(int hash) => m_prefabs.FirstOrDefault(p => p.name.GetStableHashCode() == hash);
    }
    public class Coroutine { public IEnumerator Steps = null!; }
    public class Game
    {
        public static Game instance = new();
        public readonly List<Coroutine> Routines = new();
        public Coroutine StartCoroutine(IEnumerator steps)
        {
            var routine = new Coroutine { Steps = steps };
            if (steps.MoveNext()) Routines.Add(routine);
            return routine;
        }
        public void StopCoroutine(Coroutine routine) { Routines.Remove(routine); }
        public void Drain()
        {
            for (int frame = 0; Routines.Count > 0 && frame < 100; frame++)
            {
                Time.realtimeSinceStartup += 0.02f;
                foreach (var routine in Routines.ToArray())
                    if (!routine.Steps.MoveNext()) Routines.Remove(routine);
            }
            if (Routines.Count > 0) throw new Exception("Unbounded scan");
        }
    }
    public static class Time { public static float realtimeSinceStartup; }
    public class ZNet
    {
        public static ZNet instance = new();
        public bool Server = true;
        public ZRpc? ServerRpc;
        public readonly List<ZNetPeer> Peers = new();
        public bool IsServer() => Server;
        public List<ZNetPeer> GetPeers() => Peers;
        public void Disconnect(ZNetPeer peer) { Peers.Remove(peer); }
        public ZRpc? GetServerRPC() => ServerRpc;
    }
    public class ZNetPeer
    {
        public ZRpc m_rpc = new(); public long m_uid = 100; public ZDOID m_characterID;
        public bool Ready = true; public bool IsReady() => Ready;
        public long PlayerId { get => ZDOMan.instance.GetZDO(m_characterID)!.PlayerId;
            set => ZDOMan.instance.GetZDO(m_characterID)!.PlayerId = value; }
    }
    public class ZRpc
    {
        public readonly Dictionary<string, Action<ZRpc, ZPackage>> Handlers = new();
        public readonly List<(string Name, ZPackage Package)> Sent = new();
        public void Register<T>(string name, Action<ZRpc, T> handler) =>
            Handlers[name] = (rpc, pkg) => handler(rpc, (T)(object)pkg);
        public void Invoke(string name, ZPackage package) { Sent.Add((name, package.Copy())); }
    }
    public class ZPackage
    {
        private readonly MemoryStream stream;
        private readonly BinaryReader reader;
        private readonly BinaryWriter writer;
        public ZPackage(byte[]? bytes = null)
        {
            stream = new MemoryStream();
            if (bytes != null) stream.Write(bytes, 0, bytes.Length);
            stream.Position = 0;
            reader = new BinaryReader(stream); writer = new BinaryWriter(stream);
        }
        public ZPackage Copy() => new(stream.ToArray());
        public byte[] Bytes() => stream.ToArray();
        public int Size() => (int)stream.Length;
        public int GetPos() => (int)stream.Position;
        public void Write(long value) => writer.Write(value);
        public void Write(int value) => writer.Write(value);
        public void Write(ZDOID value) { writer.Write(value.UserID); writer.Write(value.ID); }
        public void Write(Vector3 value) { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); }
        public long ReadLong() => reader.ReadInt64();
        public int ReadInt() => reader.ReadInt32();
        public ZDOID ReadZDOID() => new(reader.ReadInt64(), reader.ReadUInt32());
        public Vector3 ReadVector3() => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }
    public class Player
    {
        public static Player? m_localPlayer = new();
        public long Id = 42;
        public readonly List<string> Messages = new();
        public long GetPlayerID() => Id;
        public T? GetComponent<T>() where T : class => new PlayerController() as T;
        public void Message(MessageHud.MessageType type, string text) => Messages.Add(text);
    }
    public static class MessageHud { public enum MessageType { Center } }
    public class Minimap
    {
        public enum MapMode { None, Small, Large }
        public enum PinType { Icon3, None }
        public static Minimap instance = new();
        public MapMode m_mode = MapMode.Small;
        public GameObject m_largeRoot = new();
        public class PinData { public Vector3 Position; public bool Saved; public Sprite? m_icon; }
        public readonly List<PinData> Pins = new();
        public int Adds;
        public PinData AddPin(Vector3 pos, PinType type, string name, bool save, bool isChecked)
        { var pin = new PinData { Position = pos, Saved = save }; Pins.Add(pin); Adds++; return pin; }
        public void RemovePin(PinData pin) => Pins.Remove(pin);
        public void SetMapMode(MapMode mode) { m_mode = mode; }
    }
    public class Localization
    {
        public static Localization instance = new();
        public string Language = "English";
        public string Localize(string value) => value.Contains("hint_") ? value + "[{0}]" : value;
        public string GetSelectedLanguage() => Language;
    }
    public static class PlatformPrefs { public static int Hints = 1; public static int GetInt(string key, int fallback) => Hints; }
    public class ConfigEntry<T>
    {
        private T stored;
        public event EventHandler? SettingChanged;
        public int Saves;
        public int Subscribers => SettingChanged?.GetInvocationList().Length ?? 0;
        public ConfigEntry(T value) { stored = value; }
        public T Value { get => stored; set { if (EqualityComparer<T>.Default.Equals(stored, value)) return;
            stored = value; Saves++;
            SettingChanged?.Invoke(this, EventArgs.Empty); } }
    }
    public class BottleShipsPlugin
    {
        public const string ModGUID = "sighsorry.BottleShips";
        public readonly List<(string Group, string Name, string Category, bool Synced, int Order)> Bindings = new();
        public ConfigEntry<T> config<T>(string group, string name, T value, string description, bool synchronizedSetting = true, int order = 0)
        {
            Bindings.Add((group, name, GetConfigurationManagerCategory(group), synchronizedSetting, order));
            return new(value);
        }
        /* CONFIG_CATEGORY */
        public enum Toggle { Off, On }
        public class Logger { public void LogWarning(string message) { } }
        public static Logger BottleShipsLogger = new();
    }
    public enum KeyCode { None, V, B, LeftShift, RightShift, LeftControl, RightControl, LeftAlt, RightAlt }
    public struct KeyboardShortcut
    {
        public KeyCode MainKey;
        public IEnumerable<KeyCode> Modifiers => Array.Empty<KeyCode>();
        public KeyboardShortcut(KeyCode key) { MainKey = key; }
        public bool IsKeyDown() { bool down = Keys.Down; Keys.Down = false; return MainKey != KeyCode.None && down; }
    }
    public static class Keys { public static bool Down; }
    public static class Input
    {
        public static bool GetKeyDown(KeyCode key) { bool down = Keys.Down; Keys.Down = false; return down; }
        public static bool GetKey(KeyCode key) => true;
    }
    public class PlayerController
    {
        public static bool InputAllowed = true;
        public bool TakeInput(bool map) => InputAllowed;
    }
    public static class AccessTools
    {
        public static MethodInfo DeclaredMethod(Type type, string name, Type[] arguments) => type.GetMethod(name, arguments)!;
        public static T MethodDelegate<T>(MethodInfo method) where T : Delegate => (T)method.CreateDelegate(typeof(T));
    }
    public static class Extensions
    {
        public static string RemoveRichTextTags(this string text) => text;
        public static bool IsOn(this BottleShipsPlugin.Toggle toggle) => toggle == BottleShipsPlugin.Toggle.On;
        public static int GetStableHashCode(this string text) { int result = 0; foreach (char c in text) result = unchecked(result * 31 + c); return result; }
    }

    internal static partial class PlayerVehicleMap
    {
        private static int _checks;
        private static void Check(bool value, string label) { if (!value) throw new Exception(label); _checks++; }
        private static void Reset()
        {
            BindConfig(new BottleShipsPlugin());
            ZNet.instance = new ZNet(); ZDOMan.instance = new ZDOMan();
            ZNetScene.instance = new ZNetScene(); Game.instance = new Game();
            Player.m_localPlayer = new Player(); Minimap.instance = new Minimap();
            Time.realtimeSinceStartup = 10; PlayerController.InputAllowed = true;
            ToggleMyVehiclePinsKey.Value = new KeyboardShortcut(KeyCode.V);
            foreach (string name in new[] { "Raft", "Cart", "House" })
            {
                var prefab = new GameObject { name = name };
                prefab.Components[typeof(Piece)] = new Piece();
                if (name == "Raft") prefab.Components[typeof(Ship)] = new Ship();
                if (name == "Cart") prefab.Components[typeof(Vagon)] = new Vagon();
                ZNetScene.instance.m_prefabs.Add(prefab);
            }
            var hints = new GameObject();
            hints.transform.Children["AddPin"] = new GameObject().transform;
            Minimap.instance.m_largeRoot.transform.Children["KeyHints/keyboard_hints"] = hints.transform;
        }
        private static ZDO Add(uint id, string prefab, long creator, long owner = 999)
        {
            var zdo = new ZDO { m_uid = new ZDOID(10, id), Prefab = prefab.GetStableHashCode(),
                Creator = creator, Owner = owner, Position = new Vector3(id, 0, id) };
            ZDOMan.instance.Objects[zdo.m_uid] = zdo;
            return zdo;
        }
        private static ZNetPeer AddPeer(long playerId)
        {
            var peer = new ZNetPeer { m_characterID = new ZDOID(100, 1) };
            var characterPrefab = new GameObject { name = "Player" };
            characterPrefab.Components[typeof(Player)] = new Player();
            ZNetScene.instance.m_prefabs.Add(characterPrefab);
            ZDOMan.instance.Objects[peer.m_characterID] = new ZDO { m_uid = peer.m_characterID,
                Prefab = "Player".GetStableHashCode(), PlayerId = playerId, Owner = peer.m_uid };
            ZNet.instance.Peers.Add(peer);
            return peer;
        }
        private static void Open()
        { Minimap.instance.m_mode = Minimap.MapMode.Large; ObserveMap(Minimap.instance); }
        private static void Close()
        { Minimap.instance.m_mode = Minimap.MapMode.Small; ObserveMap(Minimap.instance); }
        private static ZPackage Request(long id, bool extra = false)
        { var p = new ZPackage(); p.Write(id); if (extra) p.Write(42L); return p.Copy(); }
        private static ZPackage Packet(long request, int status, int count, params VehicleEntry[] entries)
        {
            var p = new ZPackage(); p.Write(request); p.Write(status); p.Write(count);
            foreach (var e in entries) { p.Write(e.Id); p.Write(e.Prefab); p.Write(e.Position); }
            return p.Copy();
        }
        internal static string RunChecks()
        {
            var settings = new BottleShipsPlugin(); BindConfig(settings);
            Check(settings.Bindings.Count == 1 && settings.Bindings[0].Name == "Toggle My Vehicle Pins Key",
                "Only shortcut is configurable; visibility setting removed");
            Check(settings.Bindings[0].Group == "01 - General" && settings.Bindings[0].Category == "01 - General" &&
                settings.Bindings[0].Order < 950 && !settings.Bindings[0].Synced,
                "Local shortcut is stored in General and displayed after other General settings");
            Check(Translate("Invalid {", "V") == "Invalid {", "Malformed external formatting cannot break map updates");
            Reset();
            var own = Add(1, "Raft", 42); Add(2, "Cart", 42); Add(3, "Raft", 77, 42);
            Add(4, "Raft", 0); Add(5, "House", 42);
            Open(); Game.instance.Drain(); Tick();
            Check(Pins.Count == 2, "Only current-character boats/carts, regardless of network owner; no duplicate batches");
            Check(Pins.TrueForAll(p => !p.Saved && p.m_icon != null), "Build icons and temporary pins");
            Check(_hintLabel!.text.Contains("hide_my_vehicles") && _hintLabel.text.Contains("V"), "Visible keyhint");
            int scans = ZDOMan.instance.Scans, adds = Minimap.instance.Adds;
            own.Position = new Vector3(123, 0, 456);
            for (int frame = 0; frame < 10; frame++) Tick();
            Check(ZDOMan.instance.Scans == scans && Minimap.instance.Adds == adds, "Idle map does not scan or recreate pins");
            Check(Pins[0].Position.x == 1, "Open map retains initial position");
            Keys.Down = true; Tick();
            Check(Pins.Count == 0 && _hintLabel.text.Contains("show_my_vehicles"), "V hides pins and changes hint");
            Keys.Down = true; Tick();
            Check(Pins.Count == 2 && ZDOMan.instance.Scans == scans && Pins[0].Position.x == 1, "V reuses snapshot");
            PlayerController.InputAllowed = false; Keys.Down = true; Tick();
            Check(Pins.Count == 2, "Typing/input suppression preserves visibility");
            PlayerController.InputAllowed = true;
            ToggleMyVehiclePinsKey.Value = new KeyboardShortcut(KeyCode.B); Tick();
            Check(_hintLabel.text.Contains(">B</color>"), "Live shortcut reflected in hint");
            var oldHint = _hint!; Close();
            Check(Pins.Count == 0 && Minimap.instance.Pins.Count == 0 && oldHint.Destroyed && _snapshot.Count == 0, "Close removes all owned UI and snapshot");
            ZDOMan.instance.Objects.Remove(new ZDOID(10, 2));
            Open(); Game.instance.Drain();
            Check(Pins.Count == 1 && Pins[0].Position.x == 123, "Reopen refreshes movement and destruction");
            Close(); Open(); long cancelled = _requestId; Close(); Game.instance.Drain();
            Check(!_pending && Pins.Count == 0 && Game.instance.Routines.Count == 0, "Close cancels local scan");
            Open(); AcceptSnapshot(cancelled, SnapshotStatus.Ready, new List<VehicleEntry>());
            Check(_pending, "Reply from previous opening ignored");
            Game.instance.Drain(); int completedAdds = Minimap.instance.Adds;
            AcceptSnapshot(_requestId, SnapshotStatus.Ready, new List<VehicleEntry>());
            Check(Pins.Count == 1 && Minimap.instance.Adds == completedAdds, "Duplicate response ignored");
            Shutdown(); Check(Pins.Count == 0 && PeerScans.Count == 0, "Shutdown clears feature state");

            Reset(); Add(1, "Raft", 42);
            Open(); Check(_visible, "New map starts with vehicle pins enabled");
            Keys.Down = true; Tick(); Game.instance.Drain();
            Check(Pins.Count == 0 && _snapshot.Count == 1, "Hiding before snapshot arrives stays hidden");
            scans = ZDOMan.instance.Scans; Keys.Down = true; Tick();
            Check(Pins.Count == 1 && ZDOMan.instance.Scans == scans, "First show does not request again");
            Close(); Minimap.instance.m_mode = Minimap.MapMode.None; ObserveMap(Minimap.instance);
            Check(_map == null && ZDOMan.instance.Scans == scans, "No-map world does not scan");

            Reset(); own = Add(1, "Raft", 42);
            Check(TryGetOwnVehicle(ZDOMan.instance, own, 42, own.Prefab, out _), "Recorded creator accepted");
            Check(!TryGetOwnVehicle(ZDOMan.instance, own, 0, own.Prefab, out _), "Zero requester denied");
            Check(!TryGetOwnVehicle(ZDOMan.instance, own, 77, own.Prefab, out _), "Different builder denied");
            Check(!TryGetOwnVehicle(ZDOMan.instance, own, 42, 0, out _), "Different prefab denied");
            own.Position.x = float.NaN;
            Check(!TryGetOwnVehicle(ZDOMan.instance, own, 42, own.Prefab, out _), "Nonfinite position denied");
            own.Position.x = 0; own.Valid = false;
            Check(!TryGetOwnVehicle(ZDOMan.instance, own, 42, own.Prefab, out _), "Invalid ZDO denied");
            own.Valid = true; ZDOMan.instance.Objects.Remove(own.m_uid);
            Check(!TryGetOwnVehicle(ZDOMan.instance, own, 42, own.Prefab, out _), "Destroyed/pooled ZDO denied");

            var entry = new VehicleEntry(new ZDOID(7, 1), 50, new Vector3(1, 2, 3));
            Check(TryReadSnapshot(Packet(8, 0, 1, entry), out long request, out _, out var read) && request == 8 && read.Count == 1, "Bounded snapshot round trip");
            Check(!TryReadSnapshot(Packet(0, 0, 0), out _, out _, out _), "Invalid nonce rejected");
            Check(!TryReadSnapshot(Packet(8, 5, 0), out _, out _, out _), "Unknown status rejected");
            Check(!TryReadSnapshot(Packet(8, 0, -1), out _, out _, out _), "Negative count rejected");
            Check(!TryReadSnapshot(Packet(8, 0, MaximumVehicles + 1), out _, out _, out _), "Oversized count rejected");
            Check(!TryReadSnapshot(Packet(8, 1, 1, entry), out _, out _, out _), "Unavailable payload cannot contain pins");
            Check(!TryReadSnapshot(Packet(8, 0, 1), out _, out _, out _), "Truncated payload rejected");
            Check(!TryReadSnapshot(Packet(8, 0, 0, entry), out _, out _, out _), "Trailing data rejected");
            Check(!TryReadSnapshot(Packet(8, 0, 2, entry, entry), out _, out _, out _), "Duplicate IDs rejected");
            Check(!TryReadSnapshot(Packet(8, 0, 1, new VehicleEntry(default, 1, default)), out _, out _, out _), "Empty ID rejected");
            Check(!TryReadSnapshot(Packet(8, 0, 1, new VehicleEntry(entry.Id, 1, new Vector3(float.PositiveInfinity, 0, 0))), out _, out _, out _), "Infinite network position rejected");
            Check(TryReadSnapshot(Packet(8, 2, 1, entry), out _, out var status, out _) && status == SnapshotStatus.Truncated, "Explicit truncation accepted");
            var sender = new ZRpc(); SendSnapshot(sender, 9, SnapshotStatus.Ready, new List<VehicleEntry> { entry });
            Check(TryReadSnapshot(sender.Sent[0].Package, out request, out _, out read) && request == 9 && read.Count == 1, "Production encoder and decoder agree");

            Reset(); Add(1, "Raft", 42); Add(2, "Cart", 77);
            var peer = AddPeer(42);
            RegisterPeer(ZNet.instance, peer);
            Check(peer.m_rpc.Handlers.ContainsKey(RequestRpc), "Server registers request RPC");
            OnRequest(peer.m_rpc, Request(1, extra: true));
            Check(ZDOMan.instance.Scans == 0, "Client-supplied extra player identity rejected");
            peer.Ready = false; OnRequest(peer.m_rpc, Request(1));
            Check(ZDOMan.instance.Scans == 0, "Unauthenticated connection never scans");
            peer.Ready = true; peer.m_rpc.Sent.Clear(); OnRequest(peer.m_rpc, Request(2)); Game.instance.Drain();
            Check(TryReadSnapshot(peer.m_rpc.Sent[0].Package, out _, out _, out read) && read.Count == 1 && read[0].Id.ID == 1, "Server derives builder from authenticated peer");
            scans = ZDOMan.instance.Scans; OnRequest(peer.m_rpc, Request(3));
            Check(ZDOMan.instance.Scans == scans, "Rapid request throttled");
            Time.realtimeSinceStartup += 1; peer.m_rpc.Sent.Clear(); OnRequest(peer.m_rpc, Request(4));
            peer.PlayerId = 77; Game.instance.Drain();
            Check(peer.m_rpc.Sent.Count == 0, "Changed authenticated character cancels response");
            Time.realtimeSinceStartup += 1; OnRequest(peer.m_rpc, Request(5)); ForgetPeer(peer.m_rpc);
            Check(Game.instance.Routines.Count == 0 && PeerScans.Count == 0, "Disconnect stops scan and releases connection");

            Reset(); ZNet.instance.Server = false; var server = new ZRpc(); ZNet.instance.ServerRpc = server;
            RegisterPeer(ZNet.instance, new ZNetPeer { m_rpc = server });
            Check(server.Handlers.ContainsKey(SnapshotRpc), "Client registers snapshot RPC");
            Open(); Check(server.Sent.Count == 1 && server.Sent[0].Package.Size() == 8, "Opening remote map sends nonce only");
            OnSnapshot(new ZRpc(), Packet(_requestId, 0, 0)); Check(_pending, "Non-server RPC ignored");
            Close(); OnSnapshot(server, Packet(_requestId, 0, 0)); Check(_map == null && Pins.Count == 0, "Late remote reply cannot populate minimap");
            Open(); ZNet.instance = new ZNet(); AcceptSnapshot(_requestId, SnapshotStatus.Ready, new List<VehicleEntry>());
            Check(_pending, "Reply from old network session ignored");
            Shutdown();

            Reset(); Add(1, "Raft", 42); ZDOMan.instance.ThrowOnScan = true; Open();
            Check(!_pending && Pins.Count == 0 && Player.m_localPlayer!.Messages.Count == 1, "Scan failure reports unavailable, no partial pins");
            Reset(); Open(); Game.instance.Drain(); int saves = ToggleMyVehiclePinsKey.Saves;
            for (int toggle = 0; toggle < 5; toggle++) { Keys.Down = true; Tick(); }
            Check(!_visible && ToggleMyVehiclePinsKey.Saves == saves, "Rapid V toggles do not write configuration");
            Close(); Check(ToggleMyVehiclePinsKey.Saves == saves, "Closing map does not save visibility");
            Open(); Check(_visible, "Reopen always enables vehicle pins after hiding");
            var previousKey = ToggleMyVehiclePinsKey; Shutdown();
            Check(previousKey.Subscribers == 1, "Network shutdown keeps configuration subscription");
            BindConfig(new BottleShipsPlugin());
            Check(previousKey.Subscribers == 0 && ToggleMyVehiclePinsKey.Subscribers == 1, "Rebind replaces subscription");
            var currentKey = ToggleMyVehiclePinsKey; Dispose();
            Check(currentKey.Subscribers == 0, "Plugin disposal removes subscription");
            Reset(); peer = AddPeer(42);
            Check(TryGetAuthenticatedPeerPlayerId(peer, out long authenticated) && authenticated == 42,
                "Actual authentication accepts character ZDO without a live remote Player object");
            Check(!TryGetAuthenticatedPeerPlayerId(null, out _), "Unknown peer rejected");
            peer.Ready = false;
            Check(!TryGetAuthenticatedPeerPlayerId(peer, out _), "Not-ready peer rejected"); peer.Ready = true;
            ZNet.instance.Server = false;
            Check(!TryGetAuthenticatedPeerPlayerId(peer, out _), "Authentication is server-only"); ZNet.instance.Server = true;
            var characterId = peer.m_characterID; peer.m_characterID = default;
            Check(!TryGetAuthenticatedPeerPlayerId(peer, out _), "Missing character identity rejected"); peer.m_characterID = characterId;
            peer.m_uid++;
            Check(!TryGetAuthenticatedPeerPlayerId(peer, out _), "Character ID must belong to peer"); peer.m_uid--;
            var character = ZDOMan.instance.GetZDO(characterId)!; character.Owner++;
            Check(!TryGetAuthenticatedPeerPlayerId(peer, out _), "Character owner must match peer"); character.Owner--;
            character.Prefab = "Raft".GetStableHashCode();
            Check(!TryGetAuthenticatedPeerPlayerId(peer, out _), "Non-Player prefab rejected"); character.Prefab = "Player".GetStableHashCode();
            character.PlayerId = 0;
            Check(!TryGetAuthenticatedPeerPlayerId(peer, out _), "Zero authenticated player ID rejected"); character.PlayerId = 42;
            character.Valid = false;
            Check(!TryGetAuthenticatedPeerPlayerId(peer, out _), "Invalid character ZDO rejected"); character.Valid = true;
            ZDOMan.instance.Objects.Remove(characterId);
            Check(!TryGetAuthenticatedPeerPlayerId(peer, out _), "Absent character ZDO rejected");

            Reset(); for (uint i = 1; i <= MaximumVehicles + 1; i++) Add(i, "Cart", 42);
            Open(); Game.instance.Drain();
            Check(Pins.Count == MaximumVehicles && Player.m_localPlayer!.Messages.Last().Contains("truncated"), "Vehicle cap has explicit incomplete-list message");
            Shutdown();
            Dispose();
            return $"{_checks} vehicle map checks passed (complete production feature; Unity/transport/world boundaries are test doubles).";
        }
    }
    public static class Checks { public static string Run() => PlayerVehicleMap.RunChecks(); }
}

/* PRODUCTION_SOURCES */

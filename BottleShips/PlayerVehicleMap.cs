using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace BottleShips;

internal static partial class PlayerVehicleMap
{
    private static Minimap? _map;
    private static Player? _mapPlayer;
    private static ZNet? _mapNetwork;
    private static ZRpc? _serverRpc;
    private static long _requestId;
    private static bool _pending;
    private static float _requestedAt;
    private static bool _visible;
    private static List<VehicleEntry> _snapshot = new();
    private static readonly List<Minimap.PinData> Pins = new();
    private static GameObject? _hint;
    private static TMP_Text? _hintLabel;
    private static bool _hintAttempted;
    private static string? _hintLanguage;
    private static bool _hintVisiblePins;

    internal static void Tick()
    {
        ObserveMap(Minimap.instance);
        if (_map == null) return;
        KeyboardShortcut shortcut = ToggleMyVehiclePinsKey.Value;
        if (IsKeyDown(shortcut) && CanProcessMapShortcutInput())
        {
            _visible = !_visible;
            RenderPins();
        }
        if (_pending && Time.realtimeSinceStartup - _requestedAt >= RequestTimeoutSeconds)
            AcceptSnapshot(_requestId, SnapshotStatus.Unavailable, new List<VehicleEntry>());
        UpdateHint(shortcut);
    }

    private static bool IsLargeMap(Minimap? map)
    {
        return map != null && map.m_mode == Minimap.MapMode.Large &&
               map.m_largeRoot != null && map.m_largeRoot.activeInHierarchy;
    }

    private static void ObserveMap(Minimap? map)
    {
        if (!IsLargeMap(map) || Player.m_localPlayer == null || ZNet.instance == null)
        {
            CloseMap();
            return;
        }
        if (_map == map && _mapPlayer == Player.m_localPlayer && _mapNetwork == ZNet.instance) return;
        CloseMap();
        _map = map;
        _mapPlayer = Player.m_localPlayer;
        _mapNetwork = ZNet.instance;
        _visible = true;
        // Do not reset across worlds: a delayed reply must not match a new map opening.
        _requestId++;
        _pending = true;
        _requestedAt = Time.realtimeSinceStartup;
        RequestSnapshot();
    }

    private static void AcceptSnapshot(long requestId, SnapshotStatus status, List<VehicleEntry> entries)
    {
        if (!_pending || requestId != _requestId || !IsLargeMap(_map) ||
            _map != Minimap.instance || _mapPlayer == null || _mapPlayer != Player.m_localPlayer ||
            _mapNetwork == null || _mapNetwork != ZNet.instance) return;
        _pending = false;
        _snapshot = entries;
        RenderPins();
        if (status != SnapshotStatus.Ready && _visible)
            _mapPlayer.Message(MessageHud.MessageType.Center, Translate(
                status == SnapshotStatus.Truncated
                    ? "$sighsorry_bottleships_vehicle_map_truncated"
                    : "$sighsorry_bottleships_vehicle_map_unavailable"));
    }

    private static void RenderPins()
    {
        RemovePins();
        if (!_visible || !IsLargeMap(_map) || ZNetScene.instance == null) return;
        foreach (VehicleEntry entry in _snapshot)
        {
            Piece? piece = GetVehiclePiece(ZNetScene.instance.GetPrefab(entry.Prefab));
            if (piece == null || piece.m_icon == null) continue;
            // Use the game's location-icon pattern: None plus a per-pin sprite, without saving.
            Minimap.PinData pin = _map!.AddPin(entry.Position, Minimap.PinType.None,
                Localization.instance.Localize(piece.m_name), save: false, isChecked: false);
            pin.m_icon = piece.m_icon;
            Pins.Add(pin);
        }
    }

    private static void RemovePins()
    {
        if (_map != null)
            foreach (Minimap.PinData pin in Pins) _map.RemovePin(pin);
        Pins.Clear();
    }

    private static void CloseMap()
    {
        _pending = false;
        CancelScan(_localScan);
        _localScan = null;
        RemovePins();
        _snapshot.Clear();
        if (_hint != null)
        {
            _hint.SetActive(false);
            MarkHintLayout();
            Object.Destroy(_hint);
        }
        _hint = null;
        _hintLabel = null;
        _hintLanguage = null;
        _hintAttempted = false;
        _map = null;
        _mapPlayer = null;
        _mapNetwork = null;
        _serverRpc = null;
    }

    private static void UpdateHint(KeyboardShortcut shortcut)
    {
        bool show = shortcut.MainKey != KeyCode.None && PlatformPrefs.GetInt("KeyHints", 1) == 1;
        if (show && !_hintAttempted)
        {
            _hintAttempted = true;
            BuildHint();
        }
        if (_hint == null || _hintLabel == null) return;
        if (_hint.activeSelf != show)
        {
            _hint.SetActive(show);
            MarkHintLayout();
        }
        if (!show) return;
        string language = Localization.instance.GetSelectedLanguage();
        if (_hintLanguage == language && _hintVisiblePins == _visible) return;
        _hintLanguage = language;
        _hintVisiblePins = _visible;
        _hintLabel.text = Translate(_visible
                ? "$sighsorry_bottleships_hint_hide_my_vehicles"
                : "$sighsorry_bottleships_hint_show_my_vehicles",
            FormatHintShortcut(shortcut));
        MarkHintLayout();
    }

    internal static void OnShortcutChanged(object sender, System.EventArgs args)
    {
        // KeyboardShortcut.Equals boxes and enumerates modifiers in BepInEx 5.
        // Invalidate from the setting event instead of comparing it every frame.
        _hintLanguage = null;
    }

    private static void BuildHint()
    {
        Transform? parent = _map?.m_largeRoot.transform.Find("KeyHints/keyboard_hints");
        Transform? template = parent?.Find("AddPin");
        if (parent == null || template == null) return;
        _hint = Object.Instantiate(template.gameObject, parent, false);
        _hint.name = "BottleShipsMyVehiclesHint";
        _hint.transform.SetSiblingIndex(template.GetSiblingIndex());
        _hint.transform.Find("keyboard_hint")?.gameObject.SetActive(false);
        _hintLabel = _hint.transform.Find("Label")?.GetComponent<TMP_Text>() ??
                     _hint.GetComponentInChildren<TMP_Text>(includeInactive: true);
        if (_hintLabel == null)
        {
            Object.Destroy(_hint);
            _hint = null;
            return;
        }
        _hintLabel.richText = true;
        _hintLanguage = null;
    }

    private static void MarkHintLayout()
    {
        if (_hint?.transform.parent is RectTransform parent) LayoutRebuilder.MarkLayoutForRebuild(parent);
    }

    [HarmonyPatch(typeof(Minimap), nameof(Minimap.SetMapMode))]
    private static class MapModePatch
    {
        // Read the resulting mode: no-map worlds can override the incoming argument.
        private static void Postfix(Minimap __instance) => ObserveMap(__instance);
    }

    [HarmonyPatch(typeof(Minimap), "OnDestroy")]
    private static class MapDestroyPatch
    {
        private static void Prefix(Minimap __instance)
        {
            if (ReferenceEquals(_map, __instance)) CloseMap();
        }
    }

    private static ConfigEntry<KeyboardShortcut> ToggleMyVehiclePinsKey = null!;
    private static readonly Func<PlayerController, bool, bool> TakeInput =
        AccessTools.MethodDelegate<Func<PlayerController, bool, bool>>(
            AccessTools.DeclaredMethod(typeof(PlayerController), "TakeInput", new[] { typeof(bool) }));

    internal static void BindConfig(BottleShipsPlugin plugin)
    {
        Dispose();
        ToggleMyVehiclePinsKey = plugin.config(
            "01 - General", "Toggle My Vehicle Pins Key", new KeyboardShortcut(KeyCode.V),
            "Show or hide your boats and carts while the large map is open. Reopening the map always shows them and refreshes their positions. No minimap pins.",
            synchronizedSetting: false, order: 940);
        ToggleMyVehiclePinsKey.SettingChanged += OnShortcutChanged;
    }

    internal static void Dispose()
    {
        Shutdown();
        if (ToggleMyVehiclePinsKey != null)
            ToggleMyVehiclePinsKey.SettingChanged -= OnShortcutChanged;
    }

    private static bool IsKeyDown(KeyboardShortcut shortcut)
    {
        return shortcut.MainKey != KeyCode.None && Input.GetKeyDown(shortcut.MainKey) &&
               shortcut.Modifiers.All(Input.GetKey);
    }

    private static bool CanProcessMapShortcutInput()
    {
        if (Player.m_localPlayer == null) return false;
        PlayerController controller = Player.m_localPlayer.GetComponent<PlayerController>();
        return controller != null && TakeInput(controller, false);
    }

    private static string Translate(string token, params string[] arguments)
    {
        string text = Localization.instance.Localize(token);
        if (arguments.Length == 0) return text;
        try
        {
            return string.Format(text, arguments);
        }
        catch (FormatException)
        {
            // An invalid external translation must not break the map update loop.
            return text;
        }
    }

    private static string FormatHintShortcut(KeyboardShortcut shortcut)
    {
        StringBuilder text = new();
        foreach (KeyCode modifier in shortcut.Modifiers)
        {
            text.Append(FormatHintKey(modifier));
            text.Append(" + ");
        }

        text.Append("<color=");
        text.Append("#FFA500");
        text.Append('>');
        text.Append(FormatHintKey(shortcut.MainKey));
        text.Append("</color>");
        return text.ToString();
    }

    private static string FormatHintKey(KeyCode key)
    {
        string value = key switch
        {
            KeyCode.LeftShift or KeyCode.RightShift =>
                Translate(
                    "$sighsorry_bottleships_key_shift"),
            KeyCode.LeftControl or KeyCode.RightControl =>
                Translate(
                    "$sighsorry_bottleships_key_control"),
            KeyCode.LeftAlt or KeyCode.RightAlt =>
                Translate(
                    "$sighsorry_bottleships_key_alt"),
            _ => key.ToString()
        };
        return value.RemoveRichTextTags();
    }
}

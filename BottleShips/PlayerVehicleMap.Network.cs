using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace BottleShips;

internal static partial class PlayerVehicleMap
{
    private const string RequestRpc = "sighsorry.BottleShips" + ".VehicleMapRequest.v1";
    private const string SnapshotRpc = "sighsorry.BottleShips" + ".VehicleMapSnapshot.v1";
    private const int MaximumVehicles = 2048;
    private const int EntryBytes = 28; // ZDOID (12), prefab hash (4), position (12).
    private const int HeaderBytes = 16; // Request (8), status (4), count (4).
    private const float RequestCooldownSeconds = 0.5f;
    private const float RequestTimeoutSeconds = 30f;
    private enum SnapshotStatus { Ready, Unavailable, Truncated }

    private readonly struct VehicleEntry
    {
        internal readonly ZDOID Id;
        internal readonly int Prefab;
        internal readonly Vector3 Position;

        internal VehicleEntry(ZDOID id, int prefab, Vector3 position)
        {
            Id = id;
            Prefab = prefab;
            Position = position;
        }
    }

    private sealed class Scan
    {
        internal readonly long RequestId;
        internal readonly long PlayerId;
        internal readonly ZRpc? Rpc;
        internal readonly Player? LocalPlayer;
        internal readonly ZNet Network = ZNet.instance;
        internal readonly ZDOMan World = ZDOMan.instance;
        internal readonly ZNetScene Scene = ZNetScene.instance;
        internal readonly Game Game = Game.instance;
        internal readonly float StartedAt = Time.realtimeSinceStartup;
        internal bool Cancelled;
        internal Coroutine? Routine;

        internal Scan(long requestId, long playerId, ZRpc? rpc, Player? localPlayer)
        {
            RequestId = requestId;
            PlayerId = playerId;
            Rpc = rpc;
            LocalPlayer = localPlayer;
        }
    }

    // Retain completed requests until disconnect for per-connection throttling.
    private static readonly Dictionary<ZRpc, Scan> PeerScans = new();
    private static Scan? _localScan;

    internal static void RegisterPeer(ZNet znet, ZNetPeer peer)
    {
        if (peer?.m_rpc == null) return;
        if (znet.IsServer()) peer.m_rpc.Register<ZPackage>(RequestRpc, OnRequest);
        else peer.m_rpc.Register<ZPackage>(SnapshotRpc, OnSnapshot);
    }

    internal static void ForgetPeer(ZRpc rpc)
    {
        if (PeerScans.TryGetValue(rpc, out Scan scan)) CancelScan(scan);
        PeerScans.Remove(rpc);
        if (ReferenceEquals(_serverRpc, rpc)) CloseMap();
    }

    internal static void Shutdown()
    {
        CloseMap();
        foreach (Scan scan in PeerScans.Values) CancelScan(scan);
        PeerScans.Clear();
    }

    private static void CancelScan(Scan? scan)
    {
        if (scan == null) return;
        scan.Cancelled = true;
        if (scan.Game != null && scan.Routine != null) scan.Game.StopCoroutine(scan.Routine);
        scan.Routine = null;
    }

    private static void RequestSnapshot()
    {
        ZNet network = ZNet.instance;
        if (network == null || _mapPlayer == null || Game.instance == null ||
            ZDOMan.instance == null || ZNetScene.instance == null)
        {
            AcceptSnapshot(_requestId, SnapshotStatus.Unavailable, new List<VehicleEntry>());
            return;
        }

        if (network.IsServer())
        {
            _localScan = new Scan(_requestId, _mapPlayer.GetPlayerID(), null, _mapPlayer);
            _localScan.Routine = _localScan.Game.StartCoroutine(ScanVehicles(_localScan));
        }
        else
        {
            _serverRpc = network.GetServerRPC();
            if (_serverRpc == null)
            {
                AcceptSnapshot(_requestId, SnapshotStatus.Unavailable, new List<VehicleEntry>());
                return;
            }
            ZPackage package = new();
            package.Write(_requestId); // Never accept a client-provided creator/player ID.
            _serverRpc.Invoke(RequestRpc, package);
        }
    }

    private static void OnRequest(ZRpc rpc, ZPackage package)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer() ||
            package == null || package.Size() != sizeof(long)) return;
        long requestId = package.ReadLong();
        if (requestId <= 0) return;
        ZNetPeer? peer = FindPeer(ZNet.instance, rpc);
        if (Game.instance == null || ZNetScene.instance == null ||
            !TryGetAuthenticatedPeerPlayerId(peer, out long playerId))
        {
            SendSnapshot(rpc, requestId, SnapshotStatus.Unavailable, new List<VehicleEntry>());
            return;
        }

        if (PeerScans.TryGetValue(rpc, out Scan previous))
        {
            if (requestId <= previous.RequestId) return;
            if (Time.realtimeSinceStartup - previous.StartedAt < RequestCooldownSeconds)
            {
                SendSnapshot(rpc, requestId, SnapshotStatus.Unavailable, new List<VehicleEntry>());
                return;
            }
            CancelScan(previous);
        }
        Scan scan = new(requestId, playerId, rpc, null);
        PeerScans[rpc] = scan;
        scan.Routine = scan.Game.StartCoroutine(ScanVehicles(scan));
    }

    private static bool IsCurrentScan(Scan scan)
    {
        if (scan.Cancelled || scan.PlayerId == 0 || scan.Game == null ||
            scan.Game != Game.instance || scan.Network == null ||
            scan.Network != ZNet.instance || !scan.Network.IsServer() ||
            !ReferenceEquals(scan.World, ZDOMan.instance) ||
            scan.Scene == null || scan.Scene != ZNetScene.instance) return false;
        if (scan.Rpc == null)
            return scan.LocalPlayer != null && scan.LocalPlayer == Player.m_localPlayer &&
                   scan.LocalPlayer.GetPlayerID() == scan.PlayerId;
        return TryGetAuthenticatedPeerPlayerId(
                   FindPeer(scan.Network, scan.Rpc), out long playerId) &&
               playerId == scan.PlayerId;
    }

    private static Piece? GetVehiclePiece(GameObject? prefab)
    {
        if (prefab == null || (prefab.GetComponent<Ship>() == null &&
                               prefab.GetComponent<Vagon>() == null)) return null;
        return prefab.GetComponent<Piece>();
    }

    private static IEnumerator ScanVehicles(Scan scan)
    {
        List<VehicleEntry> entries = new();
        List<string> prefabs = new();
        HashSet<int> prefabHashes = new();
        foreach (GameObject prefab in scan.Scene.m_prefabs)
        {
            if (GetVehiclePiece(prefab) != null && prefabHashes.Add(prefab.name.GetStableHashCode()))
                prefabs.Add(prefab.name);
        }

        HashSet<ZDOID> seen = new();
        List<ZDO> chunk = new();
        SnapshotStatus status = SnapshotStatus.Ready;
        foreach (string prefab in prefabs)
        {
            int prefabHash = prefab.GetStableHashCode();
            int index = 0;
            bool finished;
            do
            {
                if (!IsCurrentScan(scan)) yield break;
                if (Time.realtimeSinceStartup - scan.StartedAt >= RequestTimeoutSeconds - 1f)
                {
                    status = SnapshotStatus.Unavailable;
                    break;
                }
                if (!TryScanChunk(scan, prefab, chunk, ref index, out finished))
                {
                    status = SnapshotStatus.Unavailable;
                    break;
                }
                foreach (ZDO zdo in chunk)
                {
                    if (!TryGetOwnVehicle(scan.World, zdo, scan.PlayerId,
                            prefabHash, out VehicleEntry entry) || !seen.Add(entry.Id)) continue;
                    if (entries.Count == MaximumVehicles)
                    {
                        status = SnapshotStatus.Truncated;
                        break;
                    }
                    entries.Add(entry);
                }
                if (status != SnapshotStatus.Ready) break;
                // The game's public iterator bounds each sector batch. No persistent world polling.
                yield return null;
            } while (!finished);
            if (status != SnapshotStatus.Ready) break;
        }

        if (!IsCurrentScan(scan)) yield break;
        if (status == SnapshotStatus.Unavailable) entries.Clear();
        scan.Routine = null;
        if (scan.Rpc != null) SendSnapshot(scan.Rpc, scan.RequestId, status, entries);
        else AcceptSnapshot(scan.RequestId, status, entries);
    }

    private static bool TryScanChunk(Scan scan, string prefab, List<ZDO> chunk,
        ref int index, out bool finished)
    {
        chunk.Clear();
        finished = false;
        try
        {
            finished = scan.World.GetAllZDOsWithPrefabIterative(prefab, chunk, ref index);
            return true;
        }
        catch (Exception ex)
        {
            BottleShipsPlugin.BottleShipsLogger.LogWarning($"Vehicle map scan failed: {ex.Message}");
            return false;
        }
    }

    private static bool TryGetOwnVehicle(ZDOMan world, ZDO zdo, long playerId,
        int prefab, out VehicleEntry entry)
    {
        entry = default;
        if (playerId == 0 || zdo == null || !zdo.IsValid() ||
            !ReferenceEquals(world.GetZDO(zdo.m_uid), zdo) ||
            zdo.GetPrefab() != prefab || zdo.GetLong(ZDOVars.s_creator, 0L) != playerId ||
            !IsValidPosition(zdo.GetPosition())) return false;
        entry = new VehicleEntry(zdo.m_uid, prefab, zdo.GetPosition());
        return true;
    }

    private static bool IsValidPosition(Vector3 position)
    {
        return IsValidCoordinate(position.x) && IsValidCoordinate(position.y) &&
               IsValidCoordinate(position.z);
    }

    private static bool IsValidCoordinate(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value) && Math.Abs(value) <= 1000000f;
    }

    private static void SendSnapshot(ZRpc rpc, long requestId, SnapshotStatus status,
        List<VehicleEntry> entries)
    {
        ZPackage package = new();
        package.Write(requestId);
        package.Write((int)status);
        package.Write(entries.Count);
        foreach (VehicleEntry entry in entries)
        {
            package.Write(entry.Id);
            package.Write(entry.Prefab);
            package.Write(entry.Position);
        }
        rpc.Invoke(SnapshotRpc, package);
    }

    private static void OnSnapshot(ZRpc rpc, ZPackage package)
    {
        if (ZNet.instance == null || ZNet.instance.IsServer() ||
            !ReferenceEquals(rpc, ZNet.instance.GetServerRPC()) ||
            !ReferenceEquals(rpc, _serverRpc)) return;
        if (TryReadSnapshot(package, out long requestId, out SnapshotStatus status,
                out List<VehicleEntry> entries)) AcceptSnapshot(requestId, status, entries);
    }

    private static bool TryReadSnapshot(ZPackage package, out long requestId,
        out SnapshotStatus status, out List<VehicleEntry> entries)
    {
        requestId = 0;
        status = SnapshotStatus.Unavailable;
        entries = new List<VehicleEntry>();
        if (package == null || package.Size() < HeaderBytes ||
            package.Size() > HeaderBytes + MaximumVehicles * EntryBytes) return false;
        try
        {
            requestId = package.ReadLong();
            status = (SnapshotStatus)package.ReadInt();
            int count = package.ReadInt();
            if (requestId <= 0 || status < SnapshotStatus.Ready || status > SnapshotStatus.Truncated ||
                count < 0 || count > MaximumVehicles ||
                (status == SnapshotStatus.Unavailable && count != 0) ||
                package.Size() != HeaderBytes + count * EntryBytes) return false;
            HashSet<ZDOID> ids = new();
            for (int index = 0; index < count; index++)
            {
                ZDOID id = package.ReadZDOID();
                int prefab = package.ReadInt();
                Vector3 position = package.ReadVector3();
                if (id.IsNone() || !ids.Add(id) || !IsValidPosition(position)) return false;
                entries.Add(new VehicleEntry(id, prefab, position));
            }
            return package.GetPos() == package.Size();
        }
        catch (Exception)
        {
            return false;
        }
    }

    [HarmonyPatch(typeof(ZNet), "OnDestroy")]
    private static class NetworkDestroyPatch
    {
        private static void Prefix(ZNet __instance)
        {
            if (ReferenceEquals(ZNet.instance, __instance)) Shutdown();
        }
    }

    internal static ZNetPeer? FindPeer(ZNet? znet, ZRpc? rpc)
    {
        if (znet == null || rpc == null)
        {
            return null;
        }

        foreach (ZNetPeer peer in znet.GetPeers())
        {
            if (peer != null && ReferenceEquals(peer.m_rpc, rpc))
            {
                return peer;
            }
        }

        return null;
    }

    internal static bool TryGetAuthenticatedPeerPlayerId(
        ZNetPeer? peer,
        out long playerId)
    {
        return TryGetAuthenticatedPeerCharacter(
            peer,
            out _,
            out playerId);
    }

    private static bool TryGetAuthenticatedPeerCharacter(
        ZNetPeer? peer,
        out ZDO character,
        out long playerId)
    {
        character = null!;
        playerId = 0L;
        if (peer == null ||
            !peer.IsReady() ||
            ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            ZDOMan.instance == null ||
            peer.m_characterID.IsNone() ||
            peer.m_characterID.UserID != peer.m_uid)
        {
            return false;
        }

        ZDO? authenticatedCharacter = ZDOMan.instance.GetZDO(peer.m_characterID);
        if (authenticatedCharacter == null ||
            !authenticatedCharacter.IsValid() ||
            authenticatedCharacter.GetOwner() != peer.m_uid)
        {
            return false;
        }

        if (ZNetScene.instance != null)
        {
            GameObject? characterPrefab =
                ZNetScene.instance.GetPrefab(authenticatedCharacter.GetPrefab());
            if (characterPrefab == null ||
                characterPrefab.GetComponent<Player>() == null)
            {
                return false;
            }
        }

        long authenticatedPlayerId = authenticatedCharacter.GetLong(
            ZDOVars.s_playerID,
            0L);
        if (authenticatedPlayerId == 0L)
        {
            return false;
        }

        character = authenticatedCharacter;
        playerId = authenticatedPlayerId;
        return true;
    }

    [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
    private static class RegisterPeerPatch
    {
        private static void Postfix(ZNet __instance, ZNetPeer peer) => RegisterPeer(__instance, peer);
    }

    [HarmonyPatch(typeof(ZNet), "Awake")]
    private static class NetworkAwakePatch
    {
        private static void Postfix() => Shutdown();
    }

    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
    private static class DisconnectPatch
    {
        private static void Prefix(ZNetPeer peer)
        {
            if (peer?.m_rpc != null) ForgetPeer(peer.m_rpc);
        }
    }
}

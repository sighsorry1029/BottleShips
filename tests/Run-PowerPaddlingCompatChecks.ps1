param([string]$ProjectRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'BottleShips'))
$ErrorActionPreference = 'Stop'

function Get-ProductionMethod([string]$Source, [string]$Name) {
    $escaped = [regex]::Escape($Name)
    $matches = [regex]::Matches($Source,
        "(?ms)^    (?:private|internal) static [^\r\n]+\b$escaped\([^)]*\)\s*\r?\n    \{.*?^    \}")
    if ($matches.Count -ne 1) { throw "Expected one production method: $Name" }
    return $matches[0].Value
}

$tweaks = [IO.File]::ReadAllText((Join-Path $ProjectRoot 'ShipTweaksManager.cs'))
$manager = [IO.File]::ReadAllText((Join-Path $ProjectRoot 'BottleShipsManager.cs'))
$paddlingRule = Get-ProductionMethod $tweaks 'CanPowerPaddle'
$removalRule = Get-ProductionMethod $manager 'ApplyShipRemovalSetting'
$prefabComparison = Get-ProductionMethod $manager 'PrefabNameEquals'

# These are source wiring checks, not simulated Harmony/Unity execution. The
# executable cases below compile the production rules, rather than copying them.
$guardedPaths = @('UpdatePowerPaddlingInput', 'UpdatePowerPaddlingHud',
    'IsLocalPowerPaddlingActive', 'HandlePowerPaddlingRequest',
    'CountActivePowerPaddlers', 'ApplyPowerPaddlingForce')
foreach ($name in $guardedPaths) {
    $method = Get-ProductionMethod $tweaks $name
    if ($method -notmatch '\bCanPowerPaddle\(') {
        throw "Power Paddling path no longer shares the anchor/gear rule: $name"
    }
}
$handling = Get-ProductionMethod $tweaks 'TryApplyHandling'
if ($handling -notmatch '\bCountActivePowerPaddlers\(') {
    throw 'Owner physics no longer counts paddlers through the guarded path'
}
$awakePatch = [regex]::Match($manager,
    '(?ms)^\[HarmonyPatch\(typeof\(Ship\), "Awake"\)\]\r?\ninternal static class BottleShipsShipAwakeRemovalSettingPatch\r?\n\{.*?^\}')
if (!$awakePatch.Success -or $awakePatch.Value -notmatch 'HarmonyPriority\(Priority.Last\)' -or
    $awakePatch.Value -notmatch 'HarmonyAfter\(PathOfValheimanCompat.PluginGuid\)' -or
    $awakePatch.Value -notmatch 'BottleShipsManager.ApplyShipRemovalSetting\(__instance\)') {
    throw 'Ship removal configuration must run after the PoV Ship.Awake patch'
}

$fixture = @'
#nullable enable
using System;

namespace BottleShipsPaddlingCompatChecks
{
    public static class Checks
    {
        private static int count;
        private static bool ConfigBound;
        private static readonly BottleTarget[] Targets = {
            new BottleTarget("Raft"), new BottleTarget("VikingShip"),
            new BottleTarget("UnboundShip") { Config = null }
        };

        /* PADDLING_RULE */
        /* REMOVAL_RULE */
        /* PREFAB_COMPARISON */

        private static void Expect(bool condition, string description)
        {
            count++;
            if (!condition) throw new Exception(description);
        }

        public static int Run()
        {
            count = 0;
            var ship = new Ship();
            foreach (bool anchored in new[] { false, true })
            {
                PathOfValheimanCompat.IsLoaded = true;
                PathOfValheimanCompat.Anchored = anchored;
                foreach (Ship.Speed gear in Enum.GetValues(typeof(Ship.Speed)))
                {
                    ship.Gear = gear;
                    bool expected = !anchored && (gear == Ship.Speed.Back || gear == Ship.Speed.Slow);
                    Expect(CanPowerPaddle(ship) == expected, $"Gear {gear}, anchor {anchored}");
                }
            }
            // A stale PoV ZDO flag must not disable ordinary paddling without PoV.
            PathOfValheimanCompat.IsLoaded = false;
            ship.Gear = Ship.Speed.Slow;
            Expect(CanPowerPaddle(ship), "No optional dependency: allow slow paddling");
            PathOfValheimanCompat.IsLoaded = true;
            Expect(!CanPowerPaddle(ship), "Anchor blocks an already paddling ship");
            PathOfValheimanCompat.Anchored = false;
            Expect(CanPowerPaddle(ship), "Raising anchor restores eligibility");

            var piece = new Piece("Raft(Clone)") { m_canBeRemoved = true };
            ship.Piece = piece;
            ConfigBound = true;
            Targets[0].Config!.CanBeRemoved.Value = BottleShipsPlugin.Toggle.Off;
            ApplyShipRemovalSetting(ship);
            Expect(!piece.m_canBeRemoved, "PoV's forced true is overridden by explicit Off");
            Targets[0].Config!.CanBeRemoved.Value = BottleShipsPlugin.Toggle.On;
            ApplyShipRemovalSetting(ship);
            Expect(piece.m_canBeRemoved, "On restores removal permission");
            Targets[0].Config!.CanBeRemoved.Value = BottleShipsPlugin.Toggle.Off;
            ApplyShipRemovalSetting(ship);
            Expect(!piece.m_canBeRemoved, "The current setting is read, not a cached value");
            Expect(piece.OtherState == 73, "Removal setting leaves other piece state alone");

            PathOfValheimanCompat.IsLoaded = false;
            piece.m_canBeRemoved = true;
            ApplyShipRemovalSetting(ship);
            Expect(piece.m_canBeRemoved, "PoV absent leaves ship unchanged");
            PathOfValheimanCompat.IsLoaded = true;
            ConfigBound = false;
            ApplyShipRemovalSetting(ship);
            Expect(piece.m_canBeRemoved, "Do not use unbound settings");
            ConfigBound = true;

            ship.Piece = new Piece("OtherModShip") { m_canBeRemoved = true };
            ApplyShipRemovalSetting(ship);
            Expect(ship.Piece.m_canBeRemoved, "Unmanaged ship remains unchanged");
            ship.Piece = new Piece("UnboundShip") { m_canBeRemoved = true };
            ApplyShipRemovalSetting(ship);
            Expect(ship.Piece.m_canBeRemoved, "Missing target configuration remains unchanged");
            ship.Piece = null;
            ship.ParentPiece = piece;
            ApplyShipRemovalSetting(ship);
            Expect(!piece.m_canBeRemoved, "A ship component nested under the managed piece is covered");

            ship.Piece = new Piece("OtherModShip") { m_canBeRemoved = true };
            piece.m_canBeRemoved = true;
            ApplyShipRemovalSetting(ship);
            Expect(piece.m_canBeRemoved && ship.Piece.m_canBeRemoved,
                "An unrelated ancestor does not override the ship's own piece");
            ship.Piece = null;
            ship.ParentPiece = null;
            ApplyShipRemovalSetting(ship);
            ApplyShipRemovalSetting(null!);
            Expect(true, "Missing ship/piece is harmless");
            return count;
        }
    }

    // Controlled game/config boundaries. No Unity, networking, or Harmony is run.
    internal sealed class Ship
    {
        internal enum Speed { Stop, Back, Slow, Half, Full }
        internal Speed Gear;
        internal Piece? Piece;
        internal Piece? ParentPiece;
        public Speed GetSpeedSetting() => Gear;
        public T? GetComponent<T>() where T : class => Piece as T;
        public T? GetComponentInParent<T>() where T : class => ParentPiece as T;
    }
    internal sealed class Piece
    {
        internal readonly GameObject gameObject;
        internal bool m_canBeRemoved;
        internal int OtherState = 73;
        internal Piece(string name) { gameObject = new GameObject(name); }
    }
    internal sealed class GameObject
    {
        internal readonly string Name;
        internal GameObject(string name) { Name = name; }
    }
    internal static class Utils
    {
        internal static string GetPrefabName(GameObject obj) => obj.Name.Replace("(Clone)", "");
    }
    internal static class PathOfValheimanCompat
    {
        internal static bool IsLoaded;
        internal static bool Anchored;
        internal static bool IsAnchored(Ship ship) => IsLoaded && Anchored;
    }
    internal static class BottleShipsPlugin
    {
        internal enum Toggle { Off, On }
    }
    internal sealed class ConfigEntry<T>
    {
        internal T Value;
        internal ConfigEntry(T value) { Value = value; }
    }
    internal sealed class BottleConfig
    {
        internal readonly ConfigEntry<BottleShipsPlugin.Toggle> CanBeRemoved =
            new ConfigEntry<BottleShipsPlugin.Toggle>(BottleShipsPlugin.Toggle.Off);
    }
    internal sealed class BottleTarget
    {
        internal readonly string PiecePrefab;
        internal BottleConfig? Config = new BottleConfig();
        internal BottleTarget(string prefab) { PiecePrefab = prefab; }
    }
}
'@
$fixture = $fixture.Replace('/* PADDLING_RULE */', $paddlingRule).
    Replace('/* REMOVAL_RULE */', $removalRule).
    Replace('/* PREFAB_COMPARISON */', $prefabComparison)
Add-Type -TypeDefinition $fixture
$cases = [BottleShipsPaddlingCompatChecks.Checks]::Run()
[pscustomobject]@{
    ProductionRuleCasesPassed = $cases
    SourceGuardPathsChecked = $guardedPaths.Count + 1
    SourcePatchOrderChecked = $true
    Note = 'Compiles production rules against controlled boundaries; source wiring checks do not execute Unity, Harmony, networking, or the optional mod.'
} | ConvertTo-Json

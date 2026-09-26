# Changelog

## 1.1.14

- Added the server-synced `Ashlands Field Repair Prefabs` whitelist for Wood repairs in boiling waters. Defaults to `VikingShip_Ashlands` (Drakkar); an empty list allows no field repairs in those waters.
- Checks the ship's position using the game's Ashlands ocean damage-region check, including Expand World Data's custom boiling regions without additional biome configuration or a hard dependency.
- Preserved repairs within station range and existing no-cost/no-workbench exceptions. Blocked field repairs consume no Wood, hide the repair-cost indicator, and display an English or Korean message.
- Updated the BepInExPack dependency to `5.4.2351`.

## 1.1.13

- Added large-map icons for boats and carts built by your current character, using each vehicle's build icon. Compatible Ship- and Vagon-based vehicles are included.
- Vehicle positions refresh when the large map opens, including vehicles outside your loaded area. Pins are temporary, do not appear on the minimap, and do not track movement while the map stays open.
- Added a map key hint and the client-only `Toggle My Vehicle Pins Key` setting at the bottom of General, defaulting to `V`. Pins start visible whenever the map opens; hiding them does not save a setting.
- Added English and Korean vehicle-map messages and updated the mod description and map documentation.

## 1.1.12

- Updated the bundled ServerSync for Valheim 1.0.7, removing obsolete routed-RPC field access while preserving configuration identifiers, locking, version checks, and the existing wire format.
- Recompiled BottleShips against the original Valheim 1.0.7 game assemblies and updated changed item initialization, player message, and effect creation calls.
- Replaced direct compilation against private game members with public APIs, cached accessors, or Harmony field injection while preserving the existing patch targets, priorities, and state restoration.

## 1.1.11

- Reduced repeated build-station lookups and resource parsing when applying piece settings to existing world objects, while preserving per-piece resource data and field-scoped updates.
- Reduced redundant visibility toggles and transform updates in the field ship-repair Wood indicator.
- Prevented duplicate Power Paddling and field-repair setting callbacks when configurations are rebound, and detached those callbacks on plugin shutdown.
- Simplified recipe application and grouped ballista ammunition patches with their existing handling code.

## 1.1.10

- Fixed Recycle_N_Reclaim compatibility for BottleShips' dynamically registered recipes. Bottle items now appear in Reclaim tabs after recipe changes without adding a hard dependency.
- Replaced `Ballista Ammo Capacity` with the server-synced `Ballista Ammo Capacity Multiplier`. It defaults to `1`, accepts `1-20`, and multiplies the captured original capacity while preserving stored excess ammunition when lowered. The removed absolute-capacity setting is not migrated.

## 1.1.9

- Fixed `Extended Cart Interaction` compatibility with mods such as OdinHorse that replace `Vagon.FixedUpdate`. The wider attach distance now applies only during the requested local player's `Vagon.CanAttach` checks and is restored immediately afterward, preserving other attachment targets and mod behavior.
- Reordered the General settings so `Battering Ram Size` appears before `Ballista Targeting Tweaks`.

## 1.1.8

- Added `Ballista Ammo Capacity`, a server-synced `0-1000` setting that raises the vanilla ballista's actual ammunition limit for its hover display and compatible fill-all mods. Existing excess ammunition is preserved when the setting is lowered, owner-side RPCs reject overfill, and large stores are returned in item stacks when the ballista is destroyed.
- Expanded `Extended Cart Interaction` from carts to valid `Vagon`-based vehicles, including battering rams and catapults.
- Fixed repeated configuration application progressively shrinking the Battering Ram by preserving its original transform baseline for the process lifetime.

## 1.1.7

- Expanded `Ballista Targeting Tweaks` protection to players, tamed creatures, and `PlayerSpawned`-faction creatures across normal selection, trophy fallback, and network target assignment while continuing to select valid enemies behind protected targets.

## 1.1.6

- Restricted Power Paddling to the paddle-driven Back and Slow gears. Half and Full now use sail propulsion without consuming paddlers' stamina, adding rowing force, or applying the Power Paddling FOV effect.

## 1.1.5

- Added field ship repair outside the required build-station range. Repairs consume one Wood per configured amount of missing durability, rounded up; `Build Station = None` ships use a Workbench as the repair fallback. `Ship Repair Durability Per Wood` defaults to `200`, accepts `0-1000`, and is disabled at `0`.
- Added a hammer-hover Wood requirement indicator with corrected orientation and a large centered amount while preserving the vanilla durability-bar display conditions.
- Replaced the fixed Power Paddling toggle with `Power Paddling Bonus Per Player`. It defaults to `0.5`, accepts `0-1`, and is disabled at `0`; each active player still consumes 10 stamina per second and receives an individual smooth 10-degree FOV increase.
- Removed `Camera Max Distance` and its distance patches so BottleShips no longer controls ship camera zoom.
- Reordered the Configuration Manager display to `01 - General`, `02 - Ship Tweaks`, then bottle sections `03-13` without changing persisted config identities.
- Applied live configuration changes only to the affected bottle and field, avoiding unrelated recipe or piece settings being reapplied.
- Applied BottleShips defaults before DataForge database patches and retained bounded, field-scoped retries for unavailable or transiently failing game data, reducing delayed full-reapply conflicts with downstream overrides.

## 1.1.4

- Added cooperative Power Paddling for the helmsman and seated passengers. Each active player holds Run, consumes a base 10 stamina per second, contributes 50% additional paddling force at Back, Slow, Half, or Full, and receives a smooth 10-degree FOV increase.
- Simplified ship configuration by applying tweaks to every `Ship` component and replacing the separate scope, sailing, paddling, steering, and passive passenger-bonus settings with `Ship Power Multiplier`. Removed legacy keys are ignored.
- Made the default `Camera Max Distance = 6` a no-op so other camera and ship mods retain control until a larger distance is configured.
- Required BottleShips on both the server and connecting clients to prevent network-prefab and ownership mismatches.
- Deferred database-triggered configuration application to a next-frame, coalesced worker and isolated apply failures so late initialization and transient prefab errors do not disrupt the game lifecycle.

## 1.1.3

- Updated the bundled ServerSync to v1.19 so server-admin changes made through Configuration Manager, including `Build Station = None`, are written to the dedicated-server config file and persist across restarts.
- Added ship scope, camera distance, minimap exploration radius, sailing, paddling, steering, and passenger paddling bonus settings.
- Fixed bottle settings and recipes being lost or retaining stale prefab references when game databases and scenes are recreated.
- Restricted extended cart interaction to carts, made troll trap reload survive unloads, and kept the vanilla ballista target-update flow while adding trophy fallback targeting.
- Improved external translation fallback and removed the unused runtime icon-rendering path.

## 1.1.2

- Rebuilt the bottle asset bundle with D3D11 and Vulkan shader support for better Proton/Vulkan compatibility.

## 1.1.1

- Reworked extended cart interaction to use the normal Use key without adding a separate tooltip.
- You can just press [E] near cart to attach/detach to it.

## 1.1.0

- Added configurable bottle recipes and build recipes through config. (Removed the dependency on WackysDatabase)
- Added extended cart interaction using the normal Use key.
- Added troll trap auto reload, ballista targeting tweaks, and battering ram size control.
- Updated bottle icon snapshots.

## 1.0.5

- readme fix, dependency bump

## 1.0.1

- Rebuilt the whole asset in Unity6
- Slight optimization for some asset
- readme fix

## 1.0.0

- Initial release

# Changelog

## 1.1.4

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

## BottleShips

BottleShips adds 11 bottle items, each containing a ship, cart, battering ram, catapult, portal, trap, or ballista. Use a bottle as the build resource and place the full-size piece when you need it.

BottleShips must be installed on the server and every connecting client because it adds synchronized network prefabs and gameplay settings.

![](https://i.ibb.co/N2Bw4btD/Screenshot-2025-08-27-121337.png)

* Each bottle is tied to the matching piece.
* Pieces can be configured to use the bottle recipe or their original build recipe.
* Build stations, build resources, bottle recipes, bottle weight, stack size, teleportability, and removal behavior are all configurable.

![](https://i.ibb.co/Tqh7nHzN/fetchup.gif)

* Keep bottles on item stands and grab one whenever you are ready to build.
* No more digging through containers for every ship, portal, or siege piece.

![](https://i.ibb.co/MDrpgXBp/Survival.gif)

* If a ship breaks, carrying a spare bottle lets you rebuild it quickly.

![](https://i.ibb.co/8n2WS3sV/Screenshot-2025-08-27-104256.png)

![](https://i.ibb.co/JjWjhwXs/Screenshot-2025-08-27-104245.png)

* Bottles on item stands.

![](https://i.ibb.co/XB3b8jM/Screenshot-2025-08-27-003527.png)

* Raft, karve, longship, and drakkar bottles.

![](https://i.ibb.co/sJk9qYg8/Screenshot-2025-08-27-003717.png)

* Cart, battering ram, and catapult bottles.

![](https://i.ibb.co/yc6NsMFS/Screenshot-2025-08-27-003754.png)

* Wood portal and stone portal bottles.

![](https://i.ibb.co/svyLCbRJ/Screenshot-2025-08-27-003829.png)

* Troll trap and ballista bottles.

![](https://i.ibb.co/4R4bLrwh/Screenshot-2025-08-27-010540.png)

* Bottle recipes at the workbench.

![](https://i.ibb.co/kgK6vQw4/Screenshot-2025-08-27-010414.png)

* Bottle recipes at the forge.

![](https://i.ibb.co/sdg1sDM8/Screenshot-2025-08-27-010317.png)

* Bottle recipes at the artisan table.

![](https://i.ibb.co/2YmzVjPw/Screenshot-2025-08-27-005812.png)

* Bottles can be configured with weight, stack size, and teleportability.

![](https://i.ibb.co/TMXrG2bc/Screenshot-2025-08-27-004745.png)

![](https://i.ibb.co/Pv7kQCDr/Screenshot-2025-08-27-004620.png)

![](https://i.ibb.co/8gsvRL2q/Screenshot-2025-08-27-004533.png)

![](https://i.ibb.co/qMdjS4py/Screenshot-2025-08-27-004201.png)

![](https://i.ibb.co/sYW6Srf/Screenshot-2025-08-27-004246.png)

* Build full-size pieces using bottles, with optional station requirements.

### Configuration

BottleShips is configured through the normal BepInEx config file. No YAML editing is required.

General options:

* `Extended Cart Interaction`: client-only toggle that expands the cart's normal Use interaction to nearby carts without adding a separate hotkey or tooltip. Default: `On`.
* `Troll Trap Auto Reload Seconds`: automatically rearms `piece_trap_troll` after it triggers. Default: `5`. Range: `0-10`, where `0` disables it.
* `Ballista Targeting Tweaks`: trophy targets are prioritized, but the ballista can still attack other valid enemies. Players are never selected as targets.
* `Battering Ram Size`: scales newly placed battering rams. Default: `0.75`. Range: `0.5-1`. Existing placed battering rams are not changed.

Ship tweaks apply to every object with a `Ship` component, including ships added by other mods.

Ship tweak options:

* `Explore Radius Multiplier`: multiplies minimap exploration radius while aboard a ship. Default: `1`.
* `Ship Power Multiplier`: scales wind-driven force at Half or Full and manual propulsion plus paddle steering at Slow or Back. It does not multiply speed-based steering or directly set top speed. Default: `1`.
* `Power Paddling Bonus Per Player`: the helmsman and passengers seated in ship chairs can each hold Run to consume a base 10 stamina per second and add the configured share of the ship's globally scaled paddling force. Passive occupants add nothing. It works only at Back or Slow and is unavailable at Half or Full while the sails are deployed. Each active paddler gets a smooth `+10` effect on their own camera FOV; other paddlers do not stack additional FOV on that player. Default: `0.5` (50% per active player). Range: `0-1`, where `0` disables Power Paddling.
* `Ship Repair Durability Per Wood`: allows the repairing player to fully repair a ship outside its required build-station range by spending one Wood per configured amount of missing durability, rounded up. Station range is checked from the player's position. The configured build station is used when present; ships with `Build Station = None` use a Workbench as the repair-station fallback. Repairs within range remain free. While the hammer's repair piece is selected and a damaged field-repairable ship is targeted, the required amount is centered over the Wood icon beside the vanilla durability bar. Default: `200`. Range: `0-1000`, where `0` disables this feature and preserves vanilla repair behavior.

BottleShips disables its minimap, handling, and Power Paddling tweaks when RockTheBoat is installed to avoid applying the same features twice. Field ship repair remains available.

Each bottle section can configure:

* `Use Original Build Recipe`
* `Build Station`
* `Can Be Removed`
* `Build Resources`
* `Weight`
* `Stack`
* `Teleportable`
* `Recipe Enabled`
* `Recipe Station`
* `Recipe Resources`

Synced options are enforced from the server when BottleShips is installed on both server and clients. Client-only options, such as extended cart interaction, remain local.

### Localization support
* There should be `BottleShips.English.yml` in plugins folder. Translate it to your language and put it anywhere inside of the Bepinex folder
### List of prefabs
* Raft_bottle
*  Karve_bottle
*  VikingShip_bottle
*  VikingShip_Ashlands_bottle
*  Cart_bottle
*  BatteringRam_bottle
*  Catapult_bottle
*  portal_wood_bottle
*  portal_stone_bottle
*  piece_trap_troll_bottle
*  piece_turret_bottle

### Special Thanks
* Many thanks to GraveBear for kindly teaching me Unity and for all the troubleshooting help!
* A huge thanks to Azumatt for Modding tutorial videos, Itemmangertemplate and all the help!

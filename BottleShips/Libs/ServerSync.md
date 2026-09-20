# Vendored ServerSync

BottleShips pins `ServerSync.dll` to the reviewed local compatibility revision
`valheim-1.0.7-r1`, applied on 2026-09-10. This is not an official upstream release.

- Target: Valheim 1.0.7, Windows x64 client build 25185596 and dedicated server build 25185644.
- Upstream: https://github.com/blaxxun-boop/ServerSync/tree/c57c2aa54e07cdcc7630d6068699ea781622323e
- License: MIT-0.
- SHA-256: `b4dd786997f4e90d770f09ef3e9d64154754fe7e8edfb4841795751895b35846`.
- Size: 49,664 bytes; assembly version: 1.0.0.0; file version: 1.0.0.1.
- Previous input SHA-256: `166956302a294e224474b26f4c7d58409084ad3f48bd0af1feb7551f229c8f60`.
- Local source, build procedure, input hashes, source diff and verification reports:
  `C:\Users\blizz\.codex\references\valheim\integrations\serversync\versions\valheim-1.0.7-r1`.
  The vendored DLL is self-contained; builds do not depend on that archive path.

## Compatibility changes

1. Compiled against original, unmodified Valheim 1.0.7 assemblies. The three old
   `ZRoutedRpc.Everybody` field reads become constant instructions.
2. Three administrator checks use the public `ZNet.IsAdmin(string)` API with the
   existing policy. The separate administrator-file reload path is retained.
3. During configuration synchronization, `BufferingSocket` also queues `PlayerList`,
   `HistoricalPlayerList`, `AdminList` and `NetTime`, preserving their order after
   `PeerInfo`. Existing control-message passthrough and FIFO flushing remain.

The configuration identifiers, locking policy, version checks and wire format are
preserved. No support for old Valheim 0.221.x runtimes is added.

## Build and validation boundaries

`BottleShips.csproj` references this relative `Libs/ServerSync.dll` and copies it to
the output directory. `ILRepack.targets` internalizes it into `BottleShips.dll`.
There is no additional binary patch step. Deploy only the final BottleShips DLL;
do not install ServerSync as a separate plugin.

The common revision passed builds against original client/server assemblies,
static API/patch checks and 19 isolated tests per target. Those are prior library
results, not BottleShips game-runtime validation. The subsequent 1.0.7 consumer
patch switched BottleShips to original game references and explicit private-member
access. Library verification alone does not establish whole-mod compatibility.

For a consumer build, verify the copied ServerSync input hash, inspect the final
merged implementation for obsolete field reads, administrator calls and buffered
message identifiers, then compare the deployed DLL hash with the final output.
Real host/dedicated connections, administrator and non-administrator configuration
changes, lock enforcement, version mismatch, reconnects, large transfers and any
used Steam/PlayFab crossplay remain runtime checks. Test with other mods embedding
ServerSync as well as a minimal mod set.

## BottleShips integration check — 2026-09-10

- `dotnet build BottleShips.sln -c Debug -p:DeployToGame=true` failed on the
  existing YamlDotNet/IDeserializer reference resolution issue.
- Visual Studio 2022 MSBuild with `/restore /t:Build /p:Configuration=Debug
  /p:DeployToGame=true` succeeded, including ILRepack and automatic game deployment.
- Both vendored and output ServerSync inputs matched the pinned SHA-256 above.
- Mono.Cecil comparison of all 200 ServerSync methods in the final BottleShips DLL
  matched the pinned method instructions, locals and exception handlers. The old
  baseline retained three obsolete `Everybody` reads as a negative control; the
  final DLL contained zero, three `ZNet.IsAdmin` calls and no external ServerSync
  assembly reference.
- Built and deployed BottleShips DLL SHA-256 matched:
  `83c2030dd30cf5bd0afdd15a6ac9363d7ed2606f091afa3b9c8086d24dae92dd`.
- No actual game or network session was run. The common revision's isolated tests
  were reviewed, not rerun. This record predates the subsequent BottleShips 1.1.12
  compatibility release build.

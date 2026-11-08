# UMMBridge

Run [UnityModManager](https://github.com/newman55/unity-mod-manager) (UMM) mods under [MelonLoader](https://github.com/LavaGang/MelonLoader).

## Overview

UMMBridge loads UMM inside MelonLoader, transparently bridging the two mod ecosystems. It intercepts `UnityModManager.modsPath`, redirecting to `UMMMods/`, then boots UMM and its UI alongside MelonLoader's plugin pipeline.

## Dual-Harmony Architecture

UMM ships **0Harmony 2.3.6** (with its own MonoMod) while MelonLoader uses **HarmonyX** (a different MonoMod). Two MonoMod runtimes cannot both detour one method entry, and UMM's MonoMod.Core does not run on Apple Silicon at all. UMMBridge therefore lets **HarmonyX apply every patch**: UMM mods keep the 0Harmony 2.3.6 API they were built against, but none of their patches is detoured by UMM's runtime.

| Layer | Behavior |
|-------|----------|
| **Cecil rewriting** (`Assembly.LoadFrom` interception) | Renames a mod's `0Harmony` reference to `0Harmony_UMM`, the in-memory copy of UMM's Harmony; also rewrites hardcoded `Mods` paths and `Assembly.Location` calls |
| **0Harmony_UMM redirects** | `PatchFunctions.UpdateWrapper` → `Bridge.UpdateWrapper`, which mirrors the method's UMM prefixes/postfixes/finalizers into HarmonyX under each mod's own Harmony ID and runs its UMM transpilers through one HarmonyX transpiler (`CodeInstruction` converted at the boundary). `PatchTools.DetourMethod` (reached by reverse patches only) → MelonLoader's MonoMod `NativeDetour`. `AccessTools.Identifiable` → identity |
| **Hosted replacements** | Libraries such as JALib build a complete replacement method from a copy of UMM's original `UpdateWrapper`. That copy's detour call goes to `Bridge.ReplaceMethod`, which hosts the replacement inside HarmonyX |

### Coexistence

| Scenario | Result |
|----------|--------|
| UMM mod patch + MelonLoader mod patch on one method | ✅ Patches stack on HarmonyX, transpilers included |
| UMM mod patch + UMM mod patch | ✅ Each mod keeps its own Harmony ID; `UnpatchAll` removes only that mod's patches |
| JALib `JAPatch` + MelonLoader mod patch | ✅ JALib's replacement runs inside HarmonyX; MelonLoader prefixes/postfixes/finalizers wrap it |
| A UMM patch that fails to build | ✅ Rolled back from HarmonyX, so it cannot break later patches on that method |

### Limitations

- UMM transpilers of one method run as a single batch inside HarmonyX, so they are ordered against MelonLoader transpilers as a group.
- A method replaced by JALib ignores MelonLoader transpilers (its body is JALib's replacement); prefixes, postfixes and finalizers still apply.
- Harmony 1.x UMM mods are not supported.

## Usage

1. Drop the compiled DLL into ADOFAI's `Plugins/` directory
2. Place UMM mods into `UMMMods/` (created automatically on first launch)
3. Open the UMM window and click **Mods Dir** at the top-right

## Build

```
dotnet build UMMBridge\UMMBridge.csproj -c Release
```

Or open `UMMBridge.slnx` in Visual Studio 2022. The `Deploy` configuration outputs directly to the game's Plugins directory.

<img width="1282" height="752" alt="image" src="https://github.com/user-attachments/assets/04447190-7fb8-4254-8091-63512e2deda0" />

---
name: as2-build-verify
description: Build, deploy and verify Audiosurf 2 mod changes - the net35 build setup, where each artifact goes in the game folder, and the launch-and-read-logs loop. Use when building or deploying AS2.Bootstrap, AS2.ModApi, AS2.Probe or AS2-SkinSettings, or when a net35 build fails.
---

# Build and verify

## Build

From the repo root:

```bash
dotnet build src/AS2.ModApi/AS2.ModApi.csproj -c Release
```

Build `AS2.ModApi` **before** `AS2-SkinSettings`, which references its DLL from `BepInEx\plugins\`.

Non-default Steam library:

```bash
dotnet build src/AS2.ModApi/AS2.ModApi.csproj -c Release -p:AudiosurfDir="D:\Steam\steamapps\common\Audiosurf 2"
```

`Directory.Build.props` holds all shared settings: `net35`, the `Audiosurf2_Data\Managed` reference
paths, and the MSB3644 workaround. Add a game assembly by adding a `Reference` with a `HintPath`
under `$(ManagedDir)` and `<Private>false</Private>` — there is no NuGet in this repo by design, so
the build is offline and matches exactly what the game loads.

### Build failures

| Error | Cause and fix |
| --- | --- |
| `CS0305: Action<T> requires 1 type arguments` | `Action`, `Action<T1,T2>`, `Func<>`, `HashSet<T>` are in **System.Core**, not mscorlib. Add the reference |
| `CS0246: HashSet<> not found` | Same |
| `CS0117` on `string.IsNullOrWhiteSpace` | net35. Use `Str.IsBlank` |
| `MSB3644: framework .NETFramework 3.5 not found` | The `_TargetFrameworkDirectories` workaround in `Directory.Build.props` was removed or bypassed |
| `MSB4025: XML comment cannot contain '--'` | A `--` inside a `.csproj` comment. Rephrase |

Full list with symptoms: [docs/gotchas.md](../../../docs/gotchas.md).

## Deploy

```
build/AS2ModLoader/AS2.Bootstrap.dll  ->  <game>\AS2ModLoader\
build/plugins/AS2.ModApi.dll          ->  <game>\BepInEx\plugins\
build/plugins/AS2.Probe.dll           ->  <game>\BepInEx\plugins\   (dev only; remove when done)
```

`AS2-SkinSettings` copies itself into `BepInEx\plugins\` from its own csproj on build
(`..\AS2-SkinSettings\AS2SkinSettings\AS2SkinSettings.csproj`).

```bash
GAME="/c/Program Files (x86)/Steam/steamapps/common/Audiosurf 2"
cp build/plugins/AS2.ModApi.dll "$GAME/BepInEx/plugins/"
```

**Never copy BepInEx's `winhttp.dll` or its `doorstop_config.ini` into the game.** They belong to
the community patch and to a different Doorstop generation; see
[docs/loading-chain.md](../../../docs/loading-chain.md).

## Verify

The game is the only test harness, and a human launches it — starting the executable from a tool
instead of through Steam risks a license popup. Each round costs a manual launch, so batch
everything into one.

1. Say exactly what to do (e.g. "open the skin selector, click through a few skins, open Skin
   Settings, change a value, play a song, quit"). Playing a song is the only way to create a
   Lua state.
2. Read the logs:

```bash
GAME="/c/Program Files (x86)/Steam/steamapps/common/Audiosurf 2"
cat "$GAME/AS2ModLoader/bootstrap.log"
cat "$GAME/BepInEx/LogOutput.log"
ls "$GAME"/preloader_*.log 2>/dev/null && cat "$GAME"/preloader_*.log || echo "none (good)"
```

Expected output, benign noise to ignore, and the end-to-end settings check are all in
[docs/verification.md](../../../docs/verification.md).

## Prefer cold checks where possible

Save launches. Assembly shape can be confirmed without running the game:

```powershell
$a = [System.Reflection.Assembly]::ReflectionOnlyLoadFrom("C:\...\AS2.Bootstrap.dll")
$a.ImageRuntimeVersion     # expect v2.0.50727
$a.GetTypes() | ForEach-Object { $_.FullName }
```

Pure filesystem or string logic can be prototyped in Python against the real game folder first.

## Safety

Everything installed is additive (`AS2ModLoader\`, `BepInEx\`). On the Steam launch-option install
the bootstrap does not touch `doorstop_config.ini` at all; on the ini install it contests one key.

To return an install to stock, set the key back to the community patch's own target explicitly:

```ini
targetAssembly=Audiosurf2_Data\Updater\PatchUpdaterPreloader.dll
```

**Do not restore a `doorstop_config.ini.*-backup` file without reading it first.** A game folder that
has had more than one loader in it accumulates several, and they are named for when they were taken,
not for what they contain — a backup called "pre-bepinex" may well hold a *previous* mod loader's
target rather than the patch's, and restoring it puts that loader back in the doorstop slot.

Do not test on a copied game folder: Audiosurf 2 needs
`steam_api.dll`/CSteamworks, so a copy outside the Steam library will not launch properly anyway.

# Environment

Everything here was measured on the live install, not taken from documentation. The "how it was
verified" column is there so you can re-check rather than trust this file after a game update.

## Runtime

| Fact | Value | How it was verified |
| --- | --- | --- |
| Unity | 2017.4.40f1 | `UnityPlayer.dll` FileVersion `2017.4.40.7214086`; string in `Audiosurf2_Data\globalgamemanagers`; BepInEx logs `Running under Unity v2017.4.40.7214086` |
| Scripting backend | legacy Mono, 2.0 profile | `Audiosurf2_Data\Mono\` exists with `EmbedRuntime\mono.dll`; **no** `MonoBleedingEdge` folder |
| CLR | 2.0.50727.1433 | BepInEx logs `CLR runtime version` |
| Architecture | x64 | PE machine `0x8664` in `Audiosurf2.exe`, `UnityPlayer.dll`, `winhttp.dll` |
| Dynamic methods | available | BepInEx logs `Supports SRE: True` — this is what makes Harmony possible |
| Managed profile | .NET 3.5 | built assemblies carry runtime string `v2.0.50727` |

`Supports SRE: True` is the load-bearing one. Harmony builds patched methods at runtime with
`System.Reflection.Emit`; Unity's IL2CPP and .NET Standard profiles cannot, which is why the same
approach does not transfer to every Unity game.

## Tooling versions

| Component | Version | Note |
| --- | --- | --- |
| BepInEx | 5.4.23.5 (win_x64, Unity Mono) | BepInEx 6 is a different plugin API; do not "upgrade" casually |
| HarmonyX | **2.9.0** | Bundled in BepInEx 5. Predates `__args` — see [gotchas](gotchas.md) |
| UnityDoorstop (installed) | **3.4.1** | Shipped by the community patch, resolves `*:Main` |
| UnityDoorstop (in BepInEx zip) | 4.5.0 | **Not installed.** Resolves `Doorstop.Entrypoint:Start` |
| Lib.Harmony (alternative) | 2.4.2 | Has a `net35` build too; unused here because BepInEx bundles HarmonyX |

## Files in the game root

| File | Owner | Notes |
| --- | --- | --- |
| `winhttp.dll` | community patch | Doorstop 3.4.1, x64, md5 `c53442c808a27f6178024a8c2e51bde1`. **Never modify.** |
| `version.dll` | community patch | **x86 in an x64 process — never loaded.** Dead weight; ignore it |
| `doorstop_config.ini` | community patch | Untouched on the launch-option install; one key contested on the ini install. Reverted by patch updates |
| `Audiosurf2_Data\Updater\PatchUpdaterPreloader.dll` | community patch | Launches `PatchUpdater.exe`; the bootstrap chain-loads it |
| `AS2ModLoader\` | us | `AS2.Bootstrap.dll` + `bootstrap.log` |
| `BepInEx\` | us | core, plugins, config, `LogOutput.log` |
| `ModSettings\` | AS2-SkinSettings | Holds only the player's `settings.json`; the source lives in a separate repo |

`version.dll` being 32-bit in a 64-bit process is worth remembering: it looks like a second doorstop
proxy and is not one. Only `winhttp.dll` is live.

## What the community patch overwrites

The patch extracts its payload over the install on update. Confirmed present in its zip:
`winhttp.dll`, `doorstop_config.ini`, `Audiosurf2_Data\Managed\`, `Audiosurf2_Data\Updater\`.

Consequences that shape the whole design:

- Anything written under `Audiosurf2_Data\` is temporary. Do not put mod files there.
- `doorstop_config.ini` edits are reverted. The durable install is the Steam launch option; see
  [loading-chain.md](loading-chain.md).
- `Assembly-CSharp.dll` is replaced wholesale, so member names can change under us. This is why
  patches resolve targets by name and degrade one-at-a-time instead of failing as a batch.

## Re-verifying after a game or patch update

```bash
# Architecture + doorstop version/descriptor
python -c "
import struct,re
d=open('winhttp.dll','rb').read()
off=struct.unpack_from('<I',d,0x3C)[0]
print('machine', hex(struct.unpack_from('<H',d,off+4)[0]))
for s in sorted(set(x.decode('ascii','ignore') for x in re.findall(rb'[ -~]{4,}',d))):
    if re.match(r'^\d+\.\d+\.\d+\.\d+$',s) or ':Main' in s or 'Entrypoint' in s: print(repr(s))
"
```

If the descriptor is no longer `*:Main`, the bootstrap's entry point convention changed — it already
carries a `Doorstop.Entrypoint.Start()` for that case, so it should keep working either way.

To re-check game member names, build and deploy `AS2.Probe`; see
[the hook skill](../the wiki).

## Binary probing note

When string-scanning a .NET assembly, remember the two heaps differ in encoding:

- **Type/method/field names** live in `#Strings`, UTF-8 — a plain ASCII regex finds them.
- **String literals** (e.g. `"LuaSkinFunctionsRegistered"`) live in `#US`, **UTF-16** — an ASCII
  scan will not find them and their absence proves nothing.

This caused a false "member not found" during this repo's development. Scan both encodings.

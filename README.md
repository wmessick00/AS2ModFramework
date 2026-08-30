# AS2ModFramework

[![Cold checks](https://github.com/wmessick00/AS2ModFramework/actions/workflows/cold-checks.yml/badge.svg)](https://github.com/wmessick00/AS2ModFramework/actions/workflows/cold-checks.yml)

A BepInEx and Harmony modding foundation for Audiosurf 2. Mods hook the game properly, and more than
one of them can be installed at a time.

The community patch owns the game's single UnityDoorstop slot. A code mod used to have to take that
slot for itself — one mod at a time, and a patch auto-updater that no longer ran. This bridges the
two instead.

**Documentation is on the [wiki][wiki].** Start at [Your First Plugin][wiki-first].

> **Status:** working and in use. Not on Thunderstore or Nexus yet.

## Requirements

- Audiosurf 2 on Steam
- The **[Audiosurf 2 Community Patch][patch]**. It ships the `winhttp.dll` UnityDoorstop this
  attaches to, so without it there is no doorstop to bridge

BepInEx 5.4.23.5 `win_x64` is bundled in the release archive. Do not install BepInEx over the top
yourself — its installer replaces the community patch's doorstop and breaks both projects at once.
BepInEx 6 is a different plugin API and will not work.

## Installing

No .NET SDK, no build step, no PowerShell. Three steps.

**1. Download and verify.** Get `AS2ModFramework-<version>.zip` and its `.sha256` from the
[latest release][rel]:

```powershell
Get-FileHash .\AS2ModFramework-<version>.zip -Algorithm SHA256
```

Compare against the `.sha256` file and the release notes. Worth doing — the archive drops a DLL into
a folder the game loads code from.

**2. Extract into the game folder,** so `AS2ModLoader\` and `BepInEx\` sit next to `Audiosurf2.exe`.
In Steam: right-click Audiosurf 2, Manage, Browse local files.

**3. Add a Steam launch option** — Properties, General, Launch Options:

```
--doorstop-target "C:\Program Files (x86)\Steam\steamapps\common\Audiosurf 2\AS2ModLoader\AS2.Bootstrap.dll"
```

Swap in your own folder. Full absolute path, and it stays quoted. A wrong path reports nothing — the
game just starts unmodded.

Installed this way the framework **adds two folders and modifies no game file**.

Launch from Steam, then check `AS2ModLoader\bootstrap.log` ends with `Handed off to BepInEx` and
`BepInEx\LogOutput.log` ends with `Chainloader startup complete`.

The ini fallback, the split packages and uninstalling: [Installing][wiki-install].

## What this framework does not touch

Two questions get asked about any Audiosurf 2 mod loader. Both are checked by a script rather than
by review.

**It cannot change what the game does.** Every Harmony patch is a *postfix*, applied through one
helper that passes `null` for the prefix. No prefixes, no transpilers, no patch writes back to
`__result`. Four types are patched, each listed in
[`src/AS2.ModApi/Patches.cs`](src/AS2.ModApi/Patches.cs).

**It reads the game and never writes to it.** `AS2.ModApi` broadcasts nothing on the game's event
bus, writes no game field, calls no reflective setter, and calls into the game only through a short
reviewed allowlist. No public member exposes a game object, so a subscriber gets immutable copies.

[`tools/verify-invariants.ps1`](tools/verify-invariants.ps1) checks all of that against the compiled
DLL with Mono.Cecil, and [`tools/pack.ps1`](tools/pack.ps1) runs it before it archives anything.
Check the DLL in the archive yourself:

```powershell
.\tools\verify-invariants.ps1 -Assembly "<game>\BepInEx\plugins\AS2.ModApi.dll"
```

This is a statement about *this framework*, not about what modes may do. Custom modes scoring
themselves is a designed game feature.

## Building from source

For contributors. Audiosurf 2 must be installed, since every project compiles against the game's own
assemblies.

```
dotnet build src/AS2.ModApi/AS2.ModApi.csproj -c Release
```

`net35` against `Audiosurf2_Data\Managed`, so .NET 3.5 BCL only. Steam library elsewhere? Pass
`-p:AudiosurfDir="D:\...\Audiosurf 2"`.

The cold checks are the one part that builds without the game:

```
dotnet run --project tests/AS2.ModApi.Tests       # 245 checks
dotnet run --project tests/AS2.Bootstrap.Tests    # 68 checks
```

Releases are built locally, never on CI — `AS2.ModApi` compile-references the game's own assemblies,
which are not redistributable. [`tools/pack.ps1`](tools/pack.ps1) builds, verifies and archives;
`-Publish` uploads. BepInEx is redistributed unmodified.

`-Publish` also decides the version. `ModApiPlugin.Version` is still the one place it lives, and
still what BepInEx prints in `LogOutput.log`, but you no longer edit it: the script writes it,
commits it and pushes it before the tag is created. What it writes comes from comparing the public
surface of the DLL about to ship against the DLL in the last release — a public member removed is a
major, members added is a minor — and, when the surface is unchanged, from which paths the diff
since the last tag touched. A diff of only docs, tests or CI is **refused**, because the DLL would
be byte-identical to the one already published.

Run `-Publish` from `main`. The bump is pushed to whatever branch the checkout tracks, and the
release is tagged on the branch GitHub calls the default one, so anywhere else those are two
different commits and the tag would carry a version the tagged source does not have. The script
checks this before it builds and refuses rather than releasing half of it.

```powershell
.\tools\pack.ps1                              # pack only; changes no tracked file
.\tools\pack.ps1 -Publish                     # decide, bump, commit, push, release
.\tools\pack.ps1 -Publish -Bump major         # override the decided level
.\tools\pack.ps1 -Publish -ReleaseVersion 1.0.0   # override the version outright
```

## Compatibility

| | |
| --- | --- |
| Unity | 2017.4.40f1, legacy Mono (2.0 profile), x64 |
| CLR | 2.0.50727, `Supports SRE: True` |
| BepInEx | 5.4.23.5 (Unity Mono x64), HarmonyX 2.9.0 |
| Doorstop | 3.4.1, shipped by the community patch, `*:Main` |

`Supports SRE: True` is the load-bearing one. Harmony builds patched methods at runtime with
`System.Reflection.Emit`, which IL2CPP and .NET Standard profiles cannot do.

## Contributing

The [wiki][wiki] carries the guides — [Adding a Hook][wiki-hook] for a new event, and
[Deploying and Logs][wiki-deploy] for the build and verify loop.

The game is the harness. Verifying anything bound to Unity or BepInEx means running it.

## Credits

- **[BepInEx](https://github.com/BepInEx/BepInEx)** — the plugin framework this builds on
- **[UnityDoorstop](https://github.com/NeighTools/UnityDoorstop)** — the injection mechanism both
  this and the community patch rely on
- **The Audiosurf 2 Community Patch team** — for keeping the game alive, and for the decompilation
  work that made its internals legible

[MIT licensed](LICENSE.txt). Distributed alongside BepInEx, which is LGPL-2.1 and redistributed
unmodified — the release archive carries a `THIRD-PARTY-NOTICES.txt` naming every bundled component.

[rel]: https://github.com/wmessick00/AS2ModFramework/releases/latest
[patch]: https://audiosurf2.info/download/windows
[wiki]: https://github.com/wmessick00/AS2ModFramework/wiki
[wiki-install]: https://github.com/wmessick00/AS2ModFramework/wiki/Installing
[wiki-first]: https://github.com/wmessick00/AS2ModFramework/wiki/Your-First-Plugin
[wiki-deploy]: https://github.com/wmessick00/AS2ModFramework/wiki/Deploying-and-Logs
[wiki-hook]: https://github.com/wmessick00/AS2ModFramework/wiki/Adding-a-Hook

# AS2ModFramework

[![Cold checks](https://github.com/wmessick00/AS2ModFramework/actions/workflows/cold-checks.yml/badge.svg)](https://github.com/wmessick00/AS2ModFramework/actions/workflows/cold-checks.yml)

A BepInEx and Harmony modding foundation for Audiosurf 2. Mods hook the game properly, and more than
one of them can be installed at a time.

Audiosurf 2's community patch owns the game's single UnityDoorstop slot. A code mod used to have to
take that slot for itself, which meant exactly one mod at a time and a patch auto-updater that no
longer ran. This bridges the two: the patch keeps updating itself, BepInEx starts normally, and any
number of plugins load side by side.

> **Status:** working and in use. Not on Thunderstore or Nexus yet — install from the
> [latest release][rel], which bundles everything needed. Building from source is for contributors,
> not for installing.

**Documentation is on the [wiki][wiki].**

## Requirements

- Audiosurf 2 on Steam
- The **[Audiosurf 2 Community Patch][patch]**. Required in practice — it ships the `winhttp.dll`
  UnityDoorstop this framework attaches to, so without it there is no doorstop to bridge

BepInEx 5.4.23.5 `win_x64` is bundled in the release archive, so it is not a separate download.

Do not install BepInEx over the top yourself. Its installer replaces the community patch's doorstop
and breaks both projects at once. BepInEx 6 is a different plugin API and will not work.

## Installing

No .NET SDK, no build step, no PowerShell. Three steps.

**1. Download and verify.** Get `AS2ModFramework-<version>.zip` and its `.sha256` from the
[latest release][rel], then check the archive is the one that was published:

```powershell
Get-FileHash .\AS2ModFramework-<version>.zip -Algorithm SHA256
```

Compare against the hash in the `.sha256` file and the release notes. Worth doing — the archive
drops a DLL into a folder the game loads code from.

**2. Extract into the game folder,** so `AS2ModLoader\` and `BepInEx\` sit next to `Audiosurf2.exe`.
In Steam: right-click Audiosurf 2, Manage, Browse local files.

**3. Add a Steam launch option** — right-click Audiosurf 2, Properties, General, Launch Options:

```
--doorstop-target "C:\Program Files (x86)\Steam\steamapps\common\Audiosurf 2\AS2ModLoader\AS2.Bootstrap.dll"
```

Swap in your own folder from step 2. Full absolute path, and it stays quoted. A wrong path reports
nothing — the game just starts unmodded.

Doorstop 3.4.1 accepts that flag, and a command-line value wins over the ini file. Installed this
way the framework **adds two folders and modifies no game file**.

To confirm it worked, launch from Steam and check that `AS2ModLoader\bootstrap.log` ends with
`Handed off to BepInEx` and `BepInEx\LogOutput.log` ends with `Chainloader startup complete`.

The ini fallback, the split packages and uninstalling: [Installing][wiki-install].

## Writing a mod

```csharp
[BepInPlugin("your.mod.id", "Your Mod", "1.0.0")]
[BepInDependency(ModApiPlugin.Id)]
public sealed class YourMod : BaseUnityPlugin
{
    private void Awake()
    {
        AS2Events.SelectionChanged += (kind, key) => Logger.LogInfo("now on " + key);

        AS2GameEvents.RideScored += result =>
            Logger.LogInfo(result.Song.Display + " scored " + result.Score);
    }
}
```

| API | For |
| --- | --- |
| [`AS2Events`][wiki-events] | 5 events raised from a Harmony patch — selectors, Lua states, the settings dialog |
| [`AS2GameEvents`][wiki-game] | 26 events off the game's own bus — rides, songs, scores, tricks, traffic, Lua errors |
| [`AS2Ui`, `AS2ModMenu`][wiki-ui] | Drawing UI that matches the game's settings dialog, and one shared Mod Menu |
| [`AS2Input.Lock()`][wiki-lock] | Holding the game's input lock without stealing it from another mod |
| [`TargetResolver`][wiki-keys] | Turning the game's relative paths into stable storage keys |
| [`AS2Paths`, `AS2Store`][wiki-paths] | Where content data goes, and how to write it without losing it |

Start at [Your First Plugin][wiki-first].

## What this framework does not touch

Two questions get asked about any Audiosurf 2 mod loader. Both have concrete answers rather than
assurances, and since `AS2ModApi` 0.2.0 both are checked by a script rather than by review.

**It cannot change what the game does.** Every Harmony patch here is a *postfix*, applied through one
helper that passes `null` for the prefix.

- No prefixes, so no game method can be skipped or short-circuited
- No transpilers, so no method body is rewritten
- No patch writes back to `__result`

Four types are patched — `RingDesignManager`, `ModeSelect`, `Settings` and `LuaSandbox` — and each
patch is listed in [`src/AS2.ModApi/Patches.cs`](src/AS2.ModApi/Patches.cs).

**It reads the game and never writes to it.** `AS2GameEvents` does expose the score, which an earlier
version did not. Reading one is not setting one, and the difference is enforced rather than promised.
`AS2.ModApi`:

- broadcasts nothing on the game's event bus
- writes no game field
- calls no reflective setter
- calls into the game only through a short reviewed allowlist — the four `UIManager` members behind
  `AS2Input.Lock()`, `Messenger.AddListener`, and three path accessors
- exposes no game object on any public member, so a subscriber gets immutable copies and has nothing
  to write back through

[`tools/verify-invariants.ps1`](tools/verify-invariants.ps1) checks all of that against the compiled
DLL with Mono.Cecil, and [`tools/pack.ps1`](tools/pack.ps1) runs it before it archives anything, so a
release cannot ship a violation. Run it against the DLL in the archive yourself:

```powershell
.\tools\verify-invariants.ps1 -Assembly "<game>\BepInEx\plugins\AS2.ModApi.dll"
```

This is a statement about *this framework*, not about what modes may do. The game gives Lua
`SetLocalScore` and `SetGlobalScore` and keeps a `ScoreManager.modInChargeOfScoring` flag, because
custom modes doing their own scoring is a designed feature. The framework adds nothing to it.

## How it works

```
Audiosurf2.exe
  └─ winhttp.dll                    Doorstop 3.4.1, shipped by the community patch
       └─ AS2ModLoader\AS2.Bootstrap.dll        <- doorstop's single target slot
            ├─ 1. chain-load PatchUpdaterPreloader.dll   (patch auto-update survives)
            ├─ 2. repoint DOORSTOP_INVOKE_DLL_PATH       (BepInEx's layout depends on it)
            └─ 3. BepInEx.Preloader.dll :: Doorstop.Entrypoint.Start()
                   └─ BepInEx chainloader -> BepInEx\plugins\
```

The bootstrap exists for two reasons, either of which alone would be enough.

1. **The conventions do not match.** Doorstop 3.4.1 resolves `*:Main`. BepInEx 5.4.23 ships Doorstop
   4.5.0, and its preloader exposes only `Doorstop.Entrypoint.Start()`. Pointing the patch's doorstop
   straight at `BepInEx.Preloader.dll` does nothing
2. **Running BepInEx's installer is worse than a shim.** It overwrites `winhttp.dll` and
   `doorstop_config.ini`, both inside the community patch's update payload. The next patch update
   reverts them, BepInEx stops loading, and no code is left in-process to notice or repair it

Same constraint is why the loader ships BepInEx itself rather than depending on the published
`BepInExPack` — that pack ships the two files the community patch owns.

Full detail, including the environment variables BepInEx reads and why the other three are left
alone: [The Loading Chain][wiki-chain].

## Building from source

For contributors. Installing needs none of this. Audiosurf 2 must be installed to build at all, since
every project compiles against the game's own assemblies.

```
dotnet build src/AS2.ModApi/AS2.ModApi.csproj -c Release
```

Everything targets `net35` against `Audiosurf2_Data\Managed`, so only the .NET 3.5 BCL is available —
no `string.IsNullOrWhiteSpace`, no `Task`, no `ValueTuple`. Steam library elsewhere? Pass
`-p:AudiosurfDir="D:\...\Audiosurf 2"`.

| Project | What it is |
| --- | --- |
| `src/AS2.Bootstrap` | Doorstop entry point. Starts BepInEx and keeps the patch's auto-updater working |
| `src/AS2.ModApi` | BepInEx plugin. The Harmony patches that turn the game's internals into events |
| `src/AS2.Probe` | Development probe, not shipped. Dumps live member signatures |
| `tests/AS2.ModApi.Tests` | Cold checks for the path and key logic. Needs no game installed |
| `tests/AS2.Bootstrap.Tests` | Cold checks for the `doorstop_config.ini` rewrite. Needs no game installed |

The checks are the one part of this repo that builds without Audiosurf 2:

```
dotnet run --project tests/AS2.ModApi.Tests       # 232 checks
dotnet run --project tests/AS2.Bootstrap.Tests    # 68 checks
```

### Cutting a release

[`tools/pack.ps1`](tools/pack.ps1) fetches the pinned BepInEx release, verifies it against a pinned
SHA-256, builds both projects against it, drops the three files the community patch owns, and writes
`build\dist\AS2ModFramework-<version>.zip` plus a matching `.sha256`.

```powershell
.\tools\pack.ps1                 # pack only
.\tools\pack.ps1 -Publish        # pack, then create the GitHub release and upload both files
```

Run it from an open terminal. Double-clicking a `.ps1` is blocked by the default execution policy and
the console closes before the error is readable — [`tools/pack.cmd`](tools/pack.cmd) wraps both.
`-Publish` needs the [GitHub CLI](https://cli.github.com/); without it the script prints the manual
upload steps.

**Releases are built locally, never on CI.** `AS2.ModApi` compile-references the game's own
`Assembly-CSharp`, `LuaInterface` and `UnityEngine`, which are not redistributable and cannot be
checked in or placed on a runner. Whoever cuts a release must own the game, which is why the
published archive carries a checksum — it is the only thing users can verify against.

BepInEx is redistributed **unmodified** from its official release. No fork to maintain, and the
shipped `BepInEx\core\` DLLs can be hashed against the upstream zip to confirm they are stock.

## Compatibility

Verified against the live game, not assumed.

| | |
| --- | --- |
| Unity | 2017.4.40f1, legacy Mono (2.0 profile), x64 |
| CLR | 2.0.50727, `Supports SRE: True` |
| BepInEx | 5.4.23.5 (Unity Mono x64), HarmonyX **2.9.0** |
| Doorstop | 3.4.1, shipped by the community patch, `*:Main` |

`Supports SRE: True` is the load-bearing one. Harmony builds patched methods at runtime with
`System.Reflection.Emit`, which IL2CPP and .NET Standard profiles cannot do.

HarmonyX 2.9.0 predates `__args`, so patches here use indexed injection — `__0`, `__1`.

## Contributing

The [wiki][wiki] carries the working guides:

- [Adding a Hook][wiki-hook] — probe the signature, write the patch, expose it as an event
- [Deploying and Logs][wiki-deploy] — where each file goes, and how to read the log
- [Game internals][wiki-lua] — the hookable surface of `Assembly-CSharp`

There are no unit tests beyond the cold checks. The game is the harness, and verifying a change means
running it.

## Credits

- **[BepInEx](https://github.com/BepInEx/BepInEx)** — the plugin framework this builds on, and the
  Harmony and HarmonyX patching it bundles
- **[UnityDoorstop](https://github.com/NeighTools/UnityDoorstop)** — the injection mechanism both this
  and the community patch rely on
- **The Audiosurf 2 Community Patch team** — for keeping the game alive, and for the decompilation
  work that made its internals legible

This project is [MIT licensed](LICENSE.txt). It is distributed alongside BepInEx, which is LGPL-2.1
and is redistributed unmodified. The release archive carries a `THIRD-PARTY-NOTICES.txt` naming every
bundled component and its upstream, generated by [`tools/pack.ps1`](tools/pack.ps1).

[rel]: https://github.com/wmessick00/AS2ModFramework/releases/latest
[patch]: https://www.moddb.com/mods/audiosurf-2-community-patch
[wiki]: https://github.com/wmessick00/AS2ModFramework/wiki
[wiki-install]: https://github.com/wmessick00/AS2ModFramework/wiki/Installing
[wiki-first]: https://github.com/wmessick00/AS2ModFramework/wiki/Your-First-Plugin
[wiki-deploy]: https://github.com/wmessick00/AS2ModFramework/wiki/Deploying-and-Logs
[wiki-events]: https://github.com/wmessick00/AS2ModFramework/wiki/AS2Events
[wiki-game]: https://github.com/wmessick00/AS2ModFramework/wiki/AS2GameEvents
[wiki-ui]: https://github.com/wmessick00/AS2ModFramework/wiki/AS2Ui-Controls
[wiki-lock]: https://github.com/wmessick00/AS2ModFramework/wiki/Input-Lock
[wiki-keys]: https://github.com/wmessick00/AS2ModFramework/wiki/Targets-and-Keys
[wiki-paths]: https://github.com/wmessick00/AS2ModFramework/wiki/AS2Paths
[wiki-chain]: https://github.com/wmessick00/AS2ModFramework/wiki/The-Loading-Chain
[wiki-hook]: https://github.com/wmessick00/AS2ModFramework/wiki/Adding-a-Hook
[wiki-lua]: https://github.com/wmessick00/AS2ModFramework/wiki/Lua-States

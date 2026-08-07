# AS2ModFramework

A BepInEx + Harmony modding foundation for Audiosurf 2 — so mods can hook the game properly, and so
more than one of them can be installed at a time.

Audiosurf 2's community patch owns the game's single UnityDoorstop slot. Until now a code mod had to
take that slot for itself, which meant exactly one mod at a time and a patch auto-updater that no
longer ran. This bridges the two instead: the patch keeps updating itself, BepInEx starts normally,
and any number of plugins load side by side.

> **Status:** working and in use. Not on Thunderstore or Nexus yet — install from the
> [latest GitHub release](https://github.com/wmessick00/AS2ModFramework/releases/latest), which
> bundles everything needed. Building from source is for contributors, not for installing.

## Requirements

- Audiosurf 2 on Steam
- The **[Audiosurf 2 Community Patch](https://www.moddb.com/mods/audiosurf-2-community-patch)**.
  Required in practice: it ships the `winhttp.dll` UnityDoorstop that this framework attaches to, so
  without it there is no doorstop to bridge.

BepInEx (5.4.23.5, `win_x64`) is bundled in the release archive, so it is not a separate download.
Do not install BepInEx over the top yourself — its installer replaces the community patch's doorstop
and breaks both projects at once. BepInEx 6 is a different plugin API and will not work.

## Installing

No .NET SDK, no build step, no PowerShell. Three steps.

**1. Download and verify.** Grab `AS2ModFramework-<version>.zip` and its `.sha256` from the
[latest release](https://github.com/wmessick00/AS2ModFramework/releases/latest), then check the
archive is the one that was published:

```powershell
Get-FileHash .\AS2ModFramework-<version>.zip -Algorithm SHA256
```

Compare that against the hash in the `.sha256` file and the release notes. This is worth doing: the
archive drops a DLL into a folder the game loads code from, so it should be the file the maintainer
actually built.

**2. Extract into the game folder,** so `AS2ModLoader\` and `BepInEx\` sit next to `Audiosurf2.exe`.
In Steam: right-click Audiosurf 2 → Manage → Browse local files. BepInEx is included — there is
nothing else to download.

**3. Add a Steam launch option** — right-click Audiosurf 2 → Properties → General → Launch Options:

```
--doorstop-target "C:\Program Files (x86)\Steam\steamapps\common\Audiosurf 2\AS2ModLoader\AS2.Bootstrap.dll"
```

Swap in your own folder from step 2. It must be the full absolute path and it must stay quoted — a
wrong path here reports nothing, the game just starts unmodded.

Doorstop 3.4.1 accepts that flag, and command-line values win over the ini file. Installed this way
the framework **adds two folders and modifies no game file** — nothing for a patch update to revert,
and nothing of the community patch's that has been edited.

To confirm it worked, launch from Steam and check that `AS2ModLoader\bootstrap.log` ends with
`Handed off to BepInEx` and `BepInEx\LogOutput.log` ends with `Chainloader startup complete`. If
`preloader_*.log` appeared in the game folder, something threw — see
[docs/verification.md](docs/verification.md).

As a fallback, set `targetAssembly=AS2ModLoader\AS2.Bootstrap.dll` in `doorstop_config.ini`. The
bootstrap then re-asserts that key on every launch, so a patch update costs one run rather than a
manual repair — but this install does modify a file the patch owns, which is why it is the fallback
and not the recommendation.

### Uninstalling

Drop the launch option and delete `AS2ModLoader\` and `BepInEx\`. If you used the ini install, set it
back to the community patch's own target:

```
targetAssembly=Audiosurf2_Data\Updater\PatchUpdaterPreloader.dll
```

Set that value explicitly rather than restoring a `.backup` file left behind by an earlier install —
those are named for when they were taken, not for what they hold.

## Writing a mod

Declare a dependency on the API and subscribe to what you need:

```csharp
[BepInPlugin("your.mod.id", "Your Mod", "1.0.0")]
[BepInDependency(ModApiPlugin.Id)]
public sealed class YourMod : BaseUnityPlugin
{
    private void Awake()
    {
        AS2Events.SelectorOpened   += kind => Logger.LogInfo(kind + " selector opened");
        AS2Events.SelectionChanged += (kind, key) => Logger.LogInfo("now on " + key);

        // Fires for every Lua state, before the game registers its API and before the script runs.
        // kind is "Skin" or "Mod".
        AS2Events.LuaStateCreated += (lua, kind) =>
            lua.RegisterFunction("MyGlobal", this, GetType().GetMethod("MyGlobal"));
    }
}
```

`AS2Events.LuaStateCreated` is worth singling out. It is a postfix on `LuaSandbox.NewLua(string)`,
the single factory every Lua state in the game comes from, so it replaces both of the game's
`LuaSkinFunctionsRegistered` / `LuaModFunctionsRegistered` messages *and* the reflection into the
private `LuaMods.lua` field that reading the mode state used to require.

It also comes with an obligation, which the one-liner above hides. **Skin and mode scripts are Steam
Workshop downloads — a player subscribes, and someone else's Lua runs.** The game knows this and
sandboxes every state it creates: `NewLua` nils out `io`, `package`, `require`, `os.execute`,
`os.remove`, and both `luanet` and `load_assembly` (LuaInterface's bridge to the CLR), then narrows
reachable types to a whitelist of about forty. Because this event is a postfix, the state you get
has already been through all of that.

`RegisterFunction` does not consult that whitelist. Anything you register is reachable by every
Workshop skin the player has ever subscribed to, so register nothing with file, network, process or
reflection reach, and treat every argument as hostile — it comes from Lua, and the signature
guarantees you nothing about it. See
[docs/game-internals.md](docs/game-internals.md#the-state-is-sandboxed-and-what-you-register-is-not).

Also available:

| API | For |
| --- | --- |
| `AS2Events.ActiveSelector` / `ActiveKey` | Current selector state without tracking it yourself |
| `TargetResolver` | Turning the game's relative paths into stable storage keys, including Workshop items and mode-dedicated skin folders |
| `AS2Input.Lock()` | Holding the game's input lock without stealing it from another mod |
| `AS2Ui` / `AS2ModMenu` | Drawing UI that matches the game's settings dialog, and registering an entry in the shared Mod Menu |
| `AS2Paths` | Where to keep content data, as opposed to BepInEx plugin config |

[docs/game-internals.md](docs/game-internals.md) documents the hookable surface these are built on.

## What this framework does not touch

Two questions get asked about any Audiosurf 2 mod loader, and both have concrete answers rather than
assurances.

**It cannot change what the game does.** Every Harmony patch here is a *postfix*, applied through a
single helper that passes `null` for the prefix. There are no prefixes, so no game method can be
skipped or short-circuited; no transpilers, so no method body is rewritten; and no patch writes back
to `__result`. The framework observes and re-broadcasts, and that is all it is structurally capable
of. Four types are patched — `RingDesignManager`, `ModeSelect`, `Settings` and `LuaSandbox` — and
each patch is listed in [`src/AS2.ModApi/Patches.cs`](src/AS2.ModApi/Patches.cs).

**There is no scoring surface.** Nothing here references `ScoreManager`, `Leaderboard`,
`LiveScoreboard`, Steam, or achievements, and no event exposes them. Note this is a statement about
*this framework*, not about what modes may do: the game gives Lua `SetLocalScore` / `SetGlobalScore`
and has a `ScoreManager.modInChargeOfScoring` flag, because custom modes doing their own scoring is
a designed feature. The framework simply adds nothing to it.

The corresponding promise to the community patch is in the install section above: on the launch
option install this modifies **no game file at all**, and `winhttp.dll` is never touched by any
install. Uninstalling is deleting two folders.

## How it works

The community patch ships UnityDoorstop **3.4.1** as `winhttp.dll` and points `doorstop_config.ini`
at `Audiosurf2_Data\Updater\PatchUpdaterPreloader.dll` to run its updater. There is exactly one
target slot, and two problems follow:

1. **The conventions don't match.** Doorstop 3.4.1 resolves the descriptor `*:Main`. BepInEx 5.4.23
   ships Doorstop 4.5.0, and its preloader exposes only `Doorstop.Entrypoint.Start()` — no `Main`.
   Pointing the patch's doorstop straight at `BepInEx.Preloader.dll` therefore does nothing.
2. **Running BepInEx's installer is worse than a shim.** It overwrites `winhttp.dll` and
   `doorstop_config.ini`, both of which are inside the community patch's update payload. The next
   patch update reverts them, BepInEx stops loading, and no code is left running in-process to
   notice or repair it.

So `AS2.Bootstrap` bridges the two conventions and never touches `winhttp.dll`. It:

1. chain-loads `PatchUpdaterPreloader.dll` so patch auto-update keeps working — first, so that a
   broken BepInEx cannot stop the patch from updating itself out of that state;
2. repoints `DOORSTOP_INVOKE_DLL_PATH` at `BepInEx\core\BepInEx.Preloader.dll`, since BepInEx derives
   its whole directory layout from that variable and doorstop had set it to the bootstrap;
3. reflectively calls `Doorstop.Entrypoint.Start()`.

The same constraint is why the loader will ship BepInEx itself rather than depending on the published
`BepInExPack`: that pack ships the two files the community patch owns, so installing it stops the
patch updater, and the next patch update then stops BepInEx.

Full detail, including the environment variables BepInEx reads and why the other three are left
alone, is in [docs/loading-chain.md](docs/loading-chain.md).

## Building from source

For contributors. Installing needs none of this — Audiosurf 2 must be installed to build at all,
since every project compiles against the game's own assemblies.

```
dotnet build src/AS2.ModApi/AS2.ModApi.csproj -c Release
```

Everything targets `net35` against the game's own assemblies in `Audiosurf2_Data\Managed`, so only
the .NET 3.5 BCL is available: no `string.IsNullOrWhiteSpace`, no `Task`, no `ValueTuple`. If your
Steam library is elsewhere, pass `-p:AudiosurfDir="D:\...\Audiosurf 2"`.

### Cutting a release

[`tools/pack.ps1`](tools/pack.ps1) fetches the pinned BepInEx release, verifies it against a pinned
SHA-256, builds both projects against it, drops the three files the community patch owns, and writes
`build\dist\AS2ModFramework-<version>.zip` plus a matching `.sha256`:

```powershell
.\tools\pack.ps1                 # pack only
.\tools\pack.ps1 -Publish        # pack, then create the GitHub release and upload both files
```

Run it from an open terminal. Double-clicking a `.ps1` in Explorer is blocked by the default
execution policy and the console closes before the error is readable — [`tools/pack.cmd`](tools/pack.cmd)
is a wrapper that handles both if you want to double-click. `-Publish` needs the
[GitHub CLI](https://cli.github.com/); without it the script prints the manual upload steps.

**Releases are built locally, never on CI.** `AS2.ModApi` compile-references the game's own
`Assembly-CSharp`, `LuaInterface` and `UnityEngine` assemblies, which are not redistributable and so
cannot be checked in or placed on a runner. Whoever cuts a release must own the game — which is why
the published archive carries a checksum: it is the only thing users can verify against.

BepInEx is redistributed **unmodified** from its official release. There is no fork to maintain, and
the shipped `BepInEx\core\` DLLs can be hashed against the upstream zip to confirm they are stock.

| Project | What it is |
| --- | --- |
| `src/AS2.Bootstrap` | Doorstop entry point. Starts BepInEx and keeps the community patch's auto-updater working. |
| `src/AS2.ModApi` | BepInEx plugin. Harmony patches that turn the game's internals into events every mod can use. |
| `src/AS2.Probe` | Development probe, not shipped. Dumps live member signatures and verifies patching works. |
| `tests/AS2.ModApi.Tests` | Cold checks for the path and key logic. Compiles the real sources, needs no game installed. |

The tests are the one part of this repo that builds without Audiosurf 2:

```
dotnet run --project tests/AS2.ModApi.Tests
```

## Compatibility

Verified against the live game, not assumed — [docs/environment.md](docs/environment.md) records how
each fact was checked so it can be re-checked after a game or patch update.

| | |
| --- | --- |
| Unity | 2017.4.40f1, legacy Mono (2.0 profile), x64 |
| CLR | 2.0.50727, `Supports SRE: True` |
| BepInEx | 5.4.23.5 (Unity Mono x64), HarmonyX **2.9.0** |
| Doorstop | 3.4.1, shipped by the community patch, `*:Main` |

`Supports SRE: True` is the load-bearing one: Harmony builds patched methods at runtime with
`System.Reflection.Emit`, which IL2CPP and .NET Standard profiles cannot do. HarmonyX 2.9.0 predates
`__args`, so patches here use indexed injection (`__0`, `__1`) instead.

## Contributing

[AGENTS.md](AGENTS.md) is the orientation and house style — start there.

| Doc | Covers |
| --- | --- |
| [docs/loading-chain.md](docs/loading-chain.md) | How a mod gets loaded, the doorstop bridge, install and uninstall |
| [docs/game-internals.md](docs/game-internals.md) | The hookable surface of `Assembly-CSharp` |
| [docs/environment.md](docs/environment.md) | Verified runtime and version facts, and how to re-verify them |
| [docs/gotchas.md](docs/gotchas.md) | The traps, most of which fail silently |
| [docs/verification.md](docs/verification.md) | How to prove a change works |
| [docs/reference/probe-dump.txt](docs/reference/probe-dump.txt) | Ground truth: real signatures off the running game |

`.claude/skills/` has task guides for adding a hook, the build/verify loop, and diagnosing a mod that
will not load.

There are no unit tests — the game is the harness, and verifying a change means running it. Please
read [docs/verification.md](docs/verification.md) before proposing one.

## Credits

- **[BepInEx](https://github.com/BepInEx/BepInEx)** — the plugin framework this builds on, and the
  Harmony/HarmonyX patching it bundles.
- **[UnityDoorstop](https://github.com/NeighTools/UnityDoorstop)** — the injection mechanism both
  this and the community patch rely on.
- **The Audiosurf 2 Community Patch team** — for keeping the game alive, and for the decompilation
  work that made its internals legible.

This project is [MIT licensed](LICENSE.txt). It is distributed alongside BepInEx, which is LGPL-2.1
and is redistributed unmodified — the release archive carries a `THIRD-PARTY-NOTICES.txt` naming
every bundled component and its upstream, generated by [`tools/pack.ps1`](tools/pack.ps1).

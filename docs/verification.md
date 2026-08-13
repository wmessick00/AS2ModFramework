# Verifying a change

For anything that binds to Unity or BepInEx, the game is the test harness and a human drives it —
prefer to have someone start the game through Steam rather than launching the executable from a tool,
which can trigger a license popup. So each verification round costs one manual launch: batch
everything you want to learn into a single run.

The exceptions are the path and key logic and the doorstop config rewrite. Neither needs any of that,
and both have [committed tests](#cold-checks-that-need-no-launch). Run those first; they are free.

## The loop

Paths below assume the repo root as the working directory and the default Steam library; set `GAME`
to wherever Audiosurf 2 actually is.

```bash
# 1. Build (ModApi first if both changed - SkinSettings references it)
dotnet build src/AS2.ModApi/AS2.ModApi.csproj -c Release

# 2. Deploy
GAME="/c/Program Files (x86)/Steam/steamapps/common/Audiosurf 2"
cp build/plugins/AS2.ModApi.dll "$GAME/BepInEx/plugins/"
cp build/AS2ModLoader/AS2.Bootstrap.dll "$GAME/AS2ModLoader/"   # only if the bootstrap changed

# 3. Launch, exercise the feature, quit
# 4. Read the logs below
```

The AS2-SkinSettings project copies itself into `BepInEx\plugins\` on build, so it needs no deploy step.

## Ask for the right actions

Whoever is driving cannot guess what your change touches. Say explicitly what to do, e.g.:

> Open the skin selector, click through two or three skins, open Skin Settings on Rainbowdrive,
> change a value, then play a song and quit.

Map actions to what they exercise:

| To exercise | Ask for |
| --- | --- |
| `SelectorOpened` / `SelectorClosed` | enter and leave the skin and mode screens |
| `SelectionChanged` | click through several entries |
| `LuaStateCreated`, injection | **play a song** — nothing else creates a Lua state |
| Persistence | change a value, quit, relaunch |
| Patch chain | just launch (check `PatchUpdater.exe` ran) |

## Where to look

| Log | Covers |
| --- | --- |
| `<game>\AS2ModLoader\bootstrap.log` | Pre-BepInEx: game root, patch chain, handoff. Truncated per launch |
| `<game>\BepInEx\LogOutput.log` | Everything after handoff: plugin loads, your `Logger` calls, Harmony warnings |
| `<game>\preloader_*.log` | **Only exists if BepInEx's preloader threw.** Its presence is itself the finding |
| `%LOCALAPPDATA%Low\Audiosurf, LLC\Audiosurf 2\output_log.txt` | Unity player log — includes Lua `print` output from skins |

```bash
GAME="/c/Program Files (x86)/Steam/steamapps/common/Audiosurf 2"
cat "$GAME/AS2ModLoader/bootstrap.log"
cat "$GAME/BepInEx/LogOutput.log"
ls "$GAME"/preloader_*.log 2>/dev/null && cat "$GAME"/preloader_*.log || echo "none (good)"
```

## What a healthy run looks like

`bootstrap.log` — three lines, in this order:

```
INFO  AS2.Bootstrap starting. Game root: C:\...\Audiosurf 2
INFO  Chained into the community patch preloader.
INFO  Handed off to BepInEx. See BepInEx\LogOutput.log from here on.
```

`LogOutput.log` — the header proves the environment, then the chainloader:

```
BepInEx 5.4.23.5 - Audiosurf2
Running under Unity v2017.4.40.7214086
CLR runtime version: 2.0.50727.1433
Supports SRE: True
System platform: Bits64, Windows
...
Chainloader started
2 plugins to load
Loading [Audiosurf 2 Mod API 0.1.0]
Loading [Audiosurf 2 Skin Settings 1.0.0]
Chainloader startup complete
```

A full reference copy is at [reference/example-LogOutput.log](reference/example-LogOutput.log).

### Known-benign noise

Do not chase these:

| Line | Why it is fine |
| --- | --- |
| `AccessTools.Property: Could not find property ... Application ... isBatchMode` | BepInEx probing for a Unity 2018+ API on a 2017 game |
| `error failed to parse songid from song` | Pre-existing game behaviour |
| `path error for: ...\tooltip_EN.txt` | Pre-existing; skin missing an optional file |

## End-to-end proof for settings injection

The strongest signal is the skin reporting its own values back, because it proves the whole chain —
schema parsed, value stored, `Setting()` injected, script read it:

```bash
grep -a "USER SETTINGS" "$LOCALAPPDATA/../LocalLow/Audiosurf, LLC/Audiosurf 2/output_log.txt" | tail -3
```

```
Lua Skin:[ USER SETTINGS:  Palette = 0, SkyBox = 2, Ship = 1, ... ]
```

Cross-check `SkyBox = 2` against `<game>\BepInEx\data\skin-settings.json`. If they disagree, the injection
ran but read the wrong key — suspect key resolution, not the Lua side.

## Cold checks that need no launch

Some things can be checked cold; prefer this when you can, to save a launch.

**Two things here have real tests**, and both run on a machine with no Audiosurf 2 on it:

```bash
dotnet run --project tests/AS2.ModApi.Tests       # path and key logic
dotnet run --project tests/AS2.Bootstrap.Tests    # doorstop_config.ini rewrite
```

Exit code 0 means every check passed; failures are listed and the exit code is 1. Both projects
compile the **real source files** rather than referencing the built DLL, so the tests cannot drift
from what ships — see the comment at the top of each `.csproj` for why. Both run on every push and
pull request; see [`.github/workflows/cold-checks.yml`](../.github/workflows/cold-checks.yml).

`TargetResolver` and `PathGuard` are what turn the game's relative paths into storage keys and, just
as importantly, what refuse a key that would resolve outside the install. Run those checks after
touching anything in `TargetResolver`, `PathGuard`, `Normalize` or `FolderForKey`. The traversal,
rooted-path and junction rejections in particular are the sort of guard a refactor deletes without
meaning to, and before these tests existed nothing would have caught that short of a manual launch
and a careful read of the log. See `Shims.cs` for the two external statics that project fakes; adding
a `using BepInEx` or `UnityEngine` to a file on its `Compile` list will break the test build, which
is the constraint working rather than a problem to route around.

`Bootstrap.Retarget`, `ReadLines`, `WriteLines` and `EnsureDoorstopTarget` rewrite one key of a file
the community patch owns, and must leave every other byte of it alone. Those checks drive the real
methods against real files in the temp folder: the key in both doorstop spellings, comments and
sections and blank lines that have to survive, the byte order mark, the truncation that stops a
fragment of the old target being left behind, and a file held open by another process. Run them after
any change to that code. A mistake there does not throw — it silently disables modding, or leaves the
community patch unable to start.

Some checks report `SKIP` rather than `PASS` or `FAIL`. The junction cases need the machine to create
a real NTFS junction, and a run that could not is neither a pass nor a failure; the same goes for a
test process launched with a doorstop flag of its own. Skips are repeated in the summary so they
cannot pass for coverage. CI is Windows, where `mklink /J` needs no elevation, so a skip there means
something is wrong with the runner.

**Assembly shape** (entry points, references, CLR version) via reflection-only load:

```powershell
$a = [System.Reflection.Assembly]::ReflectionOnlyLoadFrom("C:\...\AS2.Bootstrap.dll")
$a.ImageRuntimeVersion                                    # expect v2.0.50727
$a.GetReferencedAssemblies() | ForEach-Object { $_.Name }
$a.GetTypes() | ForEach-Object { $_.FullName }
```

This is how `Bootstrap.Main()` was confirmed to be a parameterless static that Doorstop's `*:Main`
descriptor will actually resolve — a mistake that would otherwise fail silently at launch.

**Other pure filesystem or string logic** can be prototyped against the real game folder before
committing to a launch. If the thing you are prototyping lives in `AS2.ModApi` or `AS2.Bootstrap` and
does not touch Unity or BepInEx, prefer adding a case to the matching test project over a throwaway
script — that is exactly how the key-canonicalisation algorithm ended up validated once, ad hoc, and
then unprotected.

## Diagnosing a mod that will not load

See [the wiki](../the wiki).

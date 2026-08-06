---
name: as2-troubleshoot-loading
description: Diagnose Audiosurf 2 mods that are not loading - no settings button, plugin missing from the chainloader, BepInEx not starting, or the loader breaking after a community patch update. Walks the doorstop to BepInEx chain in order and isolates which link failed.
---

# When mods do not load

The failures here are almost all **silent** — the game boots normally and the mod simply is not
there. Work the chain in order; each step's log tells you whether to continue or stop.

```
winhttp.dll (Doorstop 3.4.1)  ->  AS2.Bootstrap  ->  BepInEx preloader  ->  chainloader  ->  plugin
        [1]                          [2]                  [3]                  [4]           [5]
```

## Step 1 — Did doorstop run at all?

```bash
GAME="/c/Program Files (x86)/Steam/steamapps/common/Audiosurf 2"   # wherever yours is
cat "$GAME/AS2ModLoader/bootstrap.log"     # missing/stale => doorstop never invoked us
cat "$GAME/doorstop_config.ini"
```

`bootstrap.log` is truncated every launch, so an old timestamp means the same thing as no file.

Check in order:

- `targetAssembly=AS2ModLoader\AS2.Bootstrap.dll`? **A community patch update reverts this** — it is
  the single most likely cause, and the reason the Steam launch option is the preferred install:
  `--doorstop-target "<game>\AS2ModLoader\AS2.Bootstrap.dll"`.
- `enabled=true`?
- Is `AS2ModLoader\AS2.Bootstrap.dll` actually present?
- Did something replace `winhttp.dll`? Expect md5 `c53442c808a27f6178024a8c2e51bde1`. If BepInEx's
  installer was run, it overwrote the patch's Doorstop 3.4.1 with 4.5.0 — which will not find
  `*:Main`, and whose ini format differs. Restore the patch's file.

If the bootstrap DLL was rebuilt, confirm it still exposes a parameterless static `Main`, because
that is the descriptor Doorstop 3.4.1 resolves:

```powershell
$a = [System.Reflection.Assembly]::ReflectionOnlyLoadFrom("C:\...\AS2ModLoader\AS2.Bootstrap.dll")
$a.GetTypes() | ForEach-Object { $_.FullName }
```

## Step 2 — Did the bootstrap hand off?

A healthy `bootstrap.log` is exactly three lines ending in:

```
INFO  Handed off to BepInEx. See BepInEx\LogOutput.log from here on.
```

- `BepInEx is not installed (no BepInEx\core\BepInEx.Preloader.dll)` → install `BepInEx\core\` from
  the `win_x64` zip, **without** its `winhttp.dll` or `doorstop_config.ini`.
- `has no Doorstop.Entrypoint type` → wrong BepInEx major version. This targets BepInEx **5**;
  BepInEx 6 is a different plugin API.
- Missing the patch-chain line → harmless for mods, but the community patch will not auto-update.

## Step 3 — Did BepInEx's preloader throw?

```bash
ls "$GAME"/preloader_*.log 2>/dev/null && cat "$GAME"/preloader_*.log
```

**The existence of this file is the finding.** BepInEx writes it when `Doorstop.Entrypoint.Start()`
throws, and it contains the stack trace.

The classic cause is `DOORSTOP_INVOKE_DLL_PATH` still pointing at the bootstrap instead of
`BepInEx\core\BepInEx.Preloader.dll`. BepInEx derives its whole layout from that variable, so it
would look for plugins under `AS2ModLoader\`. `StartBepInEx` in `src/AS2.Bootstrap/Bootstrap.cs`
sets it; see [docs/loading-chain.md](../../../docs/loading-chain.md).

## Step 4 — Did the chainloader find the plugin?

```bash
cat "$GAME/BepInEx/LogOutput.log"
ls "$GAME/BepInEx/plugins/"
```

Look for `N plugins to load` and a `Loading [...]` line per plugin.

- Count too low → the DLL is not in `BepInEx\plugins\`, or it has no `[BepInPlugin]` attribute.
- `Skipping ... because it has a missing dependency` → a `[BepInDependency]` GUID does not match.
  `AS2.ModApi` declares `as2.modapi`; the string must match exactly.
- Plugin loads but its `Awake` throws → the exception is in this log; the plugin is loaded but inert.

Deleting `BepInEx\cache\` forces a rescan if the chainloader seems to be using stale metadata.

## Step 5 — Loaded but doing nothing

The plugin is running; a Harmony patch is not landing.

- Look for warnings like `X.Y not found in this build; that event is unavailable`. Patches here are
  applied individually by name precisely so this degrades one event at a time and says which one.
  A community patch update that renamed a member is the usual cause — re-run `AS2.Probe` to get the
  new signature (see the `as2-add-hook` skill).
- `HarmonyX` errors mentioning `__args` → HarmonyX 2.9.0 predates it; use `__0`.
- Nothing at all from a `LuaStateCreated` subscriber → **a song has to be played**; nothing else
  creates a Lua state.
- Settings apply to the wrong skin → key resolution, not Lua. Compare the key in the log against
  `<game>\BepInEx\data\skin-settings.json`, and remember the game is inconsistent about path casing.

## Nuclear option

Drop the Steam launch option, delete `AS2ModLoader\` and `BepInEx\`, and set `doorstop_config.ini`
back to the community patch's own target:

```
targetAssembly=Audiosurf2_Data\Updater\PatchUpdaterPreloader.dll
```

That returns the install to stock plus community patch; nothing else was ever modified. Set the value
explicitly rather than restoring a `.backup` file left over from an earlier install — those are named
for when they were taken, not for what they contain, and one of them may hold a *previous* mod
loader's target rather than the patch's.

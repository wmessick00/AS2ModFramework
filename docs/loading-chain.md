# The loading chain

How a mod gets from "DLL on disk" to "running in the game", and why it is shaped this way.

## The chain

```
Audiosurf2.exe
  └─ winhttp.dll                    UnityDoorstop 3.4.1, shipped by the community patch
       │                            resolves the method descriptor "*:Main"
       └─ AS2ModLoader\AS2.Bootstrap.dll        <- doorstop's single target slot
            ├─ 1. chain-load PatchUpdaterPreloader.dll   (patch auto-update survives)
            ├─ 2. re-assert doorstop_config.ini          (ini install only; skipped entirely
            │                                             when --doorstop-target was used)
            ├─ 3. repoint DOORSTOP_INVOKE_DLL_PATH       (BepInEx's layout depends on it)
            └─ 4. BepInEx\core\BepInEx.Preloader.dll :: Doorstop.Entrypoint.Start()
                   └─ BepInEx chainloader
                        ├─ BepInEx\plugins\AS2.ModApi.dll         (patches the game)
                        └─ BepInEx\plugins\AS2.SkinSettings.dll   (and any other mod)
```

## Why the bootstrap exists

Two independent reasons, either of which alone would be sufficient.

**1. The entry-point conventions do not meet.**

| | Descriptor it calls / exposes |
| --- | --- |
| Doorstop 3.4.1 (installed, from the patch) | `*:Main` — a static `Main` in any class |
| Doorstop 4.5.0 (in the BepInEx zip, not installed) | `Doorstop.Entrypoint:Start` |
| `BepInEx.Preloader.dll` 5.4.23.5 | **only** `Doorstop.Entrypoint.Start()`; there is no `Main` |

Pointing the patch's doorstop straight at `BepInEx.Preloader.dll` therefore does nothing at all —
silently. Verified by reading `BepInEx.Preloader/Entrypoint.cs` on the `v5-lts` branch and by
finding the literal `*:Main` in the installed `winhttp.dll`.

**2. Installing BepInEx normally is worse than a shim.**

BepInEx's installer writes `winhttp.dll` and `doorstop_config.ini` into the game root. Both are in
the community patch's update payload, so the next patch update reverts them. BepInEx then stops
loading entirely — and unlike the old arrangement, no code of ours is left running in-process to
notice or repair it. The failure is silent and permanent until someone reinstalls by hand.

The bootstrap leaves `winhttp.dll` completely alone. On the preferred launch-option install it does
not touch `doorstop_config.ini` either, so no file the community patch owns is modified at all; on
the fallback install it contests exactly one key.

## The environment variables

BepInEx's preloader reads exactly four, all set by Doorstop 3.4.1 already:

| Variable | Used for | Do we override it? |
| --- | --- | --- |
| `DOORSTOP_INVOKE_DLL_PATH` | **BepInEx's entire directory layout** | **Yes — this is the critical fix-up** |
| `DOORSTOP_PROCESS_PATH` | game exe path | No (describes the game) |
| `DOORSTOP_MANAGED_FOLDER_DIR` | `Audiosurf2_Data\Managed` | No |
| `DOORSTOP_DLL_SEARCH_DIRS` | extra probe paths | No |

The first one matters because of these two lines in BepInEx's entry point:

```csharp
preloaderPath = Path.GetDirectoryName(Path.GetFullPath(EnvVars.DOORSTOP_INVOKE_DLL_PATH));
string bepinPath = Utility.ParentDirectory(Path.GetFullPath(EnvVars.DOORSTOP_INVOKE_DLL_PATH), 2);
```

Doorstop sets that variable to whatever it invoked — our bootstrap, in `AS2ModLoader\`. Left alone,
BepInEx would conclude its root was `AS2ModLoader` and find no plugins, no config and no core
assemblies. Setting it to `<game>\BepInEx\core\BepInEx.Preloader.dll` yields
`preloaderPath = BepInEx\core` and `bepinPath = BepInEx`, exactly matching a stock install.

### Why there is no assembly-resolution problem

`Start()` touches only `EnvVars` and `Utility`, both of which are defined *inside*
`BepInEx.Preloader.dll`. It installs its own `AssemblyResolve` handler and only then reflectively
invokes `PreloaderPreMain`, which is the first thing to need `BepInEx.dll`. BepInEx's own source
comment says this ordering is deliberate. So `Assembly.LoadFrom` on the preloader is sufficient; the
bootstrap does not need to pre-resolve anything.

## Ordering inside the bootstrap

The community patch's preloader is chained **first**, before BepInEx. If BepInEx is broken, the
patch must still be able to update itself out of that state. Every step is independently
try/caught for the same reason.

The chain target is a **constant** (`Audiosurf2_Data\Updater\PatchUpdaterPreloader.dll`), not
"whatever we displaced from the ini". Reading the displaced value back would happily resurrect a
competing mod loader — and the settings mod that held this slot before BepInEx re-asserted itself
into `doorstop_config.ini` three times per session, so it would have fought us for the slot on every
launch.

## Installing

### Preferred: Steam launch option

```
--doorstop-target "C:\Program Files (x86)\Steam\steamapps\common\Audiosurf 2\AS2ModLoader\AS2.Bootstrap.dll"
```

Doorstop 3.4.1 accepts this flag (the string `--doorstop-target` is in `winhttp.dll`) and
command-line values win over the ini. This is the only install the community patch cannot revert,
because launch options are not a file it owns.

### Fallback: the ini

```ini
[UnityDoorstop]
targetAssembly=AS2ModLoader\AS2.Bootstrap.dll
```

The bootstrap re-asserts this key every launch, rewriting only that line. A patch update therefore
costs one broken launch rather than a manual repair. It handles both the Doorstop 3 spelling
(`targetAssembly`) and the Doorstop 4 spelling (`target_assembly`).

This repair runs **only** when no `--doorstop-target` was passed on the command line. Doorstop
prefers the command line over the ini, so on the launch-option install the ini's value cannot affect
anything — rewriting it would edit a file the community patch owns to no effect whatsoever.

### What to copy

From the BepInEx `win_x64` zip, install **only** `BepInEx\core\`. Deliberately skip:

- `winhttp.dll` — would replace the patch's doorstop
- `doorstop_config.ini` — Doorstop 4 format (`[General]` / `target_assembly`), incompatible with the
  installed Doorstop 3.4.1 which wants `[UnityDoorstop]` / `targetAssembly`
- `.doorstop_version`

Then `AS2ModLoader\AS2.Bootstrap.dll` and `BepInEx\plugins\*.dll`.

### Uninstalling

Drop the launch option, then delete `AS2ModLoader\` and `BepInEx\`. If the ini install was used, set
the key back to the community patch's own target:

```ini
targetAssembly=Audiosurf2_Data\Updater\PatchUpdaterPreloader.dll
```

Set that value explicitly rather than restoring a `.backup` file left behind by an earlier install.
Those files are named for when they were taken, not for what they contain, and one may hold a
*previous* mod loader's target — restoring it would put that loader back in the doorstop slot.

Nothing else was ever modified.

## If a future patch ships Doorstop 4

The bootstrap already exposes `Doorstop.Entrypoint.Start()` alongside `Main()`, so it keeps working.
At that point BepInEx could in principle be pointed at directly — but the shim is still worth
keeping, because it is what preserves the patch's auto-updater.

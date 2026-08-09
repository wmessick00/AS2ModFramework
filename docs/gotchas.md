# Gotchas

Traps that cost real time in this codebase. Most of them fail *silently*, which is why they are
written down. Read before writing code here.

## Runtime

### Never touch UnityEngine before Unity is up

`AS2.Bootstrap` runs inside doorstop's entry point, before Unity has registered its internal calls.
A `Debug.Log` there throws `MissingMethodException` on the managed-to-native wrapper for
`DebugLogHandler.Internal_Log` — **and Mono caches the failed binding**, so every later `Debug.Log`
in the process throws too. The first casualty is the game's own `PrePreLoader.Start`, whose opening
statement is a `Debug.Log`: its coroutine dies and the game never finishes booting.

The symptom is a game that hangs at startup with no error attributable to your mod.

`AS2.Bootstrap.csproj` therefore references only `mscorlib` and `System` — there is no `UnityEngine`
reference to reach for by accident. Plugins are unaffected: BepInEx constructs them long after Unity
is ready, which is why the old `Log.UnityLoggingReady` gate could be deleted outright.

### HarmonyX 2.9.0 has no `__args`

BepInEx 5 is pinned to HarmonyX 2.9.0. Use indexed injection instead:

```csharp
// Works
private static void NewLuaPostfix(string __0, Lua __result) { }

// Throws at patch time on 2.9.0
private static void NewLuaPostfix(object[] __args) { }
```

Also prefer declaring `__result` as its **exact** type. `object` can trip Harmony's assignability
check depending on version.

### Prefixes do not compose the same way as upstream Harmony

HarmonyX differs from pardeike's Harmony here: in upstream, a prefix returning `false` can cancel
other prefixes; in HarmonyX it does not. Do not rely on cancellation semantics to coordinate between
mods.

## Patching style

### Resolve targets by name, patch one at a time

The community patch replaces `Assembly-CSharp.dll` wholesale on every update, so any member can be
renamed under you. `[HarmonyPatch]` attributes plus `PatchAll()` fail as a batch — one missing member
throws and takes every other patch in the assembly with it.

Everything here goes through `AccessTools.TypeByName` / `AccessTools.Method` and patches
individually inside a try/catch, so a renamed member costs exactly one event and logs a warning
naming it. See the header comment in `src/AS2.ModApi/Patches.cs`.

Harmony resolves postfix methods by name, so keep them `static` and use `nameof()` at the call site;
the compiler will then catch a rename.

## Build

### net35 BCL only

No `string.IsNullOrWhiteSpace` (use `Str.IsBlank`), no `Task`, no `ValueTuple`, no `LINQ` niceties
that arrived later.

And the one that is genuinely surprising — **these live in `System.Core.dll`, not `mscorlib`**:

- `Action` (zero-arg) and `Action<T1,T2,...>` — but *not* `Action<T>`, which is in mscorlib
- `Func<...>`
- `HashSet<T>`

Symptom: `error CS0305: Using the generic type 'Action<T>' requires 1 type arguments` on a line that
plainly uses two, or `CS0246: HashSet<> could not be found`. Add the `System.Core` reference;
`Directory.Build.props` already does.

### MSB3644 without a .NET 3.5 targeting pack

There is no net35 targeting pack installed and we do not want one — every reference resolves from
`Audiosurf2_Data\Managed`. `Directory.Build.props` satisfies the reference-assembly lookup by
pointing two internal MSBuild properties at a real directory:

```xml
<_TargetFrameworkDirectories>$(MSBuildThisFileDirectory)</_TargetFrameworkDirectories>
<_FullFrameworkReferenceAssemblyPaths>$(MSBuildThisFileDirectory)</_FullFrameworkReferenceAssemblyPaths>
```

Do not "clean this up".

### XML comments cannot contain `--`

`error MSB4025: An XML comment cannot contain '--'`. Easy to hit when writing a prose comment in a
`.csproj` with an em-dash typed as two hyphens. Rephrase or use a real em dash.

## Game behaviour

### IMGUI: apply state changes on Layout, never on click

IMGUI builds its layout on `EventType.Layout` and replays that structure for the input and Repaint
events of the same frame. Opening or closing a panel the instant a button reports a click changes
how many layout groups exist, so Repaint replays a structure that no longer matches and Unity throws
`GUILayout: Mismatched LayoutGroup`.

Queue the change and apply it on the next Layout event — or sidestep the whole class of bug by not
using `GUILayout` at all. `OverlayUI` in AS2-SkinSettings takes the second route: it draws from
explicit rects, which it needs anyway to line its columns up with the game's, and which removes the
need to queue anything.

### The input lock counter belongs to a UIManager instance

See [game-internals.md](game-internals.md#input). Release the same manager you locked, or you
silently steal the lock from every other mod. Use `AS2Input.Lock()`.

### Storage key casing is not stable

The game reports the same skin as `skins/rainbowdrive` or `skins/Rainbowdrive` depending on how you
got there. It only *looks* harmless because Windows paths are case-insensitive and
`SettingsStore` happens to use an `OrdinalIgnoreCase` dictionary. Keys reach mods' saved JSON, so
build them through `TargetResolver.Normalize`, which resolves real on-disk casing.

## Paths

### A junction under `skins/` or `mods/` is refused, not followed

Containment in `TargetResolver` is settled by comparing the resolved path against the game root **as
a string**. That test cannot see a reparse point: `<game>\skins\Foo` reads as inside the install
whether the folder is really there or is a junction to `D:\anything`, and `Directory.GetDirectories`
and `File.Exists` follow it out either way. Creating a junction on Windows needs no elevation, so
this is not an exotic case.

Every path component *below* the game root therefore also goes through `PathGuard.IsLink`
(`FILE_ATTRIBUTE_REPARSE_POINT`), and a linked one is refused with a warning naming it. That is a
reject rather than a resolve-and-re-check, because resolving a reparse point to its target needs
.NET 6 or P/Invoke and this assembly is net35 with neither.

The consequences are the trade-off, not oversights:

- **Junctioning `<game>\skins` onto another drive stops working here.** People really do this for
  disk space. Those skins keep loading — the game does not care — but this framework will not list
  them and will not store settings for them. The log says which folder and why.
- **The game root itself is never judged**, only components below it. BepInEx resolves the root from
  the running process, and a Steam library reached through a junction is an ordinary setup.
- **A linked *file* inside a real folder is allowed.** A symlinked `modsettings.lua` yields no key
  claiming to be install content; it only means the bytes came from elsewhere, chosen by somebody who
  could already write into the game folder.
- **Attributes that cannot be read count as linked.** A guard that cannot answer has to refuse, or a
  failed attribute call quietly switches the check off.

The cold checks cover it: they build a real junction with `mklink /J`, assert it resolves, and only
then assert every entry point refuses it. Issue #13.

## Tooling

### Prefer to have a person start the game

Launching the executable from a tool, rather than through Steam, can trigger a license popup. A
copied game folder is not a workaround either — Audiosurf 2 needs `steam_api.dll`/CSteamworks, so a
copy outside the Steam library cannot be launched meaningfully anyway. Test in place; on the launch
option install everything this repo adds is additive and no game file is modified.

### String-scanning assemblies misses literals

Type and method names are UTF-8 in the `#Strings` heap; **string literals are UTF-16** in `#US`. An
ASCII regex finds the former and never the latter, so "the literal isn't in the binary" proves
nothing unless you scanned both encodings. This produced a false negative during development.

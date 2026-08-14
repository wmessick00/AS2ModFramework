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

### Mods register Mod Menu entries from whatever thread they like

`AS2ModMenu.Register` is public API, and a plugin may call it from the callback of a web request or a
file read. `OnGUI` walks the entries every frame, and more than once per frame, so a `List` that
another thread adds to mid-draw throws `InvalidOperationException: Collection was modified`.
`ModApiPlugin.OnGUI` catches that and closes the menu — so one mod's registration timing would shut
the shared hub on every other mod for that frame.

`Register` and `Unregister` therefore change the list while holding a lock and publish an immutable
array; the drawing code reads that array and takes no lock at all. Read `Published` **once** at the
top of any drawing code you add. IMGUI replays one structure for the input and Repaint events of the
same frame, so a header count from one snapshot and rows from another is the `Mismatched LayoutGroup`
bug above wearing a different hat. Issue #16.

### Read an event field once, into a local, before you raise it

The same mod that registers a Mod Menu entry off a worker thread unsubscribes from an `AS2Events`
event off one. A multicast event field goes `null` on the last `-=`, so a `Raise*` method that reads
the field twice — once for the null check, once for `GetInvocationList` — lets that `-=` land between
the two reads and throws `NullReferenceException`. `Safe` does not cover it: `Safe` wraps the
subscriber call, and this throws while building the list of subscribers to call. What is left is an
uncaught exception in a Harmony postfix on the game thread.

Copy the field to a local first, null-check the local, and enumerate the local. A raise then uses the
handler list as it stood when it started, which is why the class documents that a handler removed
mid-raise can still get one more call. Issue #26.

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

### A file name that Windows reserves for a device is refused

Anything that turns a caller-supplied name into a path goes through `PathGuard.IsPlainFileName`,
which rejects separators, drive and stream qualifiers, the dot names, and — the part nobody expects —
`CON`, `PRN`, `AUX`, `NUL`, `CONIN$`, `CONOUT$`, `COM0`–`COM9` and `LPT0`–`LPT9`. The device
comparison uses the part of the name in front of the first dot,
with the spaces around it removed, so `nul.json`, `NUL`, `nul.` and `nul .txt` are all refused;
`console.json`, `com.json` and `com10.json` are ordinary names and pass.

Win32 opens the device instead of a file for these, and the failure is silent in both directions:
`File.WriteAllText` on `BepInEx\data\nul.json` reports success, writes nothing and leaves no file, so
the mod reads back nothing next launch with no exception anywhere to explain it. The name is not
always the mod author's own — `AS2Paths.DataFile` warns against naming a per-skin file after its
storage key, and a storage key is built from a Steam Workshop folder name.

**How much of the rule applies depends on the Windows build.** That is why the guard refuses by name
rather than attempting the write and looking at the result:

| Behaviour of | `<dir>\nul` | `<dir>\nul.json` |
| --- | --- | --- |
| What Microsoft documents, and Windows 10 | device | device |
| Windows 11 build 26200 | device | ordinary file |

The 26200 row was checked through `cmd.exe` and `File.WriteAllText` together, so it is Windows and
not one runtime. .NET Framework refuses a bare `nul` itself with `NotSupportedException`; Mono 2017,
which is what the game runs, promises nothing of the kind. The same file name therefore keeps one
player's settings and loses another's. Issue #15.

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

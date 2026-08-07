# Game internals

The hookable surface of `Assembly-CSharp.dll`. Every signature here came off the running game via
`AS2.Probe`; the raw output is in [reference/probe-dump.txt](reference/probe-dump.txt).

Treat that file as the authority and this one as the commentary. Re-run the probe after a community
patch update rather than trusting either.

## Free decompiled source

`Audiosurf2_Data\Managed\` ships two decompiled source files and a class list, presumably by
accident. They are extremely useful and cost nothing to read:

- `luacontroller.cs` — the whole `LuaController`, including the `LuaSandbox.NewLua("Skin")` call and
  the full list of skin API functions registered into Lua
- `scenemanager.cs` — the skin-facing scene API
- `classlist.txt` — every type name in the assembly, handy for finding what exists before probing

## Lua states

### `LuaSandbox.NewLua(string name)` — the hook that matters most

```
static public Lua NewLua(string name)
static public void LoadSafeTypes(Lua lua)
```

**Every Lua state in the game is created here**, verified at runtime to be called with exactly two
values: `"Skin"` and `"Mod"`. A single postfix gives you every state, before the game registers its
own API into it and before the script runs — the right moment to add globals.

This one hook replaces all three of the older approaches:

| Old approach | Problem |
| --- | --- |
| `Messenger.AddListener("LuaSkinFunctionsRegistered", ...)` | Skin states only |
| `Messenger.AddListener("LuaModFunctionsRegistered", ...)` | Mode states only |
| `typeof(LuaMods).GetField("lua", NonPublic \| Instance)` | Silently breaks if the patch renames the field |

Exposed as `AS2Events.LuaStateCreated(Lua lua, string kind)`.

Expect it to fire several times per song (roughly three `Skin`/`Mod` pairs observed for one play).
`LuaController.OnDisable` disposes the state and `Awake` makes a new one, so per-song recreation is
normal — make injection idempotent and cheap.

#### The state is sandboxed, and what you register is not

`NewLua` is not just a factory. Its whole body, read off the shipped `Assembly-CSharp` with Cecil:

```csharp
lua = new Lua(true);
if (fullSecuritySandbox) { lua.SecureLuaFunctions(); LuaSandbox.Sandboxify(lua); }
LuaSandbox.LoadSafeTypes(lua);
return lua;
```

`Sandboxify` runs this against the new state, so any of these still being present is a hard failure:

```lua
assert(nil == package)    assert(nil == io)         assert(nil == require)
assert(nil == module)     assert(nil == os.execute) assert(nil == os.exit)
assert(nil == os.getenv)  assert(nil == os.remove)  assert(nil == os.rename)
assert(nil == luanet)     assert(nil == load_assembly)
```

`luanet` and `load_assembly` are LuaInterface's bridge to the CLR; `LoadSafeTypes` then narrows
reachable types to a whitelist of about forty. This is deliberate and it is not decoration — **skin
and mode scripts are Steam Workshop downloads**. A player subscribes to a skin and someone else's
Lua runs on their machine.

Because the postfix runs *after* `NewLua` returns, the state `LuaStateCreated` gives you is already
sandboxed. What it does not do is sandbox **you**: `RegisterFunction` never consults the whitelist,
so anything a mod registers is reachable by every Workshop skin the player has. Register nothing
with file, network, process or reflection reach, and treat every argument as hostile — it arrives
from Lua, so the signature guarantees nothing about it.

`fullSecuritySandbox` defaults to true in the static constructor and is cleared by exactly one thing:
the `+disablemodsecuritysandbox` launch argument. A player who passes it gets an unsandboxed state,
and so does anything subscribed to this event.

### The states themselves

```
LuaController.lua           static public Lua      <- skin state, public, no reflection needed
LuaController.Awake()                              <- calls LuaSandbox.NewLua("Skin")
LuaController.Start()                              <- registers the skin API, then DoCode
LuaController.DoCode(string code, string path)     <- runs the script

LuaMods.instance            static public LuaMods
LuaMods.lua                 private Lua            <- mode state; do NOT reflect for this any more
LuaMods.BeforeBuildHighway()                       <- private; broadcast point for the old message
```

The game's own `Messenger` bus is still a legitimate extension point where it broadcasts what you
need — an intended hook beats a patch. It just does not broadcast much.

## Selector screens

`RingDesignManager` (skins) and `ModeSelect` (modes) are near-identical in shape; they look
copy-pasted. Both have `hasSelectedADesign`, `ItemSelectedDelegate`, `OnListItemSelected`,
`SetStartingHover`, and the same comment/delete/upload delegates. One generic helper wires both —
see `WireSelector` in `src/AS2.ModApi/Patches.cs`.

```
RingDesignManager
  private void OnEnable()                                  <- screen appeared
  private void OnDisable()                                 <- screen went away
  public void OnListItemSelected(int index, GameObject tile)  <- highlight changed
  static public string selectedSkinRelativePath            <- e.g. "/skins/Rainbowdrive"
  static public RingDesign selectedSkin
  static public string selectedSkinFriendlyName
  static public bool selectedSkinIsDedicated
  static public void SetSkinPath(string skinPath, bool allowFindSkinsCall)

ModeSelect
  private void OnEnable()
  private void OnDisable()
  public void OnListItemSelected(int index, GameObject tile)
  static public Mode selectedModeScript                    <- .relativePath
  static public Mode GetCurrentMode()
  static public string GetSelectedModFullFilePath()
  static public void SetModeScript(Mode newMode, bool forceSkinChangeIfNeeded)
  static public void SetModeScript(string modeRelativePath, bool forceSkinChangeIfNeeded)
```

The selectors are prefabs instantiated by `SkinSelectorLoader` / `ModeSelectorLoader` (each a
`Start()` plus `InitIfNeeded()`), so enable state is what "the screen is up" actually means. That is
why `OnEnable`/`OnDisable` are the lifecycle hooks and why polling `FindObjectOfType` — the previous
approach, every 15 frames — was never necessary.

`SetSkinPath` / `SetModeScript` are the authoritative setters if you ever need to catch programmatic
selection changes that bypass the UI.

## Paths and keys

```
CodeEditor._skinPath    static private string   (public accessor: skinPath)
CodeEditor._modPath     static private string   (public accessor: modPath)
```

Absolute, and only populated once a song has been set up — so they are a *fallback* behind
`selectedSkinRelativePath` / `selectedModeScript.relativePath`, never the primary source.

**Casing is not consistent.** The same skin can report `skins/rainbowdrive` while browsing and
`skins/Rainbowdrive` once a song starts; the game also stores lowercase paths elsewhere
(`skins\mystical\tooltip_EN.txt`). Since keys end up in mods' saved JSON, `TargetResolver.Normalize`
resolves each segment against the real on-disk folder name and caches the result. Always build keys
through it rather than using the game's strings directly.

Only *successful* resolutions are cached. A key that did not match a folder is walked again on the
next call, so a Workshop item that is still downloading when something first asks about it is picked
up once it lands, rather than keeping the asker's spelling for the whole session.

The three folder shapes that must all key correctly:

```
skins/<name>                 plain local skin
skins/<steamid>/<name>       Workshop item (container folder is all digits)
mods/<mode>/skins/<name>     skin dedicated to one mode
```

plus `mods/<name>` and `mods/<steamid>/<name>` for modes.

## The settings dialog

```
Settings : Menu
  static bool dialogOpen          <- authoritative "is the dialog up"
  private void OnEnable()         <- AS2Events.SettingsDialogToggled(true)
  private void OnDisable()        <- ...(false)
  void buildOriginalSettings()    <- page 1
  void buildAS2InfoSettings()     <- page 2, added by the community patch
  void triggerChangePage()
  void Clicked_OK() / onBack()
  GameObject dialogRoot
```

This is **EZGUI, not IMGUI**. Rows are built programmatically and handed to
`Menu.setContents(Item[] items, int num, float xOffset)`, with prefabs behind them
(`Menu.backButtonPrefab`, `Menu.defaultPrefab`). Appending a native row therefore means supplying a
prefab, which for content that changes per skin is not practical — hence `AS2Ui`, which reproduces
the look in IMGUI instead. `Settings.dialogOpen` is what makes that safe: a mod can draw only while
the dialog is genuinely up.

`AS2Ui` holds the measurements taken off the real dialog at 2560x1440, expressed in a 1440p design
space and scaled by `AS2Ui.Unit`, with positions as offsets from the centre of the screen or of the
dialog because EZGUI centres it:

| Thing | Design units |
| --- | --- |
| Dialog | 1716 x 1216, screen centred |
| Label column right edge | 814 from the dialog's left edge |
| Control column | x 838, width 350 |
| Value column | x 1206 |
| Row pitch | 83 |
| Free space for a mod's button | 320 x 60, centred 666 left of the dialog's centre, 60 above its bottom edge |

**The dialog scales with screen width, not height.** At 1280x768 it measures 858x608 — 1716x1216 at
exactly half scale — and its rows sit 41.5px apart; a height-derived scale would give 915x648 and a
44px pitch, which is what put the mod button outside the dialog before. `AS2Ui.Unit` is therefore
`min(width / 2560, height / 1440)`: identical at 16:9, correct on anything narrower, and on wider
displays it keeps the dialog on screen rather than letting it exceed the window height. To re-check
this, screenshot the settings dialog at a non-16:9 resolution and measure the panel.

A font size is in pixels, so `AS2Ui.EnsureStyles` rebuilds every style whenever the resolution
changes. This is not an edge case: the resolution slider lives in this very dialog, so a player can
change it while a mod is drawing over it.

Its slider is white up to the handle and blue past it, which is why the game's sliders show no blue
at maximum. An enumerated setting (Graphics Level, Anti-Aliasing, Resolution) is drawn as a slider
with the value as text rather than as a list — worth copying, because it keeps every setting one row
tall.

## Input

```
UIManager.LockInput()
UIManager.UnlockInput()
UIManager.Exists()
UIManager.instance
private int inputLockCount        <- it is a counter, and it lives on the instance
```

The selector lists are EZGUI and read `UnityEngine.Input` directly, so consuming IMGUI events does
not block them; the game's own lock is the only mechanism that works.

`inputLockCount` being an instance field is the trap. Several mods holding the lock compose fine —
*provided each releases the same manager it locked*. If the scene changes while a panel is open, the
old manager and its count are gone, and unlocking the new one decrements a counter you never
incremented, silently releasing every other mod's lock. `AS2Input.Lock()` returns a handle that
captures the exact manager and releases that one or nothing.

## Other assemblies worth knowing

| Assembly | Why |
| --- | --- |
| `LuaInterface.dll` | The `Lua` type. `RegisterFunction` honours `ParameterInfo.IsOptional`, so a C# method with an optional argument binds to both `F("x")` and `F("x", 1)` from Lua |
| `Newtonsoft.Json.dll` | Ships with the game; use it rather than bundling your own |
| `UnityEngine.IMGUIModule` | IMGUI needs no prefabs or asset bundles, which keeps a mod to a single DLL |

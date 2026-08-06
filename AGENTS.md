# Working in this repo

A BepInEx + Harmony modding foundation for Audiosurf 2. Read this first; it is the map.

## What this repo is

Audiosurf 2's community patch owns the single UnityDoorstop slot, so before this existed only one
mod could be installed at a time, and it had to hijack that slot to do it. This repo replaces that
arrangement with a real plugin loader.

| Project | Runs when | What it is |
| --- | --- | --- |
| `src/AS2.Bootstrap` | doorstop time, pre-Unity | Bridges the patch's Doorstop 3.4.1 to BepInEx, keeps the patch's auto-updater alive |
| `src/AS2.ModApi` | BepInEx plugin | Harmony patches that turn the game's internals into events |
| `src/AS2.Probe` | dev only, not shipped | Dumps live member signatures off the running game |

The consumer of `AS2.ModApi` lives in a **separate repo**, `AS2-SkinSettings`, checked out beside
this one at `..\AS2-SkinSettings`. Changing an `AS2Events` signature means updating that repo too.

Do not confuse it with `<game>\ModSettings\`, which holds only `settings.json` — player data the
plugin resolves from the game root at runtime, not source.

## Testing means running the game

There are no unit tests; the game is the harness. Two working preferences follow, and both are worth
respecting even though neither is enforced by anything:

- **Prefer to let a human start and quit the game** rather than launching it from a tool. Launching
  the executable directly, outside Steam, can trigger a license popup. If you are an agent working
  here, say what you want exercised and let the person you are working with drive it.
- **Batch verification.** A launch is a manual step, so a round costs real attention. Decide
  everything you want to learn *before* asking, and add whatever logging covers all of it at once.

Prefer cold checks — reflection-only assembly loads, prototyping path logic against the real folders
— for anything that does not truly need the game running. See
[docs/verification.md](docs/verification.md).

## Documentation

Written from a session that verified all of it against the running game. Prefer these over
re-deriving; where a fact was checked, the doc says how, so you can re-check rather than trust.

| Doc | Read it when |
| --- | --- |
| [docs/environment.md](docs/environment.md) | You need runtime/version facts, or a build fails oddly |
| [docs/loading-chain.md](docs/loading-chain.md) | Anything about how mods get loaded, doorstop, install |
| [docs/game-internals.md](docs/game-internals.md) | You are hooking something in the game |
| [docs/gotchas.md](docs/gotchas.md) | **Before writing any code here.** The expensive traps |
| [docs/verification.md](docs/verification.md) | You changed something and need to prove it works |
| [docs/reference/probe-dump.txt](docs/reference/probe-dump.txt) | Ground truth: real signatures off the live game |

Skills in the wiki cover the recurring jobs: adding a hook, the build/verify loop, and
diagnosing a mod that will not load.

## The four facts that explain most of the design

1. **Doorstop 3.4.1 (the patch's) resolves `*:Main`. BepInEx 5.4.23's preloader exposes only
   `Doorstop.Entrypoint.Start()`.** They do not meet, which is the entire reason `AS2.Bootstrap`
   exists.
2. **Never run BepInEx's own installer here.** It overwrites `winhttp.dll` and
   `doorstop_config.ini`, both of which are inside the community patch's update payload. The next
   patch reverts them and nothing is left running to notice.
3. **`LuaSandbox.NewLua(string)` is the single factory for every Lua state**, called with `"Skin"`
   and `"Mod"`. One postfix there replaces two of the game's `Messenger` broadcasts *and* the
   private-field reflection that reading mode state used to need.
4. **BepInEx 5 ships HarmonyX 2.9.0**, which predates `__args`. Use indexed injection (`__0`).

## Style

Match the existing code. It is defensive on purpose: this runs inside somebody's game, and a mod
that throws must never take the game down with it.

- Every hook and every subscriber call is individually try/caught, and logs something specific.
- Patch targets are resolved by name through `AccessTools`, never with `[HarmonyPatch]` attributes.
  A renamed member in a future community patch should cost one event and one warning, not the whole
  API. See the comment at the top of `src/AS2.ModApi/Patches.cs`.
- Comments explain *why*, especially where the code looks odd. Most of the odd-looking code here is
  load-bearing and the comment says what breaks without it.
- `net35` only. No `string.IsNullOrWhiteSpace`, no `Task`, no `ValueTuple`, no string interpolation
  habits that assume newer BCL. `Str.IsBlank` exists for the first one.

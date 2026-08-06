---
name: as2-add-hook
description: Add a new Harmony hook or AS2Events event to AS2.ModApi for Audiosurf 2 - discovering the real method signature off the running game with AS2.Probe, writing the patch defensively, and exposing it as an event. Use when a mod needs to react to something the game does not currently broadcast.
---

# Adding a hook to AS2.ModApi

The rule: **never guess a signature.** A Harmony patch against a method that does not exist fails
silently or throws at patch time, and the community patch rewrites `Assembly-CSharp.dll` on every
update. Get the real signature first, then write the patch so a future rename degrades gracefully.

## 1. Check what is already known

Look in [docs/reference/probe-dump.txt](../../../docs/reference/probe-dump.txt) first — it has the
full declared members of the nine types this API already hooks. [docs/game-internals.md](../../../docs/game-internals.md)
is the annotated version.

Also read `Audiosurf2_Data\Managed\luacontroller.cs` and `scenemanager.cs` — the game ships those
decompiled — and grep `classlist.txt` there to confirm a type exists at all:

```bash
GAME="/c/Program Files (x86)/Steam/steamapps/common/Audiosurf 2"   # wherever yours is
grep -i "YourType" "$GAME/Audiosurf2_Data/Managed/classlist.txt"
```

## 2. If it is not in the dump, extend the probe

Add the type name to `TypesOfInterest` in `src/AS2.Probe/ProbePlugin.cs`, then:

```bash
dotnet build src/AS2.Probe/AS2.Probe.csproj -c Release
cp build/plugins/AS2.Probe.dll "$GAME/BepInEx/plugins/"
```

Have someone launch the game (prefer that over starting it from a tool), then read
`<game>\AS2ModLoader\probe-dump.txt`. Copy the refreshed dump back into `docs/reference/` if it
changed. Remove `AS2.Probe.dll` from `BepInEx\plugins\` afterwards — it is a dev tool, not a shipped
component.

The probe resolves everything by name through `AccessTools`, so it never fails to build because a
game type is internal.

## 3. Write the patch

In `src/AS2.ModApi/Patches.cs`, following the existing shape:

```csharp
private static void WireYourThing(Harmony harmony)
{
    Type t = AccessTools.TypeByName("SomeGameType");
    if (t == null) { ModApiPlugin.Log.LogWarning("SomeGameType not found; YourEvent unavailable."); return; }

    Patch(harmony, t, "SomeMethod", null, nameof(YourPostfix));
}

private static void YourPostfix(string __0)   // indexed injection; see below
{
    AS2Events.RaiseYourThing(__0);
}
```

Call it from `ApplyAll`. Non-negotiables:

- **`AccessTools` by name, never `[HarmonyPatch]` attributes.** Attribute patching plus `PatchAll()`
  fails as a batch — one renamed member kills every other patch. The `Patch` helper already
  try/catches and logs the specific member.
- **`__0`, `__1` for arguments — not `__args`.** BepInEx 5 ships HarmonyX 2.9.0, which predates it.
- **Declare `__result` as its exact type**, not `object`.
- **Postfixes must be `static`**, and referenced via `nameof()` so a rename is a compile error.

Prefer a postfix. Use a prefix only to genuinely suppress behaviour, and remember HarmonyX prefix
cancellation does not compose the way upstream Harmony's does.

## 4. Expose it as an event

In `src/AS2.ModApi/AS2Events.cs`:

```csharp
public static event Action<string> YourThing;

internal static void RaiseYourThing(string arg)
{
    if (YourThing == null) return;
    foreach (Action<string> h in YourThing.GetInvocationList())
        Safe("YourThing", delegate { h(arg); });
}
```

Iterate `GetInvocationList()` and wrap **each** subscriber in `Safe`. Wrapping the whole multicast
in one try/catch would let one mod's exception silently stop every later subscriber.

Document in the XML comment *when* it fires relative to game state — "before the script runs",
"after the screen is enabled". That timing is the part callers cannot discover for themselves.

## 5. Verify

One launch, batched. See [docs/verification.md](../../../docs/verification.md) for what to ask the
user to do and which logs to read. Log something from the postfix the first time so you can see it
fire before you trust it.

## 6. Downstream

`AS2-SkinSettings` (separate repo, checked out beside this one at `..\AS2-SkinSettings`) consumes these
events. Adding an event is backward compatible; changing a signature is not — update that repo in
the same change, and build `AS2.ModApi` first since it references the built DLL.

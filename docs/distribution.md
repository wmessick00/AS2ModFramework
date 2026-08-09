# Distribution

Getting this onto Nexus Mods and working under Vortex. Written after checking both, because the
obstacles are not the ones that packaging guides prepare you for: the hard part is not the archive
layout, it is that the install needs a Steam launch option no mod manager can set.

Facts below were checked in August 2026. Re-check them; both platforms move.

## Where things actually stand

| | State | Consequence |
| --- | --- | --- |
| Nexus game page for Audiosurf 2 | **Does not exist** | There is nothing to upload to yet |
| Vortex extension for Audiosurf 2 | **Does not exist** (no match in `Nexus-Mods/vortex-games`) | Someone has to write one |
| Thunderstore listing | Does not exist | Same shape of problem |

Both platforms add a game only when a conforming mod is submitted alongside the request, so the
order is forced: **a mod unlocks the listing, the listing does not unlock the mod.**

And the framework is the wrong thing to lead with. It is infrastructure — a player does not want
"a mod loader", they want the thing it enables. `AS2-SkinSettings` is the submission that makes the
case; this repo is what it depends on.

> The Nexus page check was made by search, not by loading the site — nexusmods.com refuses automated
> fetches. Confirm at `nexusmods.com/audiosurf2` before acting on it.

## What Vortex can and cannot do here

Vortex deploys a mod by linking files from a staging folder into **one** directory, the extension's
`queryModPath`. Three things about this project fight that model, in increasing order of severity:

1. **Two install roots.** `AS2ModLoader\` and `BepInEx\` are siblings in the game folder. A single
   `queryModPath` cannot cover both.
2. **An off-Nexus hard dependency.** The community patch is required and lives on ModDB. Vortex
   resolves dependencies from Nexus, so it cannot fetch or check it.
3. **The Steam launch option.** This is the real blocker.

   ```
   --doorstop-target "<game>\AS2ModLoader\AS2.Bootstrap.dll"
   ```

   **Vortex cannot set Steam launch options.** No archive layout, FOMOD installer or extension
   changes that. It stays a manual step, and per the README a wrong path here reports nothing — the
   game simply starts unmodded, which is the worst failure mode to hand a mod-manager user who
   reasonably assumes the manager did everything.

   The ini fallback is not a way out. `doorstop_config.ini` belongs to the community patch, so
   deploying over it puts Vortex in a fight with the patch updater, and a managed deployment that
   gets reverted on someone else's schedule is worse than a manual step that stays put.

## The split this argues for

Two packages, which was [already the decision for Thunderstore](../memory/thunderstore-packaging.md).
**`tools/pack.ps1` now does this**, emitting `AS2ModFramework`, `AS2ModLoader` and `AS2ModApi` with a
checksum each.

| Package | Contents | Vortex |
| --- | --- | --- |
| `AS2ModLoader` | `BepInEx\core\`, `AS2ModLoader\AS2.Bootstrap.dll` | **Not deployable.** Two roots, plus the launch option. Manual install, or an extension `setup` that copies and then prompts |
| `AS2ModApi` | `BepInEx\plugins\AS2.ModApi.dll` | **Deploys cleanly.** One DLL into one folder is exactly the BepInEx shape Vortex already handles |

This is the strongest argument for splitting, and it is a different argument from the Thunderstore
one. Thunderstore wanted the split so a plain BepInEx plugin need not drag in the event layer. Nexus
and Vortex want it because **it is the difference between one archive no mod manager can install and
one manual prerequisite plus normal, managed plugins.** Bundled, every consumer inherits the
loader's unmanageability. Split, only the one-time prerequisite is awkward.

It also matches how a player thinks about updates: the API changes when events are added, the loader
changes almost never, and bundling forces a re-download of BepInEx for an event signature.

## Changes needed in this repo

Ordered by what blocks what. None of it is worth doing before the game is listed, except the first,
which is worth doing anyway.

1. ~~**`pack.ps1` emits two archives** instead of one.~~ **Done.** It emits three: the combined
   archive, which stays the recommendation for hand installs, plus `AS2ModLoader` and `AS2ModApi`.
   The sub-packages are copied out of the combined staging folder rather than built separately, so
   the archives cannot disagree about the bytes they ship.

2. **Assembly version metadata.** `Directory.Build.props` sets `GenerateAssemblyInfo=false`, so the
   DLLs carry no version resource at all. `pack.ps1` reads `ModApiPlugin.Version` out of the source
   text to name the archive. That is fine for a zip on GitHub and not fine for a mod manager, which
   wants a version it can read off the deployed file to decide whether an update applies. Setting a
   real `AssemblyVersion` from that same constant keeps one source of truth.

3. **A stable marker for `testSupportedContent`.** A Vortex installer recognises an archive by
   looking inside it. `AS2ModLoader/AS2.Bootstrap.dll` and `BepInEx/plugins/AS2.ModApi.dll` are good
   markers as long as neither moves; if either does, every installed copy stops being recognised.

4. **A FOMOD installer**, optionally, to offer "loader only" versus "loader + API". Low value while
   there are two packages anyway.

## What a Vortex extension would involve

Not in this repo — it is a small JavaScript project published separately, and it is what makes the
game appear in Vortex at all. The shape:

```
info.json     name, author, version, description
index.js      registerGame({ ... })
gameart.jpg   640x360
```

The properties that matter for this game:

| Property | Value |
| --- | --- |
| `id` | the Nexus domain name, once the game is listed |
| `queryPath` | `GameStoreHelper.findByAppId` — Audiosurf 2 is Steam appid **235800** |
| `executable` | `Audiosurf2.exe` |
| `requiredFiles` | `Audiosurf2.exe`, and `Audiosurf2_Data\Managed\Assembly-CSharp.dll` |
| `queryModPath` | `BepInEx/plugins` — the only path a mod manager can own safely here |
| `mergeMods` | true |
| `setup` | check `AS2ModLoader\AS2.Bootstrap.dll` exists, and that `winhttp.dll` does, and warn otherwise |

That `setup` is where the honesty has to live. It cannot fix the launch option, so it should detect
the situation and say so plainly: the loader is missing, or the community patch is missing, or both
are present and the user still has to paste one string into Steam. A `setup` that quietly succeeds
and leaves the game running unmodded is the failure this whole project exists to avoid.

## Redistribution

The release bundles BepInEx unmodified under LGPL v2.1, with `THIRD-PARTY-NOTICES.txt` naming the
version, source URL, SHA-256 and licence. Nexus requires the uploader to hold redistribution rights;
LGPL grants them provided the notice travels with the files, which it does. Keep that file in every
archive that contains `BepInEx\core\`, including any split package.

## References

- [Creating a Vortex game extension](https://github.com/Nexus-Mods/Vortex/wiki/MODDINGWIKI-Developers-General-Creating-a-game-extension)
- [Nexus: how to add a new game](https://help.nexusmods.com/article/104-how-can-i-add-a-new-game-to-nexus-mods)
- [`Nexus-Mods/vortex-games`](https://github.com/Nexus-Mods/vortex-games) — the official extension collection
- [docs/loading-chain.md](loading-chain.md) — why the launch option exists and why the ini is the fallback

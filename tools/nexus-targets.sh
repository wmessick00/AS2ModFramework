#!/usr/bin/env bash
# Checks .github/nexus-targets.json and builds the upload list, for
# .github/workflows/publish-to-nexus.yml.
#
#   nexus-targets.sh <config> <version> <tag> <release-assets-file> <output-json-file>
#
# <release-assets-file> holds the names of the assets on the release, one to a line. Writes the
# upload list to <output-json-file>, and exits 1 with an ::error:: annotation and writes nothing when
# the configuration cannot be trusted with a live mod page. Warnings are annotations and do not fail
# it.
#
# A script of its own so tests/Check-NexusPublish.sh can drive it with configurations and release
# asset lists that no real release would have. #80.

set -euo pipefail

CONFIG="${1:?usage: nexus-targets.sh <config> <version> <tag> <release-assets-file> <output-json-file>}"
VERSION="${2:?usage: nexus-targets.sh <config> <version> <tag> <release-assets-file> <output-json-file>}"
TAG="${3:?usage: nexus-targets.sh <config> <version> <tag> <release-assets-file> <output-json-file>}"
ON_RELEASE="${4:?usage: nexus-targets.sh <config> <version> <tag> <release-assets-file> <output-json-file>}"
OUT="${5:?usage: nexus-targets.sh <config> <version> <tag> <release-assets-file> <output-json-file>}"

MOD_ID="$(jq -r '.mod_id' "$CONFIG")"
if [ "$MOD_ID" = "0" ] || [ "$MOD_ID" = "null" ]; then
  echo "::error::$CONFIG still has mod_id 0. Fill in the real ids before publishing."
  exit 1
fi

# A slot left at 0 is the unfilled placeholder, not a slot number. Uploading against it
# would either fail obscurely inside the action or write to somebody else's mod.
#
# A slot with no file_id at all is the same thing said differently, and used to pass: null is not
# equal to 0. `// 0` makes an absent one count as the placeholder it is. #80.
UNSET="$(jq -r '[.files[] | select((.file_id // 0) == 0) | .asset] | join(", ")' "$CONFIG")"
if [ -n "$UNSET" ]; then
  echo "::error::$CONFIG has no file_id for: $UNSET. Create the file slots on Nexus first."
  exit 1
fi

# Exactly one entry owns the changelog and the page version. Two would post the same text
# twice on one mod page.
for field in changelog update_mod_version; do
  COUNT="$(jq -r --arg f "$field" '[.files[] | select(.[$f] == true)] | length' "$CONFIG")"
  if [ "$COUNT" != "1" ]; then
    echo "::error::$COUNT entries in $CONFIG set $field. Exactly one has to."
    exit 1
  fi
done

# category defaults to main when an entry omits it, matching the action's own default, so
# an entry written before that field existed keeps behaving the way it did.
JSON="$(jq -c --arg v "$VERSION" --argjson mod "$MOD_ID" '
  [ .files[] | {
      asset,
      file_id,
      mod_id: $mod,
      # filename is the release asset, verbatim. It is what gets uploaded and what a
      # browser saves, and it carries the version so a download lands in a folder beside
      # the last two and stays tellable apart.
      #
      # display_name is what the mod page calls the entry, and it carries neither the
      # version nor the extension. Nexus prints the version in its own column next to the
      # name, so repeating it reads as a stutter, and .zip is not part of a name. The
      # action defaults this to filename, so leaving it out gets both back.
      # display_name is overridable per entry, because the name on the page is a choice
      # somebody may already have made when they created the slot by hand. Falling back to
      # the asset name would quietly rename their file on the next release.
      filename: "\(.asset)-\($v).zip",
      display_name: (.display_name // .asset),

      category: (.category // "main"),
      changelog,
      update_mod_version,
      primary_mod_manager_download
  } ]' "$CONFIG")"

# Every named asset has to be on the release already. A typo in asset, or a package
# pack.ps1 stopped emitting, would otherwise reach the upload job as a missing file after
# the approval has already been given.
for f in $(printf '%s' "$JSON" | jq -r '.[].filename'); do
  if ! grep -qxF "$f" "$ON_RELEASE"; then
    echo "::error::$f is named in $CONFIG but is not among the assets on $TAG."
    exit 1
  fi
done

# And the other direction, which is a warning rather than an error.
#
# A slot has to be created by hand before it can be written to, so a release carrying a
# package with no slot yet is a real intermediate state, not a mistake. Skipping it
# silently is what would be the mistake: the zip is on GitHub, nobody on Nexus can see it,
# and nothing said so.
while read -r asset; do
  case "$asset" in
    *.zip) ;;
    *) continue ;;
  esac
  if ! printf '%s' "$JSON" | jq -e --arg a "$asset" 'any(.[]; .filename == $a)' > /dev/null; then
    echo "::warning::$asset is on the release but has no file slot in $CONFIG, so it is not going to Nexus."
  fi
done < "$ON_RELEASE"

printf '%s' "$JSON" > "$OUT"
printf '%s' "$JSON" | jq .

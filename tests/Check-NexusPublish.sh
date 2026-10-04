#!/usr/bin/env bash
# Cold checks for the logic .github/workflows/publish-to-nexus.yml runs before it publishes.
#
# That workflow posts to a live, public Nexus mod page, and posting cannot be undone by running
# anything again: the changelog endpoint is additive, and every upload adds a version to a slot.
# The logic that decides what it posts -- which part of the release body is the changelog, whether
# the configuration can be trusted, what the version is -- used to be inline in the workflow, so it
# could only be run by publishing a release. Issue #80. It is in tools/nexus-*.sh now, and this runs
# those, on release bodies and configurations of every shape.
#
# No test framework, matching tests/Check.cs and tests/Check-ReleaseTooling.ps1 and for the same
# reason: a script that prints PASS lines and returns an exit code is understood by every CI there
# is. Needs bash, jq and GNU sed and grep, which is what the workflow itself runs on: this is run on
# ubuntu-latest, not on the Windows runner the other checks need.
#
#   bash tests/Check-NexusPublish.sh

set -u

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
TOOLS="$ROOT/tools"

PASSED=0
FAILED=()

pass() { PASSED=$((PASSED + 1)); echo "  PASS  $1"; }
fail() { FAILED+=("$1"); echo "  FAIL  $1"; }

# same <what> <actual> <expected>
same() {
  if [ "$2" = "$3" ]; then pass "$1"; else fail "$1 -- expected '$3', got '$2'"; fi
}

# true <what> <command...>: passes when the command succeeds
true_() {
  local what="$1"; shift
  if "$@" > /dev/null 2>&1; then pass "$what"; else fail "$what -- expected true"; fi
}

# false_ <what> <command...>: passes when the command fails
false_() {
  local what="$1"; shift
  if "$@" > /dev/null 2>&1; then fail "$what -- expected false"; else pass "$what"; fi
}

WORK="$(mktemp -d)"
cleanup() { if [ -n "${WORK:-}" ] && [ -d "$WORK" ]; then rm -rf "$WORK"; fi; }
trap cleanup EXIT

# Runs a command, keeping what it printed in $OUT and how it ended in $CODE. The scripts under test
# report with ::error:: annotations on stdout, so a check on a failure reads those.
OUT=""
CODE=0
run() {
  OUT="$("$@" 2>&1)"
  CODE=$?
}

# ---- The version in a tag ----------------------------------------------------------------------

echo "Reading a version out of a tag"

version() { run bash "$TOOLS/nexus-version.sh" "$1"; }

version v0.2.2
same "a tag with a v is accepted" "$CODE" "0"
true_ "and names the tag as given" grep -qx 'tag=v0.2.2' <<< "$OUT"
true_ "and the version without the v, which is what Nexus's version field wants" grep -qx 'version=0.2.2' <<< "$OUT"

version 0.2.2
same "a tag with no v is accepted too" "$CODE" "0"
true_ "and the tag is not rewritten" grep -qx 'tag=0.2.2' <<< "$OUT"

version v10.20.30
same "every part can have two digits" "$CODE" "0"
true_ "and reads as written, not as text sorted" grep -qx 'version=10.20.30' <<< "$OUT"

for bad in "v0.2" "v0.2.2.1" "v0.2.2-rc1" "v0.2.2+build" "vv0.2.2" "latest" "v0.x.2" "V0.2.2" " v0.2.2"; do
  version "$bad"
  same "'$bad' is refused" "$CODE" "1"
  true_ "'$bad' is refused with an annotation that names it" grep -q "^::error::.*$bad" <<< "$OUT"
done

version ""
same "no tag at all is refused" "$CODE" "1"
true_ "and says there was no tag" grep -q '^::error::No tag' <<< "$OUT"

# ---- The changelog -------------------------------------------------------------------------------

echo ""
echo "Cutting the changelog out of a release body"

# changelog <tag> <body-text>: the body is given the way gh gives it, as text.
changelog() {
  printf '%s' "$2" > "$WORK/body.txt"
  rm -f "$WORK/changelog.txt"
  run bash -c "bash '$TOOLS/nexus-changelog.sh' '$1' '$WORK/changelog.txt' < '$WORK/body.txt'"
}

NL=$'\n'

changelog v0.3.4 "## What changed${NL}${NL}- Add the thing${NL}- Fix the other thing${NL}${NL}<!-- nexus:end -->${NL}${NL}## Install${NL}${NL}Most people want the combined package.${NL}"
same "an ordinary body is accepted" "$CODE" "0"
same "and the changelog is the bullets, with the heading and the blank line under it gone" \
     "$(cat "$WORK/changelog.txt")" "- Add the thing${NL}- Fix the other thing"
same "and nothing below the marker reaches it" "$(grep -c 'Install\|Most people' "$WORK/changelog.txt")" "0"
same "and the file ends in one newline" "$(tail -c 1 "$WORK/changelog.txt" | od -An -c | tr -d ' ')" '\n'

# The heading goes whatever its spelling. GitHub's editor, hand edits and other tools all produce
# these, and each one left in is a line of nothing on the mod page.
for heading in "## What changed" "# What changed" "### What changed" "###### What changed" "## what changed" "## WHAT CHANGED" "##What changed" "## What changed   "; do
  changelog v0.3.4 "${heading}${NL}${NL}- One entry${NL}<!-- nexus:end -->${NL}"
  same "the heading '$heading' is dropped" "$(cat "$WORK/changelog.txt")" "- One entry"
done

# A line that merely mentions it is a changelog entry, not a heading.
changelog v0.3.4 "- Fix what changed in the log${NL}<!-- nexus:end -->"
same "a bullet that mentions 'what changed' is kept" "$(cat "$WORK/changelog.txt")" "- Fix what changed in the log"

changelog v0.3.4 "## What changed is different${NL}- One${NL}<!-- nexus:end -->"
same "a heading with more words than 'What changed' is kept" \
     "$(head -1 "$WORK/changelog.txt")" "## What changed is different"

# A body written on Windows, or edited in the browser, can be CRLF. The blank line under the heading
# was then a lone CR, which counted as content, and the changelog posted to the mod page opened with
# an empty line.
changelog v0.3.4 $'## What changed\r\n\r\n- CRLF entry\r\n- another\r\n\r\n<!-- nexus:end -->\r\nfooter\r\n'
same "a body with CRLF line ends is accepted" "$CODE" "0"
same "and the changelog opens with the first entry, not with an empty line" "$(head -1 "$WORK/changelog.txt")" "- CRLF entry"
same "and holds both entries" "$(wc -l < "$WORK/changelog.txt" | tr -d ' ')" "2"
same "and no carriage return is posted" "$(grep -c $'\r' "$WORK/changelog.txt")" "0"

changelog v0.3.4 "${NL}${NL}   ${NL}- starts late${NL}<!-- nexus:end -->"
same "blank lines above the first entry are dropped, and a line of spaces is blank" "$(cat "$WORK/changelog.txt")" "- starts late"

changelog v0.3.4 "## What changed${NL}   ${NL}	${NL}- under spaces and a tab${NL}<!-- nexus:end -->"
same "so is the line of spaces and a tab a heading can leave behind" "$(cat "$WORK/changelog.txt")" "- under spaces and a tab"

# The text is the author's, and goes through unharmed.
changelog v0.3.4 '- `code` and $HOME and 100% done and \n and "quotes"'"${NL}"'<!-- nexus:end -->'
same "shell metacharacters in an entry survive" \
     "$(cat "$WORK/changelog.txt")" '- `code` and $HOME and 100% done and \n and "quotes"'

# Only the first marker counts.
changelog v0.3.4 "- real${NL}<!-- nexus:end -->${NL}- after the first${NL}<!-- nexus:end -->${NL}- after the second"
same "only what is above the first marker is the changelog" "$(cat "$WORK/changelog.txt")" "- real"

changelog v0.3.4 "- entry <!-- nexus:end --> on the same line"
same "a marker in the middle of a line still ends it" "$(cat "$WORK/changelog.txt")" "- entry "

# Refusals. Each leaves nothing behind for the workflow to post.
changelog v0.2.2 "## Install${NL}${NL}Most people want the combined package.${NL}"
same "a body with no marker is refused" "$CODE" "1"
true_ "and the annotation names the release" grep -q '^::error::No nexus:end marker in the body of v0.2.2' <<< "$OUT"
true_ "and says how to fix a release from before the marker" grep -q 'by hand' <<< "$OUT"
false_ "and nothing is written for the workflow to post" test -e "$WORK/changelog.txt"

changelog v0.3.4 ""
same "an empty body is refused" "$CODE" "1"

changelog v0.3.4 "## What changed${NL}${NL}<!-- nexus:end -->${NL}- belongs below, not above${NL}"
same "a heading with nothing under it is an empty changelog" "$CODE" "1"
true_ "and the annotation says Nexus would show nothing" grep -q '^::error::.*empty' <<< "$OUT"
false_ "and nothing is written" test -e "$WORK/changelog.txt"

changelog v0.3.4 "<!-- nexus:end -->${NL}- everything is below${NL}"
same "a marker at the very top is an empty changelog" "$CODE" "1"

changelog v0.3.4 $'   \r\n\t\r\n<!-- nexus:end -->'
same "whitespace alone is an empty changelog" "$CODE" "1"

# The belt and braces for install boilerplate that leaks above the marker.
for leaked in "Get-FileHash -Algorithm SHA256 the-zip" "Most people want the combined package." "BepInEx 5.4.23.5 is bundled in this one"; do
  changelog v0.3.4 "- a real entry${NL}${leaked}${NL}<!-- nexus:end -->"
  same "install text above the marker ('${leaked:0:24}...') is refused" "$CODE" "1"
  true_ "and the annotation says so" grep -q '^::error::.*install text or checksums' <<< "$OUT"
  false_ "and nothing is written" test -e "$WORK/changelog.txt"
done

changelog v0.3.4 "- the get-filehash cmdlet is mentioned${NL}<!-- nexus:end -->"
same "that check is case-insensitive" "$CODE" "1"

changelog v0.3.4 "- Make the bepinex bundle smaller${NL}<!-- nexus:end -->"
same "but an entry that merely says 'bundle' is fine" "$CODE" "0"

false_ "a missing tag argument is a usage error, not a silent success" bash "$TOOLS/nexus-changelog.sh"

# ---- The upload list --------------------------------------------------------------------------

echo ""
echo "Checking the targets and building the upload list"

# config <json>: writes a configuration. targets <assets...>: runs the script against a release
# that carries exactly those assets, and leaves the list in $WORK/targets.json.
config() { printf '%s' "$1" > "$WORK/config.json"; }
targets() {
  printf '%s\n' "$@" > "$WORK/assets.txt"
  rm -f "$WORK/targets.json"
  run bash "$TOOLS/nexus-targets.sh" "$WORK/config.json" 0.3.4 v0.3.4 "$WORK/assets.txt" "$WORK/targets.json"
}

GOOD='{
  "mod_id": 42803644071937,
  "files": [
    { "asset": "Combined", "file_id": 11, "category": "main", "changelog": true, "update_mod_version": true, "primary_mod_manager_download": true },
    { "asset": "Split", "file_id": 12, "category": "optional", "display_name": "Split package", "changelog": false, "update_mod_version": false }
  ]
}'

config "$GOOD"
targets Combined-0.3.4.zip Split-0.3.4.zip
same "a sound configuration is accepted" "$CODE" "0"

same "and the list has an entry for each slot" "$(jq 'length' "$WORK/targets.json")" "2"
same "and the file name is the asset, the version and .zip" "$(jq -r '.[0].filename' "$WORK/targets.json")" "Combined-0.3.4.zip"
same "and the page name carries neither the version nor the extension" "$(jq -r '.[0].display_name' "$WORK/targets.json")" "Combined"
same "and a display_name in the configuration is honoured" "$(jq -r '.[1].display_name' "$WORK/targets.json")" "Split package"
same "and so is a category" "$(jq -r '.[1].category' "$WORK/targets.json")" "optional"
same "and the mod id is the composite one, as a number" "$(jq -r '.[0].mod_id' "$WORK/targets.json")" "42803644071937"
same "and the slot id is kept" "$(jq -r '.[1].file_id' "$WORK/targets.json")" "12"
same "and exactly one entry owns the changelog" "$(jq '[.[] | select(.changelog == true)] | length' "$WORK/targets.json")" "1"
same "and the order is the configuration's" "$(jq -r '[.[].asset] | join(",")' "$WORK/targets.json")" "Combined,Split"
true_ "and what it prints is the list" bash -c "printf '%s' \"\$1\" | jq -e 'length == 2' > /dev/null" _ "$OUT"

config '{ "mod_id": 7, "files": [ { "asset": "A", "file_id": 1, "changelog": true, "update_mod_version": true } ] }'
targets A-0.3.4.zip
same "an entry with no category is main, as the action defaults it" "$(jq -r '.[0].category' "$WORK/targets.json")" "main"
same "and with no display_name it is the asset" "$(jq -r '.[0].display_name' "$WORK/targets.json")" "A"

# The placeholder ids.
config '{ "mod_id": 0, "files": [ { "asset": "A", "file_id": 1, "changelog": true, "update_mod_version": true } ] }'
targets A-0.3.4.zip
same "a mod_id of 0 is refused" "$CODE" "1"
true_ "and the annotation says the ids are not filled in" grep -q '^::error::.*mod_id 0' <<< "$OUT"
false_ "and no list is written" test -e "$WORK/targets.json"

config '{ "files": [ { "asset": "A", "file_id": 1, "changelog": true, "update_mod_version": true } ] }'
targets A-0.3.4.zip
same "a configuration with no mod_id at all is refused" "$CODE" "1"

config '{ "mod_id": 7, "files": [ { "asset": "A", "file_id": 0, "changelog": true, "update_mod_version": true }, { "asset": "B", "file_id": 0 }, { "asset": "C", "file_id": 3 } ] }'
targets A-0.3.4.zip B-0.3.4.zip C-0.3.4.zip
same "a slot left at 0 is refused" "$CODE" "1"
true_ "and every such slot is named, in one annotation" grep -q '^::error::.*no file_id for: A, B\.' <<< "$OUT"
false_ "and the one with a real id is not" grep -qE 'for: [^.]*\bC\b' <<< "$OUT"

# Found by writing these. 'null == 0' is false, so a slot with no file_id at all got through a check
# whose comment says a placeholder would not, and reached the upload action as a missing field.
config '{ "mod_id": 7, "files": [ { "asset": "A", "changelog": true, "update_mod_version": true } ] }'
targets A-0.3.4.zip
same "a slot with no file_id at all is refused like a zero" "$CODE" "1"
true_ "and is named" grep -q '^::error::.*no file_id for: A' <<< "$OUT"

# Who owns the changelog and the page version.
config '{ "mod_id": 7, "files": [ { "asset": "A", "file_id": 1, "changelog": true, "update_mod_version": true }, { "asset": "B", "file_id": 2, "changelog": true, "update_mod_version": false } ] }'
targets A-0.3.4.zip B-0.3.4.zip
same "two entries claiming the changelog is refused" "$CODE" "1"
true_ "and the annotation counts them" grep -q '^::error::2 entries in .* set changelog' <<< "$OUT"

config '{ "mod_id": 7, "files": [ { "asset": "A", "file_id": 1, "changelog": true, "update_mod_version": false } ] }'
targets A-0.3.4.zip
same "no entry owning the page version is refused" "$CODE" "1"
true_ "and the annotation says so" grep -q '^::error::0 entries in .* set update_mod_version' <<< "$OUT"

config '{ "mod_id": 7, "files": [ { "asset": "A", "file_id": 1, "changelog": false, "update_mod_version": true } ] }'
targets A-0.3.4.zip
same "no entry owning the changelog is refused" "$CODE" "1"

# The release has to carry what the configuration names.
config "$GOOD"
targets Combined-0.3.4.zip
same "an asset named in the configuration and missing from the release is refused" "$CODE" "1"
true_ "and the annotation names the file and the tag" grep -q '^::error::Split-0.3.4.zip is named in .* not among the assets on v0.3.4' <<< "$OUT"
false_ "and no list is written" test -e "$WORK/targets.json"

targets Combined-0.3.3.zip Split-0.3.3.zip
same "assets of the wrong version do not count" "$CODE" "1"

targets
same "a release with no assets at all is refused" "$CODE" "1"

# A package with no slot yet is a real intermediate state: a warning, not a failure.
targets Combined-0.3.4.zip Split-0.3.4.zip Extra-0.3.4.zip
same "a zip on the release with no slot is not an error" "$CODE" "0"
true_ "but it is said, by name" grep -q '^::warning::Extra-0.3.4.zip is on the release but has no file slot' <<< "$OUT"
same "and does not get a slot" "$(jq 'length' "$WORK/targets.json")" "2"

targets Combined-0.3.4.zip Split-0.3.4.zip checksums.txt notes.md
same "an asset that is not a zip is not mentioned" "$(grep -c '^::warning::' <<< "$OUT")" "0"

targets Combined-0.3.4.zip Split-0.3.4.zip
same "a release with nothing extra warns of nothing" "$(grep -c '^::warning::' <<< "$OUT")" "0"

# The configuration this repository ships. A change to it that would fail a real release fails here
# first: a placeholder id, a second changelog owner, a slot with no id.
SHIPPED="$ROOT/.github/nexus-targets.json"
ASSETS_FOR_SHIPPED="$(jq -r '.files[].asset | . + "-9.9.9.zip"' "$SHIPPED")"
printf '%s\n' "$ASSETS_FOR_SHIPPED" > "$WORK/shipped-assets.txt"
run bash "$TOOLS/nexus-targets.sh" "$SHIPPED" 9.9.9 v9.9.9 "$WORK/shipped-assets.txt" "$WORK/shipped.json"
same "the checked-in nexus-targets.json passes its own checks" "$CODE" "0"
if [ "$CODE" != "0" ]; then echo "$OUT"; fi

false_ "a missing argument is a usage error, not a silent success" bash "$TOOLS/nexus-targets.sh" "$SHIPPED"

# ---- Summary -----------------------------------------------------------------------------------

echo ""
if [ "${#FAILED[@]}" -eq 0 ]; then
  echo "All $PASSED checks passed."
  exit 0
fi

echo "${#FAILED[@]} FAILED ($PASSED passed):"
for f in "${FAILED[@]}"; do echo "  - $f"; done
exit 1

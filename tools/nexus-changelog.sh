#!/usr/bin/env bash
# Cuts the Nexus changelog out of a release body, for .github/workflows/publish-to-nexus.yml.
#
#   nexus-changelog.sh <tag> <output-file>        the release body on stdin
#
# Writes the changelog to <output-file>. Exits 1 with an ::error:: annotation, and writes nothing,
# when the body has no changelog worth posting. <tag> is only for the messages.
#
# The changelog is the part of the body above the nexus:end marker. pack.ps1 puts a "What changed"
# section there, and everything below it -- install steps, the checksum table -- is for somebody
# reading GitHub, not somebody on a mod page deciding whether to press update.
#
# A script of its own, and not lines in the workflow, because this is what decides what a public mod
# page says, and a regression in it only showed when a real release was published, to a page that
# cannot be taken back by re-running anything: the changelog endpoint is additive. #80. The checks
# are tests/Check-NexusPublish.sh.

set -euo pipefail

TAG="${1:?usage: nexus-changelog.sh <tag> <output-file>}"
OUT="${2:?usage: nexus-changelog.sh <tag> <output-file>}"

# Without carriage returns. A body written on the Windows machine pack.ps1 runs on, or edited in the
# browser, can be CRLF, and a CR is a character as far as sed is concerned: the blank line left under
# the heading was "\r", which counted as content, so the changelog posted to the mod page opened with
# an empty line. Nothing in a changelog wants one. #80.
BODY="$(tr -d '\r')"

# Everything above the marker, and nothing without one.
#
# This used to forward the whole body when the marker was absent, on the reasoning that
# something beats nothing. It does not. A release cut before pack.ps1 emitted the marker
# has a body that is entirely install instructions and a checksum table, and posting that
# as a changelog is worse than posting nothing -- it reads as though nobody looked.
# v0.2.2 went up that way and had to be fixed by hand on the mod page.
if ! printf '%s' "$BODY" | grep -qF '<!-- nexus:end -->'; then
  echo "::error::No nexus:end marker in the body of $TAG, so there is no changelog to post."
  echo "::error::Releases from before pack.ps1 emitted the marker need the section added to the release body by hand: a '## What changed' section, then a line containing only <!-- nexus:end -->."
  exit 1
fi

CHANGELOG="${BODY%%<!-- nexus:end -->*}"

# Drop the heading. The mod page already prints "Version 0.2.2" directly above the entry,
# so a "What changed" heading under it is one line of nothing.
#
# Then the blank lines the heading left behind. '/[^[:space:]]/,$!d' deletes from the top until
# the first line with something on it; trailing blanks go on their own, because $( ) strips them.
# A line of spaces is blank, which '/./' did not think, so one under the heading stayed.
CHANGELOG="$(printf '%s' "$CHANGELOG" \
  | sed -e '/^#\{1,6\}[[:space:]]*What changed[[:space:]]*$/Id' \
  | sed -e '/[^[:space:]]/,$!d')"

if [ -z "${CHANGELOG//[$' \t\r\n']/}" ]; then
  echo "::error::The changelog for $TAG is empty. Nexus would show nothing for this version."
  exit 1
fi

# Belt and braces. If the install boilerplate ever leaks past the marker again, it will
# carry one of these, and failing here is cheaper than editing a mod page afterwards.
if printf '%s' "$CHANGELOG" | grep -qiE 'Get-FileHash|Most people want|BepInEx .* is bundled'; then
  echo "::error::The changelog for $TAG still contains install text or checksums. Check where the nexus:end marker sits in the release body."
  exit 1
fi

printf '%s\n' "$CHANGELOG" > "$OUT"
echo "Changelog is $(printf '%s' "$CHANGELOG" | wc -c) bytes"

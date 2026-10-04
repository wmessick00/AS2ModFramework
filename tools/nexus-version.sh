#!/usr/bin/env bash
# Reads the version out of a release tag, for .github/workflows/publish-to-nexus.yml.
#
#   nexus-version.sh <tag>
#
# Prints "tag=<tag>" and "version=<version>" on stdout, one to a line, ready to append to
# $GITHUB_OUTPUT. Exits 1 with an ::error:: annotation when there is no tag, or when it does not
# carry a three-number version.
#
# A script of its own, and not lines in the workflow, so that tests/Check-NexusPublish.sh can run it
# on tags that are not worth cutting a release to try. The workflow is the only caller.

set -euo pipefail

TAG="${1:-}"
if [ -z "$TAG" ]; then
  echo "::error::No tag. This ran without a release and without the tag input."
  exit 1
fi

# The tag carries the v and the Nexus version field does not. pack.ps1 forms the tag as
# "v$version" and names the assets without it, so both spellings are needed here.
VERSION="${TAG#v}"
if ! printf '%s' "$VERSION" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$'; then
  echo "::error::$TAG does not carry a three-number version. Expected v0.2.2 and got $TAG."
  exit 1
fi

echo "tag=$TAG"
echo "version=$VERSION"

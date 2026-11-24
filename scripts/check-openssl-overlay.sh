#!/usr/bin/env bash
set -euo pipefail

title="OpenSSL overlay needs attention"
repo_root=$(cd "$(dirname "$0")/.." && pwd)
repo=${GITHUB_REPOSITORY:-$(gh repo view --json nameWithOwner --jq .nameWithOwner)}

manifest_version() { grep -m1 '"version"' | sed -E 's/.*"version": *"([^"]+)".*/\1/'; }
newer() { [[ "$1" != "$2" && "$(printf '%s\n%s\n' "$1" "$2" | sort -V | tail -n1)" == "$1" ]]; }

pinned=$(manifest_version < "$repo_root/vcpkg-overlay-ports/openssl/vcpkg.json")
latest_lts=$(gh api repos/openssl/openssl/git/matching-refs/tags/openssl-3.5. --jq '.[].ref | ltrimstr("refs/tags/openssl-")' \
  | grep -E '^3\.5\.[0-9]+$' | sort -V | tail -n1)
vcpkg_ships=$(gh api repos/microsoft/vcpkg/contents/ports/openssl/vcpkg.json -H "Accept: application/vnd.github.raw" | manifest_version)

echo "overlay pin: $pinned, newest 3.5 tag: $latest_lts, vcpkg master: $vcpkg_ships"

findings=()
if newer "$latest_lts" "$pinned"; then
  findings+=("- OpenSSL $latest_lts is out. Bump \`REF\` in \`vcpkg-overlay-ports/openssl/portfile.cmake\` and \`version\` in its \`vcpkg.json\` (currently $pinned).")
fi
if { [[ "$vcpkg_ships" == 3.5.* ]] || ! newer 4.2 "$vcpkg_ships"; } && ! newer "$pinned" "$vcpkg_ships"; then
  findings+=("- vcpkg master ships \`openssl\` $vcpkg_ships, a supported series. Move \`builtin-baseline\` past it and drop \`vcpkg-overlay-ports/openssl\`.")
fi

if (( ${#findings[@]} == 0 )); then
  echo "Overlay is current."
  exit 0
fi

body=$(printf '%s\n' "Checked $(date -u +%Y-%m-%d) by the OpenSSL overlay watch workflow." "" "${findings[@]}")
echo "$body"

existing=$(gh issue list --repo "$repo" --state open --label dependencies --search "in:title \"$title\"" \
  --json number,title --jq "map(select(.title == \"$title\")) | .[0].number // empty")
if [[ -n "$existing" ]]; then
  gh issue edit "$existing" --repo "$repo" --body "$body"
else
  gh issue create --repo "$repo" --title "$title" --label dependencies --body "$body"
fi

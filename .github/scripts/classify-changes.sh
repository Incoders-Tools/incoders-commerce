#!/usr/bin/env bash
# classify-changes.sh
#
# Classifies a pull request's changed-file set into the Web / .NET job
# groups per design.md "Path Glob Groups" (STEP 1-6). Used by the `changes`
# job in .github/workflows/release.yml, and driven directly by
# .github/scripts/tests/classify-changes.test.sh.
#
# Production usage (inside the `changes` job):
#   GH_TOKEN=... GITHUB_REPOSITORY=owner/repo PR_NUMBER=<n> \
#     [PR_CHANGED_FILES_COUNT=<github.event.pull_request.changed_files>] \
#     ./classify-changes.sh
#
# Emits, on stdout and (if set) appended to $GITHUB_OUTPUT:
#   web=true|false
#   dotnet=true|false
#
# Fail-safe rule (design "inconclusive is never a green light"): any
# condition that makes the changed-file set untrustworthy (API failure,
# empty result, or a 3000-file API cap mismatch) forces BOTH groups true
# rather than guessing.
#
# Test/override hooks (used only by the fixture test harness):
#   CLASSIFY_FILES_OVERRIDE          - if SET (even to ""), used verbatim as
#                                       the newline-separated file list
#                                       instead of calling `gh api`.
#   CLASSIFY_SIMULATE_API_FAILURE=1  - simulate the `gh api` call failing.
set -uo pipefail

emit() {
  local web="$1" dotnet="$2"
  echo "web=${web}"
  echo "dotnet=${dotnet}"
  if [ -n "${GITHUB_OUTPUT:-}" ]; then
    echo "web=${web}" >>"$GITHUB_OUTPUT"
    echo "dotnet=${dotnet}" >>"$GITHUB_OUTPUT"
  fi
}

fetch_files() {
  if [ "${CLASSIFY_SIMULATE_API_FAILURE:-0}" = "1" ]; then
    return 1
  fi
  if [ -n "${CLASSIFY_FILES_OVERRIDE+x}" ]; then
    printf '%s' "$CLASSIFY_FILES_OVERRIDE"
    return 0
  fi
  gh api "repos/${GITHUB_REPOSITORY}/pulls/${PR_NUMBER}/files" \
    --paginate --jq '.[].filename'
}

# STEP 1 — documentation-only paths (never select a group on their own).
is_doc_path() {
  case "$1" in
  *.md) return 0 ;;
  docs/*) return 0 ;;
  openspec/*) return 0 ;;
  LICENSE) return 0 ;;
  .github/ISSUE_TEMPLATE/*) return 0 ;;
  .github/PULL_REQUEST_TEMPLATE.md) return 0 ;;
  esac
  return 1
}

# STEP 2 — shared root: any match sets BOTH web=true AND dotnet=true.
is_shared_root_path() {
  case "$1" in
  Dockerfile) return 0 ;;
  .github/workflows/*) return 0 ;;
  deploy/*) return 0 ;;
  Commerce.sln) return 0 ;;
  railway.json) return 0 ;;
  .editorconfig) return 0 ;;
  global.json) return 0 ;;
  Directory.*.props) return 0 ;;
  Directory.*.targets) return 0 ;;
  nuget.config) return 0 ;;
  NuGet.config) return 0 ;;
  esac
  return 1
}

# STEP 3 — Web group.
is_web_path() {
  case "$1" in
  src/Commerce.Web/*) return 0 ;;
  esac
  return 1
}

# STEP 4 — .NET group: src/** (excluding Web) and tests/**.
is_dotnet_path() {
  case "$1" in
  src/Commerce.Web/*) return 1 ;;
  src/*) return 0 ;;
  tests/*) return 0 ;;
  esac
  return 1
}

# Reads newline-separated paths on stdin (already known to be non-empty at
# the call site). Prints web=<bool> and dotnet=<bool>.
classify() {
  local web=false dotnet=false any_non_doc=false path

  while IFS= read -r path; do
    [ -z "$path" ] && continue
    if is_doc_path "$path"; then
      continue
    fi
    any_non_doc=true

    if is_shared_root_path "$path"; then
      web=true
      dotnet=true
      continue
    fi
    if is_web_path "$path"; then
      web=true
      continue
    fi
    if is_dotnet_path "$path"; then
      dotnet=true
      continue
    fi
    # STEP 5 — drift fail-safe: an unmatched path selects BOTH groups.
    web=true
    dotnet=true
  done

  if [ "$any_non_doc" = false ]; then
    # STEP 6 — everything remaining after STEP 1 was documentation: docs-only.
    echo "web=false"
    echo "dotnet=false"
    return 0
  fi

  echo "web=${web}"
  echo "dotnet=${dotnet}"
}

main() {
  local raw_files
  if ! raw_files=$(fetch_files); then
    echo "::warning::Could not fetch PR changed-file list. Running both groups." >&2
    emit true true
    return 0
  fi

  if [ -z "$raw_files" ]; then
    echo "::warning::Changed-file set is INCONCLUSIVE (empty). Running both groups." >&2
    emit true true
    return 0
  fi

  # 3000-file API cap guard: the `pulls/{n}/files` endpoint caps at 3000
  # entries. If the returned count does not match the PR's reported
  # changed_files total, the list was truncated and is no longer trustworthy
  # for exclusion decisions — run both groups rather than under-test.
  local returned_count
  returned_count=$(printf '%s\n' "$raw_files" | grep -c .)
  if [ -n "${PR_CHANGED_FILES_COUNT:-}" ] && [ "$returned_count" -ne "$PR_CHANGED_FILES_COUNT" ]; then
    echo "::warning::Returned file count (${returned_count}) != PR changed_files (${PR_CHANGED_FILES_COUNT}); possible 3000-file API cap truncation. Running both groups." >&2
    emit true true
    return 0
  fi

  local result web dotnet
  result=$(printf '%s\n' "$raw_files" | classify)
  web=$(printf '%s\n' "$result" | sed -n 's/^web=//p')
  dotnet=$(printf '%s\n' "$result" | sed -n 's/^dotnet=//p')
  emit "$web" "$dotnet"
}

if [ "${CLASSIFY_TEST_SOURCE:-0}" != "1" ]; then
  main "$@"
fi

#!/usr/bin/env bash
# aggregate-results.sh
#
# Aggregates GitHub Actions job results (the `needs` context, passed as
# `toJSON(needs)`) into a pass/fail decision for the always-run `ci-gate`
# job (design.md "ci-gate" step). "Inconclusive is never a green light"
# (design Verified 4): the `changes` detector job MUST have resolved to a
# hard `success`, or the filter decision is unknown and the gate fails.
#
# Usage:
#   echo "$NEEDS_JSON" | ./aggregate-results.sh
#   ./aggregate-results.sh needs.json
#
# Decision matrix:
#   changes  must be exactly "success", else FAIL.
#   web-tests / web-e2e / build:
#     success  -> pass (ran and passed)
#     skipped  -> pass (legitimately not selected by the path filter)
#     anything else (failure, cancelled, null, ...) -> FAIL
set -euo pipefail

if [ "${1:-}" != "" ] && [ -f "$1" ]; then
  needs_json=$(cat "$1")
else
  needs_json=$(cat)
fi

detector=$(jq -r '.changes.result' <<<"$needs_json")
if [ "$detector" != "success" ]; then
  echo "::error::changes -> ${detector}; filter decision unknown."
  exit 1
fi

rc=0
for job in web-tests web-e2e build; do
  r=$(jq -r --arg j "$job" '.[$j].result' <<<"$needs_json")
  case "$r" in
  success) echo "PASS    ${job}" ;;
  skipped) echo "FILTERED ${job} (legitimately not selected)" ;;
  *)
    echo "::error::${job} -> ${r}"
    rc=1
    ;;
  esac
done

exit "$rc"

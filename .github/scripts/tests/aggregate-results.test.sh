#!/usr/bin/env bash
# RED/GREEN fixture tests for .github/scripts/aggregate-results.sh
# (ci-gate decision matrix, design.md "ci-gate" step).
#
# Run directly:
#   bash .github/scripts/tests/aggregate-results.test.sh
#
# This file is written BEFORE .github/scripts/aggregate-results.sh exists
# (Phase 1, RED) and MUST fail for that reason until Phase 2 (GREEN)
# creates the script.
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$SCRIPT_DIR/../aggregate-results.sh"

pass_count=0
fail_count=0

assert_gate() {
  local description="$1" needs_json="$2" expect_pass="$3"

  if [ ! -f "$SCRIPT" ]; then
    echo "FAIL: $description (script not found: $SCRIPT)"
    fail_count=$((fail_count + 1))
    return
  fi

  local actual_pass
  if echo "$needs_json" | bash "$SCRIPT" >/dev/null 2>&1; then
    actual_pass=true
  else
    actual_pass=false
  fi

  if [ "$actual_pass" = "$expect_pass" ]; then
    echo "PASS: $description"
    pass_count=$((pass_count + 1))
  else
    echo "FAIL: $description (expected pass=$expect_pass, got pass=$actual_pass)"
    fail_count=$((fail_count + 1))
  fi
}

assert_gate "changes=skipped fails the gate" \
  '{"changes":{"result":"skipped"},"web-tests":{"result":"success"},"web-e2e":{"result":"success"},"build":{"result":"success"}}' \
  false

assert_gate "changes=failure fails the gate" \
  '{"changes":{"result":"failure"},"web-tests":{"result":"success"},"web-e2e":{"result":"success"},"build":{"result":"success"}}' \
  false

assert_gate "web-tests skipped, build success -> pass" \
  '{"changes":{"result":"success"},"web-tests":{"result":"skipped"},"web-e2e":{"result":"skipped"},"build":{"result":"success"}}' \
  true

assert_gate "all three skipped (docs-only) -> pass" \
  '{"changes":{"result":"success"},"web-tests":{"result":"skipped"},"web-e2e":{"result":"skipped"},"build":{"result":"skipped"}}' \
  true

assert_gate "any failure -> fail" \
  '{"changes":{"result":"success"},"web-tests":{"result":"failure"},"web-e2e":{"result":"skipped"},"build":{"result":"success"}}' \
  false

assert_gate "any cancelled -> fail" \
  '{"changes":{"result":"success"},"web-tests":{"result":"cancelled"},"web-e2e":{"result":"skipped"},"build":{"result":"success"}}' \
  false

echo ""
echo "aggregate-results: ${pass_count} passed, ${fail_count} failed"
[ "$fail_count" -eq 0 ]

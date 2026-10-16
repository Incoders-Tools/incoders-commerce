#!/usr/bin/env bash
# RED/GREEN fixture tests for .github/scripts/classify-changes.sh
# (STEP 1-6 classification, design.md "Path Glob Groups").
#
# Run directly:
#   bash .github/scripts/tests/classify-changes.test.sh
#
# This file is written BEFORE .github/scripts/classify-changes.sh exists
# (Phase 1, RED) and MUST fail for that reason until Phase 2 (GREEN)
# creates the script.
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$SCRIPT_DIR/../classify-changes.sh"

pass_count=0
fail_count=0

assert_classification() {
  local description="$1" files="$2" expected_web="$3" expected_dotnet="$4"

  if [ ! -f "$SCRIPT" ]; then
    echo "FAIL: $description (script not found: $SCRIPT)"
    fail_count=$((fail_count + 1))
    return
  fi

  local output actual_web actual_dotnet
  output=$(env CLASSIFY_FILES_OVERRIDE="$files" GITHUB_OUTPUT="" bash "$SCRIPT" 2>/dev/null)
  actual_web=$(printf '%s\n' "$output" | sed -n 's/^web=//p' | head -n1)
  actual_dotnet=$(printf '%s\n' "$output" | sed -n 's/^dotnet=//p' | head -n1)

  if [ "$actual_web" = "$expected_web" ] && [ "$actual_dotnet" = "$expected_dotnet" ]; then
    echo "PASS: $description"
    pass_count=$((pass_count + 1))
  else
    echo "FAIL: $description (expected web=$expected_web dotnet=$expected_dotnet, got web=$actual_web dotnet=$actual_dotnet)"
    fail_count=$((fail_count + 1))
  fi
}

# Web-only
assert_classification "Web-only path yields web=true dotnet=false" \
  "src/Commerce.Web/src/App.tsx" true false

# .NET-only
assert_classification ".NET-only path yields web=false dotnet=true" \
  "src/Commerce.Domain/Order.cs" false true

# docs-only
assert_classification "Docs-only path yields web=false dotnet=false" \
  "docs/architecture/decisions/ADR-999.md" false false

# both groups touched directly
assert_classification "Web + .NET path yields both true" \
  "$(printf 'src/Commerce.Web/src/App.tsx\nsrc/Commerce.Domain/Order.cs')" true true

# Dockerfile shared-root
assert_classification "Dockerfile (shared-root) yields both true" \
  "Dockerfile" true true

# deploy/** shared-root
assert_classification "deploy/db migration (shared-root) yields both true" \
  "deploy/db/migrations/0012_x.sql" true true

# unknown root path -> drift fail-safe
assert_classification "Unknown root path yields both true (drift fail-safe)" \
  "newdir/x.txt" true true

# README.md alone -> docs-only
assert_classification "README.md alone yields docs-only" \
  "README.md" false false

# README.md + dotnet source -> dotnet only
assert_classification "README.md + .NET source yields dotnet-only" \
  "$(printf 'README.md\nsrc/Commerce.Domain/X.cs')" false true

# empty file list -> inconclusive fail-safe (both true), NOT docs-only
assert_classification "Empty file list yields both true (inconclusive fail-safe)" \
  "" true true

# API failure -> both true (fail-safe)
if [ ! -f "$SCRIPT" ]; then
  echo "FAIL: API failure yields both true (script not found: $SCRIPT)"
  fail_count=$((fail_count + 1))
else
  output=$(env CLASSIFY_SIMULATE_API_FAILURE=1 GITHUB_OUTPUT="" bash "$SCRIPT" 2>/dev/null)
  actual_web=$(printf '%s\n' "$output" | sed -n 's/^web=//p' | head -n1)
  actual_dotnet=$(printf '%s\n' "$output" | sed -n 's/^dotnet=//p' | head -n1)
  if [ "$actual_web" = true ] && [ "$actual_dotnet" = true ]; then
    echo "PASS: API failure yields both true"
    pass_count=$((pass_count + 1))
  else
    echo "FAIL: API failure yields both true (got web=$actual_web dotnet=$actual_dotnet)"
    fail_count=$((fail_count + 1))
  fi
fi

# 3000-file cap mismatch guard -> both true even though returned paths alone
# would classify as web-only
if [ ! -f "$SCRIPT" ]; then
  echo "FAIL: cap-mismatch guard forces both true (script not found: $SCRIPT)"
  fail_count=$((fail_count + 1))
else
  output=$(env CLASSIFY_FILES_OVERRIDE="src/Commerce.Web/src/App.tsx" PR_CHANGED_FILES_COUNT=3001 GITHUB_OUTPUT="" bash "$SCRIPT" 2>/dev/null)
  actual_web=$(printf '%s\n' "$output" | sed -n 's/^web=//p' | head -n1)
  actual_dotnet=$(printf '%s\n' "$output" | sed -n 's/^dotnet=//p' | head -n1)
  if [ "$actual_web" = true ] && [ "$actual_dotnet" = true ]; then
    echo "PASS: cap-mismatch guard forces both true"
    pass_count=$((pass_count + 1))
  else
    echo "FAIL: cap-mismatch guard forces both true (got web=$actual_web dotnet=$actual_dotnet)"
    fail_count=$((fail_count + 1))
  fi
fi

echo ""
echo "classify-changes: ${pass_count} passed, ${fail_count} failed"
[ "$fail_count" -eq 0 ]

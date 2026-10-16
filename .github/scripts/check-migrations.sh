#!/usr/bin/env bash
# check-migrations.sh
#
# Expand-only guard for deploy/db/migrations (ADR-016). Used by the
# `migrations` job in .github/workflows/release.yml, and driven directly by
# .github/scripts/tests/check-migrations.test.sh.
#
# Usage (inside the repository, with <base-ref> fetched):
#   ./check-migrations.sh <base-ref>
#
# Compares <base-ref>...HEAD (from the merge base, so commits that landed on
# the base after the branch point are not counted) and fails when:
#   - an existing migration file is modified, renamed, or deleted: the
#     migrator's ledger refuses an applied file whose checksum changed, so an
#     edit would stop every later deploy;
#   - an added .sql file has, on a line outside a `--` comment, `DROP TABLE`,
#     `DROP COLUMN`, or `RENAME` (case-insensitive), unless the file has the
#     exact line `-- contract-change: approved`. These break the previous
#     application version, which a code rollback runs against this schema.
# `DROP POLICY/CONSTRAINT/INDEX/FUNCTION/TRIGGER/VIEW` stay allowed: migrations
# use them to re-create objects idempotently.
set -euo pipefail

MIGRATIONS_DIR="deploy/db/migrations"
APPROVAL_MARKER="-- contract-change: approved"
CONTRACT_PATTERN='\bDROP[[:space:]]+(TABLE|COLUMN)\b|\bRENAME\b'

if [ "$#" -ne 1 ] || [ -z "$1" ]; then
  echo "usage: check-migrations.sh <base-ref>" >&2
  exit 2
fi
base_ref="$1"

# Prints the lines of a file that change the contract, comments removed.
contract_lines() {
  tr -d '\r' | sed 's/--.*$//' | grep -inE "$CONTRACT_PATTERN" || true
}

changes=$(git diff --name-status --find-renames "${base_ref}...HEAD" -- "$MIGRATIONS_DIR/")

rc=0
added=0
while IFS=$'\t' read -r status path new_path; do
  [ -z "$status" ] && continue
  case "$status" in
  A)
    case "$path" in
    *.sql) ;;
    *) continue ;;
    esac
    added=$((added + 1))
    content=$(git show "HEAD:$path")
    if tr -d '\r' <<<"$content" | grep -qxF -- "$APPROVAL_MARKER"; then
      echo "approved contract change: $path"
      continue
    fi
    offending=$(contract_lines <<<"$content")
    if [ -n "$offending" ]; then
      echo "::error::${path} drops or renames a table or column without the '${APPROVAL_MARKER}' line (ADR-016: migrations only expand):"
      printf '%s\n' "$offending" | sed 's/^/    /'
      rc=1
    fi
    ;;
  R*)
    echo "::error::${path} is an existing migration and was renamed to ${new_path}; add a new migration instead."
    rc=1
    ;;
  D)
    echo "::error::${path} is an existing migration and was deleted; add a new migration instead."
    rc=1
    ;;
  *)
    echo "::error::${path} is an existing migration and was modified (${status}); add a new migration instead."
    rc=1
    ;;
  esac
done <<<"$changes"

if [ "$rc" -ne 0 ]; then
  exit "$rc"
fi
echo "check-migrations: OK (${added} added, no existing migration changed, expand-only against ${base_ref})"

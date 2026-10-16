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
#   - an added file's extension is not exactly `.sql`: the migrator reads only
#     `*.sql`, so a `.SQL` file would never run;
#   - an added .sql file has a statement that drops a table, column, schema or
#     type, or renames anything, unless the file has the exact line
#     `-- contract-change: approved`. These break the previous application
#     version, which a code rollback runs against this schema. Contract changes
#     are `DROP TABLE|COLUMN|SCHEMA|TYPE`, `RENAME`, and any `ALTER TABLE ...
#     DROP <x>` other than `DROP CONSTRAINT|NOT NULL|DEFAULT|IDENTITY|
#     EXPRESSION` (PostgreSQL accepts `DROP <column>` without `COLUMN`).
# The check is statement-aware: `--` comments outside single-quoted literals
# are removed, then the text is split on `;` (outside literals), so a
# statement split across lines or after a literal holding `--` is still seen.
# `DROP POLICY/CONSTRAINT/INDEX/FUNCTION/TRIGGER/VIEW` stay allowed: migrations
# use them to re-create objects idempotently.
set -euo pipefail

MIGRATIONS_DIR="deploy/db/migrations"
APPROVAL_MARKER="-- contract-change: approved"
CONTRACT_PATTERN='\bDROP[[:space:]]+(TABLE|COLUMN|SCHEMA|TYPE)\b|\bRENAME\b|\bALTER[[:space:]]+TABLE\b.*\bDROP\b'
# `ALTER TABLE ... DROP` forms that keep the contract; masked before matching.
ALTER_DROP_ALLOWED='\bDROP([[:space:]]+(CONSTRAINT|NOT[[:space:]]+NULL|DEFAULT|IDENTITY|EXPRESSION)\b)'

if [ "$#" -ne 1 ] || [ -z "$1" ]; then
  echo "usage: check-migrations.sh <base-ref>" >&2
  exit 2
fi
base_ref="$1"

# Prints one SQL statement per line: `--` comments outside single-quoted
# literals removed, split on `;` outside literals, line breaks turned into
# spaces. A doubled quote ('') closes and reopens the literal, which keeps the
# tracking right.
statements() {
  tr -d '\r' | awk 'BEGIN { RS = "\001" }
    function emit(text) {
      gsub(/[\n\t]/, " ", text)
      if (text ~ /[^ ]/) print text
    }
    {
      n = length($0); quoted = 0; start = 1; stmt = ""
      for (i = 1; i <= n; i++) {
        c = substr($0, i, 1)
        if (quoted) { if (c == "\047") quoted = 0; continue }
        if (c == "\047") { quoted = 1; continue }
        if (c == "-" && substr($0, i + 1, 1) == "-") {
          stmt = stmt substr($0, start, i - start)
          j = index(substr($0, i), "\n")
          if (j == 0) { start = n + 1; break }
          i = i + j - 2; start = i + 1
          continue
        }
        if (c == ";") {
          emit(stmt substr($0, start, i - start))
          stmt = ""; start = i + 1
        }
      }
      emit(stmt substr($0, start))
    }'
}

# Prints the statements of a file that change the contract.
contract_statements() {
  local stmts numbers
  stmts=$(statements)
  numbers=$(sed -E "s/${ALTER_DROP_ALLOWED}/ALLOWED_DROP\1/gI" <<<"$stmts" | grep -inE "$CONTRACT_PATTERN" | cut -d: -f1 || true)
  [ -z "$numbers" ] && return 0
  awk 'NR == FNR { want[$1] = 1; next } FNR in want' <(printf '%s\n' "$numbers") <(printf '%s\n' "$stmts")
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
    *)
      echo "::error::${path} is in ${MIGRATIONS_DIR} but its extension is not lowercase .sql; the migrator runs only *.sql files."
      rc=1
      continue
      ;;
    esac
    added=$((added + 1))
    content=$(git show "HEAD:$path")
    if tr -d '\r' <<<"$content" | grep -qxF -- "$APPROVAL_MARKER"; then
      echo "approved contract change: $path"
      continue
    fi
    offending=$(contract_statements <<<"$content")
    if [ -n "$offending" ]; then
      echo "::error::${path} drops or renames a table, column, schema or type without the '${APPROVAL_MARKER}' line (ADR-016: migrations only expand):"
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

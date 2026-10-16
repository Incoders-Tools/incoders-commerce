#!/usr/bin/env bash
# RED/GREEN fixture tests for .github/scripts/check-migrations.sh
# (expand-only migration guard, ADR-016, spec S6c).
#
# Run directly:
#   bash .github/scripts/tests/check-migrations.test.sh
#
# Each case builds a throwaway git repository: a `base` branch with two
# existing migrations, then a commit on top that makes the case's change.
# The guard runs inside that repository against `base`.
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$SCRIPT_DIR/../check-migrations.sh"
MIGRATIONS="deploy/db/migrations"

WORK_ROOT=$(mktemp -d)
trap 'rm -rf "$WORK_ROOT"' EXIT

pass_count=0
fail_count=0

# Creates a fixture repository whose `base` branch points at the commit with
# the two existing migrations, and prints its path. Runs in a command
# substitution, so every git message goes to stderr, never into the path.
new_fixture_repo() {
  local repo
  repo=$(mktemp -d "$WORK_ROOT/case-XXXXXX")
  mkdir -p "$repo/$MIGRATIONS"
  (
    cd "$repo" || exit 1
    git init -q
    git config user.email "fixture@example.com"
    git config user.name "Fixture"
    git config core.autocrlf false
    printf 'CREATE TABLE IF NOT EXISTS widgets (id uuid PRIMARY KEY);\n' >"$MIGRATIONS/0001_widgets.sql"
    printf 'DROP POLICY IF EXISTS widgets_tenant ON widgets;\nCREATE POLICY widgets_tenant ON widgets USING (true);\n' >"$MIGRATIONS/0002_widget_policy.sql"
    printf '# app\n' >README.md
    git add -A
    git commit -q -m "base"
    git branch -q base
  ) >&2
  printf '%s' "$repo"
}

commit_all() {
  (cd "$1" && git add -A && git commit -q -m "change")
}

# assert_guard <description> <repo> <expect_pass> [<expected stdout fragment>...]
assert_guard() {
  local description="$1" repo="$2" expect_pass="$3"
  shift 3

  if [ ! -f "$SCRIPT" ]; then
    echo "FAIL: $description (script not found: $SCRIPT)"
    fail_count=$((fail_count + 1))
    return
  fi

  local output actual_pass
  if output=$(cd "$repo" && bash "$SCRIPT" base 2>&1); then
    actual_pass=true
  else
    actual_pass=false
  fi

  if [ "$actual_pass" != "$expect_pass" ]; then
    echo "FAIL: $description (expected pass=$expect_pass, got pass=$actual_pass)"
    printf '%s\n' "$output" | sed 's/^/    | /'
    fail_count=$((fail_count + 1))
    return
  fi

  local fragment
  for fragment in "$@"; do
    if ! grep -qF -- "$fragment" <<<"$output"; then
      echo "FAIL: $description (output lacks: $fragment)"
      printf '%s\n' "$output" | sed 's/^/    | /'
      fail_count=$((fail_count + 1))
      return
    fi
  done

  echo "PASS: $description"
  pass_count=$((pass_count + 1))
}

# add_migration <repo> <file name> <content>
add_migration() {
  printf '%s' "$3" >"$1/$MIGRATIONS/$2"
  commit_all "$1"
}

repo=$(new_fixture_repo)
printf '# app, edited\n' >"$repo/README.md"
commit_all "$repo"
assert_guard "no migration changes -> pass" "$repo" true \
  "check-migrations: OK"

repo=$(new_fixture_repo)
add_migration "$repo" 0003_gadgets.sql $'CREATE TABLE IF NOT EXISTS gadgets (id uuid PRIMARY KEY);\nALTER TABLE widgets ADD COLUMN IF NOT EXISTS name text;\n'
assert_guard "new clean migration -> pass" "$repo" true \
  "check-migrations: OK"

repo=$(new_fixture_repo)
printf -- '-- tweak\n' >>"$repo/$MIGRATIONS/0001_widgets.sql"
commit_all "$repo"
assert_guard "modified existing migration -> fail" "$repo" false \
  "::error::" "$MIGRATIONS/0001_widgets.sql"

repo=$(new_fixture_repo)
(cd "$repo" && git rm -q "$MIGRATIONS/0002_widget_policy.sql")
commit_all "$repo"
assert_guard "deleted existing migration -> fail" "$repo" false \
  "::error::" "$MIGRATIONS/0002_widget_policy.sql"

repo=$(new_fixture_repo)
(cd "$repo" && git mv "$MIGRATIONS/0002_widget_policy.sql" "$MIGRATIONS/0002_widget_policies.sql")
commit_all "$repo"
assert_guard "renamed existing migration -> fail" "$repo" false \
  "::error::" "$MIGRATIONS/0002_widget_policy.sql"

repo=$(new_fixture_repo)
add_migration "$repo" 0003_drop_widgets.sql $'DROP TABLE widgets;\n'
assert_guard "new migration with DROP TABLE -> fail" "$repo" false \
  "::error::" "$MIGRATIONS/0003_drop_widgets.sql"

repo=$(new_fixture_repo)
add_migration "$repo" 0003_drop_name.sql $'alter table widgets drop column if exists name;\n'
assert_guard "new migration with DROP COLUMN (lower case) -> fail" "$repo" false \
  "::error::" "$MIGRATIONS/0003_drop_name.sql"

repo=$(new_fixture_repo)
add_migration "$repo" 0003_rename_name.sql $'ALTER TABLE widgets RENAME COLUMN name TO title;\n'
assert_guard "new migration with RENAME COLUMN -> fail" "$repo" false \
  "::error::" "$MIGRATIONS/0003_rename_name.sql"

repo=$(new_fixture_repo)
add_migration "$repo" 0003_rename_table.sql $'ALTER TABLE widgets RENAME TO gizmos;\n'
assert_guard "new migration with RENAME TO -> fail" "$repo" false \
  "::error::" "$MIGRATIONS/0003_rename_table.sql"

repo=$(new_fixture_repo)
add_migration "$repo" 0003_rename_constraint.sql $'ALTER TABLE widgets RENAME CONSTRAINT widgets_pkey TO widgets_pk;\n'
assert_guard "new migration with RENAME CONSTRAINT -> fail" "$repo" false \
  "::error::" "$MIGRATIONS/0003_rename_constraint.sql"

repo=$(new_fixture_repo)
add_migration "$repo" 0003_drop_widgets.sql $'-- contract-change: approved\nDROP TABLE IF EXISTS widgets;\n'
assert_guard "DROP TABLE with the approval marker -> pass" "$repo" true \
  "check-migrations: OK"

repo=$(new_fixture_repo)
add_migration "$repo" 0003_drop_widgets.sql $'-- contract-change: approved later\nDROP TABLE widgets;\n'
assert_guard "a marker line that is not exact does not approve -> fail" "$repo" false \
  "::error::" "$MIGRATIONS/0003_drop_widgets.sql"

repo=$(new_fixture_repo)
add_migration "$repo" 0003_recreate.sql $'DROP POLICY IF EXISTS widgets_tenant ON widgets;\nALTER TABLE widgets DROP CONSTRAINT IF EXISTS widgets_name_check;\nDROP INDEX IF EXISTS widgets_name_idx;\nDROP FUNCTION IF EXISTS widgets_touch();\nDROP TRIGGER IF EXISTS widgets_touch ON widgets;\nDROP VIEW IF EXISTS widgets_view;\nDROP POLICY widgets_other ON widgets;\nALTER TABLE widgets ALTER COLUMN name DROP NOT NULL;\n'
assert_guard "DROP POLICY/CONSTRAINT/INDEX/FUNCTION/TRIGGER/VIEW -> pass" "$repo" true \
  "check-migrations: OK"

repo=$(new_fixture_repo)
add_migration "$repo" 0003_gadgets.sql $'-- Rollback:\n--   DROP TABLE gadgets;\n--   ALTER TABLE widgets DROP COLUMN name; ALTER TABLE x RENAME TO y;\nCREATE TABLE IF NOT EXISTS gadgets (id uuid PRIMARY KEY); -- DROP TABLE gadgets to undo\n'
assert_guard "DROP TABLE / RENAME inside -- comments -> pass" "$repo" true \
  "check-migrations: OK"

repo=$(new_fixture_repo)
add_migration "$repo" 0003_renamed_at.sql $'ALTER TABLE widgets ADD COLUMN IF NOT EXISTS renamed_at timestamptz;\n'
assert_guard "an identifier that merely contains 'rename' -> pass" "$repo" true \
  "check-migrations: OK"

echo ""
echo "check-migrations: ${pass_count} passed, ${fail_count} failed"
[ "$fail_count" -eq 0 ]

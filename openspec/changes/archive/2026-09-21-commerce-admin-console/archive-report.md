# Archive Report: commerce-admin-console

schema: gentle-ai.sdd-archive-report/v1
change: commerce-admin-console
archive_date: 2026-09-21
artifact_store: hybrid
status: success

## Final State

- Native verify evidence: `sha256:10298b22d50a9182b858cdc378e3707d6622a8e85b1442528df8457c3643e6da`
- Verify verdict: PASS WITH WARNINGS
- Requirements: 20/20
- Scenarios: 39/39
- Blockers: 0
- Critical findings: 0
- .NET tests: 633/633
- Web tests: 63/63
- Build: succeeded with the existing 24 NU1903 advisories
- Final-state fixes incorporated: `branding.json` upgrade-preservation proof and production `UserAdminClient` POS endpoint proof.
- Remaining warnings are non-blocking: partial XAML/composition lifecycle proof, stale `/platform/organizations` wording in one specification sentence (implementation uses `/account/organizations`), and existing `System.IO.Packaging 8.0.0` NU1903 advisories.

## Delta Correction

Added explicit `RENAMED Requirements` mappings to the platform-administration delta, after its `MODIFIED Requirements` section so the native composer applies renames before same-change modifications:

- `Platform Admin Identity and Credentials` → `Sysadmin Identity Lives in the Unified Model`
- `Platform-Admin Sign-In` → `Single Sign-In Endpoint For Every Identity`
- `List Organizations` → `Cross-Org Read Requires Explicit Sysadmin Capability, Fail-Closed`
- `Platform-Admin Scope Isolation` → `Cross-Org Endpoint Isolation From Org-Scoped Callers`

Requirement and scenario semantics were preserved; no canonical specification was manually merged.

## Composition Commands

Each command returned exit code 0:

```text
gentle-ai sdd-archive-compose --canonical "openspec/specs/organization-persistence/spec.md" --delta "openspec/changes/commerce-admin-console/specs/organization-persistence/spec.md" --output "openspec/specs/organization-persistence/spec.md.compose-tmp"
gentle-ai sdd-archive-compose --canonical "openspec/specs/platform-administration/spec.md" --delta "openspec/changes/commerce-admin-console/specs/platform-administration/spec.md" --output "openspec/specs/platform-administration/spec.md.compose-tmp"
gentle-ai sdd-archive-compose --canonical "openspec/specs/user-credentials/spec.md" --delta "openspec/changes/commerce-admin-console/specs/user-credentials/spec.md" --output "openspec/specs/user-credentials/spec.md.compose-tmp"
gentle-ai sdd-archive-compose --canonical "openspec/specs/web-app-routing/spec.md" --delta "openspec/changes/commerce-admin-console/specs/web-app-routing/spec.md" --output "openspec/specs/web-app-routing/spec.md.compose-tmp"
```

The full `admin-console` spec was mechanically copied because no canonical main spec existed.

## Recursive Diff Readback

Admin-console mechanical copy (`diff -r` verbatim output):

```text
```

Pre-move snapshot versus archived tree (`diff -r` verbatim output):

```text
```

Both recursive comparisons produced empty output and exit code 0.

## Archive Destination

`openspec/changes/archive/2026-09-21-commerce-admin-console/`

Contents:

- `proposal.md`
- `specs/admin-console/spec.md`
- `specs/organization-persistence/spec.md`
- `specs/platform-administration/spec.md`
- `specs/user-credentials/spec.md`
- `specs/web-app-routing/spec.md`
- `design.md`
- `tasks.md` (68/68 tasks complete)
- `apply-progress.md`
- `verify-report.md`
- `archive-report.md`

## Source of Truth Updates

- Created `openspec/specs/admin-console/spec.md` from the full delta.
- Composed `openspec/specs/organization-persistence/spec.md` with branch requirements.
- Composed `openspec/specs/platform-administration/spec.md` with unified sysadmin requirements.
- Composed `openspec/specs/user-credentials/spec.md` with staff listing requirements.
- Composed `openspec/specs/web-app-routing/spec.md` with guarded admin routes.

## Engram Traceability

Artifact observations read during archive:

- `2105` delta specs
- `2106` design
- `2107` tasks
- `2108` planning session summary
- `2113` apply progress
- `2131` verify report
- `2134` upgrade-preservation proof
- `2132` branding and endpoint remediation
- `2136` fresh verification session summary
- `2130` apply completion discovery
- `2129` accepted POS evidence substitution
- `2137` prior archive composition blocker

No commit, push, pull request, or native review was performed.

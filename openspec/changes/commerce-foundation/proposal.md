# Proposal: Establish the commerce foundation

## Intent

Define a walking skeleton for tenant isolation, offline operation, private ordering, synchronization, and Windows upgrades without implementing the whole PRD.

## Scope

### In Scope

- Resolve runtime, persistence, packaging, and test-harness ADRs.
- Establish tenant/branch identity, authorization, audit, branch-owned SQLite, and retryable sync with idempotent effects.
- Prove one enabled-customer catalogue-to-order path and one recoverable release-artifact upgrade.
- Preserve Product/Presentation reuse and local/web management parity; full screens are deferred.

### Out of Scope

- Remaining PRD modules, public marketplace discovery, complete administration, multi-station deployment, and production providers/hardware.
- Publishing releases, installing `HEAD`, replacing notebooks, or implementing under this planning authorization.

## Capabilities

### New Capabilities

- `tenant-access-foundation`: Tenant/branch isolation, roles, installation identity, audit, and offline access.
- `branch-offline-sync`: SQLite ownership, local-sale continuity, authority/freshness, acknowledged retry, and idempotent effects.
- `private-customer-ordering`: Customer enablement, revocable bound access, reusable catalogue, order submission, and acknowledgement.
- `safe-release-upgrades`: Signed artifacts, compatibility, client initiation, backup, migration, health, recovery, and rollback.

### Modified Capabilities

None; `openspec/specs/` has no existing specifications.

## Approach

Use a modular-monolith walking skeleton with explicit cloud and branch-node boundaries. Delivery retries until acknowledged; duplicates cannot repeat acceptance or downstream effects. Reversibly assume offline-branch orders remain pending and never confirm stock. Functional acceptance uses one organization and two branches; a second organization is only a security fixture.

## Affected Areas

| Area | Impact | Description |
|---|---|---|
| `openspec/changes/commerce-foundation/specs/*` | Planned/New | Four bounded capability specifications |
| Application/test projects | Planned/New | Stack-dependent executable proof |
| Packaging workflow | Planned/New | Artifact-based upgrades |

## Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Ambiguous offline identity or authority | High | Resolve ADRs before implementation |
| Ordering expands into payments or full inventory | Medium | Keep pending confirmation and defer settlement/providers |
| Upgrade corrupts local state | Medium | Require signed artifacts, backup, compatibility, recovery, and rollback tests |

## Rollback Plan

Planning rollback removes this change’s artifacts. Future slices remain independently reversible; migrations require verified backup/restore and stay separate from device replacement.

## Dependencies

- Before implementation: approved ADRs for stack/harness, tenancy/offline identity, sync/SQLite, and Windows packaging/compatibility.
- A real test command is mandatory; none exists or has passed.

## Success Criteria

- [ ] Cross-tenant access is denied using the second-organization fixture.
- [ ] A local sale succeeds offline; retried sync creates one committed business effect.
- [ ] An enabled customer can order; revoked access fails; offline destination remains pending without stock confirmation.
- [ ] Representative management authorization behaves consistently through local/web application contracts.
- [ ] N→N+1 uses a release artifact and proves backup, migration, health, interruption recovery, and rollback.

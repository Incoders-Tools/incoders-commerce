# Proposal: Commerce POS User Login

## Intent

`Commerce.Pos.Windows` authenticates only the **device**. After pairing (`PairingWindow.xaml.cs` → `POST /device/pair`), no staff identity is ever carried. The smoking gun: `MainWindow.CommitSaleButton_Click` calls `_branchNodeService.CompleteOfflineSale(..., actorId: _installationId, ...)` — every sale is attributed to the installation GUID, never to a person. The server does persist `device_credentials.issued_to_user_id`, but that records *who paired the terminal*, a different and much longer lifecycle than *who is standing at it right now*. This change introduces the missing concept: a **current operator** on a POS terminal, so desktop-originated actions can be attributed to the actual staff member.

## Scope

### In Scope
- An **operator-login layer on top of** existing device pairing (pairing is unchanged and still required).
- **One-time online provisioning per terminal-operator pair**: a staff member with valid server credentials verifies once while online, establishing a locally-cached PIN credential.
- **Offline-thereafter operator switching**: subsequent logins/switches at that terminal are a local-only PIN check with **no network call**, following `LocalInstallationStore`'s exact DPAPI pattern (decrypt failure = "no credential", never throws).
- A third plain WPF window, sibling to `PairingWindow`, constructed in `App.xaml.cs`; current-operator state as a singleton in `PosHostBuilder`.
- `CompleteOfflineSale`'s `actorId` is fed the current operator's user id instead of `_installationId`. `Commerce.BranchNode` needs **no signature change** (`actorId` is already a plain `Guid`).
- Possibly one lightweight server endpoint for the provisioning check — shape deferred to design.

### Out of Scope
- A `RecordSales` permission or any permission-gating of `CommitSaleButton_Click`. `Permission.Seller` is `ViewSales` only; gating is an explicit future change.
- Full session/shift-management UI, cash drawer, or shift reconciliation.
- Server-side reconciliation of `issued_to_user_id` against operator-session identity beyond what local attribution needs.
- Any MVVM/navigation framework. This stays plain WPF code-behind.

## Decisions (confirmed by the user — do not re-litigate)

| Decision | Answer |
|---|---|
| Mechanism | Local PIN-based operator session, DPAPI-cached per `LocalInstallationStore` |
| Connectivity | Online **once** per terminal-operator pair; fully offline for later switches |
| Rationale | Cookie reuse or a per-switch bearer token would violate the shipped invariant "cloud cannot block local sales, only synchronization" the moment two sellers share a terminal during an outage |

Precedent: `/device/pair` itself already requires connectivity — onboarding-type operations may be online, only **sales** are offline-authoritative.

## Additional Decisions (confirmed by the user)

| Decision | Answer |
|---|---|
| Staleness / revocation while offline | Cached PIN expires after **N days without successful reconnection** (exact N is a design-phase value, not re-litigated here — balances sale continuity against a deactivated operator staying usable indefinitely) |
| Multi-operator per terminal | **Yes** — more than one staff member may hold a cached PIN on the same terminal at once (real shift handoff); each operator provisions independently, once, online |
| Provisioning authority | **Self-provisioning** — any staff member with valid server credentials may provision their own PIN on a terminal they have physical access to; device pairing itself is the second factor, no separate admin authorization step |
| No operator identifiable (no cached PIN, no connectivity) | **Never block the sale** — falls back to today's behavior, attributing to the installation. Identifying the operator is additive, not a gate |

## Capabilities

### New Capabilities
- `pos-operator-session`: local operator identity on a POS terminal — online provisioning, DPAPI-cached PIN credential, offline switching, and sale attribution.

### Modified Capabilities
- `pos-installation-identity`: device identity gains an operator dimension; `issued_to_user_id` (who paired) is explicitly distinguished from current-operator identity.

## Approach

Additive and desktop-local. A new `LocalOperatorStore` mirrors `LocalInstallationStore` verbatim (DPAPI at rest, tolerant decrypt). Provisioning performs one online credential verification and stores the resulting operator id plus a locally-derived PIN verifier. A new `OperatorLoginWindow` (PairingWindow's pattern) gates `MainWindow`; a `CurrentOperator` singleton in `PosHostBuilder` supplies the id to `CompleteOfflineSale`. Operator switching is operator-triggered from `MainWindow`, mirroring `RepairButton_Click`'s "explicit action, not interrupt" pattern.

## Open Questions (design phase MUST own these)

1. **Provisioning endpoint**: reuse `/account/sign-in` or introduce a new lightweight endpoint? The default cookie scheme in `Program.cs` has no `OnRedirectToLogin`/`OnRedirectToAccessDenied` override (unlike `PlatformAdminCookie`), so it would leak an HTML 302 into a non-browser `HttpClient` if reused as-is.
2. **Local PIN credential shape**: PIN length/complexity, hash algorithm and work factor, DPAPI scope — following `LocalInstallationStore` verbatim.
3. **Exact TTL value** for offline PIN staleness (the policy itself — "expires after N days offline" — is locked above; design picks N and the reconnection-check mechanism).
4. **UI affordance** for switching between multiple cached operators on one terminal (locked: multiple operators ARE supported; design owns the picker/switch UI, mirroring `RepairButton_Click`'s "explicit action, not interrupt" pattern).

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `src/Commerce.Pos.Windows/OperatorLoginWindow.xaml(.cs)` | New | Third plain WPF window, `PairingWindow` pattern |
| `src/Commerce.Pos.Windows/LocalOperatorStore.cs` | New | DPAPI-cached operator/PIN credential, `LocalInstallationStore` template |
| `src/Commerce.Pos.Windows/App.xaml.cs` | Modified | Window flow: pair → operator login → main |
| `src/Commerce.Pos.Windows/MainWindow.xaml.cs` | Modified | `actorId:` current operator id; operator-switch affordance |
| `src/Commerce.Pos.Windows/PosHostBuilder.cs` | Modified | Register operator store + `CurrentOperator` singleton |
| `src/Commerce.BranchNode/BranchNodeService.cs` | **Unchanged** | `actorId` already `Guid`; only the value supplied changes |
| `src/Commerce.Cloud.Api/` | Possibly New | One lightweight provisioning-verification endpoint (design owns) |

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| A cached PIN is a new local secret class with no revocation story | High if unaddressed | Open question 3 is blocking for design: explicit staleness/revocation policy required |
| `issued_to_user_id` (who paired) conflated with current operator | Medium | Named as distinct lifecycles in spec; no server-side reconciliation in scope |
| Scope creep into permission-gating the sale button | Medium | Explicit non-goal; `RecordSales` does not exist and is not introduced here |
| Requiring connectivity per operator switch would break the offline invariant | Mitigated by decision | Online once per terminal-operator pair only |
| Weak PIN entropy on a physically accessible terminal | Medium | Design owns PIN complexity and verifier hardening; device pairing remains a second factor |

## Rollback Plan

Revert the commit. The login window, `LocalOperatorStore`, and the singleton disappear; `MainWindow` returns to `actorId: _installationId`. No database migration and no `Commerce.BranchNode` change to undo. Orphaned DPAPI-encrypted operator files on disk are inert (unreadable = "no credential") and can be deleted; if a server endpoint was added, it becomes unused and is removed with the same revert.

## Dependencies

- Builds on the shipped `pos-installation-identity` pairing flow (merged); pairing must run first.
- Existing `PostgresUserAccountStore` / `PasswordHasher<UserAccount>` verification path for the one-time online check.

## Success Criteria

- [ ] A paired terminal requires an operator login before `MainWindow` is usable.
- [ ] First provisioning for an operator on a terminal succeeds online and fails clearly when offline.
- [ ] After provisioning, the operator logs in with PIN alone with the network disconnected.
- [ ] A completed sale records the operator's user id as `actorId`, not `_installationId`.
- [ ] A corrupted or undecryptable local operator file is treated as "no credential" and never throws.
- [ ] `CommitSaleButton_Click` performs no permission check — behavior is unchanged apart from attribution.
- [ ] Device pairing behavior is unchanged; a lost/revoked device credential still does not block a local sale.
- [ ] `dotnet test Commerce.sln` passes.

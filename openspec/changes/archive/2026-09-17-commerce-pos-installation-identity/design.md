# Design: POS installation identity via operator sign-in

## Technical Approach

One append-only migration (`deploy/db/migrations/0004_device_credentials.sql`) adds a single table, `device_credentials`, holding the SHA-256 hash of a server-generated opaque secret together with the **server's own** record of `organization_id`, `branch_id`, `installation_id`, and revocation state. A new anonymous endpoint (`Endpoints/Device.cs`, `POST /device/pair`) verifies a real operator email+password through the *exact* `PostgresUserAccountStore` path `/account/sign-in` already uses, reads the user's `branch_scope`, and either returns the branch list for selection or issues a credential. `DeviceBearerAuthenticationHandler` stops parsing caller-claimed GUIDs entirely: it hashes the presented bearer, looks the row up, and mints claims from the stored row. `Commerce.Pos.Windows` drops all self-minted org/branch GUIDs, gains a `PairingWindow`, and persists the server-issued material.

`InstallationIdentityService` and `Commerce.Domain.Tenancy.Installation` are **deleted** — resolved explicitly below, not deferred.

This is a coordinated breaking change: the old `Bearer {organizationId}.{installationId}` shape stops working the moment `0004` + the new handler deploy, so POS and Cloud.Api ship in one PR.

## Architecture Decisions

| Decision | Choice and rationale | Rejected alternative |
|---|---|---|
| **Credential shape (the core decision)** | A server-generated **opaque secret**: 32 cryptographically random bytes, base64url-encoded, returned once in plaintext and never stored. The server persists only `token_hash = sha256(secret)` as the table's PRIMARY KEY and looks it up on every `/sync` request. The credential carries **zero** claims — every identity attribute (`organization_id`, `branch_id`, `installation_id`) comes from the server's own row. This is what makes the credential "independently verifiable" in the only sense that matters here: a forged or replayed string resolves to no row. | (a) **Signed/stateless token (JWT or hand-rolled HMAC)** — requires a signing key, key storage, and key rotation this repo has none of; adds a library where the repo has deliberately zero (`no EF, no JWT library` precedent). Decisively: fixed requirement 5 makes **revocation** a first-class feature, and a stateless token cannot be revoked without a per-request denylist lookup — i.e. the same database round-trip, with a key-management burden bolted on for nothing. (b) **Reuse the Identity auth cookie** — a desktop process is not a browser; the cookie is user-scoped, not installation-scoped, and expires on sign-out. |
| Hashing algorithm for the secret | Plain **SHA-256**, not `PasswordHasher<UserAccount>`. A 256-bit random secret has no dictionary to attack, so a deliberately slow KDF buys nothing and would add ~100 ms to *every* sync request. `users.password_hash` correctly uses `PasswordHasher` because passwords are low-entropy; that asymmetry is intentional and must be documented in the migration comment so it does not read as an oversight. | bcrypt/PBKDF2 via `PasswordHasher` — per-request cost with zero security gain at this entropy. |
| **`device_credentials` RLS shape** | **Asymmetric, exactly the `user_directory` precedent**, because this is the same class of problem: verification must resolve a credential *before* any organization scope is known. Three per-command policies: `FOR SELECT USING (true)`; `FOR INSERT WITH CHECK (organization_id = current org)`; `FOR UPDATE USING (true) WITH CHECK (is_revoked)`. The UPDATE policy is the interesting one — it permits an **unscoped** update *only if the resulting row is revoked*, which is precisely what re-pairing across organizations needs (a terminal re-paired into org B must revoke its org-A row, and the request's scope is org B) while making an unscoped un-revoke or field-rewrite structurally impossible. | (a) **Symmetric `organization_id`-scoped policy** like `users` — the handler cannot set `app.current_org_id` before lookup, because resolving the org *is* the lookup. Fails closed on every request. (b) **No RLS** — the only table in the schema without it; breaks the readiness check's uniform verification and the `MigrationRlsTests` invariant. (c) **Blanket `USING (true)` for UPDATE with `WITH CHECK (true)`** — lets any authenticated context rewrite any terminal's binding. |
| **`InstallationIdentityService`: subsumed, then deleted** | The `device_credentials` row **is** the installation registration. It carries `installation_id`, `branch_id`, `replaces_installation_id`, and `revoked_at` — every field the in-memory service modelled, now durable. `InstallationIdentityService.cs` and `Commerce.Domain/Tenancy/Installation.cs` are deleted; `TenantAccessTests.cs`'s hardware-replacement test moves to `DeviceCredentialStoreTests` against real Postgres. Keeping a process-lifetime `Dictionary` that claims in its own XML doc to survive "application upgrades" while resetting every restart is a comment that lies about the product. | (a) **Make it Postgres-backed as a separate `installations` table** — a second table whose entire content (`id`, `branch_id`, `revoked`, `replaces`) is a strict subset of `device_credentials`, joined 1:1 on every verification. Two tables, two RLS policies, one extra round-trip, zero extra information. (b) **Leave it in memory** — fixed decision 7 explicitly forbids hand-waving this. |
| Pairing flow: two steps over **one** stateless endpoint | `POST /device/pair` takes `{email, password, installationId, branchId?}` and returns a discriminated response. Step 2 (after branch selection) **re-posts the credentials** rather than redeeming a server-held pairing ticket. Stateless: no new singleton, no TTL, no single-use bookkeeping — and critically, it survives multi-instance hosting, which `BootstrapTokenRegistry`'s in-memory design already cannot. The price is one extra password verification on a once-per-terminal flow. | (a) **A `PairingTicketRegistry` mirroring `BootstrapTokenRegistry`** — inherits that class's known single-process limitation on a path a scaled Railway deployment would hit; adds expiry/replay logic for a 10-second gap. (b) **Two separate endpoints** (`/device/branches` + `/device/pair`) — duplicates the entire credential-verification block, including the timing-parity dummy hash. |
| `CloudTenantScope` stays `(Guid OrganizationId)` | Branch identity travels as a **claim** (`branch_id`) read via a new `DeviceIdentity.TryResolve(principal)` helper, not as a widened `CloudTenantScope`. `CloudTenantScope` is constructed on the cookie path too (`/account/sign-in`, catalog, ordering), where no single branch exists; widening it would force a meaningless `Guid?` through every one of those call sites. | Add `BranchId` to the record — ripples into `Catalog.cs`, `Ordering.cs`, `TenantScopeEndpointFilter`, and every store method signature, for one consumer. |
| No verification cache | The handler hits Postgres on every `/sync` request. Sync is operator-triggered and low-frequency; the lookup is a single PK probe. A cache with any TTL > 0 directly weakens revocation, which is the feature. Recorded as an explicit non-goal so a later "optimization" does not silently reintroduce it. | In-memory `MemoryCache` keyed by token hash — trades the one property this design exists to provide. |
| Device token at rest on the terminal | **DPAPI-encrypted** (`ProtectedData.Protect`/`Unprotect`, `DataProtectionScope.CurrentUser`) via the `System.Security.Cryptography.ProtectedData` NuGet package — the user explicitly chose this over plaintext-with-server-side-revocation-as-the-only-control, accepting this as the first new `PackageReference` in the repo. The package is a thin, first-party Microsoft wrapper over the Windows DPAPI syscall — no crypto is hand-rolled, no key material is managed by application code (the OS derives the key from the current Windows user's credentials). Encrypt on write in `LocalInstallationStore.Save`, decrypt on read in `LoadOrCreate`; a decrypt failure (e.g. `installation.json` copied to a different machine/user) is treated identically to "no valid credential" and routes straight into the pairing/re-pair flow, never a crash. | (a) Plaintext (the design's original default) — rejected by the user; server-side revocation remains a real compensating control but is not sufficient on its own for their bar. (b) A custom encryption scheme — no reason to hand-roll when the OS-native, zero-key-management primitive is a one-file wrapper away. |

## Data Flow

```text
PAIRING (once per terminal, or on re-pair)
  POS first run: LocalInstallationStore.LoadOrCreate()
      -> installation.json absent -> { InstallationId = Guid.NewGuid(), Pairing = null }
         (installationId is client-minted BY DESIGN: it is a terminal label,
          never an authorization input; the server stores it, never trusts it)
      -> Pairing == null  =>  App.OnStartup shows PairingWindow modally
                              (MainWindow is not created; there is no branch to sell into)

  POST /device/pair { email, password, installationId, branchId? }   [AllowAnonymous]
    1. blank email/password                      -> 400 ValidationProblem
    2. store.FindDirectoryEntryAsync(email)      -> null: dummy-hash verify, 401   <- exact
    3. store.FindByEmailAsync(scope, email)      -> null: dummy-hash verify, 401      sign-in
    4. hasher.VerifyHashedPassword(...)          -> Failed: 401                       path,
    5. credential.IsRevoked                      -> 401 (same generic body)           byte
       (every 2-5 failure is the SAME 401 - never reveals which check failed)         for
    6. store.LoadActorAsync(scope, userId)       -> actor.BranchScope                  byte
    7. branchScope.Count == 0
         -> 403 { code: "no-branches-assigned" }        <- NOT a generic 401
    8. organizationStore.ListBranchesAsync(scope, branchScope)   [org-scoped, RLS on]
    9. branchId == null && branches.Count > 1
         -> 200 { status: "branch-selection-required", branches: [{id, name}, ...] }
   10. branchId == null && branches.Count == 1  -> selected = branches[0]
       branchId != null && not in branchScope   -> 403 { code: "branch-not-in-scope" }
   11. deviceCredentialStore.IssueAsync(scope, installationId, branchId, userId)
         BEGIN
           set_config('app.current_org_id', org, true)
           UPDATE device_credentials SET is_revoked = true, revoked_at = now()
             WHERE installation_id = $1 AND NOT is_revoked      <- unscoped UPDATE,
                RETURNING id                                       allowed only because
                                                                   the result IS revoked
           INSERT device_credentials (token_hash, organization_id, branch_id,
                                      installation_id, issued_to_user_id,
                                      replaces_credential_id)      <- WITH CHECK pins org
         COMMIT
    -> 200 { status: "paired", organizationId, branchId, branchName,
             installationId, deviceToken }        <- deviceToken plaintext, ONCE, only here
    -> PairingWindow writes installation.json, closes; MainWindow opens

SYNC (every request)
  CloudSyncClient.PushAsync(envelope, deviceToken)
    -> Authorization: Bearer {deviceToken}
  DeviceBearerAuthenticationHandler
    -> no/!Bearer header                    -> NoResult()
    -> sha256(token) -> store.FindByTokenHashAsync(hash)   [UNSCOPED: USING (true)]
         null                               -> Fail("Unknown device credential.")
         row.IsRevoked                      -> Fail("Device credential revoked.")
    -> claims BUILT FROM THE ROW, never from the request:
         org_id = row.OrganizationId, branch_id = row.BranchId,
         installation_id = row.InstallationId
    -> TenantScopeEndpointFilter -> CloudTenantScope(row.OrganizationId) -> RLS

OFFLINE SALE (never touches any of the above)
  CommitSaleButton_Click
    -> BranchNodeService.CompleteOfflineSale(org, branch, actor, ...)  -> branch.db
       reads ONLY _pairing.OrganizationId / .BranchId from the local file.
       Zero references to DeviceToken. Zero HTTP. Zero credential validity check.
```

### Why revocation cannot block a sale (structural, not conventional)

`Commerce.BranchNode` does not reference `Commerce.Pos.Windows`, so `BranchNodeService` and `BranchSyncStore` cannot reach `CloudSyncClient` even by accident — the assembly graph forbids it, and an architecture test asserts it. Inside `MainWindow.xaml.cs` the split is equally mechanical: `DeviceToken` is read in exactly **one** expression, the `PushAsync` call inside `SyncButton_Click`. `CommitSaleButton_Click` never reads it. A rejected credential therefore takes the existing `SyncPushResult.Failed` path, which already skips `_branchNodeService.Acknowledge(...)`, leaving outbox rows `Pending` so they flush automatically after a successful re-pair. The only new behavior is that a 401/403 sets `SyncPushResult.CredentialRejected`, which appends one sentence to the existing per-operation failure list.

## File Changes

| Path | Action | Purpose |
|---|---|---|
| `deploy/db/migrations/0004_device_credentials.sql` | Create | `device_credentials`, forced RLS, three per-command policies, `app_runtime` grants. `0001`–`0003` untouched. |
| `deploy/dev/db/init-rls.sql` | Modify | Same DDL appended verbatim (hand-sync convention). |
| `src/Commerce.Cloud.Api/Persistence/PostgresDeviceCredentialStore.cs` | Create | `IssueAsync` (one transaction: revoke-prior + insert), `FindByTokenHashAsync` (unscoped), `RevokeAsync`. `PostgresCloudInboxStore` shape. |
| `src/Commerce.Cloud.Api/Persistence/DeviceCredentialRecords.cs` | Create | `DeviceCredentialRecord`, `IssuedDeviceCredential`, `DeviceTokenHasher` (generate + hash; the only crypto surface). |
| `src/Commerce.Cloud.Api/Persistence/PostgresOrganizationStore.cs` | Modify | Add `ListBranchesAsync(scope, Guid[] branchIds, ct)` — org-scoped `WHERE id = ANY($1) ORDER BY name`. Bootstrap transaction untouched. |
| `src/Commerce.Cloud.Api/Endpoints/Device.cs` | Create | `POST /device/pair`, `AllowAnonymous`. Shares the dummy-hash timing-parity constant with `AccountEndpoints` (extracted to an `internal static` holder, not duplicated). |
| `src/Commerce.Cloud.Api/Authentication/DeviceBearerAuthenticationHandler.cs` | Modify | Full rewrite: hash → lookup → revocation check → claims from the row. Adds `BranchClaimType`. The stale XML doc describing the self-signed shape is deleted. |
| `src/Commerce.Cloud.Api/Tenancy/DeviceIdentity.cs` | Create | `TryResolve(principal, out DeviceIdentity)` — pure claim reader for `branch_id`/`installation_id`, mirroring `TenantScopeResolver`'s hosting-type-free style. |
| `src/Commerce.Cloud.Api/HealthChecks/PostgresReadinessHealthCheck.cs` | Modify | Verify `device_credentials` table, forced RLS, and all three policies; remediation names `0004`. |
| `src/Commerce.Cloud.Api/Program.cs` | Modify | Register `PostgresDeviceCredentialStore`; `app.MapDeviceEndpoints()`. |
| `src/Commerce.Application/Access/InstallationIdentityService.cs` | **Delete** | Subsumed by `device_credentials`. |
| `src/Commerce.Domain/Tenancy/Installation.cs` | **Delete** | Its only consumer was the deleted service. |
| `src/Commerce.Pos.Windows/LocalInstallationStore.cs` | Modify | New shape (below); `Load()` / `Save()` / `LoadOrCreate()`; no `InstallationIdentityService` parameter; no self-minted org/branch GUIDs. |
| `src/Commerce.Pos.Windows/DevicePairingClient.cs` | Create | Typed `HttpClient` for `POST /device/pair`; returns a discriminated `PairingOutcome`. Separate from `CloudSyncClient` — pairing is anonymous, sync is bearer-authenticated. |
| `src/Commerce.Pos.Windows/PairingWindow.xaml` + `.xaml.cs` | Create | Email `TextBox`, `PasswordBox`, "Sign in" button, collapsed branch `ListBox` + "Pair" button revealed only on `branch-selection-required`, status `TextBlock`. Plain `StackPanel`, identical style to `MainWindow.xaml`. Handles both first-run and re-pair. |
| `src/Commerce.Pos.Windows/MainWindow.xaml` + `.xaml.cs` | Modify | Identity block shows org/branch **name** + operator email; new "Re-pair terminal" button (always visible) opening `PairingWindow` modally and reloading identity on success; `SyncButton_Click` appends the credential-rejected hint. |
| `src/Commerce.Pos.Windows/CloudSyncClient.cs` | Modify | `PushAsync(envelope, string deviceToken, ct)`; `SyncPushResult` gains `CredentialRejected` set on 401/403. |
| `src/Commerce.Pos.Windows/PosHostBuilder.cs` | Modify | Drop `InstallationIdentityService`; add `DevicePairingClient` + `PairingWindow`. |
| `src/Commerce.Pos.Windows/App.xaml.cs` | Modify | If unpaired, `ShowDialog()` the pairing window first; on cancel, `Shutdown()` — an unpaired terminal has no branch to sell into. |
| `tests/Commerce.Integration/MigrationRlsTests.cs` | Modify | Apply `0004`; idempotency; the three asymmetric-policy cases. |
| `tests/Commerce.Integration/DeviceCredentialStoreTests.cs` | Create | Issuance, lookup, revocation, re-issue-revokes-prior, hardware-replacement lineage. |
| `tests/Commerce.Integration/DeviceEndpointTests.cs` | Create | The full `/device/pair` matrix + end-to-end `/sync` with the issued token. |
| `tests/Commerce.Integration/TenantAccessTests.cs` | Modify | Remove the in-memory `InstallationIdentityService` test (relocated above). |
| `tests/Commerce.Integration/PosCompositionRootTests.cs` | Modify | Resolve `DevicePairingClient`; assert `InstallationIdentityService` is no longer registered; add the assembly-reference architecture test. |
| `tests/Commerce.Integration/LocalInstallationStoreTests.cs` | Create | Round-trip; first-run-unpaired; re-pair overwrite preserves `InstallationId`. |
| `deploy/pos-manual-verify.md` | Modify | New "Part 2" checklist, executed live on this machine. |

## Interfaces / Contracts

```sql
-- deploy/db/migrations/0004_device_credentials.sql
--
-- token_hash is sha256(secret) hex. The secret is 32 random bytes, returned to
-- the terminal exactly once and NEVER stored. Plain SHA-256 is correct here and
-- is NOT an oversight: the input is a 256-bit uniformly random secret, so a slow
-- KDF (as users.password_hash correctly uses for LOW-entropy passwords) would add
-- per-request latency to every sync for zero security gain.
CREATE TABLE IF NOT EXISTS device_credentials (
    token_hash             text PRIMARY KEY,
    id                     uuid NOT NULL UNIQUE,
    organization_id        uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    branch_id              uuid NOT NULL REFERENCES branches (id) ON DELETE CASCADE,
    installation_id        uuid NOT NULL,
    issued_to_user_id      uuid NOT NULL,          -- no FK: matches users' existing no-FK status
    replaces_credential_id uuid NULL,              -- ADR-002 hardware-replacement lineage
    is_revoked             boolean NOT NULL DEFAULT false,
    issued_at              timestamptz NOT NULL DEFAULT now(),
    revoked_at             timestamptz NULL
);
CREATE INDEX IF NOT EXISTS device_credentials_installation_idx
    ON device_credentials (installation_id) WHERE NOT is_revoked;

ALTER TABLE device_credentials ENABLE ROW LEVEL SECURITY;
ALTER TABLE device_credentials FORCE ROW LEVEL SECURITY;

REVOKE ALL ON device_credentials FROM PUBLIC;
GRANT SELECT, INSERT, UPDATE ON device_credentials TO app_runtime;

-- Asymmetric, same class of problem as user_directory: verification resolves the
-- credential BEFORE any tenant scope exists, so the org id cannot be known yet.
DROP POLICY IF EXISTS device_credentials_lookup ON device_credentials;
CREATE POLICY device_credentials_lookup ON device_credentials
    FOR SELECT USING (true);

DROP POLICY IF EXISTS device_credentials_issue ON device_credentials;
CREATE POLICY device_credentials_issue ON device_credentials
    FOR INSERT WITH CHECK (organization_id = NULLIF(current_setting('app.current_org_id', true), '')::uuid);

-- Unscoped UPDATE is permitted ONLY when the resulting row is revoked. This is
-- exactly what cross-org re-pairing needs (revoke the org-A row while scoped to
-- org B) and makes an unscoped un-revoke or field rewrite unrepresentable.
DROP POLICY IF EXISTS device_credentials_revoke ON device_credentials;
CREATE POLICY device_credentials_revoke ON device_credentials
    FOR UPDATE USING (true) WITH CHECK (is_revoked);
```

```csharp
// Endpoints/Device.cs — ONE endpoint, discriminated response, stateless two-step.
public sealed record DevicePairRequest(
    string Email, string Password, Guid InstallationId, Guid? BranchId);

public sealed record DeviceBranchOption(Guid Id, string Name);

// status: "paired" | "branch-selection-required"
public sealed record DevicePairResponse(
    string Status,
    IReadOnlyList<DeviceBranchOption>? Branches,   // set only when selection is required
    Guid? OrganizationId, Guid? BranchId, string? BranchName,
    Guid? InstallationId,
    string? DeviceToken);                          // plaintext, returned exactly once
```

```csharp
// Persistence/PostgresDeviceCredentialStore.cs
public sealed class PostgresDeviceCredentialStore
{
    /// UNSCOPED by design (device_credentials_lookup USING (true)): resolves the
    /// credential the request does not yet have a tenant scope for. Mirrors
    /// PostgresUserAccountStore.FindDirectoryEntryAsync's rationale exactly.
    Task<DeviceCredentialRecord?> FindByTokenHashAsync(string tokenHash, CancellationToken ct);

    /// ONE transaction: set_config -> revoke every live credential for this
    /// installation (unscoped UPDATE, allowed only because the result is revoked)
    /// -> INSERT the new row (WITH CHECK pins organization_id) -> COMMIT.
    /// A terminal therefore never holds two live credentials, and re-pairing into a
    /// different organization cannot leave the prior organization's binding alive.
    Task<IssuedDeviceCredential> IssueAsync(
        CloudTenantScope scope, Guid installationId, Guid branchId, Guid issuedToUserId, CancellationToken ct);
}
```

```csharp
// Pos.Windows/LocalInstallationStore.cs — no self-minted org/branch GUIDs.
// InstallationId IS client-minted and that is correct: it is a terminal label,
// never an authorization input, and it survives re-pairing so the server can
// revoke the terminal's prior credentials.
public sealed record LocalInstallationRecord(Guid InstallationId, DevicePairing? Pairing);

public sealed record DevicePairing(
    Guid OrganizationId, Guid BranchId, string BranchName, string OperatorEmail, string DeviceToken);
```

## Testing Strategy

| Layer | What to test | Approach |
|---|---|---|
| Integration (RLS) | `0004` applies twice cleanly; unscoped `SELECT` **returns** the row (the asymmetry that makes verification possible); cross-org `INSERT` violates `WITH CHECK`; unscoped `UPDATE … SET is_revoked = true` **succeeds**; unscoped `UPDATE … SET is_revoked = false` or `SET branch_id = …` **fails** | Extend `MigrationRlsTests.cs` with `Resolve/Apply` helpers mirroring the `0003` pair |
| Integration (store) | Issue → `FindByTokenHashAsync` returns the exact row; unknown hash → `null`; revoked row still returned with `IsRevoked = true` (the handler, not the query, decides); re-issue for the same `installationId` revokes the prior row and sets `replaces_credential_id`; re-issue into a **different** organization still revokes the prior org's row | `DeviceCredentialStoreTests.cs`, live Postgres, `PostgresTestFixture` skip convention |
| Integration (endpoint) | Wrong password / unknown email / revoked user → **identical** generic 401; empty `branch_scope` → 403 `no-branches-assigned`; one branch → `status: "paired"` with a token; two branches → `branch-selection-required` listing **only** in-scope branches; `branchId` outside scope → 403 `branch-not-in-scope`; the returned token appears in the response exactly once and never again | `DeviceEndpointTests.cs`, `WebApplicationFactory` |
| Integration (**the breaking-change regression guard**) | `POST /sync/inbox` with the *old* `Bearer {organizationId}.{installationId}` shape → **401**; with a well-formed-but-unissued random token → 401; with a revoked token → 401; with a valid token → 200 **and** the persisted `sync_inbox` row carries the credential row's org/branch, proving the identity came from the server and not the client | `DeviceEndpointTests.cs`; assert the inbox row via `PostgresCloudInboxStore` |
| Integration (architecture) | `typeof(BranchNodeService).Assembly` does not reference `Commerce.Pos.Windows` — the structural reason a revoked credential cannot reach a local write path | `PosCompositionRootTests.cs`, `GetReferencedAssemblies()` |
| Unit | `LocalInstallationStore` round-trip; first run yields `Pairing == null` with a fresh `InstallationId`; re-pair overwrites `Pairing` but **preserves** `InstallationId`; `DeviceTokenHasher` generate/hash determinism and length | `LocalInstallationStoreTests.cs`; plain xUnit, no Postgres |
| **Manual (WPF)** | Honest per the `deploy/pos-manual-verify.md` precedent — and genuinely executed live on this Windows machine, not written-and-assumed | See below |

### Manual verification script (`deploy/pos-manual-verify.md`, Part 2)

1. Fresh `%LOCALAPPDATA%\Incoders\Commerce\` → launch POS → `PairingWindow` appears, `MainWindow` does **not**.
2. Wrong password → generic failure; no `installation.json` pairing written.
3. Correct password, operator with one branch → paired without a picker; `installation.json` contains a server-issued token and real org/branch ids (verify the ids exist in Postgres with `psql`).
4. Commit a sale → row lands in `branch.db` (confirm with the `sqlite3` CLI, independent process). Sync → 200.
5. **The critical case**: `UPDATE device_credentials SET is_revoked = true` via `psql`, then in the still-running POS — commit another sale (**must succeed**, `branch.db` row present) and then sync (**must fail visibly** with the credential-rejected hint, outbox still `Pending`).
6. Click "Re-pair terminal", sign in again, pick a different branch → `installation.json` updated with the same `InstallationId` and a new token; the previously pending outbox flushes on the next sync.
7. Confirm the prior credential row is `is_revoked = true` in Postgres and that its token now returns 401 against `/sync/inbox`.

## Threat Matrix

| Native row | Applicability |
|---|---|
| Documentation-like paths | N/A — no file-classification boundary |
| Git repository selection | N/A — no product code runs Git |
| Commit state / Push state / PR commands | N/A — no VCS automation added |
| Shell / subprocess / process integration | N/A — no process is spawned |
| **Routing** | **Applicable.** A new `AllowAnonymous` endpoint (`POST /device/pair`) accepts credentials, and the `/sync` authentication handler is rewritten. Safe behavior: (a) every credential-verification failure returns the same generic 401 and the unknown-email path still runs the dummy-hash verification for timing parity; (b) `branchId` is validated against the server-loaded `branch_scope` before issuance — a caller-submitted branch is never trusted; (c) the issued token's identity claims are read exclusively from the server's stored row, never from the presented string; (d) the plaintext token is returned in exactly one response and never logged. RED tests: the identical-401 matrix, `branch-not-in-scope` 403, the old-token-shape 401 regression guard, the unissued-token 401, the revoked-token 401, and the server-identity assertion on the persisted `sync_inbox` row. |

## Migration / Rollout

Forward-only and additive. Apply `deploy/db/migrations/0004_device_credentials.sql` via `psql` on the direct (non-pooled) connection **before** deploying the new image; `/health/ready` fails closed until the table and its three policies exist, so ordering is gate-enforced rather than discipline-enforced.

**Local dev safety.** `0004` contains only `CREATE TABLE IF NOT EXISTS`, `CREATE INDEX IF NOT EXISTS`, `ALTER TABLE … ROW LEVEL SECURITY`, `REVOKE`/`GRANT`, and `DROP POLICY IF EXISTS` + `CREATE POLICY` — all on one brand-new table. It touches no existing table. Its two FKs point at `organizations`/`branches`, both created by `0003`, so `0004` **must** run after `0003` (already true of the numbering). Applying it twice is a no-op by the same mechanics `0002`/`0003` rely on.

**Coordinated cutover.** This is a genuine breaking change with no compatibility window: once the new handler is live, every existing `installation.json` is useless and every POS terminal must re-pair. That is acceptable precisely because there are no production terminals yet (the only `installation.json` in existence is this machine's verification artifact from the `commerce-deployment-orchestration` run). Shipping a dual-accept handler that still honored the self-signed shape would leave the exact vulnerability this change exists to close, live in production, behind a flag someone would forget to flip.

**Rollback.** Revert the single PR and `DROP TABLE device_credentials;`. Nothing else references it, so the drop cannot cascade into `users`, `sync_inbox`, `organizations`, or `branches`. Terminals then fall back to the reverted self-mint path after deleting `installation.json` — acceptable only as an emergency measure, since that path is the vulnerability.

## Work Units

| Unit | Scope | Budget | Test / runtime boundary | Rollback |
|---|---|---|---|---|
| 1 | Everything: `0004` + dev mirror, device credential store + records, `/device/pair`, handler rewrite, `DeviceIdentity`, branch listing, readiness, `Program.cs`, the whole `Pos.Windows` rewrite (`LocalInstallationStore`, `DevicePairingClient`, `PairingWindow`, `MainWindow`, `CloudSyncClient`, `PosHostBuilder`, `App`), two deletions, all tests, manual-verify doc | ~1,300 | `0004` applies twice cleanly; full xUnit suite green against `deploy/dev/compose.yaml`; `Commerce.Pos.Windows` builds; the 7-step manual script executed live with recorded results | Revert one commit; drop one table |

Rough breakdown: ~110 SQL across two files, ~170 credential store + records, ~140 endpoint, ~80 handler, ~40 `DeviceIdentity`, ~75 org-store/readiness/`Program.cs`, ~100 `LocalInstallationStore` + `DevicePairingClient`, ~210 WPF (`PairingWindow` XAML + code-behind, `MainWindow` edits), ~25 host wiring, **−60 deletions**, ~400 tests.

Decision needed before apply: Yes
Chained PRs recommended: No
400-line budget risk: High

**Why one PR despite the budget.** The reviewable unit is the *bearer contract*. `CloudSyncClient` and `DeviceBearerAuthenticationHandler` must agree byte-for-byte on the token shape; a split lands one side of a wire contract without the other and leaves `main` in a state where sync is provably broken. There is no ordering that avoids it — server-first rejects every existing POS, client-first sends a token no server understands. Recommend an explicit `size:exception` rather than a chain. If apply overruns badly, the **only** defensible split is **PR #1 = `0004` + dev mirror + `PostgresDeviceCredentialStore` + `DeviceCredentialRecords` + `MigrationRlsTests`/`DeviceCredentialStoreTests`** — inert schema and dead code with no behavior change, trivially revertible — chained by **PR #2 = endpoint + handler + the entire POS side + the rest of the tests**. Never a split that lands the handler rewrite without the POS rewrite, or vice versa.

## Open Questions

- [x] RESOLVED 2026-09-16: DPAPI protection of the local device token is IN SCOPE, not deferred — the user explicitly chose it over plaintext, accepting `System.Security.Cryptography.ProtectedData` as this repo's first new `PackageReference`. Apply must add the package to `Commerce.Pos.Windows.csproj`, encrypt on write (`LocalInstallationStore.Save`) and decrypt on read (`LoadOrCreate`) via `ProtectedData.Protect`/`Unprotect` with `DataProtectionScope.CurrentUser`, and treat any decrypt failure as "no valid credential" (routes into pairing/re-pair, never throws to the user).
- [ ] Credential expiry is deliberately absent (issuance + revocation + re-pair only), per the proposal's non-goal. A `expires_at timestamptz NULL` column could be added to `0004` now at zero cost and left unenforced, avoiding a later `0005` ALTER — flagged rather than silently included, because an unenforced column is a different kind of lie.
- [ ] No operator-facing revocation UI exists; revocation today is a `psql` UPDATE. That is honest for this change's scope but should be named as the next follow-up, not discovered later.

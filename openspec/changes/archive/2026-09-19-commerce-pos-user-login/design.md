# Design: Commerce POS user login

## Technical Approach

Entirely additive and desktop-local, plus **two** small server routes on the
already-existing `/device` group.

A new `LocalOperatorStore` is a structural clone of `LocalInstallationStore`
(`operators.json` beside `installation.json`, plaintext envelope, DPAPI on the
secret field only, tolerant decrypt that returns "no credential" and never
throws) holding a **list** of cached operators — multi-operator per terminal is
a property of the file shape, not a later feature. Provisioning is one online
password verification that returns the operator's user id; the PIN verifier is
derived and stored **client-side only** and never leaves the terminal. A
`CurrentOperator` singleton resolves `actorId` with a total function that has no
failure mode, so "never block a sale" is structural rather than a guarded branch.
`Commerce.BranchNode` is untouched.

## Architecture Decisions

| Decision | Choice and rationale | Rejected alternative |
|---|---|---|
| **Provisioning endpoint** | A **new** `POST /device/operators/verify` added to the existing `DeviceEndpoints` group, **behind `.RequireAuthorization("DeviceBearer")`**. It reuses `PostgresUserAccountStore.FindDirectoryEntryAsync` → `FindByEmailAsync` → `PasswordHasher<UserAccount>.VerifyHashedPassword` → `IsRevoked` → `LoadActorAsync` **verbatim from `/device/pair`**, including the shared `DeviceEndpoints.DummyPasswordHash` timing-parity call on both unknown-email paths and the single generic 401 across every credential failure. It calls **no `SignInAsync`**: no cookie, no session, no server-side state. Response is the minimum the client needs to mint a local credential: `{status:"verified", userId, email, organizationId}`. Device-bearer rather than anonymous is the substantive part: the org and branch come from the **stored `device_credentials` row**, so the endpoint can also assert server-side that the operator's `BranchScope` contains *this terminal's* branch (403 `"branch-not-in-scope"`, the status-string shape `/device/pair` already uses) — and it is not a new anonymous internet-facing password oracle. | **Reusing `/account/sign-in`** — the default cookie scheme in `Program.cs` has no `OnRedirectToLogin`/`OnRedirectToAccessDenied` override (unlike `PlatformAdminCookie`), so a challenge emits a 302 to an HTML login page into a non-browser `HttpClient`; and it mints a session the WPF client must then carry and expire for no reason. Adding those overrides to the *default* scheme to suit the POS would change browser/SPA behavior for an unrelated caller. **Anonymous, like `/device/pair`** — `/device/pair` is anonymous only because it is the flow that *issues* the first credential; here one already exists, so requiring it costs nothing and buys the branch-scope assertion. |
| **PIN shape and verifier** | **Exactly 6 numeric digits**, rejecting all-same-digit and strictly ascending/descending runs. Verifier = **PBKDF2-HMAC-SHA256, 128-bit random salt, 256-bit subkey, 210 000 iterations**, via `Rfc2898DeriveBytes.Pbkdf2` and compared with `CryptographicOperations.FixedTimeEquals` — the same primitive, salt size, and subkey size as `PasswordHasher<T>`'s v3 format, so this is the repo's existing hashing decision re-expressed, not a new one. It is computed and verified **only** in `Commerce.Pos.Windows`; the PIN and its verifier are never sent to the server, in provisioning or afterwards. | **Referencing `Microsoft.Extensions.Identity.Core` to reuse `PasswordHasher<T>` literally** — it drags an ASP.NET Identity package into a WPF client for one 32-byte derivation, and its `IPasswordHasher<TUser>` shape wants a user entity this layer does not have. `Rfc2898DeriveBytes` ships in the Windows Desktop shared framework the csproj already relies on for `ProtectedData`. **A plain SHA-256 of the PIN** — a 10⁶ space falls in milliseconds. PBKDF2 does not make 10⁶ unbreakable either; the honest boundary is DPAPI plus physical possession of the terminal, which is why the verifier is encrypted too (next row). |
| **`LocalOperatorStore` file and DPAPI scope** | `operators.json` in the same `dataDirectory` as `installation.json`. Envelope mirrors `PersistedInstallationDto` exactly: plaintext scalars, **one** protected field. Here the protected field is the per-entry verifier blob (`salt‖subkey`), `ProtectedData.Protect(..., optionalEntropy: null, DataProtectionScope.CurrentUser)` — **`CurrentUser`, identical to `LocalInstallationStore`**, because the threat being priced is the file being copied to another machine or read by another Windows account, and `CurrentUser` binds the key to the POS operator's Windows profile while `LocalMachine` would let *any* local account decrypt it. Decrypt failure, malformed base64, or a corrupt file yields **"that entry is not cached"** (and a whole-file `JsonException` yields "no operators cached"), never a throw — `TryDecryptPairing`'s exact contract. Encrypting the verifier is what forces an offline PIN brute-force to first obtain code execution as that Windows user. | **`DataProtectionScope.LocalMachine`** — would survive a Windows profile change, but any account on the box could then decrypt every operator's verifier and brute-force a 6-digit PIN offline. **A second file per operator** — fans out the tolerant-decrypt logic; one list in one file keeps the store a single, testable clone. **Storing the verifier in plaintext** — a hash is not a replayable secret, but at 10⁶ candidates it is effectively the PIN. |
| **Staleness TTL and reconciliation trigger** | **14 days** since `LastVerifiedUtc`. Reconciliation piggybacks on the existing `SyncButton_Click`, via a second device-bearer route **`GET /device/operators/{userId}/status`** → `{status:"active"\|"inactive"}` (active = row exists in the terminal's org, not revoked, branch scope contains the terminal's branch). It needs no password, so it is a pure background-of-an-explicit-action check. It runs **before** the `pending.Count == 0` early return, so pressing Sync with an empty outbox still reconciles. `active` ⇒ stamp `LastVerifiedUtc = now`; `inactive` ⇒ **delete that entry** (server revocation beats the timer); unreachable ⇒ change nothing and let the TTL run. A stale entry stays on disk and stays in the reconciliation sweep, so one successful Sync revives it **without re-entering the server password** — only the login picker filters it out. | **7 days** — one holiday weekend plus a rural-branch outage locks out a whole store. **30 days** — a terminated employee keeps attributing sales to themselves for a month. 14 is a fortnightly pay period and the midpoint. **A background timer / launch-time check** — invents a new network trigger, slows startup, and would re-introduce "the cloud can interfere with the terminal"; the operator already has one explicit connectivity ritual and this rides it. **Sending the device token to check status implicitly during push** — `/sync/inbox` is a hot path with a fixed contract; a separate idempotent GET stays reviewable. |
| **One window for three flows** | A single `OperatorLoginWindow` (modal, `PairingWindow`'s exact shape: ctor-injected client + store, `DialogResult`, a `public CachedOperator? ActiveOperator { get; private set; }` read by the caller). It shows a picker of **non-stale** cached entries + PIN box; when exactly one is cached it is auto-selected and no picker appears (the auto-select-single-branch convention `/device/pair` already sets). A collapsed **"Add another operator"** panel (email / password / PIN / confirm PIN) is revealed on demand — `BranchSelectionPanel`'s reveal idiom — and is revealed automatically, with the picker hidden, when zero entries are cached. From `MainWindow`, a **"Switch operator"** button sits beside "Re-pair terminal" and opens the same window with `Owner = this`: `RepairButton_Click`'s shape verbatim, always visible, explicit, never an interrupt, and never a precondition of `CommitSaleButton_Click`. | **Two windows (login vs. switch)** — identical states, duplicated code-behind. **An operator picker embedded in `MainWindow`** — turns identity into ambient chrome and invites an automatic "who are you?" interrupt mid-sale, the exact pattern re-pairing already rejected. |
| **Cancel does not shut down; `actorId` is total** | `App.xaml.cs` always shows `OperatorLoginWindow` after pairing resolves, but — **unlike `PairingWindow`** — a cancel/close does **not** `Shutdown()`. The window carries an explicit **"Continue without operator"** button, and cancel/close means the same thing: `MainWindow` opens with `CurrentOperator.Value == null`. `CommitSaleButton_Click` changes to exactly `actorId: _currentOperator.ResolveActorId(_installationId)`, where `ResolveActorId(Guid fallback) => Value?.UserId ?? fallback`. There is no `if`, no null check, and no throwing path at the call site, so the locked "never block a sale" decision is enforced by the signature rather than by a reviewer noticing a branch. | **Shutting down on cancel, like pairing** — an unpaired terminal has no branch to sell into, but an unidentified operator does; that would convert an additive attribution feature into a hard gate and break the locked decision. **`Guid?`-typed `actorId` through `CompleteOfflineSale`** — a `Commerce.BranchNode` signature change the proposal explicitly excludes. |

## Data Flow

```text
Provisioning (ONLINE, once per terminal + operator)
  OperatorLoginWindow "Add another operator" {email, password, pin, confirmPin}
    -> PIN policy check (6 digits, not trivial)            [local, before any I/O]
    -> POST /device/operators/verify {email, password}   Bearer <device token>
         RequireAuthorization("DeviceBearer") -> org/branch from the STORED row
         FindDirectoryEntryAsync -> null -> dummy hash -> 401   (timing parity)
         FindByEmailAsync -> null -> dummy hash -> 401
         VerifyHashedPassword Failed | IsRevoked          -> 401 (same body)
         LoadActorAsync.BranchScope !contains branch_id   -> 403 branch-not-in-scope
      <- 200 {status:"verified", userId, email, organizationId}
    -> verifier = PBKDF2(pin, salt)         [PIN never crosses the wire]
    -> LocalOperatorStore.Upsert(entry{userId, email, orgId,
         Protect(salt||subkey), LastVerifiedUtc = now})
    -> ActiveOperator = entry; DialogResult = true

Returning login / switch (OFFLINE, zero network)
  pick entry (auto-selected when exactly one non-stale) + PIN
    -> TryDecrypt(entry) -> null  => "not cached" (no throw, entry hidden)
    -> FixedTimeEquals(PBKDF2(pin, salt), subkey) -> false -> "Incorrect PIN."
    -> ActiveOperator = entry

Sale attribution
  CommitSaleButton_Click
    -> CompleteOfflineSale(..., actorId: _currentOperator.ResolveActorId(_installationId), ...)
         operator signed in  -> operator user id
         none / cancelled    -> _installationId   (today's behavior, unchanged)

Reconciliation (piggybacks the existing Sync button)
  SyncButton_Click
    -> ReconcileOperatorsAsync()          <- FIRST, before the pending.Count==0 return
         for each cached entry (INCLUDING stale ones):
           GET /device/operators/{userId}/status   Bearer <device token>
             "active"   -> LastVerifiedUtc = now       (a stale entry revives)
             "inactive" -> remove the entry            (revocation beats the timer)
             unreachable/401 -> leave untouched; the 14-day TTL still applies
    -> existing push loop, unchanged
```

## File Changes

| Path | Action | Purpose |
|---|---|---|
| `src/Commerce.Pos.Windows/LocalOperatorStore.cs` | Create | `operators.json`; list of `CachedOperator`; DPAPI `CurrentUser` on the verifier only; `Load()`, `Upsert()`, `Remove()`, `TouchVerified()`; tolerant decrypt, never throws. |
| `src/Commerce.Pos.Windows/OperatorPinCredential.cs` | Create | `IsValidPin`, `Derive(pin) -> (salt, subkey)`, `Verify(pin, salt, subkey)`. Pure, no I/O, no WPF — unit-testable. |
| `src/Commerce.Pos.Windows/OperatorProvisioningClient.cs` | Create | Typed `HttpClient`: `VerifyAsync(email, password, deviceToken)` and `GetStatusAsync(userId, deviceToken)`; `DevicePairingClient`'s discriminated-outcome shape. |
| `src/Commerce.Pos.Windows/CurrentOperator.cs` | Create | Singleton holding `CachedOperator?`; `Set`/`Clear`/`ResolveActorId(Guid fallback)`. |
| `src/Commerce.Pos.Windows/OperatorLoginWindow.xaml(.cs)` | Create | Picker + PIN, collapsed provisioning panel, "Continue without operator". |
| `src/Commerce.Pos.Windows/App.xaml.cs` | Modify | Show the window after pairing; cancel does **not** shut down; pass `CurrentOperator` to `MainWindow`. |
| `src/Commerce.Pos.Windows/MainWindow.xaml(.cs)` | Modify | `actorId:` → `ResolveActorId`; "Switch operator" button; operator line in `RefreshIdentityText`; reconciliation call at the top of `SyncButton_Click`. |
| `src/Commerce.Pos.Windows/PosHostBuilder.cs` | Modify | `AddSingleton(new LocalOperatorStore(Path.Combine(dataDirectory, "operators.json")))`, `AddSingleton<CurrentOperator>()`, `AddHttpClient<OperatorProvisioningClient>` on the same `cloudApiBaseUrl`. |
| `src/Commerce.Cloud.Api/Endpoints/Device.cs` | Modify | `POST /device/operators/verify` + `GET /device/operators/{userId}/status`, both `.RequireAuthorization("DeviceBearer")`, reusing the existing verification path and `DummyPasswordHash`. |
| `tests/Commerce.Pos/OperatorPinCredentialTests.cs`, `LocalOperatorStoreTests.cs`, `CurrentOperatorTests.cs` | Create | Unit coverage. |
| `tests/Commerce.Integration/OperatorProvisioningTests.cs` | Create | Endpoint coverage against live Postgres. |

## Interfaces / Contracts

```csharp
// Commerce.Pos.Windows
public sealed record CachedOperator(
    Guid UserId, string Email, Guid OrganizationId,
    byte[] Salt, byte[] Subkey, DateTimeOffset LastVerifiedUtc)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromDays(14);
    public bool IsStale(DateTimeOffset now) => now - LastVerifiedUtc > Ttl;
}

public static class OperatorPinCredential
{
    public const int PinLength = 6;            // numeric, no trivial runs
    private const int Iterations = 210_000;    // PBKDF2-HMAC-SHA256, 16B salt, 32B subkey
    public static bool IsValidPin(string pin);
    public static (byte[] Salt, byte[] Subkey) Derive(string pin);
    public static bool Verify(string pin, byte[] salt, byte[] subkey);   // FixedTimeEquals
}

public sealed class CurrentOperator
{
    public CachedOperator? Value { get; private set; }
    public void Set(CachedOperator op);
    public void Clear();
    public Guid ResolveActorId(Guid installationIdFallback) => Value?.UserId ?? installationIdFallback;
}
```

```csharp
// Commerce.Cloud.Api/Endpoints/Device.cs  — both .RequireAuthorization("DeviceBearer")
public sealed record OperatorVerifyRequest(string Email, string Password);
// status: "verified" (200) | "branch-not-in-scope" (403); every credential failure is a bare 401.
public sealed record OperatorVerifyResponse(string Status, Guid? UserId, string? Email, Guid? OrganizationId);
// status: "active" | "inactive"  (200 in both cases — "inactive" is an answer, not an error)
public sealed record OperatorStatusResponse(string Status);
```

## Testing Strategy

| Layer | What to test | Approach |
|---|---|---|
| Unit | `OperatorPinCredential`: correct PIN verifies; wrong PIN fails; two derivations of the same PIN differ (random salt) yet both verify; `IsValidPin` rejects 5/7 digits, non-digits, `111111`, `123456`, `654321` | xUnit, no I/O |
| Unit | `LocalOperatorStore`: round-trip; multiple operators coexist; upsert by `UserId` replaces in place; remove; **tampered ciphertext ⇒ that entry is silently absent, no throw**; truncated/garbage JSON ⇒ empty list, no throw; a missing file ⇒ empty list | xUnit, temp directory |
| Unit | `CachedOperator.IsStale` at 13 d / 14 d / 15 d; `CurrentOperator.ResolveActorId` returns the fallback when unset and after `Clear()` | xUnit |
| Integration | `POST /device/operators/verify`: valid credentials ⇒ 200 + the operator's real user id; wrong password, unknown email, and revoked user all ⇒ an **identical bare 401**; an operator not scoped to the terminal's branch ⇒ 403 `branch-not-in-scope`; **no `Set-Cookie` header on any response**; no bearer / revoked device token ⇒ 401 | `WebApplicationFactory` + live Postgres |
| Integration | `GET /device/operators/{userId}/status`: active ⇒ `"active"`; revoked user ⇒ `"inactive"`; a user id from another organization ⇒ `"inactive"` (never a leak of existence) | same, two seeded orgs |
| Manual (WPF) | First run provisions online; the network is then disconnected and the same operator logs in by PIN alone; a second operator provisions and both appear in the picker; "Continue without operator" reaches `MainWindow` and the sale commits | scripted walkthrough in the change's tasks |

## Threat Matrix

| Native row | Applicability |
|---|---|
| Routing | **Applicable** — two new routes. Safe behavior: both sit under the existing `/device` group with `.RequireAuthorization("DeviceBearer")`, so org and branch are read from the stored `device_credentials` row and never from the request body; the verify route reuses `/device/pair`'s exact verification path with dummy-hash timing parity and one generic 401 for every credential failure; it calls no `SignInAsync`, so no cookie or session is created; the status route is a read-only GET that answers `"inactive"` (never 404) for a foreign-org user id, so it cannot be used to probe account existence across tenants. RED tests: identical-401 matrix, branch-not-in-scope 403, absent `Set-Cookie`, cross-org `"inactive"`, missing/revoked device token 401. |
| Process integration | **Applicable** — a new local secret class (`operators.json`). Safe behavior: DPAPI `CurrentUser`, verifier-only encryption, tolerant decrypt that degrades to "not cached"; the PIN and verifier never cross the wire; a 14-day TTL plus server-driven removal bound a revoked operator's offline life. RED tests: the tamper/corruption and staleness unit tests above. |
| Documentation-like paths | N/A — no file-classification boundary. |
| Git repository selection / Commit state / Push state / PR commands | N/A — no VCS automation. |
| Shell / subprocess | N/A — none introduced. |

## Migration / Rollout

No migration. No schema change, no new table, no new column — the two routes are
pure reads over `users`/`user_directory`/`device_credentials` through the
existing stores and RLS policies. Deploy order is unconstrained: the server
routes are inert until a client calls them, and an old POS build never does. On
an already-paired terminal the first launch after the update shows the login
window with an empty picker and the provisioning panel revealed; an operator who
skips it gets exactly today's installation-id attribution.

Rollback is the proposal's: revert the commit. `operators.json` is left behind
and is inert.

## Work Units

| Unit | Scope | Budget | Test / runtime boundary | Rollback |
|---|---|---|---|---|
| 1 | `OperatorPinCredential` + `CachedOperator` + `LocalOperatorStore` + `CurrentOperator` + unit tests | ~230 | Pure/local; nothing calls them yet | Revert; dead files |
| 2 | Two `Device.cs` routes + `OperatorProvisioningClient` + integration tests | ~220 | Routes green under RLS; no client UI yet | Revert; routes unused |
| 3 | `OperatorLoginWindow` + `App.xaml.cs` + `MainWindow` (`actorId`, switch button, reconciliation) + `PosHostBuilder` | ~300 | Manual WPF walkthrough; `dotnet test Commerce.sln` | Revert; `actorId` returns to `_installationId` |

Decision needed before apply: Yes
Chained PRs recommended: Yes
400-line budget risk: Medium

**Why chained**: ~750 authored lines, under twice the budget but over it, and the
slices are cleanly ordered by dependency with no half-open security state — unit
1 is pure local code nothing calls, unit 2 is server surface no shipped client
reaches, and only unit 3 changes observable POS behavior. Feature Branch Chain:
PR #1 targets the feature branch, #2 targets #1, #3 targets #2. If the
orchestrator's delivery strategy resolves to `single-pr`, units 1–3 are still a
coherent single review at ~750 lines and the risk is Medium, not High.

## Open Questions

- [ ] None blocking. Three accepted assumptions: a terminal whose device
      credential is revoked cannot provision a *new* operator until it re-pairs
      (correct — provisioning is an onboarding operation, and the already-cached
      operators keep working offline, so no sale is blocked); `actorId` now
      carries two kinds of GUID (user id or installation id) with no
      discriminator column in `branch.db`, which is a recorded future seam, not a
      gap this change closes since `Commerce.BranchNode` is deliberately
      unchanged; and PIN entry is keyboard-based, since no touch/on-screen keypad
      exists anywhere in the current WPF shell.

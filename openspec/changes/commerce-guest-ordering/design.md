# Design: Commerce Guest Ordering

## Technical Approach

The proposal's six decisions are locked and are not re-opened here. This design
records **three verified codebase facts** that shape the implementation, then
maps the work onto the repo's existing idioms.

**Verified 1 — there is no second auth scheme for customers, but the precedent
exists.** `Program.cs:121-166` registers three schemes: the staff cookie
(`CookieAuthenticationDefaults.AuthenticationScheme`), `DeviceBearer`, and
`PlatformAdminCookie`. The platform cookie already demonstrates the exact shape
this change needs: its **own `Cookie.Name` and `Cookie.Path = "/platform"`**, so
it is not even *sent* to other paths, plus an authorization policy that **names
only that scheme**, so the staff cookie authenticates nothing under `/platform`.
The customer scheme is that pattern applied a second time — not a new idiom.

**Verified 2 — email delivery is already shipped and reusable.**
`IEmailSender`/`EmailMessage` (`Email/IEmailSender.cs`) with `ResendEmailSender`
and a `LogOnlyEmailSender` fallback when `RESEND_API_KEY` is absent, plus the
hash-stored / expiring / single-use / superseding token idiom in
`PostgresPasswordRecoveryStore.IssueTokenAsync`, plus the two-key-space
fixed-window `ResetRequestThrottle`. **Email is therefore the verification
channel** — SMS/WhatsApp would add a provider, a secret, a cost centre and a
package reference for the same security property. The channel is stored as a
column value (`contact_channel`), so adding SMS later is a new enum value and a
new sender, not a schema change.

**Verified 3 — there is no principal/default branch column.**
`0003_organizations_branches.sql` has no `is_principal`/`is_default` on
`branches`. The hardcoded target (Decision 3) therefore cannot be a query; it is
**configuration read at startup in exactly one place** (`GuestOrderTarget`), and
when it is absent the public endpoint group is **not mapped at all** — no guest
surface rather than a guest surface pointed at a guessed branch.

Everything else follows the proposal's Approach: domain first, surface last, and
`PricingResolutionService` is **not modified** — this change is its first
`discountPercentage: null` caller.

## Architecture Decisions

| Decision | Choice and rationale | Rejected alternative |
|---|---|---|
| **Guest verification channel** | **Email, via the existing `IEmailSender`.** Zero new packages, zero new secrets, zero new provider onboarding; `ResendEmailSender`/`LogOnlyEmailSender` already make local dev and CI work without an account. `contact_channel text CHECK (contact_channel IN ('Email'))` keeps the column shape ready for `'Sms'`/`'WhatsApp'` — a later channel widens the CHECK and adds a sender, and touches nothing else. | **SMS/WhatsApp first** — a Twilio/Meta account, a new secret, per-message cost, and a new package, for the same anti-abuse property email already delivers today. **A captcha** — explicitly not what Decision 2 asks for, and it proves nothing about the identity the branch must act on. |
| **Verification state shape** | **A persisted `guest_order_verifications` row, mirroring `password_reset_tokens` idiom for idiom**: 6-digit code stored only as a SHA-256 hex hash, `expires_at` (10 min), `confirmed_at`, `consumed_at`, `attempt_count`, and supersede-prior-unconsumed-rows in the same transaction as issuance. Brute force is bounded three ways at once: 10⁶ code space, **5 failed attempts burns the row**, and a 10-minute window. A confirmed row is a single-use **ticket** (30-minute confirm→submit TTL) consumed immediately before `CloudOrderStore.Submit`, so one confirmation admits exactly one order. | **In-memory verification state** — would silently drop every in-flight guest on a Railway redeploy, and unlike `CloudOrderStore` (already in-memory, already a stated scope boundary) this is a *security* control. **Storing the code in plaintext** — a DB read would hand an attacker every live code. **Consuming the ticket at confirm time** — the guest would have to re-verify after a `no-effective-price` denial that was never their fault. |
| **Customer session** | **A fourth scheme `CustomerCookie`**: `Cookie.Name = "commerce.customer"`, `Cookie.Path = "/customer"`, API-only `OnRedirectToLogin/AccessDenied` → 401/403, and a `"Customer"` policy naming **only** that scheme. Staff groups keep the default scheme, so a customer cookie is not sent to `/orders`, `/catalog`, `/customers`, `/pricing` at all, and would not authenticate there even if replayed. `UserAccount.EffectivePermissions ⇒ Permission.None` for a `CustomerId`-bearing account stays as defence in depth, not as the primary barrier. `/customer/sign-in` **rejects an account whose `customer_id IS NULL`** with the same generic 401 as a bad password — a staff user cannot obtain a customer cookie either, so the exclusion is symmetric. | **Reusing the staff cookie with a claim** — one forgotten claim check anywhere becomes privilege escalation; the proposal explicitly rejects it. **A customer-only authorization policy over the same cookie** — same cookie material, so the isolation is a convention rather than a transport property. |
| **Org/branch resolution point** | **One `sealed record GuestOrderTarget(Guid OrganizationId, Guid DestinationBranchId)`**, built once from `GuestOrdering:OrganizationId` / `GuestOrdering:BranchId` (env `GuestOrdering__*`), registered as a singleton, and injected everywhere the public surface needs a scope. **Missing or unparseable config ⇒ `MapPublicOrderingEndpoints` is never called** — the public surface does not exist rather than existing with a wrong target. Multi-org later replaces this single type with a request-derived resolver; no call site changes shape. | **Hardcoded `Guid` literals in the endpoints** — a second organization would mean grepping every call site, which Decision 3 explicitly forbids. **Deriving org from a subdomain/path now** — routing infrastructure the current single-tenant deployment does not need. |
| **Domain shape for origin** | **`OrderOrigin { Guest, RegisteredCustomer }` required; `CustomerId` becomes `Guid?`; `GuestContact?` value object on the order.** The paired invariant lives in the `Order` constructor, so *both* inconsistent states (registered-without-customer, guest-with-customer, and guest-without-contact) are unconstructable — the guard is not a validation the endpoint could forget. Guest identity is `GuestContact(DocumentId, ContactChannel, ContactAddress, DisplayName, DeliveryNotes)` captured on the order itself; no `Customer` row is created. | **A synthetic per-guest `Customer` row** — rejected in the proposal: pollutes the registry, contradicts admin-provisioned-only, and makes guest orders invisible to every existing query. **A nullable `OrderOrigin` with a default** — pre-existing rows would read as registered by accident, which is exactly the commingling ADR-009 forbids. |
| **Non-priority = ranking, never a gate** | **`Order.DispatchRank => Origin == RegisteredCustomer ? 0 : 1`**, a derived read-only property used only as a **sort key** when listing pending orders. `AttemptDelivery` is untouched: a guest order goes through the identical `SyncEnvelope` → `ApplyInbound` path, reaches `DestinationConfirmed` under the same conditions, and needs no staff acceptance. | **A `RequiresStaffAcceptance` flag or a delivery guard** — Decision 5 explicitly rejects it; it would turn verification-passed guests into a manual queue. |
| **Non-staff actor** | **`OrderActors.PublicGuest`, a named non-empty sentinel `Guid`** in `Commerce.Domain/Ordering`, used as the `SyncEnvelope.ActorId` for guest orders. `Guid.Empty` is deliberately avoided: it reads as "unknown/missing staff user" everywhere else in this repo. Audit consumers distinguish by `Order.Origin`; the sentinel exists so the envelope's non-nullable `ActorId` is honest rather than blank. | **`Guid.Empty`** — indistinguishable from a bug. **Making `SyncEnvelope.ActorId` nullable** — a shared sync contract changed for one caller, rippling into BranchNode and the POS. |
| **Guest submission path** | **A separate `SubmitGuestAsync` on `CloudOrderSubmissionService`**, not nullable parameters bolted onto `SubmitAsync`. The registered path keeps its four denial checks verbatim and in order; the guest path is structurally incapable of reaching them and vice versa. The line-pricing loop (resolve → `NoEffectivePrice` denies the whole order → catalog lookup → snapshot) is extracted to one private `ResolveLinesAsync(scope, lines, discountPercentage, ct)` used by both, so the money rule is written **once** and the only difference between the paths is the argument `null` vs `customer.DiscountPercentage`. | **One method with `Guid? customerId`/`Guid? credential`** — every existing check becomes an `if (customerId is not null)`, and the guest path's freedom from the credential check becomes a conditional a future edit can invert. |
| **Rate limiting** | **Built-in `Microsoft.AspNetCore.RateLimiting`** (shared framework, no `PackageReference`), four named fixed-window policies attached **only** to the public group, partitioned by remote IP, rejecting with **429 + `Retry-After`**. Sized for a two-branch butcher shop (tens of orders/day): verification request **5 / 15 min**, confirm **10 / 15 min**, guest submit **10 / hour**, public catalog read **60 / min**. Every ceiling is ~50–100× realistic per-person traffic and still bounds a burst. A second, identity-keyed `GuestVerificationThrottle` (the `ResetRequestThrottle` shape: two key spaces, contact address and IP, 10 000-entry cap with oldest eviction) stops one address being mailed repeatedly from rotating IPs. **No staff, customer or device endpoint carries a limiter policy**, so an abusive public burst provably cannot degrade them. | **A global limiter** — would couple staff throughput to public abuse, violating a success criterion. **A distributed limiter (Redis)** — new infrastructure for a single Railway replica; the in-memory assumption is the one `BootstrapTokenRegistry` and `ResetRequestThrottle` already document and accept. **Relying on rate limiting alone** — the verification gate, not the limiter, is the anti-abuse control; the limiter protects the *verification* endpoint. |
| **One screen, guest and registered as peers** | **`OrderScreen.tsx` keeps a single route** and renders a two-option segmented control (`Order as guest` / `Sign in to order`), guest listed **first**, with no "recommended", no benefits pitch, no interstitial, and no dismissible login banner. Line entry becomes a shared `OrderLinesEditor` fed by the **public catalog read** (a presentation picker) instead of hand-typed GUID fields — the raw-ID form is unusable by a member of the public and is the last thing the proposal's "no raw ID text fields for the public path" requires removed. | **Two screens or a modal-gated guest path** — ADR-009 locked. **Keeping the GUID text fields for guests** — a public surface no non-employee can operate is not a public surface. |

## Data Flow

```text
Guest order (the platform's first public-reachable path)
  POST /public/guest-orders/verification  {documentId, email}   [AllowAnonymous]
    -> rate limit: 5 / 15 min per IP        -> 429 + Retry-After
    -> GuestVerificationThrottle (email + IP key spaces)  -> same 202, no mail
    -> code = RandomNumberGenerator.GetInt32(0, 1_000_000) formatted "D6"
    -> ONE tx: INSERT guest_order_verifications (code_hash=SHA256(code),
                 expires_at=now+10m) + supersede prior unconsumed rows
                 + opportunistic purge   [password_reset_tokens idiom]
    -> IEmailSender.SendAsync(...)   -> ALWAYS 202 {verificationId}
  POST /public/guest-orders/verification/confirm  {verificationId, code}
    -> rate limit: 10 / 15 min per IP
    -> row missing | expired | consumed | attempt_count >= 5  -> SAME 401
    -> mismatch -> attempt_count++ (5th burns the row) -> 401
    -> match    -> confirmed_at = now                     -> 204
  POST /public/guest-orders {verificationId, documentId, contact, lines[]}
    -> rate limit: 10 / hour per IP
    -> scope/branch from GuestOrderTarget      <- the ONE resolution point
    -> verification must be confirmed, unconsumed, confirmed_at < 30m
         and its documentId/contact must MATCH the submitted ones -> else 401
    -> CloudOrderSubmissionService.SubmitGuestAsync
         -> NO CustomerOrderingAccess check, NO Customer read (there is none)
         -> ResolveLinesAsync(..., discountPercentage: null)   <- GUEST = list price
              NoEffectivePrice on ANY line -> Denied "no-effective-price",
              verification NOT consumed (the guest is not punished for it)
         -> consume verification (single-use)   <- immediately before Submit
         -> CloudOrderStore.Submit(origin: Guest, customerId: null,
                                   guestContact, actorId: OrderActors.PublicGuest)
              -> new Order(...)  <- paired invariant enforced HERE
              -> AttemptDelivery: IDENTICAL to a registered order (Decision 5)

Registered customer order (new session, same submission rules)
  POST /customer/sign-in {email, password}        [AllowAnonymous]
    -> users row must have customer_id NOT NULL  -> else the SAME generic 401
    -> cookie "commerce.customer", Path=/customer, scheme CustomerCookie
  POST /customer/orders {lines[]}                 [policy "Customer"]
    -> customerId from the cookie's claim, NEVER from the body
    -> CloudOrderSubmissionService.SubmitAsync(..., credential bypass path:
         the 4 checks minus the credential check, customer read unchanged)
    -> ResolveLinesAsync(..., customer.DiscountPercentage)   <- REGISTERED
  Existing staff-operated POST /orders with AccessCredential: UNCHANGED
    (Decision 1: CustomerOrderingAccess coexists, spec unamended)

Endpoint-group split (structural, not conventional)
  default cookie  -> /orders /catalog /customers /pricing /account/users   [staff]
  CustomerCookie  -> /customer/*            Path=/customer, own Cookie.Name
  DeviceBearer    -> /device/*                          [unchanged]
  PlatformAdmin   -> /platform/*                        [unchanged]
  AllowAnonymous  -> /public/*              rate-limited, GuestOrderTarget scope
  A customer cookie is never SENT to a staff path, and names a scheme no staff
  policy accepts: two independent reasons it cannot reach staff endpoints.
```

## File Changes

| Path | Action | Purpose |
|---|---|---|
| `src/Commerce.Domain/Ordering/OrderOrigin.cs` | Create | `Guest \| RegisteredCustomer`. |
| `src/Commerce.Domain/Ordering/GuestContact.cs` | Create | `record GuestContact(string DocumentId, GuestContactChannel Channel, string ContactAddress, string DisplayName, string? DeliveryNotes)` + blank-field guards. |
| `src/Commerce.Domain/Ordering/OrderActors.cs` | Create | `PublicGuest` sentinel `Guid` (non-empty, documented). |
| `src/Commerce.Domain/Ordering/Order.cs` | **Modify** | `CustomerId → Guid?`; required `Origin`; `GuestContact?`; `DispatchRank`; the non-empty guard replaced by the paired invariant. |
| `deploy/db/migrations/0010_guest_ordering.sql` | Create | `guest_order_verifications` + RLS/policies/grants mirroring `password_reset_tokens`; inverse block as comments. |
| `deploy/dev/db/init-rls.sql` | Modify | Same DDL appended (the parity convention `MigrationRlsTests` asserts). |
| `deploy/README.md`, `deploy/staging-runbook.md` | Modify | `0010` apply + inverse + the two new `GuestOrdering__*` env vars. |
| `src/Commerce.Cloud.Api/Persistence/PostgresGuestVerificationStore.cs` | Create | Issue / find / record-attempt / confirm / consume; `PostgresPasswordRecoveryStore`'s idiom verbatim. |
| `src/Commerce.Cloud.Api/Ordering/GuestVerificationService.cs` | Create | Code generation, hashing, expiry/attempt policy, email composition through `IEmailSender`. |
| `src/Commerce.Cloud.Api/Authentication/GuestVerificationThrottle.cs` | Create | `ResetRequestThrottle` shape: contact-address + IP key spaces, capped, evicting. |
| `src/Commerce.Cloud.Api/Authentication/CloudAuthenticationSchemes.cs` | Modify | `CustomerCookie` constant. |
| `src/Commerce.Cloud.Api/Tenancy/GuestOrderTarget.cs` | Create | The single org/branch resolution point + `TryFromConfiguration`. |
| `src/Commerce.Cloud.Api/Tenancy/PublicScopeEndpointFilter.cs` | Create | Stamps `CloudTenantScope` from `GuestOrderTarget` for claim-less requests (`TenantScopeEndpointFilter` requires a principal). |
| `src/Commerce.Cloud.Api/Endpoints/PublicOrdering.cs` | Create | `/public/catalog/presentations`, `/public/guest-orders/verification`, `.../confirm`, `POST /public/guest-orders`. |
| `src/Commerce.Cloud.Api/Endpoints/CustomerSession.cs` | Create | `/customer/sign-in`, `/customer/sign-out`, `/customer/me`, `/customer/catalog`, `POST /customer/orders`. |
| `src/Commerce.Cloud.Api/Ordering/CloudOrderSubmissionService.cs` | **Modify** | `SubmitGuestAsync`; `SubmitForCustomerSessionAsync`; shared private `ResolveLinesAsync`; existing `SubmitAsync` behaviour unchanged. |
| `src/Commerce.Cloud.Api/Ordering/CloudOrderStore.cs` | Modify | `Submit` takes `OrderOrigin origin, Guid? customerId, GuestContact? guestContact`; `AttemptDelivery` untouched. |
| `src/Commerce.Cloud.Api/Endpoints/Ordering.cs` | Modify | Pass `OrderOrigin.RegisteredCustomer`; pending-list reads ordered by `DispatchRank` then `SubmittedAtUtc`. |
| `src/Commerce.Cloud.Api/Program.cs` | **Modify** | `CustomerCookie` scheme + `"Customer"` policy; `AddRateLimiter` with four policies + `UseRateLimiter()`; `GuestOrderTarget` singleton; new store/service/throttle registrations; `MapCustomerEndpoints()`; `MapPublicOrderingEndpoints()` **only when the target is configured**. |
| `src/Commerce.Cloud.Api/HealthChecks/PostgresReadinessHealthCheck.cs` | Modify | `guest_order_verifications` (+ `relforcerowsecurity` + policy name). |
| `src/Commerce.Web/src/screens/OrderScreen.tsx` | **Modify (rework)** | One screen, guest/registered peer branch, catalog-picker lines, verification step. |
| `src/Commerce.Web/src/components/OrderLinesEditor.tsx` | Create | Shared presentation picker + quantity rows for both branches. |
| `src/Commerce.Web/src/api/publicOrdering.ts`, `customerSession.ts`, `types.ts` | Create/Modify | Public + customer clients; `OrderOrigin`, `GuestContact` DTOs. |
| `tests/Commerce.Cloud/OrderOriginTests.cs`, `GuestVerificationTests.cs`, `GuestVerificationThrottleTests.cs` | Create | Invariant, code lifecycle, throttle windows. |
| `tests/Commerce.Integration/GuestOrderingTests.cs`, `CustomerSessionIsolationTests.cs`, `PublicRateLimitTests.cs`, `MigrationRlsTests.cs` | Create/Modify | End-to-end guest flow, the ADR-010 divergence through the submission path, the negative auth matrix, 429 behaviour, RLS. |
| `src/Commerce.Web/e2e/ordering.spec.ts` | Modify | Guest path E2E (code read through the dev `LogOnlyEmailSender`/seed hook). |

## Interfaces / Contracts

```sql
-- 0010_guest_ordering.sql — RLS/grants mirror `password_reset_tokens` (0005)
-- exactly, including the NULLIF(current_setting('app.current_org_id',true),'')::uuid
-- pooler hardening. GRANT SELECT, INSERT, UPDATE to app_runtime; NO DELETE.
CREATE TABLE IF NOT EXISTS guest_order_verifications (
    id               uuid PRIMARY KEY,
    organization_id  uuid NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    document_id      text NOT NULL,
    contact_channel  text NOT NULL CHECK (contact_channel IN ('Email')), -- widened, not reshaped, for SMS later
    contact_address  text NOT NULL,
    code_hash        text NOT NULL,          -- SHA-256 hex. The code itself is never stored.
    attempt_count    integer NOT NULL DEFAULT 0 CHECK (attempt_count <= 5),
    requested_at     timestamptz NOT NULL DEFAULT now(),
    expires_at       timestamptz NOT NULL,   -- issued + 10 minutes
    confirmed_at     timestamptz NULL,       -- ticket becomes usable
    consumed_at      timestamptz NULL,       -- exactly ONE order per confirmation
    consumed_order_id uuid NULL              -- audit trail for the admitted order
);
CREATE INDEX IF NOT EXISTS guest_order_verifications_contact_idx
    ON guest_order_verifications (organization_id, contact_address, requested_at DESC);
```

```csharp
// Commerce.Domain/Ordering — BOTH inconsistent states are unconstructable.
public enum OrderOrigin { Guest, RegisteredCustomer }

public Order(
    Guid orderId, Guid organizationId, OrderOrigin origin, Guid? customerId,
    GuestContact? guestContact, Guid destinationBranchId,
    IReadOnlyList<OrderLineSnapshot> lines, DateTimeOffset submittedAtUtc)
{
    // Replaces the old `customerId == Guid.Empty` guard (ADR-009's stated blocker).
    if (origin == OrderOrigin.RegisteredCustomer &&
        (customerId is null || customerId == Guid.Empty || guestContact is not null))
        throw new ArgumentException("A registered order requires a CustomerId and carries no GuestContact.");
    if (origin == OrderOrigin.Guest && (customerId is not null || guestContact is null))
        throw new ArgumentException("A guest order carries a GuestContact and no CustomerId.");
    ...
}

// ADR-009 "non-priority" == a SORT KEY. Never consulted by AttemptDelivery.
public int DispatchRank => Origin == OrderOrigin.RegisteredCustomer ? 0 : 1;

// Commerce.Cloud.Api/Tenancy — the ONE org/branch resolution point (Decision 3).
// Multi-org later replaces this type; no call site changes shape.
public sealed record GuestOrderTarget(Guid OrganizationId, Guid DestinationBranchId)
{
    public CloudTenantScope Scope => new(OrganizationId);
    public static bool TryFromConfiguration(IConfiguration config, out GuestOrderTarget? target);
}

// Public DTOs — a guest cannot address another org or branch: neither field exists.
public sealed record GuestVerificationRequest(string DocumentId, string Email);
public sealed record GuestVerificationConfirmRequest(Guid VerificationId, string Code);
public sealed record SubmitGuestOrderRequest(
    Guid OrderId, Guid VerificationId, string DocumentId, string Email,
    string DisplayName, string? DeliveryNotes,
    IReadOnlyList<SubmitOrderLine> Lines, Guid CorrelationId);
```

```csharp
// Program.cs — the fourth scheme, the platform-cookie pattern applied again.
.AddCookie(CloudAuthenticationSchemes.CustomerCookie, options =>
{
    options.Cookie.Name = "commerce.customer";   // distinct material
    options.Cookie.Path = "/customer";           // never SENT to a staff path
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Customer", p => p
        .AddAuthenticationSchemes(CloudAuthenticationSchemes.CustomerCookie)  // ONLY this one
        .RequireAuthenticatedUser());

// Four fixed windows, public group only. Staff/customer/device groups carry no
// policy, so an abusive public burst cannot degrade them.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (ctx, _) => { ctx.HttpContext.Response.Headers.RetryAfter = "..."; ... };
    // guest-verification-request 5/15m · guest-verification-confirm 10/15m
    // guest-order-submit 10/1h     · public-catalog-read 60/1m   (per remote IP)
});
```

## Testing Strategy

| Layer | What to test | Approach |
|---|---|---|
| Unit | Both inconsistent `Order` states throw (registered-without-customer, guest-with-customer, guest-without-contact); a valid guest and a valid registered order construct | xUnit |
| Unit | `DispatchRank` is 1 for guest / 0 for registered, and is read by **no** delivery code path (`AttemptDelivery` reaches `DestinationConfirmed` for a guest order under identical conditions) | xUnit |
| Unit | Verification lifecycle: wrong code increments attempts; the 5th burns the row; expired/consumed/unknown all return the **same** failure; the code is never persisted in plaintext | xUnit + live Postgres |
| Unit | `GuestVerificationThrottle`: per-address and per-IP windows, cap eviction | xUnit with an injected clock |
| Unit | `GuestOrderTarget.TryFromConfiguration` fails on missing/unparseable config | xUnit |
| Integration | **ADR-010 divergence end-to-end**: the same presentation submitted through `POST /public/guest-orders` and through a discounted customer's path yields a strictly lower price for the customer — asserted on the two submitted orders, not on `PricingResolutionService` | `WebApplicationFactory` + live Postgres |
| Integration | Full guest flow: request → confirm → submit ⇒ `Origin = Guest`, `CustomerId = null`, `GuestContact` persisted, `ActorId = OrderActors.PublicGuest` | same |
| Integration | A submit with an unconfirmed / expired / already-consumed / mismatched-contact verification is denied and **no order is stored**; a `no-effective-price` denial leaves the verification **unconsumed** | same |
| Integration (negative auth) | A customer cookie ⇒ 401/403 on `/orders`, `/catalog`, `/customers`, `/pricing`, `/account/users`, `/platform/*`; a staff cookie ⇒ 401 on `/customer/*`; `/customer/sign-in` with a staff account ⇒ the same generic 401; no `/register`-shaped route exists anywhere in the mapped endpoint list | same (route-table assertion included) |
| Integration (rate limit) | An abusive burst on each public route ⇒ 429 + `Retry-After`; a concurrent staff and customer request during that burst ⇒ unaffected | same |
| Integration (config) | With `GuestOrdering__*` absent, every `/public/*` route is **404** (never mapped) and the app still starts | same |
| Integration (RLS/migration) | `0010` applies twice cleanly; org B cannot read org A's verifications; `app_runtime` has no `DELETE`; `/health/ready` fails before and passes after | `MigrationRlsTests` |
| Web (Vitest) | One screen renders both peer options with guest first and no login-pressure copy; the guest branch shows document/email/code steps; lines come from the catalog picker, and **no raw GUID input exists** on the guest path | Vitest + Testing Library |
| Web (E2E) | Guest submits an order end to end and sees the server-resolved total | Playwright + the dev seed/log email hook |

## Threat Matrix

| Native row | Applicability |
|---|---|
| **Routing** | **Applicable** — this change adds the platform's first anonymous route group (`/public/*`) and a new authenticated group (`/customer/*`). Safe behavior: `/public/*` reaches only the guest verification and guest submit paths, derives its scope solely from `GuestOrderTarget` (no org/branch field exists on any public DTO), carries a rate-limiter policy on every route, and is **not mapped at all** without configuration. `/customer/*` requires the `"Customer"` policy naming only `CustomerCookie`; the cookie's `Path=/customer` means it is never transmitted to a staff route. Staff, device and platform groups are byte-identical to today. RED tests: the negative-auth matrix above; `/public/*` ⇒ 404 without config; a guest DTO has no org/branch member to populate; no self-registration route is present in the mapped route table. |
| **Process integration** | **Applicable** — outbound verification email through the existing `IEmailSender`/Resend HTTP client. Safe behavior: the recipient address is a stored column value, never interpolated into a header; a send failure is logged and still returns the uniform 202 (the `reset-password/request` precedent), so delivery status is not an oracle; with `RESEND_API_KEY` absent, `LogOnlyEmailSender` keeps CI and dev off the network. RED tests: a failing sender still returns 202 and still issues the row; no test reaches the network. |
| Executable-file classification | N/A — no file upload, download, or classification boundary in this change. |
| Shell / subprocess | N/A — none introduced. |
| Git repository selection / Commit state / Push state / PR commands | N/A — no product code runs Git or PR automation. |
| Documentation-like paths | N/A — no file-classification boundary. |

## Migration / Rollout

Forward-only, following `0009`'s convention. **Before** deploying the new image,
apply `0010_guest_ordering.sql` through the direct (non-pooled) connection;
`/health/ready` fails closed until `guest_order_verifications` exists with
`FORCE ROW LEVEL SECURITY` and its policy, so ordering is enforced by the gate,
not by discipline.

**No data migration.** The table is new. `Order` is in-memory
(`CloudOrderStore`), so the `CustomerId → Guid?` change has **nothing at rest to
convert** — the proposal's stated scope boundary is also what makes this change
cheap to deploy and cheap to revert *today*.

**Two new environment variables gate the public surface**:
`GuestOrdering__OrganizationId` and `GuestOrdering__BranchId` (Vaca Verde and its
principal branch). Deploy the image with them **unset** first: the app runs
normally, `/public/*` returns 404, and nothing about staff or registered
behaviour changes. Set them when the guest surface is ready to announce. That is
also the **narrowest rollback**: unset the two variables and the entire public
surface disappears within one restart, with the domain origin field and the
customer session left intact.

Full rollback: revert the commit and run `0010`'s inverse block. Lossy only for
in-flight verifications (minutes of state, re-requestable), because no order data
is persisted anywhere today.

## Review Workload Forecast

Decision needed before apply: No
Chained PRs recommended: Yes
400-line budget risk: High

**~2 700–3 000 authored lines against this session's 2 000-line budget
(`delivery_strategy = auto-chain`) — roughly 1.4×, so it exceeds it and is
flagged as instructed.** Under `auto-chain` this resolves to a chain without
further input. Six dependency-ordered units:

| Unit | Scope | Budget | Boundary | Rollback |
|---|---|---|---|---|
| 1 | **Domain origin**: `OrderOrigin`, `GuestContact`, `OrderActors`, the `Order` invariant rework, `CloudOrderStore.Submit` signature, `Ordering.cs` call site, unit tests | ~200 | Both inconsistent states throw; existing order tests still green | Revert; nothing consumes the new field |
| 2 | **Verification substrate**: `0010` + `init-rls` parity + readiness + `MigrationRlsTests`; `PostgresGuestVerificationStore`; `GuestVerificationService`; `GuestVerificationThrottle`; unit tests | ~600 | Codes hashed, expiring, single-use, attempt-burned; RLS proven | Revert; run the inverse block |
| 3 | **Customer session**: `CustomerCookie` scheme + `"Customer"` policy, `CustomerSession.cs`, `Program.cs` wiring, the full negative-auth matrix | ~380 | A customer cookie reaches no staff endpoint and vice versa | Revert; staff auth untouched throughout |
| 4 | **Guest submission branch**: `GuestOrderTarget`, `PublicScopeEndpointFilter`, `SubmitGuestAsync` + extracted `ResolveLinesAsync`, the ADR-010 divergence test through the submission path | ~430 | Guest gets list price; registered gets a strictly lower one; guest path never touches `Customer` | Revert; registered path is byte-identical |
| 5 | **Public surface**: `PublicOrdering.cs` (catalog read, verification request/confirm, guest submit), `AddRateLimiter` + the four policies, config-gated mapping, 429 and config tests | ~520 | Abusive burst ⇒ 429 without touching staff traffic; unset config ⇒ 404 | Unset the two env vars |
| 6 | **Web rework**: `OrderScreen.tsx` branch, `OrderLinesEditor`, `publicOrdering.ts`/`customerSession.ts`, Vitest + Playwright | ~600 | Guest and registered as peers; no raw GUID fields on the guest path | Revert the web slice only |

Units 1–2 are additive and inert until wired; Units 3–5 create the new
boundaries and hold essentially all of the change's risk; Unit 6 is the only one
that reworks a working screen. Units 1→2→3→4→5 are independently shippable as a
complete server-side guest surface (~2 130 lines) with Unit 6 following; the
public surface stays dark behind unset configuration until Unit 5 lands and the
variables are set, so no intermediate slice exposes a half-built public path.

## Open Questions

- [ ] Non-blocking, for `sdd-apply`: **the branch id must be supplied by the
      user at deploy time.** `branches` has no `is_principal`/`is_default`
      column, so "the principal branch" cannot be derived — it is a configured
      GUID. The absent-config-means-no-public-surface default makes this safe to
      defer to the deploy step rather than blocking design.
- [ ] Non-blocking: a guest's `document_id` is stored as free text with no
      Argentine DNI/CUIT format validation. Decision 4 asks for an identifier the
      branch can act on, not a validated one, and the verified contact channel is
      the actual gate. Format validation is an additive follow-up.
- [ ] Non-blocking: guest orders remain non-durable across restart
      (`CloudOrderStore` is in-memory) while *verifications* are durable — a
      restart can therefore leave a consumed verification whose order is gone.
      This is the already-accepted Phase B persistence gap surfacing, and it
      resolves when the `orders` table lands (the recommended next change).
- [ ] Non-blocking: rate-limit partitions and `GuestVerificationThrottle` are
      per-process, so they weaken if the API ever scales past one Railway
      replica — the same documented assumption `BootstrapTokenRegistry` and
      `ResetRequestThrottle` already carry.

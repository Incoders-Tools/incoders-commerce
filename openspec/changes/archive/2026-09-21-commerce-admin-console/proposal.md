# Proposal: Commerce Admin Console

## Intent

**There is no admin-facing UI at all, anywhere in the product.** Verified in code:

- `Commerce.Web` (`src/Commerce.Web/src/App.tsx`) has exactly four screens: Catalog, Orders/StaffOrder, RenewPassword, Customers. There is no Users screen, no Organizations screen, no Branches screen. `RequireAdmin` (gated on `Permission.ManageUsers`) wraps only the `customers` route today — it exists as a mechanism with nothing built behind it for identity/org administration.
- `Commerce.Web/src/context/AuthContext.tsx`'s `SignedInResponse` models only the org-scoped `/account/*` cookie session (`OrganizationId`, `UserId`, `DisplayName`, a `Permissions` bitmask). It has **zero client-side concept of a platform-admin session** — the two identities that exist server-side (see below) are invisible to each other in the web client.
- `Commerce.Pos.Windows` (`src/Commerce.Pos.Windows/MainWindow.xaml.cs`) opens per-purpose modal windows via `ShowDialog()` — `PairingWindow`, `OperatorLoginWindow`, `CustomersWindow`. `CustomersWindow` is already the "admin mode" precedent: its button visibility is gated client-side by `_currentOperator.Value.HasFlag(Permission.ManageUsers)` (~lines 118-121), and it opens with a fresh per-window `CustomerAdminClient` whose cookie is scoped to that window and discarded on close. There is no Users/Roles window at all, and the POS device is always pre-paired to exactly one org+branch (`_pairing.OrganizationId`/`BranchId`) — grepping `Commerce.Pos.Windows/**/*.cs` for `"Platform"` returns nothing; it has no plumbing for cross-org operations.

Meanwhile the server already carries real admin capability with no UI to drive it: `POST /platform/organizations` (create org + first branch + first business-admin), `GET /platform/organizations` (list all orgs), `POST /account/users` (business-admin creates staff), `PUT /account/users/{id}/roles` (full-replace role reassignment), `POST /account/users/{id}/reset-password`. Two read/write gaps sit directly in this surface's path: **no `GET /account/users`** (list staff in an org — needed before any UI can let an admin choose who to edit) and **no endpoint at all to create a branch in an existing organization** (`Permission.ManageBranchSettings` is defined and granted to `business-admin` in `RoleCatalog.cs`, but grepping `src/Commerce.Cloud.Api/Endpoints/**` shows nothing reads or checks it — it is a dead permission bit today).

**Why now.** Every organization currently depends on someone hand-driving these endpoints (curl/Postman) to onboard a business, add a branch, or manage staff. That is not sustainable operator posture once there is more than one organization, and it blocks the business-admin role from doing anything with the `ManageUsers`/`ManageBranchSettings` permissions it was already granted at the domain layer.

This change is **planning only** (`openspec/config.yaml` `approval_scope: planning-only`).

## Scope

### In Scope

1. **Branch management, server + UI.** A new endpoint (or endpoints — exact shape deferred to design) to create a branch within an existing organization, plus a list-branches read. Both the endpoint's authorization scope (business-admin within their own org vs. platform-admin across orgs — see Decision 1) and its UI surface are in scope.
2. **Staff/role management UI, in both `Commerce.Web` and `Commerce.Pos.Windows`.**
   - `Commerce.Web`: a net-new Users screen following the existing `CustomersScreen`/`RequireAdmin` pattern — list staff, create a user (`POST /account/users`), reassign roles (`PUT /account/users/{id}/roles`), force a password reset (`POST /account/users/{id}/reset-password`).
   - `Commerce.Pos.Windows`: a net-new modal window following the `CustomersWindow` precedent directly — `ManageUsers`-gated button visibility on `MainWindow`, a fresh per-window HTTP client, `ShowDialog()`.
   - Both UIs need `GET /account/users` (list), which does not exist yet and is therefore in scope as a prerequisite for either screen.
3. **Organization onboarding UI in `Commerce.Web`**, driving the already-built `POST /platform/organizations` (create) and `GET /platform/organizations` (list). This is where the identity-model question below becomes unavoidable: the user wants **one unified sign-in** where "the system detects who the logged-in user is and shows what it has to show accordingly... we're multitenant, need to handle privileges, a sysadmin should be able to see everything" — see Decision 1, not resolved by this proposal.

### Out of Scope (non-goals)

- **"Other future config"** — no other parametrization surface (pricing rules UI, catalog-wide settings, tenant feature flags, etc.) is named or implied. The user's own answer was explicit: nothing concrete exists to scope, so nothing is invented here. Named as a future, unscoped item, deliberately deferred rather than forgotten.
- **Provider/gateway configuration UI**, any settings from `commerce-payments` (planning-only, no provider selected yet) — that capability has not shipped an integration to configure.
- **Multi-branch device re-pairing or cross-branch POS operation.** `Commerce.Pos.Windows` stays pre-paired to one org+branch; this change adds an admin *window* inside that pairing, not a change to pairing itself.
- **A generic role/permission editor.** `RoleCatalog.cs`'s four roles (`business-admin`, `seller`, `provider`, `platform-admin`) and their bitmask are not opened for editing — staff UI assigns *existing* roles via the current full-replace `PUT .../roles` shape; it does not let an admin invent new roles or redefine permission bits.
- **Platform-admin bootstrap-token flow removal or replacement.** `POST /platform/bootstrap(-request-token)` (the one-time, log-only genesis path) is unaffected; `POST /platform/organizations` is already its documented successor per the `platform-administration` spec and stays as-is.
- **Audit-log UI.** `platform-administration`'s audit-record requirement is already satisfied server-side; surfacing it in an admin screen is not requested and not scoped here.

## Decisions

### Locked here

1. **Branch management and staff/role management UI are in scope for both server and client, per the user's direct answers (2026-09-20).**
2. **Staff/role UI reuses the existing precedents, not a new pattern**: `Commerce.Web` follows `CustomersScreen`/`RequireAdmin`; `Commerce.Pos.Windows` follows `CustomersWindow` (modal, `ManageUsers`-gated, fresh per-window client). No new UI architecture is introduced for this.
3. **"Other future config" is explicitly out of scope** — the user's own answer, not this proposal's inference.

### Requires explicit user confirmation before `sdd-design` starts (open question, this proposal's own round)

**The one open decision: does "one unified login that detects privileges and shows what it has to show" mean merging the platform-admin and org-scoped identity schemes, or keeping them separate and only smoothing the client UX?**

Taken literally, the user's request conflicts with a decision this repository already shipped and verified in code and spec (`openspec/specs/platform-administration/spec.md`, "Platform-Admin Scheme Isolation," and `openspec/changes/archive/2026-09-19-commerce-role-taxonomy/design.md`): platform-admin authentication is a **deliberately separate table** (`platform_admins`, no `organization_id` column), a **separate cookie scheme** (`PlatformAdminCookie`, no `org_id` claim), and a **separate password hasher** (`PasswordHasher<PlatformAdmin>`). The `/platform` route group carries no `TenantScopeEndpointFilter` by design — the spec states outright that "the default org cookie authenticates nothing here." This was a considered isolation boundary in an already-implemented, already-reviewed phase, not an oversight this change can quietly work around.

Two honest resolutions exist:

- **(a) Merge the schemes.** Extend the existing single-scheme identity/cookie/claims model with a cross-org "sysadmin" capability, so one sign-in works everywhere and the UI conditionally renders admin surfaces based on a claim. This is what the user's request most literally asks for, and it is the more coherent long-term shape for a multitenant sysadmin story — one identity, one session, server-derived visibility. **It reverses/extends the shipped "Platform-Admin Scheme Isolation" decision** (`platform-administration` spec's "Requirement: Platform-Admin Scope Isolation," and the isolation rationale in the role-taxonomy design doc) — a real architecture change to already-reviewed, already-shipped code, not a UI-only addition.
- **(b) Keep the two schemes separate server-side; approximate "one login" client-side** — e.g., a single sign-in form that tries the org-scoped endpoint first and falls back to the platform-admin endpoint on failure (or the reverse), so it *feels* like one login without touching the isolation boundary. This is the smaller change and it preserves the shipped isolation decision intact, but it is a UX approximation layered on top of two sessions — it does not literally deliver "the system detects who is logged in," because two separate authentication calls and two separate cookies are still happening underneath.

**Decided: (a), merge the schemes.** Confirmed by the user 2026-09-20 — see "Proposal question round" below for the exact answer and the resulting locked decision.

### Deferred to `sdd-design`

- Exact shape of the new branch-creation endpoint(s) (`POST .../branches` request/response, validation, and whether a `GET .../branches` list is a separate endpoint or embedded in the organization-detail response) — and its exact authorization scope, which is downstream of the identity-model decision above (if schemes stay separate, "business-admin creates a branch in their own org" is the clean default; if schemes merge, a sysadmin path across orgs also becomes possible and needs its own scoping rule).
- Exact UI layout/navigation for the new screens in both `Commerce.Web` (routing, where the Users/Organizations/Branches screens sit relative to existing Catalog/Orders/Customers nav) and `Commerce.Pos.Windows` (menu entry point analogous to how `CustomersWindow` is launched today).
- Whether `GET /account/users` needs pagination/filtering, or a flat list is sufficient at current expected org/staff scale.
- Whether role assignment gets a dedicated UI (checkbox list, role picker) or reuses the existing full-replace `PUT .../roles` request shape as-is with a simple multi-select.
- Whether `GET /platform/organizations/{id}` (a detail endpoint, currently absent) is needed to support an organization-detail/edit view, or the existing list endpoint is sufficient for this phase's UI.

## Capabilities

### New Capabilities

- `admin-console`: the staff/role management UI (`Commerce.Web` and `Commerce.Pos.Windows`) and the organization-onboarding UI (`Commerce.Web`) that drive the existing `account`/`platform` admin endpoints — screen behavior, navigation, permission-gated rendering, and the client-side session-detection UX for whichever identity-model resolution is confirmed.

### Modified Capabilities

- `organization-persistence`: gains branch creation within an existing organization (currently organizations are created with their first branch via bootstrap/`POST /platform/organizations`, but no endpoint adds a *subsequent* branch to an existing org).
- `platform-administration`: if Decision (a) above is confirmed, this capability's "Platform-Admin Scope Isolation" requirement is the one being revisited — named here as a dependency, not resolved. If (b) is confirmed instead, this capability is only referenced, not modified.
- `user-credentials`: gains `GET /account/users` (list staff in an org) as a read surface alongside the existing create/role-assign/reset-password endpoints.
- `web-app-routing`: gains the new admin routes (`RequireAdmin`-gated) in `Commerce.Web`.

## Approach

Sequence the two Locked items ahead of the one open question where possible: `GET /account/users`, branch creation scoped to "business-admin, own org" (the answer that holds under either resolution of Decision 1, since it never requires cross-org authority), and the `CustomersWindow`/`CustomersScreen`-precedent staff UI can all be designed and specced without waiting on the identity-model answer — none of them requires the platform-admin scheme to change. The organization-onboarding UI's sign-in/session-detection behavior is the one piece of this proposal that materially forks depending on the answer, so design work on *that* screen specifically should not start until the question is confirmed.

## Affected Areas

| Area | Impact | Description |
|------|--------|--------------|
| `src/Commerce.Cloud.Api/Endpoints/*` | New/Modified | `GET /account/users`, branch-creation endpoint(s), possibly `GET /platform/organizations/{id}` |
| `src/Commerce.Domain/Identity/RoleCatalog.cs` | Referenced | `Permission.ManageBranchSettings` goes from dead bit to enforced; no taxonomy change |
| `src/Commerce.Web/src/App.tsx` | Modified | New `RequireAdmin`-gated routes: Users, Organizations/Branches |
| `src/Commerce.Web/src/context/AuthContext.tsx` | Modified (scope depends on Decision 1) | Session model extended if (a); unchanged if (b) beyond a sign-in-flow wrapper |
| `src/Commerce.Web/src/screens/*` | New | Users screen (`CustomersScreen` precedent), Organization onboarding screen |
| `src/Commerce.Pos.Windows/MainWindow.xaml.cs` | Modified | New menu entry gated by `ManageUsers`, opening a new modal window |
| `src/Commerce.Pos.Windows/*Window.xaml(.cs)` | New | Users/Roles admin window (`CustomersWindow` precedent) |
| `openspec/specs/organization-persistence/spec.md` | Modified | New branch-creation requirement |
| `openspec/specs/platform-administration/spec.md` | Modified only if Decision (a) confirmed | Scope-isolation requirement revisited |
| `openspec/specs/user-credentials/spec.md` | Modified | New list-staff requirement |

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| Identity-model question gets resolved by default (whichever endpoint the UI happens to call first) instead of by explicit decision | High | Named as this proposal's own blocking question round item; design must not start on the onboarding screen until answered |
| Branch-creation authorization scope copied from the wrong precedent (business-admin vs. platform-admin) before Decision 1 is answered | Medium | Scoped to "business-admin, own org" as the resolution-independent default; cross-org creation only added if (a) is confirmed |
| `ManageBranchSettings` enforcement introduced inconsistently (checked on create but not on other branch-mutating paths, if any exist later) | Low | Named explicitly as the dead-bit being activated; design should state exactly which endpoints now check it |
| POS.Windows admin window drifts from the `CustomersWindow` precedent (different client lifecycle, different permission-check helper) | Medium | Locked as "follow the precedent directly," not "build something new that happens to look similar" |
| Scope creep into "other future config" during design | Low | Explicitly out of scope per the user's own answer; any such request needs its own proposal |

## Rollback Plan

Revert the commit(s): new endpoints, new screens/windows, and the new routes disappear; existing `CustomersScreen`, `CustomersWindow`, and all currently-shipped `/account` and `/platform` endpoints are unmodified in behavior, so no existing staff/customer/order flow needs unwinding. If Decision (a) is confirmed and implemented, its migration must ship an inverse (schema/claim rollback) since it touches the already-shipped platform-admin isolation boundary — this is a heavier rollback than the additive UI work and should be called out again explicitly in design once (a) or (b) is confirmed.

## Dependencies

- `platform-administration` (shipped) — `POST /platform/organizations`, `GET /platform/organizations`, `PlatformAdminCookie` scheme; the isolation requirement this proposal's open question may revisit.
- `organization-persistence` (shipped) — organizations/branches domain model; gains a branch-creation endpoint here.
- `user-credentials` (shipped) — `POST /account/users`, `PUT /account/users/{id}/roles`, `POST /account/users/{id}/reset-password`; gains a list endpoint here.
- `commerce-role-taxonomy` (shipped, archived 2026-09-19) — defines the four-role taxonomy and the Platform-Admin Scheme Isolation decision this proposal's open question directly engages with.
- `web-app-routing` (shipped) — `RequireAuth`/`RequireAdmin` route-guard mechanism this change's new Web routes reuse.
- User's answers to scoping questions 2-4 (2026-09-20, recorded in Decisions above) — specs may proceed on these without further confirmation.
- User's answer to the identity-model question (Decision 1's open item) — specs/design for the organization-onboarding screen's sign-in flow may NOT proceed until this is confirmed.

## Success Criteria

- [ ] A business-admin can list, create, and edit-role staff members in their own organization from both `Commerce.Web` and `Commerce.Pos.Windows`, using `GET /account/users` (new), `POST /account/users`, and `PUT /account/users/{id}/roles`.
- [ ] A business-admin can create a new branch in their own organization from the UI, via the new branch-creation endpoint; `Permission.ManageBranchSettings` is checked and enforced (no longer a dead bit).
- [ ] The `Commerce.Pos.Windows` staff/role window follows the `CustomersWindow` precedent exactly: `ManageUsers`-gated visibility, fresh per-window client, `ShowDialog()` lifecycle.
- [ ] The `Commerce.Web` Users screen follows the `CustomersScreen`/`RequireAdmin` precedent exactly.
- [ ] The organization-onboarding screen in `Commerce.Web` implements whichever identity-model resolution was confirmed (merged scheme or smoothed dual-session UX) — not built ahead of that confirmation.
- [ ] `RoleCatalog.OrgAssignable`'s exclusion of `platform-admin` from org-grantable roles remains enforced by the new staff UI (an org admin cannot self-grant platform-admin via the role-reassignment screen).
- [ ] `dotnet test Commerce.sln` and `dotnet build Commerce.sln` pass; `Commerce.Web` and `Commerce.Pos.Windows` build.

## Proposal question round

**Answered by the user 2026-09-20.** Asked whether to (a) merge platform-admin into the existing org-scoped identity model (one real backend auth mechanism, cross-org capability expressed as a claim/permission) or (b) keep two backend schemes but paper over it with a single-looking sign-in form that tries both under the hood.

User's answer, verbatim intent: **"El componente login es y debe ser único. Lo que cambia es quién se loguea y los permisos que tiene dentro del sitio."** ("The login component is and must be unique. What changes is who logs in and the permissions they have within the site.") The user does not want two authentication mechanisms coexisting even invisibly — a single sign-in form calling two different backends under the hood (option b) does not satisfy this. This is **option (a): merge the identity schemes.**

### Locked decision: merge platform-admin into the single org-scoped identity model

- **One authentication mechanism, one cookie scheme, one sign-in endpoint, one login UI component** (`SignInScreen`/`LoginRoute` in Web; the equivalent in POS.Windows if platform-admin sign-in is ever needed there — likely not, see below). No separate `PlatformAdminCookie` scheme, no separate `platform_admins` table with its own password hasher living apart from `UserAccount`.
- A user's cross-org "sysadmin" capability becomes a **claim/flag expressed within the existing identity model**, not a categorically different login type. The exact mechanism (a new `Permission` bit that is meaningful cross-org rather than org-scoped, a separate `IsSystemAdmin` flag on `UserAccount` orthogonal to the org-scoped `Permission` bitmask, or a dedicated `system_admins` table that still authenticates through the SAME cookie scheme as everyone else, just consulted as an additional claim source at sign-in) is **explicitly deferred to `sdd-design`** — the user's requirement is about having one login surface and permission-driven visibility, not about the specific storage shape.
- This **formally reverses** the `commerce-role-taxonomy` phase's "Platform-Admin Scheme Isolation" decision (already shipped, already reviewed). `sdd-design` for this change MUST explicitly address: (1) how existing platform-admin data (the `platform_admins` table, if any real rows exist beyond the dev bootstrap) migrates into the unified model without data loss; (2) whether `GET /platform/organizations`'s current fail-closed-503 cross-org-read guarantee is preserved under the new claim-based model (the safety property — never silently falling back to an org-scoped read — must survive the merge, even if the mechanism enforcing it changes); (3) whether the existing `/platform/*` routes are kept (now behind the unified cookie + a sysadmin claim check) or folded into `/account/*` with a permission check, and what happens to any code/tests that assumed the old two-scheme separation.
- **Not required by this decision**: POS.Windows does not need its own platform-admin/sysadmin sign-in surface. A POS terminal is architecturally paired to one org+branch (device pairing model, unchanged) — a sysadmin managing multiple organizations doing so from a POS terminal has no realistic use case flagged by the user. The unified login applies to `Commerce.Web`; POS.Windows keeps using its existing paired-device + operator-login flow, extended only with the staff/role management window (already locked in scope above), which stays within the POS's own org/branch, same as `CustomersWindow` today.

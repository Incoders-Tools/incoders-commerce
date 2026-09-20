# Proposal: Commerce Role Taxonomy

## Intent

The staff identity model has exactly one role in existence: the literal `"admin"`, minted once at bootstrap (`Account.cs:444`) with all four `Permission` flags. There is no way to create a second staff user, no way to assign a role, and no vocabulary for the actors PRD.md §7 already assumes (cajero/vendedor, proveedor, administrador del sistema). `Role` is a free-text `Name` + `Permission` flags pair persisted inside the user's jsonb roles column, so today *any* permission set could be written under *any* name — there is no canonical catalog. This change defines the staff role taxonomy and gives it a minimal way to actually be used.

Note: **Client/Cliente is NOT a role.** `CustomerOrderingAccess` is a separate token-credential model (no login, no password, revocable link) per the approved `private-customer-ordering` spec. It stays untouched.

## Scope

### In Scope
- **Canonical server-side role catalog**: name → permission-set mapping owned by the server, not by the caller. Role assignment references a catalog entry; permissions are never accepted from the request body (required by `tenant-access-foundation`'s "actor identity from the persisted store" rule). Catalog keys (role names, permission flag names) are English kebab-case/PascalCase technical identifiers, never translated or persisted in Spanish — Spanish labels (Vendedor, Proveedor, Administrador de Negocio, Administrador de Plataforma) are a **UI-only display concern**, resolved client-side from the English key, never sent to or stored by the API.
- **Role set for this slice**:

  | Role name | Status | Permissions |
  |---|---|---|
  | `business-admin` | **rename of today's `admin`** | all flags (unchanged set) |
  | `seller` (Vendedor) | new | `ViewSales` only for now — no `RecordSales` flag yet (deferred, see Decisions) |
  | `provider` (Proveedor) | **reserved placeholder** | `None` — assignable name, zero capability, zero endpoints |
  | `platform-admin` | **new, implemented in this change** | cross-organization scope — see "Platform Admin" below |

- **Rename migration**: existing persisted `"admin"` role entries rewritten to `"business-admin"`; bootstrap literal updated. `"admin"` alone is now ambiguous once a platform-level admin exists.
- **Minimal user creation + role assignment**: `POST /account/users` (create staff user with email, password, role, branch scope) and `PUT /account/users/{userId}/roles`, both on the existing `ManageUsers`-gated `/account/users` group, org-scoped from cookie claims. Branch scope must be contained in the caller's organization. **Grant cap**: a caller can only assign permissions that are a subset of their own `EffectivePermissions` — a `ManageUsers` holder can never create or promote a user to a permission set greater than their own. `platform-admin` can only be granted by an existing `platform-admin` (never by an org-scoped `business-admin`, regardless of grant-cap math).
- **Platform Admin (cross-organization superadmin)**: a genuinely separate identity space, operated by Incoders, not scoped to any single `organization_id`. Concretely: a `platform_admins` table (own credentials, own `PasswordHasher<PlatformAdmin>` verification, separate from `UserAccount`/`user_directory`) issuing its own cookie scheme (`PlatformAdminAuth`, distinct from the org-scoped `CookieAuthenticationDefaults` scheme) carrying no `org_id` claim at all. Platform-admin-only endpoints run under a policy that requires the `PlatformAdminAuth` scheme and explicitly do NOT go through `TenantScopeResolver`/`app.current_org_id` RLS — they read/write with an explicit organization id supplied per call (never "current org from claims", since there is none), audited every time (see Audit below). This is intentionally the smallest possible slice of "platform admin": bootstrap one, sign in, list organizations, and force-create the first `business-admin` for a new organization (replacing today's log-only bootstrap-token flow as the operator path) — no broader platform console.
- **Audit logging (cross-cutting requirement)**: every user-management action in this change's scope — user created, role assigned/changed, platform-admin action of any kind — is written to an append-only audit log: actor id, actor kind (org user vs. platform admin), acted-on entity type + id, organization id, action, timestamp, and (where applicable) old/new value. This is the minimum needed to answer "what happened to this entity, who did it, when" for the surfaces this change touches. It is **not** a general-purpose audit framework for the whole system — that is a larger, separate concern the user flagged as a standing principle for future changes, noted here so later changes don't have to rediscover it.
- Delta requirements on `user-credentials` and `tenant-access-foundation`; a new capability `platform-administration` for the cross-org piece.

### Out of Scope
- Client/customer ordering access (not a role — separate model, untouched).
- Provider invoice upload, or any provider-facing capability.
- `RecordSales` permission flag and any seller-facing sales/POS endpoint — deferred until the sales module exists, per explicit user decision. `seller` ships with `ViewSales` only.
- Full user-management CRUD (list/edit/delete/revoke), role *definition* editing by tenants, per-user permission overrides, branch-admin / financial / inventory / delivery roles from PRD §7.
- A general-purpose, system-wide audit framework — this change's audit log covers only the entities/actions it introduces (user creation, role assignment, platform-admin actions).
- Web UI for user management or for platform-admin (API slice only).
- Platform-admin impersonation of an org user, or any broader platform console/dashboard beyond the minimum listed above.

## Decisions (confirmed by the user)

| Decision | Answer |
|---|---|
| Platform-admin sizing | Implement now, in this change (not split into a follow-up) |
| Grant cap | A caller cannot grant permissions beyond their own `EffectivePermissions` |
| `RecordSales` flag | Deferred until the sales module exists — not added in this change |
| Technical naming | Role/permission keys are English identifiers always; Spanish is UI-display-only |
| Audit | Every user-management and platform-admin action must be logged: actor, entity, org, action, timestamp, old/new value |

## Capabilities

### New Capabilities
- `platform-administration`: cross-organization Incoders operator identity, separate credential/session scheme, minimal bootstrap-organization capability, fully audited.

### Modified Capabilities
- `user-credentials`: canonical role catalog, `business-admin` rename with migration, `seller` and reserved `provider` roles, staff user creation and role assignment endpoints, grant-cap enforcement.
- `tenant-access-foundation`: role names come from a server-owned catalog; assignable roles are constrained; reserved/platform roles cannot be granted by org-scoped callers; audit logging is a first-class requirement for user-management actions.

## Approach

Additive for the staff-role catalog piece: introduce a static server-side role catalog in `Commerce.Domain.Identity` mapping canonical names to `Permission` sets, preserving the existing jsonb `RoleDto(name, permissions)` shape on disk (no schema change to the roles column). New org-scoped endpoints mount on the existing `ManageUsers`-gated `/account/users` group established by `commerce-password-recovery`, reusing its org-scoping and same-org-target checks, plus a grant-cap check (`target permissions ⊆ caller.EffectivePermissions`). Rename handled by a forward-only data migration over the jsonb column.

Structurally new for platform-admin: a parallel, minimal identity plane. `platform_admins` table (own row, own password hash, no `organization_id` column at all — it is not tenant data), its own ASP.NET Core cookie scheme so it can never be confused with an org-scoped session, and endpoints under `/platform/...` gated by that scheme instead of `TenantScopeEndpointFilter`. Because RLS policies are keyed on `app.current_org_id`, platform-admin endpoints connect using an explicit, per-call target `organization_id` parameter and a Postgres role/policy path deliberately reviewed in design (e.g., a narrow `WITH CHECK`-scoped write path, not a blanket RLS bypass) — this is the single highest-risk piece of this change and design must own the exact mechanism, not this proposal. Every platform-admin action and every org-scoped user-management action writes an audit row in the same transaction as the mutating write.

## Affected Areas

| Area | Impact | Description |
|------|--------|-------------|
| `src/Commerce.Domain/Identity/RoleCatalog.cs` | New | Canonical name → permission map, reserved-role list |
| `src/Commerce.Cloud.Api/Endpoints/Account.cs` | Modified | `POST /account/users`, `PUT /account/users/{id}/roles` (grant-cap enforced); bootstrap emits `business-admin` |
| `src/Commerce.Cloud.Api/Endpoints/PlatformAdmin.cs` | New | Platform-admin sign-in, list organizations, bootstrap a new organization's first `business-admin` |
| `src/Commerce.Cloud.Api/Authentication/PlatformAdminAuth*.cs` | New | Separate cookie scheme, distinct from the org-scoped one |
| `src/Commerce.Domain/Identity/PlatformAdmin.cs` | New | Platform-admin identity, not organization-scoped |
| `src/Commerce.Cloud.Api/Auditing/` | New | Append-only audit-log write path (actor, entity, org, action, timestamp, old/new value) |
| `deploy/db/migrations/0006_*.sql` | New | Rewrite `"admin"` → `"business-admin"`; `platform_admins` table; audit-log table with its own RLS treatment (platform-admin actions have no single owning org) |
| `src/Commerce.Domain/Ordering/CustomerOrderingAccess.cs` | **Unchanged** | Explicitly not a role; guarded against regression |

## Risks

| Risk | Likelihood | Mitigation |
|------|------------|------------|
| Platform-admin's RLS/scoping mechanism is designed loosely and becomes a cross-tenant data-leak path | High if unreviewed | Design phase must specify the exact Postgres access path for platform-admin writes/reads; no blanket RLS bypass; every access audited |
| Rename migration misses a persisted `"admin"` entry and locks an org out | Medium | Migration is a jsonb rewrite verified by scenario; run inside a transaction; bootstrap literal changed in the same commit |
| Free-text role names let a caller invent a role or inflate permissions | High if unguarded | Catalog-driven assignment; reject any name outside the catalog; permissions never read from the request body |
| A `ManageUsers` holder escalates by creating a user with more rights than itself | Mitigated by decision | Grant-cap: target permissions must be a subset of the caller's own `EffectivePermissions`; `platform-admin` only grantable by a platform-admin |
| Platform-admin credential compromise has cross-tenant blast radius | Medium | Separate credential/session scheme (not reachable via the org sign-in path); every action audited; minimal capability surface (bootstrap-organization only, no broad console) |
| Audit log scoped only to this change's entities gives a false sense of full system coverage | Low | Explicitly named as a partial, first-instance implementation in Out of Scope; not a general audit framework |

## Rollback Plan

Revert the commit: new endpoints, the catalog, and the platform-admin plane disappear; bootstrap returns to `"admin"`. The data migration needs the inverse jsonb rewrite (`"business-admin"` → `"admin"`) and a `DROP TABLE` for `platform_admins` and the audit table; ship the inverse statements in the migration file's comments so rollback is mechanical.

## Dependencies

- Builds on `commerce-password-recovery`'s `/account/users` authenticated group (merged).
- None external — platform-admin bootstrap replaces the org bootstrap-token operator flow but does not require it to be removed in this change.

## Success Criteria

- [ ] A `business-admin` creates a `seller` in their own organization and that seller signs in.
- [ ] Created users cannot be given a branch scope outside the caller's organization.
- [ ] A `business-admin` cannot grant a permission they do not themselves hold, nor assign `provider`'s reserved capability set beyond `None`, nor assign `platform-admin`.
- [ ] Assigning an unknown role name is rejected.
- [ ] Permissions attached to a created user come from the catalog, not from the request body, even when the body supplies them.
- [ ] A pre-existing `"admin"` user is `"business-admin"` after migration with an unchanged permission set and can still sign in.
- [ ] `provider` is assignable and grants nothing.
- [ ] A platform-admin signs in via the separate scheme, lists organizations, and bootstraps a new organization's first `business-admin` — an org-scoped `business-admin` cannot reach any platform-admin endpoint.
- [ ] Every user-management action (create user, assign role, platform-admin bootstrap) produces an audit row with actor, entity, organization, action, and timestamp.
- [ ] Customer ordering access is unaffected — no `CustomerOrderingAccess` change.
- [ ] `dotnet test Commerce.sln` passes.

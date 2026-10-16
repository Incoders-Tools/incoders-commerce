# Delta for Tenant Access Foundation

## MODIFIED Requirements

### Requirement: Organization and Branch Isolation

Every business record, action, and authorization decision MUST carry an
organization context and MUST enforce the user's permitted branch scope. A
request from one organization or unauthorized branch MUST NOT disclose or
mutate data in another scope. Organizations and branches referenced in
authorization decisions MUST be persisted, RLS-scoped rows rather than
unvalidated ids — a user's branch scope is contained only when it references
a branch that actually exists under that user's organization.

(Previously: "the branch" a user's branch scope referenced had no persisted
existence — `Organization` and `Branch` were domain types with zero
persistence, so branch-containment checks in practice always failed for
bootstrap-created admins whose `branch_scope` was seeded empty. This delta
changes what "the branch" IS, not the containment check itself —
`TenantAuthorizationService.Evaluate` requires no code change.)

#### Scenario: Authorized branch access

- GIVEN a user is authorized for Organization A, Branch 1
- WHEN the user reads or changes an allowed record in Branch 1
- THEN the action succeeds and retains Organization A and Branch 1 scope

#### Scenario: Cross-branch and cross-organization denial

- GIVEN a user is authorized only for Organization A, Branch 1
- WHEN the user requests Branch 2 data or any Organization B data
- THEN the request is denied without revealing protected record existence

#### Scenario: Bootstrapped admin passes branch containment against a real branch

- GIVEN an admin was created via organization bootstrap and its branch scope
  contains the id of the branch created in that same transaction
- WHEN the admin performs catalog management scoped to that branch
- THEN the branch-containment check succeeds because the referenced branch
  is a persisted row under the admin's organization

# Delta for Tenant Access Foundation

## ADDED Requirements

### Requirement: Anonymous Request Organization Resolution

A request that arrives with no authenticated principal (a guest order)
MUST still resolve to exactly one organization and branch before being
processed; this resolution MUST NOT depend on any authenticated
principal's claims. For the current deployment this resolution MUST
target the single configured organization (Vaca Verde) and its
principal/default branch. The resolution mechanism MUST be isolated behind
a single responsibility so a future per-request resolution (e.g.
multi-organization) can replace the fixed default without changing the
domain or endpoint shape it feeds.

#### Scenario: Anonymous guest request resolves to the fixed default organization and branch

- GIVEN a guest request carries no authenticated principal
- WHEN the request is processed
- THEN it resolves to the single configured organization and its
  principal/default branch

#### Scenario: Guest order carries organization/branch context despite no principal

- GIVEN a guest order was admitted from an anonymous request
- WHEN the order is read back
- THEN it carries the same organization and branch context required of
  every business record under "Organization and Branch Isolation"

# Delta for Organization Persistence

## ADDED Requirements

### Requirement: Price and Identification Tables Follow Established RLS Convention

New org-scoped tables introduced for price list entries and per-supplier
import mapping/staging MUST follow the same row-level security and store
conventions defined by `PostgresOrganizationStore` (`FORCE ROW LEVEL
SECURITY`, `REVOKE ALL FROM PUBLIC` plus explicit `GRANT`,
`NULLIF(current_setting(...))` pooler idiom) rather than introducing a new
persistence idiom.

#### Scenario: New price table enforces the same RLS pattern

- GIVEN the price list entry table is created by this change
- WHEN a request scoped to one organization queries it
- THEN only that organization's rows are visible, per the existing RLS
  pattern used by `organizations` and `branches`

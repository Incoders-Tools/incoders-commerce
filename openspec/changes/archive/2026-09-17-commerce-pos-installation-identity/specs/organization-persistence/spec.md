# Delta for Organization Persistence

## ADDED Requirements

### Requirement: Branch Listing Scoped to a User's Branch Scope

The system MUST provide a query that returns only the branches contained
in a given user's persisted `BranchScope`, resolved against real,
persisted branch rows. The query MUST NOT return branches outside that
scope, even if they belong to the user's own organization.

#### Scenario: Query returns only in-scope branches

- GIVEN a user's `BranchScope` contains Branch 1 but not Branch 2, and
  both branches belong to the same organization
- WHEN the branch-by-user query runs for that user
- THEN only Branch 1 is returned

#### Scenario: Query returns nothing for a user with an empty branch scope

- GIVEN a user's `BranchScope` is empty
- WHEN the branch-by-user query runs for that user
- THEN no branches are returned

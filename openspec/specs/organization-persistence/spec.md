# Organization Persistence Specification

## Purpose

Define Postgres-backed, RLS-scoped persistence for organizations and
branches — the tenancy roots that user accounts and authorization decisions
depend on — created transactionally at bootstrap time.

## Requirements

### Requirement: Persisted Organization and Branch Storage

The system MUST persist organizations and branches as rows in Postgres,
with each branch carrying a foreign key to its owning organization. Both
tables MUST enforce row-level security scoped by `app.current_org_id`,
following the `users`/`sync_inbox` `FORCE ROW LEVEL SECURITY` /
non-owner `app_runtime` role pattern. Each organization row MUST carry a
stable id and a human-friendly `organizationName`. Each branch row MUST
carry a stable id, its owning organization id, and a human-friendly name.

#### Scenario: Organization and branch rows are isolated by organization

- GIVEN an organization and branch exist for Organization A
- WHEN a request scoped to Organization B queries `organizations` or
  `branches`
- THEN no row belonging to Organization A is returned

#### Scenario: Branch is scoped to its organization

- GIVEN a branch exists under Organization A
- WHEN the branch row is read
- THEN it carries Organization A's id as its owning organization

### Requirement: Transactional Organization Bootstrap Creation

The system MUST create exactly one organization and exactly one branch for
that organization as part of the same database transaction that creates the
organization's first admin user. If any part of that transaction fails, the
system MUST persist none of the organization, branch, or user rows.

#### Scenario: Successful bootstrap creates organization, branch, and admin together

- GIVEN a valid, unconsumed bootstrap token for an organization id that has
  no persisted organization
- WHEN bootstrap completes successfully
- THEN exactly one organization row, one branch row, and one admin user row
  exist, all referencing the same organization id

#### Scenario: Failure during bootstrap leaves no partial rows

- GIVEN a bootstrap request that fails after the organization would have
  been created but before the admin user is persisted
- WHEN the failure occurs
- THEN no organization row, branch row, or user row from that attempt exists

#### Scenario: Bootstrap rejected when the organization already exists

- GIVEN a persisted organization already exists for the requested
  organization id
- WHEN a bootstrap request targets that organization id
- THEN the request is rejected and no new organization, branch, or user row
  is created

### Requirement: Branch Name Defaulting

The system MUST accept an optional branch name at bootstrap time and MUST
create the bootstrap branch using that name when provided. When no branch
name is provided, the system MUST create the branch using a default name of
"Main".

#### Scenario: Branch created with a caller-supplied name

- GIVEN a bootstrap request supplies a branch name of "Downtown"
- WHEN bootstrap completes successfully
- THEN the created branch's name is "Downtown"

#### Scenario: Branch created with the default name

- GIVEN a bootstrap request omits the branch name
- WHEN bootstrap completes successfully
- THEN the created branch's name is "Main"

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

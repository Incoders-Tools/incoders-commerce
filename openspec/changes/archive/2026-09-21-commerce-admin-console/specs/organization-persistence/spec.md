# Delta for Organization Persistence

## ADDED Requirements

### Requirement: Branch Creation Within an Existing Organization

The system MUST expose an endpoint, gated by `Permission.ManageBranchSettings`
and scoped to the acting user's own organization, that creates a new branch
row under an existing organization. The endpoint MUST reject a request whose
target organization is not the caller's own organization. This activates
`Permission.ManageBranchSettings`, previously defined in `RoleCatalog.cs` but
enforced by no endpoint.

#### Scenario: Business-admin creates a branch in their own organization

- GIVEN an authenticated caller holding `Permission.ManageBranchSettings` in
  Organization A
- WHEN they call the branch-creation endpoint with a branch name and no
  target organization other than their own
- THEN a new branch row is created under Organization A

#### Scenario: Cross-organization branch creation is rejected

- GIVEN an authenticated caller holding `Permission.ManageBranchSettings` in
  Organization A
- WHEN they attempt to create a branch under Organization B
- THEN the request is rejected and no branch row is created

#### Scenario: Caller without ManageBranchSettings is denied

- GIVEN an authenticated caller lacking `Permission.ManageBranchSettings`
- WHEN they call the branch-creation endpoint
- THEN the request is rejected and no branch row is created

### Requirement: Branch Listing Endpoint

The system MUST expose an endpoint that lists the branches belonging to the
caller's own organization, scoped by the same row-level security as branch
storage. The endpoint MUST NOT return a branch belonging to another
organization.

#### Scenario: Business-admin lists their organization's branches

- GIVEN Organization A has two persisted branches
- WHEN an authenticated caller in Organization A calls the branch-listing
  endpoint
- THEN both branches are returned

#### Scenario: Listing does not leak another organization's branches

- GIVEN Organization A and Organization B each have persisted branches
- WHEN an authenticated caller in Organization A calls the branch-listing
  endpoint
- THEN no branch belonging to Organization B is returned

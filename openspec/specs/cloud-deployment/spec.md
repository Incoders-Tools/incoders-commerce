# Cloud Deployment Specification

## Purpose

Define environment topology, container build, deploy mechanism, and managed-Postgres persistence for the cloud-facing hosts (Commerce.Cloud.Api, Commerce.Web) without deploying branch-local components (Commerce.Pos.Windows / Commerce.BranchNode) to the cloud, and without any database identity that would bypass tenant isolation. The database host is PostgreSQL running as a Railway service (ADR-014, which replaced Supabase).

## Requirements

### Requirement: Cloud Scope Excludes Branch-Local Hosts

Only Commerce.Cloud.Api and Commerce.Web MUST be deployable to Railway. Commerce.Pos.Windows and the in-process Commerce.BranchNode MUST NOT be deployed to Railway or any cloud host; they MUST be validated on a real or virtual Windows machine pointed at either local Docker or a deployed cloud environment.

#### Scenario: POS validation target

- GIVEN Commerce.Pos.Windows requires validation
- WHEN a validation run is planned
- THEN it targets a Windows machine or VM connecting to local Docker or a deployed Cloud.Api, never a Railway deployment of Pos.Windows itself

### Requirement: Environment-Parameterized Deploy Configuration

The deploy mechanism MUST treat the environment/channel name as a configurable value, not a value hardcoded into application or deploy code, so that provisioning a new environment (e.g. production) later requires configuration, not code changes.

#### Scenario: Adding a new environment later

- GIVEN only staging is provisioned in this change
- WHEN a production environment is provisioned later
- THEN it reuses the same deploy configuration shape with a different environment value, without editing application source

### Requirement: Railway Push-to-Deploy via Declarative Config

Deployment to Railway MUST use native GitHub push-to-deploy driven by an in-repo `railway.json` (Railway's schema: `build.builder: "DOCKERFILE"`, `build.dockerfilePath`, `build.watchPatterns`, `deploy.startCommand`, `deploy.healthcheckPath`, `deploy.healthcheckTimeout`, `deploy.restartPolicyType`, `deploy.restartPolicyMaxRetries`) plus a Dockerfile. It MUST NOT use a GitHub Actions CLI-based deploy step. The Dockerfile's build context MUST be the repository root regardless of `dockerfilePath`, and any `COPY` path MUST be repo-root-relative.

#### Scenario: Push triggers Railway build and deploy

- GIVEN `railway.json` and a Dockerfile exist in the repository
- WHEN a commit is pushed to the environment's mapped branch
- THEN Railway builds the Dockerfile image using the repo root as build context and deploys it without a CLI deploy step in CI

#### Scenario: Health check gates deploy completion

- GIVEN `deploy.healthcheckPath` is configured
- WHEN a new deployment starts
- THEN Railway only marks the deployment healthy after the health endpoint responds successfully within `deploy.healthcheckTimeout`

### Requirement: Railway-Hosted Postgres on the Private Network

Each Railway environment MUST run its own PostgreSQL service, reached by Commerce.Cloud.Api only over that environment's private network, with no public TCP proxy. The runtime MUST NOT connect as the `postgres` superuser Railway creates. Each environment MUST keep scheduled volume backups and point-in-time recovery enabled.

#### Scenario: Runtime connects without the superuser

- GIVEN Commerce.Cloud.Api is configured against a Railway environment
- WHEN it establishes a database connection
- THEN it uses a non-owner, RLS-bound runtime role's connection string, never the `postgres` superuser

#### Scenario: Per-environment database isolation

- GIVEN staging and production are provisioned
- WHEN either environment's Cloud.Api resolves `postgres.railway.internal`
- THEN it reaches only its own environment's Postgres service, never a database shared across environments

### Requirement: RLS Policy Carried Over from Local Reference Design

The deployed schema MUST enforce the same row-level-security design proven in `deploy/dev/db/init-rls.sql`: `FORCE ROW LEVEL SECURITY`, a non-owner `app_runtime`-equivalent role as the sole application connection identity, and tenant scoping via `current_setting('app.current_org_id')`.

#### Scenario: Cross-organization read denial on deployed database

- GIVEN two organizations have rows in the same deployed table
- WHEN a request scoped to Organization A queries the table
- THEN no Organization B rows are returned

#### Scenario: Table owner cannot bypass RLS

- GIVEN RLS is enabled and forced on a tenant-scoped table
- WHEN a connection attempts a query without an explicit policy match
- THEN zero rows are returned even for the table owner

### Requirement: Pooler and Session-Scoping Interaction Must Be Proven

Deployed environments connect directly, using Npgsql's built-in pool (ADR-014). If a transaction-mode pooler (for example PgBouncer) is ever placed in front of a deployed database, the deployment MUST NOT assume its interaction with `SET LOCAL app.current_org_id` is safe. An integration test against a real or realistic pooled connection MUST verify that per-request tenant scoping remains correctly isolated before the pooler configuration is accepted for production use.

#### Scenario: Pooled connection scoping test passes

- GIVEN a realistic transaction-mode pooled connection is available for testing
- WHEN concurrent requests set different `app.current_org_id` values and query tenant-scoped tables
- THEN each request observes only its own organization's rows with no cross-contamination

#### Scenario: Pooler behavior found unsafe

- GIVEN the pooled-connection test reveals tenant-scope leakage or unreliable `SET LOCAL` behavior
- WHEN this is discovered
- THEN the deployment falls back to session or direct (non-transaction-pooled) connections rather than shipping the unverified combination

### Requirement: Local Docker Compose Extension for Full Local Development

The local Docker Compose stack MUST be extended to support running Cloud.Api and Web locally end-to-end, while Postgres remains dev-only and no client is required to run Docker for basic operation.

#### Scenario: Full local stack via Compose

- GIVEN a developer runs the extended `deploy/dev/compose.yaml`
- WHEN the stack starts
- THEN Cloud.Api, its Postgres dependency, and Web are reachable locally without a cloud dependency

#### Scenario: POS runs without Docker requirement

- GIVEN a developer wants to run Commerce.Pos.Windows locally
- WHEN it targets an already-running Cloud.Api
- THEN the POS itself does not require Docker to be installed or running

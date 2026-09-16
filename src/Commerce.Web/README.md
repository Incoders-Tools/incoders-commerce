# React + TypeScript + Vite

This template provides a minimal setup to get React working in Vite with HMR and some Oxlint rules.

Currently, two official plugins are available:

- [@vitejs/plugin-react](https://github.com/vitejs/vite-plugin-react/blob/main/packages/plugin-react) uses [Oxc](https://oxc.rs)
- [@vitejs/plugin-react-swc](https://github.com/vitejs/vite-plugin-react/blob/main/packages/plugin-react-swc) uses [SWC](https://swc.rs/)

## React Compiler

The React Compiler is not enabled on this template because of its impact on dev & build performances. To add it, see [this documentation](https://react.dev/learn/react-compiler/installation).

## Expanding the Oxlint configuration

If you are developing a production application, we recommend enabling type-aware lint rules by installing `oxlint-tsgolint` and editing `.oxlintrc.json`:

```json
{
  "$schema": "./node_modules/oxlint/configuration_schema.json",
  "plugins": ["react", "typescript", "oxc"],
  "options": {
    "typeAware": true
  },
  "rules": {
    "react/rules-of-hooks": "error",
    "react/only-export-components": ["warn", { "allowConstantExport": true }]
  }
}
```

See the [Oxlint rules documentation](https://oxc.rs/docs/guide/usage/linter/rules) for the full list of rules and categories.

## E2E tests

`npm test` (Vitest) only ever exercises the SPA against a **mocked** `fetch`
— it proves request/response *shape*, never that a real backend actually
authenticates a browser or enforces authorization. `src/Commerce.Web/e2e/`
is a separate, real-browser [Playwright](https://playwright.dev/) suite that
runs against a REAL, running `Commerce.Cloud.Api` (real Postgres, real
cookie auth, real authorization checks) — no mocking anywhere.

### 1. Bring up Postgres

```sh
docker compose -f ../../deploy/dev/compose.yaml up -d
```

(`commerce-postgres-1` / `commerce-pgbouncer-1`; skip if already running —
check with `docker ps`.)

### 2. Build the SPA and copy it into Cloud.Api's `wwwroot`

Same-origin matters here: `Program.cs`'s sign-in cookie sets
`CookieSecurePolicy.Always`, so a browser will silently refuse to store it
unless the page itself was loaded over HTTPS from the SAME origin as the
API. The Vite dev-server proxy (plain HTTP, a different origin) cannot
carry that cookie, so E2E does not use `npm run dev` — instead it builds
the real production bundle and serves it from Cloud.Api itself, exactly as
the Dockerfile does for a real deploy:

```sh
npm run test:e2e:build-backend-spa
```

### 3. Run Commerce.Cloud.Api

```sh
dotnet run --project ../Commerce.Cloud.Api
```

This binds plain HTTP on port 8080 (`Program.cs` always calls
`ConfigureKestrel`/`ListenAnyIP($PORT ?? 8080)` — this overrides
`Properties/launchSettings.json`'s `https://localhost:56595` entry
entirely; in a real deploy, HTTPS only ever exists via Railway's edge TLS
termination in front of this same plain-HTTP Kestrel).

### 4. Front it with a local HTTPS proxy

Because of the Secure-cookie requirement above, plain `http://localhost:8080`
cannot carry a session cookie in a real browser. A tiny local TLS proxy
(bundled self-signed cert, `local-ssl-proxy`, already a devDependency here)
sits in front of it purely for local/CI E2E:

```sh
npx local-ssl-proxy --source 5443 --target 8080
```

### 5. Run the suite

```sh
npm run test:e2e
```

`playwright.config.ts` defaults `baseURL` to `https://localhost:5443`
(step 4's proxy) and sets `ignoreHTTPSErrors: true` (the proxy's cert is
self-signed). Point at a different already-running instance with
`E2E_BASE_URL=https://your-host:port npm run test:e2e`.

### What's covered

- **Sign-in success** (`e2e/sign-in.spec.ts`) — real form fill/submit
  against real `/account/sign-in`, asserts the SPA reflects authenticated
  state (`Sign out` button, display name).
- **Sign-in failure** — wrong password AND unknown email each produce the
  exact same generic error text (the backend's generic-401 design in
  `Endpoints/Account.cs` never reveals which check failed).
- **Order submission** (`e2e/ordering.spec.ts`) — real signed-in user
  submits a real order via `/orders/` and gets a real `Accepted` outcome.
  Ordering is NOT gated by branch scope (see `CustomerCatalogAccessService`
  vs. `TenantAuthorizationService` — a structurally different,
  customer-facing access check), so this is fully E2E-testable today with
  no test-only seam beyond a signed-in user.
- **Catalog rename** (`e2e/catalog.spec.ts`) — two cases:
  - **Denied**, using a bootstrap-shaped admin (empty branch scope): this
    is the REAL, permanent behavior of every real bootstrap-created admin
    today, since no branch-persistence feature exists anywhere in this
    system (`Endpoints/Account.cs`'s bootstrap remarks) — a real admin can
    never pass `TenantAuthorizationService`'s
    `actor.BranchScope.Contains(targetBranchId)` check, for any branch.
  - **Allowed**, using a user seeded with a non-empty branch scope via the
    **test-only** `POST /internal/test-seed/user` endpoint
    (`Endpoints/TestSeedEndpoints.cs`, mapped only when
    `ASPNETCORE_ENVIRONMENT=Development`, never reachable in a real
    deploy). This is a test seam, not a real onboarding flow — there is no
    real way to grant a user a non-empty branch scope today. If a real
    branch-assignment feature ships, this test should be rewritten against
    that instead.

### Known limitation

Catalog rename's "allowed" path can only be exercised through the
test-only seeding seam above — this is a real, documented product gap
(no branch persistence exists), not a testing shortcut around a feature
that actually works end to end through the product's own UI/API.

```yaml
schema: gentle-ai.verify-result/v1
evidence_revision: sha256:e26fedadab178f97dcafa1498aca1be3672775929582c58cf02f2b16c0cd96f6
verdict: fail
blockers: 1
critical_findings: 1
requirements: 5/6
scenarios: 8/9
test_command: dotnet test Commerce.sln && npm run test --prefix src/Commerce.Web -- --run
test_exit_code: 0
test_output_hash: sha256:913dcc75f5775e020950025a3d38e756216342ca308d3d013c4f03e0d9b58b7f
build_command: npm run build --prefix src/Commerce.Web
build_exit_code: 0
build_output_hash: sha256:fefae384cd6555d2827acdb69c1e4bf54de11dce6176bace7bf44d964ab1e367
```

## Verification Report

**Change**: commerce-web-routing
**Version**: N/A (no version field in spec)
**Mode**: Strict TDD

### Completeness
| Metric | Value |
|--------|-------|
| Tasks total | 29 |
| Tasks complete | 29 |
| Tasks incomplete | 0 |

### Build and Tests Execution
**Build**: PASSED
```text
$ npm run build (src/Commerce.Web)
tsc -b && vite build
49 modules transformed, built in 241ms, exit 0
```

**Tests**: 313 passed / 0 failed / 0 skipped (across three commands)
```text
$ dotnet test Commerce.sln
Commerce.Bootstrap.Tests.dll: 1 passed
Commerce.Upgrade.dll: 19 passed
Commerce.Integration.dll: 268 passed
Total: 288 passed, 0 failed, exit 0

$ npm run test -- --run (src/Commerce.Web, vitest)
Test Files: 11 passed (11)
Tests: 25 passed (25), exit 0

$ npm run test:e2e (Playwright) -- NOT RUN.
Reason: requires a live Postgres instance, a running Commerce.Cloud.Api built
with the SPA copied in (npm run test:e2e:build-backend-spa), and a running
local-ssl-proxy terminating HTTPS on :5443 in front of the API plain-HTTP
Kestrel (required because the sign-in cookie is CookieSecurePolicy.Always).
A Postgres container (incoders-commerce-postgres-1) was found already
running, but the Cloud.Api process and local-ssl-proxy were not, and this
verification pass did not stand up that harness. Reporting this explicitly
rather than fabricating a pass or silently skipping it. Task 3.6 is checked
in tasks.md but its all-specs-green claim is UNVERIFIED by this phase.
```

**Coverage**: Not available -- no coverage tool detected in the vitest/dotnet run output.

### Spec Compliance Matrix

The spec file (openspec/changes/commerce-web-routing/specs/web-app-routing/spec.md)
contains 6 requirements and 9 scenarios, counted directly from its
`### Requirement:` / `#### Scenario:` headings. The task brief that requested
this verification stated "12 Given/When/Then scenarios" -- that count does not
match the file. Recounted twice against the actual headings; the file has 9.

| Requirement | Scenario | Test | Result |
|-------------|----------|------|--------|
| Public Routes Render Without Auth Dependency | Unauthenticated visitor loads the public landing page | HomeScreen.test.tsx: renders without an AuthProvider and makes no auth/session request | COMPLIANT |
| Public Routes Render Without Auth Dependency | Public routes are addressable on hard refresh | none found -- no e2e/integration test exercises a full navigation reload against the built SPA and MapFallbackToFile | UNTESTED |
| Signed-In Visitor at / Sees the Public Page | Authenticated visitor navigates to / | HomeScreen.test.tsx: shows a visible link into the app when a user is signed in | COMPLIANT |
| Single Shared /login Route | Staff member signs in via /login | LoginRoute.test.tsx: navigates to resolveLandingPath(user) on a direct /login visit with no from state | COMPLIANT |
| Guarded Staff Routes Redirect on Unauthenticated Access | Unauthenticated deep link redirects to login | RequireAuth.test.tsx: redirects an unauthenticated visitor to /login and renders no guarded content (plus carries the originally-requested location in navigation state) | COMPLIANT |
| Guarded Staff Routes Redirect on Unauthenticated Access | Post-login return to originally-requested route | LoginRoute.test.tsx: returns to the originally-requested deep link after a successful sign-in | COMPLIANT |
| Reset-Password Uses a Path Param, Not a Query String | Path-param reset link resolves the token | ResetPasswordRoute.test.tsx: passes the path-param token to ResetPasswordScreen, plus AccountEndpointTests confirm-valid-token case (dotnet, passed) | COMPLIANT |
| Reset-Password Uses a Path Param, Not a Query String | Legacy query-string link is not supported post-deploy | ResetPasswordRoute.test.tsx: renders the invalid-link treatment for a bare /reset-password with no token, and makes no request | COMPLIANT |
| OrderScreen Behavior Is Unchanged | Order console behaves identically after routing is introduced | OrderScreen.test.tsx (unmodified, passed) plus git diff --stat confirms byte-identical file | COMPLIANT |

**Compliance summary**: 8/9 scenarios compliant, 1 untested.

### Correctness (Static Evidence)
| Requirement | Status | Notes |
|------------|--------|-------|
| react-router v7 declarative mode | Implemented | main.tsx wraps App in BrowserRouter; App.tsx uses Routes/Route, not createBrowserRouter |
| RequireAuth redirect semantics | Implemented | RequireAuth.tsx: user ? Outlet : Navigate to /login replace state from location -- matches design interface exactly, no blank render, no ?returnTo= (grepped codebase-wide: zero hits outside a rejected-alternative comment) |
| / never auto-redirects an authenticated visitor | Implemented | App.tsx / route renders HomeScreen unconditionally, outside RequireAuth; no guard wraps it |
| Reset-link path-param shape | Implemented | Account.cs line 233 emits /reset-password/{token}; ExtractToken marker is /reset-password/; no ?token= remains in live code (only in comments describing the retired/rejected shape) |
| Shared INVALID_RESET_LINK_MESSAGE constant | Implemented | ResetPasswordRoute.tsx imports the same named export from ResetPasswordScreen.tsx, which also throws it in its own catch branch -- single source, not a duplicated string |
| resolveLandingPath(user) seam | Implemented | src/routes/landing.ts, exported, unit-tested in landing.test.ts, called from LoginRoute.tsx |
| 5 untouched screens byte-identical | Implemented | git diff --stat against SignInScreen.tsx, ForgotPasswordScreen.tsx, RenewPasswordScreen.tsx, CatalogScreen.tsx, OrderScreen.tsx, and Program.cs returns empty output; git status --short shows none of them modified |
| 7 e2e edit sites | Implemented | sign-in.spec.ts x3 goto(/login), catalog.spec.ts x2 goto(/login), ordering.spec.ts x1 goto(/login) plus getByRole link name Orders -- all 7 confirmed by direct read |
| SPA hard-refresh addressability | Plausible, unverified at runtime | Program.cs MapFallbackToFile is unchanged (confirmed byte-identical) so the server-side mechanism predates this change, but no test in the repo performs a full-navigation reload against /login, /forgot-password, or /reset-password/:token to prove the router mounts the right screen after one |

### Coherence (Design)
| Decision | Followed | Notes |
|----------|-----------|-------|
| Declarative BrowserRouter, not framework mode | Yes | main.tsx / App.tsx |
| Route tree shape (public routes, guarded /app/*, catch-all to /) | Yes | App.tsx matches exactly |
| useOptionalAuth() non-throwing | Yes | AuthContext.tsx, 3 lines as specified |
| location.state.from, never ?returnTo= | Yes | Confirmed by code and grep |
| useResetToken.ts retired | Yes | File is deleted (git status shows D on it); no remaining call sites found |
| ResetPasswordScreen props unchanged, no test edit | Yes | token/onSuccess unchanged; its own test file was not part of this change diff |
| Container/presentational: existing screens stay router-free | Yes | All six existing screen test files pass unmodified (25 vitest tests, 11 files, 0 failures) |
| Work Unit 1 -> Unit 2 dependency ordering | Yes | Task file shows Unit 1 (1.1-1.17) fully preceding Unit 2 (2.1-2.6) |

### TDD Compliance
| Check | Result | Details |
|-------|--------|---------|
| TDD Evidence reported | Partial | No separate apply-progress artifact was available to this verify pass (no Engram tooling in this session; artifact store is openspec/disk-only and no apply-progress.md file exists on disk). RED/GREEN cycle evidence was instead read directly from tasks.md per-task annotations (e.g. 1.2 RED / 1.3 GREEN, 1.8 RED / 1.9 GREEN, 2.1 RED / 2.3 RED / 2.4 GREEN), a weaker substitute than a dedicated apply-progress table |
| All tasks have tests | Yes | Every RED task in tasks.md names a specific test file that exists on disk and was confirmed by direct read |
| RED confirmed (tests exist) | Yes | HomeScreen.test.tsx, landing.test.ts, RequireAuth.test.tsx, LoginRoute.test.tsx, ResetPasswordRoute.test.tsx all exist |
| GREEN confirmed (tests pass) | Yes | All 25 vitest tests pass on this run; all 288 dotnet tests pass on this run |
| Triangulation adequate | Yes | RequireAuth.test.tsx has 3 cases (redirect, outlet, state); LoginRoute.test.tsx has 4 cases; ResetPasswordRoute.test.tsx has 2 cases matching its 2 spec scenarios |
| Safety Net for modified files | Yes | ResetPasswordScreen.tsx was modified (export added) and its existing test file was not edited and still passes |

**TDD Compliance**: 5/6 checks fully passed, 1 partial (no dedicated apply-progress artifact to cross-reference)

---

### Test Layer Distribution
| Layer | Tests | Files | Tools |
|-------|-------|-------|-------|
| Unit/Integration (RTL) | 25 | 11 | Vitest + React Testing Library |
| Backend integration | 288 | 3 assemblies | xUnit + WebApplicationFactory + Postgres |
| E2E | 0 executed (7 call sites exist, unexecuted) | 3 spec files | Playwright -- harness not stood up this pass |
| Total executed | 313 | | |

---

### Assertion Quality
No tautologies, ghost loops, or assertion-without-production-call patterns found in the new test files (HomeScreen.test.tsx, RequireAuth.test.tsx, LoginRoute.test.tsx, ResetPasswordRoute.test.tsx, landing.test.ts). Every test calls real production code (render of the actual component, real useContext/useParams/useNavigate wiring) and asserts specific rendered content, hrefs, or navigation targets rather than smoke-only toBeInTheDocument() checks. fetch mocks are used only where AuthProvider/accountApi genuinely reach the network, at roughly a 1:1-1:2 mock-to-assertion ratio, not mock-heavy.

**Assertion quality**: All assertions verify real behavior

---

### Quality Metrics
**Linter**: Not run this pass (oxlint available via npm run lint, not executed -- out of the requested command list)
**Type Checker**: No errors (tsc -b is the first step of npm run build, which exited 0)

### Issues Found

**CRITICAL**:
1. Spec scenario "Public routes are addressable on hard refresh" (Requirement: Public Routes Render Without Auth Dependency) has no covering test at any layer. The design document's Testing Strategy table promised this as a Playwright E2E case (hard-refresh on /login and /reset-password/x serves the SPA), but none of the three e2e spec files (sign-in.spec.ts, catalog.spec.ts, ordering.spec.ts) contain a reload/hard-navigation assertion. Practical risk is likely low (Program.cs MapFallbackToFile is confirmed byte-unchanged, so the server mechanism itself predates this change), but per verification protocol an untested required scenario is a blocker, not a warning.

**WARNING**:
1. npm run test:e2e (Playwright, task 3.6) was not executed by this verification pass. A Postgres container was found running, but Commerce.Cloud.Api and the required local-ssl-proxy (HTTPS termination, needed for the CookieSecurePolicy.Always sign-in cookie) were not stood up. Task 3.6 is checked complete in tasks.md, but its all-specs-green claim, including the 7 retargeted /login call sites and the Orders link-selector change, is unverified by this phase, not disproven.
2. No dedicated apply-progress artifact was available to cross-reference RED/GREEN test-execution history; TDD evidence was reconstructed from tasks.md inline annotations instead, a weaker substitute per the strict-TDD verify protocol.
3. The task brief for this verification stated the spec contains "12 Given/When/Then scenarios." Direct recount of specs/web-app-routing/spec.md finds 6 requirements and 9 scenarios, not 12. Flagging the discrepancy rather than silently adopting the stated count.

**SUGGESTION**:
1. Consider adding one Playwright test (or a lightweight Vitest test using window.history plus a fresh router mount) that specifically proves a full-navigation reload at /login, /forgot-password, or /reset-password/:token resolves to the correct screen, to close the CRITICAL gap above with real runtime evidence instead of static inference from Program.cs.
2. npm run lint (oxlint) was not run this pass; consider including it in the standard verify command set for this stack alongside test/build.

### Verdict
FAIL -- 288 dotnet tests, 25 vitest tests, and the build all pass with zero regressions, and 8/9 spec scenarios have real passing covering tests, but one required scenario (SPA hard-refresh addressability) has no covering test anywhere in the codebase, and the full Playwright e2e suite (task 3.6) was not independently executed this pass.

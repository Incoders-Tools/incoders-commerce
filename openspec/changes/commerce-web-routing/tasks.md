# Tasks: Commerce Web Routing

## Review Workload Forecast

| Field | Value |
|-------|-------|
| Estimated changed lines | ~470 (design's own estimate: Unit 1 ~330, Unit 2 ~140) |
| Effective review budget (session default) | 1500 changed lines |
| 400-line budget risk (vs. skill default) | Medium (design's own figure, ~70 lines over the 400-line default) |
| Budget risk vs. the 1500-line session budget | Low — comfortably under |
| Chained PRs recommended | No (delivery strategy is `single-pr`; design's own recommendation is `single-pr` or `size:exception`, explicitly rejecting a chain because splitting the reset-link flip would reintroduce the query-string reader the locked decision retires) |
| Suggested split | Single PR, internally ordered so Unit 1 → Unit 2 land as sequential, individually-green commit groups |
| Delivery strategy | single-pr |
| Chain strategy | none needed (under budget) |

Decision needed before apply: No
Chained PRs recommended: No
400-line budget risk: Medium (vs. skill default) / Low (vs. session's 1500-line budget)

**Dependency ordering preserved as task order, not PR order**: Unit 1 (router dependency, route tree, guard, `HomeScreen`, e2e retargeting) MUST land first — it establishes the `/reset-password/:token` route slot that Unit 2 fills. Unit 2 (the reset-link path-param flip across `ResetPasswordRoute`, `Account.cs`, and `AccountEndpointTests.cs`) depends on Unit 1's route tree existing and MUST ship in the same commit set per the design's Migration/Rollout note: "web and API must ship together, since a new-shape link needs the new route and vice versa." This mirrors the design's Work Unit 1→2 dependency exactly.

**Test conventions verified, not assumed**: `src/Commerce.Web` uses Vitest + React Testing Library (`npm run test`), one `*.test.tsx` per screen/component. The backend uses xUnit via `dotnet test Commerce.sln`, with `AccountEndpointTests.cs` under `tests/Commerce.Integration` exercising `WebApplicationFactory` + live Postgres. Both follow strict RED/GREEN below. Playwright e2e (`npm run test:e2e`) is a separate runtime harness, exercised only at the end since it depends on both the built web app and the built API.

### Suggested Work Units

| Unit | Goal | Likely PR | Focused test command | Runtime harness | Rollback boundary |
|------|------|-----------|----------------------|-----------------|-------------------|
| 1 | `react-router` dependency, `useOptionalAuth`, `HomeScreen`, `RequireAuth`, `LoginRoute`, `ForgotPasswordRoute`, `AppLayout`, `landing.ts`, `main.tsx`/`App.tsx` route tree, e2e retargeting (7 edit sites) | Single PR, commits 1.x | `npm run test` (vitest, from `src/Commerce.Web`) | jsdom + RTL, `<MemoryRouter>`; no live backend | Revert; no schema/server change |
| 2 | `/reset-password/:token` route + `ResetPasswordRoute`, delete `useResetToken`, `Account.cs` reset-link line, `ExtractToken` marker, runbook doc — depends on Unit 1's route tree | Single PR, commits 2.x | `dotnet test tests/Commerce.Integration --filter FullyQualifiedName~Account` | `WebApplicationFactory` + live Postgres | Revert both sides together — two-sided contract, per proposal's Rollback Plan |

## Unit 1: Router Shell — Public Routes, Auth Guard, Home Screen

- [x] 1.1 Add `react-router` `^7` to `src/Commerce.Web/package.json` dependencies; install and pin the exact resolved 7.x version — spec: web-app-routing "Public Routes Render Without Auth Dependency" (prerequisite for all following route work).
- [x] 1.2 RED: `src/Commerce.Web/src/screens/HomeScreen.test.tsx` — mounts `<HomeScreen>` inside a bare `<MemoryRouter>` with **no `AuthProvider`**; asserts it renders without throwing, shows the `Commerce` heading and a `Sign in` link to `/login`, and that a stubbed global `fetch` is never called — spec: web-app-routing "Public Routes Render Without Auth Dependency" / "Unauthenticated visitor loads the public landing page".
- [x] 1.3 GREEN: add `useOptionalAuth(): AuthContextValue | null` to `src/Commerce.Web/src/auth/AuthContext.tsx` (`useContext(AuthContext) ?? null`, never throws); create `src/Commerce.Web/src/screens/HomeScreen.tsx` — `<main>` shell with `<h1>Commerce</h1>`, one subtitle line, `<Link to="/login">Sign in</Link>`; imports only `@/components/ui/button` + `react-router`.
- [x] 1.4 RED: extend `HomeScreen.test.tsx` — wrapped in an `AuthContext.Provider` with a signed-in user value, `HomeScreen` renders unchanged plus a visible `<Link to="/app">` app CTA — spec: web-app-routing "Signed-In Visitor at `/` Sees the Public Page".
- [x] 1.5 GREEN: extend `HomeScreen.tsx` — render the app CTA only when `useOptionalAuth()` returns a non-null value.
- [x] 1.6 RED: `src/Commerce.Web/src/routes/landing.test.ts` — `resolveLandingPath(user)` returns `'/app'` for any signed-in user shape — spec: design "Post-auth destination seam" (ADR-009 Phase D named seam).
- [x] 1.7 GREEN: create `src/Commerce.Web/src/routes/landing.ts` — `export function resolveLandingPath(_user: SignedInResponse): string { return '/app' }`.
- [x] 1.8 RED: `src/Commerce.Web/src/routes/RequireAuth.test.tsx` — over the real route tree with `<MemoryRouter initialEntries={['/app/orders']}>`: unauthenticated visitor is redirected to `/login`, no guarded content renders, and the navigation carries `state={{ from: location }}`; an authenticated visitor renders the guarded `<Outlet/>` content instead — spec: web-app-routing "Guarded Staff Routes Redirect on Unauthenticated Access" / "Unauthenticated deep link redirects to login".
- [x] 1.9 GREEN: create `src/Commerce.Web/src/routes/RequireAuth.tsx` per the design's interface: `const { user } = useAuth(); const location = useLocation(); return user ? <Outlet/> : <Navigate to="/login" replace state={{ from: location }}/>`.
- [x] 1.10 RED: `src/Commerce.Web/src/routes/LoginRoute.test.tsx` — entering at a guarded deep link (e.g. `/app/orders`) with no session redirects to `/login`; after a mocked successful `signIn`, `navigate` is called with `{ replace: true }` targeting the originally-requested deep link, not `/app`; a direct visit to `/login` (no `from` state) after sign-in navigates to `resolveLandingPath(user)` instead — spec: web-app-routing "Post-login return to originally-requested route".
- [x] 1.11 GREEN: create `src/Commerce.Web/src/routes/LoginRoute.tsx` (`SignInScreen` + "Forgot password?" link + post-auth `navigate(from ?? resolveLandingPath(user), { replace: true })`), `src/Commerce.Web/src/routes/ForgotPasswordRoute.tsx` (`onBackToSignIn={() => navigate('/login')}`), and `src/Commerce.Web/src/routes/AppLayout.tsx` (header, display name, Sign out, `NavLink` tabs replacing today's `tab` state, `<Outlet/>`) — thin wrappers only, existing screen props/tests untouched.
- [x] 1.12 GREEN: replace `src/Commerce.Web/src/App.tsx`'s ternary with the route tree — `AuthProvider` wrapping `<Routes>`: `/` → `HomeScreen`, `/login` → `LoginRoute`, `/forgot-password` → `ForgotPasswordRoute`, `<Route element={<RequireAuth/>}>` → `<Route path="/app" element={<AppLayout/>}>` with index redirect to `catalog`, plus `catalog`, `orders`, `password`, and `*` → `<Navigate to="/" replace/>`. (`/reset-password/:token` and `/reset-password` are wired in Unit 2.) Wrap `<App/>` in `<BrowserRouter>` in `src/Commerce.Web/src/main.tsx`.
- [x] 1.13 Regression guard: run `npm run test` from `src/Commerce.Web` and confirm all six existing `screens/*.test.tsx` files (`SignInScreen`, `ForgotPasswordScreen`, `ResetPasswordScreen`, `RenewPasswordScreen`, `CatalogScreen`, `OrderScreen`) pass **unmodified** — spec: web-app-routing "OrderScreen Behavior Is Unchanged"; design "props and behavior identical" for all six screens.
- [x] 1.14 Update `src/Commerce.Web/e2e/sign-in.spec.ts` — retarget all 3 `page.goto('/')` call sites to `page.goto('/login')` (since `/` is now the public page, not the sign-in form).
- [x] 1.15 Update `src/Commerce.Web/e2e/catalog.spec.ts` — retarget both 2 `page.goto('/')` call sites to `page.goto('/login')`.
- [x] 1.16 Update `src/Commerce.Web/e2e/ordering.spec.ts` — retarget its 1 `page.goto('/')` call site to `page.goto('/login')`, AND change the `getByRole('button', { name: 'Orders' })` selector to `getByRole('link', { name: 'Orders' })` (the Orders nav tab becomes a `<NavLink>` under `AppLayout`).
- [x] 1.17 Confirm Unit 1 green: `npm run test` and `npm run build` (from `src/Commerce.Web`) before starting Unit 2.

## Unit 2: Reset-Link Path-Param Flip (depends on Unit 1's route tree)

- [x] 2.1 RED: `src/Commerce.Web/src/routes/ResetPasswordRoute.test.tsx` — `/reset-password/abc` passes `token="abc"` to `ResetPasswordScreen`; bare `/reset-password` (no param) renders the invalid-link treatment via `INVALID_RESET_LINK_MESSAGE`, issuing no request — spec: web-app-routing "Reset-Password Uses a Path Param, Not a Query String" / "Legacy query-string link is not supported post-deploy".
- [x] 2.2 GREEN: export `INVALID_RESET_LINK_MESSAGE` from `src/Commerce.Web/src/screens/ResetPasswordScreen.tsx` and use it in its own error branch (props unchanged, no test edit needed); create `src/Commerce.Web/src/routes/ResetPasswordRoute.tsx` (`const { token } = useParams()`; renders `<ResetPasswordScreen token onSuccess={() => navigate('/login', { replace: true })}/>` when present, the invalid-link card with `INVALID_RESET_LINK_MESSAGE` when absent); wire `/reset-password/:token` and `/reset-password` to this route component in `App.tsx`; delete `src/Commerce.Web/src/auth/useResetToken.ts` and remove its remaining call sites.
- [x] 2.3 RED: update `tests/Commerce.Integration/AccountEndpointTests.cs`'s `ExtractToken` helper — change the marker literal from `"token="` to `"/reset-password/"` (terminator scan on whitespace/`"` stays unmodified; token is already base64url-safe, no URL-encoding needed). Running `dotnet test tests/Commerce.Integration --filter FullyQualifiedName~Account` now fails against the unmodified endpoint (still emits `?token=`) — confirms the test actually exercises the new shape rather than silently still passing.
- [x] 2.4 GREEN: `src/Commerce.Cloud.Api/Endpoints/Account.cs:233` — change `$"{emailOptions.PublicBaseUrl}/reset-password?token={token}"` to `$"{emailOptions.PublicBaseUrl}/reset-password/{token}"`.
- [x] 2.5 Update `deploy/staging-runbook.md:68` — documented reset-link shape to the path-param form.
- [x] 2.6 Confirm Unit 2 green: `dotnet test tests/Commerce.Integration --filter FullyQualifiedName~Account`.

## Final Verification

- [x] 3.1 Confirm `OrderScreen.tsx` and the other four unmodified screens (`SignInScreen`, `ForgotPasswordScreen`, `RenewPasswordScreen`, `CatalogScreen`) are byte-unchanged — regression guard against the proposal's Success Criteria ("`OrderScreen` behavior is byte-for-byte unchanged").
- [x] 3.2 Confirm `src/Commerce.Cloud.Api/Program.cs` is unchanged — `MapFallbackToFile("index.html")` already serves every client route; no server route change is needed or introduced.
- [x] 3.3 Run `dotnet test Commerce.sln` (full suite, zero regressions).
- [x] 3.4 Run `npm run test` (vitest, from `src/Commerce.Web`).
- [x] 3.5 Run `npm run build` (from `src/Commerce.Web`).
- [x] 3.6 Run `npm run test:e2e` (Playwright, from `src/Commerce.Web`) — all specs green, including the 7 retargeted call sites across `sign-in.spec.ts`, `catalog.spec.ts`, and `ordering.spec.ts`.

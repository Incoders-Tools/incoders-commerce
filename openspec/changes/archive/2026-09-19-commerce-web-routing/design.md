# Design: Commerce.Web Client-Side Routing Shell

## Technical Approach

`react-router` v7 in declarative mode (`<BrowserRouter>` + `<Routes>`), mounted in
`main.tsx`. `App.tsx` stops being a ternary and becomes the route tree only.

The organising rule is **container/presentational**: every existing screen stays
router-free and keeps its current props verbatim, so all six `*.test.tsx` files
remain valid. Router coupling is isolated in thin *route components* under
`src/routes/` that read `useParams`/`useNavigate`/`useLocation` and hand plain
values down. `OrderScreen.tsx` is not opened at all — only its mount point moves.

`AuthProvider` wraps the whole tree, including `/`. This is verified, not assumed:
`AuthContext.tsx` is pure `useState` with no `useEffect`, no bootstrap request, and
no session probe — `signIn`/`signOut` only fire on user action. There is no cost to
route around, so no lazy/split auth-presence mechanism is invented. The spec's
"zero dependency on auth machinery" is instead made **executable** (see the
`useOptionalAuth` row).

## Architecture Decisions

| Decision | Choice and rationale | Rejected alternative |
|---|---|---|
| **Router mode and package** | Single dependency `react-router` `^7` (v7 merges `react-router-dom`), declarative `<BrowserRouter>`. Confirmed compatible: `package.json` has React `^19.2.8`, Vite `^8.3.0`, TypeScript `~6.0.2`. Pin the exact 7.x minor at install time. | **Framework mode / `createBrowserRouter` + data APIs** — framework mode needs a Node server, which ADR-007 forbids (static SPA in Cloud.Api `wwwroot`). Data-router loaders are rejected in the guard row below; without loaders, `createBrowserRouter` buys nothing over `<BrowserRouter>` here. |
| **Route tree** | Flat public routes + one pathless guard route wrapping one layout route. `/` → `HomeScreen`; `/login` → `LoginRoute`; `/forgot-password` → `ForgotPasswordRoute`; `/reset-password/:token` → `ResetPasswordRoute`; `/reset-password` (no param) → same route component, invalid-link state; then `<Route element={<RequireAuth/>}>` → `<Route path="/app" element={<AppLayout/>}>` with index redirect to `catalog`, plus `catalog`, `orders`, `password`. `*` → `<Navigate to="/" replace/>`. `AppLayout` is today's `AuthenticatedApp` chrome (header, display name, Sign out) with the `tab` state replaced by `<NavLink>` + `<Outlet/>`. | **Guarded routes at top level (`/catalog`, `/orders`)** — no single place to mount the guard or the shell chrome, and it pollutes the root namespace that Phase D's customer routes will need. **Keeping `tab` state under one `/app` URL** — defeats the whole point: bookmarkability. |
| **Guard implementation** | `RequireAuth` is a component route: `const { user } = useAuth(); const location = useLocation(); return user ? <Outlet/> : <Navigate to="/login" replace state={{ from: location }} />`. `LoginRoute` reads `location.state?.from?.pathname` and, after a successful `signIn`, calls `navigate(from ?? resolveLandingPath(user), { replace: true })`. `replace` on both legs keeps Back from bouncing through `/login`. | **A loader-based redirect** — loaders run outside the React tree and cannot read `AuthContext`; auth state here lives in React state, so a loader would have to duplicate it in a module-level singleton. Rejected as a real architectural mismatch, not a style preference. **`?returnTo=` query param** — puts a redirect target in a user-editable URL (open-redirect surface) for no gain; `location.state` is in-memory only. |
| **Post-auth destination seam** | `resolveLandingPath(user: SignedInResponse): string` in `src/routes/landing.ts`, returning `'/app'` today. It exists so ADR-009 Phase D branches on *identity*, not on URL — satisfying the locked "one `/login` for everyone" decision with one named function instead of a second login route. | **Hardcoding `/app`** — the decision would then be an intention with nothing in the code expressing it. **`/staff/login` + `/customer/login`** — explicitly rejected by the locked decision. |
| **`/` for a signed-in visitor** | `HomeScreen` renders the hero unconditionally and appends a `<Link to="/app">` CTA only when a user is present, read via a new non-throwing `useOptionalAuth()` (3 lines in `AuthContext.tsx`: `useContext(AuthContext) ?? null`). `/` is never redirected away from. The payoff is testability: `HomeScreen` renders **with no `AuthProvider` at all**, so `HomeScreen.test.tsx` can mount it bare and assert it neither throws nor calls `fetch` — the spec's "no auth/session request" becomes an executing assertion instead of a code-review promise. | **`useAuth()` directly in `HomeScreen`** — throws outside a provider, which makes the provider-free render test impossible; the zero-dependency requirement would then be unverifiable. **A separate lazy auth-presence probe** — over-engineering: `AuthContext` costs one `useState`, verified above. |
| **`HomeScreen` content** | Deliberately a shell: `<main>` with an `<h1>Commerce</h1>`, one subtitle line, a `<Link to="/login">Sign in</Link>`, and the conditional app CTA. No imagery, copy, or layout system. Imports only `@/components/ui/button` + `react-router`. | Writing real marketing content — out of scope per the proposal; it would also be thrown away by the content/design change. |
| **`useResetToken` retirement** | Delete `src/Commerce.Web/src/auth/useResetToken.ts`. `ResetPasswordRoute` does `const { token } = useParams()` and renders `<ResetPasswordScreen token={token} onSuccess={() => navigate('/login', { replace: true })}/>`. `ResetPasswordScreen`'s `{ token, onSuccess }` props are **unchanged** — its test file needs no edit. When `token` is absent (bare `/reset-password`, i.e. an old `?token=` link), the route renders the invalid-link card using `INVALID_RESET_LINK_MESSAGE`, a constant exported from `ResetPasswordScreen.tsx` and used by its own error branch, so the two treatments cannot drift. No query string is ever read. | **Moving `useParams` into `ResetPasswordScreen`** — couples a presentational screen to the router and invalidates its existing tests for no benefit. **Submitting an empty token to get the real 401** — a pointless request, and it assumes the endpoint's empty-string behavior. **`history.replaceState` (today's `clear()`)** — the router owns history now. |
| **Reset-link shape (API + test, one commit)** | `Account.cs:233` → `$"{emailOptions.PublicBaseUrl}/reset-password/{token}"`. No URL-encoding is needed and this is verified at line 227: the token is base64url-normalised (`Replace('+','-').Replace('/','_').TrimEnd('=')`), so its alphabet is `[A-Za-z0-9-_]` — no `/`, `+`, or `=` can leak into the path segment. `AccountEndpointTests.ExtractToken`'s marker changes from `"token="` to `"/reset-password/"`; its existing terminator scan (whitespace or `"`) still works unmodified. Both edits ship in the same commit as the web change. | **Accepting both shapes for a window** — explicitly rejected by the locked decision; the 1-hour TTL *is* the window. **Leaving `ExtractToken` alone** — it would match nothing and fail loudly, but only after a confusing diff; the proposal already flags the silent-pass risk. |
| **E2E retargeting** | Four call sites, not three: `sign-in.spec.ts` ×3, `catalog.spec.ts` ×2, `ordering.spec.ts` ×1 all change `page.goto('/')` → `page.goto('/login')`. **Additionally** `ordering.spec.ts:32` clicks `getByRole('button', { name: 'Orders' })`; the nav tab becomes a `<NavLink>`, so that selector becomes `getByRole('link', { name: 'Orders' })`. Assertions on `Sign out`, the `Commerce` heading, and every `#field` locator are untouched. | Styling the nav as `<button onClick={navigate}>` purely to preserve one selector — hiding real navigation behind a button is exactly the addressability defect this change exists to fix. |

## Data Flow

```text
Cold visitor
  GET /  ──► MapFallbackToFile("index.html") ──► main.tsx
     └─► BrowserRouter ─► "/" ─► HomeScreen   [useOptionalAuth() -> null; zero fetch]

Guarded deep link, no session
  GET /app/orders ─► RequireAuth: user === null
     └─► <Navigate to="/login" replace state={{ from: "/app/orders" }}>
         LoginRoute ─► SignInScreen ─► signIn() ok
            └─► navigate(from ?? resolveLandingPath(user), { replace: true })
                ─► /app/orders          (Back does NOT return to /login)

Reset link
  Account.cs  {PublicBaseUrl}/reset-password/{base64url-token}
     └─► "/reset-password/:token" ─► useParams().token
         ─► <ResetPasswordScreen token onSuccess={() => navigate('/login', replace)}>
  legacy  /reset-password?token=...  ─► matches "/reset-password" (no param)
     └─► INVALID_RESET_LINK_MESSAGE   (identical to an expired-token result)
```

## File Changes

| Path | Action | Purpose |
|---|---|---|
| `src/Commerce.Web/package.json` | Modify | `+ "react-router": "^7.x"` (dependencies) |
| `src/Commerce.Web/src/main.tsx` | Modify | Wrap `<App/>` in `<BrowserRouter>` |
| `src/Commerce.Web/src/App.tsx` | Modify | `AuthProvider` + `<Routes>` only; `AuthenticatedApp`/`SignedOutApp`/`Root` removed |
| `src/Commerce.Web/src/routes/AppLayout.tsx` | Create | Header, display name, Sign out, `NavLink` tabs, `<Outlet/>` (moved from `AuthenticatedApp`) |
| `src/Commerce.Web/src/routes/RequireAuth.tsx` | Create | `<Outlet/>` or `<Navigate to="/login" state={{from}}/>` |
| `src/Commerce.Web/src/routes/LoginRoute.tsx` | Create | `SignInScreen` + "Forgot password?" link + post-auth navigate |
| `src/Commerce.Web/src/routes/ForgotPasswordRoute.tsx` | Create | `onBackToSignIn={() => navigate('/login')}` |
| `src/Commerce.Web/src/routes/ResetPasswordRoute.tsx` | Create | `useParams` token; invalid-link state when absent |
| `src/Commerce.Web/src/routes/landing.ts` | Create | `resolveLandingPath(user)` — Phase D seam |
| `src/Commerce.Web/src/screens/HomeScreen.tsx` | Create | Public placeholder shell + conditional app CTA |
| `src/Commerce.Web/src/auth/AuthContext.tsx` | Modify | Export `useOptionalAuth()` (non-throwing) |
| `src/Commerce.Web/src/auth/useResetToken.ts` | **Delete** | Superseded by `useParams` |
| `src/Commerce.Web/src/screens/ResetPasswordScreen.tsx` | Modify | Export `INVALID_RESET_LINK_MESSAGE`; use it in the error branch. Props unchanged. |
| `src/Commerce.Web/src/screens/{SignIn,ForgotPassword,RenewPassword,Catalog,Order}Screen.tsx` | **Unchanged** | Props and behavior identical |
| `src/Commerce.Web/src/routes/*.test.tsx`, `screens/HomeScreen.test.tsx` | Create | See Testing Strategy |
| `src/Commerce.Cloud.Api/Endpoints/Account.cs` (line 233) | Modify | Path-segment reset link |
| `tests/Commerce.Integration/AccountEndpointTests.cs` (line 586) | Modify | `ExtractToken` marker → `"/reset-password/"` |
| `src/Commerce.Web/e2e/{sign-in,catalog,ordering}.spec.ts` | Modify | `goto('/login')`; `ordering.spec.ts` Orders selector → `link` |
| `deploy/staging-runbook.md` (line 68) | Modify | Documented link shape |
| `src/Commerce.Cloud.Api/Program.cs` | **Unchanged** | `MapFallbackToFile` already serves every path |

## Interfaces / Contracts

```tsx
// src/Commerce.Web/src/auth/AuthContext.tsx  (addition only)
export function useOptionalAuth(): AuthContextValue | null {
  return useContext(AuthContext) ?? null   // never throws: lets / render provider-free
}

// src/Commerce.Web/src/routes/RequireAuth.tsx
export function RequireAuth() {
  const { user } = useAuth()
  const location = useLocation()
  return user ? <Outlet /> : <Navigate to="/login" replace state={{ from: location }} />
}

// src/Commerce.Web/src/routes/landing.ts
export function resolveLandingPath(_user: SignedInResponse): string { return '/app' }
```

## Testing Strategy

| Layer | What to test | Approach |
|---|---|---|
| Unit | `HomeScreen` renders **without any `AuthProvider`** and issues zero `fetch` calls; shows the app CTA only when a user is present | Vitest + RTL, bare `<MemoryRouter>`; `vi.stubGlobal('fetch', spy)` asserted never called |
| Unit | `RequireAuth` redirects an unauthenticated `/app/orders` to `/login`, renders no guarded content, and carries `state.from`; renders `<Outlet/>` when a user exists | `<MemoryRouter initialEntries={['/app/orders']}>` over the real route tree |
| Unit | Post-login return: entering at a guarded deep link, then a successful mocked `signIn`, lands on that deep link — not `/app` | RTL + mocked `accountApi.signIn` |
| Unit | `/reset-password/abc` passes `abc` to `ResetPasswordScreen`; bare `/reset-password` renders `INVALID_RESET_LINK_MESSAGE` and makes no request | RTL |
| Unit | The six existing `screens/*.test.tsx` pass **unmodified** | Regression gate on the props-unchanged decision |
| Integration | The reset email body contains `/reset-password/{token}` and `ExtractToken` recovers a token that the confirm endpoint accepts | `dotnet test tests/Commerce.Integration --filter ~Account` |
| E2E | `/login` signs in and reaches the app; `/` is the public page; hard-refresh on `/login` and `/reset-password/x` serves the SPA | Playwright, `test:e2e:build-backend-spa` (real `MapFallbackToFile`) |

## Threat Matrix

| Boundary | Applicability |
|---|---|
| Routing | **Applicable** — client-side routes and a new auth guard. Safe behavior: the guard **redirects**, never blank-renders, and defaults to denial (only a non-null `user` reaches `<Outlet/>`); the post-login destination comes from in-memory `location.state`, never from a query param, so no attacker-supplied redirect target exists; `*` resolves to `/`, never to a guarded route; the reset token is a path segment whose base64url alphabet cannot escape the segment. No server route changes — `MapFallbackToFile` is untouched, so no new server surface is exposed. RED tests: unauthenticated-deep-link redirect, post-login return-to-origin, bare `/reset-password` invalid-link, provider-free `HomeScreen` render. |
| Documentation-like paths | N/A — no file-classification boundary. |
| Git repository selection / Commit state / Push state / PR commands | N/A — no VCS or PR automation. |
| Shell / subprocess | N/A — none introduced. |

## Migration / Rollout

No data migration, no schema, no deploy-config change. The one coordination
constraint is the reset link: web and API must ship **together**, since a new-shape
link needs the new route and vice versa. In-flight `?token=` links land on the
invalid-link card within the 1-hour TTL — the accepted transition window. Rollback
is the proposal's: revert the commit(s); nothing is persisted.

## Work Units

| Unit | Scope | Budget | Test / runtime boundary | Rollback |
|---|---|---|---|---|
| 1 | Router dep, `main.tsx`, `App.tsx` route tree, `AppLayout`, `RequireAuth`, `LoginRoute`, `ForgotPasswordRoute`, `landing.ts`, `HomeScreen`, `useOptionalAuth`, route tests, e2e retargeting | ~330 | `npm run build && npx vitest run`, `npm run test:e2e` | Revert |
| 2 | `/reset-password/:token` route + `ResetPasswordRoute`, delete `useResetToken`, `Account.cs` link, `ExtractToken`, runbook | ~140 | `dotnet test --filter ~Account` | Revert both sides together |

Decision needed before apply: Yes
Chained PRs recommended: No
400-line budget risk: Medium

**Why not chained**: ~470 authored lines, just over the 400 budget, and unit 2 is
only ~140 of them. Splitting the reset-link flip into its own PR is *possible*
(unit 1 leaves `/reset-password?token=` working through the untouched
`useResetToken` for one PR's lifetime), but that reintroduces the query-string
reader the locked decision retires and makes an old-shape link work in exactly one
intermediate build. One review of a single-package, additive, fully-tested change
is the cheaper and more honest option; recommend `single-pr` or `size:exception`.
If the strategy is `auto-chain`, the split above is the ordering to use.

## Open Questions

- [ ] None blocking. Two recorded assumptions: `resolveLandingPath` ignores its
      argument today because `SignedInResponse` carries no role/account-type
      discriminator yet — it is a named seam for ADR-009 Phase D, not dead code;
      and `HomeScreen`'s "zero auth dependency" is enforced as *renders with no
      provider and makes no request*, which is the executable form of the
      requirement — the module still imports `useOptionalAuth` from
      `AuthContext.tsx`, and nothing in the authenticated shell (`AppLayout`,
      `CatalogScreen`, `OrderScreen`) is reachable from its import graph.

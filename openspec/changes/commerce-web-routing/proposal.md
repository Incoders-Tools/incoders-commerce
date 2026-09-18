# Proposal: Commerce.Web Client-Side Routing Shell

## Intent

`src/Commerce.Web` has no router. `App.tsx` switches views from state: `Root()` checks `useResetToken()` (a `?token=` query-param read), then renders `AuthenticatedApp` or `SignedOutApp`. Consequences today:

| Gap | Effect |
|---|---|
| No public page | Every unauthenticated visitor lands on `SignInScreen` — the product has no front door |
| No URLs | Nothing is bookmarkable, shareable, or deep-linkable; back/forward do nothing |
| Auth is a ternary, not a guard | "Protected" means "the branch that renders"; there is no redirect semantics |

This change introduces the routing **shell** only: real URLs, a public `/`, and a real auth guard. It does not build the marketing content or the customer ordering experience.

## Scope

### In Scope

- Add `react-router` v7 (declarative/`BrowserRouter` mode, not framework mode) to `src/Commerce.Web/package.json`. v7 is the current line, React 19-compatible (project is React 19.2.8 / Vite 8); framework mode is rejected because ADR-007 keeps Commerce.Web a static SPA embedded in Cloud.Api's `wwwroot` — no SSR, no separate Node server.
- Public routes: `/` (new `HomeScreen` placeholder shell — hero structure only), `/login` (`SignInScreen`), `/forgot-password`, `/reset-password/:token`.
- **Deliberate reversal**: `/reset-password?token=` becomes a path param `/reset-password/:token`, superseding the no-router decision in `commerce-password-recovery/design.md`. This is a coordinated web+API change (see Affected Areas), not a frontend-only rename.
- Guarded staff routes behind an auth check that **redirects** to `/login` when unauthenticated: catalog, order console, renew-password.

### Out of Scope

- Marketing content, imagery, animations, copy for `/` — a later content/design change.
- Everything ADR-009 defers to Phase D: customer-facing ordering UI, guest-vs-registered branching, customer login/account routes, pricing display, `CustomerOrderingAccess` route disposition.
- `OrderScreen.tsx` behavior. Only its mount point in the route tree changes; it stays staff-only behind the guard.
- Domain/subdomain work. ADR-006 is inert until the apex is purchased; this is web-app-internal routing on one origin.
- Server route changes: `MapFallbackToFile("index.html")` already serves any path, and the Vite dev proxy is unaffected.

## Capabilities

### New Capabilities

- `web-app-routing`: addressable client routes, the public landing route, and redirect-on-unauthenticated guard semantics for staff routes.

### Modified Capabilities

- None. No existing spec states the reset-link URL shape (`user-credentials` covers storage/hashing/sign-in only); the link format lives in code and change docs.

## Decisions (confirmed by the user)

| Decision | Answer |
|---|---|
| Reset-link transition | Accept the 1-hour token TTL as the transition window — no dual-format support. In-flight `?token=` links simply expire naturally; the user re-requests. |
| Signed-in visitor at `/` | Always shows the public page, for anyone, logged in or not — with a visible link into the app for an already-authenticated visitor. `/` is never auto-redirected away from. |
| Login route naming | **One single `/login` for everyone** — staff today, registered customers once ADR-009's Phase D ships. What renders after successful sign-in depends on *who* authenticated (role/account type), not on which URL they used to get there. |

## Approach

Route tree replaces `App.tsx`'s conditional; `main.tsx` mounts the router provider. Public routes render with zero dependency on auth machinery so `/` loads for a first-time visitor. A guard element wraps the authenticated branch and issues a redirect (preserving intended destination) instead of rendering nothing. `useResetToken` is retired in favor of `useParams`.

## Affected Areas

| Area | Impact | Description |
|---|---|---|
| `src/Commerce.Web/package.json` | Modified | Add `react-router` v7 |
| `src/Commerce.Web/src/main.tsx` | Modified | Router provider |
| `src/Commerce.Web/src/App.tsx` | Modified | Replaced by route tree |
| `src/Commerce.Web/src/screens/HomeScreen.tsx` | New | Public placeholder shell |
| `src/Commerce.Web/src/auth/` | New/Modified | Route guard; `useResetToken.ts` retired |
| `src/Commerce.Cloud.Api/Endpoints/Account.cs:233` | Modified | `{PublicBaseUrl}/reset-password?token={token}` → path segment |
| `tests/Commerce.Integration/AccountEndpointTests.cs:584` | Modified | `ExtractToken` parses the literal marker `"token="`; a path param breaks it |
| `src/Commerce.Web/e2e/{sign-in,ordering,catalog}.spec.ts` | Modified | All three `page.goto('/')` then expect the sign-in form; `/` is now the public page, so each must target `/login` |
| `deploy/staging-runbook.md:68` | Modified | Documented link shape |
| `src/Commerce.Cloud.Api/Program.cs` | **Unchanged** | `MapFallbackToFile` already covers all routes |

## Risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| In-flight reset emails sent before deploy carry `?token=` and 404 after | Medium | Design must decide: accept both shapes for a transition window, or accept the 1-hour token TTL as the window |
| `ExtractToken` test silently still passes on a malformed link | Medium | Update the integration test's marker with the endpoint change, same commit |
| Guard renders nothing instead of redirecting on unauthenticated deep link | Medium | Explicit redirect-to-`/login` scenario in the spec |
| `/` transitively imports auth/session code and fails for a cold visitor | Medium | Public routes must not import the authenticated shell; assert in a test |
| Every Playwright e2e breaks: they all `goto('/')` expecting sign-in | High | Known and enumerated above; update the three specs in the same slice |
| Scope creep into Phase D ordering UI | Medium | ADR-009 deferrals listed as out-of-scope above |

## Rollback Plan

Revert the web commits (router dep, `main.tsx`, `App.tsx`, `HomeScreen`, guard) and the `Account.cs` link line plus its test. No schema, no migration, no server route, no deploy config changes — revert is complete and has no persisted state to undo. If only the API side must revert, the web app must revert with it: the link shape is a two-sided contract.

## Dependencies

- ADR-007 (embedded SPA, single origin) and ADR-009 (Phase D deferrals) — constraints, already merged.
- `react-router` v7 npm package.

## Success Criteria

- [ ] An unauthenticated visitor at `/` sees the public placeholder without any auth request.
- [ ] `/login`, `/forgot-password`, `/reset-password/:token` are directly addressable on a hard refresh (SPA fallback verified).
- [ ] An unauthenticated request for a staff route redirects to `/login`, not a blank render.
- [ ] After sign-in, catalog / order console / renew-password are reachable by URL and `OrderScreen` behavior is byte-for-byte unchanged.
- [ ] The reset email link and `AccountEndpointTests` agree on the new path-param shape; `dotnet test` and `npm run build && npx vitest run` pass.
- [ ] `npm run test:e2e` passes with the three specs retargeted to `/login`.

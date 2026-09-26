# Frontend Modernization (organic, no SDD)

## Objective

Rebuild `src/Commerce.Web` (Vite + React 19 + react-router 7 + Tailwind 4,
embedded in `Commerce.Cloud.Api`'s wwwroot) into an enterprise-grade,
responsive UI: real navigation shell, account-scoped user menu, list/card
view switching, a 3-way theme system (light / dark / org-custom via
sysadmin-assigned color picker), and an organization settings surface
(logo, date format, geolocation, usage plan). Framework stays Vite/React —
Next.js was explicitly rejected (breaks the single-container deploy shape
for no SSR benefit in an authenticated admin app). Component base is
shadcn/ui (already partially present: `components/ui/{button,card,input,label}.tsx`).

## Why

Current UI (per exploration, 2026-09-24): no sidebar/nav menu, no dark mode,
no design tokens, no responsive layout work, "change password" is a loose
top-level tab instead of account-menu-scoped, `OrganizationsScreen.tsx` is a
45-line unformatted file with only create+list (no settings: logo/theme/date
format/geo/plan). This blocks the user's stated goal of an enterprise-caliber
admin panel.

## Constraints

- No SDD: organic task-by-task work, user explicitly chose this route.
- Periodically re-check against `openspec/specs/` to confirm we're not
  drifting from what the product already defines; adjust tasks if gaps or
  contradictions surface.
- TDD mode: unresolved — no explicit project/session TDD config found for
  the frontend; will confirm before first implementation task and record
  here.
- Delivery: work-unit commits on a feature branch (currently on
  `feat/product-update-service` — branch off before first write per ODD
  policy, since this is unrelated feature work).

## Baseline (exploration findings, 2026-09-24)

- Routing: `App.tsx`, public (`/`, `/login`, `/forgot-password`,
  `/reset-password/:token`, `/order`) + protected under `/app`
  (`RequireAuth` → catalog/orders/password; `RequireAdmin` →
  customers/users/branches; `RequireSystemAdmin` → organizations).
- Layout: `routes/AppLayout.tsx` — header + horizontal `NavTab` bar +
  `Outlet`. No sidebar, no account dropdown, no theming.
- Change password: `screens/RenewPasswordScreen.tsx`, mounted as a loose
  `NavTab` at `/app/password` alongside Catalog/Orders — needs to move
  into an account menu.
- `OrganizationsScreen.tsx`: create + list only, no settings fields.
- No CSS variables / design tokens, no `tailwind.config`, `color-scheme:
  light` hardcoded in `index.css`. shadcn `ui/*` components use direct
  Tailwind classes, not semantic tokens yet.
- shadcn/ui pattern already correctly bootstrapped (`cva` + `cn`) — extend,
  don't replace.

## Task list

- [x] T1. Design token foundation: semantic CSS vars (shadcn-style
      `--background`, `--foreground`, `--primary`, etc.) in `index.css`,
      Tailwind v4 `@theme` mapping, remove hardcoded `color-scheme: light`.
      Route: direct inline (1 file, mechanical once tokens are chosen).
- [x] T2. Theme system: `ThemeProvider` + 3-way switcher (light/dark/
      custom), Vercel-style UI, persisted per-user (localStorage keyed by
      user id). "Custom" theme is a placeholder consumer of org-level
      tokens until T5 exists. Route: delegated direct (provider + switcher
      component + hook = 2+ non-trivial files).
- [x] T3. App shell redesign: enterprise nav (sidebar or responsive
      top-nav — decide during task), responsive breakpoints, account
      dropdown menu (user display name, Change password, Sign out). Move
      `RenewPasswordScreen` off the top-level tab bar into that menu.
      Route: delegated direct.
- [x] T4. Reusable list/card view-switch component, applied first to one
      data-heavy screen (Customers or Catalog TBD) as the reference
      implementation, then rolled out. Route: delegated direct.
- [x] T4b. Roll the `components/data/*` layer (PageHeader + DataToolbar +
      ViewSwitch + DataView + `useViewPreference`) out to `CustomersScreen`,
      `UsersScreen` and `BranchesScreen` — each dropping its
      `mx-auto … max-w-*` centered card, getting its own
      `view:<screen>` preference key (`view:customers`, `view:users`,
      `view:branches`) and real columns from `api/types.ts`.
      Auth screens (`SignInScreen`, `ForgotPasswordScreen`,
      `ResetPasswordScreen`, `RenewPasswordScreen`) are explicitly OUT of
      scope — the narrow centered card is the correct pattern there.
      Route: delegated direct (3 non-trivial screens + tests).
- [x] T4c. Finish the rollout: `PriceListsScreen` onto the same
      `components/data/*` layer, with a `view:price-lists` preference key and
      real columns from `PriceListRecord` / `PriceListEntryRecord`. Split out
      of T4b as its own slice because the screen carries a second nested list
      (price entries) plus the supplier-import surface, which needs its own
      column/expansion design rather than a mechanical migration.
      `OrganizationsScreen` is deliberately NOT part of this rollout — it is
      absorbed into T5, which rebuilds that screen wholesale (settings form,
      colour picker, logo) rather than migrating the current 45-line
      create+list stub twice.
      Route: delegated direct.
- [x] T5. Organization branding, minimal scope (user decision 2026-09-25:
      "lo mas simple posible, a futuro ampliamos"): only `logoUrl`
      (optional absolute http/https URL, no upload) and `primaryColor`
      (optional `#rrggbb`) on the Organization. Date format, geolocation and
      usage plan are explicitly deferred. Steps:
      T5a backend — spec requirement in `openspec/specs/`, entity fields,
      numbered SQL migration, sysadmin-gated read/update endpoints, and a
      read endpoint for the signed-in user's own organization branding;
      integration tests including cross-tenant denial.
      T5b UI — "Edit branding" from the Organizations list opens a
      full-screen `FormPage` with logo URL + color picker and a preview.
      Route: delegated direct (writer).
- [x] T6. Wire the authenticated user's resolved organization to the
      "custom" theme option (fetch org theme on session load, feed
      `ThemeProvider`). Depends on T2 + T5. Also fixes review WARNING
      `R3-branding-load-failure-save-clears`: when loading branding fails
      the form must not let Save wipe the stored values.
- [ ] T6b. T6 review follow-ups: WARNING
      `R3-custom-availability-validation-mismatch` (ThemeSwitcher.tsx:26 —
      the Custom option's availability check and the theme's color
      validation disagree, so Custom can be enabled for a color that
      applies nothing), WARNING `R3-stale-branding-on-identity-change`
      (OrganizationBrandingProvider.tsx:40-42 — the previous user's
      branding stays visible while the next identity loads), SUGGESTION
      `R3-logo-failed-never-resets` (AppLayout.tsx:109-112).
- [x] T7. Navigation icons: add `lucide-react` (the shadcn/ui icon
      standard) and give every primary nav item and account-menu entry an
      identifying icon next to its text. Replace the hand-drawn
      `MenuIcon`/`ChevronDownIcon`. Accessible names MUST stay exactly the
      current text (`Catalog`, `Orders`, `Customers`, `Users`, `Branches`,
      `Price lists`, `Organizations`, `Change password`, `Sign out`) —
      icons are `aria-hidden`. Route: delegated direct (writer).
- [x] T8. Organizations screen layout fix (UI half of T5, no backend):
      reformat the minified `OrganizationsScreen.tsx`, list on the
      `components/data/*` layer (`view:organizations`), create form behind
      a "New organization" action rendered full-width with visible
      `<Label>`s and proper field spacing. Labels MUST keep the E2E names
      `Organization name`, `Administrator email`, `Administrator password`,
      button `Create organization` (`e2e/system-admin.spec.ts`) — if the
      form moves behind a toggle, update that spec to open it first.
      Settings fields (logo/theme/date/geo/plan) stay in T5: exploration on
      2026-09-25 found NO spec and NO entity/API field for any of them
      (`Organization` has only `Id`, `Name`), so they need a spec change
      first. Route: delegated direct (writer).
- [x] T9. Full-screen forms instead of inline expansions: add a shared
      `components/layout/FormPage` shell (page header with back action,
      full-width body, footer actions) and move the boxed inline editors
      onto it — Catalog "Edit code" (`DataView.renderExpanded`) and the
      Price lists entries/history panels. Follows the established
      `CustomersScreen`→`CustomerForm` state-swap pattern (no per-item
      routes yet); migrate `CustomerForm` onto the same shell. Preserve the
      `Edit code` button name and form field labels used by
      `e2e/catalog.spec.ts`; update E2E where the flow changes and
      typecheck `e2e/` explicitly. Route: delegated direct (writer).
- [x] T10. Forms actually use the width + dark-mode-safe selects: found by
      the parent while verifying T9. `CustomerForm` keeps ~20 fields in a
      single `max-w-lg` column inside the full-screen shell, and its three
      native `<select>`s (plus `OrderLinesEditor`'s) hardcode `bg-white
      border-neutral-300`, so they stay white in dark mode. Add a token-based
      `components/ui/select.tsx` (native select styled like `Input`), use it
      everywhere, and lay `CustomerForm` out as grouped sections on a
      responsive multi-column grid. Route: delegated direct (writer; 3+
      non-trivial files).

## Progress

- 2026-09-24: Task file created after exploration (mapping fork). No
  source writes yet.
- 2026-09-24: Engram mirror write failed (`multiple active runtime
  sessions match`) — mirror is PENDING, resync when available.
- 2026-09-24: Branched `feat/frontend-modernization` off `incoders/main`
  (not off the unrelated in-flight `feat/product-update-service`, which
  carries unrelated POS-update-detection commits). TDD resolved: global
  config has Strict TDD Mode enabled; frontend runner is `vitest`
  (`npm run test` = `vitest run`).

- 2026-09-24: T1 done (direct inline, 1 file + 4 mechanical component
  swaps). `index.css` now defines the shadcn-style semantic token set
  (background, foreground, card/card-foreground, primary/primary-foreground,
  secondary/secondary-foreground, muted/muted-foreground, accent/
  accent-foreground, destructive/destructive-foreground, border, input,
  ring) for `:root` (light) and `.dark` (class selector, not
  `prefers-color-scheme` — future ThemeProvider owns the toggle), mapped to
  Tailwind v4 utilities via `@theme inline`. **Correction (2026-09-24,
  review finding R3-task-log-color-scheme-claim): `color-scheme: light`
  was NOT removed from `:root` as originally logged here — it remains
  unchanged as the `:root` default, and `.dark` correctly overrides it to
  `color-scheme: dark`. Behavior is correct; only this log entry was
  wrong.** Updated `components/ui/{button,card,input,label}.tsx`
  to consume the semantic utilities instead of raw `neutral-*`/`red-*`/
  `white` classes; no visual/behavioral change intended. Added RED-then-
  GREEN vitest + Testing Library tests for all 4 primitives (asserting
  `toHaveClass('bg-primary')` etc., confirmed failing against the old raw
  classes before the swap). Full suite: 70/70 passing. `npm run lint`: only
  pre-existing warnings, no new issues. `npm run build`: tsc + vite build
  succeed. "Custom" org theme intentionally left unimplemented — only
  token names are ready for a future runtime override.

- 2026-09-24: T1 `gentle-ai review assess` (base-ref `incoders/main`,
  committed-only, untracked excluded): risk `medium` (executable_change on
  `button.test.tsx`), 300 changed lines, `review_due: false`
  (`under_budget`). Reviewed boundary stays at pre-T1 until the running
  slice total reaches ~400 lines or a high-risk commit lands; T1's 300
  lines carry forward into the next assessment.

- 2026-09-24: T2 done (delegated-direct route, 4 non-trivial source files +
  2 test files). Added `src/theme/ThemeProvider.tsx` (context + `useTheme()`
  hook, `Theme = 'light' | 'dark' | 'custom'`) and
  `src/theme/organizationTheme.ts` (`getOrganizationThemeOverrides()`
  placeholder, always `null` today — explicit T6 integration point,
  documented inline). Applies/removes the `.dark` class on
  `document.documentElement` (consumed by T1's tokens); "custom" without
  org overrides intentionally falls back to light (no `.dark` class) rather
  than doing nothing. Persists per authenticated user via
  `localStorage` keyed `theme:${userId}` (read through `useOptionalAuth()`
  so it also works, key `theme:anonymous`, on public routes with no
  `AuthProvider` gating). Added `src/components/theme/ThemeSwitcher.tsx`, a
  Vercel-style 3-option segmented control (`role="radiogroup"`, inline SVG
  icons — no icon library was installed, so none was added for 3 glyphs),
  built on the existing `cn()` helper, mounted in `AppLayout.tsx`'s header
  (temporary; comment marks T3 to move it into the future account dropdown).
  `ThemeProvider` wraps `<Routes>` inside `AuthProvider` in `App.tsx`, so
  it's available on both public and authenticated routes.
  TDD: strict RED→GREEN confirmed for both new files (import-resolution
  failure before the implementation existed, documented as the RED
  evidence, then implemented to GREEN). 9 new tests (7 `ThemeProvider`, 2
  `ThemeSwitcher`) covering: dark class applied/removed, custom-without-
  overrides stays light, localStorage persistence and restore-on-remount
  keyed per user, per-user isolation, anonymous fallback key, and switcher
  rendering/selection. Full suite: 79/79 passing (70 pre-existing + 9 new).
  `npm run lint`: exit 0, only pre-existing warning categories plus one new
  `react(only-export-components)` warning on `ThemeProvider.tsx` — same
  pattern already present on `AuthContext.tsx` (context + hook co-exported
  from one file), not a new category. `npm run build`: `tsc -b && vite
  build` succeed clean.

- 2026-09-24: `gentle-ai review` (lineage `review-51be7d67b03768e2`,
  base-ref pre-T1 `incoders/main` → T1+T2 combined, 17 files / 764 lines,
  risk medium, lens `review-reliability` only): **approved**, acknowledged,
  authority burned. Reviewed boundary now advances to the T2 commit
  (`316359a`). 4 non-blocking advisory findings (no correction opened):
  - WARNING `R3-identity-change-untested`: `ThemeProvider`'s re-read effect
    on user-identity change (sign-in/out swapping accounts on a mounted
    provider) is untested; a regression could leak the previous account's
    theme, plus a one-frame flash on identity change. Follow-up, not fixed
    now.
  - WARNING `R3-per-user-isolation-test-weak`: the "separate themes per
    user" test seeds one user with the DEFAULT_THEME value, so it would
    still pass even if per-user key scoping were broken. Follow-up: seed
    both users with non-default, distinct values.
  - SUGGESTION `R3-dark-class-applied-post-mount`: `.dark` is applied in a
    post-render `useEffect`, so a user with a persisted "dark" preference
    sees a flash of the light theme on full page load before React mounts.
    Follow-up: apply the class synchronously before paint (e.g. inline
    script or `useLayoutEffect`).
  - SUGGESTION `R3-task-log-color-scheme-claim`: corrected above.
  These are tracked here as future follow-up work, not re-opened against
  this closed review.

- 2026-09-24: Bugfix (unrelated to T1-T6, found while helping the user get
  the local stack running to test T1/T2): `src/Commerce.Web/vite.config.ts`
  proxied `/account`, `/catalog`, `/orders`, `/sync` to
  `http://localhost:5080`, but `Commerce.Cloud.Api` always binds `8080` by
  default (`Program.cs:28`, `PORT` env var or "8080" when unset — nothing
  sets `PORT` for a local `dotnet run`). Nothing ever listened on 5080, so
  every sign-in attempt through Vite (`:5173`) hit `ECONNREFUSED`
  regardless of Docker/timing state. Fixed both targets to `8080`.
  Separately, `deploy/dev/run-all.ps1` never launched the HTTPS proxy step
  its own launcher `.bat` already advertises on screen
  (`https://localhost:5443/login`) — and it matters functionally, not just
  cosmetically: the sign-in cookie is `CookieSecurePolicy.Always`
  (`Program.cs:164,188`), so a browser silently drops it over plain HTTP,
  meaning a "successful" login via bare `http://localhost:5173` would not
  actually persist a session. Added a 4th launcher step:
  `npx local-ssl-proxy --source 5443 --target 5173` (fronting Vite itself,
  not the raw API, so hot-reload dev editing and real cookie auth both
  work at once — the API-fronting `--target 8080` pattern in
  `README.md`/E2E docs is for the separately-built static SPA, a different
  workflow). Added a `-NoHttps` switch. Verified end-to-end: API `:8080`
  healthy, Vite `:5173` proxy now returns the API's real response instead
  of `ECONNREFUSED`, HTTPS proxy `:5443/login` returns 200. Not
  TDD-applicable (dev-environment/launcher config, no application
  behavior to unit-test). Also confirmed and explained to the user,
  without fixing (out of scope): the WPF POS desktop window showing "an
  old version" is expected, not a bug — its modernization work
  (`b488bf7 feat(pos): modernize desktop shell and themes`) lives only on
  the unrelated, unmerged `feat/product-update-service` branch, not on
  `main`, which `feat/frontend-modernization` was branched from.
- 2026-09-24: Recreated `sysadmin@incoders.local` and
  `admin@vacaverde.local` (same credentials as originally provisioned)
  after the local Postgres data was lost — `deploy/dev/compose.yaml`'s
  `postgres` service has no named volume, only a read-only bind mount for
  `init-rls.sql`, so `docker compose down` (even without `-v`) destroys
  the data since it only ever lived in the container's own writable
  layer. Corrected here since an earlier message in this session wrongly
  told the user the accounts would survive a plain `down`.

- 2026-09-24: Bugfix commit `c89ee50`. `gentle-ai review assess` against
  the correct last-reviewed boundary (`--base-ref 316359a`, the T2
  commit): risk medium (executable_change on `run-all.ps1`), 3 files /
  119 lines, `review_due: false` (`under_budget`). Reviewed boundary
  stays at `316359a` until the running slice reaches ~400 lines again.

- 2026-09-24: Branch-target correction. User couldn't open a PR ("no veo
  PR a dev... falla cuando intento pushear, le quiere pegar a main"):
  this repo's real convention (confirmed via `gh pr list`) is feature
  branches → PR into `dev`; `dev` → `main` is promoted separately
  ("chore: promote dev to main"). `feat/frontend-modernization` had been
  branched off `incoders/main` (a deliberate earlier choice to avoid an
  unrelated in-flight branch), missing 6 commits already on `dev`:
  `main` is a clean ancestor of `dev` (no divergence), so merged
  `incoders/dev` in (commit `aaee9ac`) rather than rebasing, to avoid a
  force-push on an already-pushed branch. One conflict, in
  `deploy/dev/run-all.ps1`: `dev`'s `11df256` had already added a more
  complete, tested version (builds the SPA statically into
  `Commerce.Cloud.Api/wwwroot`, proper env vars, health-check polling,
  HTTPS proxy fronting the API on 5443, auto-opens the browser) — this
  supersedes the ad-hoc HTTPS-proxy-fronting-Vite step added in this
  session's earlier bugfix commit. Resolved by taking `dev`'s canonical
  version whole; the `vite.config.ts` 5080→8080 fix from that same
  bugfix commit remains valid and kept (still used by a bare `npm run
  dev` Vite session, a separate/faster iteration workflow this official
  launcher doesn't cover since it only serves a rebuilt static SPA, no
  hot reload). Other 5 commits (POS desktop theme incl.
  `Themes/VacaVerdeTheme.xaml` — existing prior art for an org-branded
  theme, relevant reference for future T5/T6 web work; POS update-service
  detection; docs) merged cleanly, no conflicts, do not touch
  `src/Commerce.Web`. Re-ran `deploy/dev/run-all.ps1 -NoPos -NoBrowser`
  end to end: Postgres healthy, SPA rebuilt from current branch (T1+T2
  included) and copied into `wwwroot`, API healthy, HTTPS proxy up,
  sign-in verified 200 over `https://localhost:5443` with the sysadmin
  account.

- 2026-09-24: T3 done (delegated-direct route, 2 non-trivial source files +
  2 test files — confirmed trigger: `AppLayout.tsx` full rewrite +
  new `AccountMenu.tsx`). User complaint driving this task (Spanish): "el
  front lo sigo viendo horrible sin maqueta... no aprovechamos la totalidad
  de la pantalla" — no real shell, everything loose, not using full screen
  width. Read `auth/AuthContext.tsx` first and confirmed `SignedInResponse`
  (`api/types.ts`) has no `email` field, only `displayName` — the account
  menu header and trigger use `displayName` only, documented inline in
  `AccountMenu.tsx` so a future reader doesn't go looking for an `email`
  that doesn't exist. Confirmed no Radix/headless-ui dependency exists in
  `package.json` and added none.
  New files: `src/Commerce.Web/src/components/layout/AccountMenu.tsx`,
  `src/Commerce.Web/src/components/layout/AccountMenu.test.tsx`,
  `src/Commerce.Web/src/routes/AppLayout.test.tsx` (new — none existed
  before). Rewrote `src/Commerce.Web/src/routes/AppLayout.tsx` in place.
  Structure: `AppLayout` is now a flex shell — a `<aside>` sidebar
  (`bg-card`, `border-border` tokens from T1) fixed/off-canvas and
  translated off-screen (`-translate-x-full`) below the `md:` breakpoint,
  toggled by a header hamburger button (`useState` bool, no external state
  lib, closes on nav-link click and on the mobile scrim overlay click); at
  `md:` it becomes `static`/`w-64 shrink-0` and sits in normal flex flow
  next to the content column, which drops the old `mx-auto max-w-3xl`
  centered-column artifact entirely — content area is now `w-full flex-1`
  so it fills all remaining width. Nav items mirror the exact gating
  `AppLayout` already used (UI-only mirror; `App.tsx`'s
  `RequireAuth`/`RequireAdmin`/`RequireSystemAdmin` untouched, confirmed
  not modified): Catalog/Orders unconditional,
  Customers/Users/Branches behind `hasPermission(user,
  Permission.ManageUsers)`, Organizations behind `user?.isSystemAdmin`.
  "Change password" removed from the nav list entirely and now lives only
  inside `AccountMenu`. `AccountMenu` is a hand-built dropdown (no
  Radix/headless-ui — none was installed, none was added): a header button
  showing `user.displayName` with `aria-haspopup="menu"`/
  `aria-expanded`, and a `role="menu"` panel with a display-name header,
  a `Link` (react-router, not an external lib) to `/app/password`, the
  existing `ThemeSwitcher` (moved out of the header where T2 had mounted
  it temporarily — that mount point and its "T3 will move this" comment
  are both gone now), and a "Sign out" button reusing `useAuth().signOut()`
  unchanged. Open/close state is a local `useState`; a `useEffect` (only
  active while `open`) attaches `keydown`/`mousedown` document listeners
  for Escape-to-close and click-outside-to-close, cleaned up on close/
  unmount — no new dependency, following the same hand-rolled-primitive
  precedent `ThemeSwitcher` already set for icons.
  TDD: strict RED confirmed for both new test files before writing any
  implementation — `AccountMenu.test.tsx` failed on unresolved import
  (`./AccountMenu` didn't exist yet); `AppLayout.test.tsx` (5 of 6 cases)
  failed against the pre-T3 `AppLayout.tsx` (old flat `NavTab` bar,
  `Change password` still present as a nav link, no account-menu trigger
  button, no mobile-sidebar toggle) — captured verbatim in this session's
  transcript. Implemented to GREEN, one follow-up correction during GREEN:
  the mobile-toggle test initially queried `getByRole('navigation')`
  (the inner `<nav>`, whose classes never change) instead of the `<aside>`
  that actually carries the transform classes — fixed the test to query
  the `<aside>` via `container.querySelector`, not a production-code
  change. 11 new tests (5 `AccountMenu`, 6 `AppLayout`) covering: nav
  visibility for a plain authenticated user (Catalog/Orders only, no
  Change-password link in the nav), a `ManageUsers` user (additionally
  Customers/Users/Branches, still no Organizations), a system admin
  (Organizations too); no `max-w-3xl` column present; mobile sidebar
  toggle via the hamburger button; account-menu trigger shows the display
  name; dropdown opens on click and shows display name + a `/app/password`
  `menuitem` link + the `ThemeSwitcher` `radiogroup`; closes on Escape;
  closes on outside click; "Sign out" calls the mocked `signOut()`. Full
  suite: 90/90 passing (79 pre-existing + 11 new). `npm run lint`: exit 0,
  same pre-existing warning categories only (no new ones — `AccountMenu.tsx`
  and the rewritten `AppLayout.tsx` triggered no new warnings). `npm run
  build`: `tsc -b && vite build` succeed clean (330.84 kB / 100.79 kB gzip
  JS bundle, 17.55 kB / 4.27 kB gzip CSS).

- 2026-09-24: `gentle-ai review` #2 (lineage `review-924803f734cd5ae0`,
  base-ref = T2's acknowledged tree, 29 files / 2547 lines, risk **high**
  via `hot_path` on `odd/tasks/product-update-service.md`, all 4 lenses):
  **approved** after one bounded correction, acknowledged, authority
  burned. The correction was NOT in this feature's frontend work — the
  reliability lens found a CRITICAL defect in
  `src/Commerce.Updater/ReleaseDiscovery.cs`, code that arrived via the
  `dev` merge (PR #70, POS update service). Verified independently before
  accepting: `System.Text.Json` overwrites the manifest records'
  collection initializers when the payload carries explicit nulls, so
  `"packages": null`, `"architectures": null`, a null package entry, or a
  null package `"architecture"` each threw `NullReferenceException`;
  `CheckForUpdates` only converted IO/JSON exceptions, so the NRE escaped
  into `MainWindow`'s constructor (`MainWindow.xaml.cs:92`, called
  synchronously) and crashed POS startup — directly contradicting
  `docs/pos-product-updates.md:25`'s documented non-goal "Blocking offline
  sales because update detection failed". Reproduced as 4 RED tests
  (all `NullReferenceException`) before fixing. User explicitly authorized
  taking this fix in this branch rather than deferring it, because `dev`
  is about to be promoted to `main`. Fix committed as `6672267`
  (79 lines, within the declared 80-line correction plan; budget was 200):
  null-checked collections, skip null package entries,
  `NormalizeArchitecture` accepts null. `dotnet test Commerce.Upgrade`:
  31/31 green.
  Non-blocking advisory findings recorded for later (none reopen this
  review): `R1-001` (risk, package-selection suggestion),
  `R2-repair-button-now-opens-settings` (WARNING),
  `R2-unchecked-compat-trust-fields` (WARNING),
  `R2-update-outcome-doc-code-drift` (WARNING),
  `R2-vite-comment-misattributes-proxy`, `R2-duplicated-status-builders`,
  `R2-mutable-update-result-field`, `R2-theme-registry-duplicated`. Of
  these, only `R2-vite-comment-misattributes-proxy` was fixed immediately,
  since it flagged a comment this session had just written that became
  wrong after the `dev` merge (the HTTPS proxy fronts the API, not Vite).
- 2026-09-24: Workspace hygiene. The "13 pending changes" the user saw in
  their git client were not files — the working tree was clean. They were
  13 unpushed *commits* measured against the wrong upstream: creating the
  branch with `git checkout -b ... incoders/main` set its tracking ref to
  `incoders/main`, which is also why pushes targeted `main`. Repointed the
  upstream to `incoders/feat/frontend-modernization` (local config only,
  no commit). Also committed `.codegraph/.gitignore`: CodeGraph ships a
  229-byte self-ignoring file (`*` plus `!.gitignore`) that is meant to be
  tracked so its 18 MB local `codegraph.db` stays ignored — committing it
  is what clears the last untracked entry.

- 2026-09-24: **Branching convention changed.** From now on this feature
  works directly on `dev` — no per-feature branch, no PR. Work-unit commits
  land on `dev` and are pushed there (`git push`). The earlier
  `feat/frontend-modernization` → PR-into-`dev` flow (PR #72, merged) is
  superseded; T4 onwards commits straight to `dev`.

- 2026-09-24: T4 done (delegated-direct route, 5 new source files + 1
  rewritten screen + 4 new test files + 1 extended test file). User
  complaint driving it (Spanish): the app "no se parece en nada a un
  sistema" and "no aprovechamos la totalidad de la pantalla" — T3 made the
  shell full-width, but every screen was still wrapped in
  `<Card className="mx-auto mt-8 w-full max-w-2xl">`, so content stayed
  narrow and centered inside a wide shell.
  New reusable layer in `src/Commerce.Web/src/components/data/` (new folder,
  mirroring the `components/layout/` convention T3 established):
  - `PageHeader.tsx` — `title` / optional `description` / optional `actions`
    slot; stacks on mobile, actions move right from `sm:` up.
  - `DataToolbar.tsx` — client-side search `Input` (label is `sr-only`,
    id via `useId`) + `ViewSwitch`, plus a `children` slot for future extra
    filters; column on mobile, one row from `sm:` up.
  - `ViewSwitch.tsx` — exports `DataViewMode = 'table' | 'cards'` and a
    segmented `role="radiogroup"` control with `aria-checked` buttons and
    inline SVG icons (no icon library added — same precedent as T2's
    `ThemeSwitcher`, whose exact class set and markup shape this copies so
    both read as one design system). Native `<button>`s keep it tab- and
    Enter/Space-operable with no custom key handling.
  - `DataView.tsx` — generic `DataView<T>` over `items` + `columns`
    (`DataViewColumn<T>`: `key`, `header`, `cell`, plus `hideOnMobile` /
    `hideInCards`) + `getRowKey`, rendering either a table or a responsive
    card grid, and owning the three states: `loading` (`role="status"`),
    empty (`emptyMessage`, never a blank area) and populated. Optional
    `renderActions` (per-item buttons) and `renderExpanded` (inline form
    under the item, as a `colSpan` row in the table and inside the card in
    the grid). Deliberately NOT a table framework: no sorting, pagination
    or column resizing — only what these screens actually need.
  - `useViewPreference.ts` — `[view, setView]` persisted at
    `view:<screenKey>` (e.g. `view:catalog`), with the same tolerant
    localStorage handling as `theme/ThemeProvider.tsx` (try/catch on read
    and write, corrupt/unknown values fall back to `'table'`).
  `screens/CatalogScreen.tsx` rewritten as the reference implementation:
  the `mx-auto mt-8 w-full max-w-2xl` Card wrapper is gone, replaced by a
  full-width `<section className="flex w-full flex-col gap-6">` with
  `PageHeader` + error alert + `DataToolbar` + `DataView`. Columns come
  from the real `PresentationRecord` fields (`api/types.ts`): Name,
  Identification code (`'No code'` placeholder kept verbatim so existing
  assertions stay meaningful), Quantity behavior (mapped through a new
  `QUANTITY_BEHAVIOR_LABELS` record over the numeric `QuantityBehavior`
  enum) and Last updated (`updatedAtUtc`, `toLocaleDateString`, `'—'` on an
  unparseable value); the last two are `hideOnMobile`. Search is a
  `useMemo` client-side filter over the already-loaded list by name or
  identification code — no new endpoint, `fetch` is still called exactly
  once. Edit-code flow is unchanged in behavior: the button moved into
  `renderActions` and the `IdentificationCodeForm` into `renderExpanded`,
  so it works identically in both views. Error text now uses the
  `text-destructive` token instead of the leftover raw `text-red-600`.
  TDD: strict RED confirmed twice. (1) All 4 new `components/data/*` test
  files failed on unresolved imports before the implementations existed.
  (2) The 4 new `CatalogScreen` cases (full-width/no-`max-w-2xl`, search
  filter, no-match empty state, card-view switch + persistence across
  remount) failed against the pre-T4 screen, while the 2 other new cases
  (empty catalog, editing from the card view) passed already — kept anyway
  as regression coverage for behavior T4 must not break. 25 new tests
  (4 `ViewSwitch`, 5 `useViewPreference`, 6 `DataView`, 4
  `DataToolbar`/`PageHeader`, 6 `CatalogScreen`). **No existing test was
  deleted or weakened**; `CatalogScreen.test.tsx` gained a second
  `labelled` fixture and a `localStorage.clear()` in `afterEach` (needed
  now that the view preference persists between cases) — all 3 original
  cases still assert exactly what they did before and still pass unmodified
  against the new markup. Full suite: **115/115 passing** (90 pre-existing +
  25 new). `npm run lint`: exit 0, only pre-existing warning categories —
  no new warnings from any `components/data/*` file (`CatalogScreen`'s
  `set-state-in-effect` warning pre-dates T4). `npm run build`: `tsc -b &&
  vite build` clean (337.08 kB / 102.32 kB gzip JS, 20.84 kB / 4.86 kB gzip
  CSS).

- 2026-09-24: T4 `gentle-ai review` (lineage `review-f4066cafa0bbc71a`,
  base-ref = pre-T4 commit, 12 files / 1001 lines, risk medium, lens
  `review-reliability`): **approved**, acknowledged, authority burned. Four
  non-blocking advisory findings. One was fixed immediately —
  `R3-expanded-vacuous-assertion` (WARNING): the "renders expanded content
  under the matching item only" test asserted the absence of
  `expanded beta`, text the callback never produces for any row, so it
  would have passed even if the component repeated one expanded node under
  every row. Replaced with a match count plus a companion test that renders
  expanded content for both rows, then mutation-checked both: injecting the
  scoping bug (`renderExpanded?.(items[0])` in the table branch) fails both,
  and reverting restores green. Suite now 116/116 across 34 files.
  Remaining advisory findings left as follow-up:
  `R3-cards-title-empty-columns`, `R3-new-columns-unasserted`,
  `R3-storage-failure-untested`.
- 2026-09-24: `dev` branch protection. The first direct push to `dev` was
  rejected (`GH006 ... Changes must be made through a pull request`) even
  though both the classic branch-protection and rulesets APIs returned
  empty — the token lacks admin read on protection settings, so enforcement
  was only visible by attempting the push. The user removed the protection
  rule, and the direct-to-`dev` workflow now works as intended. The global
  `branch-strategy` skill gained a hard rule plus a gate row for a
  push-protected integration branch: report it, fall back to a short-lived
  branch and PR, never force-push, never change protection settings.

- 2026-09-24: T4b done (delegated-direct route, 3 rewritten screens + 2
  extended test files + 1 new test file). Rolled the T4 `components/data/*`
  layer onto `CustomersScreen`, `UsersScreen` and `BranchesScreen`, following
  `CatalogScreen.tsx` as the reference implementation. **No file under
  `components/data/` was modified** — the layer covered all three screens as
  built.
  - `CustomersScreen.tsx`: dropped the `mx-auto mt-8 w-full max-w-3xl` Card
    wrapper for a full-width `<section>`; `PageHeader` ("Customers" + the
    "New customer" button in the actions slot); columns from the real
    `CustomerRecord` type — Name (`displayName`), Kind (`customerKind`),
    Status (`isEnabled`), Tax ID (`taxId`, `hideOnMobile`), Phone (`phone`,
    `hideOnMobile`); `renderActions` carries the existing Edit and
    "Issue ordering access" buttons. The one-time issued credential
    (`data-testid="issued-credential"`) and the full-screen `CustomerForm`
    takeover for create/edit are both unchanged — the form is long, not an
    inline row edit, so it deliberately still replaces the screen. Error text
    moved from the raw `text-red-600` to the `text-destructive` token.
  - `UsersScreen.tsx`: was 15 lines of ~1 KB each; reformatted and migrated.
    Columns from `UserSummary` — Email, Roles (`roleNames.join`), Status
    (`isRevoked`, `hideOnMobile`). "Save roles", the per-user replacement
    password `Input` and "Force reset" all live in `renderActions`, so they
    work identically in both layouts. **Pre-existing quirk preserved
    verbatim and now documented inline**: "Save roles" sends the *create
    form's* checked `roles`, not the row's own `user.roleNames` — changing it
    would be a functional change, which this presentation migration is not.
  - `BranchesScreen.tsx`: was a single ~1 KB line; reformatted and migrated.
    Columns from `BranchSummary` — Branch name, Identifier (`branchId`,
    mono, `hideOnMobile`).
  - **Create forms deliberately stay permanently visible** on Users and
    Branches rather than moving behind a "New user"/"New branch" toggle:
    `e2e/admin-console.spec.ts` fills `User email` / `User password` /
    `Branch name` straight after navigating to each screen, so a toggle would
    have silently broken that real-backend journey. Branches' single-field
    form fits in `PageHeader`'s action slot; Users' larger form sits in a
    bordered panel under the header. Reasoning is recorded inline in both
    files.
  TDD: strict RED confirmed for all three before any implementation —
  19 of the 34 cases across the three files failed against the pre-T4b
  screens (every new T4b-specific case: columns, search filter, no-match
  empty state, empty state, view switch + persistence, per-screen key
  isolation, and Customers' full-width case). The remaining new cases
  (Users/Branches full-width, and the two "still works from the card view"
  cases) passed already and were kept as regression guards, same precedent as
  T4. Additionally mutation-checked the per-screen preference key: pointing
  `BranchesScreen` at `useViewPreference('customers')` fails 2 tests,
  reverting restores green — so the key-isolation assertions can genuinely
  fail. **No existing test was deleted or weakened.** Two existing
  assertions were consciously tightened, both because the new markup made the
  old query ambiguous or vacuous: `CustomersScreen`'s edit-form case now
  matches `/^edit$/i` instead of `/edit/i` (the row also renders
  "Issue ordering access", which the looser pattern would have matched
  non-deterministically), and the two "from the card view" cases now assert
  no `role="table"` plus a `data-view-card` count, so they actually prove
  they are exercising the card layout. 29 new tests (10 Customers,
  8 Users, 11 Branches — `BranchesScreen.test.tsx` is new, the screen had no
  test file at all before). Full suite: **145/145 passing across 35 files**
  (116/34 before). `npm run lint`: exit 0, no new warnings — the
  `set-state-in-effect` entries now reported on `UsersScreen` and
  `BranchesScreen` were both present on the baseline too (verified by
  re-running lint against a stashed tree). `npm run build`: `tsc -b && vite
  build` clean (340.12 kB / 102.89 kB gzip JS, 21.00 kB / 4.90 kB gzip CSS).

- 2026-09-25: **Per-row role editor in `UsersScreen`** (commit `a426cb3`,
  direct inline, 1 source + 1 test file). Product decision by the user:
  instead of removing the broken "Save roles" button, each row now owns its
  role selection. Fixes the verified destructive bug at the old
  `UsersScreen.tsx:176` — `onClick={() => void updateUserRoles(user.userId,
  roles)}` sent the CREATE form's checked roles to whichever row was clicked,
  overwriting that user's permissions with an unrelated selection (or
  clearing them outright when nothing was checked). The comment that
  documented this as "pre-existing behavior, preserved verbatim by T4b" is
  gone together with the bug. New `rowRoles: Record<string, string[]>` state
  is re-seeded from the server's `roleNames` on every load; each row renders
  the three `assignableRoles` as checkboxes labelled `<role> for <email>`
  (the create form's own checkboxes are now labelled `<role> for new user`,
  so the two editors are unambiguous to both users and tests).
  `platform-admin` is deliberately NOT offered: `Commerce.Domain/Identity/
  RoleCatalog.cs` excludes it from the organization-assignable set and the
  `user-credentials` spec requires an organization-scoped caller never to be
  able to grant it — that reasoning is now an inline comment on the constant.
  The promise is no longer discarded: `saveRoles` awaits the PUT, and on the
  server's grant-cap rejection (a caller cannot hand out permissions it does
  not itself hold) it surfaces the `ApiError` message as an alert AND resets
  that row to `user.roleNames`, so the row never advertises a selection that
  was not persisted. A successful save re-reads the list, so the row shows
  what the server actually stored. Works identically in table and card view
  (the editor lives in `DataView`'s `renderActions` slot).
  TDD strict: 5 new cases written first, all 5 RED before the change.
  **Mutation check** (explicitly requested): re-introducing the old bug
  (`updateUserRoles(user.userId, roles)`) fails 3 of them
  ("saves a row's own roles, not whatever the create form has checked",
  "edits one row without touching another row", "edits and saves the roles of
  a row from the card view too"); reverted immediately. Two existing
  assertions consciously updated, neither weakened: "saves the selected roles
  for a listed user" now expects 3 fetches instead of 2 and names the refresh
  call (a successful save legitimately re-reads the list), and "renders the
  real user columns" now scopes its lookups to the row's data CELLS, because
  the actions cell repeats the role names as checkbox labels and an unscoped
  `getByText('provider')` would be ambiguous rather than wrong. Suite:
  150/150.

- 2026-09-25: **"No X yet." no longer lies after a failed load** (commit
  `0e49d87`, delegated-direct scope, 4 source + 4 test files). Closes the
  three `review-4d6e15256b3b46f6` findings against our own T4b code:
  WARNING `R3-branches-load-failure-empty-state`, WARNING
  `R3-users-load-failure-empty-state`, and SUGGESTION
  `R3-customers-error-empty-message-conflation`. Chosen solution, applied
  identically to all three screens rather than three local patches:
  1. `DataView` gains ONE additive optional prop, `loadErrorMessage?: string
     | null`. When the collection is empty AND the last load failed, it
     renders that message in a destructive-styled panel
     (`data-testid="data-view-load-error"`) instead of `emptyMessage`.
     Ordering is deliberate and tested: `loading` still wins over it, and
     stale items already on screen keep rendering (a failed RELOAD should not
     blank the last known rows — the screen's alert already reports it).
     Extending the shared component was preferred over per-screen ternaries
     because "empty" vs "unreadable" is a state of the list surface itself,
     and the old per-screen ternary is exactly what produced finding 3.
  2. Each screen splits its single `error` into `loadError` (set only by
     `refresh`, cleared on a successful load) and `actionError` (set only by
     create / force-reset / save-roles / issue-access). Only `loadError`
     feeds `loadErrorMessage`, which is what actually fixes the conflation:
     a failed `handleIssueAccess` can no longer make a later no-match search
     claim the customers could not be loaded. Both errors render as their own
     `role="alert"`.
  TDD strict: 9 new cases, 6 of them RED before the change — DataView "says
  the load failed instead of claiming
  the collection is empty"; Branches "does not claim there are no branches"
  and "stops reporting a load failure once a later load succeeds"; Users
  "does not claim there are no users"; Customers "does not claim there are no
  customers" and "does not blame the load when a failed action left an error
  on screen"). The 3 that passed on arrival are ordering/regression guards,
  and were mutation-checked rather than trusted: making the load-error panel
  win over non-empty items fails "keeps showing the items it already has when
  a later load fails", and feeding `actionError` into `loadErrorMessage`
  fails "keeps the load failure separate from a failed action" (plus, as a
  bonus, the role-rejection case). Reverted both. As the reviewer noted, the
  pre-existing "surfaces a load failure as an alert" cases could not catch
  this — they assert the alert only; the new cases assert the absence of a
  message this screen genuinely renders in its empty state, so they can fail.
  **No existing test was deleted or weakened.** Suite: **159/159 across 35
  files** (145 baseline + 5 from the role editor + 9 here). `npm run lint`:
  exit 0, 13 warnings, identical categories and count to the baseline (1
  `no-unused-vars`, 6 `only-export-components`, 6 `set-state-in-effect`) —
  no new warning. `npm run build`: `tsc -b && vite build` clean.

- 2026-09-25: **T4c done** (delegated-direct route, 3 source files + 1 new
  test file + 1 extended test file). Two halves.
  1. **The screen was unreachable.** `PriceListsScreen.tsx` (488 lines),
     `PriceHistory.tsx` (71) and `ImportReviewTable.tsx` (79) were ~638 lines
     of working UI that `App.tsx` never imported, which left two specs with no
     surface at all: `price-list-management` ("Admin Create, Edit, and History
     Access") and `supplier-price-import` ("Staged Batch Requires Admin Review
     Before Commit"). Mounted at `/app/price-lists` inside the existing
     `RequireAdmin` block, next to customers/users/branches, and added a
     "Price lists" `NavItem` to `AppLayout`'s sidebar inside the same
     `hasPermission(user, Permission.ManageUsers)` block (presentation only —
     `App.tsx`'s guard is the routing boundary, `Endpoints/Pricing.cs` the real
     one).
     The screen's own spec file could not have caught this: it hand-builds its
     own `<Routes>`, so it passes whether or not `App.tsx` mounts the path.
     New `src/App.test.tsx` fixes that class of blind spot — it renders the
     REAL `App` tree with only `AuthProvider` stubbed, so the guards and every
     screen stay real, and asserts the mount, the sidebar link, the non-admin
     redirect to the catalog and the signed-out redirect to `/login`.
  2. **Migration onto `components/data/*`.** Dropped the
     `mx-auto mt-8 w-full max-w-3xl` Card for a full-width `<section>`;
     `PageHeader` ("Price lists", with "Create default price list" in the
     actions slot only while no default exists), `DataToolbar` (client-side
     search by name, no new endpoint) and `DataView` over the price lists with
     real `PriceListRecord` columns — Name, Is default (`Yes`/`No`), Created
     (`createdAtUtc`, `hideOnMobile`). Preference key `view:price-lists`.
     The header is "Is default", not "Default", because a list is also
     routinely NAMED "Default" and the duplicate string made both the screen
     and its queries ambiguous.
     **Nested list decision**: only the price lists go through `DataView`. The
     prices held BY the selected list render in a separate
     `data-testid="price-list-entries"` panel below it, not a second
     table/card grid — one view switch cannot sensibly own two grids, and the
     nested surface is a per-presentation `PriceHistory` expander plus the
     publish form, not a flat record list. The selected list is derived, not
     stored: the operator's pick wins, otherwise the default list opens by
     itself, so the previous "default list only" behavior is the unchanged
     starting state while a second list is now reachable.
     `loadError` / `actionError` split exactly as T4b's three screens: only
     `loadError` feeds `loadErrorMessage`, so a failed create can no longer
     make the list claim it could not be read. Raw `text-red-600`/`neutral-*`
     replaced with the semantic tokens.
     Functionality preserved verbatim: create default list, publish an entry,
     price history, and the whole supplier import cycle (upload, review,
     commit, reject).
  **Price-composition hold**: `openspec/changes/commerce-price-composition`
  (proposed, NOT implemented) will turn `PriceListEntry.unitPrice` into a BASE
  price with the sellable price derived from rate components (IVA, IB,
  freight, markup). So no derived column, tax breakdown or total was built
  here; the "Unit price" label stays neutral and carries an inline comment
  naming that proposal as the thing that will have to change it.
  TDD strict: 16 of the 17 new cases were RED first (4 `App.test.tsx` — all
  four failed with the route absent, since `*` sends `/app/price-lists` to the
  home screen; 12 T4c screen cases). The 17th ("rejects a staged batch
  through the endpoint") passed on arrival and was kept as a regression guard
  for the import half, which had commit coverage but no reject coverage.
  **Mutation checks (2)**: (a) deleting the `<Route path="price-lists">` line
  fails all 4 route tests, then restored — so the most important assertion,
  that the screen is actually reachable, can genuinely fail; (b) collapsing
  `selectedList` to the default list only fails "publishes against the price
  list the operator picked, not the default one", then restored.
  **No existing test was deleted or weakened.** Two consciously updated, both
  because the markup change made the old expectation wrong rather than
  because it became inconvenient: the load-failure alert now asserts
  `/unreachable/i` (a rejected `fetch` reaches the screen as `ApiError`
  "Commerce.Cloud.Api is unreachable.", so the old wording was never the
  rendered text), and the action-failure case sends a 409 with a real body so
  its message is distinguishable from the load failure's. Suite:
  **176/176 across 36 files** (159/35 before). `npm run lint`: exit 0, 13
  warnings — identical count and categories to the baseline.
  `npm run build`: `tsc -b && vite build` clean (355.29 kB / 105.79 kB gzip
  JS, 20.98 kB / 4.90 kB gzip CSS). `.NET` builds/tests deliberately NOT run:
  the user has `Commerce.Pos.Windows` and `Commerce.Cloud.Api` running
  locally and holding the DLLs.

- 2026-09-25: Playwright E2E regression caught by CI, not by us. Promotion
  PR #75 sat unmerged with `web-e2e` failing 7 of 18 tests and `ci-gate`
  failing behind it, while `web-tests` (Vitest) passed. Single root cause,
  ours: T3 moved "Sign out" into the account dropdown, and ten assertions
  across `catalog`, `customers`, `ordering` and `sign-in` specs used
  `getByRole('button', { name: 'Sign out' })` as the signed-in signal, so it
  is no longer visible until the menu is opened. Fixed by adding
  `openAccountMenu`, `expectSignedIn` and `signOut` to `e2e/helpers.ts` and
  routing all ten call sites through them; `expectSignedIn` closes the menu
  with Escape afterwards so it cannot cover controls a test clicks next. The
  trigger is addressed by `button[aria-haspopup="menu"]` because its
  accessible name is the signed-in user's display name, which varies per
  test. No production code changed.
  **Process gap worth keeping:** the whole T1-T4b frontend overhaul was
  verified with Vitest only. The E2E harness needs the SPA built into
  `Commerce.Cloud.Api/wwwroot`, which this session deliberately forbade to
  avoid disturbing the user's running stack and to keep
  `PublicRateLimitTests` green — so Playwright never exercised the reshaped
  DOM until CI did. Unit tests cover components in isolation; only E2E
  covers the navigation contract. A DOM-structural change should be assumed
  to break E2E selectors until proven otherwise.
  Second gap: `e2e/` is outside the TypeScript project (`tsconfig.app.json`
  includes only `src`), so `npm run build` never typechecks it and Playwright
  only strips types. This fix was typechecked with an explicit `tsc` run.

- 2026-09-25: Resumed after a power cut (tree clean, nothing lost). User
  reported remaining aesthetic gaps: no nav icons, Organizations fields
  overlapping, item editors looking like embedded modals instead of using
  the full screen. Mapping fork findings recorded in T7-T9. T5 backend
  settings blocked on a missing spec (no field exists anywhere). Order:
  T7 -> T8 -> T9, committed directly on `dev` (user workflow preference).

- 2026-09-25: T7 done (delegated direct, commit `cccfc08`). `lucide-react`
  added; icons on every nav item, the hamburger, account menu (ChevronDown,
  KeyRound, LogOut) and ThemeSwitcher (Sun/Moon/Palette replace hand-drawn
  SVGs). All `aria-hidden`, accessible names unchanged. TDD strict: 2 RED
  (no svg inside links / menu entries), then GREEN; 180/180.
- 2026-09-25: T8 done (delegated direct, commit `6f8eeba`). New
  `OrganizationForm.tsx`; screen on the data layer (`view:organizations`,
  Name + Created columns), form full-width behind "New organization",
  2-column grid with visible labels. Branch name is now user-controlled
  (was hardcoded `'Main'`); blank is sent as null and the backend defaults
  it to "Main" (`Account.cs:317`, verified by the parent). E2E
  `system-admin.spec.ts` now opens the form first. TDD strict: 11/13 RED
  first. Checks: `npm run test` 193/193 (37 files), `npm run lint` exit 0
  with the baseline 13 warnings, `npm run build` clean, e2e typecheck clean
  with `npx tsc --noEmit --strict --module esnext --moduleResolution
  bundler --target es2022 --skipLibCheck --ignoreConfig --types node
  e2e/*.ts`. Parent spot check: OrganizationsScreen + AppLayout tests
  21/21. Playwright not run locally (CI only).
  RDD: assess over `a5ddb77..6f8eeba` = medium, `slice_budget_reached`
  (672 lines); user granted consent; lineage `review-a396c07a3d0aba3a`,
  one reliability lens, APPROVED and acknowledged (authority burned).
  Reviewed boundary advances to `6f8eeba`. Non-blocking follow-ups:
  `R3-chevron-test-not-discriminating` (AccountMenu.test.tsx:89-96) and
  `R3-hamburger-test-not-discriminating` (AppLayout.test.tsx:74-81) —
  those icon tests passed before the change too; `R3-refresh-out-of-order`
  (OrganizationsScreen.tsx:38-57) — a slow list refresh could overwrite a
  newer one. Folded into T9 as cleanup.

- 2026-09-25: T9 done (delegated direct). Commits: `d4d2ae1` FormPage
  shell (+ CustomerForm/OrganizationForm on it), `34f49a5` Catalog "Edit
  code" as a full-screen form (DataView `renderExpanded` removed, no other
  caller), `6994991` price lists open via "Manage prices" as a full-screen
  detail page, history stays an expandable section inside it (UX change:
  the default list's prices no longer show without a click), `e12effc`
  stale organization refresh guard (sequence number), `9eecf3b` icon tests
  now assert the lucide class. TDD strict, RED observed per commit.
  `npm run test` 204/204 (38 files), lint exit 0 / 13 baseline warnings,
  build clean, e2e typecheck clean; `e2e/catalog.spec.ts` needed no change
  (role/label selectors unchanged). Parent spot check: full suite 204/204.

- 2026-09-25: T10 done (delegated direct). `2bde2bc` token-based
  `ui/select.tsx` replacing every hardcoded select (CustomerForm x3,
  OrderLinesEditor); `662aed2` CustomerForm in five fieldset sections
  (Identity, Tax, Contact, Address, Commercial) on a 1/2/3-column grid,
  `max-w-lg` removed. Catalog code form left at `max-w-md` on purpose
  (single field). TDD strict: RED observed (missing module; 3 failing
  CustomerForm cases; OrderLinesEditor color assertion). Checks: `npm run
  test` 208/208 (39 files), lint exit 0 / 13 baseline warnings, build clean;
  e2e untouched (`#order-line-presentation` id unchanged). Parent spot
  check: full suite 208/208.
  RDD over `6f8eeba..662aed2`: medium, `slice_budget_reached` (989 lines,
  20 files); user granted; lineage `review-ca0149decfc067f7`, reliability
  lens, APPROVED and acknowledged. Reviewed boundary -> `662aed2`.
  Non-blocking follow-ups: `R3-select-tests-negative-only`
  (CustomerForm.test.tsx:117-128 only asserts absence of old classes),
  `R3-stale-load-test-timing` (OrganizationsScreen.test.tsx:278-284).

- 2026-09-25: T5 done (delegated direct). `83b8d86` backend: spec
  requirement in `organization-persistence`, `Organization` gains
  `LogoUrl`/`PrimaryColor`, migration `0015_organization_branding.sql`
  (nullable + CHECK, mirrored into `deploy/dev/db/init-rls.sql`),
  `GET/PUT /account/organizations/{id}/branding` (system admin, audited
  `organization.branding_updated`), `GET /account/organization/branding`
  (caller's own org from the tenant scope, never a submitted id). Response
  `{ logoUrl, primaryColor }`; http/https <= 2048 chars, `#rrggbb`, blank
  clears. `48034db` UI: "Edit branding" row action -> full-screen
  `OrganizationBrandingForm` (URL, color picker + hex, preview).
  TDD strict: RED observed (compile errors backend; unresolved import and
  3 failing screen tests frontend). Checks: integration suite 698/699 —
  the one failure is the known-environmental `PublicRateLimitTests`
  (populated `wwwroot`); `dotnet build` 0 errors; `npm run test` 218/218
  (40 files); lint exit 0, 14 warnings (+1 `set-state-in-effect`, existing
  category); build clean; e2e untouched. Parent spot check: branding
  integration tests 16/16. Writer started Docker Desktop and only the
  `postgres` service (`up -d`, never `down`).

- 2026-09-25: RDD over `662aed2..HEAD` (T5): medium, `slice_budget_reached`
  (905 lines, 15 files, migration 0015); user granted; lineage
  `review-01cd6bf7324810db`, reliability lens, APPROVED and acknowledged.
  Reviewed boundary -> T5 doc commit. Follow-ups: WARNING
  `R3-branding-load-failure-save-clears` (OrganizationBrandingForm.tsx:48-53
  — a failed load leaves empty fields, Save then clears real branding;
  folded into T6), SUGGESTION `R3-branding-error-contract-unproved`
  (OrganizationBrandingForm.test.tsx:115-117).

- 2026-09-25: T6 done (delegated direct). `7e210ae`
  `OrganizationBrandingProvider` (own-org branding per signed-in identity,
  403/404/network -> null), `organizationTheme.ts` (primary, WCAG-contrast
  foreground, ring), ThemeProvider applies them under "custom", Custom
  disabled with an explanation when the org has no color, `BrandMark` logo
  in the shell with text fallback. `2b54bef` fixes the T5 WARNING: a failed
  branding load disables Save and offers Retry. TDD strict: RED observed
  per commit. `npm run test` 241/241 (42 files), lint exit 0 / 17 warnings
  (+3, existing categories), build clean, e2e untouched. Parent spot check
  241/241. RDD over `e159daa..2b54bef`: medium (705 lines), user granted,
  lineage `review-c156e2357c217f51`, APPROVED and acknowledged; follow-ups
  in T6b.
- 2026-09-25: Local sysadmin access lost again. Root cause: first
  `compose up` after the named volume (59c3afa) recreated Postgres onto an
  empty volume (one-time), and `deploy/dev/.env` had the sysadmin under a
  wrong key so provisioning refused. `.env` fixed (SYSADMIN_* = platform
  owner, ORGANIZATION_ADMIN_* = client), accounts re-provisioned.

## Product review backlog (user, 2026-09-25)

Reported by the product owner while using the app. Each item is being
checked against `openspec/specs/` to classify it as a spec gap or an
implementation gap before implementing.

- [x] B1. Users screen shows the platform sysadmin with the
      `business-admin` role checked. Wrong: sysadmin is the platform
      owner's role, business-admin is the client's user inside an
      organization. They must never be conflated.
- [ ] B2. Every entity in the system must be editable. Users cannot be
      edited today.
- [ ] B3. Roles are poorly presented; use better components (badges /
      chips / proper multi-select) instead of bare checkboxes.
- [ ] B4. There is no customer (client) role; customers need access to
      review price lists and place orders.
- [ ] B5. Modularization: organization management is sysadmin-only, and
      each role should see only its own modules.
- [ ] B6. Branches cannot be created from the UI.
- [ ] B7. More attractive menu; for business admins, a top navbar with a
      branch switcher.
- [ ] B8. Owner's concern: the project is behind — determine whether the
      cause is missing definition (specs) or failing implementation, per
      item.

### Gap audit result (mapping fork, 2026-09-25)

- B1 IMPLEMENTATION GAP. Spec (`platform-administration`, "Sysadmin
  Identity Lives in the Unified Model") makes sysadmin the
  `is_system_admin` flag, not a role; `platform-admin` role is reserved and
  unassignable. Bug is in seeding: `TestSeedEndpoints.cs:39-68` grants
  every seeded user `business-admin` with full permissions in an auto-made
  "E2E Test Organization", and `provision-admin.ps1` seeds the sysadmin
  through it and only sets the flag. Users screen renders what it was
  given. No spec change needed.
- B5 mostly a consequence of B1 (tenant nav shown because of that stray
  role). Organizations is correctly sysadmin-only in API and UI. Missing:
  a spec line for what a sysadmin with no organization sees.
- B6 DECIDED by the owner 2026-09-25: the sysadmin can do everything in
  every organization (not only organization CRUD). Plan: a spec change in
  `platform-administration` for a sysadmin organization context (pick an
  organization in the top navbar, then use every tenant module on it with
  full permissions), reusing the existing screens instead of duplicating
  them under Organizations. Shares the navbar with B7's branch switcher.
- B6 was a SPEC GAP (acknowledged-open in the admin-console change): branches
  are created only inside the caller's own org (`POST /account/branches`,
  `ManageBranchSettings`); no sysadmin cross-org path. A business-admin can
  create branches today.
- B2 IMPLEMENTATION + SPEC GAP: no update/disable for organizations,
  branches, price lists, products (only rename), presentations delete;
  users can change roles and reset passwords but `IsRevoked` is never set
  by any endpoint. Specs never define delete vs disable per entity.
- B3 matches spec (checkbox per assignable role, platform-admin excluded);
  purely a UI quality change.
- B4 SPEC-INTENTIONAL: customers are a separate entity with their own
  credential scheme (`private-customer-ordering`, `customer-registry`,
  `CustomerOrderingAccess`, `/order`), not a staff role. Customer login and
  ordering exist. Needs owner confirmation, not a fix.
- B7 SPEC GAP: no "selected branch" session concept or switch API
  anywhere; multi-branch users exist in the model.
- B8 answer: both. B1/B2(partly) are implementation failures against
  existing specs; B6/B7/B2(delete semantics) were never defined.

- 2026-09-25: B1 done (delegated direct; writer was cut off once by a usage
  limit and resumed). `656c807` seed seam gains `systemAdmin` (zero roles,
  zero branch scope, own "Platform System Administrator" org because
  `users.organization_id` is NOT NULL) + `PromoteToSystemAdminAsync`;
  `provision-admin.ps1` seeds with it and always repairs the sysadmin row
  (`roles = []`, `branch_scope = {}`); `b9c052d` treats the expected 403
  `no-branches-assigned` Desktop pairing as success for a branchless
  sysadmin; parent fix `fix(tests)` added a missing `using` the writer never
  compiled. Checks: full integration suite 702/702 in a clean worktree (the
  user's running API/POS lock the main tree's DLLs; `--artifacts-path` gives
  361 false failures — do not use it); `npm run test` 242/242, lint exit 0 /
  17, build clean. Dev DB verified: sysadmin `is_system_admin=t`, roles `[]`,
  no branches; Vaca Verde admin unchanged. RED evidence for the backend
  tests was not captured separately (writer could not build) — disclosed.
  RDD over `05ce1f6..HEAD`: medium (433 lines), granted, lineage
  `review-2a111627d67b4672`, APPROVED and acknowledged. Follow-ups (B1b):
  WARNING `R3-applayout-test-contradicts-name` (AppLayout.test.tsx:116-122),
  WARNING `R3-pair-parse-before-status` (provision-admin.ps1:303-305),
  WARNING `R3-seed-promote-not-atomic` (TestSeedEndpoints.cs:88-90),
  SUGGESTION `R3-no-conflict-path-coverage`.

## Next step

B1 in progress (writer). Then B6+B7 together: spec for the sysadmin
organization context and the business-admin branch context, then the top
navbar with both switchers. Then B2 semantics, B3, B4 confirmation.

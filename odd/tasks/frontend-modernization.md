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
- [ ] T3. App shell redesign: enterprise nav (sidebar or responsive
      top-nav — decide during task), responsive breakpoints, account
      dropdown menu (user display name, Change password, Sign out). Move
      `RenewPasswordScreen` off the top-level tab bar into that menu.
      Route: delegated direct.
- [ ] T4. Reusable list/card view-switch component, applied first to one
      data-heavy screen (Customers or Catalog TBD) as the reference
      implementation, then rolled out. Route: delegated direct.
- [ ] T5. Organization settings: backend fields (logo, theme colors, date
      format, geolocation, usage plan) on the Organization entity +
      sysadmin-gated endpoints, plus rebuilt `OrganizationsScreen.tsx` UI
      (color picker, logo upload/URL, settings form). Backend touches
      `Commerce.Cloud.Api` (entity, migration, endpoint) — cross-checked
      against `organization-persistence` / `platform-administration` /
      `admin-console` specs for consistency. Route: delegated direct,
      likely its own sub-breakdown given size.
- [ ] T6. Wire the authenticated user's resolved organization to the
      "custom" theme option (fetch org theme on session load, feed
      `ThemeProvider`). Depends on T2 + T5.

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
  Tailwind v4 utilities via `@theme inline`. Removed hardcoded
  `color-scheme: light`. Updated `components/ui/{button,card,input,label}.tsx`
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

## Next step

Start T3 (app shell redesign: nav + account menu).

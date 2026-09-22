# Main PR-only CI

## Goal
Relax GitHub Actions so development is not blocked by push/dev gates. Keep only a lightweight automatic CI signal for pull requests targeting `main`.

## Tasks

- [x] Confirm issue #62 and main promotion state.
- [x] Diagnose local Vite Bad Gateway sign-in failure.
- [x] Select CI scope: PRs to `main` only; no `dev` or push workflows.
- [x] Apply workflow change.
- [ ] Validate and open PR.

## Evidence

- Issue #62 is closed as completed by PR #64.
- `http://localhost:5173/login` fails because Vite proxies `/account` to `http://localhost:5080`, but Cloud.Api is listening on `8080`; real cookie auth should use the built SPA served behind `https://localhost:5443` via local SSL proxy to `8080`.
- Selected CI policy: no push workflows, no dev branch gates, only `pull_request` targeting `main`.
- Workflow change: `.github/workflows/release.yml` now triggers only on `pull_request` with base branch `main`.
- Validation: `git diff --check` passed.

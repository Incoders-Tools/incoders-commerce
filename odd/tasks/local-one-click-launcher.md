# Local one-click launcher

## Goal
Make `deploy/dev/run-all.ps1` the repository-side entrypoint for a transparent local launch, so an external shortcut can start the stack without asking the user to discover ports or choose between Vite/API URLs.

## Tasks

- [x] Start local Postgres from `deploy/dev/compose.yaml` and wait for the compose health check.
- [x] Run `Commerce.Cloud.Api` on `http://localhost:8080` with `ASPNETCORE_ENVIRONMENT=Development`, `PORT=8080`, and both local Postgres connection strings.
- [x] Build `Commerce.Web` and copy the built SPA into `Commerce.Cloud.Api/wwwroot` before API/proxy startup.
- [x] Start `local-ssl-proxy` as `https://localhost:5443 -> http://localhost:8080` for browser testing with Secure auth cookies.
- [x] Wait for `http://localhost:8080/health` before opening the browser.
- [x] Open `https://localhost:5443/login` automatically unless `-NoBrowser` is supplied.
- [x] Start `Commerce.Pos.Windows` unless `-NoPos` is supplied.
- [x] Preserve practical skip switches: `-NoApi` skips launching Cloud.Api; `-NoWeb` skips SPA build/copy and HTTPS proxy; `-NoPos` skips POS; `-NoBrowser` skips opening the browser.
- [x] Update `C:\shortcuts\run-incoders-commerce-all.bat` to validate prerequisites and call this repository script with visible errors.

## Final local URLs

- Browser login: `https://localhost:5443/login`
- HTTPS proxy: `https://localhost:5443 -> http://localhost:8080`
- API direct health/debug URL: `http://localhost:8080`
- Postgres: `localhost:5432` using the development-only roles from `deploy/dev/compose.yaml` and `deploy/dev/db/init-rls.sql`

## Notes

- The launcher no longer starts the Vite dev server. The browser path is the built SPA served by `Commerce.Cloud.Api` and fronted by HTTPS, matching the E2E/prod-like local shape required by Secure cookies.
- `C:\shortcuts\run-incoders-commerce-all.bat` calls `pwsh -NoProfile -ExecutionPolicy Bypass -File C:\repositories\incoders\incoders-commerce\deploy\dev\run-all.ps1`, validates Docker/.NET/npm/pwsh first, and stays open on errors.
- Postgres remains running in Docker after process windows are closed; stop it with `docker compose -f deploy/dev/compose.yaml down`.

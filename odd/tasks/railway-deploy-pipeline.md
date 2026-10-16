# Railway deploy pipeline, domain, and multi-client base

Activate `vaca-verde.com.ar` on Railway and make staging and production deploy themselves: a migration runner with a
ledger runs as Railway's pre-deploy command, PRs to `staging` and `main` are gated, `dev` stays ungated, and the POS
gets a pilot channel. Delivery strategy: ask-on-risk; forecast ~900 authored lines (code + CI + docs).

## Specs

- S1. Domain: "El dominio que compre lo realice en nic y como vavaverde.com.ar esta ocupado compre vaca-verde.com.ar ya es de mi propiedad." → every doc uses `vaca-verde.com.ar`; ADR-006 is amended (it requires an amendment for a different domain).
- S2. Platform domain decision: "vaca-verde.com.ar por ahora" → staging at `staging.vaca-verde.com.ar`, production at `vaca-verde.com.ar`; when a second client arrives, the platform moves to a neutral domain.
- S3. "dejame la guia para activar el dominio en raleway y empezar a integrar el sistema" → a step-by-step guide: NIC Argentina delegation to a DNS provider, Railway custom domain (CNAME + TXT), `PUBLIC_BASE_URL`, Resend, POS origin.
- S4. Storage question: "Dentro de la version pro tenemos acceso a storage tambien? Por ejemplo para poder guardar imagenes o facturas que el cliente pueda subir al sistema?" → answered in chat and documented (Railway Storage Buckets; no upload feature exists yet). No upload feature is built in this change.
- S5. Branches: "branch dev es todo desarrollo no deberia correr gates y demas politics para migrations, y staging ya si, deberia correr contra su env de railway las migraciones y demas cuestiones, igual que main." → no workflow gates `dev`; PRs to `staging` and `main` run the CI gate; a merge to `staging` or `main` deploys that Railway environment and applies pending migrations there before the new version serves traffic.
- S6. "Tenemos que poder manejar idempotencia y cualquier rollback por fallos ya sea en el sistema o en la db." →
  - S6a. The migration runner records each applied file (name + checksum, line endings normalized) in a ledger, skips applied files, refuses an applied file whose content changed, holds an advisory lock, and exits non-zero on any failure, so Railway cancels the deploy and the previous version keeps serving.
  - S6b. CI applies every migration twice on an empty database with the runner and fails on any error.
  - S6c. CI rejects a PR that modifies an existing migration file, or adds `DROP TABLE`, `DROP COLUMN`, or a `RENAME` without an explicit approval marker (expand-only, so a code rollback stays schema-compatible).
  - S6d. The guides document the rollback for each failure: migration failure, failed healthcheck, bad deploy, data damage (PITR), POS release.
- S7. "Con el versionado del pos lo mismo." → `vX.Y.Z-rc.N` tags (from `staging`) publish to a `pilot` channel; `vX.Y.Z` on `main` publishes `stable`; a terminal on `pilot` receives pilot and stable releases; POS rollback is roll-forward.
- S8. Multi-client: "este sistema debe poder implementarse para cualquier otro cliente, es decir con su propia web, sus propias reglas, pero tomando como base nuestro sistema. Mas que nada el pos, pero el pos termina dependiendo de la web." + decision "Compartido (Recomendado)" → ADR: one shared deployment; each client is an organization (RLS) with its own domain, branding and settings; client rules are configuration, never per-client code; onboarding steps documented; host-based organization resolution is named as future work.
- S9. Scope decision: "Sí, implementarlo (Recomendado)" → implement the runner, the pre-deploy wiring, the CI gates, and the POS pilot channel, with tests and docs.

## Tasks

- T1 [S1, S2, S3, S4, S8] Docs: amend ADR-006, add ADR-015 (shared multi-client model), and add the domain activation steps to `deploy/railway-runbook.md`. Route: inline. Commit: pending.
- T2 [S6a] `src/Commerce.Migrator` console runner + tests. Route: gentle-ai-worker. Commit: pending.
- T3 [S5, S6a] Image and Railway wiring: Dockerfile, `railway.json` `preDeployCommand` and watch patterns, runbook sections 4-5. Route: inline. Commit: pending.
- T4 [S5, S6b, S6c] CI: `release.yml` gates PRs to `staging` and `main`; a `migrations` job (runner twice + expand-only guard) aggregated by `ci-gate`; required-checks runbook (`staging` and `main` only). Route: inline. Commit: pending.
- T5 [S7] POS pilot channel: `pos-release.yml`, `build-pos-msix.ps1`, updater prerelease selection, with tests. Route: gentle-ai-worker. Commit: pending.
- T6 [S5, S6d, S7, S8] Release guide rewrite (automatic flow, rollback matrix, POS channels, client onboarding, storage note). Route: inline. Commit: pending.
- T7 Independent verification (high risk: delivery/CI and migrations). Route: gentle-ai-verify. Commit: n/a.

### Runner contract (shared by T2, T3, T4)

- Project `src/Commerce.Migrator` (net10.0 console, Npgsql 9.0.3), added to `Commerce.sln`.
- Run: `dotnet Commerce.Migrator.dll [--migrations <dir>]`; the default directory is `migrations` next to the executable.
- Environment: `ConnectionStrings__Migrations` (required; an owner/superuser connection); `APP_RUNTIME_PASSWORD` and `PLATFORM_READONLY_PASSWORD` (required only when a pending file contains the matching placeholder; missing → fail before executing anything).
- Ledger: `public.schema_migrations (file_name text PRIMARY KEY, checksum text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now())`, created if absent; no grants to `app_runtime`.
- Checksum: SHA-256 (lowercase hex) of the file's UTF-8 text with `\r\n` normalized to `\n`, computed before placeholder substitution.
- Order: ordinal by file name (`*.sql`). Applied + same checksum → skip; applied + different checksum → fail naming the file (nothing executed); pending → execute the file as one command, then insert its ledger row.
- Lock: session `pg_advisory_lock` on a fixed key for the whole run.
- Output: one line per file (`applied <name>` / `skipped <name>`), then a summary; exit 0 on success, non-zero on any failure with the error on stderr.

## Log

- L1 (user): "El dominio que compre lo realice en nic y como vavaverde.com.ar esta ocupado compre vaca-verde.com.ar ya es de mi propiedad. Dentro de la version pro tenemos acceso a storage tambien? Por ejemplo para poder guardar imagenes o facturas que el cliente pueda subir al sistema? respondeme y dejame la guia para activar el dominio en raleway y empezar a integrar el sistema. Recorda que en github tenemos que manejar branches de manera tal que branch dev es todo desarrollo no deberia correr gates y demas politics para migrations, y staging ya si, deberia correr contra su env de railway las migraciones y demas cuestiones, igual que main. Tenemos que poder manejar idempotencia y cualquier rollback por fallos ya sea en el sistema o en la db. Con el versionado del pos lo mismo. Y recorda que este sistema debe poder implementarse para cualquier otro cliente, es decir con su propia web, sus propias reglas, pero tomando como base nuestro sistema. Mas que nada el pos, pero el pos termina dependiendo de la web."
- L2 (user answers): client model "Compartido (Recomendado)"; platform domain "vaca-verde.com.ar por ahora"; scope "Sí, implementarlo (Recomendado)".
- L3 (evidence): Railway's pre-deploy command runs in a separate container with service variables and the private network, and a failing command stops the deployment (docs.railway.com `/deployments/pre-deploy-command`, `/config-as-code/reference`). Storage Buckets: S3-compatible, private, presigned URLs, $0.015/GB-month, free operations and egress.
- L4 (explore map): `release.yml` triggers only on `pull_request` to `main` (`:9-11`); `ci-gate` aggregates `changes, web-tests, web-e2e, build` via `aggregate-results.sh`; the classifier counts `deploy/*` as both web and dotnet. The Dockerfile final stage is `aspnet:10.0` with no `psql` and no migrations. `railway.json` watchPatterns omit `deploy/db/**`. `pos-release.yml:84-93` derives internal/stable only; `build-pos-msix.ps1:28` restricts Channel to stable/internal; `UpdateManifestSources.cs:70` includes prereleases only for `internal`. Integration tests apply migrations with `File.ReadAllText` + `.Replace` + a single `ExecuteNonQuery` (`PostgresTestFixture.ApplyMigration`), so files with their own BEGIN/COMMIT run as one command. `ContractVersion` is frozen at 1 with no negotiation.

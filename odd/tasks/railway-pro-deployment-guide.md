# Railway Pro deployment and release guide

Documentation-only feature: replace the Supabase-based provisioning docs with a Railway Pro guide (API + Postgres on
the private network, backups, cost control) and a release/versioning guide for staging and production.
Delivery strategy: ask-on-risk; forecast ~450 authored lines across three docs commits.

## Specs

- S1. Database on Railway, same project as API and front: "existe la posibilidad de manejar un motor de db que nos sirva para el back desde el mismo raleway donde vamos a tener api y front?" → Postgres runs as a Railway service reached over the private network (`postgres.railway.internal`), with no public access by default.
- S2. "investiguemos como dejar alineada esta solucion a una red interna postgress en railway y si vamos a contar con los mecanismos de backup" → the guide configures scheduled volume backups and PITR, plus an off-Railway `pg_dump`, and includes a restore drill.
- S3. "la idea es gastar lo minimo posible" / "Si vamos con la version de 20 USD mensuales" → Railway Pro plan ($20/month, usage credit included); cost estimate documented.
- S4. "Lo que no quiero es pagar por demanda, es decir que este limitado a una cantidad y en tal caso me notifiquen y lo evaluamos" → a usage email alert at $20; a hard limit is documented as optional and destructive (takes all workloads offline).
- S5. "en este gasto no estamos sumando el gasto anual de dominio que tengo que sacar previo a implementar todo" → domain purchase (`vacaverde.com.ar`, NIC Argentina, annual) is a prerequisite step in the guide.
- S6. "cuando un cliente necesite su propia webpage? ... deberia ir mapeado a otro dominio y correr su propia instancia, cierto?" → document that a client site maps an extra custom domain to the same service (multi-organization), not a separate instance; host-based organization resolution does not exist yet.
- S7. Environments: "Staging + producción (Recomendado)" → `staging` branch deploys the staging environment (pilot) and `main` deploys production (stable), per ADR-004.
- S8. Guide language: "Inglés (convención del repo)".
- S9. Starting state: "De cero" → no existing Supabase or Railway resources; no data migration.
- S10. "empezar la guia para implementar el producto y manejar las versiones" → a release guide that explains how a change moves dev → staging → main, how the cloud and the POS are versioned and released, and how to roll back.

## Tasks

- T1 [S1, S3, S4, S6, S9] ADR-014: Railway-hosted Postgres replaces Supabase. Route: inline. Commit: pending.
- T2 [S1, S2, S3, S4, S5, S7, S8, S9] Railway runbook for staging and production (replaces `deploy/staging-runbook.md`); update Supabase references in `deploy/README.md`. Route: inline. Commit: pending.
- T3 [S7, S8, S10] Release and versioning guide; add the Railway go-live items to `docs/launch/go-live-requirements.md`. Route: inline. Commit: pending.

## Log

- L1 (user): "Estabamos por empezar la guia para implementar el producto y manejar las versiones, pero antes, existe la posibilidad de manejar un motor de db que nos sirva para el back desde el mismo raleway donde vamos a tener api y front?"
- L2 (user): "Si investiguemos como dejar alineada esta solucion a una red interna postgress en railway y si vamos a contar con los mecanismos de backup. Si podes arrojarme el monto de gasto mensual mejor, la idea es gastar lo minimo posible."
- L3 (user): "La opcion de evitar el front no se si es tan viable porque cuando un cliente necesite su propia webpage? Es decir su propio sitio, deberia ir mapeado a otro dominio y correr su propia instancia, cierto? Ademas en este gasto no estamos sumando el gasto anual de dominio que tengo que sacar previo a implementar todo. Me interesa la idea de tener backups de la informacion del cliente por si en algun momento sucede algo tener un punto de retorno o restauracion. Lo que no quiero es pagar por demanda, es decir que este limitado a una cantidad y en tal caso me notifiquen y lo evaluamos. El cliente de este producto ya existe y es vaca verde, se supone va a tener mayor uso en el POS y que la web van a acceder simultaneamente 2-3 usuarios como mucho"
- L4 (user): "Con los 20 del pro ya entra postress la db o eso se saca aparte?" → answered: Postgres is a regular service billed against the same usage credit.
- L5 (user): "Si vamos con la version de 20 USD mensuales"
- L6 (user answers): environments "Staging + producción (Recomendado)"; language "Inglés (convención del repo)"; starting state "De cero".
- L7 (evidence): The web SPA is already served from Cloud.Api's `wwwroot` (Dockerfile web-build stage; `src/Commerce.Cloud.Api/Program.cs:409-416`). Railway facts come from docs.railway.com (pricing, cost-control, volumes/backups, volumes/point-in-time-recovery, private-networking, domains), Oct 2026. Whether scheduled backups exist on Hobby is undocumented; Pro was chosen.

# ADR-006: Domain and subdomain topology

## Status

Accepted (applies on domain acquisition — not a current configuration)

## Context

The platform has zero domain configuration. `PUBLIC_BASE_URL` is the literal placeholder `https://<railway-domain>` (`deploy/staging-runbook.md`), and it is already load-bearing — password-reset links are built as `{PUBLIC_BASE_URL}/reset-password?token=...`. Upcoming public ordering, an admin console, and POS clients will each need a stable origin. `vacaverde.com.ar` is confirmed not yet purchased; deciding the topology now prevents ad-hoc origins from being minted one at a time under deadline.

## Decision

- **One apex domain, `vacaverde.com.ar`**, is the platform's public identity. All first-party origins are subdomains of it; no second apex domain is introduced for a surface that is part of this platform.
- **Reserved subdomains**: `vacaverde.com.ar` (public/customer-facing ordering entry), `admin.` (staff/administrative web console), `pedidos.` (reserved for a dedicated ordering surface if it is ever split from the apex), `api.` (reserved for the HTTP API only if it is ever split from the SPA host).
- **`api.` and `pedidos.` are reserved, not required.** Today Cloud.Api serves the SPA from its own `wwwroot` in one image; a single origin is therefore the correct current shape and splitting hosts is a later, separately-justified change. Reserving the names prevents them being taken by something else.
- **This ADR changes no configuration.** Railway-generated domains remain the live origins until the apex is purchased and mapped. When it is, the work is: register the domain, add custom domains on the Railway service(s), and replace `PUBLIC_BASE_URL` with the real origin — per environment.
- **Per-environment origins** follow the same rule (e.g. a staging subdomain or a separate staging apex); staging and production never share one origin, because `PUBLIC_BASE_URL` targets emailed reset links at real users.

## Consequences

- `deploy/staging-runbook.md`'s `PUBLIC_BASE_URL` row and its `https://<railway-domain>` checks become domain-mapping steps once acquired; until then the placeholder is correct and must not be "fixed" to an aspirational value.
- Anything that hardcodes an origin (CORS, POS `Commerce:CloudApiBaseUrl`, email link construction) must read it from configuration, so the cutover is a variable change and not a code change.
- Until the domain is purchased this ADR is inert; it constrains naming, not runtime. Purchasing a different domain requires amending this ADR, not silently diverging.

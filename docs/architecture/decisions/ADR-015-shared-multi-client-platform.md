# ADR-015: One shared platform for every client

## Status

Accepted (owner decision, 2026-10-16). Builds on [ADR-002](./ADR-002-offline-authority-and-identity.md) (tenancy and
RLS) and [ADR-014](./ADR-014-railway-hosted-postgres.md) (hosting).

## Context

Vaca Verde is the first client, but the product must serve other businesses, each "with its own website and its own
rules", on the same base system. The POS matters most, and the POS depends on the cloud (Cloud.Api and the web
console): it pairs, syncs, and reads its catalog from there.

Two hosting models were compared:

| Model | For | Against |
|---|---|---|
| **Shared**: one deployment, each client an organization | One deploy and one migration run update every client; nearly fixed cost; the code already isolates organizations with forced RLS | Client rules must be configuration; some surfaces are still single-organization (see below) |
| **Silo**: one Railway project per client | Strongest isolation; independent release timing | About $5-7/month more per client and environment; N databases to migrate, back up and watch |

## Decision

- **One shared deployment per environment** (`staging`, `production`) serves every client. Each client is an
  organization; forced RLS keeps its data apart.
- **A client's rules are configuration, never a code fork.** Organization settings (`organization_settings`),
  branding (`organizations.logo_url`, `primary_color`), price lists, roles, and branches carry what differs between
  clients. A rule that cannot be expressed that way becomes a product feature that every client may switch on.
- **A client's public website is its own static site** under `sites/<client>` (Vaca Verde: `sites/vaca-verde`),
  deployed as its own small Railway service on the client's domain. It links into the shared platform and carries the
  client's brand, which the multi-tenant product must not.
- **POS terminals point at the platform's origin** (`Commerce:CloudApiBaseUrl`), and pairing binds each terminal to its
  organization and branch.
- **The silo model stays possible.** Configuration is per environment and the image is identical, so a client that
  contractually needs isolation can get its own Railway project running the same pipeline. That exception needs its own
  decision.

## Consequences

Work required before a **second** client goes live (none of it blocks Vaca Verde):

- **Public ordering is single-organization.** `GuestOrdering__OrganizationId` and `GuestOrdering__BranchId` are
  deployment-wide variables (`src/Commerce.Cloud.Api/Tenancy/GuestOrderTarget.cs`), so `/order` serves one
  organization per deployment. Resolving the organization from the request host (each client's `app.<domain>` mapped
  to the same service) has to replace them.
- **Platform domain.** The platform moves from `vaca-verde.com.ar` to a neutral Incoders domain (ADR-006 amendment).
- **Branding by host.** Branding is stored per organization, but the pages before sign-in cannot know which client
  they serve until the host resolves it.
- **File storage.** There is no upload feature yet. When one is built, files go to a private Railway Storage Bucket
  per environment with keys prefixed by organization, served through presigned URLs, and they need their own backup
  because database backups do not include them.

Onboarding a client on the shared model is operational work, not a deployment: create the organization
(`POST /account/organizations` as a system administrator, or `/account/bootstrap` for an organization with no users), its branches and administrator, configure settings and
branding, add its website under `sites/<client>` with its own service and domain, and pair its terminals.

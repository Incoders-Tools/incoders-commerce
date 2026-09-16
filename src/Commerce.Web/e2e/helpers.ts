// Shared helpers for the real-browser E2E suite (src/Commerce.Web/README.md
// "E2E tests" documents how to bring up the backend these hit).
//
// The dev HTTPS endpoint (https://localhost:56595, from
// src/Commerce.Cloud.Api/Properties/launchSettings.json) uses the ASP.NET
// Core Kestrel dev certificate, which is self-signed unless
// `dotnet dev-certs https --trust` has been run on this machine. Node's
// native fetch (unlike Playwright's browser context) does not read
// Playwright's `ignoreHTTPSErrors` option, so it needs this opt-out too.
process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0'

export interface SeededUser {
  organizationId: string
  email: string
  password: string
  userId: string
}

/**
 * Seeds a real Postgres-backed user via the TEST-ONLY, Development-gated
 * `/internal/test-seed/user` endpoint (Commerce.Cloud.Api/Endpoints/
 * TestSeedEndpoints.cs). Real product code never uses this endpoint — the
 * real user-creation path is `/account/bootstrap`, which (a) requires
 * reading a token off server stdout and (b) always seeds an EMPTY branch
 * scope. Neither is workable from an out-of-process browser test, and (b)
 * specifically makes the catalog-rename "allowed" path unreachable with a
 * bootstrap-only admin — see README.md's "Known limitation" section.
 *
 * A fresh, random `organizationId` is required per call: the real store's
 * bootstrap invariant (`PostgresUserAccountStore.TryCreateAsync`) allows
 * exactly one user per organization, ever.
 */
export async function seedUser(
  baseURL: string,
  options: { email: string; password: string; branchScope?: string[] },
): Promise<SeededUser> {
  const organizationId = crypto.randomUUID()
  const response = await fetch(`${baseURL}/internal/test-seed/user`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      organizationId,
      email: options.email,
      password: options.password,
      branchScope: options.branchScope ?? [],
    }),
  })

  if (!response.ok) {
    throw new Error(
      `seedUser failed (${response.status}): ${await response.text()}. ` +
        'Is Commerce.Cloud.Api running in Development with /internal/test-seed mapped? See README.md "E2E tests".',
    )
  }

  const body = (await response.json()) as { userId: string }
  return { organizationId, email: options.email, password: options.password, userId: body.userId }
}

export function uniqueEmail(prefix: string): string {
  return `${prefix}-${crypto.randomUUID()}@example.com`
}

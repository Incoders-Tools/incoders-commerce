import { defineConfig, devices } from '@playwright/test'

/**
 * Real-browser E2E against a REAL running Commerce.Cloud.Api (Postgres +
 * ASP.NET Core host), never a mocked fetch — see src/Commerce.Web/README.md
 * "E2E tests" for exactly how to bring that backend up before running this
 * suite.
 *
 * Base URL defaults to a local HTTPS proxy (`local-ssl-proxy`, port 5443) in
 * front of the plain-HTTP Cloud.Api (`dotnet run`, port 8080 — Program.cs's
 * explicit `ConfigureKestrel`/`ListenAnyIP($PORT)` call always binds plain
 * HTTP, overriding launchSettings.json's `https://localhost:56595` entry
 * entirely; real HTTPS only ever exists via Railway's edge TLS termination
 * in front of this same plain-HTTP Kestrel in production). The proxy is
 * required, not optional, for local E2E: Program.cs's sign-in cookie sets
 * `CookieSecurePolicy.Always`, and a browser will silently refuse to store a
 * Secure cookie unless the page itself was loaded over HTTPS — so hitting
 * port 8080 directly, or the Vite dev-server proxy (also plain HTTP), can
 * never carry the session cookie. Override with E2E_BASE_URL to point at a
 * different already-running (HTTPS) instance.
 */
const baseURL = process.env.E2E_BASE_URL ?? 'https://localhost:5443'

export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list']],
  timeout: 30_000,
  use: {
    baseURL,
    // The ASP.NET Core dev HTTPS certificate is a locally-trusted-or-not
    // self-signed cert depending on the machine; E2E only cares that the
    // connection is HTTPS (so the Secure cookie is honored), not that the
    // cert chain validates.
    ignoreHTTPSErrors: true,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
})

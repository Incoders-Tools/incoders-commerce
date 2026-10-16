// Copies the built SPA (dist/) into Commerce.Cloud.Api's wwwroot so the
// Playwright E2E suite can hit the SPA and the API from ONE origin served by
// `dotnet run`, exactly matching production shape (Dockerfile's web-build
// stage does the same COPY for the real deploy image — see repo-root
// Dockerfile). Same-origin matters for E2E specifically because
// Program.cs's sign-in cookie sets `SecurePolicy.Always`: browsers refuse a
// Secure cookie unless the page itself was loaded over HTTPS, so the Vite
// dev-server proxy (a plain-http origin) cannot carry the session cookie —
// see README.md "E2E tests" for the local HTTPS proxy this is paired with.
import { cpSync, rmSync, existsSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const distDir = path.resolve(here, '..', 'dist')
const wwwrootDir = path.resolve(here, '..', '..', 'Commerce.Cloud.Api', 'wwwroot')

if (!existsSync(distDir)) {
  console.error(`dist/ not found at ${distDir} — run "npm run build" first.`)
  process.exit(1)
}

rmSync(wwwrootDir, { recursive: true, force: true })
cpSync(distDir, wwwrootDir, { recursive: true })

console.log(`Copied ${distDir} -> ${wwwrootDir}`)

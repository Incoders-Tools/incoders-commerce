import { expect, test } from '@playwright/test'

/**
 * Closes the gap the commerce-web-routing verify-report flagged: the spec's
 * "Public routes are addressable on hard refresh" scenario (Requirement:
 * "Public Routes Render Without Auth Dependency") had no covering test.
 * `page.goto()` alone only proves client-side routing works after the SPA
 * has already booted — it does NOT prove the SERVER's SPA fallback
 * (`MapFallbackToFile("index.html")`) actually serves these paths on a cold
 * request. `page.reload()` forces a real full-page navigation back through
 * the server for the exact same URL, which is what a real hard refresh does.
 */
test.describe('public route addressability on hard refresh', () => {
  test('"/" survives a hard refresh', async ({ page }) => {
    await page.goto('/')
    await expect(page.getByRole('heading', { name: 'Commerce' })).toBeVisible()

    await page.reload()

    await expect(page.getByRole('heading', { name: 'Commerce' })).toBeVisible()
  })

  test('"/login" survives a hard refresh', async ({ page }) => {
    await page.goto('/login')
    await expect(page.getByRole('button', { name: /iniciar sesión/i })).toBeVisible()

    await page.reload()

    await expect(page.getByRole('button', { name: /iniciar sesión/i })).toBeVisible()
  })

  test('"/forgot-password" survives a hard refresh', async ({ page }) => {
    await page.goto('/forgot-password')
    await expect(page.getByRole('heading', { name: /recuperar/i })).toBeVisible()

    await page.reload()

    await expect(page.getByRole('heading', { name: /recuperar/i })).toBeVisible()
  })

  test('an unknown deep path falls back to the SPA instead of a raw 404', async ({ page }) => {
    const response = await page.goto('/this-route-does-not-exist')

    // The SERVER's fallback must serve index.html (200), not a bare 404 —
    // client-side routing then takes over: App.tsx's catch-all route
    // navigates unknown paths to "/".
    expect(response?.status()).toBe(200)
    await expect(page).toHaveURL(/\/$/)
    await expect(page.getByRole('heading', { name: 'Commerce' })).toBeVisible()
  })
})

test.describe('guarded route redirect', () => {
  test('an unauthenticated deep link to a staff route redirects to /login', async ({ page }) => {
    await page.goto('/app')

    await expect(page).toHaveURL(/\/login$/)
    await expect(page.getByRole('button', { name: /iniciar sesión/i })).toBeVisible()
  })
})

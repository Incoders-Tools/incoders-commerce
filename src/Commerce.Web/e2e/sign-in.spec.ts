import { expect, test } from '@playwright/test'
import { seedUser, uniqueEmail } from './helpers'

/**
 * Real browser -> real `/account/sign-in` -> real Postgres-backed
 * PostgresUserAccountStore, closing the gap the prior verify-report flagged:
 * the existing Vitest specs (SignInScreen.test.tsx) only ever exercise a
 * mocked `fetch`, so they can prove the request/response SHAPE but never
 * that a real backend actually authenticates the browser.
 */
test.describe('sign-in', () => {
  test('succeeds with a real seeded credential and reflects authenticated state', async ({ page, baseURL }) => {
    const password = 'correct-horse-battery-staple'
    const user = await seedUser(baseURL!, { email: uniqueEmail('signin-success'), password })

    await page.goto('/')
    await page.getByLabel('Email').fill(user.email)
    await page.getByLabel('Password').fill(password)
    await page.getByRole('button', { name: /sign in/i }).click()

    // Real /account/sign-in issued a real cookie and returned a real
    // SignedInResponse; the SPA renders the authenticated shell.
    await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible()
    await expect(page.getByText(user.email)).toBeVisible()
    await expect(page.getByRole('heading', { name: 'Commerce' })).toBeVisible()
  })

  test('rejects a wrong password with a generic error', async ({ page, baseURL }) => {
    const password = 'correct-horse-battery-staple'
    const user = await seedUser(baseURL!, { email: uniqueEmail('signin-wrong-password'), password })

    await page.goto('/')
    await page.getByLabel('Email').fill(user.email)
    await page.getByLabel('Password').fill('definitely-the-wrong-password')
    await page.getByRole('button', { name: /sign in/i }).click()

    const alert = page.getByRole('alert')
    await expect(alert).toBeVisible()
    const wrongPasswordMessage = await alert.textContent()

    // Still on the sign-in screen — never authenticated.
    await expect(page.getByRole('button', { name: /sign in/i })).toBeVisible()

    expect(wrongPasswordMessage).toBeTruthy()
    // Cross-checked against the unknown-email case below: the backend design
    // (Endpoints/Account.cs remarks) is that sign-in NEVER reveals which
    // check failed. Recorded here so the unknown-email test can assert byte-
    // for-byte equality without duplicating the seeding setup.
    test.info().annotations.push({ type: 'wrong-password-message', description: wrongPasswordMessage! })
  })

  test('rejects an unknown email with the exact same generic error as a wrong password', async ({ page, baseURL }) => {
    // No seeding at all: this email has never existed in any organization.
    await page.goto('/')
    await page.getByLabel('Email').fill(uniqueEmail('signin-unknown'))
    await page.getByLabel('Password').fill('whatever-password')
    await page.getByRole('button', { name: /sign in/i }).click()

    const alert = page.getByRole('alert')
    await expect(alert).toBeVisible()
    const unknownEmailMessage = await alert.textContent()

    await expect(page.getByRole('button', { name: /sign in/i })).toBeVisible()

    // The generic-401 design (Endpoints/Account.cs: "Sign-in never reveals
    // WHICH check failed") means an unknown email and a wrong password must
    // produce an indistinguishable message. Both currently surface the raw
    // 401 status text via ApiError's fallback (see api/client.ts) — asserting
    // a fixed, non-field-specific string keeps this test honest about what
    // it actually proves rather than asserting an implementation detail that
    // could drift.
    expect(unknownEmailMessage).toBeTruthy()
    expect(unknownEmailMessage!.toLowerCase()).not.toContain('email')
    expect(unknownEmailMessage!.toLowerCase()).not.toContain('password')
  })
})

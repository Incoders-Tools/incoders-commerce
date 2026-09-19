import { expect, test } from '@playwright/test'
import { seedUser, uniqueEmail } from './helpers'

/**
 * proposal.md success criterion: "A `seller` (non-admin) cannot reach the
 * customer-management screen on either client." `RequireAdmin` redirects a
 * `seller` to `/app/catalog`; the server's `ManageUsers` check on every
 * `/customers` call remains the real gate.
 */
test.describe('customer registry admin gating', () => {
  test('a business-admin sees the Customers tab and can reach the screen', async ({ page, baseURL }) => {
    const password = 'correct-horse-battery-staple'
    const admin = await seedUser(baseURL!, { email: uniqueEmail('customers-admin'), password })

    await page.goto('/login')
    await page.getByLabel('Email').fill(admin.email)
    await page.getByLabel('Password').fill(password)
    await page.getByRole('button', { name: /sign in/i }).click()
    await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible()

    await expect(page.getByRole('link', { name: 'Customers' })).toBeVisible()
    await page.getByRole('link', { name: 'Customers' }).click()
    await expect(page.getByRole('heading', { name: 'Customers' })).toBeVisible()
  })

  test('a seller has no Customers tab and is redirected away from /app/customers', async ({ page, baseURL }) => {
    const password = 'correct-horse-battery-staple'
    const admin = await seedUser(baseURL!, { email: uniqueEmail('customers-seller-admin'), password })
    const sellerEmail = uniqueEmail('customers-seller')
    const sellerPassword = 'correct-horse-battery-staple'

    await page.goto('/login')
    await page.getByLabel('Email').fill(admin.email)
    await page.getByLabel('Password').fill(password)
    await page.getByRole('button', { name: /sign in/i }).click()
    await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible()

    // Real, cookie-authorized ManageUsers call — the admin's own session,
    // exactly what a real admin does to provision a seller.
    const createUserResponse = await page.request.post('/account/users', {
      data: {
        email: sellerEmail,
        password: sellerPassword,
        roleNames: ['seller'],
        branchIds: [admin.branchId],
      },
    })
    expect(createUserResponse.ok()).toBeTruthy()

    await page.getByRole('button', { name: 'Sign out' }).click();
    await expect(page).toHaveURL(/\/login$/)

    await page.getByLabel('Email').fill(sellerEmail)
    await page.getByLabel('Password').fill(sellerPassword)
    await page.getByRole('button', { name: /sign in/i }).click()
    await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible()

    // The Customers tab is hidden for a seller — there is no clickable path
    // into the screen from the UI (design.md "Web admin gating": a UX
    // affordance; the server's ManageUsers check on every /customers call is
    // the real gate, exercised directly by CustomerRegistryTests).
    await expect(page.getByRole('link', { name: 'Customers' })).not.toBeVisible()

    // In-SPA (client-side) navigation attempt to the guarded path — no full
    // page reload, so the still-mounted AuthProvider's `user` state (and its
    // `permissions`) is exercised directly, proving `RequireAdmin` itself
    // denies a seller rather than merely hiding the tab. A raw
    // `page.goto('/app/customers')` here would not isolate this: it forces a
    // full reload, and `AuthProvider` has no cookie-rehydration-on-mount
    // (pre-existing, out of this unit's scope) — every guarded route,
    // including ones a seller CAN reach, redirects to `/login` on a cold
    // load, not just `/app/customers`.
    await page.evaluate(() => {
      window.history.pushState({}, '', '/app/customers')
      window.dispatchEvent(new PopStateEvent('popstate'))
    })
    await expect(page).toHaveURL(/\/app\/catalog$/)
  })
})

import { expect, test } from '@playwright/test'
import { expectSignedIn, seedUser, signOut, uniqueEmail } from './helpers'

/**
 * Purchasing (receptions and stock) shares the suppliers' gate: the nav items
 * are a UX affordance for `ManageUsers`, `RequireAdmin` redirects everyone
 * else, and the server's check on every `/purchases` and `/stock` call is the
 * real boundary.
 */
test.describe('purchasing admin gating', () => {
  test('a business-admin sees the Purchasing tabs and can reach both screens', async ({ page, baseURL }) => {
    const password = 'correct-horse-battery-staple'
    const admin = await seedUser(baseURL!, { email: uniqueEmail('purchasing-admin'), password })

    await page.goto('/login')
    await page.getByLabel('Correo electrónico').fill(admin.email)
    await page.getByLabel('Contraseña').fill(password)
    await page.getByRole('button', { name: /iniciar sesión/i }).click()
    await expectSignedIn(page)

    await page.getByRole('link', { name: 'Recepciones' }).click()
    await expect(page.getByRole('heading', { name: 'Recepciones' })).toBeVisible()

    await page.getByRole('link', { name: 'Stock' }).click()
    await expect(page.getByRole('heading', { name: 'Stock' })).toBeVisible()
  })

  test('a seller has no Purchasing tabs and is redirected away from /app/receptions', async ({ page, baseURL }) => {
    const password = 'correct-horse-battery-staple'
    const admin = await seedUser(baseURL!, { email: uniqueEmail('purchasing-seller-admin'), password })
    const sellerEmail = uniqueEmail('purchasing-seller')

    await page.goto('/login')
    await page.getByLabel('Correo electrónico').fill(admin.email)
    await page.getByLabel('Contraseña').fill(password)
    await page.getByRole('button', { name: /iniciar sesión/i }).click()
    await expectSignedIn(page)

    const createUserResponse = await page.request.post('/account/users', {
      data: { email: sellerEmail, password, roleNames: ['seller'], branchIds: [admin.branchId] },
    })
    expect(createUserResponse.ok()).toBeTruthy()

    await signOut(page)
    await expect(page).toHaveURL(/\/login$/)

    await page.getByLabel('Correo electrónico').fill(sellerEmail)
    await page.getByLabel('Contraseña').fill(password)
    await page.getByRole('button', { name: /iniciar sesión/i }).click()
    await expectSignedIn(page)

    await expect(page.getByRole('link', { name: 'Recepciones' })).not.toBeVisible()
    await expect(page.getByRole('link', { name: 'Stock' })).not.toBeVisible()

    // In-SPA navigation (no reload) so the mounted AuthProvider's permissions are what denies it.
    for (const path of ['/app/receptions', '/app/stock']) {
      await page.evaluate((target) => {
        window.history.pushState({}, '', target)
        window.dispatchEvent(new PopStateEvent('popstate'))
      }, path)
      await expect(page).toHaveURL(/\/app\/catalog$/)
    }
  })
})

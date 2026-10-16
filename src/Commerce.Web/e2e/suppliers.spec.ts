import { expect, test } from '@playwright/test'
import { expectSignedIn, seedUser, signOut, uniqueEmail } from './helpers'

/**
 * Suppliers share the customers' gate: the nav items are a UX affordance for
 * `ManageUsers`, `RequireAdmin` redirects everyone else, and the server's
 * check on every `/suppliers` call is the real boundary.
 */
test.describe('supplier registry admin gating', () => {
  test('a business-admin sees the Suppliers tabs and can reach both screens', async ({ page, baseURL }) => {
    const password = 'correct-horse-battery-staple'
    const admin = await seedUser(baseURL!, { email: uniqueEmail('suppliers-admin'), password })

    await page.goto('/login')
    await page.getByLabel('Correo electrónico').fill(admin.email)
    await page.getByLabel('Contraseña').fill(password)
    await page.getByRole('button', { name: /iniciar sesión/i }).click()
    await expectSignedIn(page)

    await page.getByRole('link', { name: 'Proveedores' }).click()
    await expect(page.getByRole('heading', { name: 'Proveedores' })).toBeVisible()

    await page.getByRole('link', { name: 'Rubros de proveedor' }).click()
    await expect(page.getByRole('heading', { name: 'Rubros de proveedor' })).toBeVisible()
  })

  test('a seller has no Suppliers tab and is redirected away from /app/suppliers', async ({ page, baseURL }) => {
    const password = 'correct-horse-battery-staple'
    const admin = await seedUser(baseURL!, { email: uniqueEmail('suppliers-seller-admin'), password })
    const sellerEmail = uniqueEmail('suppliers-seller')

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

    await expect(page.getByRole('link', { name: 'Proveedores' })).not.toBeVisible()
    await expect(page.getByRole('link', { name: 'Rubros de proveedor' })).not.toBeVisible()

    // In-SPA navigation (no reload) so the mounted AuthProvider's permissions are what denies it.
    await page.evaluate(() => {
      window.history.pushState({}, '', '/app/suppliers')
      window.dispatchEvent(new PopStateEvent('popstate'))
    })
    await expect(page).toHaveURL(/\/app\/catalog$/)
  })
})

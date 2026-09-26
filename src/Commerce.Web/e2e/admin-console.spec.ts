import { expect, test } from '@playwright/test'
import { seedUser, uniqueEmail } from './helpers'

test('a real business-admin can use Users and Branches but cannot reach Organizations', async ({ page, baseURL }) => {
  const password = 'correct-horse-battery-staple'
  const admin = await seedUser(baseURL!, { email: uniqueEmail('admin-console'), password })
  await page.goto('/login')
  await page.getByLabel('Correo electrónico').fill(admin.email)
  await page.getByLabel('Contraseña').fill(password)
  await page.getByRole('button', { name: /iniciar sesión/i }).click()
  await expect(page.getByRole('link', { name: 'Usuarios' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Sucursales' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Organizaciones' })).not.toBeVisible()
  await page.getByRole('link', { name: 'Usuarios' }).click()
  await page.getByLabel('Correo electrónico del usuario').fill(uniqueEmail('staff'))
  await page.getByLabel('Contraseña del usuario').fill(password)
  await page.getByRole('button', { name: 'Crear usuario' }).click()
  await expect(page.getByText(/staff-/)).toBeVisible()
  await page.getByRole('link', { name: 'Sucursales' }).click()
  await page.getByLabel('Nombre de la sucursal').fill('Runtime branch')
  await page.getByRole('button', { name: 'Crear sucursal' }).click()
  await expect(page.getByText('Runtime branch')).toBeVisible()
  await page.evaluate(() => { window.history.pushState({}, '', '/app/organizations'); window.dispatchEvent(new PopStateEvent('popstate')) })
  await expect(page).toHaveURL(/\/app\/catalog$/)
})

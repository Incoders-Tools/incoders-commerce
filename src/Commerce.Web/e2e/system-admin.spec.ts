import { expect, test } from '@playwright/test'
import { seedUser, uniqueEmail } from './helpers'

test('a migrated system admin sees Organizations and can onboard an organization', async ({ page }) => {
  const organizationName = `Runtime onboarded ${Date.now()}`
  await page.goto('/login')
  await page.getByLabel('Correo electrónico').fill(process.env.SYSADMIN_EMAIL!)
  await page.getByLabel('Contraseña').fill(process.env.SYSADMIN_PASSWORD!)
  await page.getByRole('button', { name: /iniciar sesión/i }).click()
  await expect(page.getByRole('link', { name: 'Organizaciones' })).toBeVisible()
  await page.getByRole('link', { name: 'Organizaciones' }).click()
  await page.getByRole('button', { name: 'Nueva organización' }).click()
  await page.getByLabel('Nombre de la organización').fill(organizationName)
  await page.getByLabel('Correo electrónico del administrador').fill(uniqueEmail('runtime-new-admin'))
  await page.getByLabel('Contraseña del administrador').fill('correct-horse-battery-staple')
  await page.getByRole('button', { name: 'Crear organización' }).click()
  await expect(page.getByText(organizationName)).toBeVisible()
})

// platform-administration spec, "Sysadmin Acts On A Selected Organization":
// with no organization selected the sysadmin sees only Organizations (and
// other platform screens) — Catalog/Orders/Branches/etc. are all tenant
// modules. Opening an organization from the list selects it and jumps to
// its Branches screen, where the sysadmin can now create a branch despite
// holding zero org-scoped Permission of their own.
test('a system admin opens an organization and manages its branches', async ({ page, baseURL }) => {
  // seedUser's org is always named "E2E Test Organization" (TestSeedEndpoints.cs)
  // — searching by that name and opening the first (freshest) match is
  // enough to reach a real org this sysadmin does not belong to.
  await seedUser(baseURL!, { email: uniqueEmail('sysadmin-open-target'), password: 'correct-horse-battery-staple' })

  await page.goto('/login')
  await page.getByLabel('Correo electrónico').fill(process.env.SYSADMIN_EMAIL!)
  await page.getByLabel('Contraseña').fill(process.env.SYSADMIN_PASSWORD!)
  await page.getByRole('button', { name: /iniciar sesión/i }).click()

  // No organization selected yet: only Organizations is reachable.
  await expect(page.getByRole('link', { name: 'Organizaciones' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Catálogo' })).not.toBeVisible()
  await expect(page.getByRole('link', { name: 'Sucursales' })).not.toBeVisible()

  await page.getByRole('link', { name: 'Organizaciones' }).click()
  await page.getByLabel('Buscar organizaciones').fill('E2E Test Organization')
  await page.getByRole('button', { name: 'Abrir' }).first().click()

  await expect(page.getByRole('link', { name: 'Sucursales' })).toBeVisible()
  await page.getByRole('link', { name: 'Sucursales' }).click()
  const branchName = `Sysadmin Branch ${Date.now()}`
  await page.getByLabel('Nombre de la sucursal').fill(branchName)
  await page.getByRole('button', { name: 'Crear sucursal' }).click()
  await expect(page.getByText(branchName)).toBeVisible()

  // Deselecting the organization returns to the platform-only view.
  await page.getByLabel('Organización').selectOption({ label: 'Sin organización' })
  await expect(page.getByRole('link', { name: 'Sucursales' })).not.toBeVisible()
})

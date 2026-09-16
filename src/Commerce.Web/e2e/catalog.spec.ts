import { expect, test } from '@playwright/test'
import { seedUser, uniqueEmail } from './helpers'

/**
 * Catalog rename is gated by TenantAuthorizationService.Authorize, which
 * requires `actor.BranchScope.Contains(request.TargetBranchId)`
 * (Commerce.Application/Access/TenantAuthorizationService.cs). A REAL
 * bootstrap-created admin (the only way to create a user through the
 * product's own UI/API) always gets an EMPTY branch scope — there is no
 * branch-persistence feature anywhere in this system yet (see
 * Endpoints/Account.cs's bootstrap remarks) — so that admin can NEVER pass
 * this check, for any target branch. That "denied" behavior is itself real,
 * permanent, and worth covering end to end (first test below).
 *
 * To also exercise the "allowed" path honestly (rather than leaving it
 * completely uncovered), this suite uses the TEST-ONLY
 * `/internal/test-seed/user` seam (see e2e/helpers.ts and
 * Endpoints/TestSeedEndpoints.cs) to seed a user WITH a branch scope — a
 * capability the real product intentionally does not expose through any
 * real endpoint today. That test is clearly the seam being exercised, not a
 * real onboarding flow; if branch persistence ships for real, this seam
 * should be replaced with the real assignment flow.
 */
test.describe('catalog rename', () => {
  test('a bootstrap-shaped admin (empty branch scope) is denied for any branch — documented product limitation', async ({
    page,
    baseURL,
  }) => {
    const password = 'correct-horse-battery-staple'
    const user = await seedUser(baseURL!, { email: uniqueEmail('catalog-denied'), password, branchScope: [] })

    await page.goto('/')
    await page.getByLabel('Email').fill(user.email)
    await page.getByLabel('Password').fill(password)
    await page.getByRole('button', { name: /sign in/i }).click()
    await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible()

    // Catalog is the default tab.
    await page.locator('#productId').fill(crypto.randomUUID())
    await page.locator('#targetBranchId').fill(crypto.randomUUID())
    await page.locator('#currentName').fill('Original Name')
    await page.locator('#categoryId').fill(crypto.randomUUID())
    await page.locator('#defaultUnitId').fill(crypto.randomUUID())
    await page.locator('#newName').fill('Renamed via E2E')

    await page.getByRole('button', { name: /rename product/i }).click()

    await expect(page.getByTestId('catalog-outcome')).toHaveText('Denied: not-found')
  })

  test('a user seeded WITH the target branch in scope (test-seam only) is allowed to rename', async ({
    page,
    baseURL,
  }) => {
    const password = 'correct-horse-battery-staple'
    const targetBranchId = crypto.randomUUID()
    const user = await seedUser(baseURL!, {
      email: uniqueEmail('catalog-allowed'),
      password,
      branchScope: [targetBranchId],
    })

    await page.goto('/')
    await page.getByLabel('Email').fill(user.email)
    await page.getByLabel('Password').fill(password)
    await page.getByRole('button', { name: /sign in/i }).click()
    await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible()

    await page.locator('#productId').fill(crypto.randomUUID())
    await page.locator('#targetBranchId').fill(targetBranchId)
    await page.locator('#currentName').fill('Original Name')
    await page.locator('#categoryId').fill(crypto.randomUUID())
    await page.locator('#defaultUnitId').fill(crypto.randomUUID())
    await page.locator('#newName').fill('Renamed via E2E')

    await page.getByRole('button', { name: /rename product/i }).click()

    await expect(page.getByTestId('catalog-outcome')).toHaveText('Renamed to "Renamed via E2E".')
  })
})

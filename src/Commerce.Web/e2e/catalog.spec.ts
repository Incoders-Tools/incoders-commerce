import { expect, test } from '@playwright/test'
import { seedUser, uniqueEmail } from './helpers'

/**
 * Catalog rename is gated by TenantAuthorizationService.Authorize, which
 * requires `actor.BranchScope.Contains(request.TargetBranchId)`
 * (Commerce.Application/Access/TenantAuthorizationService.cs).
 *
 * As of commerce-organization-persistence, `/account/bootstrap` (and this
 * suite's `/internal/test-seed/user` seam, which now routes through the same
 * `PostgresOrganizationStore.TryCreateBootstrapAsync` transaction) creates a
 * REAL persisted branch and seeds the admin's branch scope with that real
 * branch id. Both tests below exercise genuinely real authorization paths:
 * "allowed" targets the branch the seeded admin actually belongs to;
 * "denied" targets a DIFFERENT admin's branch — a real cross-branch denial,
 * not an artifact of an empty-scope limitation that no longer exists.
 */
test.describe('catalog rename', () => {
  test('a user renaming a product on a DIFFERENT organization\'s branch is denied', async ({ page, baseURL }) => {
    const password = 'correct-horse-battery-staple'
    const user = await seedUser(baseURL!, { email: uniqueEmail('catalog-denied'), password })
    // A second, unrelated seeded user's branch — not in `user`'s branch scope.
    const otherOrgUser = await seedUser(baseURL!, { email: uniqueEmail('catalog-denied-other'), password })

    await page.goto('/')
    await page.getByLabel('Email').fill(user.email)
    await page.getByLabel('Password').fill(password)
    await page.getByRole('button', { name: /sign in/i }).click()
    await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible()

    // Catalog is the default tab.
    await page.locator('#productId').fill(crypto.randomUUID())
    await page.locator('#targetBranchId').fill(otherOrgUser.branchId)
    await page.locator('#currentName').fill('Original Name')
    await page.locator('#categoryId').fill(crypto.randomUUID())
    await page.locator('#defaultUnitId').fill(crypto.randomUUID())
    await page.locator('#newName').fill('Renamed via E2E')

    await page.getByRole('button', { name: /rename product/i }).click()

    await expect(page.getByTestId('catalog-outcome')).toHaveText('Denied: not-found')
  })

  test('a user renaming a product on their OWN branch is allowed', async ({ page, baseURL }) => {
    const password = 'correct-horse-battery-staple'
    const user = await seedUser(baseURL!, { email: uniqueEmail('catalog-allowed'), password })

    await page.goto('/')
    await page.getByLabel('Email').fill(user.email)
    await page.getByLabel('Password').fill(password)
    await page.getByRole('button', { name: /sign in/i }).click()
    await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible()

    await page.locator('#productId').fill(crypto.randomUUID())
    await page.locator('#targetBranchId').fill(user.branchId)
    await page.locator('#currentName').fill('Original Name')
    await page.locator('#categoryId').fill(crypto.randomUUID())
    await page.locator('#defaultUnitId').fill(crypto.randomUUID())
    await page.locator('#newName').fill('Renamed via E2E')

    await page.getByRole('button', { name: /rename product/i }).click()

    await expect(page.getByTestId('catalog-outcome')).toHaveText('Renamed to "Renamed via E2E".')
  })
})

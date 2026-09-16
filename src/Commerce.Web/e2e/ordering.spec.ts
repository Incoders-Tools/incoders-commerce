import { expect, test } from '@playwright/test'
import { seedUser, uniqueEmail } from './helpers'

/**
 * Order submission is NOT gated by TenantAuthorizationService's branch-scope
 * check (Endpoints/Ordering.cs builds a CustomerOrderingAccess and delegates
 * to CustomerCatalogAccessService, a structurally different, customer-facing
 * concept — see CustomerCatalogAccessService.cs's remarks: "Distinct from
 * TenantAuthorizationService, which authorizes staff/branch actors ... a
 * customer credential has neither and must never be modeled as one"). Its
 * only checks are `access.IsEnabled` and an organization match, both of
 * which the SPA already satisfies unconditionally (OrderScreen hardcodes
 * `accessEnabled: true`, and the organization is always the signed-in
 * actor's own). This makes order submission genuinely, fully E2E-testable
 * end to end against the real backend today — unlike catalog rename (see
 * catalog.spec.ts and README.md's "Known limitation").
 */
test.describe('order submission', () => {
  test('a signed-in user can submit a real order and receive a real Accepted outcome', async ({ page, baseURL }) => {
    const password = 'correct-horse-battery-staple'
    // Branch scope is irrelevant to this flow (see remarks above) — an
    // empty scope, exactly what a real bootstrap admin gets, is used here
    // deliberately to prove ordering does NOT depend on it.
    const user = await seedUser(baseURL!, { email: uniqueEmail('order-submit'), password })

    await page.goto('/')
    await page.getByLabel('Email').fill(user.email)
    await page.getByLabel('Password').fill(password)
    await page.getByRole('button', { name: /sign in/i }).click()
    await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible()

    await page.getByRole('button', { name: 'Orders' }).click()

    await page.locator('#customerId').fill(crypto.randomUUID())
    await page.locator('#accessCredential').fill(crypto.randomUUID())
    await page.locator('#destinationBranchId').fill(crypto.randomUUID())
    await page.locator('#actorId').fill(user.userId)
    await page.locator('#productId').fill(crypto.randomUUID())
    await page.locator('#productName').fill('E2E Test Product')
    await page.locator('#presentationId').fill(crypto.randomUUID())
    await page.locator('#presentationName').fill('E2E Test Presentation')
    await page.locator('#unitId').fill(crypto.randomUUID())
    await page.locator('#quantity').fill('3')

    await page.getByRole('button', { name: /submit order/i }).click()

    // Real CloudOrderSubmissionService -> CloudOrderStore round trip: the
    // order is accepted (destination delivery itself stays honestly
    // "pending" per ADR-003, since no destination branch registry exists —
    // but overall submission acceptance is real and asserted here).
    await expect(page.getByTestId('order-outcome')).toHaveText('Order accepted.')
  })
})

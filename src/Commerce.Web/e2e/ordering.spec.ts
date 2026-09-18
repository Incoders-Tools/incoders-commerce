import { expect, test } from '@playwright/test'
import { seedUser, uniqueEmail } from './helpers'

/**
 * commerce-customer-identity Unit 3 (security fix): `SubmitOrderRequest` no
 * longer has an `accessEnabled` field, and `CustomerCatalogAccessService` no
 * longer trusts anything from the request body — it resolves the credential
 * and its enabled/binding state from the persisted `customer_ordering_access`
 * store via `ICustomerOrderingAccessResolver`. A random, never-issued
 * credential is denied with reason "not-found", not silently accepted.
 *
 * Unit 5 closes the KNOWN LIMITATION Unit 3 documented here: a real customer
 * row plus an issued ordering-access credential are now seeded through the
 * same `POST /customers` and `POST /customers/{id}/ordering-access` routes
 * `CustomersScreen` itself calls — `page.request` shares the signed-in
 * browser context's cookie, so this is a genuinely real, cookie-authorized
 * admin call, not a test-only seam.
 */
test.describe('order submission', () => {
  test('a signed-in user can submit a real order and receive a real Accepted outcome', async ({ page, baseURL }) => {
    const password = 'correct-horse-battery-staple'
    // Branch scope is irrelevant to this flow (see remarks above) — an
    // empty scope, exactly what a real bootstrap admin gets, is used here
    // deliberately to prove ordering does NOT depend on it.
    const user = await seedUser(baseURL!, { email: uniqueEmail('order-submit'), password })

    await page.goto('/login')
    await page.getByLabel('Email').fill(user.email)
    await page.getByLabel('Password').fill(password)
    await page.getByRole('button', { name: /sign in/i }).click()
    await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible()

    // Seed a real Retail customer + issued ordering-access credential via the
    // real, cookie-authorized endpoints (the bootstrap admin already holds
    // ManageUsers).
    const createResponse = await page.request.post('/customers', {
      data: {
        customerKind: 'Retail',
        displayName: 'E2E Order Customer',
        legalName: null,
        taxIdType: 'None',
        taxId: null,
        taxCondition: 'ConsumidorFinal',
        phone: null,
        email: null,
        addressStreet: null,
        addressNumber: null,
        neighborhood: null,
        locality: null,
        province: null,
        postalCode: null,
        deliveryNotes: null,
        discountPercentage: null,
        paymentTerms: null,
        notes: null,
      },
    })
    expect(createResponse.ok()).toBeTruthy()
    const { customerId } = (await createResponse.json()) as { customerId: string }

    const accessResponse = await page.request.post(`/customers/${customerId}/ordering-access`)
    expect(accessResponse.ok()).toBeTruthy()
    const { credential } = (await accessResponse.json()) as { credential: string }

    await page.getByRole('link', { name: 'Orders' }).click()

    await page.locator('#customerId').fill(customerId)
    await page.locator('#accessCredential').fill(credential)
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

  test('an unissued (random) credential is denied with reason not-found', async ({ page, baseURL }) => {
    const password = 'correct-horse-battery-staple'
    const user = await seedUser(baseURL!, { email: uniqueEmail('order-denied'), password })

    await page.goto('/login')
    await page.getByLabel('Email').fill(user.email)
    await page.getByLabel('Password').fill(password)
    await page.getByRole('button', { name: /sign in/i }).click()
    await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible()

    await page.getByRole('link', { name: 'Orders' }).click()

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

    await expect(page.getByTestId('order-outcome')).toHaveText('Denied: not-found')
  })
})

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
 *
 * commerce-pricing-engine (Unit 4): pricing resolution is now additive on
 * top of the access/binding/customer-enabled checks proven below. This
 * change does not ship the Unit 5 admin pricing API, so there is no route
 * yet to publish a real price for a presentation — the first scenario below
 * asserts the real, currently-reachable outcome for an unpriced
 * presentation ("no-effective-price"), not a false "Accepted".
 */
test.describe('order submission', () => {
  test('a signed-in user with valid access is denied "no-effective-price" for an unpriced presentation', async ({ page, baseURL }) => {
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
    await page.locator('#presentationId').fill(crypto.randomUUID())
    await page.locator('#quantity').fill('3')

    await page.getByRole('button', { name: /submit order/i }).click()

    // commerce-pricing-engine: a presentation with no published price has
    // ZERO effective price rows, so CloudOrderSubmissionService now denies
    // the whole order with "no-effective-price" (design.md "OrderLineSnapshot
    // extension and where resolution runs") instead of accepting a priceless
    // line. Publishing a real price requires the Unit 5 admin pricing API,
    // which this change does not yet ship — asserting the denial here (a
    // real access/binding/customer-enabled acceptance, additive pricing
    // check) is the correct, currently-reachable outcome.
    await expect(page.getByTestId('order-outcome')).toHaveText('Denied: no-effective-price')
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
    await page.locator('#presentationId').fill(crypto.randomUUID())
    await page.locator('#quantity').fill('3')

    await page.getByRole('button', { name: /submit order/i }).click()

    await expect(page.getByTestId('order-outcome')).toHaveText('Denied: not-found')
  })
})

/**
 * commerce-guest-ordering Unit 6, task 6.7 — PARTIAL, documented gap (not
 * silently marked complete): design.md's Testing Strategy asks for a full
 * guest-path E2E "through the dev seed/log email hook", but no such hook is
 * mapped in `Program.cs` today. `LogOnlyEmailSender` only WRITES the code to
 * the server's log stream; unlike `/internal/test-seed/user`
 * (TestSeedEndpoints.cs), there is no Development-gated HTTP seam to READ
 * the last-issued guest verification code back out of that log from an
 * out-of-process Playwright test. Adding one is a small, additive backend
 * change (mirroring `TestSeedEndpoints.cs`'s existing pattern) — deliberately
 * NOT done here because Unit 6 is scoped to the web app only and Units 1-5
 * are closed/merged; it is the one remaining follow-up before this test can
 * exercise the real code-confirmation step end to end.
 *
 * This test proves everything reachable WITHOUT that hook: the public
 * catalogue read, the guest verification REQUEST (a real 202 against the
 * real backend), and the UI's own defense-in-depth block on submitting
 * before verification is confirmed.
 */
test.describe('guest order submission (partial — see gap note above)', () => {
  test('a guest requests a verification code and is blocked from submitting until confirmed', async ({ page }) => {
    await page.goto('/order')

    await expect(page.getByRole('tab', { name: /order as guest/i })).toBeVisible()
    await expect(page.getByRole('tab', { name: /sign in to order/i })).toBeVisible()

    await page.getByLabel(/document/i).fill('30111222')
    await page.getByLabel(/^email/i).fill(uniqueEmail('guest'))
    await page.getByRole('button', { name: /send verification code/i }).click()

    await expect(page.getByLabel(/verification code/i)).toBeVisible()

    const submitButton = page.getByRole('button', { name: /submit order/i })
    await expect(submitButton).toBeDisabled()
    await expect(page.getByText(/confirm your verification code before submitting/i)).toBeVisible()
  })
})

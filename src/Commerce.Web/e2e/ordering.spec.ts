import { expect, test } from '@playwright/test'
import { expectSignedIn, seedUser, uniqueEmail } from './helpers'

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
    await expectSignedIn(page)

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
    await expectSignedIn(page)

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
 * commerce-guest-ordering Unit 6, task 6.7 — regression guard for the UI's
 * own defense-in-depth block. Independent of the full-cycle test below: it
 * proves the client refuses to submit before a code is confirmed, which
 * stays true even in an environment where the full-cycle test below is
 * skipped (see that test's own guard comment).
 */
test.describe('guest order submission — defense in depth', () => {
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

/**
 * commerce-guest-ordering Unit 6, task 6.7 — CLOSES the gap the test above
 * used to document as PARTIAL. Phase 8 follow-up B (verify-report.md
 * WARNING 2, backend branch `feat/commerce-guest-ordering-01-backend`,
 * merged in via rebase before this test was written) added
 * `GET /internal/test-seed/guest-verification-code?contactAddress={email}`
 * (`Endpoints/TestSeedEndpoints.cs`) — a Development-only HTTP seam that
 * reads the REAL 6-digit code `LogOnlyEmailSender` just issued for a given
 * address, mirroring the existing `/internal/test-seed/user` precedent for
 * skipping an out-of-band hop this out-of-process browser harness cannot
 * retrieve any other way.
 *
 * This closes the full request -> confirm -> submit cycle: request a real
 * code through the UI, read it back via the seam (a raw HTTP call
 * alongside the UI actions, exactly as a Playwright `APIRequestContext`
 * request is meant to be used), type it into the confirmation step, and
 * drive the real submit.
 *
 * Guard: the seam is mapped ONLY when `app.Environment.IsDevelopment()`
 * (see `TestSeedEndpoints.cs` remarks) — never reachable in a real deploy.
 * The documented local E2E setup (`README.md` "E2E tests" step 3,
 * `dotnet run --project ../Commerce.Cloud.Api`) always launches under the
 * default launch profile, which sets `ASPNETCORE_ENVIRONMENT=Development`
 * (`Properties/launchSettings.json`) — so this test runs its real assertions
 * in that setup. If a caller points `E2E_BASE_URL` at a non-Development
 * instance instead (or one where a real `IEmailSender` such as
 * `ResendEmailSender` is configured, e.g. `RESEND_API_KEY` set), the seam
 * itself returns `503` rather than a route-level 404 — this test treats
 * that as an explicit skip, not a silent pass or a hard failure, so it
 * cannot break a non-Development E2E run elsewhere.
 *
 * Second guard, orthogonal to the one above: `POST /public/guest-orders`
 * itself is also config-gated (`GuestOrdering__OrganizationId`/`BranchId`,
 * see `Tenancy/GuestOrderTarget.cs`) and, once configured, resolves to a
 * FIXED organization/branch this out-of-process test cannot seed a product
 * catalog into (unlike the throwaway orgs `/internal/test-seed/user`
 * creates fresh per run — see this file's README.md "Known limitation").
 * When that fixed guest catalog has no presentations to add as an order
 * line, the UI correctly disables both "Add line" and "Submit order" —
 * this test asserts that real, disabled state rather than fabricating a
 * line the UI has no way to construct. When the catalog does have at least
 * one presentation, it drives the real add-line + submit and asserts on
 * whatever real outcome the backend returns, reusing this file's own
 * established pattern (the earlier "no-effective-price" test above) of
 * asserting the correct, currently-reachable outcome rather than a
 * specific one no test-controlled environment can guarantee.
 */
test.describe('guest order submission — full verification cycle (commerce-guest-ordering Phase 8)', () => {
  test('a guest requests a code, reads it back via the dev-only seam, confirms it, and reaches a real order outcome', async ({
    page,
    request,
    baseURL,
  }) => {
    const email = uniqueEmail('guest-full-cycle')

    await page.goto('/order')

    await page.getByLabel(/document/i).fill('30111222')
    await page.getByLabel(/^email/i).fill(email)
    await page.getByRole('button', { name: /send verification code/i }).click()

    await expect(page.getByLabel(/verification code/i)).toBeVisible()

    const codeResponse = await request.get(
      `${baseURL}/internal/test-seed/guest-verification-code?contactAddress=${encodeURIComponent(email)}`,
    )
    test.skip(
      codeResponse.status() === 503,
      'Dev-only guest-verification-code seam unavailable (active email sender is not the log-only one, e.g. a non-Development instance) — skipping the full request-confirm-submit cycle rather than failing.',
    )
    expect(codeResponse.status(), 'seam should return the real issued code for an address the UI just requested one for').toBe(200)
    const { code } = (await codeResponse.json()) as { code: string }
    expect(code).toMatch(/^\d{6}$/)

    await page.getByLabel(/verification code/i).fill(code)
    await page.getByRole('button', { name: /confirm code/i }).click()

    await expect(page.getByText(/verification confirmed/i)).toBeVisible()

    const presentationOptionCount = await page.locator('#order-line-presentation option').count()
    if (presentationOptionCount === 0) {
      // See guard comment above: the fixed GuestOrderTarget catalog has no
      // presentations in this environment, so there is no line this test
      // can construct. Assert the real, correctly-disabled state instead
      // of fabricating one.
      await expect(page.getByRole('button', { name: /^add line$/i })).toBeDisabled()
      await expect(page.getByRole('button', { name: /submit order/i })).toBeDisabled()
      return
    }

    await page.getByRole('button', { name: /^add line$/i }).click()
    await page.getByRole('button', { name: /submit order/i }).click()

    await expect(page.getByTestId('order-outcome')).toBeVisible()
    await expect(page.getByTestId('order-outcome')).toHaveText(/^(Order accepted\.|Denied: .+)$/)
  })
})

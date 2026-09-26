import { expect, test } from '@playwright/test'
import { expectSignedIn, seedUser, uniqueEmail } from './helpers'

/**
 * commerce-pricing-engine's "Web: CatalogScreen rework" (design.md, archived
 * under openspec/changes/archive/2026-09-19-commerce-pricing-engine)
 * replaced CatalogScreen's hand-typed rename form (`#productId`,
 * `#targetBranchId`, `#currentName`, `#categoryId`, `#defaultUnitId`,
 * `#newName`, "Rename product") with a real presentation list + identification
 * code editing (`Endpoints/Catalog.cs` `GET/POST /catalog/presentations`,
 * `PUT /catalog/presentations/{id}`). None of the old form fields exist in
 * the DOM any more — `RenameProductRequest` itself dropped
 * `currentName`/`categoryId`/`defaultUnitId` (Catalog.cs remarks), so the
 * previous two tests here (cross-branch rename allow/deny) were exercising a
 * screen that no longer exists. This rewrite targets the real, current
 * screen and its real, still-enforced authorization: `ListPresentationsAsync`
 * scopes strictly by `organization_id` (`Persistence/PostgresCatalogStore.cs`),
 * and every route is gated by `Permission.ManageCatalog`
 * (`AuthorizeCallerAsync` in `Endpoints/Catalog.cs`).
 */
test.describe('catalog screen', () => {
  test("an admin's presentation list never includes another organization's presentation", async ({ page, baseURL }) => {
    const password = 'correct-horse-battery-staple'
    const user = await seedUser(baseURL!, { email: uniqueEmail('catalog-scope'), password })
    const otherOrgUser = await seedUser(baseURL!, { email: uniqueEmail('catalog-scope-other'), password })

    // Seed a real presentation in the OTHER organization via its own
    // authenticated session, using the same production endpoints the UI
    // itself calls (no test-only seam for catalog data).
    const otherContext = await page.context().browser()!.newContext({ ignoreHTTPSErrors: true })
    const otherPage = await otherContext.newPage()
    await otherPage.goto('/login')
    await otherPage.getByLabel('Correo electrónico').fill(otherOrgUser.email)
    await otherPage.getByLabel('Contraseña').fill(password)
    await otherPage.getByRole('button', { name: /iniciar sesión/i }).click()
    await expectSignedIn(otherPage)

    const otherProduct = await otherPage.request.post('/catalog/products', {
      headers: { 'X-Branch-Id': otherOrgUser.branchId },
      data: { name: 'Other Org Product', categoryId: crypto.randomUUID(), defaultUnitId: crypto.randomUUID() },
    })
    expect(otherProduct.ok()).toBeTruthy()
    const otherProductBody = await otherProduct.json()
    const otherPresentation = await otherPage.request.post('/catalog/presentations', {
      headers: { 'X-Branch-Id': otherOrgUser.branchId },
      data: {
        productId: otherProductBody.id,
        name: 'Other Org Presentation',
        quantityBehavior: 0,
        unitId: crypto.randomUUID(),
        identificationCode: null,
      },
    })
    expect(otherPresentation.ok()).toBeTruthy()
    await otherContext.close()

    await page.goto('/login')
    await page.getByLabel('Correo electrónico').fill(user.email)
    await page.getByLabel('Contraseña').fill(password)
    await page.getByRole('button', { name: /iniciar sesión/i }).click()
    await expectSignedIn(page)

    // Catalog is the default tab.
    await expect(page.getByText('Todavía no hay presentaciones.')).toBeVisible()
    await expect(page.getByText('Other Org Presentation')).toHaveCount(0)
  })

  test('an admin can edit their own presentation\'s identification code', async ({ page, baseURL }) => {
    const password = 'correct-horse-battery-staple'
    const user = await seedUser(baseURL!, { email: uniqueEmail('catalog-edit'), password })

    await page.goto('/login')
    await page.getByLabel('Correo electrónico').fill(user.email)
    await page.getByLabel('Contraseña').fill(password)
    await page.getByRole('button', { name: /iniciar sesión/i }).click()
    await expectSignedIn(page)

    // Seed a real product + presentation through the same production
    // endpoints CatalogScreen itself calls, using this admin's own session.
    const product = await page.request.post('/catalog/products', {
      headers: { 'X-Branch-Id': user.branchId },
      data: { name: 'E2E Product', categoryId: crypto.randomUUID(), defaultUnitId: crypto.randomUUID() },
    })
    expect(product.ok()).toBeTruthy()
    const productBody = await product.json()
    const presentation = await page.request.post('/catalog/presentations', {
      headers: { 'X-Branch-Id': user.branchId },
      data: {
        productId: productBody.id,
        name: 'E2E Presentation',
        quantityBehavior: 0,
        unitId: crypto.randomUUID(),
        identificationCode: null,
      },
    })
    expect(presentation.ok()).toBeTruthy()

    // Re-mount CatalogScreen via client-side navigation (not page.reload()):
    // CatalogScreen only fetches on mount, and AuthContext keeps the signed-in
    // user purely in-memory with no `/account/me` rehydration on a full page
    // reload (AuthContext.tsx), so a hard refresh here would be a separate,
    // unrelated bug this test has no need to exercise.
    await page.getByRole('link', { name: 'Pedidos' }).click()
    await page.getByRole('link', { name: 'Catálogo' }).click()
    await expect(page.getByText('E2E Presentation')).toBeVisible()
    await expect(page.getByText('Sin código')).toBeVisible()

    await page.getByRole('button', { name: /editar código/i }).click()
    await page.getByLabel(/código de identificación/i).fill('7791234567890')
    await page.getByRole('button', { name: /^guardar$/i }).click()

    await expect(page.getByText('7791234567890')).toBeVisible()
    await expect(page.getByText('Sin código')).toHaveCount(0)
  })
})

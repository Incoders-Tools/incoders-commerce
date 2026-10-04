import { expect, test, type Page } from '@playwright/test'
import { expectSignedIn, seedUser, uniqueEmail } from './helpers'

/**
 * staff-order-taking T4: a staff member takes an order for a customer from the "Take order" screen, at a phone
 * viewport (a seller on the road) and at desktop width (an administrator taking a phone order). The seeded
 * business admin holds TakeOrders. Products, a "Reparto" price list with published prices and a customer on that
 * list are seeded through the same cookie-authorized production endpoints the admin screens call; the order goes
 * through the screen only, and is then read back from the branch's pending orders.
 */

const password = 'correct-horse-battery-staple'

interface Seeded {
  customerName: string
  fixedProduct: string
  weightedProduct: string
}

/** Yesterday as `yyyy-mm-dd`, so the published price is already effective whatever the server's time zone. */
function yesterday(): string {
  return new Date(Date.now() - 24 * 60 * 60 * 1000).toISOString().slice(0, 10)
}

async function seedOrderData(page: Page, branchId: string): Promise<Seeded> {
  const headers = { 'X-Branch-Id': branchId }
  const suffix = crypto.randomUUID().slice(0, 8)
  const fixedProduct = `Chorizo E2E ${suffix}`
  const weightedProduct = `Vacio E2E ${suffix}`

  const presentationIds: string[] = []
  for (const [name, quantityBehavior] of [
    [fixedProduct, 0],
    [weightedProduct, 1],
  ] as const) {
    const product = await page.request.post('/catalog/products', {
      headers,
      data: { name, defaultUnitId: crypto.randomUUID() },
    })
    expect(product.ok()).toBeTruthy()
    const { id: productId } = (await product.json()) as { id: string }
    const presentation = await page.request.post('/catalog/presentations', {
      headers,
      data: {
        productId,
        name: quantityBehavior === 0 ? 'Paquete' : 'Kilo',
        quantityBehavior,
        unitId: crypto.randomUUID(),
        identificationCode: null,
      },
    })
    expect(presentation.ok()).toBeTruthy()
    presentationIds.push(((await presentation.json()) as { id: string }).id)
  }

  const priceList = await page.request.post('/pricing/price-lists', {
    headers,
    data: { name: `Reparto ${suffix}`, isDefault: false },
  })
  expect(priceList.ok()).toBeTruthy()
  const { id: priceListId } = (await priceList.json()) as { id: string }
  for (const [presentationId, unitPrice] of [
    [presentationIds[0], 1000],
    [presentationIds[1], 2500],
  ] as const) {
    const entry = await page.request.post(`/pricing/price-lists/${priceListId}/entries`, {
      headers,
      data: { presentationId, unitPrice, effectiveFrom: yesterday() },
    })
    expect(entry.ok()).toBeTruthy()
  }

  const customerName = `Almacen E2E ${suffix}`
  const customer = await page.request.post('/customers', {
    headers,
    data: {
      customerKind: 'Retail',
      partyType: 'Person',
      displayName: customerName,
      taxIdType: 'None',
      taxId: null,
      taxCondition: 'ConsumidorFinal',
      phone: null,
      email: null,
      addressStreet: null,
      addressNumber: null,
      neighborhood: null,
      postalCode: null,
      deliveryNotes: null,
      discountPercentage: null,
      paymentTerms: null,
      notes: null,
      priceListId,
    },
  })
  expect(customer.ok()).toBeTruthy()

  return { customerName, fixedProduct, weightedProduct }
}

const viewports = [
  { name: 'phone', size: { width: 390, height: 844 }, collapsedNav: true },
  { name: 'desktop', size: { width: 1440, height: 900 }, collapsedNav: false },
]

for (const viewport of viewports) {
  test.describe(`staff order taking (${viewport.name})`, () => {
    test.use({ viewport: viewport.size })

    test('picks a customer, adds two products, sees the customer prices and total, and submits', async ({ page, baseURL }) => {
      const admin = await seedUser(baseURL!, { email: uniqueEmail(`staff-order-${viewport.name}`), password })

      await page.goto('/login')
      await page.getByLabel('Correo electrónico').fill(admin.email)
      await page.getByLabel('Contraseña').fill(password)
      await page.getByRole('button', { name: /iniciar sesión/i }).click()
      await expectSignedIn(page)

      const seeded = await seedOrderData(page, admin.branchId)

      // In-SPA navigation (no reload): AuthProvider keeps the session in memory only.
      if (viewport.collapsedNav) await page.getByRole('button', { name: 'Alternar navegación' }).click()
      await page.getByRole('link', { name: 'Pedidos' }).click()
      await expect(page.getByRole('heading', { name: 'Tomar pedido' })).toBeVisible()

      await page.getByLabel('Buscar cliente').fill(seeded.customerName)
      await page.getByRole('button', { name: new RegExp(seeded.customerName) }).click()
      await expect(page.getByRole('button', { name: 'Cambiar' })).toBeVisible()

      await page.getByLabel('Buscar producto').fill(seeded.fixedProduct)
      await page.getByRole('button', { name: `Agregar ${seeded.fixedProduct} Paquete` }).click()
      await page.getByLabel('Buscar producto').fill(seeded.weightedProduct)
      await page.getByRole('button', { name: `Agregar ${seeded.weightedProduct} Kilo` }).click()
      await page.getByLabel(`Cantidad de ${seeded.weightedProduct} Kilo`).fill('1,5')

      const fixedLine = page.getByRole('listitem', { name: `${seeded.fixedProduct} Paquete` })
      const weightedLine = page.getByRole('listitem', { name: `${seeded.weightedProduct} Kilo` })
      await expect(fixedLine).toContainText(/1\.000,00/)
      await expect(fixedLine).toContainText(/Lista Reparto/)
      await expect(weightedLine).toContainText(/3\.750,00/)
      await expect(page.getByTestId('order-total')).toHaveText(/4\.750,00/)

      const submit = page.getByRole('button', { name: 'Confirmar pedido' })
      await expect(submit).toBeEnabled()
      await expect(submit).toBeInViewport()
      await submit.click()

      const confirmation = page.getByRole('heading', { name: /^Pedido P\d{2,3}-W-\d+ registrado$/ })
      await expect(confirmation).toBeVisible()
      const orderNumber = /P\d{2,3}-W-\d+/.exec((await confirmation.textContent()) ?? '')![0]

      const pending = await page.request.get('/orders/pending', { headers: { 'X-Branch-Id': admin.branchId } })
      expect(pending.ok()).toBeTruthy()
      const orders = (await pending.json()) as { orderNumber?: string | null }[]
      expect(orders.map((order) => order.orderNumber)).toContain(orderNumber)

      await page.getByRole('button', { name: 'Nuevo pedido' }).click()
      await expect(page.getByLabel('Buscar cliente')).toHaveValue('')
    })
  })
}

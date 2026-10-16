import type { MasterDataEntry, SupplierRecord } from '@/api/types'

/** Shared by the supplier screen tests. */
export const supplierFixture = (overrides: Partial<SupplierRecord> = {}): SupplierRecord => ({
  id: '11111111-1111-1111-1111-111111111111',
  displayName: 'Frigorífico Norte',
  legalName: 'Frigorífico Norte SA',
  taxIdType: 'Cuit',
  taxId: '30-12345678-9',
  taxCondition: 'ResponsableInscripto',
  phone: '11-4444-4444',
  email: 'ventas@norte.example',
  addressStreet: null,
  addressNumber: null,
  neighborhood: null,
  postalCode: null,
  cityId: 'city-rosario',
  cityName: 'Rosario',
  provinceId: '82',
  provinceName: 'Santa Fe',
  categoryId: 'cat-carne',
  categoryName: 'Carne',
  paymentTermsDays: 30,
  bankCbu: null,
  bankAlias: null,
  notes: null,
  isEnabled: true,
  createdAtUtc: '2024-01-01T00:00:00Z',
  updatedAtUtc: '2024-01-01T00:00:00Z',
  balance: 60000,
  contacts: [
    { id: 'c0', firstName: 'Ana', lastName: 'Gómez', phone: null, email: null, role: null, isPrimary: false, sortOrder: 0 },
    { id: 'c1', firstName: 'Juana', lastName: 'Pérez', phone: null, email: null, role: 'Ventas', isPrimary: true, sortOrder: 1 },
  ],
  ...overrides,
})

export const categoryEntry = (id: string, name: string, isActive = true): MasterDataEntry => ({
  id,
  organizationId: 'org-1',
  name,
  key: name.toLowerCase(),
  sortOrder: 1,
  isActive,
  createdAtUtc: '2024-01-01T00:00:00Z',
  updatedAtUtc: '2024-01-01T00:00:00Z',
})

export const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

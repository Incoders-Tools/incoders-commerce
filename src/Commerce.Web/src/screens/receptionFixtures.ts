import type { ReceptionRecord, ReceptionSummary } from '@/api/types'

/** Shared by the reception screen tests. */
export const SUPPLIER_ID = '11111111-1111-1111-1111-111111111111'

export const receptionSummary = (overrides: Partial<ReceptionSummary> = {}): ReceptionSummary => ({
  id: 'rec-1',
  supplierId: SUPPLIER_ID,
  supplierName: 'Frigorífico Norte',
  status: 'Confirmed',
  number: 'R01-W-1',
  documentType: 'Invoice',
  documentReference: 'A-0001-00001234',
  occurredOn: '2026-10-01',
  dueOn: '2026-10-31',
  totalAmount: 480000,
  lineCount: 1,
  createdAtUtc: '2026-10-01T12:00:00Z',
  updatedAtUtc: '2026-10-01T12:00:00Z',
  ...overrides,
})

export const receptionRecord = (overrides: Partial<ReceptionRecord> = {}): ReceptionRecord => ({
  ...receptionSummary(),
  notes: null,
  ledgerInvoiceMovementId: 'mov-1',
  ledgerReversalMovementId: null,
  voidReason: null,
  confirmedAtUtc: '2026-10-01T12:05:00Z',
  voidedAtUtc: null,
  lines: [
    {
      id: 'line-1',
      presentationId: 'pr-1',
      productName: 'Media res',
      presentationName: 'Kilo',
      quantityBehavior: 'Weighted',
      quantity: 120,
      unitCost: 4000,
      lineTotal: 480000,
      lotCode: 'L-77',
      expiresOn: '2026-10-20',
      sortOrder: 0,
    },
  ],
  ...overrides,
})

export const catalogProduct = (id: string, name: string) => ({
  id,
  organizationId: 'org-1',
  branchId: 'b-1',
  name,
  categoryId: 'cat-1',
  defaultUnitId: 'unit-1',
  createdAtUtc: '2026-01-01T00:00:00Z',
  createdByUserId: 'u-1',
  updatedAtUtc: '2026-01-01T00:00:00Z',
})

export const catalogPresentation = (id: string, productId: string, name: string, quantityBehavior: number) => ({
  id,
  organizationId: 'org-1',
  branchId: 'b-1',
  productId,
  name,
  quantityBehavior,
  unitId: 'unit-1',
  identificationCode: null,
  createdAtUtc: '2026-01-01T00:00:00Z',
  createdByUserId: 'u-1',
  updatedAtUtc: '2026-01-01T00:00:00Z',
})

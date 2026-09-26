import { apiFetch, apiFetchForm } from './client'
import type {
  AppendPriceEntryRequest,
  CreatePriceListRequest,
  CreateSupplierMappingRequest,
  ImportBatchDetail,
  ImportBatchRecord,
  PriceListEntryRecord,
  PriceListRecord,
  SupplierPriceMappingRecord,
} from './types'

export function listPriceLists(): Promise<PriceListRecord[]> {
  return apiFetch<PriceListRecord[]>('/pricing/price-lists')
}

export function getPriceList(priceListId: string): Promise<PriceListRecord> {
  return apiFetch<PriceListRecord>(`/pricing/price-lists/${priceListId}`)
}

export function createPriceList(request: CreatePriceListRequest): Promise<PriceListRecord> {
  return apiFetch<PriceListRecord>('/pricing/price-lists', {
    method: 'POST',
    body: JSON.stringify(request),
  })
}

export function listHistory(priceListId: string, presentationId: string): Promise<PriceListEntryRecord[]> {
  return apiFetch<PriceListEntryRecord[]>(
    `/pricing/price-lists/${priceListId}/presentations/${presentationId}/history`,
  )
}

/**
 * price-list-management spec "Price History Filterable By Date": review
 * the branch's prices as of a single date, or across a range showing every
 * change inside it. Pass either `asOf` alone, or `from`+`to` together
 * (never both shapes at once — the server rejects that combination); pass
 * neither for "now" (`GET /pricing/price-lists/{id}/prices`'s own default).
 * Read-only.
 */
export function listPrices(
  priceListId: string,
  filter: { asOf?: string } | { from: string; to: string } | Record<string, never> = {},
): Promise<PriceListEntryRecord[]> {
  const params = new URLSearchParams()
  if ('asOf' in filter && filter.asOf) {
    params.set('asOf', filter.asOf)
  }
  if ('from' in filter && filter.from) {
    params.set('from', filter.from)
  }
  if ('to' in filter && filter.to) {
    params.set('to', filter.to)
  }
  const query = params.toString()
  return apiFetch<PriceListEntryRecord[]>(
    `/pricing/price-lists/${priceListId}/prices${query ? `?${query}` : ''}`,
  )
}

export function appendEntry(
  priceListId: string,
  request: AppendPriceEntryRequest,
): Promise<PriceListEntryRecord> {
  return apiFetch<PriceListEntryRecord>(`/pricing/price-lists/${priceListId}/entries`, {
    method: 'POST',
    body: JSON.stringify(request),
  })
}

// commerce-pricing-engine Work Unit 9: supplier mappings + the Excel import
// lifecycle (design.md "Import lifecycle": upload -> review -> commit/reject).

export function listSupplierMappings(): Promise<SupplierPriceMappingRecord[]> {
  return apiFetch<SupplierPriceMappingRecord[]>('/pricing/supplier-mappings')
}

export function createSupplierMapping(request: CreateSupplierMappingRequest): Promise<SupplierPriceMappingRecord> {
  return apiFetch<SupplierPriceMappingRecord>('/pricing/supplier-mappings', {
    method: 'POST',
    body: JSON.stringify(request),
  })
}

/** Multipart upload — the untrusted file never rides through a JSON body. */
export function uploadImport(supplierMappingId: string, file: File): Promise<ImportBatchRecord> {
  const form = new FormData()
  form.append('file', file)
  form.append('supplierMappingId', supplierMappingId)
  return apiFetchForm<ImportBatchRecord>('/pricing/imports', form)
}

export function getImportBatch(batchId: string): Promise<ImportBatchDetail> {
  return apiFetch<ImportBatchDetail>(`/pricing/imports/${batchId}`)
}

export function commitImport(batchId: string): Promise<ImportBatchRecord> {
  return apiFetch<ImportBatchRecord>(`/pricing/imports/${batchId}/commit`, { method: 'POST' })
}

export function rejectImport(batchId: string): Promise<ImportBatchRecord> {
  return apiFetch<ImportBatchRecord>(`/pricing/imports/${batchId}/reject`, { method: 'POST' })
}

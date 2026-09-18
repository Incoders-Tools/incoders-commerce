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

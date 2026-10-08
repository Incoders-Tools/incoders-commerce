import { apiFetch } from './client'
import type {
  CreateSupplierRequest,
  CreateSupplierResponse,
  SupplierBalance,
  SupplierListFilters,
  SupplierRecord,
  UpdateSupplierRequest,
} from './types'

/** Lists suppliers; search, category, city and enabled are applied by the server. */
export function listSuppliers(filters: SupplierListFilters = {}): Promise<SupplierRecord[]> {
  const query = new URLSearchParams()
  if (filters.search) query.set('search', filters.search)
  if (filters.categoryId) query.set('categoryId', filters.categoryId)
  if (filters.cityId) query.set('cityId', filters.cityId)
  if (filters.enabled !== undefined) query.set('enabled', String(filters.enabled))
  const queryString = query.toString()
  return apiFetch<SupplierRecord[]>(queryString ? `/suppliers?${queryString}` : '/suppliers')
}

export function getSupplier(id: string): Promise<SupplierRecord> {
  return apiFetch<SupplierRecord>(`/suppliers/${id}`)
}

export function createSupplier(request: CreateSupplierRequest): Promise<CreateSupplierResponse> {
  return apiFetch<CreateSupplierResponse>('/suppliers', { method: 'POST', body: JSON.stringify(request) })
}

export function updateSupplier(id: string, request: UpdateSupplierRequest): Promise<SupplierRecord> {
  return apiFetch<SupplierRecord>(`/suppliers/${id}`, { method: 'PUT', body: JSON.stringify(request) })
}

/** Balance and overdue amount of every supplier, for the list. */
export function listSupplierBalances(): Promise<SupplierBalance[]> {
  return apiFetch<SupplierBalance[]>('/suppliers/account/balances')
}

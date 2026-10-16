import { apiFetch } from './client'
import type {
  CreateCustomerRequest,
  CreateCustomerResponse,
  CustomerListFilters,
  CustomerRecord,
  IssueOrderingAccessResponse,
  UpdateCustomerRequest,
} from './types'

/** Lists customers; search, city and business type are applied by the server. */
export function listCustomers(filters: CustomerListFilters = {}): Promise<CustomerRecord[]> {
  const query = new URLSearchParams()
  if (filters.search) query.set('search', filters.search)
  if (filters.cityId) query.set('cityId', filters.cityId)
  if (filters.businessTypeId) query.set('businessTypeId', filters.businessTypeId)
  const queryString = query.toString()
  return apiFetch<CustomerRecord[]>(queryString ? `/customers?${queryString}` : '/customers')
}

export function getCustomer(id: string): Promise<CustomerRecord> {
  return apiFetch<CustomerRecord>(`/customers/${id}`)
}

export function createCustomer(request: CreateCustomerRequest): Promise<CreateCustomerResponse> {
  return apiFetch<CreateCustomerResponse>('/customers', {
    method: 'POST',
    body: JSON.stringify(request),
  })
}

export function updateCustomer(id: string, request: UpdateCustomerRequest): Promise<CustomerRecord> {
  return apiFetch<CustomerRecord>(`/customers/${id}`, {
    method: 'PUT',
    body: JSON.stringify(request),
  })
}

export function issueOrderingAccess(id: string): Promise<IssueOrderingAccessResponse> {
  return apiFetch<IssueOrderingAccessResponse>(`/customers/${id}/ordering-access`, {
    method: 'POST',
  })
}

export function revokeOrderingAccess(id: string, credential: string): Promise<void> {
  return apiFetch<void>(`/customers/${id}/ordering-access`, {
    method: 'DELETE',
    body: JSON.stringify({ credential }),
  })
}

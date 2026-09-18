import { apiFetch } from './client'
import type {
  CreateCustomerRequest,
  CreateCustomerResponse,
  CustomerRecord,
  IssueOrderingAccessResponse,
  UpdateCustomerRequest,
} from './types'

export function listCustomers(): Promise<CustomerRecord[]> {
  return apiFetch<CustomerRecord[]>('/customers')
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

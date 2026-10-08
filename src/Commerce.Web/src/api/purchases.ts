import { apiFetch } from './client'
import type { ReceptionListFilters, ReceptionRecord, ReceptionRequest, ReceptionSummary } from './types'

export function listReceptions(filters: ReceptionListFilters = {}): Promise<ReceptionSummary[]> {
  const query = new URLSearchParams()
  if (filters.status) query.set('status', filters.status)
  if (filters.supplierId) query.set('supplierId', filters.supplierId)
  if (filters.from) query.set('from', filters.from)
  if (filters.to) query.set('to', filters.to)
  if (filters.search) query.set('search', filters.search)
  const queryString = query.toString()
  return apiFetch<ReceptionSummary[]>(queryString ? `/purchases/receptions?${queryString}` : '/purchases/receptions')
}

export function getReception(id: string): Promise<ReceptionRecord> {
  return apiFetch<ReceptionRecord>(`/purchases/receptions/${id}`)
}

export function createReception(request: ReceptionRequest): Promise<ReceptionRecord> {
  return apiFetch<ReceptionRecord>('/purchases/receptions', { method: 'POST', body: JSON.stringify(request) })
}

export function updateReception(id: string, request: ReceptionRequest): Promise<ReceptionRecord> {
  return apiFetch<ReceptionRecord>(`/purchases/receptions/${id}`, { method: 'PUT', body: JSON.stringify(request) })
}

export function confirmReception(id: string): Promise<ReceptionRecord> {
  return apiFetch<ReceptionRecord>(`/purchases/receptions/${id}/confirm`, { method: 'POST' })
}

export function voidReception(id: string, reason: string): Promise<ReceptionRecord> {
  return apiFetch<ReceptionRecord>(`/purchases/receptions/${id}/void`, {
    method: 'POST',
    body: JSON.stringify({ reason }),
  })
}

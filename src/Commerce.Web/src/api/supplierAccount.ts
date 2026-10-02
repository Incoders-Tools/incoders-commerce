import { apiFetch } from './client'
import type {
  AccountMovement,
  AccountStatement,
  AccountSummary,
  RegisterMovementRequest,
  ReverseMovementRequest,
} from './types'

export function registerMovement(supplierId: string, request: RegisterMovementRequest): Promise<AccountMovement> {
  return apiFetch<AccountMovement>(`/suppliers/${supplierId}/account/movements`, {
    method: 'POST',
    body: JSON.stringify(request),
  })
}

export function reverseMovement(
  supplierId: string,
  movementId: string,
  request: ReverseMovementRequest = {},
): Promise<AccountMovement> {
  return apiFetch<AccountMovement>(`/suppliers/${supplierId}/account/movements/${movementId}/reverse`, {
    method: 'POST',
    body: JSON.stringify(request),
  })
}

export function getStatement(supplierId: string, range: { from?: string; to?: string } = {}): Promise<AccountStatement> {
  const query = new URLSearchParams()
  if (range.from) query.set('from', range.from)
  if (range.to) query.set('to', range.to)
  const queryString = query.toString()
  return apiFetch<AccountStatement>(
    `/suppliers/${supplierId}/account/statement${queryString ? `?${queryString}` : ''}`,
  )
}

export function getSummary(supplierId: string, asOf?: string): Promise<AccountSummary> {
  return apiFetch<AccountSummary>(`/suppliers/${supplierId}/account/summary${asOf ? `?asOf=${asOf}` : ''}`)
}

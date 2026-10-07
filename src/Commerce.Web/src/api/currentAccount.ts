import { apiFetch } from './client'
import type {
  AccountMovement,
  AccountStatement,
  AccountSummary,
  MovementDirection,
  RegisterMovementRequest,
  ReverseMovementRequest,
} from './types'

/**
 * Whose current account. Both ledgers have the same routes (`/suppliers/{id}/account/...`,
 * `/customers/{id}/account/...`) and shapes; they differ in what a positive balance means: what the business owes a
 * supplier, what a customer owes the business.
 */
export type AccountPartyKind = 'supplier' | 'customer' | 'employee'

/** The direction that increases what is owed: an invoice is a Credit on a supplier's account, a sale a Debit on a customer's. */
// An employee's account reads like a supplier's: what the business owes it (its salary is a Credit).
export const DEBT_DIRECTION: Record<AccountPartyKind, MovementDirection> = { supplier: 'Credit', customer: 'Debit', employee: 'Credit' }

const PATHS: Record<AccountPartyKind, string> = { supplier: 'suppliers', customer: 'customers', employee: 'employees' }

const base = (party: AccountPartyKind, id: string) => `/${PATHS[party]}/${id}/account`

export function registerMovement(party: AccountPartyKind, id: string, request: RegisterMovementRequest): Promise<AccountMovement> {
  return apiFetch<AccountMovement>(`${base(party, id)}/movements`, { method: 'POST', body: JSON.stringify(request) })
}

export function reverseMovement(
  party: AccountPartyKind,
  id: string,
  movementId: string,
  request: ReverseMovementRequest = {},
): Promise<AccountMovement> {
  return apiFetch<AccountMovement>(`${base(party, id)}/movements/${movementId}/reverse`, {
    method: 'POST',
    body: JSON.stringify(request),
  })
}

export function getStatement(
  party: AccountPartyKind,
  id: string,
  range: { from?: string; to?: string } = {},
): Promise<AccountStatement> {
  const query = new URLSearchParams()
  if (range.from) query.set('from', range.from)
  if (range.to) query.set('to', range.to)
  const queryString = query.toString()
  return apiFetch<AccountStatement>(`${base(party, id)}/statement${queryString ? `?${queryString}` : ''}`)
}

export function getSummary(party: AccountPartyKind, id: string, asOf?: string): Promise<AccountSummary> {
  return apiFetch<AccountSummary>(`${base(party, id)}/summary${asOf ? `?asOf=${asOf}` : ''}`)
}

export interface CustomerBalance {
  customerId: string
  /** What the customer owes (negative: credit in the customer's favour). */
  balance: number
  overdue: number
}

export function listCustomerBalances(): Promise<CustomerBalance[]> {
  return apiFetch<CustomerBalance[]>('/customers/account/balances')
}

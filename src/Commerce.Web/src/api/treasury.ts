import { apiFetch } from './client'

// Endpoints/Treasury.cs + Persistence/PostgresTreasuryStore.cs, mirrored. The administration manages the accounts (of the
// organization's own account types, `./treasuryAccountTypes`), records money in or out by hand, transfers, and voids or
// edits movements: a voided one is kept but counts nowhere, an edit is a void plus its replacement. Sales and customer
// payments are voided at the POS; here they only change account.

export type TreasuryAccountKind = 'Cash' | 'Card' | 'Qr' | 'Safe' | 'Bank' | 'Other'
export type ManualAccountKind = 'Safe' | 'Bank' | 'Other'
export type TreasuryMovementKind =
  | 'Sale'
  | 'CustomerPayment'
  | 'DeliveryPayment'
  | 'Reversal'
  | 'CashCountDifference'
  | 'CashWithdrawal'
  | 'CashDeposit'
  | 'Transfer'
  | 'ManualIn'
  | 'ManualOut'

export interface TreasuryAccount {
  accountId: string
  /** Null: a company-wide account (a bank, say). */
  branchId: string | null
  branchName: string | null
  kind: TreasuryAccountKind
  name: string
  balance: number
  todayIn: number
  todayOut: number
  description?: string | null
  accountTypeId?: string | null
  accountTypeName?: string | null
  isActive?: boolean
  /** The drawer, card or QR account of a branch: the POS posts to it, it is never deactivated. */
  isAutomatic?: boolean
}

export interface TreasuryMovement {
  movementId: string
  accountId: string
  kind: TreasuryMovementKind
  direction: 'In' | 'Out'
  amount: number
  occurredAtUtc: string
  businessDate: string
  concept: string
  documentReference: string | null
  customerId: string | null
  customerName: string | null
  sourceType: string | null
  reversesMovementId: string | null
  reversed: boolean
  transferId?: string | null
  /** Kept, but counts in no balance. */
  voided?: boolean
  voidReason?: string | null
  voidedAtUtc?: string | null
  /** Set on the replacement of an edited movement. */
  correctsMovementId?: string | null
  canVoid?: boolean
  canEdit?: boolean
  /** A sale or customer payment: only its account can change. */
  canReclassify?: boolean
  /** Recorded by a recurring movement (a fixed expense, say). */
  recurrenceId?: string | null
}

export function listTreasuryAccounts(branchId?: string): Promise<TreasuryAccount[]> {
  return apiFetch<TreasuryAccount[]>(branchId ? `/treasury/accounts?branchId=${branchId}` : '/treasury/accounts')
}

export function listTreasuryMovements(accountId: string, range: { from?: string; to?: string } = {}): Promise<TreasuryMovement[]> {
  const query = new URLSearchParams()
  if (range.from) query.set('from', range.from)
  if (range.to) query.set('to', range.to)
  const queryString = query.toString()
  const path = `/treasury/accounts/${accountId}/movements`
  return apiFetch<TreasuryMovement[]>(queryString ? `${path}?${queryString}` : path)
}

export interface CreateTreasuryAccountRequest {
  /** Omitted: an ordinary account. Safe: the branch safe the POS withdraws "to the safe" into (needs its branch). */
  kind?: ManualAccountKind
  name: string
  /** Omitted: the default type of the kind. */
  accountTypeId?: string
  /** Required for a safe; omitted = a company-wide account. */
  branchId?: string
  description?: string
}

export interface UpdateTreasuryAccountRequest {
  name: string
  accountTypeId: string | null
  description: string | null
}

export function updateTreasuryAccount(accountId: string, request: UpdateTreasuryAccountRequest): Promise<TreasuryAccount> {
  return apiFetch<TreasuryAccount>(`/treasury/accounts/${accountId}`, { method: 'PUT', body: JSON.stringify(request) })
}

export function setTreasuryAccountActive(accountId: string, isActive: boolean): Promise<void> {
  return apiFetch<void>(`/treasury/accounts/${accountId}/active`, { method: 'POST', body: JSON.stringify({ isActive }) })
}

export function createTreasuryAccount(request: CreateTreasuryAccountRequest): Promise<TreasuryAccount> {
  return apiFetch<TreasuryAccount>('/treasury/accounts', { method: 'POST', body: JSON.stringify(request) })
}

export interface RecordTreasuryMovementRequest {
  accountId: string
  direction: 'In' | 'Out'
  amount: number
  date?: string
  concept: string
  reference?: string
}

export function recordTreasuryMovement(request: RecordTreasuryMovementRequest): Promise<{ movementId: string }> {
  return apiFetch<{ movementId: string }>('/treasury/movements', { method: 'POST', body: JSON.stringify(request) })
}

export interface TreasuryTransferRequest {
  fromAccountId: string
  toAccountId: string
  amount: number
  date?: string
  concept: string
  reference?: string
}

export function transferBetweenAccounts(request: TreasuryTransferRequest): Promise<{ transferId: string }> {
  return apiFetch<{ transferId: string }>('/treasury/transfers', { method: 'POST', body: JSON.stringify(request) })
}

// ---- recurring movements -----------------------------------------------------------------------

export type RecurrenceFrequency = 'Weekly' | 'Monthly' | 'Yearly'
export type RecurrenceEndMode = 'Never' | 'OnDate' | 'AfterCount'

export interface TreasuryRecurrence {
  recurrenceId: string
  accountId: string
  accountName: string
  direction: 'In' | 'Out'
  amount: number
  concept: string
  documentReference: string | null
  frequency: RecurrenceFrequency
  interval: number
  startDate: string
  endMode: RecurrenceEndMode
  endDate: string | null
  maxOccurrences: number | null
  isActive: boolean
  occurrencesRecorded: number
  lastRecordedOn: string | null
  /** The next date it will be recorded; null when it ended or is paused. */
  nextOn: string | null
  updatedAtUtc: string
}

export interface TreasuryRecurrenceRequest {
  accountId: string
  direction: 'In' | 'Out'
  amount: number
  concept: string
  reference?: string
  frequency: RecurrenceFrequency
  interval: number
  startDate: string
  endMode: RecurrenceEndMode
  endDate?: string
  maxOccurrences?: number
  /** On create: also record the dates since the start that already passed. */
  includePastDates?: boolean
}

export function listTreasuryRecurrences(): Promise<TreasuryRecurrence[]> {
  return apiFetch<TreasuryRecurrence[]>('/treasury/recurrences')
}

export function createTreasuryRecurrence(request: TreasuryRecurrenceRequest): Promise<{ recurrenceId: string }> {
  return apiFetch<{ recurrenceId: string }>('/treasury/recurrences', { method: 'POST', body: JSON.stringify(request) })
}

export function updateTreasuryRecurrence(id: string, request: TreasuryRecurrenceRequest): Promise<void> {
  return apiFetch<void>(`/treasury/recurrences/${id}`, { method: 'PUT', body: JSON.stringify(request) })
}

export function setTreasuryRecurrenceActive(id: string, isActive: boolean): Promise<void> {
  return apiFetch<void>(`/treasury/recurrences/${id}/active`, { method: 'POST', body: JSON.stringify({ isActive }) })
}

/** Brings into the treasury the operations the terminals pushed before it existed; returns how many movements it added. */
export function reprocessTreasury(): Promise<{ movementsAdded: number }> {
  return apiFetch<{ movementsAdded: number }>('/treasury/reprocess', { method: 'POST' })
}

export function voidTreasuryMovement(movementId: string, reason: string): Promise<void> {
  return apiFetch<void>(`/treasury/movements/${movementId}/void`, { method: 'POST', body: JSON.stringify({ reason }) })
}

/** New values of a movement (omitted: unchanged) and why. A sale or payment only takes `accountId`. */
export interface EditTreasuryMovementRequest {
  accountId?: string
  amount?: number
  date?: string
  concept?: string
  reference?: string
  reason: string
}

export function editTreasuryMovement(movementId: string, request: EditTreasuryMovementRequest): Promise<{ movementId: string }> {
  return apiFetch<{ movementId: string }>(`/treasury/movements/${movementId}`, { method: 'PUT', body: JSON.stringify(request) })
}

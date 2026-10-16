import type { TreasuryAccount, TreasuryMovement } from '@/api/treasury'

/** yyyy-mm-dd of a local date. */
export function isoDay(date: Date): string {
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const day = String(date.getDate()).padStart(2, '0')
  return `${date.getFullYear()}-${month}-${day}`
}

/** "15000", "$ 15000" or "15000,50" -> the amount; null when it is not positive with at most two decimals. */
export function parseAmount(text: string): number | null {
  const normalized = text.trim().replace(/^\$/, '').trim().replace(',', '.')
  if (!/^\d+(\.\d{1,2})?$/.test(normalized)) return null
  const value = Number(normalized)
  return value > 0 ? value : null
}

/** The account's display name: "Caja efectivo · Centro", "Banco Nación (Toda la empresa)". */
export function accountLabel(account: TreasuryAccount, wholeCompany: string): string {
  return account.branchId === null ? `${account.name} (${wholeCompany})` : account.name
}

/** The concept a transfer was given, without the "Transferencia a X: " its legs add. */
export function baseConcept(movement: TreasuryMovement): string {
  if (movement.kind !== 'Transfer') return movement.concept
  const match = /^Transferencia (?:a|desde) .+?: (.*)$/s.exec(movement.concept)
  return match ? match[1] : movement.concept
}

/** Money that counts: everything but voided movements. */
export function countedMovements(movements: readonly TreasuryMovement[]): TreasuryMovement[] {
  return movements.filter((movement) => !movement.voided)
}

export interface Subtotal {
  key: string
  label: string
  total: number
}

/** Account balances added up by a grouping (type, branch), in the accounts' order. */
export function subtotals(accounts: readonly TreasuryAccount[], group: (account: TreasuryAccount) => { key: string; label: string }): Subtotal[] {
  const byKey = new Map<string, Subtotal>()
  for (const account of accounts) {
    const { key, label } = group(account)
    const entry = byKey.get(key) ?? { key, label, total: 0 }
    entry.total = Math.round((entry.total + account.balance) * 100) / 100
    byKey.set(key, entry)
  }
  return [...byKey.values()]
}

export interface BranchOption {
  id: string
  name: string
}

/**
 * The dates of a recurring movement, as the server computes them (`RecurrenceSchedule`): every `interval` weeks, months or
 * years from the start; a day the month does not have becomes its last day. `count` dates from the n-th on.
 */
export function recurrenceDates(
  start: string,
  frequency: 'Weekly' | 'Monthly' | 'Yearly',
  interval: number,
  from: number,
  count: number,
): string[] {
  const [year, month, day] = start.split('-').map(Number)
  const dates: string[] = []
  for (let index = from; index < from + count; index++) {
    if (frequency === 'Weekly') {
      const date = new Date(year, month - 1, day + 7 * interval * index)
      dates.push(isoDay(date))
      continue
    }
    const months = frequency === 'Monthly' ? month - 1 + interval * index : month - 1
    const targetYear = year + (frequency === 'Yearly' ? interval * index : Math.floor(months / 12))
    const targetMonth = frequency === 'Yearly' ? month - 1 : months % 12
    const lastDay = new Date(targetYear, targetMonth + 1, 0).getDate()
    dates.push(isoDay(new Date(targetYear, targetMonth, Math.min(day, lastDay))))
  }
  return dates
}

/**
 * The one way every money field reads what was typed, in es-AR, shared with the POS (`MoneyInput`) and recorded in
 * `docs/architecture/cross-layer-parity.md`. An amount carries at most a few decimals, so a point is never ambiguous:
 * - a `.` followed by exactly three digits groups thousands: `15.000` = 15000, `1.234.567,50` = 1234567.50;
 * - a `,`, or a `.` followed by one or two digits, is the decimal: `15,5` and `15.50` = 15.50;
 * - anything else is refused (`1,234.56`, `15.0000`, a sign).
 * A leading `$` and surrounding spaces are ignored. Quantities keep the organization's own format (`lib/quantity.ts`).
 */
export type MoneyReading = { ok: true; value: number } | { ok: false; reason: 'blank' | 'format' | 'decimals' }

const GROUPED = /^\d{1,3}(\.\d{3})+(,\d+)?$/
const PLAIN = /^\d+(,\d+)?$/
const POINT_DECIMAL = /^\d+\.\d{1,2}$/

export function readMoney(text: string, maxDecimals = 2): MoneyReading {
  const value = text.trim().replace(/^\$/, '').trim()
  if (value === '') return { ok: false, reason: 'blank' }

  let normalized: string
  if (GROUPED.test(value)) normalized = value.replace(/\./g, '').replace(',', '.')
  else if (PLAIN.test(value)) normalized = value.replace(',', '.')
  else if (POINT_DECIMAL.test(value)) normalized = value
  else return { ok: false, reason: 'format' }

  const decimals = normalized.split('.')[1]?.length ?? 0
  if (decimals > maxDecimals) return { ok: false, reason: 'decimals' }
  return { ok: true, value: Number(normalized) }
}

/** `readMoney` for callers that only need the number: blank or unreadable text is `null`, never `0`. */
export function parseMoney(text: string, maxDecimals = 2): number | null {
  const reading = readMoney(text, maxDecimals)
  return reading.ok ? reading.value : null
}

const formatters = new Map<number, Intl.NumberFormat>()

/** The amount as it was read, `$ 15.000,00`: what a field shows under itself before it is saved. */
export function formatMoney(value: number, maxDecimals = 2): string {
  let formatter = formatters.get(maxDecimals)
  if (!formatter) {
    formatter = new Intl.NumberFormat('es-AR', {
      style: 'currency',
      currency: 'ARS',
      minimumFractionDigits: 2,
      maximumFractionDigits: Math.max(2, maxDecimals),
    })
    formatters.set(maxDecimals, formatter)
  }
  return formatter.format(value)
}

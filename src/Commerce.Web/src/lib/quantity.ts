import type { StockQuantityBehavior } from '@/api/types'

/** How an organization writes quantities (kilos): `1,5` (Comma, the default) or `1.5` (Dot). */
export type DecimalSeparator = 'Comma' | 'Dot'

export type QuantityReading =
  | { ok: true; value: number }
  | { ok: false; reason: 'blank' | 'format' | 'separator' }

const commaFormatter = new Intl.NumberFormat('es-AR', { maximumFractionDigits: 3 })
const commaSignedFormatter = new Intl.NumberFormat('es-AR', { maximumFractionDigits: 3, signDisplay: 'exceptZero' })
// No grouping in Dot mode: a "," would not be accepted back by `parseQuantity`.
const dotFormatter = new Intl.NumberFormat('en-US', { maximumFractionDigits: 3, useGrouping: false })
const dotSignedFormatter = new Intl.NumberFormat('en-US', {
  maximumFractionDigits: 3,
  useGrouping: false,
  signDisplay: 'exceptZero',
})

const COMMA_PATTERN = /^-?(\d{1,3}(\.\d{3})+|\d+)(,\d+)?$/
const DOT_PATTERN = /^-?(\d+(\.\d+)?|\.\d+)$/

/**
 * Reads a quantity the way the organization writes it, with no guessing:
 * - Comma: digits, `.` only as thousands (`4.000` = 4000) and `,` as the decimal (`2,5`). `2.5` is rejected.
 * - Dot: digits and one `.` as the decimal (`1.5`). A `,` is rejected (there is no thousands separator).
 * `reason: 'separator'` means the text is a number written with the other convention, so the screen can say so.
 */
export function readQuantity(text: string, separator: DecimalSeparator): QuantityReading {
  const value = text.trim()
  if (value === '') return { ok: false, reason: 'blank' }
  if (separator === 'Dot') {
    if (DOT_PATTERN.test(value)) return { ok: true, value: Number(value) }
    return { ok: false, reason: value.includes(',') && /^[-\d.,]+$/.test(value) ? 'separator' : 'format' }
  }
  if (COMMA_PATTERN.test(value)) return { ok: true, value: Number(value.replace(/\./g, '').replace(',', '.')) }
  return { ok: false, reason: value.includes('.') && /^[-\d.,]+$/.test(value) ? 'separator' : 'format' }
}

/** `readQuantity` for callers that only need the number: blank or unreadable text is `null`, never `0`. */
export function parseQuantity(text: string, separator: DecimalSeparator): number | null {
  const reading = readQuantity(text, separator)
  return reading.ok ? reading.value : null
}

/**
 * Reads a money amount, always in es-AR: a decimal comma (`2,5`), a decimal point (`2.5`) or a thousands point
 * (`4.000`, `1.234,56`). The organization's number format applies to quantities only.
 */
export function parseAmount(text: string): number | null {
  const value = text.trim()
  if (value === '') return null
  let normalized = value
  if (value.includes(',')) normalized = value.replace(/\./g, '').replace(',', '.')
  else if (/^\d{1,3}(\.\d{3})+$/.test(value)) normalized = value.replace(/\./g, '')
  if (!/^-?\d*\.?\d+$/.test(normalized)) return null
  return Number(normalized)
}

/** kg for weighted and bulk presentations, units for fixed quantity ones. */
export function unitLabel(behavior: StockQuantityBehavior): 'kg' | 'u' {
  return behavior === 'FixedQuantity' ? 'u' : 'kg'
}

/** `2,5 kg` / `2.5 kg`, up to three decimals, with the unit. */
export function formatStockQuantity(
  quantity: number,
  behavior: StockQuantityBehavior,
  separator: DecimalSeparator,
): string {
  return `${formatQuantity(quantity, separator)} ${unitLabel(behavior)}`
}

/** The number alone, up to three decimals, in the organization's format. */
export function formatQuantity(quantity: number, separator: DecimalSeparator): string {
  return (separator === 'Dot' ? dotFormatter : commaFormatter).format(quantity)
}

/** `+120`, `-2,5` / `-2.5`; nothing for zero. */
export function formatSignedQuantity(quantity: number, separator: DecimalSeparator): string {
  return (separator === 'Dot' ? dotSignedFormatter : commaSignedFormatter).format(quantity)
}

/** Maps the catalog's numeric behavior ordinal (0, 1, 2) to the purchasing API's names. */
export function behaviorFromCatalog(ordinal: number): StockQuantityBehavior {
  return ordinal === 1 ? 'Weighted' : ordinal === 2 ? 'Bulk' : 'FixedQuantity'
}

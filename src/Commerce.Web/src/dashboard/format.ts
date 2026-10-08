const LOCALE = 'es-AR'

const arsFormatter = new Intl.NumberFormat(LOCALE, {
  style: 'currency',
  currency: 'ARS',
  maximumFractionDigits: 0,
})

const arsCompactFormatter = new Intl.NumberFormat(LOCALE, {
  style: 'currency',
  currency: 'ARS',
  notation: 'compact',
  maximumFractionDigits: 1,
})

const quantityFormatter = new Intl.NumberFormat(LOCALE, { maximumFractionDigits: 1 })

const dayFormatter = new Intl.DateTimeFormat(LOCALE, { day: '2-digit', month: '2-digit', timeZone: 'UTC' })

/** Whole pesos, e.g. `$ 1.284.300`. */
export function formatArs(amount: number): string {
  return arsFormatter.format(amount)
}

/** Short form for chart axes, e.g. `$ 1,3 M`. */
export function formatArsCompact(amount: number): string {
  return arsCompactFormatter.format(amount)
}

export function formatQuantity(quantity: number, unit: 'kg' | 'u'): string {
  return `${quantityFormatter.format(quantity)} ${unit}`
}

/** `2026-10-01` becomes `01/10`; `2026-10-01T14:00` becomes `14:00`. */
export function formatBucket(bucket: string): string {
  const timeIndex = bucket.indexOf('T')
  if (timeIndex >= 0) return bucket.slice(timeIndex + 1)
  return dayFormatter.format(new Date(`${bucket}T00:00:00Z`))
}

const moneyFormatter = new Intl.NumberFormat(LOCALE, {
  style: 'currency',
  currency: 'ARS',
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
})

/** Exact ledger amount with cents, e.g. `$ 100.000,00`. */
export function formatMoney(amount: number): string {
  return moneyFormatter.format(amount)
}

/** `2026-10-01` becomes `01/10/2026`; the string is split, never parsed, so no timezone can shift the day. */
export function formatIsoDate(isoDate: string): string {
  const [year, month, day] = isoDate.slice(0, 10).split('-')
  return `${day}/${month}/${year}`
}

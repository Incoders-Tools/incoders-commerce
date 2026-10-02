import type { StockQuantityBehavior } from '@/api/types'

const quantityFormatter = new Intl.NumberFormat('es-AR', { maximumFractionDigits: 3 })

/**
 * Reads what an operator types in es-AR: a decimal comma (`2,5`), a decimal
 * point (`2.5`) or a thousands point (`4.000`, `1.234,56`). Blank or
 * unreadable text is `null`, never `0`.
 */
export function parseDecimal(text: string): number | null {
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

/** `2,5 kg`, `1.234,568 kg`, `12 u`: es-AR, up to three decimals, with the unit. */
export function formatStockQuantity(quantity: number, behavior: StockQuantityBehavior): string {
  return `${quantityFormatter.format(quantity)} ${unitLabel(behavior)}`
}

/** Maps the catalog's numeric behavior ordinal (0, 1, 2) to the purchasing API's names. */
export function behaviorFromCatalog(ordinal: number): StockQuantityBehavior {
  return ordinal === 1 ? 'Weighted' : ordinal === 2 ? 'Bulk' : 'FixedQuantity'
}

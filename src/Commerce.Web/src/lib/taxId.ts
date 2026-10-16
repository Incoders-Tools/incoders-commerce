import { TaxIdType } from '@/api/types'

/**
 * Mirrors the server's tax id rule (customers and suppliers share it):
 * separators (spaces, dots, hyphens) are ignored, then a DNI needs 7-8 digits
 * and a CUIT/CUIL 11. `None` has no id.
 */
export function isValidTaxId(type: TaxIdType, value: string): boolean {
  const digits = value.replace(/[\s.-]/g, '')
  if (type === TaxIdType.None) return true
  if (type === TaxIdType.Dni) return /^\d{7,8}$/.test(digits)
  return /^\d{11}$/.test(digits)
}

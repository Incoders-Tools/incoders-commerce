/**
 * Reads a percentage typed as text (owner-requests-quick-adjustments Q3): a decimal comma or point, an optional "%",
 * up to three whole digits and two decimals, never a thousands separator. The same reading as the POS
 * (`CustomerFormRules.TryParseDiscount`); the 0-100 range stays the server's `CustomerDiscountRules`. Blank is "none".
 */
export function parsePercentage(text: string): { ok: true; value: number | null } | { ok: false } {
  let value = text.trim()
  if (value.endsWith('%')) value = value.slice(0, -1).trim()
  if (value === '') return text.trim() === '' ? { ok: true, value: null } : { ok: false }
  const normalized = value.replace(',', '.')
  if (!/^\d{1,3}(\.\d{1,2})?$/.test(normalized)) return { ok: false }
  return { ok: true, value: Number(normalized) }
}

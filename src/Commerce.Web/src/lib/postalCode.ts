/**
 * The optional postal code of a city: an Argentine CP of 4 digits ("2000") or a CPA, the province letter (no I or
 * O), 4 digits and 3 letters ("S2000ABC"). Mirrors src/Commerce.Domain/Geography/PostalCodeRules.cs.
 */
const PATTERN = /^([0-9]{4}|[A-HJ-NP-Z][0-9]{4}[A-Z]{3})$/

/** The trimmed, upper-cased code; `''` for a blank value (no postal code); `null` when the format is wrong. */
export function normalizePostalCode(value: string): string | null {
  const candidate = value.trim().toUpperCase()
  if (candidate === '') return ''
  return PATTERN.test(candidate) ? candidate : null
}

/**
 * The one email format rule every email field shares (customer, customer contact, staff user, supplier). Mirrors
 * src/Commerce.Domain/Validation/EmailAddressRules.cs, which the server applies to the same fields: keep the
 * pattern and the limits in step with it. Case-insensitive, checked on the trimmed value.
 */
const PATTERN =
  /^[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+(\.[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+)*@([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z]{2,63}$/i

export const EMAIL_MAX_LENGTH = 254
export const EMAIL_MAX_LOCAL_PART_LENGTH = 64

export function isValidEmail(value: string | null | undefined): boolean {
  const trimmed = value?.trim() ?? ''
  if (trimmed === '') return false
  const at = trimmed.indexOf('@')
  return trimmed.length <= EMAIL_MAX_LENGTH && at > 0 && at <= EMAIL_MAX_LOCAL_PART_LENGTH && PATTERN.test(trimmed)
}

export type EmailStatus = 'empty' | 'valid' | 'invalid'

/** For an optional email field: blank is no email (neither valid nor invalid). */
export function emailStatus(value: string): EmailStatus {
  if (value.trim() === '') return 'empty'
  return isValidEmail(value) ? 'valid' : 'invalid'
}

const LOCAL_CHARS = /^[A-Za-z0-9!#$%&'*+/=?^_`{|}~.-]*$/
const DOMAIN_CHARS = /^[A-Za-z0-9.-]*$/

/**
 * Whether more typing can still turn `value` into a valid address. False for what no suffix can fix (a second `@`,
 * inner spaces, a forbidden character, a misplaced dot or hyphen, a part already too long), so a field can flag it
 * at once instead of waiting for the user to finish.
 */
export function canBecomeValidEmail(value: string): boolean {
  const trimmed = value.trim()
  if (trimmed.length > EMAIL_MAX_LENGTH || /\s/.test(trimmed)) return false
  const parts = trimmed.split('@')
  if (parts.length > 2) return false
  const [local, domain] = parts
  if (!LOCAL_CHARS.test(local) || local.startsWith('.') || local.includes('..')) return false
  if (local.length > EMAIL_MAX_LOCAL_PART_LENGTH) return false
  if (domain === undefined) return true
  if (local === '' || local.endsWith('.')) return false
  return (
    DOMAIN_CHARS.test(domain) &&
    !domain.startsWith('.') &&
    !domain.startsWith('-') &&
    !domain.includes('..') &&
    !domain.includes('-.') &&
    !domain.includes('.-')
  )
}

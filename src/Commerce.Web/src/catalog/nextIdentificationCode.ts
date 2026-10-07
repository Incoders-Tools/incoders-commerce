/** Numeric codes longer than this are barcodes (EAN-8, EAN-13…), never part of the internal sequence. */
const MAX_SEQUENCE_DIGITS = 7

export interface CodeSuggestion {
  /** The highest internal numeric code in use, as stored ("090"). */
  last: string
  /** The one after it, with the same zero padding ("091"). */
  next: string
}

/**
 * The next internal identification code: the highest purely numeric code (barcodes excluded) plus one, keeping its
 * width ("090" -> "091", "999" -> "1000"). Null when there is no numeric code yet. Codes already taken are skipped,
 * so the suggestion is always free in `codes`.
 */
export function suggestNextIdentificationCode(codes: readonly (string | null | undefined)[]): CodeSuggestion | null {
  const sequence = codes
    .map((code) => code?.trim() ?? '')
    .filter((code) => /^\d+$/.test(code) && code.length <= MAX_SEQUENCE_DIGITS)
  if (sequence.length === 0) return null

  let last = sequence[0]
  for (const code of sequence) {
    const value = Number(code)
    if (value > Number(last) || (value === Number(last) && code.length > last.length)) last = code
  }

  const taken = new Set(sequence.map(Number))
  let value = Number(last) + 1
  while (taken.has(value)) value += 1
  return { last, next: String(value).padStart(last.length, '0') }
}

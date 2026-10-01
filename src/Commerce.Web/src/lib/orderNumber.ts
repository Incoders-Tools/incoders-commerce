export interface OrderNumberParts {
  type: string
  branch: string
  origin: string
  sequence: string
}

// Same canonical spelling as the server: a branch code of two to three digits, no zero padding on the sequence.
const ORDER_NUMBER = /^(P)(\d{2,3})-(W)-([1-9]\d*)$/

/**
 * Splits a web order number (`P01-W-37`) into the parts the tooltip explains. Mirrors
 * `Commerce.Domain.Ordering.OrderNumber.TryParse()` — keep the two in step. Returns `null` for anything that
 * is not a canonical number so the caller shows the text as it came, without a made-up explanation.
 */
export function parseOrderNumber(text: string | null | undefined): OrderNumberParts | null {
  const match = text ? ORDER_NUMBER.exec(text) : null
  if (!match) return null
  const [, type, branch, origin, sequence] = match
  return { type, branch, origin, sequence }
}

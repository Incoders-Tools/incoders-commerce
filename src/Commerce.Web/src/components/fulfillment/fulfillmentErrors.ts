import type { TFunction } from 'i18next'
import { ApiError } from '@/api/client'
import { FULFILLMENT_ERRORS } from '@/api/fulfillment'

/** The operator's message for a failed fulfillment call: the known error code translated, else the server's text. */
export function fulfillmentErrorMessage(err: unknown, t: TFunction<'fulfillment'>, fallbackKey = 'errors.unexpectedAction'): string {
  if (err instanceof ApiError) {
    const code = err.code as (typeof FULFILLMENT_ERRORS)[number] | undefined
    if (code && (FULFILLMENT_ERRORS as readonly string[]).includes(code)) return t(`errors.${code}`)
    return err.message
  }
  return t(fallbackKey)
}

/** "05/10/2026 14:30" in the browser's zone (timestamps are UTC). */
export function formatDateTime(isoUtc: string): string {
  const date = new Date(isoUtc)
  return Number.isNaN(date.getTime())
    ? '—'
    : date.toLocaleString('es-AR', { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit' })
}

/** Today in the browser's zone as `yyyy-mm-dd`. */
export function todayIso(): string {
  const now = new Date()
  const month = String(now.getMonth() + 1).padStart(2, '0')
  const day = String(now.getDate()).padStart(2, '0')
  return `${now.getFullYear()}-${month}-${day}`
}

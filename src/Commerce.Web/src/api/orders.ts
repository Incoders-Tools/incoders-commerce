import { apiFetchOutcome } from './client'
import type { OrderSubmissionOutcome, SubmitOrderRequest } from './types'

export function submitOrder(request: SubmitOrderRequest): Promise<OrderSubmissionOutcome> {
  return apiFetchOutcome<OrderSubmissionOutcome>('/orders/', {
    method: 'POST',
    body: JSON.stringify(request),
  })
}

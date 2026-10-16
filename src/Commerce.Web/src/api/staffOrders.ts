import { ApiError, apiFetch } from './client'
import type {
  StaffCustomerOption,
  StaffOrderQuote,
  StaffOrderSubmission,
  StaffPresentationOption,
  StaffQuoteRequest,
  StaffSubmitOrderRequest,
} from './types'

// The quote and the submit answer a business denial (404 not-found, 409 customer-disabled or another conflict,
// 422 no-effective-price) with the full outcome as the JSON body. Those come back as the outcome; anything else
// (400 validation, 403 without TakeOrders, network) stays an ApiError.
const DENIAL_STATUSES = new Set([404, 409, 422])

async function outcomeOf<TOutcome>(request: Promise<TOutcome>): Promise<TOutcome> {
  try {
    return await request
  } catch (err) {
    if (
      err instanceof ApiError &&
      DENIAL_STATUSES.has(err.status) &&
      typeof (err.body as { status?: unknown } | undefined)?.status === 'string'
    ) {
      return err.body as TOutcome
    }
    throw err
  }
}

const withSearch = (path: string, search: string) => `${path}?search=${encodeURIComponent(search)}`

export const searchStaffCustomers = (search: string) =>
  apiFetch<StaffCustomerOption[]>(withSearch('/orders/staff/customers', search))

export const searchStaffPresentations = (search: string) =>
  apiFetch<StaffPresentationOption[]>(withSearch('/orders/staff/presentations', search))

export const quoteStaffOrder = (request: StaffQuoteRequest) =>
  outcomeOf(apiFetch<StaffOrderQuote>('/orders/staff/quote', { method: 'POST', body: JSON.stringify(request) }))

export const submitStaffOrder = (request: StaffSubmitOrderRequest) =>
  outcomeOf(apiFetch<StaffOrderSubmission>('/orders/staff', { method: 'POST', body: JSON.stringify(request) }))

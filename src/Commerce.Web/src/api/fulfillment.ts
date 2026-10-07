import { apiFetch } from './client'

// Endpoints/Fulfillment.cs + Persistence/FulfillmentRecords.cs, mirrored. Statuses travel as their names.

export const ORDER_STATUSES = [
  'Confirmed',
  'InPreparation',
  'ReadyToDispatch',
  'OutForDelivery',
  'Delivered',
  'PartiallyDelivered',
  'Cancelled',
] as const
export type OrderStatus = (typeof ORDER_STATUSES)[number]

export type OrderSettlement = 'CurrentAccount' | 'PaidOnDelivery'

export type RunStatus = 'Planned' | 'OutForDelivery' | 'Completed'

export interface OrderTrackingSummary {
  orderId: string
  orderNumber: string
  submittedAtUtc: string
  origin: 'Guest' | 'RegisteredCustomer'
  customerId: string | null
  customerName: string
  status: OrderStatus
  total: number
  lineCount: number
  runId: string | null
  runNumber: number | null
  runDate: string | null
  remitoNumber: string | null
  deliveredTotal: number | null
  settlement: OrderSettlement | null
  deliveredAtUtc: string | null
  note: string | null
  cancelReason: string | null
}

export interface OrderTrackingLine {
  lineNo: number
  presentationId: string
  productName: string
  presentationName: string
  quantityBehavior: 'FixedQuantity' | 'Weighted' | 'Bulk'
  quantity: number
  unitNetPrice: number
  lineTotal: number
  deliveredQuantity: number | null
}

export interface OrderPartyData {
  displayName: string
  legalName: string | null
  taxIdType: string | null
  taxId: string | null
  taxCondition: string | null
  phone: string | null
  address: string | null
  locality: string | null
  province: string | null
  postalCode: string | null
  deliveryNotes: string | null
}

export interface OrderTrackingDetail {
  summary: OrderTrackingSummary
  lines: OrderTrackingLine[]
  party: OrderPartyData
  allowedTransitions: OrderStatus[]
}

export interface DeliveryRunSummary {
  runId: string
  runNumber: number
  runDate: string
  driverName: string | null
  vehicle: string | null
  notes: string | null
  status: RunStatus
  orderCount: number
  total: number
  createdAtUtc: string
  dispatchedAtUtc: string | null
  completedAtUtc: string | null
}

export interface DeliveryRunDetail {
  run: DeliveryRunSummary
  stops: { stopNo: number; order: OrderTrackingSummary }[]
}

export interface OrganizationDocumentProfile {
  name: string
  legalName: string | null
  taxId: string | null
  taxCondition: string | null
  grossIncomeNumber: string | null
  activityStartDate: string | null
  fiscalAddress: string | null
  documentFooter: string | null
  logoUrl: string | null
  primaryColor: string | null
}

export interface BranchDocumentProfile {
  branchId: string
  name: string
  code: number
  address: string | null
  locality: string | null
  phone: string | null
  email: string | null
  warehouseAddress: string | null
}

export interface RemitoDocument {
  orderId: string
  remitoNumber: string
  orderNumber: string
  issuedOn: string
  organization: OrganizationDocumentProfile
  branch: BranchDocumentProfile
  customer: OrderPartyData
  lines: OrderTrackingLine[]
  total: number
  runNumber: number | null
  driverName: string | null
  vehicle: string | null
  note: string | null
}

export interface OrderListFilters {
  status?: OrderStatus | 'Active' | ''
  from?: string
  to?: string
  search?: string
}

export function listOrders(filters: OrderListFilters = {}): Promise<OrderTrackingSummary[]> {
  const query = new URLSearchParams()
  if (filters.status) query.set('status', filters.status)
  if (filters.from) query.set('from', filters.from)
  if (filters.to) query.set('to', filters.to)
  if (filters.search) query.set('search', filters.search)
  const queryString = query.toString()
  return apiFetch<OrderTrackingSummary[]>(queryString ? `/orders/tracking?${queryString}` : '/orders/tracking')
}

export function getOrder(orderId: string): Promise<OrderTrackingDetail> {
  return apiFetch<OrderTrackingDetail>(`/orders/tracking/${orderId}`)
}

export function changeOrderStatus(orderId: string, status: OrderStatus, reason?: string): Promise<void> {
  return apiFetch<void>(`/orders/tracking/${orderId}/status`, {
    method: 'POST',
    body: JSON.stringify({ status, reason: reason ?? null }),
  })
}

export function getRemitos(orderIds: string[]): Promise<RemitoDocument[]> {
  return apiFetch<RemitoDocument[]>('/orders/tracking/remitos', { method: 'POST', body: JSON.stringify({ orderIds }) })
}

export interface DeliveryRunRequest {
  runDate: string
  driverName: string | null
  vehicle: string | null
  notes: string | null
  orderIds: string[]
}

export function listRuns(filters: { from?: string; to?: string; status?: RunStatus | '' } = {}): Promise<DeliveryRunSummary[]> {
  const query = new URLSearchParams()
  if (filters.from) query.set('from', filters.from)
  if (filters.to) query.set('to', filters.to)
  if (filters.status) query.set('status', filters.status)
  const queryString = query.toString()
  return apiFetch<DeliveryRunSummary[]>(queryString ? `/deliveries/runs?${queryString}` : '/deliveries/runs')
}

export function getRun(runId: string): Promise<DeliveryRunDetail> {
  return apiFetch<DeliveryRunDetail>(`/deliveries/runs/${runId}`)
}

export function createRun(request: DeliveryRunRequest): Promise<{ runId: string }> {
  return apiFetch<{ runId: string }>('/deliveries/runs', { method: 'POST', body: JSON.stringify(request) })
}

export function updateRun(runId: string, request: DeliveryRunRequest): Promise<void> {
  return apiFetch<void>(`/deliveries/runs/${runId}`, { method: 'PUT', body: JSON.stringify(request) })
}

/** Discards a run still planned: its orders are free again for another run. */
export function deleteRun(runId: string): Promise<void> {
  return apiFetch<void>(`/deliveries/runs/${runId}`, { method: 'DELETE' })
}

export function dispatchRun(runId: string): Promise<void> {
  return apiFetch<void>(`/deliveries/runs/${runId}/dispatch`, { method: 'POST' })
}

export interface OrderReturnInput {
  orderId: string
  delivered: boolean
  settlement: OrderSettlement | null
  lines: { lineNo: number; deliveredQuantity: number }[] | null
}

export function settleRun(runId: string, orders: OrderReturnInput[]): Promise<void> {
  return apiFetch<void>(`/deliveries/runs/${runId}/settle`, { method: 'POST', body: JSON.stringify({ orders }) })
}

export function getOrganizationDocumentProfile(): Promise<OrganizationDocumentProfile> {
  return apiFetch<OrganizationDocumentProfile>('/account/organization/document-profile')
}

export type OrganizationDocumentProfileRequest = Omit<OrganizationDocumentProfile, 'name'>

export function updateOrganizationDocumentProfile(request: OrganizationDocumentProfileRequest): Promise<void> {
  return apiFetch<void>('/account/organization/document-profile', { method: 'PUT', body: JSON.stringify(request) })
}

export function listBranchDocumentProfiles(): Promise<BranchDocumentProfile[]> {
  return apiFetch<BranchDocumentProfile[]>('/account/branch-profiles')
}

export type BranchDocumentProfileRequest = Pick<
  BranchDocumentProfile,
  'address' | 'locality' | 'phone' | 'email' | 'warehouseAddress'
>

export function updateBranchDocumentProfile(branchId: string, request: BranchDocumentProfileRequest): Promise<void> {
  return apiFetch<void>(`/account/branch-profiles/${branchId}`, { method: 'PUT', body: JSON.stringify(request) })
}

/** Error codes the fulfillment endpoints answer with (`{ error }`), for messages. */
export const FULFILLMENT_ERRORS = [
  'invalid-transition',
  'cancel-reason-required',
  'order-in-another-run',
  'order-not-dispatchable',
  'run-not-planned',
  'run-empty',
  'run-not-out-for-delivery',
  'returns-must-cover-every-order',
  'invalid-delivered-quantity',
  'duplicate-order',
  'order-not-found',
] as const

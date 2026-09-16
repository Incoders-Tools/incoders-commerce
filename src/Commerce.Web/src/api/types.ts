// Mirrors Commerce.Cloud.Api request/response shapes exactly
// (Endpoints/Account.cs, Endpoints/Catalog.cs, Endpoints/Ordering.cs) — the
// SPA has no independent data model, per spec.md "MUST NOT depend on mocked
// or hardcoded data".

export interface SignInRequest {
  email: string
  password: string
}

export interface SignedInResponse {
  organizationId: string
  userId: string
  displayName: string
}

export interface RoleDto {
  name: string
  permissions: number
}

export interface RenameProductRequest {
  targetBranchId: string
  currentName: string
  categoryId: string
  defaultUnitId: string
  newName: string
  isOffline: boolean
  correlationId: string
}

export type ManagementOutcomeStatus = 'Allowed' | 'Denied'

export interface ManagementOutcome {
  status: ManagementOutcomeStatus
  reason: string
  updatedProduct?: {
    id: string
    organizationId: string
    name: string
    categoryId: string
    defaultUnitId: string
  } | null
}

export interface OrderLineSnapshot {
  productId: string
  productName: string
  presentationId: string
  presentationName: string
  quantityBehavior: number
  unitId: string
  quantity: number
}

export interface SubmitOrderRequest {
  orderId: string
  customerId: string
  accessCredential: string
  accessEnabled: boolean
  destinationBranchId: string
  actorId: string
  lines: OrderLineSnapshot[]
  correlationId: string
}

export type OrderSubmissionOutcomeStatus = 'Accepted' | 'Denied'

export interface OrderSubmissionOutcome {
  status: OrderSubmissionOutcomeStatus
  reason: string
  order?: {
    id: string
    organizationId: string
    status: number
  } | null
}

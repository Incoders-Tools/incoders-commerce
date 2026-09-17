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

// commerce-password-recovery design.md "Interfaces / Contracts"
export interface ResetPasswordRequest {
  email: string
}

export interface ConfirmResetPasswordRequest {
  token: string
  newPassword: string
}

export interface RenewPasswordRequest {
  currentPassword: string
  newPassword: string
}

export interface AdminResetPasswordRequest {
  newPassword: string
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

// Cloud.Api has no JsonStringEnumConverter registered, so C# enums (here,
// Commerce.Application.Management.ManagementOutcomeStatus) serialize as
// their raw numeric ordinal, NOT their name — a real-backend E2E test
// (src/Commerce.Web/e2e/catalog.spec.ts) caught this: the screen used to
// compare `outcome.status === 'Allowed'`, which is never true against a real
// response and silently rendered every successful rename as "Denied:
// allowed". The two mocked Vitest specs never caught it because they
// fabricated the string literal directly. Keep this numeric and mirror the
// C# enum's declared member order exactly (Management/ManagementOutcome.cs).
export const ManagementOutcomeStatus = {
  Allowed: 0,
  Denied: 1,
} as const
export type ManagementOutcomeStatus = (typeof ManagementOutcomeStatus)[keyof typeof ManagementOutcomeStatus]

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

// Same real-numeric-enum shape as ManagementOutcomeStatus above — mirrors
// Commerce.Cloud.Api.Ordering.OrderSubmissionOutcomeStatus's declared member
// order exactly.
export const OrderSubmissionOutcomeStatus = {
  Accepted: 0,
  Denied: 1,
} as const
export type OrderSubmissionOutcomeStatus = (typeof OrderSubmissionOutcomeStatus)[keyof typeof OrderSubmissionOutcomeStatus]

export interface OrderSubmissionOutcome {
  status: OrderSubmissionOutcomeStatus
  reason: string
  order?: {
    id: string
    organizationId: string
    status: number
  } | null
}

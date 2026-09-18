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
  // commerce-customer-identity "Web admin gating": server-derived
  // (actor.EffectivePermissions), never trusted from the client.
  permissions: number
}

// Mirrors Commerce.Domain.Identity.Permission's [Flags] bit layout exactly —
// commerce-customer-identity design.md "Web admin gating".
export const Permission = {
  None: 0,
  ViewSales: 1 << 0,
  ManageCatalog: 1 << 1,
  ManageUsers: 1 << 2,
  ManageBranchSettings: 1 << 3,
} as const
export type Permission = (typeof Permission)[keyof typeof Permission]

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

// commerce-customer-identity security fix: no caller-supplied enabled flag —
// there is deliberately no `accessEnabled` member here. Cloud.Api resolves
// enabled/binding state from the persisted store, never from the request.
export interface SubmitOrderRequest {
  orderId: string
  customerId: string
  accessCredential: string
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

// commerce-customer-identity: Customers.cs / CustomerRecords.cs DTOs, mirrored
// exactly — string enums here because Cloud.Api's Customers.cs parses these
// via `Enum.TryParse<T>(request.Field, ...)`, unlike the numeric-ordinal
// enums above which have no such parse step.
export const CustomerKind = {
  Retail: 'Retail',
  Wholesale: 'Wholesale',
} as const
export type CustomerKind = (typeof CustomerKind)[keyof typeof CustomerKind]

export const TaxIdType = {
  None: 'None',
  Cuit: 'Cuit',
  Cuil: 'Cuil',
} as const
export type TaxIdType = (typeof TaxIdType)[keyof typeof TaxIdType]

export const TaxCondition = {
  ConsumidorFinal: 'ConsumidorFinal',
  ResponsableInscripto: 'ResponsableInscripto',
  Monotributo: 'Monotributo',
  Exento: 'Exento',
  NoAplica: 'NoAplica',
} as const
export type TaxCondition = (typeof TaxCondition)[keyof typeof TaxCondition]

export interface CustomerRecord {
  id: string
  organizationId: string
  customerKind: CustomerKind
  displayName: string
  legalName: string | null
  taxIdType: TaxIdType
  taxId: string | null
  taxCondition: TaxCondition
  phone: string | null
  email: string | null
  addressStreet: string | null
  addressNumber: string | null
  neighborhood: string | null
  locality: string | null
  province: string | null
  postalCode: string | null
  deliveryNotes: string | null
  discountPercentage: number | null
  paymentTerms: string | null
  notes: string | null
  isEnabled: boolean
  createdAtUtc: string
  createdByUserId: string
  updatedAtUtc: string
}

// No `organizationId`/`isEnabled`/`createdByUserId` — org comes from the
// tenant scope, enabled is true at birth, actor comes from the cookie claim
// (Endpoints/Customers.cs `CreateCustomerRequest`).
export interface CreateCustomerRequest {
  customerKind: CustomerKind
  displayName: string
  legalName: string | null
  taxIdType: TaxIdType
  taxId: string | null
  taxCondition: TaxCondition
  phone: string | null
  email: string | null
  addressStreet: string | null
  addressNumber: string | null
  neighborhood: string | null
  locality: string | null
  province: string | null
  postalCode: string | null
  deliveryNotes: string | null
  discountPercentage: number | null
  paymentTerms: string | null
  notes: string | null
}

// `CreateCustomerRequest` minus `customerKind` (read-only at edit) plus
// `isEnabled` (`UpdateCustomerRequest` in Endpoints/Customers.cs).
export type UpdateCustomerRequest = Omit<CreateCustomerRequest, 'customerKind'> & {
  isEnabled: boolean
}

export interface CreateCustomerResponse {
  customerId: string
}

// Shown exactly once — the server never stores the plaintext.
export interface IssueOrderingAccessResponse {
  credential: string
}

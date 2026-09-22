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
  isSystemAdmin: boolean
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

// commerce-pricing-engine design.md "OrderLineSnapshot extension and where
// resolution runs": price-free — a caller has nowhere to put a price. The
// server resolves UnitListPrice/AppliedDiscountPercentage/UnitNetPrice/
// LineTotal from PricingResolutionService and freezes them into the stored
// order's OrderLineSnapshot, never from anything sent here.
export interface SubmitOrderLine {
  productId: string
  presentationId: string
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
  lines: SubmitOrderLine[]
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
    // commerce-pricing-engine: the frozen, server-resolved total per line —
    // never sent by the client, only ever returned once the order is
    // accepted.
    lines?: { lineTotal: number }[]
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

// commerce-pricing-engine design.md "Verified deviation" / Work Unit 1 —
// mirrors Commerce.Domain.Catalog.QuantityBehavior's declared member order
// exactly. No JsonStringEnumConverter is registered in Cloud.Api (see the
// ManagementOutcomeStatus remark above), so this serializes as its numeric
// ordinal.
export const QuantityBehavior = {
  FixedQuantity: 0,
  Weighted: 1,
  Bulk: 2,
} as const
export type QuantityBehavior = (typeof QuantityBehavior)[keyof typeof QuantityBehavior]

// Endpoints/Catalog.cs `ProductRecord` / `CreateProductRequest`, mirrored
// exactly. `organizationId` is never sent by the client on create — the
// tenant scope supplies it server-side.
export interface ProductRecord {
  id: string
  organizationId: string
  name: string
  categoryId: string
  defaultUnitId: string
  createdAtUtc: string
  createdByUserId: string
  updatedAtUtc: string
}

export interface CreateProductRequest {
  name: string
  categoryId: string
  defaultUnitId: string
}

// Endpoints/Catalog.cs `PresentationRecord` / `CreatePresentationRequest` /
// `UpdatePresentationRequest`, mirrored exactly. `identificationCode` is
// optional — an unlabelled presentation stays unconstrained
// (commerce-pricing-engine design.md "Identification code placement and
// uniqueness").
export interface PresentationRecord {
  id: string
  organizationId: string
  productId: string
  name: string
  quantityBehavior: QuantityBehavior
  unitId: string
  identificationCode: string | null
  createdAtUtc: string
  createdByUserId: string
  updatedAtUtc: string
}

export interface CreatePresentationRequest {
  productId: string
  name: string
  quantityBehavior: QuantityBehavior
  unitId: string
  identificationCode: string | null
}

export interface UpdatePresentationRequest {
  name: string
  quantityBehavior: QuantityBehavior
  unitId: string
  identificationCode: string | null
}

// commerce-pricing-engine Endpoints/Pricing.cs `PriceListRecord` /
// `CreatePriceListRequest`, mirrored exactly. `organizationId` is never a
// request field — it comes from the tenant scope.
export interface PriceListRecord {
  id: string
  organizationId: string
  name: string
  isDefault: boolean
  createdAtUtc: string
  createdByUserId: string
}

export interface CreatePriceListRequest {
  name: string
  isDefault: boolean
}

// Endpoints/Pricing.cs `PriceListEntryRecord` / `AppendPriceEntryRequest`,
// mirrored exactly. `effectiveFrom` is a `DateOnly` on the wire, so it
// serializes as a plain `YYYY-MM-DD` string.
export interface PriceListEntryRecord {
  id: string
  organizationId: string
  priceListId: string
  presentationId: string
  unitPrice: number
  effectiveFrom: string
  source: string
  importBatchId: string | null
  createdAtUtc: string
  createdByUserId: string
}

export interface AppendPriceEntryRequest {
  presentationId: string
  unitPrice: number
  effectiveFrom: string
}

// commerce-pricing-engine Work Unit 9: Endpoints/Pricing.cs supplier-mapping
// and import lifecycle DTOs, mirrored exactly. `codeColumn`/`priceColumn`
// are Excel COLUMN LETTERS (design.md "Per-supplier column mapping"), not
// column names or indices.
export interface SupplierPriceMappingRecord {
  id: string
  organizationId: string
  supplierName: string
  sheetName: string
  headerRow: number
  codeColumn: string
  priceColumn: string
  createdAtUtc: string
  createdByUserId: string
}

export interface CreateSupplierMappingRequest {
  supplierName: string
  sheetName: string
  headerRow: number
  codeColumn: string
  priceColumn: string
}

// Deliberately no `Uploaded` state (design.md "Import state machine"): a
// file that fails validation never creates a batch, so this union is the
// COMPLETE set of states a persisted batch can ever be in.
export const ImportBatchStatus = {
  Staged: 'Staged',
  Committed: 'Committed',
  Rejected: 'Rejected',
  Failed: 'Failed',
} as const
export type ImportBatchStatus = (typeof ImportBatchStatus)[keyof typeof ImportBatchStatus]

export interface ImportBatchRecord {
  id: string
  organizationId: string
  supplierMappingId: string
  fileName: string
  rowCount: number
  status: ImportBatchStatus
  uploadedAtUtc: string
  uploadedByUserId: string
  resolvedAtUtc: string | null
}

export interface ImportBatchRowRecord {
  id: string
  organizationId: string
  batchId: string
  rowNumber: number
  rawCode: string | null
  rawPrice: string | null
  presentationId: string | null
  currentPrice: number | null
  proposedPrice: number | null
  matchStatus: 'Matched' | 'NoChange' | 'UnknownCode' | 'InvalidPrice' | 'DuplicateInFile'
  rejectReason: string | null
}

// `GET /pricing/imports/{id}` — mirrors `Results.Ok(new { batch, rows })` exactly.
export interface ImportBatchDetail {
  batch: ImportBatchRecord
  rows: ImportBatchRowRecord[]
}

// commerce-guest-ordering design.md "Interfaces / Contracts" — mirrors
// Commerce.Domain.Ordering.OrderOrigin's declared member order exactly. No
// JsonStringEnumConverter is registered in Cloud.Api (see the
// ManagementOutcomeStatus remark above), so this serializes as its numeric
// ordinal, not its name.
export const OrderOrigin = {
  Guest: 0,
  RegisteredCustomer: 1,
} as const
export type OrderOrigin = (typeof OrderOrigin)[keyof typeof OrderOrigin]

// Mirrors Commerce.Domain.Ordering.GuestContactChannel — currently a single
// member (`Email`); a later channel (SMS/WhatsApp) widens this, not the
// schema.
export const GuestContactChannel = {
  Email: 0,
} as const
export type GuestContactChannel = (typeof GuestContactChannel)[keyof typeof GuestContactChannel]

// Endpoints/PublicOrdering.cs DTOs, mirrored exactly. Deliberately no
// organizationId/branchId member on any of these — a guest cannot address
// another org or branch (public-order-surface spec.md "Public Catalogue
// Read").
export interface GuestVerificationRequest {
  documentId: string
  email: string
}

export interface GuestVerificationRequestedResponse {
  verificationId: string
}

export interface GuestVerificationConfirmRequest {
  verificationId: string
  code: string
}

export interface SubmitGuestOrderRequest {
  orderId: string
  verificationId: string
  documentId: string
  email: string
  displayName: string
  deliveryNotes: string | null
  lines: SubmitOrderLine[]
  correlationId: string
}

// Endpoints/CustomerSession.cs DTOs, mirrored exactly.
export interface CustomerSignInRequest {
  email: string
  password: string
}

export interface CustomerSignedInResponse {
  customerId: string
  email: string
}



export interface UserSummary { userId: string; email: string; roleNames: string[]; isRevoked: boolean }
export interface CreateUserRequest { email: string; password: string; roleNames: string[]; branchIds: string[]; customerId?: string | null }
export interface CreateUserResponse { userId: string }
export interface BranchSummary { branchId: string; branchName: string }
export interface CreateBranchRequest { branchName: string }
export interface CreateBranchResponse { branchId: string }
export interface OrganizationSummary { id: string; name: string; createdAt: string }
export interface CreateOrganizationRequest { organizationName: string; branchName?: string | null; adminEmail: string; adminPassword: string }
export interface CreateOrganizationResponse { organizationId: string; branchId: string; userId: string }

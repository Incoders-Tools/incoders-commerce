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
  // organization-persistence spec, "Selectable Branches In The Session"
  // (B7 U1, Endpoints/Account.cs `SelectableBranch`): the caller's own
  // `BranchScope`, or every branch of the selected organization for a
  // system administrator acting on one. Returned by both sign-in and
  // `/account/me`.
  selectableBranches: SelectableBranch[]
}

export interface SelectableBranch {
  id: string
  name: string
  // Short per-organization code, assigned by the server and never changed
  // (organization-persistence "Branch Short Code"); show it with
  // `formatBranchCode`.
  code: number
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
    // The server serializes `Order.OrderId` as `orderId` (never shown to the customer).
    orderId: string
    organizationId: string
    status: number
    // The human number (`P01-W-37`), plain text; absent on an order stored before orders were numbered.
    orderNumber?: string | null
    // commerce-pricing-engine: the frozen, server-resolved total per line —
    // never sent by the client, only ever returned once the order is
    // accepted.
    // customer-price-lists: `fellBack` marks a line the customer's list did not price and the default list
    // (Mostrador) did; `pricedFromListId` is the list that priced it.
    lines?: { lineTotal: number; fellBack?: boolean; pricedFromListId?: string | null }[]
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
  Dni: 'Dni',
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

/** A person to talk to at a customer (`contacts[]` of the customer JSON). */
export interface CustomerContact {
  id: string
  firstName: string
  lastName: string | null
  phone: string | null
  email: string | null
  role: string | null
  isPrimary: boolean
  sortOrder: number
}

/** Replace-set entry sent on POST/PUT: a known `id` keeps the row, a missing one creates it. */
export interface CustomerContactInput {
  id?: string
  firstName: string
  lastName: string | null
  phone: string | null
  email: string | null
  role: string | null
  isPrimary: boolean
  sortOrder: number
}

export interface CustomerRecord {
  id: string
  organizationId: string
  customerKind: CustomerKind
  displayName: string
  legalName: string | null
  taxIdType: TaxIdType
  taxId: string | null
  taxCondition: TaxCondition
  cityId: string | null
  cityName: string | null
  provinceId: string | null
  provinceName: string | null
  businessTypeId: string | null
  businessTypeName: string | null
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
  /** The price list this customer is priced from; `priceListName` is null when the list is not visible in the branch. */
  priceListId?: string | null
  priceListName?: string | null
  createdAtUtc: string
  createdByUserId: string
  updatedAtUtc: string
  contacts: CustomerContact[]
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
  // Optional: on PUT an omitted value keeps the stored one; to clear send the
  // all-zero GUID for the ids.
  cityId?: string
  businessTypeId?: string
  // Omitted on create: the organization's default customer list. On PUT omitted keeps the
  // stored list and the all-zero GUID clears it.
  priceListId?: string
  // Replace-set on PUT (max 50, at most one primary): omitted keeps the
  // stored contacts, an empty array clears them.
  contacts?: CustomerContactInput[]
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

/** Server-side filters of `GET /customers`. */
export interface CustomerListFilters {
  search?: string
  cityId?: string
  businessTypeId?: string
}

// `CreateCustomerRequest` minus `customerKind` (read-only at edit) plus
// `isEnabled` (`UpdateCustomerRequest` in Endpoints/Customers.cs).
export type UpdateCustomerRequest = Omit<CreateCustomerRequest, 'customerKind'> & {
  isEnabled: boolean
  /** The `updatedAtUtc` last read; a mismatch answers 409 `customer-modified`. */
  expectedUpdatedAtUtc?: string
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
  branchId: string
  name: string
  categoryId: string
  defaultUnitId: string
  createdAtUtc: string
  createdByUserId: string
  updatedAtUtc: string
  // Soft deletion: an inactive product is hidden from the default lists, the POS and new receptions.
  isActive: boolean
  deactivatedAtUtc: string | null
}

export interface CreateProductRequest {
  name: string
  // Optional: omitted means the organization's default "Sin categoría".
  categoryId?: string
  defaultUnitId: string
}

// Endpoints/Categories.cs `CategoryRecord` / `CategoryRequest`, mirrored
// exactly (catalog-categories spec). Categories belong to the organization and
// are shared by every branch.
export interface CategoryRecord {
  id: string
  organizationId: string
  name: string
  iconKey: string
  createdAtUtc: string
  updatedAtUtc: string
}

export interface CategoryRequest {
  name: string
  iconKey: string
}

// Endpoints/Catalog.cs `PresentationRecord` / `CreatePresentationRequest` /
// `UpdatePresentationRequest`, mirrored exactly. `identificationCode` is
// optional — an unlabelled presentation stays unconstrained
// (commerce-pricing-engine design.md "Identification code placement and
// uniqueness").
export interface PresentationRecord {
  id: string
  organizationId: string
  branchId: string
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

// B7 U5b (catalog-item-identification spec "Copying Catalog Between
// Branches"), `Endpoints/Catalog.cs` `CopyCatalogRequest`/`CopyCatalogResponse`/
// `SkippedPresentationDto`, mirrored exactly. `sourceBranchId` MUST equal the
// caller's currently selected branch (`X-Branch-Id`) — the server rejects a
// mismatch with 400, so the UI never lets the operator pick it independently.
export interface CopyCatalogRequest {
  sourceBranchId: string
  targetBranchId: string
  productIds?: string[]
}

export interface SkippedPresentation {
  presentationId: string
  identificationCode: string | null
  reason: string
}

export interface CopyCatalogResponse {
  productsCopied: number
  presentationsCopied: number
  skipped: SkippedPresentation[]
  priceListId: string | null
  priceEntriesCopied: number
}

// commerce-pricing-engine Endpoints/Pricing.cs `PriceListRecord` /
// `CreatePriceListRequest`, mirrored exactly. `organizationId` is never a
// request field — it comes from the tenant scope.
export interface PriceListRecord {
  id: string
  organizationId: string
  branchId: string
  name: string
  isDefault: boolean
  /** The list this one may never price below; absent/null when it has no floor. */
  floorPriceListId?: string | null
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
  branchId: string
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
  branchId: string
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
  branchId: string
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
  branchId: string
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



export interface UserSummary { userId: string; email: string; roleNames: string[]; isRevoked: boolean; branchIds: string[] }
export interface CreateUserRequest { email: string; password: string; roleNames: string[]; branchIds: string[]; customerId?: string | null }
export interface CreateUserResponse { userId: string }
export interface BranchSummary { branchId: string; branchName: string; code: number }
export interface CreateBranchRequest { branchName: string }
export interface CreateBranchResponse { branchId: string; code: number }
export interface OrganizationSummary { id: string; name: string; createdAt: string }
export interface CreateOrganizationRequest { organizationName: string; branchName?: string | null; adminEmail: string; adminPassword: string }
export interface CreateOrganizationResponse { organizationId: string; branchId: string; userId: string }
// T5b: minimal organization branding — logoUrl + primaryColor only (no upload, no other fields).
export interface OrganizationBranding { logoUrl: string | null; primaryColor: string | null }
export interface OrganizationSettings { quantityDecimalSeparator: 'Comma' | 'Dot'; defaultCustomerPriceListId?: string | null }
// Every field is optional on the wire: an omitted one is left unchanged.
export interface UpdateOrganizationSettingsRequest {
  quantityDecimalSeparator?: 'Comma' | 'Dot'
  defaultCustomerPriceListId?: string
  clearDefaultCustomerPriceList?: boolean
}
export interface UpdateOrganizationBrandingRequest { logoUrl: string | null; primaryColor: string | null }
// branch-discount-pin: whether a branch has a discount PIN and when it last changed; the PIN itself is never returned.
export interface BranchDiscountPinStatus { isSet: boolean; version: number | null; changedAtUtc: string | null }

// Customers master data (cities, business types): organization-scoped
// catalogs with the same shape, served under `/customers/cities` and
// `/customers/business-types`. There is no DELETE: an entry is deactivated.
export interface MasterDataEntry {
  id: string
  organizationId: string
  name: string
  key: string
  sortOrder: number
  isActive: boolean
  createdAtUtc: string
  updatedAtUtc: string
}

// On PUT an omitted `key`/`sortOrder` keeps the stored value, but an omitted
// `isActive` becomes true, so the client always sends it.
export interface MasterDataRequest {
  name: string
  key?: string
  sortOrder?: number
  isActive: boolean
}

/** `GET /geo/provinces`: `id` is the INDEC code (e.g. "06"). */
export interface GeoProvince {
  id: string
  isoCode: string
  name: string
  countryCode: string
  countryName: string
}

/** Core (organization-independent) city, `GET /geo/cities`. */
export interface GeoCity {
  id: string
  indecId: string | null
  name: string
  provinceId: string
  provinceName: string
  countryCode: string
  departmentName: string | null
  isActive: boolean
  createdAtUtc: string
  updatedAtUtc: string
}

export interface GeoCityFilters {
  search?: string
  provinceId?: string
  limit?: number
  offset?: number
  includeInactive?: boolean
}

/** Sysadmin write body; on PUT an omitted field is kept and a blank `departmentName` clears it. */
export interface GeoCityRequest {
  name: string
  provinceId?: string
  departmentName?: string
  isActive?: boolean
}

// Suppliers (Suppliers.cs / SupplierAccount.cs DTOs, mirrored). Contacts share
// the customer contact shape; tax id type / condition are the same string enums.
export type SupplierContact = CustomerContact
export type SupplierContactInput = CustomerContactInput

export interface SupplierRecord {
  id: string
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
  postalCode: string | null
  cityId: string | null
  cityName: string | null
  provinceId: string | null
  provinceName: string | null
  categoryId: string | null
  categoryName: string | null
  paymentTermsDays: number | null
  bankCbu: string | null
  bankAlias: string | null
  notes: string | null
  isEnabled: boolean
  createdAtUtc: string
  updatedAtUtc: string
  /** What the business owes the supplier (Credit - Debit); negative = in our favour. */
  balance: number
  contacts: SupplierContact[]
}

/** Server-side filters of `GET /suppliers`. */
export interface SupplierListFilters {
  search?: string
  categoryId?: string
  cityId?: string
  /** `undefined` = both. */
  enabled?: boolean
}

export interface CreateSupplierRequest {
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
  postalCode: string | null
  // On PUT the all-zero GUID clears; an omitted id keeps the stored one.
  cityId?: string
  categoryId?: string
  paymentTermsDays: number | null
  bankCbu: string | null
  bankAlias: string | null
  notes: string | null
  contacts?: SupplierContactInput[]
}

export type UpdateSupplierRequest = CreateSupplierRequest & {
  isEnabled: boolean
  /** The `updatedAtUtc` last read; a mismatch answers 409 `supplier-modified`. */
  expectedUpdatedAtUtc?: string
}

export interface CreateSupplierResponse {
  supplierId: string
}

export interface SupplierBalance {
  supplierId: string
  balance: number
  overdue: number
}

export const MovementKind = {
  OpeningBalance: 'OpeningBalance',
  Invoice: 'Invoice',
  DebitNote: 'DebitNote',
  CreditNote: 'CreditNote',
  Payment: 'Payment',
  Adjustment: 'Adjustment',
} as const
export type MovementKind = (typeof MovementKind)[keyof typeof MovementKind]

export type MovementDirection = 'Debit' | 'Credit'

export interface AccountMovement {
  id: string
  supplierId: string
  kind: MovementKind
  direction: MovementDirection
  amount: number
  /** yyyy-MM-dd */
  occurredOn: string
  dueOn: string | null
  documentReference: string | null
  concept: string
  reversesMovementId: string | null
  createdAtUtc: string
  createdByUserId: string
}

export interface StatementLine extends AccountMovement {
  runningBalance: number
  reversed: boolean
  reversedByMovementId: string | null
}

export interface AccountStatement {
  openingBalance: number
  movements: StatementLine[]
  closingBalance: number
}

export interface AccountSummary {
  asOf: string
  balance: number
  overdue: number
  current: number
  aging: { d0_30: number; d31_60: number; d61_90: number; d90plus: number }
}

export interface RegisterMovementRequest {
  kind: MovementKind
  amount: number
  occurredOn?: string
  dueOn?: string
  documentReference?: string
  concept: string
  /** Required only for an Adjustment. */
  direction?: MovementDirection
}

export interface ReverseMovementRequest {
  concept?: string
  occurredOn?: string
}

// ---------------------------------------------------------------------------
// Purchases and stock (Endpoints/PurchaseReceptions.cs, Endpoints/Stock.cs).
// Unlike the catalog DTOs, these serialize their enums as strings.
// ---------------------------------------------------------------------------

export type StockQuantityBehavior = 'FixedQuantity' | 'Weighted' | 'Bulk'
export type ReceptionStatus = 'Draft' | 'Confirmed' | 'Voided'
export type ReceptionDocumentType = 'Invoice' | 'DeliveryNote' | 'Other'

export interface ReceptionSummary {
  id: string
  supplierId: string
  supplierName: string
  status: ReceptionStatus
  number: string | null
  documentType: ReceptionDocumentType
  documentReference: string | null
  occurredOn: string
  dueOn: string | null
  totalAmount: number
  lineCount: number
  createdAtUtc: string
  updatedAtUtc: string
}

export interface ReceptionLine {
  id: string
  presentationId: string
  productName: string
  presentationName: string
  quantityBehavior: StockQuantityBehavior
  quantity: number
  unitCost: number
  lineTotal: number
  lotCode: string | null
  expiresOn: string | null
  sortOrder: number
}

export interface ReceptionRecord extends ReceptionSummary {
  notes: string | null
  ledgerInvoiceMovementId: string | null
  ledgerReversalMovementId: string | null
  voidReason: string | null
  confirmedAtUtc: string | null
  voidedAtUtc: string | null
  lines: ReceptionLine[]
}

export interface ReceptionListFilters {
  status?: ReceptionStatus
  supplierId?: string
  from?: string
  to?: string
  search?: string
}

export interface ReceptionLineInput {
  presentationId: string
  quantity: number
  unitCost: number
  lotCode?: string | null
  expiresOn?: string | null
}

export interface ReceptionRequest {
  supplierId: string
  documentType: ReceptionDocumentType
  documentReference?: string | null
  occurredOn?: string | null
  dueOn?: string | null
  notes?: string | null
  lines: ReceptionLineInput[]
  expectedUpdatedAtUtc?: string
}

export interface StockLevel {
  presentationId: string
  productId: string
  productName: string
  presentationName: string
  quantityBehavior: StockQuantityBehavior
  unitId: string
  identificationCode: string | null
  onHand: number
  minimumQuantity: number | null
  belowMinimum: boolean
  shortfall: number | null
  lastMovementAtUtc: string | null
}

export type StockMovementKind =
  | 'Opening'
  | 'PurchaseReceipt'
  | 'Sale'
  | 'Adjustment'
  | 'Shrinkage'
  | 'CountCorrection'
  | 'Reversal'

export type ManualStockKind = 'Opening' | 'Shrinkage' | 'CountCorrection' | 'Adjustment'

export interface StockMovement {
  id: string
  kind: StockMovementKind
  quantity: number
  occurredAtUtc: string
  reason: string | null
  lotCode: string | null
  sourceType: string | null
  sourceId: string | null
  sourceNumber: string | null
  reversesMovementId: string | null
  createdByUserId: string | null
  balanceAfter: number
}

export interface StockHistoryPage {
  presentationId: string
  onHand: number
  total: number
  page: number
  pageSize: number
  items: StockMovement[]
}

export interface StockAdjustmentRequest {
  presentationId: string
  kind: ManualStockKind
  quantity: number
  reason: string
}

export interface StockAdjustmentResult {
  movement: StockMovement
  onHand: number
}

export interface StockMinimum {
  presentationId: string
  minimumQuantity: number | null
  updatedAtUtc: string | null
}

// --- Price composition: base price + rate components = final price ---
export type CalculationBase = 'Base' | 'Subtotal'

export interface RateComponent {
  code: string
  label: string
  percentage: number
  calculationBase: CalculationBase
  order: number
}

export interface CompositionVersion {
  id: string
  effectiveFrom: string
  components: RateComponent[]
}

export interface CompositionRecord {
  source: 'list' | 'organization' | 'none'
  effectiveFrom: string | null
  components: RateComponent[]
  history: CompositionVersion[]
}

export interface BreakdownComponent extends RateComponent {
  calculationAmount: number
  amount: number
}

export interface BreakdownItem {
  presentationId: string
  productId: string
  productName: string
  presentationName: string
  identificationCode: string | null
  entryEffectiveFrom: string
  base: number
  components: BreakdownComponent[]
  final: number
}

export interface PriceListBreakdown {
  priceListId: string
  priceListName: string
  on: string
  floorPriceListId: string | null
  composition: CompositionRecord
  items: BreakdownItem[]
}

/** Publish a new composition: the full component set, or just the markup. */
export type PublishCompositionRequest =
  | { effectiveFrom: string; components: RateComponent[] }
  | { effectiveFrom: string; remarcacionPercentage: number }

export type CopyPriceListRequest = {
  name: string
  effectiveFrom?: string
  floorPriceListId?: string
  clearFloor?: boolean
} & ({ remarcacionPercentage: number } | { components: RateComponent[] })

export interface CopyPriceListResponse {
  priceList: PriceListRecord
  entriesCopied: number
  composition: CompositionRecord
}

/** One product a change would put below its floor list (409 `price-below-floor`; nothing is written). */
export interface FloorViolation {
  priceListId: string
  priceListName: string
  floorPriceListId: string
  floorPriceListName: string
  presentationId: string
  productId: string
  productName: string
  presentationName: string
  price: number
  floorPrice: number
}

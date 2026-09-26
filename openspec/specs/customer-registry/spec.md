# Customer Registry Specification

## Purpose

Define the `Customer` commercial-party aggregate: an organization-bound
entity carrying identity, fiscal, address, and commercial data, provisioned
only by admins, independent of any login, and made available offline for
sale-time selection via existing sync replication.

## Requirements

### Requirement: Organization-Scoped Customer Persistence

The system MUST persist each `Customer` as a row scoped to exactly one
`OrganizationId`, protected by Postgres row-level security following
`PostgresOrganizationStore`'s convention (`FORCE ROW LEVEL SECURITY`,
`REVOKE ALL FROM PUBLIC` plus explicit `GRANT`, `NULLIF(current_setting(...))`
pooler idiom). A `Customer` MUST carry: `Id`, `OrganizationId`, `CustomerKind`
(`Retail` or `Wholesale`), `DisplayName`, nullable `LegalName`, `TaxIdType`
(`None|Cuit|Cuil`) with its `TaxId` number, `TaxCondition`
(`ConsumidorFinal|ResponsableInscripto|Monotributo|Exento|NoAplica`), nullable
`Phone`, nullable `Email`, `AddressStreet`, `AddressNumber`, optional
`Neighborhood`, `Locality`, `Province`, `PostalCode`, `DeliveryNotes`,
nullable `DiscountPercentage`, `PaymentTerms`, `Notes`, `IsEnabled`,
`CreatedAtUtc`, `CreatedByUserId`. `Customer` MUST NOT carry a `UserId` field.

#### Scenario: Retail customer created with minimal fields

- GIVEN a `ManageUsers` holder in Organization A submits a `Retail` customer
  with only `DisplayName` and `Phone`
- WHEN the customer is created
- THEN the row persists with `CustomerKind: Retail`, the supplied fields, and
  all other fiscal/address fields left nullable or empty

#### Scenario: Wholesale customer created with full fiscal field set

- GIVEN a `ManageUsers` holder submits a `Wholesale` customer with
  `LegalName`, `TaxIdType: Cuit`, a `TaxId` number, `TaxCondition:
  ResponsableInscripto`, full address, `DeliveryNotes`, `DiscountPercentage`,
  and `PaymentTerms`
- WHEN the customer is created
- THEN all submitted fields persist exactly, scoped to the caller's
  organization

#### Scenario: Second organization cannot read or write another org's customers

- GIVEN a customer row exists in Organization A
- WHEN a request scoped to Organization B reads, edits, or lists customers
- THEN Organization A's row is neither returned nor mutated

### Requirement: Admin-Only Provisioning With Audit

Only a `ManageUsers` holder MAY create or edit a `Customer`, scoped to their
own organization. Every create or edit MUST produce an audit entry per
`commerce-role-taxonomy`'s audit path. Customer creation MUST NOT be
available to self-registration or any non-admin caller.

#### Scenario: Cross-organization customer creation is rejected

- GIVEN a `ManageUsers` holder is authorized only for Organization A
- WHEN they submit a customer creation request targeting Organization B
- THEN the request is denied and no row is created in Organization B

#### Scenario: Customer edit is audited

- GIVEN a `ManageUsers` holder edits an existing customer's fields
- WHEN the edit is persisted
- THEN an audit entry records the actor, organization, target customer, and
  the outcome

### Requirement: Customer Usable Without a Linked Login

A `Customer` MUST be a complete, usable record with zero linked `UserAccount`
rows. Linking a login is optional and independent of customer creation.

#### Scenario: Customer with zero logins remains usable

- GIVEN a customer was created with no linked `UserAccount`
- WHEN the customer is referenced for ordering-access purposes (e.g. as
  `Order.CustomerId` or in the admin customer list)
- THEN the customer resolves normally and its absence of a login does not
  block that usage

### Requirement: Offline Selectability for Sale-Time Selection

An enabled `Customer` created or edited online MUST become selectable on a
POS terminal after the existing cloud-to-local sync channel
(`Commerce.BranchNode`, the same mechanism already used for catalog/pricing)
next replicates, with no network call required at selection time. Offline
customer *creation* on the POS is out of scope; only selection of an already
synced, enabled customer during a sale is covered.

#### Scenario: Customer becomes selectable on POS after sync, no network call needed

- GIVEN a customer was created and enabled online
- WHEN the next `Commerce.BranchNode` sync cycle completes on a terminal
- THEN the terminal can select that customer for a sale afterward with no
  network connectivity and no network call made at selection time

### Requirement: Payment-Instrument Reference Is a Distinct Field from PaymentTerms

`Customer` MAY carry a payment-instrument reference field, separately
named from and independent of `PaymentTerms`. `PaymentTerms` MUST remain
unchanged, free-text, and byte-identical after this capability is added.
The instrument-reference field MUST store a tokenized/PCI-scoped
reference only; it MUST NOT store card data (no PAN, no CVV, no raw card
number) in any form.

#### Scenario: PaymentTerms is unaffected by adding an instrument reference

- GIVEN a `Customer` row has an existing `PaymentTerms` value
- WHEN a payment-instrument reference is added to that customer
- THEN `PaymentTerms` is read back byte-identical to its prior value

#### Scenario: Instrument reference stores no card data

- GIVEN a payment-instrument reference is recorded on a `Customer`
- WHEN that field is inspected
- THEN it contains a tokenized/PCI-scoped reference only, with no PAN,
  CVV, or raw card number present in any form

### Requirement: Branch-Owned Customers

Each customer and its ordering access MUST be owned by exactly one branch,
its home branch. Customer list, read, create, edit, and ordering-access
endpoints MUST operate only on the selected branch's customers. Device
customer sync MUST return only the paired branch's customers.

#### Scenario: Centro cannot see a Ruta 51 customer

- GIVEN a customer owned by "Ruta 51"
- WHEN a staff member with "Centro" selected lists or opens customers
- THEN that customer is neither listed nor returned

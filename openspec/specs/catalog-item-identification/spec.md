# Catalog Item Identification Specification

## Purpose

Define the barcode/SKU identification code carried by `Presentation`,
its uniqueness enforcement, and lookup-by-code, so a scanned or imported
code resolves to exactly one sellable line with no follow-up prompt.

## Requirements

### Requirement: Identification Code Per Presentation

Each `Presentation` MUST be able to carry an identification code
(barcode/SKU). The code MUST be scoped to the `Presentation`, not the
parent `Product`: a scan or lookup MUST resolve directly to one
Presentation without a "which presentation?" follow-up.

#### Scenario: Code resolves to exactly one Presentation

- GIVEN a Product has two Presentations, each with its own identification
  code
- WHEN a lookup is performed with one Presentation's code
- THEN exactly that Presentation is returned, with no ambiguity prompt

### Requirement: Database-Enforced Uniqueness

An identification code MUST be unique across Presentations within an
organization, enforced at the database level (not only in application or
UI code). An attempt to persist a duplicate code MUST fail.

#### Scenario: Duplicate code is rejected at persistence

- GIVEN a Presentation already has identification code "7791234567890"
- WHEN another Presentation in the same organization is saved with the
  same code
- THEN the database rejects the write and no duplicate is persisted

### Requirement: Admin Editing of Identification Codes

An authorized admin MUST be able to view and edit a Presentation's
identification code independent of import.

#### Scenario: Admin edits a Presentation's identification code

- GIVEN a Presentation has no identification code
- WHEN an admin sets one via the catalog screen
- THEN the code persists and is available for lookup afterward

### Requirement: Lookup By Code

The system MUST provide a lookup that, given an identification code,
returns the matching Presentation and its owning Product, or reports no
match.

#### Scenario: Lookup with no match

- GIVEN no Presentation carries the submitted code
- WHEN the lookup is performed
- THEN it reports no match rather than an arbitrary or partial result

### Requirement: Branch-Owned Catalog

Products and presentations MUST be owned by exactly one branch
(organization-persistence, "Branch-Owned Business Data"), and catalog
reads, writes, and lookups by identification code MUST operate only on
the selected branch's catalog. The identification-code uniqueness in
"Database-Enforced Uniqueness" MUST hold within a branch; the same code
MAY exist in two branches of one organization.

#### Scenario: Same barcode in two Vaca Verde branches

- GIVEN "Ruta 51" has a presentation with code "7791234567890"
- WHEN "Centro" saves its own presentation with the same code
- THEN both persist, and a lookup with "Ruta 51" selected returns only
  Ruta 51's presentation

### Requirement: Copying Catalog Between Branches

An administrator (a `business-admin` of the organization, or the system
administrator acting on it) MUST be able to copy catalog items from one
branch of an organization to another branch of the SAME organization,
either the whole catalog or individual products. A copy MUST create new,
independent products and presentations owned by the target branch —
later edits in either branch MUST NOT affect the other. A copy MUST NOT
overwrite or merge into an existing target item: an item whose
identification code already exists in the target branch MUST be skipped
and reported, never duplicated. Staff without administrator rights MUST
be refused, and copying across organizations MUST be refused. Each copy
MUST be audited with the actor, source branch, target branch, and the
number of items copied and skipped.

A copy MUST also carry prices: the source branch's most recently uploaded
price list (the latest one created or imported) is copied into the
target branch as a new price list containing the entries of the copied
presentations only, keeping their effective dates. The target branch's
existing price lists and history MUST NOT be modified; if the target
branch has no default price list, the copied list becomes its default.

#### Scenario: Seeding a new branch from Ruta 51

- GIVEN "Ruta 51" of "Vaca Verde" has 120 products and "Centro" is empty
- WHEN an administrator copies the whole catalog from "Ruta 51" to "Centro"
- THEN "Centro" holds 120 new products owned by "Centro", and renaming one
  in "Centro" leaves Ruta 51's product unchanged
- AND "Centro" holds a copy of Ruta 51's latest price list for those
  products, so their prices resolve in "Centro" right away

#### Scenario: Copying one product that partly exists

- GIVEN a product whose presentation code "7791234567890" already exists in
  "Centro"
- WHEN an administrator copies that product from "Ruta 51" to "Centro"
- THEN the conflicting presentation is skipped and reported, the rest is
  copied, and nothing in "Centro" is overwritten

#### Scenario: A seller cannot copy

- GIVEN a user whose roles lack administrator rights
- WHEN they request a catalog copy between branches
- THEN the request is refused and nothing is copied

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

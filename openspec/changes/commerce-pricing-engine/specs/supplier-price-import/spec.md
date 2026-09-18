# Supplier Price Import Specification

## Purpose

Define the staged, reviewable pipeline for importing supplier Excel price
files: per-supplier saved column mapping, barcode/SKU row matching,
unmatched-row reporting, and mandatory admin review before any price
takes effect. The uploaded file is untrusted input from outside the trust
boundary.

## Requirements

### Requirement: Per-Supplier Saved Column Mapping

The system MUST allow an admin to configure and save a column mapping for
a supplier, associating spreadsheet columns with the fields required to
stage a price entry (identification code, price, and any supplier
reference). A later import for the same supplier MUST reuse its saved
mapping without requiring re-configuration.

#### Scenario: Saved mapping is reused on a later import

- GIVEN a supplier has a saved column mapping
- WHEN a new file is uploaded for that supplier
- THEN the rows are parsed using the saved mapping without prompting for
  column configuration again

#### Scenario: New supplier requires mapping configuration first

- GIVEN a supplier has no saved column mapping
- WHEN an admin attempts to import a file for that supplier
- THEN the system requires column mapping to be configured before parsing

### Requirement: Barcode/SKU Row Matching With Unmatched Reporting

Each parsed row MUST be matched to an existing `Presentation` by its
identification code (barcode/SKU). A row whose code matches no
Presentation MUST be reported as unmatched with its reason and MUST NOT
be guessed at or matched to the nearest candidate.

#### Scenario: Row matches an existing Presentation

- GIVEN a row's code matches a Presentation's identification code
- WHEN the file is parsed
- THEN the row is staged as a proposed price entry for that Presentation

#### Scenario: Row with no matching code is reported, not guessed

- GIVEN a row's code matches no Presentation
- WHEN the file is parsed
- THEN the row appears in the unmatched report with its code and reason,
  and no proposed entry is created for it

### Requirement: Staged Batch Requires Admin Review Before Commit

A parsed import MUST produce a staged batch of proposed price entries that
does not affect live prices. Committing the batch MUST require an
explicit admin approval action. No price MAY become effective as a direct
result of upload or parsing alone.

#### Scenario: Uploaded file changes no live price before review

- GIVEN a file has been uploaded and parsed into a staged batch
- WHEN no admin approval has occurred yet
- THEN no price list entry has become effective from this import

#### Scenario: Admin approval commits the batch

- GIVEN an admin reviews a staged batch and approves it
- WHEN the approval is submitted
- THEN each matched row's proposed entry is persisted through the normal
  price-entry write path and the batch is marked committed

#### Scenario: Admin rejects a staged batch

- GIVEN an admin reviews a staged batch and rejects it
- WHEN the rejection is submitted
- THEN no proposed entry from that batch is persisted and the batch is
  marked rejected

### Requirement: Untrusted File Validation

The system MUST validate an uploaded file's type and structure before
parsing its rows, and MUST reject a malformed or wrong-format file with
per-row or per-file reasons rather than applying a partial result. A
rejected upload MUST leave zero prices changed.

#### Scenario: Malformed file is rejected with reasons

- GIVEN an uploaded file is not a valid spreadsheet or violates the
  supplier's saved mapping structure
- WHEN the file is processed
- THEN the upload is rejected with per-row or per-file reasons and no
  staged batch is committed

#### Scenario: Wrong file type is rejected before parsing

- GIVEN an uploaded file is not the expected Excel format
- WHEN the upload is submitted
- THEN the system rejects it before attempting to parse rows

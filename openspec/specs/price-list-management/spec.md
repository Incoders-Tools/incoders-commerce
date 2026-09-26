# Price List Management Specification

## Purpose

Define the versioned, effective-dated `PriceList` aggregate for
`Presentation` prices: append-only history, organization-scoped
persistence with row-level security, and admin create/edit/history access.
Prices are never a mutable scalar and never destructively overwritten.

## Requirements

### Requirement: Append-Only Effective-Dated Price Entries

The system MUST persist each price as a `PriceListEntry` scoped to one
`Presentation`, carrying an amount and an `EffectiveFrom` date. The system
MUST NOT allow an existing entry's amount or effective date to be mutated
or deleted once persisted. Superseding a price MUST be expressed by adding
a new entry, never by overwriting the prior one.

#### Scenario: New entry supersedes without deleting history

- GIVEN a Presentation has an entry effective from 2026-01-01
- WHEN an admin adds a new entry effective from 2026-06-01
- THEN both entries persist and the prior entry remains readable as history

#### Scenario: Direct mutation of an existing entry is rejected

- GIVEN a persisted `PriceListEntry` exists
- WHEN a request attempts to change its amount or effective date in place
- THEN the request is rejected and the original entry is unchanged

### Requirement: Resolution By Effective Date

The system MUST resolve the price for a `Presentation` on a given date as
the entry with the latest `EffectiveFrom` at or before that date. If no
entry has an `EffectiveFrom` at or before the resolution date, the system
MUST report an explicit absence rather than a zero or null price.

#### Scenario: Resolution picks the latest applicable entry

- GIVEN entries effective 2026-01-01 and 2026-06-01 exist for a Presentation
- WHEN the price is resolved for 2026-07-01
- THEN the 2026-06-01 entry's amount is returned

#### Scenario: No applicable entry is an explicit absence

- GIVEN a Presentation has only an entry effective 2027-01-01
- WHEN the price is resolved for 2026-07-01
- THEN the resolution reports no effective price, not zero

### Requirement: Organization-Scoped Persistence With RLS

The system MUST persist price list rows scoped to exactly one
`OrganizationId`, protected by Postgres row-level security following
`PostgresOrganizationStore`'s convention (`FORCE ROW LEVEL SECURITY`,
`REVOKE ALL FROM PUBLIC` plus explicit `GRANT`,
`NULLIF(current_setting(...))` pooler idiom).

#### Scenario: Second organization cannot read another org's price entries

- GIVEN a price entry exists in Organization A
- WHEN a request scoped to Organization B reads price entries
- THEN Organization A's entry is not returned

### Requirement: Admin Create, Edit, and History Access

An authorized admin MUST be able to create a new price entry for a
Presentation and view its complete effective-dated history independent of
any import. "Edit" is limited to adding a new superseding entry; it MUST
NOT remove or alter prior history.

#### Scenario: Admin views full price history for a Presentation

- GIVEN a Presentation has three entries added over time
- WHEN an admin requests its price history
- THEN all three entries are returned in effective-date order

#### Scenario: Non-admin cannot create a price entry

- GIVEN a caller lacks price-management authorization
- WHEN they attempt to create a price entry
- THEN the request is denied and no entry is persisted

### Requirement: Branch-Owned Price Lists

Price lists, their entries, and their rate component sets MUST be owned by
the selected branch, and each branch MUST have at most one default price
list. A price entry or rate component set MUST reference a price list and
presentation of the same branch. Price reads, history, and resolution
MUST use only the selected branch's lists.

#### Scenario: Ruta 51 and Centro price the same product independently

- GIVEN "Ruta 51" and "Centro" each have their own default price list
- WHEN "Ruta 51" adds a new entry for one of its presentations
- THEN Ruta 51's resolved price changes and Centro's does not

### Requirement: Price History Filterable By Date

The price lists screen MUST let an authorized user review historical
prices of the selected branch by date: choosing a date (or a date range)
MUST show the price each presentation had in effect on that date, using
the same effective-date resolution as "Resolution By Effective Date".
Without a date filter it MUST show the prices in effect now. Filtering
MUST be read-only and MUST NOT alter history.

#### Scenario: Reviewing Ruta 51 prices as of a past date

- GIVEN a presentation in "Ruta 51" cost 1000 from March 1 and 1200 from
  June 1
- WHEN an admin filters the price list by May 15
- THEN the presentation shows 1000, and filtering by today shows 1200

#### Scenario: A range shows every change inside it

- GIVEN the same presentation
- WHEN an admin filters from May 1 to June 30
- THEN both the March 1 entry in effect at the range start and the
  June 1 change are listed in effective-date order

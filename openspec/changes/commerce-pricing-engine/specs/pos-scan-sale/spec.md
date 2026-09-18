# POS Scan Sale Specification

## Purpose

Define the POS scan-to-sell flow: a scanned code resolves to a catalog
item and its current cached price, is composed into a real line-item
sale with a computed total, and works offline against the local cache.
The prior manual-total flow is kept as an explicit, distinguishable
fallback.

## Requirements

### Requirement: Scan Resolves to Item and Current Cached Price

Entering an identification code (via keyboard-wedge scan or manual entry
ending in Enter) MUST resolve, against the local branch cache, to a
product, its presentation, and its current effective price, and MUST
display the item's main data and price on screen. The lookup MUST NOT
require network connectivity.

#### Scenario: Scan displays item and price offline

- GIVEN the branch terminal has no network connection and its local cache
  holds a Presentation's code and current price
- WHEN the code is scanned
- THEN the item's main data and current price are displayed with no
  network call made

#### Scenario: Unknown code produces no item

- GIVEN a scanned code matches nothing in the local cache
- WHEN the scan is submitted
- THEN no item is added and the terminal reports the code as unresolved

### Requirement: Line-Item Sale Composed From Scans

Each resolved scan MUST append a line to the current sale carrying the
product/presentation identity, quantity, and resolved unit price. The
sale total MUST be computed as the sum of its line totals, never a
directly typed number.

#### Scenario: Sale total is computed from scanned lines

- GIVEN two items have been scanned and added as lines with resolved
  prices
- WHEN the sale is committed
- THEN the total equals the sum of the line totals, not a manually
  entered value

### Requirement: Manual-Total Fallback Kept and Distinguishable

The existing manual-total entry path MUST remain available for
miscellaneous or unlisted items lacking a code. A sale (or sale line)
completed via the manual-total path MUST be recorded so it is
distinguishable from a scan-composed, catalog-backed sale (or line); the
two MUST NOT be silently conflated in the sale record.

#### Scenario: Manual-total line is recorded as distinct

- GIVEN a cashier rings up a miscellaneous item with no barcode via the
  manual-total entry
- WHEN the sale is committed
- THEN the sale record marks that line as manual-total, distinct from
  scan-composed catalog lines

#### Scenario: Mixed sale keeps both kinds distinguishable

- GIVEN a sale contains both scanned catalog lines and one manual-total
  line
- WHEN the sale is committed and later reviewed
- THEN each line's origin (catalog-resolved vs. manual-total) remains
  identifiable

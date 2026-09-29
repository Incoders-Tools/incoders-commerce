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

### Requirement: Optional Customer Recorded on Scan-Composed Sales

A scan-composed sale MUST record the customer the cashier selected at the
POS, in the local sale record and in the sale payload sent to the cloud. When
no customer is selected (walk-in) the customer MUST be absent, never a
substituted placeholder. Recording the customer MUST NOT depend on the device
credential being valid or on connectivity.

#### Scenario: Selected customer is recorded on the sale

- GIVEN a cashier has selected a synced customer and scanned two products
- WHEN the scan-composed sale is committed offline
- THEN the local sale record and the queued sale payload both carry that
  customer's identifier

#### Scenario: Walk-in sale carries no customer

- GIVEN the customer selector is left on walk-in
- WHEN the scan-composed sale is committed
- THEN the local sale record and the queued sale payload carry no customer

### Requirement: Percentage Discounts on Lines and on the Whole Sale

The cashier MUST be able to apply a percentage discount to a specific line of
a scan-composed sale, to the whole sale, or both. A percentage MUST be greater
than 0 and at most 100 with at most two decimals; any other value MUST be
refused and change nothing. A line discount MUST apply to that line's total
(unit price times quantity, already rounded). A sale discount MUST apply to the
subtotal AFTER line discounts. Every discount amount MUST be rounded to two
decimals, half away from zero: once per discounted line and once for the sale
discount. The sale total MUST equal the sum of the line totals, minus the line
discount amounts, minus the sale discount amount, and MUST never be negative.
Removing a discount MUST NOT require authorization; adding or changing one MUST
be authorized (see the `branch-discount-pin` capability). A sale with no
discount MUST total exactly as before this requirement existed.

#### Scenario: Line discount reduces only that line

- GIVEN a sale with a line totalling 1000.00 and another totalling 500.00
- WHEN an authorized 10% discount is applied to the first line
- THEN that line's discount amount is 100.00 and the sale total is 1400.00

#### Scenario: Sale discount applies after line discounts

- GIVEN a sale whose only line totals 1000.00 and carries a 10% line discount
  (net 900.00)
- WHEN an authorized 5% sale discount is applied
- THEN the sale discount amount is 45.00 (5% of the 900.00 net subtotal) and
  the total is 855.00

#### Scenario: Discount amounts are rounded half away from zero

- GIVEN a line totalling 10.05
- WHEN an authorized 10% discount is applied
- THEN the discount amount is 1.01 (1.005 rounded away from zero) and the line
  nets 9.04

#### Scenario: Invalid percentage is refused

- GIVEN a discount is requested with 0, a negative value, more than 100 or more
  than two decimals
- WHEN the discount is applied
- THEN it is refused with a message and the sale is unchanged

#### Scenario: Removing a discount needs no authorization

- GIVEN a line carries a discount
- WHEN the cashier removes it
- THEN no PIN is requested and the line returns to its undiscounted total

### Requirement: Discounts Recorded and Synchronized With Their Authorization

A committed scan-composed sale MUST record, locally and in the queued sale
payload: for each line its discount percentage and discount amount; for the
sale its discount percentage and discount amount; and, when any discount was
applied, an authorization marker stating the authorization method (branch
PIN) and the operator who was signed in when it was applied. The line total
recorded for a line MUST remain the undiscounted amount so that older readers
keep their meaning. A sale without discounts MUST carry no discount data and a
payload produced before this requirement (no discount fields) MUST still be
ingested by the cloud. On ingestion the cloud MUST write one audit record per
discounted sale naming the operator, the amounts and the authorization method.
Committing a discounted sale MUST NOT read the device credential, and MUST work
offline.

#### Scenario: Discounted sale is committed offline with its authorization

- GIVEN a sale with a line discount and a sale discount, authorized with the
  branch PIN by the signed-in operator, and no network
- WHEN the sale is committed
- THEN the local record and the queued payload carry the line and sale
  discounts and the branch-PIN authorization marker with that operator

#### Scenario: Cloud audits a discounted sale

- GIVEN a queued discounted sale reaches the cloud
- WHEN it is ingested
- THEN exactly one audit record for that sale exists, and re-delivering the
  same operation writes no second one

#### Scenario: Older payload without discounts still ingests

- GIVEN a sale payload with no discount fields
- WHEN it is ingested
- THEN it is stored as before and no discount audit record is written

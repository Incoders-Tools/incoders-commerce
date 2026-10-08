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

### Requirement: Tender Recorded at the Moment of Sale

Completing a sale at the POS MUST record how the customer paid: exactly one
tender per sale, one of cash, card or QR. The POS only RECORDS the tender: card
and QR are charged on the merchant's own terminal or app, and no payment
provider is integrated. The tender is a point-of-sale record and MUST NOT
extend or alter the order payment method taxonomy (`order-payment-lifecycle`);
it is not an order payment attempt. Tender buttons are the way to complete a
sale, for scan-composed and manual-total sales alike, and completing a sale
without choosing a tender MUST NOT be possible.

For cash the cashier MUST enter the amount received. The amount MUST be at
least the sale total and have at most two decimals; anything else MUST be
refused with a message and the sale MUST NOT be committed. The change MUST be
the amount received minus the total and MUST be shown before the sale is
confirmed. The cashier MUST be able to fill the amount received with the exact
total in one action. Card and QR MUST each need a single explicit confirmation
by the cashier that the charge was completed; they carry no amount received
and no change. After a committed sale the cart MUST be cleared and the result
MUST be shown, including the change for cash.

#### Scenario: Cash with change

- GIVEN a sale totalling 855.00
- WHEN the cashier chooses cash and enters 1000.00 as the amount received
- THEN the change shown is 145.00 and confirming records cash, 1000.00 received
  and 145.00 change

#### Scenario: Exact cash

- GIVEN a sale totalling 855.00
- WHEN the cashier chooses cash and uses the exact-amount action
- THEN the amount received is 855.00, the change is 0.00 and the sale can be
  confirmed

#### Scenario: Insufficient or invalid cash is refused

- GIVEN a sale totalling 855.00
- WHEN the cashier enters 800.00, a non-number, or an amount with more than two
  decimals as the amount received
- THEN the sale cannot be confirmed and nothing is committed

#### Scenario: Card or QR needs one confirmation

- GIVEN a sale totalling 855.00
- WHEN the cashier chooses card (or QR) and confirms the charge was completed
- THEN the sale is committed with that method and no amount received or change

#### Scenario: Cancelling the tender keeps the sale open

- GIVEN a sale and an open tender prompt
- WHEN the cashier cancels
- THEN nothing is committed and the cart is unchanged

#### Scenario: Tender is recorded offline

- GIVEN no network and an invalid device credential
- WHEN a sale is completed with a tender
- THEN the sale and its tender are committed locally, never reading the device
  credential

### Requirement: Tender Recorded and Synchronized With the Sale

A committed sale MUST record its tender, locally and in the queued sale
payload: the method (cash, card or QR) and, for cash only, the amount received
and the change. A sale committed before this requirement (no tender) MUST still
read locally, and a payload with no tender MUST still be ingested by the cloud,
which stores the payload as received. The tender fields are additive: an older
reader ignores them.

#### Scenario: Tender is stored locally and queued

- GIVEN a cash sale of 855.00 with 1000.00 received
- WHEN it is committed
- THEN the local sale record and the queued payload carry cash, 1000.00 and
  145.00

#### Scenario: Card sale carries no cash fields

- GIVEN a card sale
- WHEN it is committed
- THEN the recorded tender is card with no amount received and no change

#### Scenario: Older payload without a tender still ingests

- GIVEN a sale payload with no tender
- WHEN the cloud ingests it
- THEN it is stored as before

#### Scenario: Cloud stores the tender

- GIVEN a queued sale with a tender reaches the cloud
- WHEN it is ingested
- THEN the stored payload carries the tender and re-delivering the same
  operation stores nothing new

### Requirement: Walk-in Customer Label and Customer on Manual Sales

The customer selector MUST label the no-customer entry in Spanish
("Consumidor final"). A manual-total sale MUST record the selected customer in
the local sale record and the queued payload, like a scan-composed sale, and
walk-in MUST carry none.

#### Scenario: Manual sale records the selected customer

- GIVEN a cashier selected a synced customer and completes a manual-total sale
- WHEN the sale is committed
- THEN the local record and queued payload carry that customer

#### Scenario: Walk-in label

- GIVEN the customer selector is at its default
- WHEN the operator opens it
- THEN the first entry reads "Consumidor final"

### Requirement: Sales Require an Open Cash Session

Both sale paths (scan-composed and manual-total) MUST refuse to commit while
the terminal has no open cash session, as defined by `pos-cash-session`, and the
sale screen MUST NOT be usable until a session is open. A committed sale MUST
reference the open session.

#### Scenario: Sale refused without a session

- GIVEN the terminal has no open cash session
- WHEN a sale is committed by either path
- THEN it is refused and nothing is recorded

### Requirement: Sale Number

See [Document numbering](../../../docs/document-numbering.md) for the whole scheme.

Every sale committed by a terminal that knows its branch code and register
number MUST get a human sale number `V{branch}-C{register}-{sequence}` (for
example `V01-C2-125`): `V` is the document type (venta), `{branch}` is the
branch code with at least two digits, `C{register}` is the terminal's register
number (not zero-padded) and `{sequence}` counts that register's sales from 1
without padding. The sequence MUST come from a local counter keyed by (branch,
register) that lives in the SAME local transaction as the sale, taken AFTER the
idempotency check and the open-cash-session check, so the number commits or rolls
back with the sale, an idempotent retry returns the ORIGINAL number without
advancing the counter, and a refused sale never burns one. A missing counter row
MUST be seeded from the highest sequence already stored for that (branch,
register), so a lost row can never repeat a number. Numbering works offline.

A terminal that does not know its register yet (paired before numbering existed
and not refreshed since) MUST still commit the sale, WITHOUT a number; the
number MUST NEVER be assigned or changed afterwards, and sales made before
numbering existed keep no number. The sale record carries the branch code,
register number and sequence as nullable columns, and the queued `sale` payload
carries them as optional trailing fields (see branch-offline-sync, "Payload-Kind
Versioning").

After a sale the POS MUST name it by its number and never by its GUID or by the
local database file: `Venta V01-C1-125 registrada por $X (Efectivo).`, or, while
the number is pending, `Venta registrada por $X (Efectivo, número pendiente).`.
The result line MUST carry a tooltip explaining the composition: `V = Venta ·
01 = Sucursal · C1 = Caja 1 · 125 = número de venta de esta caja`. The terminal
MUST NOT wait for the network to commit a sale: when its identity is still
unknown the sale is committed unnumbered and a background refresh is started so
the next sale is numbered.

#### Scenario: Consecutive sales get consecutive numbers

- GIVEN a terminal of Branch 1 holding register 2
- WHEN it commits two sales
- THEN they are numbered `V01-C2-1` and `V01-C2-2`
- AND the numbers are stored with the sales and carried in the queued payloads

#### Scenario: The counter is per branch and register

- GIVEN a terminal that sold under register 2 and is later paired as register 3
- WHEN it commits a sale
- THEN the sale is `V01-C3-1`
- AND going back to register 2 continues that register's own sequence

#### Scenario: An idempotent retry keeps its number

- GIVEN a sale committed as `V01-C2-1`
- WHEN the same operation is committed again
- THEN the original sale and number are returned and the counter does not advance

#### Scenario: Unknown register means no number

- GIVEN a terminal that does not know its register number
- WHEN it commits a sale
- THEN the sale is committed with no number and no counter is created
- AND the message says the number is pending
- AND once the register is known later sales are numbered and earlier ones stay unnumbered

#### Scenario: A lost counter is seeded from the stored maximum

- GIVEN the counter row of a register is missing and its highest stored sequence is 3
- WHEN a sale is committed
- THEN it is numbered with sequence 4

#### Scenario: The sale message never shows a GUID

- WHEN a sale is committed
- THEN the message shows the sale number (or that it is pending), the total and the tender
- AND it contains neither the sale GUID nor the local database file name

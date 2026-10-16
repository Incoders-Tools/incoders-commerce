# POS Cash Session Specification

## Purpose

Define the cash session ("caja") of a POS terminal: it is opened by a
signed-in operator with an opening cash float, every sale of the terminal
belongs to the open session, and closing it reconciles the cash the drawer
should hold against the cash the operator counted. Everything works offline and
the opened and closed session are synchronized and audited like sales. See
`pos-scan-sale` (a sale needs an open session) and `pos-operator-session`
(operators sign in and switch on the terminal).

## Requirements

### Requirement: Opening a Cash Session

A terminal MUST have at most one open cash session at a time. Opening MUST
require a signed-in operator and an opening cash float (zero or more, with at
most two decimals) and MUST record the operator, the float and the opening
moment. Opening while a session is already open MUST be refused and MUST leave
the existing session untouched. Opening MUST work offline.

#### Scenario: Open a session

- GIVEN no session is open and an operator is signed in
- WHEN the operator opens the cash session with a float of 5000.00
- THEN one open session exists with that operator, that float and the opening
  moment

#### Scenario: A second open session is refused

- GIVEN a session is open on the terminal
- WHEN another open is attempted
- THEN it is refused and the original session, float and operator are unchanged

#### Scenario: Invalid float is refused

- GIVEN no session is open
- WHEN opening is attempted with a negative float or more than two decimals
- THEN it is refused and no session is created

### Requirement: Sales Belong to the Open Session

Every sale committed on the terminal, scan-composed or manual, MUST be linked
to the open session in the same atomic commit that writes the sale and its
outbox record. A sale attempted with no open session MUST be refused with a
typed "no open cash session" result, and nothing MUST be written. Each sale
keeps the operator who made it: switching operators inside an open session MUST
keep the session open and MUST NOT change the operator recorded on sales
already made or on the session. Committing a sale MUST NOT depend on the device
credential (sync-blocked is not sales-blocked).

#### Scenario: Sale is linked to the session

- GIVEN a session is open
- WHEN a sale is committed
- THEN the recorded sale references the open session

#### Scenario: Sale without a session is refused

- GIVEN no session is open
- WHEN a sale is committed
- THEN the result is "no open cash session" and no sale, line or outbox row is
  written

#### Scenario: Switching operator keeps the session

- GIVEN a session opened by operator A and a sale made by A
- WHEN operator B signs in and makes a sale
- THEN both sales belong to the same session, each keeps its own operator, and
  the session still names A as the opener

### Requirement: Closing a Cash Session

Closing MUST compute, from the sales linked to the session and their recorded
tenders: the sale count; the cash kept (per cash sale the amount received minus
the change, which equals the sale total); the card total; the QR total; and the
expected cash, which is the opening float plus the cash kept. Totals use each
sale's final total, discounts included as recorded. The operator MUST enter the
counted cash (zero or more, at most two decimals), and closing MUST record the
counted cash and the difference, counted minus expected (positive is a surplus,
negative a shortage). A closed session MUST NOT accept sales or be closed
again, and closing MUST work offline.

#### Scenario: Expected cash and totals

- GIVEN a session with a float of 5000.00, a cash sale of 855.00 paid with
  1000.00 (change 145.00), a cash sale of 100.00, a card sale of 300.00 and a
  QR sale of 200.00
- WHEN the session is summarized
- THEN the sale count is 4, cash kept is 955.00, card total is 300.00, QR
  total is 200.00, and expected cash is 5955.00

#### Scenario: Difference is recorded

- GIVEN the expected cash is 5955.00
- WHEN the operator closes with a counted cash of 5900.00
- THEN the session is closed and records counted 5900.00 and a difference of
  -55.00

#### Scenario: Closed session accepts nothing

- GIVEN a closed session
- WHEN a sale is committed or the close is attempted again
- THEN both are refused and the recorded close is unchanged

#### Scenario: Session with no sales

- GIVEN an open session with a float of 2000.00 and no sales
- WHEN it is summarized
- THEN the sale count is 0, the totals are 0.00 and the expected cash is
  2000.00

### Requirement: Session Survives Application Restarts

An open session MUST stay open across application restarts and reboots and MUST
resume as the open session, with its sales and float intact. The terminal MUST
NOT auto-close a session.

#### Scenario: Reopening the application

- GIVEN a session is open with sales
- WHEN the application is restarted
- THEN the same session is open and its summary includes the earlier sales

### Requirement: Cash Session Operator Interface

When no session is open, the sale screen MUST NOT be usable and the POS MUST
show a prompt to open the cash (naming the signed-in operator and asking for
the opening float, in Spanish). While a session is open, the header MUST show it
compactly with its opening time, and the navigation bar MUST offer "Cerrar Caja".
Closing MUST show the expected cash, the card total, the QR total, the sale
count, an input for the counted cash and the live difference, and MUST require
an explicit confirmation. After a successful close the POS MUST return to the
open-cash prompt.

#### Scenario: Prompt when no session is open

- GIVEN the operator is signed in and no session is open
- WHEN the sale screen appears
- THEN the open-cash prompt is shown and no sale can be started until it is
  confirmed

#### Scenario: Close dialog shows the difference live

- GIVEN a session whose expected cash is 5955.00
- WHEN the operator types 5900 as the counted cash
- THEN the dialog shows a difference of -55.00 before confirming

### Requirement: Session Synchronized and Audited

Opening and closing a session MUST be queued through the branch outbox in the
same transaction that changes the session, as versioned payloads
(`cash-session.opened` and `cash-session.closed`), and delivered like sales.
The sale payload MUST carry the session id as an additive optional field; a
sale payload without it MUST still be ingested. The cloud MUST store each
payload as received in the sync inbox (deduplicated by operation) and MUST write
one audit row per opened session and one per closed session, the latter
carrying the expected cash, counted cash and difference. Ingestion MUST NOT fail
on a payload it cannot read; it stores it and writes no audit row.

#### Scenario: Open and close are queued

- GIVEN a session is opened and later closed while offline
- WHEN the outbox is read
- THEN it holds one opened and one closed payload for that session

#### Scenario: Cloud audits the close with its difference

- GIVEN a closed-session payload with a difference of -55.00 reaches the cloud
- WHEN it is ingested
- THEN it is stored in the sync inbox and one audit row records the difference,
  and re-delivering the same operation writes nothing new

#### Scenario: Older sale payload still ingests

- GIVEN a sale payload with no session id
- WHEN the cloud ingests it
- THEN it is stored as before

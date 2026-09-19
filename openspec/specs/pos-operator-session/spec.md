# POS Operator Session Specification

## Purpose

Define the lifecycle and authorization contract for operators on POS terminals.

## Requirements

### Requirement: Admin-Only Customer Management Screen Gated by Current Operator Role

The desktop customer-management screen (list/create/edit) MUST be gated by
the role of the terminal's current operator (`CurrentOperator`), using the
same operator-identification mechanism this capability already establishes
— no new authorization concept is introduced. Only an operator holding
`business-admin`/`ManageUsers` MAY reach the screen; a `seller` operator MUST
NOT see or reach it. Unlike sale completion, reaching this screen is an
administrative operation and MAY require the terminal to resolve the current
operator's persisted role, consistent with the connectivity precedent
already established for device pairing and bootstrap.

#### Scenario: Business-admin operator reaches the customer-management screen

- GIVEN the terminal's current operator holds `business-admin`/`ManageUsers`
- WHEN that operator navigates to the customer-management screen
- THEN the screen is shown and customer create/edit/list actions are
  available

#### Scenario: Seller operator cannot reach the customer-management screen

- GIVEN the terminal's current operator holds only the `seller` role
- WHEN that operator attempts to navigate to the customer-management screen
- THEN access is denied and the screen is not shown

#### Scenario: No operator identified denies access to the screen

- GIVEN the terminal has no currently identified operator
- WHEN an attempt is made to open the customer-management screen
- THEN access is denied, consistent with treating an unidentified operator
  as having no elevated role

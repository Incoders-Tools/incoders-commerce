# Delta for POS Installation Identity

## ADDED Requirements

### Requirement: Issued-To User Distinct From Current Operator

The device credential's `issued_to_user_id` MUST continue to record only
the operator who performed pairing, unaffected by any subsequent
operator-session activity on that terminal. Current-operator identity
(who is currently identified as operating the terminal, per
`pos-operator-session`) MUST be tracked separately from
`issued_to_user_id` and MUST NOT overwrite or be reconciled with it.

#### Scenario: Pairing operator remains issued_to_user_id after operator switches

- GIVEN a terminal was paired by Operator A, recording
  `issued_to_user_id = Operator A`
- WHEN Operator B later provisions and logs in as the current operator
  on that same terminal
- THEN `issued_to_user_id` still identifies Operator A, unchanged, and
  Operator B's identity is tracked only as the current operator, not as
  a new value of `issued_to_user_id`

#### Scenario: Current operator identity does not require re-pairing

- GIVEN a terminal has a valid device credential paired by Operator A
- WHEN a different staff member provisions and becomes the current
  operator
- THEN no re-pairing occurs and the existing device credential and
  `issued_to_user_id` are left untouched

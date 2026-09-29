# Branch Discount PIN Specification

## Purpose

Authorize POS discounts with one shared PIN per branch. Administrators set and
rotate it in the web console; the cloud keeps only a slow salted hash and
replicates that hash to the branch terminals paired to it, which verify the PIN
offline. The design leaves room for another authorization source (for example a
card reader) presenting the same authorization later.

## Requirements

### Requirement: Administrators Set and Rotate One Discount PIN per Branch

A branch MUST have at most one discount PIN. It MUST be 4 to 12 digits, digits
only. Setting it for a branch that has none and rotating an existing one are the
same operation and MUST be allowed only to a caller with the branch-settings
permission in the branch's organization, or to a system administrator acting on
that organization. The PIN MUST NOT be readable after it is set: no response,
list, log or audit record may contain it or a reversible form of it. Callers MAY
read only whether a PIN is set and when it was last changed.

#### Scenario: Admin sets the PIN

- GIVEN a business administrator of an organization and a branch of it with no
  PIN
- WHEN the administrator submits a valid PIN for that branch
- THEN the branch reports a PIN set with its change time and the response
  contains no PIN

#### Scenario: Invalid PIN is refused

- GIVEN a PIN shorter than 4 characters or containing a non-digit
- WHEN it is submitted
- THEN the request is refused and any existing PIN is unchanged

#### Scenario: Caller without the permission or from another organization is refused

- GIVEN a cashier, or an administrator of a different organization
- WHEN they try to set or read the status of a branch PIN
- THEN the request is refused (a foreign branch is indistinguishable from a
  missing one)

### Requirement: Only a Slow Salted Hash Is Stored

The cloud MUST store only a PBKDF2-SHA256 hash of the PIN with a random 128-bit
salt, 210 000 iterations and a 256-bit subkey, plus a monotonically increasing
version and the time it was rotated, in a branch-scoped table protected by
row-level security that denies access outside the branch. Rotation MUST write an
audit record naming the acting user and the branch, and never the PIN.

#### Scenario: Rotation is audited and versioned

- GIVEN a branch with a PIN at version 1
- WHEN an administrator rotates it
- THEN the version becomes 2, the salt differs, and one audit record names the
  administrator and the branch without the PIN

### Requirement: The Hash Is Replicated Only to the Branch Paired Terminals

A terminal paired to a branch MUST be able to fetch that branch discount PIN
verifier (hash, salt, algorithm parameters, version) with its device
credential, and MUST NOT be able to obtain another branch's or another
organization's. The terminal MUST cache the verifier locally and verify a
typed PIN against it with no network call.

#### Scenario: Terminal receives its own branch verifier

- GIVEN a terminal paired to a branch that has a PIN
- WHEN it syncs
- THEN it receives that branch verifier and can verify the PIN offline

#### Scenario: No PIN yet

- GIVEN a branch with no PIN
- WHEN a terminal syncs
- THEN it learns that none is set and the POS keeps discounts unavailable

### Requirement: Discount Authorization Prompt With Lockout

Adding or changing a discount at the POS MUST require the branch PIN. After 5
consecutive failed attempts the prompt MUST refuse further attempts for 5
minutes on that terminal, and this lockout MUST survive restarting the POS. A
correct PIN resets the failure count. If the branch has no PIN, discounts MUST
be unavailable with a clear message. The authorization is per discount action;
removing a discount MUST NOT prompt.

#### Scenario: Wrong PIN is refused

- GIVEN a verifier is cached
- WHEN a wrong PIN is entered
- THEN authorization fails and the failure is counted

#### Scenario: Fifth failure locks the prompt

- GIVEN four consecutive failures
- WHEN a fifth wrong PIN is entered
- THEN the prompt is locked for 5 minutes, even a correct PIN is refused during
  that time, and the lock is still in force after the POS restarts

#### Scenario: Lock expires

- GIVEN a lock set 5 minutes ago
- WHEN the correct PIN is entered
- THEN it is accepted and the failure count resets

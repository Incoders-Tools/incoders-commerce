# Deployment profiles

Deployment profiles describe physical topology without changing the commerce domain, synchronization model, or organization/branch boundaries.

## Confirmed profiles

| Profile | Physical topology | Logical rule |
|---|---|---|
| Initial branch | One Windows POS notebook per branch co-locates POS, local administration, and the local node. | The local-node boundary remains explicit. |
| Future scalable branch | Multiple terminals communicate with one branch local node. | Terminals do not own independent business databases. |

The detailed source for the initial profile, backup expectations, and coordinated notebook replacement is [SINGLE_DEVICE_BRANCH_PROFILE.md](../../SINGLE_DEVICE_BRANCH_PROFILE.md).

## Coordinated replacement

A notebook replacement is not an application update. Before enabling the new installation, the existing source requires durable cloud confirmation, reconciliation of synchronizable local state, a verified local backup, a new installation identity, consistent bootstrap, and validation of operational data and peripherals. The previous device is retired and revoked only after validation.

## Pending decisions

- The final physical hosting form of the branch node in the multi-station profile.
- The local storage choice after concurrency, maintenance, backup, migration, and recovery validation.
- Hardware models, protocols, and deployment-specific layouts.

This page does not select technologies or implementation mechanisms.

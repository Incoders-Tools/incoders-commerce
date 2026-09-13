# Architecture decision index

Architecture decisions are versioned in the repository. This index intentionally records no unapproved technology choice.

## Planned ADR topics

| Topic | Why it needs a decision |
|---|---|
| Local persistence | SQLite remains provisional and must be compared with a local server database for the scalable profile. |
| Cloud persistence and tenancy | Organization isolation, cost, recovery, and reporting need an explicit posture. |
| Local-node topology | The initial co-located notebook must evolve safely to multi-station branches. |
| Synchronization authority and conflicts | Shared-data ownership and non-resolvable conflicts are still pending. |
| Identity continuity and recovery | Offline operation, enrollment, replacement, and revocation need a coherent policy. |
| Installation and updates | Signed installation, compatibility, backups, migration, health checks, and recovery require an operational decision. |
| Hardware integrations | Scale, printer, scanner, and cash-drawer models/protocols require field evidence. |

## ADR rule

An ADR records a decision only after it names the problem, alternatives, operational implications, and evidence. Until then, this index and the architecture pages must label the topic as pending or provisional.

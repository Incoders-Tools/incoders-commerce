# Design: Commerce foundation walking skeleton

## Technical Approach

Proposed baseline: .NET 10 LTS, WPF POS/local management, a same-machine Windows Service branch node with SQLite, ASP.NET Core, React/TypeScript, and PostgreSQL ([support](https://dotnet.microsoft.com/en-us/platform/support/policy), [WPF](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/)). WinUI 3 remains viable. Docker Compose is development-only; clients require no Docker ([Docker](https://docs.docker.com/compose)).

## Architecture Decisions

| Decision | Planning choice and rationale | Alternative |
|---|---|---|
| Branch topology | One notebook hosts WPF, peripherals, and a branch node. Only the node opens SQLite; future terminals use its API, never its file. | In-process ownership simplifies deployment but couples UI/node lifecycles more tightly. |
| SQLite | Serialized writer, WAL, `synchronous=FULL`; verify embedded `sqlite3_libversion()` is 3.51.3+ or fixed 3.50.7/3.44.6. WAL is same-host, single-writer persistent state ([SQLite](https://www.sqlite.org/wal.html)). | Rollback journal trades concurrency for fewer WAL concerns. |
| Cloud tenancy | PostgreSQL tenant keys, claim-derived filters, RLS, and non-owner runtime role. RLS defaults to deny without policy ([PostgreSQL](https://www.postgresql.org/docs/current/ddl-rowsecurity.html)); ports preserve later database-per-tenant isolation. | Per-tenant databases add operations. |
| Identity | ASP.NET Core Identity; opaque hashed customer credentials bound to tenant/customer and revoked at cloud origin regardless of branch connectivity. Branches cache encrypted admin verifier/permission snapshots; an ADR defines offline actions/freshness, not a guessed lease. | Cloud-only login violates offline continuity. |
| Management authority | Local/web adapters invoke the same use cases. Versioned shared-master commands originate in either channel; non-commuting edits retain both histories for review, never last-write-wins. | A cloud-only writer breaks parity. |
| Releases | Proposed provenance is `dev`→internal, `staging`→pilot, `main`→stable. Only an authorized protected source/ref may create an immutable tag/release attestation; arbitrary signed/tagged commits do not qualify. Client verifies attestation, publisher signature/hash, and app/sync/schema compatibility or stays operational on its current version. Public assets exclude secrets/tenant data. GitHub immutable releases lock tag/assets and attest commit/assets ([GitHub](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases)); none is configured today. MSIX is conditional: packaged services need Windows 10 2004+ and admin installation ([requirements](https://learn.microsoft.com/en-us/windows/msix/desktop/managing-your-msix-deployment-targetdevices)); otherwise use signed MSI behind the same manifest. |

## Data Flow and Contracts

```text
WPF -> Branch node -> SQLite {effect + outbox atomically}
                         -> Cloud API -> PostgreSQL {inbox + effect atomically} -> ACK
Customer web -> Cloud order/outbox -> Branch inbox/effect -> ACK
```

Acknowledgements follow durable commits. Retries reuse `operationId`; unique inbox keys prevent duplicate effects. Envelopes carry contract/tenant/branch/aggregate versions, actor, correlation, and payload. Branch owns sales/cash/stock; cloud owns online-order origin/customer revocation; shared masters use conflict policy. Orders snapshot Product, Presentation, quantity behavior, units, and commercial context. Offline destinations stay pending without stock/settlement promises.

Cloud requests derive scope from authenticated credentials, not submitted tenant IDs, and apply application checks plus RLS. A second-organization fixture proves non-disclosure. Installation identity survives upgrade; notebook replacement creates a new identity.

## File Changes

| Planned/new paths | Purpose |
|---|---|
| `src/Commerce.Domain`, `src/Commerce.Application` | Domain and shared use cases |
| `src/Commerce.BranchNode`, `src/Commerce.Pos.Windows` | Local node and WPF shell |
| `src/Commerce.Cloud.Api`, `src/Commerce.Web` | Cloud API and web clients |
| `src/Commerce.Updater`, `tests/*`, `deploy/dev/compose.yaml`, `.github/workflows/release.yml` | Upgrade, proof, development, release |

## Testing Strategy

Strict TDD remains enabled. Implementation starts with a failing bootstrap test. Proposed `dotnet test Commerce.sln` and frontend commands do not replace configured `test_command: null` until runnable.

| Layer | RED coverage |
|---|---|
| Unit | scope/permissions, authority conflicts, idempotency, historical catalogue snapshots, compatibility selection |
| Integration | real SQLite/PostgreSQL atomicity, lost ACK, RLS/second-tenant denial, offline revocation freshness, backup/migration |
| E2E | offline sale, pending order, N/N+1 sync, tampered package, quiesced snapshot restore, post-reopen binary rollback/forward repair |

## Threat Matrix

| Native row | Applicability |
|---|---|
| Documentation-like paths | N/A: updater accepts only a manifest-bound package, not generic file classification |
| Git repository selection | N/A: product runs no Git commands |
| Commit state | N/A: no commit automation |
| Push state | N/A: no push automation |
| PR commands | N/A: no PR automation |

The updater stages downloads in a controlled directory and invokes the OS deployment API with typed arguments, never a shell. RED tests reject traversal, wrong publisher/type, tampering, incompatibility, privilege denial, and interruption.

## Migration / Rollout

Cloud deployment and branch upgrades stay separate with N/N+1 compatibility. Upgrade blocks new sales/incoming sync, waits for active work, preserves unacknowledged operations, verifies a SQLite Backup API snapshot ([SQLite backup](https://www.sqlite.org/backup.html)), migrates, then checks health. While writes remain quiesced, recovery may restore that snapshot with prior compatible binaries as the known-good state. After writes reopen, NEVER restore it: either roll binaries back against an explicitly N/N+1-compatible live schema while retaining the current database, or forward-repair compatible code and preserve/replay committed durable operations. A crash journal records the boundary. Promote channels through their proposed source mapping.

## Open Questions

No design blocker. Before implementation, approve these recommendations and policy ADRs for offline administrative scope/freshness, shared-master conflict ownership, signing custody, target Windows floor, and pending-offline orders.

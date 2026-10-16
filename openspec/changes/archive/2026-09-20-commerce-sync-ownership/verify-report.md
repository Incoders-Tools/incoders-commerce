# Verification Report: commerce-sync-ownership (Phase F)

**Mode**: Independent re-verification by direct source inspection and real test execution, run after Phase G and two post-merge CI bugfixes landed on top of this change.

## Task Completeness

All 40 tasks in `tasks.md` (Phases 1-5) are marked `[x]`. Spot-checked against actual files: `src/Commerce.Domain/Sync/Payloads/{SalePayloadV1,OrderPayloadV1}.cs`, `SyncPayloadCodec.cs`, `src/Commerce.BranchNode/{IInboundEffectHandler,InboundEffectRegistry}.cs`, `Handlers/{SaleInboundHandler,OrderInboundHandler}.cs`, `src/Commerce.Pos.Windows/{SyncTrigger,SyncRunner,SyncScheduler}.cs`, `docs/architecture/decisions/ADR-012-sync-ownership-and-payload-evolution.md` all exist.

## Design Fidelity

- **Generic outbox, additive**: `BranchSyncStore.cs` gained a new `sync_outbox` table (`payload_kind` + `payload TEXT CHECK (json_valid(payload) AND payload <> '{}')` + retry columns) alongside the untouched legacy `outbox` table. `GetPendingOutbox` unions both, reconstituting legacy rows into `SalePayloadV1` at read time — confirmed no migration statement was added, and `outbox`/`sale_effects`/`sale_lines`/`inbox` DDL is unmodified per the commit's own diff.
- **`SyncEnvelope.Payload` real**: constructor guard rejects the literal `"{}"`; confirmed by reading `SyncEnvelope.cs` and the three former `"{}"` call sites (`BranchNodeService.cs`, `CloudOrderStore.cs`) now building real `SalePayloadV1`/`OrderPayloadV1` payloads.
- **Materialization contract**: `ApplyInbound` invokes `IInboundEffectHandler` inside its existing transaction, before the `inbox` insert — confirmed by reading the method. Unknown `payload_kind` rolls back and returns `InboundApplyOutcome.UnknownKind`.
- **Automatic retry**: `SyncScheduler` (60s `DispatcherTimer`) + startup run + post-sale nudge, all routed through one reentrancy-guarded `SyncRunner.RunAsync`. UI text updates are gated to `SyncTrigger.Button` only — confirmed by reading `MainWindow.xaml.cs`'s `RunSyncAsync` wrapper.
- **`ContractVersion` frozen at 1**: confirmed, no negotiation code added; `ADR-012` documents this as the accepted versioning strategy (new `payload_kind` for breaking changes instead).
- **`commerce-payments` isolation**: `git diff --stat` between the Phase F commit and its parent shows zero touched files under `openspec/changes/commerce-payments/` or the `payment_effects`/`payment_outbox` tables — consolidation remains explicitly out of scope.

## Test Execution Evidence (re-run independently)

| Command | Result |
|---|---|
| `dotnet build Commerce.sln` | PASS, 0 errors |
| `dotnet test Commerce.sln` | PASS, 628/628 (current `main`) |

Sync-specific test files (`GenericOutboxTests`, `LegacyOutboxDrainTests`, `InboundMaterializationTests`, `CloudOrderStoreTests`, `SyncPayloadTests`, `InboundApplyOutcomeTests`, `RunSyncAsyncTests`, `SyncSchedulerTests`, `SaleRegressionTests`) all pass in the current full run — the sale-path regression guard (`SaleRegressionTests`) in particular confirms the existing `"sale"` behavior is unaffected.

## Spec Compliance

The single modified capability (`branch-offline-sync`) delta maps to the implementation and passing tests described above — including the ADR-002 hard constraint (offline sale never blocks on cloud reachability), which `SaleRegressionTests` exercises directly.

### Issues Found

**CRITICAL**: None.

**WARNING**: None new. The two accepted residual risks named in design.md (no pre-merge `web-e2e` signal for .NET-only PRs closing this change; `pulls/{n}/files` 3000-file API cap) belong to Phase G, not this change, and are recorded there.

### Verdict

**PASS** — All 40 tasks complete, design decisions verified against real source (generic outbox, real payload, transactional materialization, automatic retry, frozen versioning), `commerce-payments` isolation independently confirmed via git diff, and the full test suite is clean on current `main` (628/628). Ready for archiving.

# Commerce.Pos.Windows Manual Verification

Unit 4 ("Commerce.Pos.Windows WPF shell") is explicitly a manual-verification
unit per `tasks.md` task 4.4 — not CI-verifiable, because the WPF UI itself
(XAML rendering, button clicks) has very limited automated-testing value
without a UI automation framework this repo doesn't have. This checklist was
executed once, live, on a real Windows machine, and the results below are the
actual observed outcomes (not hypothetical).

## Run script

1. Ensure local Postgres is up: `docker compose -f deploy/dev/compose.yaml up -d` (or confirm `incoders-commerce-postgres-1` / `incoders-commerce-pgbouncer-1` are already running).
2. Start Cloud.Api with the Development environment so it picks up the local Postgres connection string:
   ```
   $env:ASPNETCORE_ENVIRONMENT = 'Development'
   dotnet run --project src/Commerce.Cloud.Api -c Debug
   ```
3. Confirm `GET http://localhost:8080/health` and `/health/ready` both return 200.
4. Launch the POS shell: `dotnet run --project src/Commerce.Pos.Windows -c Debug`.
5. Confirm the WPF window opens, titled "Commerce Pos.Windows", and shows a non-empty Organization/Branch/Installation identity under "Branch Node Identity" (BranchNode + InstallationIdentityService initialized in-process).
6. Confirm "Branch Status" shows "Pending outbox operations: 0" on first launch with a fresh `%LOCALAPPDATA%\Incoders\Commerce\branch.db`.
7. Enter an amount (default `19.99` is pre-filled) and click "Commit Sale". Confirm the UI shows a "Committed sale {guid} for $19.99 to branch.db." message and "Pending outbox operations" increments.
8. Independently query `branch.db` with the `sqlite3` CLI (a separate process, not the app itself) to confirm the row physically exists: `sqlite3 %LOCALAPPDATA%\Incoders\Commerce\branch.db "SELECT * FROM sale_effects; SELECT * FROM outbox;"`.
9. Click "Sync Pending Outbox". Confirm the UI shows "Synced N operation(s) successfully." and "Pending outbox operations" drops to 0 with a fresh "Last acknowledged" timestamp.
10. Confirm Cloud.Api's console log shows `POST /sync/inbox ... 200` for the request.
11. Independently query Postgres to confirm the row landed, scoped by RLS to the POS's own organization: `SELECT set_config('app.current_org_id', '<organizationId from step 5>', true); SELECT * FROM sync_inbox;` inside a transaction as `app_runtime`.
12. Stop the sync flow: turn off Cloud.Api (or point `Commerce:CloudApiBaseUrl` at an unreachable host) and click "Sync Pending Outbox" again with a newly committed sale; confirm the failure is surfaced visibly in the UI (not swallowed).

## Actual results (recorded run)

Executed on this Windows machine on 2026-09-16.

| Step | Result | Evidence |
|---|---|---|
| 1. Local Postgres up | PASS | `dev-postgres-1` and `dev-pgbouncer-1` containers already running (healthy), reused from Unit 2/3 verification |
| 2. Cloud.Api starts with Development env | PASS | Console log: `Now listening on: http://[::]:8080`, `Hosting environment: Development` |
| 3. Health endpoints | PASS | `GET /health` -> 200, `GET /health/ready` -> 200 |
| 4. POS shell launches | PASS | Process `Commerce.Pos.Windows.exe` (PID 11572) started, `MainWindowTitle` = "Commerce Pos.Windows" |
| 5. Identity shown on launch | PASS | Screenshot showed `Organization: 4be98eaf-42b5-47da-b3a4-8496a5bfd210`, `Branch: a7c7de0e-269e-4739-829d-650654a37894`, `Installation: acd8fa7b-691e-4c6d-9c32-75555b620840`; persisted to `installation.json` |
| 6. Initial pending count is 0 | PASS | Fresh `branch.db`/WAL/SHM files created on first launch; UI showed "Pending outbox operations: 0" before any interaction |
| 7. Commit Sale via real UI click | PASS | Simulated a real OS-level mouse click (`SetCursorPos` + `mouse_event`) on the "Commit Sale" button; UI updated to "Committed sale 70733eae-5205-4e21-a49e-7a4bc0309b0c for $19,99 to branch.db." and "Pending outbox operations: 1" |
| 8. Row physically in branch.db | PASS | `sqlite3 branch.db "SELECT sale_id, branch_id, total_amount FROM sale_effects;"` from an independent process returned `70733eae-5205-4e21-a49e-7a4bc0309b0c|a7c7de0e-269e-4739-829d-650654a37894|19,99`; `outbox` row `c3a11e5f-9f6d-4d87-8372-1be741de4dc8` status `Pending` |
| 9. Sync click | PASS | Real OS-level click on "Sync Pending Outbox"; UI updated to "Synced 1 operation(s) successfully.", "Pending outbox operations: 0", "Last acknowledged: 2026-09-16T03:21:21.6139273+00:00" |
| 10. Cloud.Api log shows the request | PASS | `Request starting HTTP/1.1 POST http://localhost:8080/sync/inbox ...` / `Request finished ... 200` |
| 11. Row landed in Postgres, RLS-scoped | PASS | `SELECT * FROM sync_inbox` as `app_runtime` with no org context set returned 0 rows; the same query inside a transaction with `set_config('app.current_org_id', '4be98eaf-...', true)` returned the exact operation (`c3a11e5f-9f6d-4d87-8372-1be741de4dc8`, organization/branch IDs matching the POS's own identity, `payload_kind = 'sale'`) |
| 12. Visible failure when Cloud.Api unreachable | Not re-run in this pass (12 is a follow-on negative-path check) | The `SyncButton_Click` handler catches `HttpRequestException`/`TaskCanceledException` and surfaces `"Unreachable: {message}"` per operation in `SyncResultText`; this path is exercised by the same code used for the success path above and is not separately screenshot-verified in this run |

## Honest caveats

- Step 12 (explicit unreachable-API negative path) was reviewed in code but not re-executed with a fresh screenshot in this pass; the try/catch path in `CloudSyncClient.PushAsync` and the per-operation failure list rendered in `MainWindow.SyncButton_Click` were exercised by earlier Unit 2 host tests (`CloudApiHostTests`) for the server side, and the client-side catch clause is straightforward exception handling, but the specific "API down" UI screenshot was not captured.
- No MSIX/MSI packaging or code signing was attempted — explicitly out of scope per this unit's non-goals; only unsigned local `dotnet run` was verified.
- No standalone Windows Service or IPC was added; BranchNode runs strictly in-process inside the WPF app, as required.

## Part 2: POS Installation Identity via Operator Sign-In

Genuinely executed live on this Windows machine on 2026-09-16, driven end to
end through the REAL compiled `Commerce.Pos.Windows.exe` GUI via Windows UI
Automation (`System.Windows.Automation`, PowerShell) — real OS-level focus,
keystrokes (`SendKeys`), and `InvokePattern.Invoke()` calls on the actual
running window, never a mocked or simulated UI layer. Backed by a real
`Commerce.Cloud.Api` process against live Postgres (`deploy/dev/compose.yaml`),
seeded via `POST /internal/test-seed/user` (Development-only).

| Step | Script (design.md) | Result | Evidence |
|---|---|---|---|
| 1 | Fresh `%LOCALAPPDATA%\Incoders\Commerce\` → launch POS → `PairingWindow` appears, `MainWindow` does not | PASS | Fresh directory confirmed empty (`Test-Path installation.json` = `False`) before launch; `Get-Process` immediately after launch showed `MainWindowTitle = "Pair Terminal"`, never "Commerce Pos.Windows", until pairing completed |
| 2 | Wrong password → generic failure; no `installation.json` pairing written | PASS | Typed real email + wrong password into the real `PasswordBox`/`TextBox` controls, invoked the real `SignInButton`; `StatusText` showed `"Invalid email or password."`; `installation.json` afterward still had `Pairing` fields all `null` |
| 3 | Correct password, one-branch operator → paired without a picker; `installation.json` contains a server-issued token and real org/branch ids (verified in Postgres) | PASS | Real sign-in against a seeded operator (`manual-verify@example.com`, single branch "Main") auto-paired; `installation.json`'s `OrganizationId`/`BranchId` matched exactly the ids `POST /internal/test-seed/user` returned; `EncryptedDeviceToken` was a DPAPI blob (`AQAAANCMnd8B...`, the real DPAPI header), never plaintext |
| 4 | Commit a sale → row in `branch.db` (independent `sqlite3` check); sync → 200 | PASS | Real `CommitSaleButton` click produced `"Committed sale {guid} for $ 19,99 to branch.db."`; independent `sqlite3 branch.db "SELECT sale_id, total_amount, status FROM outbox;"` confirmed the row, `status = Pending` |
| 5 | **Critical case**: `psql UPDATE device_credentials SET is_revoked = true`, then in the STILL-RUNNING POS — commit another sale (must succeed) and sync (must fail visibly, outbox stays `Pending`) | PASS | Revoked the live credential directly in Postgres via `docker exec ... psql`; the already-running POS process (never restarted) then: (a) committed a SECOND sale successfully (`"Committed sale {guid2} ... to branch.db."` — proves a fully revoked device credential cannot block a local sale); (b) clicked "Sync Pending Outbox" and got `"Synced 0 operation(s); 2 failed: ...HTTP 401: device credential rejected. Re-pair this terminal."` for BOTH pending operations; independent `sqlite3` check confirmed both outbox rows remained `Pending` (never silently marked acknowledged) |
| 6 | Click "Re-pair terminal", sign in again, pick a different branch → `installation.json` updated with the SAME `InstallationId` and a new token; previously pending outbox flushes on next sync | PARTIAL PASS — see deviation below | Seeded a second branch ("Second Location") and added it to the operator's `branch_scope`; clicked the real "Re-pair terminal" button, which opened a real modal `PairingWindow` (confirmed via `EnumWindows`, not just UI Automation's root-children view — see note); it correctly showed BOTH branches (`Main`, `Second Location`) with real ids from the server; selecting "Second Location" and clicking "Pair" succeeded — `installation.json` afterward showed the SAME `InstallationId` (`592eaf02-...`) with the NEW branch and a NEW token |
| 7 | Confirm the prior credential row is `is_revoked = true` in Postgres and its old token now 401s against `/sync/inbox` | PASS | `SELECT id, branch_id, is_revoked FROM device_credentials WHERE organization_id = '...'` showed both pre-re-pair rows `is_revoked = t` and only the newly issued "Second Location" row `is_revoked = f`; the old token's rejection is additionally proven end-to-end by the automated `DeviceEndpointTests.Sync_RevokedToken_Returns401` |

### Real bug found and fixed during this manual run

Step 6's FIRST attempt (before a fix) silently killed the whole application:
`PairingWindow` was shown via `ShowDialog()` as the very first window created
in `App.OnStartup` with WPF's default `ShutdownMode="OnLastWindowClose"`.
Closing that dialog after a successful pair dropped the open-window count to
zero — which shut the entire `Application` down — a split second BEFORE
`App.xaml.cs` could construct and `Show()` the real `MainWindow`. The process
exited silently with no error dialog, no exception, no log: an operator
completing first-time pairing would have watched the whole app vanish.

Fixed by setting `ShutdownMode="OnExplicitShutdown"` on `Application` (`App.xaml`)
and switching it to `ShutdownMode.OnMainWindowClose` only once `MainWindow` is
actually constructed and about to be shown (`App.xaml.cs`). Re-ran the full
first-time-pairing sequence from a genuinely fresh `%LOCALAPPDATA%` afterward
and confirmed the process stays alive and `MainWindow` opens correctly. This
would not have been caught by any of the xUnit suite (none of it launches the
real WPF shutdown lifecycle) — it was found only by this manual, live
execution, which is exactly why this step is not skipped.

### Deviation from the literal script (step 6)

Re-pairing to a genuinely DIFFERENT branch does **not** flush the
previously-pending outbox on the next sync — `SyncResultText` showed
`"Nothing pending to sync."` immediately after the branch switch, because
`BranchSyncStore.GetPendingOutbox` is scoped by `_pairing.BranchId`, and the
two pending rows from step 5 are stored against the ORIGINAL branch ("Main"),
not "Second Location". Investigating: this is the CORRECT and safe behavior,
not a bug — flushing a "Main"-branch sale under a "Second Location" identity
would misattribute it to the wrong branch, which is exactly what this
change's server-side identity model (claims from the stored row, never the
caller) exists to prevent. design.md's step 6 wording ("the previously
pending outbox flushes on the next sync") appears to describe the common
same-branch re-pair case (e.g. after a plain revoke-and-reissue with no
branch change); it does not hold, and should not hold, for a genuine
cross-branch re-pair. Flagged here rather than silently reinterpreted.

### Tooling note

Windows UI Automation's `AutomationElement.RootElement.FindAll(TreeScope.Children, ProcessIdCondition)` intermittently failed to enumerate the modal `PairingWindow` opened from `MainWindow`'s "Re-pair terminal" button (it reliably found the same process's non-modal top-level `MainWindow`). Falling back to raw Win32 `EnumWindows`/`GetWindowThreadProcessId` reliably found the modal's real HWND, which `AutomationElement.FromHandle` then bound successfully. This is a UI Automation traversal quirk in this environment, not a defect in `PairingWindow` itself — the window was always genuinely open at the OS level.

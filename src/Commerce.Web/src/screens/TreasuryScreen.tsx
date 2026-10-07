import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Info } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { PageHeader } from '@/components/data/PageHeader'
import { ApiError } from '@/api/client'
import { listBranchDocumentProfiles } from '@/api/fulfillment'
import {
  listTreasuryAccounts,
  listTreasuryMovements,
  reprocessTreasury,
  setTreasuryAccountActive,
  voidTreasuryMovement,
  type TreasuryAccount,
  type TreasuryMovement,
} from '@/api/treasury'
import { treasuryAccountTypesApi } from '@/api/treasuryAccountTypes'
import type { MasterDataEntry } from '@/api/types'
import { formatIsoDate, formatMoney } from '@/dashboard/format'
import { cn } from '@/lib/utils'
import { countedMovements, isoDay, subtotals, type BranchOption } from '@/treasury/treasuryInput'
import { AccountForm, MovementEditForm, MovementForm } from './TreasuryForms'
import { TreasuryRecurrences } from './TreasuryRecurrences'

type Panel =
  | { kind: 'none' }
  | { kind: 'newAccount' }
  | { kind: 'editAccount' }
  | { kind: 'movement' }
  | { kind: 'editMovement'; movement: TreasuryMovement }

/**
 * The company's treasury: how much money the business has and where. The total, split by account type (the
 * organization's own catalog: cash, banks, credit cards...) and by branch, then every account with its balance. The
 * administration manages the accounts (create, edit, activate/deactivate) and the movements: records money in or out by
 * hand, transfers, and voids or edits a movement. A voided movement stays visible, crossed out with its reason, and
 * counts in no balance; an edit leaves the original voided and its correction in place.
 */
export function TreasuryScreen() {
  const { t } = useTranslation('treasury')
  const [accounts, setAccounts] = useState<TreasuryAccount[]>([])
  const [types, setTypes] = useState<MasterDataEntry[]>([])
  const [branches, setBranches] = useState<BranchOption[]>([])
  const [showInactive, setShowInactive] = useState(false)
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [panel, setPanel] = useState<Panel>({ kind: 'none' })
  const [from, setFrom] = useState(() => {
    const start = new Date()
    start.setDate(1)
    return isoDay(start)
  })
  const [to, setTo] = useState(() => isoDay(new Date()))
  const [movements, setMovements] = useState<TreasuryMovement[]>([])
  const [movementsLoading, setMovementsLoading] = useState(false)
  const [movementsError, setMovementsError] = useState<string | null>(null)
  const [movementsToken, setMovementsToken] = useState(0)
  const [voiding, setVoiding] = useState<TreasuryMovement | null>(null)
  const [voidReason, setVoidReason] = useState('')
  const [actionError, setActionError] = useState<string | null>(null)
  const [status, setStatus] = useState<string | null>(null)
  const [reprocessing, setReprocessing] = useState(false)
  const latestRequest = useRef(0)
  const panelRef = useRef<HTMLDivElement>(null)
  const voidRef = useRef<HTMLDivElement>(null)

  // A form that opens (new or edited account, a movement, its correction) is brought into view with the cursor in its
  // first field: the operator never has to look for where to go on.
  useEffect(() => {
    if (panel.kind === 'none') return
    const container = panelRef.current
    container?.scrollIntoView?.({ behavior: 'smooth', block: 'start' })
    container?.querySelector<HTMLElement>('input:not([type="checkbox"]), select')?.focus({ preventScroll: true })
  }, [panel])

  useEffect(() => {
    if (!voiding) return
    voidRef.current?.scrollIntoView?.({ behavior: 'smooth', block: 'center' })
    voidRef.current?.querySelector<HTMLElement>('input')?.focus({ preventScroll: true })
  }, [voiding])

  const errorText = useCallback(
    (err: unknown) =>
      err instanceof ApiError && err.code
        ? t(`errors.codes.${err.code}`, { defaultValue: err.message })
        : err instanceof ApiError
          ? err.message
          : t('errors.save'),
    [t],
  )

  const loadAccounts = useCallback(async () => {
    setLoading(true)
    setLoadError(null)
    try {
      const result = await listTreasuryAccounts()
      setAccounts(result)
      setSelectedId((current) => current ?? result.find((a) => a.isActive !== false)?.accountId ?? null)
      // The types are seeded with the first look at the treasury: read them after it.
      treasuryAccountTypesApi.list(true).then(
        (list) => setTypes(Array.isArray(list) ? list : []),
        () => setTypes([]),
      )
    } catch {
      setLoadError(t('errors.load'))
    } finally {
      setLoading(false)
    }
  }, [t])

  useEffect(() => {
    void loadAccounts()
    // The branches only feed the "new account" form; without them it offers company-wide accounts.
    listBranchDocumentProfiles().then(
      (profiles) => setBranches(Array.isArray(profiles) ? profiles.map((p) => ({ id: p.branchId, name: p.name })) : []),
      () => setBranches([]),
    )
  }, [loadAccounts])

  const rangeInvalid = Boolean(from && to && from > to)

  useEffect(() => {
    if (!selectedId || rangeInvalid) return
    const request = ++latestRequest.current
    setMovementsLoading(true)
    setMovementsError(null)
    listTreasuryMovements(selectedId, { from, to })
      .then((result) => {
        if (request === latestRequest.current) setMovements(result)
      })
      .catch(() => {
        if (request === latestRequest.current) setMovementsError(t('errors.movements'))
      })
      .finally(() => {
        if (request === latestRequest.current) setMovementsLoading(false)
      })
  }, [selectedId, from, to, rangeInvalid, movementsToken, t])

  const visibleAccounts = useMemo(
    () => accounts.filter((account) => showInactive || account.isActive !== false),
    [accounts, showInactive],
  )
  const activeAccounts = useMemo(() => accounts.filter((account) => account.isActive !== false), [accounts])
  const total = useMemo(() => accounts.reduce((sum, account) => sum + account.balance, 0), [accounts])
  const typeLabel = useCallback((account: TreasuryAccount) => account.accountTypeName ?? t('noType'), [t])
  const byType = useMemo(
    () =>
      subtotals(accounts, (account) => ({
        key: account.accountTypeId ?? '',
        label: typeLabel(account),
      })),
    [accounts, typeLabel],
  )
  const byBranch = useMemo(
    () =>
      subtotals(accounts, (account) => ({
        key: account.branchId ?? '',
        label: account.branchName ?? t('wholeCompany'),
      })),
    [accounts, t],
  )
  const groups = useMemo(() => {
    const byKey = new Map<string, { label: string; accounts: TreasuryAccount[] }>()
    for (const account of visibleAccounts) {
      const key = account.accountTypeId ?? ''
      const entry = byKey.get(key) ?? {
        label: typeLabel(account),
        accounts: [],
      }
      entry.accounts.push(account)
      byKey.set(key, entry)
    }
    return [...byKey.entries()].map(([key, entry]) => ({ key, ...entry }))
  }, [visibleAccounts, typeLabel])

  const refreshAll = async (select?: string) => {
    await loadAccounts()
    if (select) setSelectedId(select)
    setMovementsToken((token) => token + 1)
  }

  const selected = accounts.find((account) => account.accountId === selectedId) ?? null

  const toggleActive = async () => {
    if (!selected) return
    setActionError(null)
    try {
      await setTreasuryAccountActive(selected.accountId, selected.isActive === false)
      setStatus(selected.isActive === false ? t('accounts.activated') : t('accounts.deactivated'))
      await refreshAll()
    } catch (err) {
      setActionError(errorText(err))
    }
  }

  const confirmVoid = async () => {
    if (!voiding) return
    if (voidReason.trim() === '') {
      setActionError(t('void.reasonRequired'))
      return
    }
    try {
      await voidTreasuryMovement(voiding.movementId, voidReason.trim())
      setVoiding(null)
      setVoidReason('')
      setActionError(null)
      setStatus(t('void.done'))
      await refreshAll()
    } catch (err) {
      setActionError(errorText(err))
    }
  }

  const reprocess = async () => {
    setReprocessing(true)
    setActionError(null)
    try {
      const result = await reprocessTreasury()
      setStatus(result.movementsAdded === 0 ? t('reprocess.nothing') : t('reprocess.done', { count: result.movementsAdded }))
      await refreshAll()
    } catch (err) {
      setActionError(errorText(err))
    } finally {
      setReprocessing(false)
    }
  }

  const counted = countedMovements(movements)
  const totalIn = counted.filter((m) => m.direction === 'In').reduce((sum, m) => sum + m.amount, 0)
  const totalOut = counted.filter((m) => m.direction === 'Out').reduce((sum, m) => sum + m.amount, 0)

  return (
    <section className="flex w-full flex-col gap-6">
      <PageHeader
        title={t('title')}
        description={t('description')}
        actions={
          <div className="flex flex-wrap gap-2">
            <Button
              type="button"
              variant="outline"
              onClick={() =>
                setPanel({
                  kind: panel.kind === 'newAccount' ? 'none' : 'newAccount',
                })
              }
            >
              {t('actions.newAccount')}
            </Button>
            <Button
              type="button"
              disabled={activeAccounts.length === 0}
              onClick={() =>
                setPanel({
                  kind: panel.kind === 'movement' ? 'none' : 'movement',
                })
              }
            >
              {t('actions.newMovement')}
            </Button>
          </div>
        }
      />

      <div className="flex items-start gap-2 rounded-md border border-border bg-muted/40 p-3 text-sm text-muted-foreground">
        <Info className="mt-0.5 size-4 shrink-0" aria-hidden />
        <div className="flex flex-col gap-2">
          <p>{t('info')}</p>
          <p>{t('reprocess.hint')}</p>
          <div>
            <Button type="button" variant="outline" size="sm" disabled={reprocessing} onClick={() => void reprocess()}>
              {reprocessing ? t('reprocess.running') : t('reprocess.action')}
            </Button>
          </div>
        </div>
      </div>

      {status && (
        <p role="status" className="text-sm text-muted-foreground">
          {status}
        </p>
      )}

      <div ref={panelRef} className="scroll-mt-4">
        {panel.kind === 'newAccount' && (
          <AccountForm
            types={types}
            branches={branches}
            onCancel={() => setPanel({ kind: 'none' })}
            onSaved={(account) => {
              setPanel({ kind: 'none' })
              setStatus(t('accountForm.created', { name: account.name }))
              void refreshAll(account.accountId)
            }}
          />
        )}
        {panel.kind === 'editAccount' && selected && (
          <AccountForm
            key={selected.accountId}
            account={selected}
            types={types}
            branches={branches}
            onCancel={() => setPanel({ kind: 'none' })}
            onSaved={(account) => {
              setPanel({ kind: 'none' })
              setStatus(t('accountForm.saved', { name: account.name }))
              void refreshAll(account.accountId)
            }}
          />
        )}
        {panel.kind === 'movement' && (
          <MovementForm
            accounts={activeAccounts}
            initialAccountId={selectedId}
            onCancel={() => setPanel({ kind: 'none' })}
            onSaved={(accountId) => {
              setPanel({ kind: 'none' })
              setStatus(t('movementForm.saved'))
              void refreshAll(accountId)
            }}
          />
        )}
        {panel.kind === 'editMovement' && (
          <MovementEditForm
            key={panel.movement.movementId}
            movement={panel.movement}
            accounts={accounts}
            onCancel={() => setPanel({ kind: 'none' })}
            onSaved={() => {
              setPanel({ kind: 'none' })
              setStatus(t('editForm.saved'))
              void refreshAll()
            }}
          />
        )}
      </div>

      {loadError && (
        <div
          role="alert"
          className="flex flex-col items-start gap-2 rounded-md border border-destructive/50 bg-destructive/10 p-3 text-sm text-destructive"
        >
          <p>{loadError}</p>
          <Button type="button" variant="outline" size="sm" onClick={() => void loadAccounts()}>
            {t('retry')}
          </Button>
        </div>
      )}

      {loading ? (
        <p className="text-sm text-muted-foreground">{t('loading')}</p>
      ) : !loadError && accounts.length === 0 ? (
        <p className="text-sm text-muted-foreground">{t('empty')}</p>
      ) : (
        <>
          {/* Where the money is: the business total, by type and by branch. */}
          <div className="grid gap-3 lg:grid-cols-3">
            <div className="flex flex-col gap-1 rounded-lg border border-border bg-card p-4">
              <span className="text-sm text-muted-foreground">{t('summary.total')}</span>
              <span className={cn('text-3xl font-semibold tabular-nums', total < 0 && 'text-destructive')}>
                {formatMoney(total)}
              </span>
              <span className="text-xs text-muted-foreground">{t('summary.totalHint')}</span>
            </div>
            <SubtotalCard title={t('summary.byType')} items={byType} />
            <SubtotalCard title={t('summary.byBranch')} items={byBranch} />
          </div>

          <div className="flex items-center justify-between gap-3">
            <h2 className="text-lg font-semibold">{t('accounts.title')}</h2>
            <label className="flex items-center gap-2 text-sm text-muted-foreground">
              <input type="checkbox" checked={showInactive} onChange={(e) => setShowInactive(e.target.checked)} />
              {t('accounts.showInactive')}
            </label>
          </div>
          {groups.map((group) => (
            <div key={group.key} className="flex flex-col gap-2">
              <h3 className="text-sm font-semibold text-muted-foreground">{group.label}</h3>
              <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
                {group.accounts.map((account) => (
                  <button
                    key={account.accountId}
                    type="button"
                    aria-pressed={account.accountId === selectedId}
                    onClick={() => {
                      setSelectedId(account.accountId)
                      setActionError(null)
                    }}
                    className={cn(
                      'flex flex-col gap-1 rounded-lg border bg-card p-4 text-left transition-colors hover:bg-muted/50',
                      account.accountId === selectedId ? 'border-primary ring-1 ring-primary' : 'border-border',
                      account.isActive === false && 'opacity-60',
                    )}
                  >
                    <span className="flex items-center gap-2 text-sm font-medium">
                      {account.name}
                      {account.isActive === false && (
                        <span className="rounded bg-muted px-1.5 py-0.5 text-xs text-muted-foreground">
                          {t('accounts.inactive')}
                        </span>
                      )}
                    </span>
                    <span className="text-xs text-muted-foreground">{account.branchName ?? t('wholeCompany')}</span>
                    <span className={cn('text-2xl font-semibold tabular-nums', account.balance < 0 && 'text-destructive')}>
                      {formatMoney(account.balance)}
                    </span>
                    <span className="text-xs text-muted-foreground">
                      {t('today', {
                        in: formatMoney(account.todayIn),
                        out: formatMoney(account.todayOut),
                      })}
                    </span>
                    {account.description && <span className="text-xs text-muted-foreground">{account.description}</span>}
                  </button>
                ))}
              </div>
            </div>
          ))}
        </>
      )}

      {!loading && !loadError && <TreasuryRecurrences accounts={accounts} onChanged={() => void refreshAll()} />}

      {selected && (
        <div className="flex flex-col gap-3">
          <div className="flex flex-wrap items-end justify-between gap-3">
            <div className="flex flex-col gap-2">
              <h2 className="text-lg font-semibold">
                {t('movements.title', {
                  account: selected.name,
                  branch: selected.branchName ?? t('wholeCompany'),
                })}
              </h2>
              <div className="flex flex-wrap gap-2">
                <Button type="button" variant="outline" size="sm" onClick={() => setPanel({ kind: 'editAccount' })}>
                  {t('accounts.edit')}
                </Button>
                {(selected.isActive === false || !selected.isAutomatic) && (
                  <Button type="button" variant="outline" size="sm" onClick={() => void toggleActive()}>
                    {selected.isActive === false ? t('accounts.activate') : t('accounts.deactivate')}
                  </Button>
                )}
              </div>
            </div>
            <div className="flex flex-wrap items-end gap-3">
              <div className="flex flex-col gap-1">
                <Label htmlFor="treasuryFrom">{t('movements.from')}</Label>
                <Input id="treasuryFrom" type="date" value={from} onChange={(event) => setFrom(event.target.value)} />
              </div>
              <div className="flex flex-col gap-1">
                <Label htmlFor="treasuryTo">{t('movements.to')}</Label>
                <Input id="treasuryTo" type="date" value={to} onChange={(event) => setTo(event.target.value)} />
              </div>
            </div>
          </div>

          {actionError && !voiding && (
            <p role="alert" className="text-sm text-destructive">
              {actionError}
            </p>
          )}

          {voiding && (
            <div ref={voidRef} className="flex flex-col gap-2 rounded-lg border border-destructive/50 bg-destructive/5 p-3">
              <p className="text-sm">
                {t('void.question', {
                  concept: voiding.concept,
                  amount: formatMoney(voiding.amount),
                })}
              </p>
              {voiding.transferId && <p className="text-xs text-muted-foreground">{t('void.transferNote')}</p>}
              <Label htmlFor="treasuryVoidReason">{t('void.reason')}</Label>
              <Input
                id="treasuryVoidReason"
                value={voidReason}
                maxLength={200}
                onChange={(event) => setVoidReason(event.target.value)}
              />
              {actionError && (
                <p role="alert" className="text-sm text-destructive">
                  {actionError}
                </p>
              )}
              <div className="flex gap-2">
                <Button type="button" variant="destructive" onClick={() => void confirmVoid()}>
                  {t('void.confirm')}
                </Button>
                <Button
                  type="button"
                  variant="outline"
                  onClick={() => {
                    setVoiding(null)
                    setActionError(null)
                  }}
                >
                  {t('cancel')}
                </Button>
              </div>
            </div>
          )}

          {rangeInvalid || movementsError ? (
            <p role="alert" className="text-sm text-destructive">
              {rangeInvalid ? t('errors.range') : movementsError}
            </p>
          ) : movementsLoading ? (
            <p className="text-sm text-muted-foreground">{t('loading')}</p>
          ) : movements.length === 0 ? (
            <p className="text-sm text-muted-foreground">{t('movements.empty')}</p>
          ) : (
            <>
              <p className="text-sm text-muted-foreground">
                {t('movements.totals', {
                  in: formatMoney(totalIn),
                  out: formatMoney(totalOut),
                  net: formatMoney(totalIn - totalOut),
                })}
              </p>
              <div className="overflow-x-auto rounded-lg border border-border">
                <table className="w-full text-sm">
                  <thead className="bg-muted/50 text-left text-xs text-muted-foreground">
                    <tr>
                      <th className="px-3 py-2 font-medium">{t('movements.columns.date')}</th>
                      <th className="px-3 py-2 font-medium">{t('movements.columns.kind')}</th>
                      <th className="px-3 py-2 font-medium">{t('movements.columns.concept')}</th>
                      <th className="px-3 py-2 font-medium">{t('movements.columns.customer')}</th>
                      <th className="px-3 py-2 text-right font-medium">{t('movements.columns.in')}</th>
                      <th className="px-3 py-2 text-right font-medium">{t('movements.columns.out')}</th>
                      <th className="px-3 py-2">
                        <span className="sr-only">{t('movements.columns.actions')}</span>
                      </th>
                    </tr>
                  </thead>
                  <tbody>
                    {movements.map((movement) => (
                      <tr
                        key={movement.movementId}
                        className={cn(
                          'border-t border-border',
                          (movement.reversed || movement.voided) && 'text-muted-foreground',
                        )}
                      >
                        <td className="px-3 py-2 whitespace-nowrap">{formatIsoDate(movement.businessDate)}</td>
                        <td className="px-3 py-2 whitespace-nowrap">
                          {t(`movementKinds.${movement.kind}`, {
                            defaultValue: movement.kind,
                          })}
                          {movement.reversed && <span className="ml-1 text-xs">({t('movements.reversed')})</span>}
                          {movement.voided && (
                            <span className="ml-1 text-xs font-medium text-destructive">({t('movements.voided')})</span>
                          )}
                          {movement.recurrenceId && (
                            <span className="ml-1 rounded bg-muted px-1.5 py-0.5 text-xs text-muted-foreground">{t('movements.recurring')}</span>
                          )}
                        </td>
                        <td className="px-3 py-2">
                          <span className={cn(movement.voided && 'line-through')}>{movement.concept}</span>
                          {movement.documentReference && (
                            <span className="ml-1 text-xs text-muted-foreground">· {movement.documentReference}</span>
                          )}
                          {movement.voided && movement.voidReason && (
                            <span className="block text-xs">
                              {t('movements.voidReason', {
                                reason: movement.voidReason,
                              })}
                            </span>
                          )}
                          {movement.correctsMovementId && <span className="block text-xs">{t('movements.correction')}</span>}
                        </td>
                        <td className="px-3 py-2">{movement.customerName ?? '—'}</td>
                        <td className={cn('px-3 py-2 text-right tabular-nums', movement.voided && 'line-through')}>
                          {movement.direction === 'In' ? formatMoney(movement.amount) : ''}
                        </td>
                        <td className={cn('px-3 py-2 text-right tabular-nums', movement.voided && 'line-through')}>
                          {movement.direction === 'Out' ? formatMoney(movement.amount) : ''}
                        </td>
                        <td className="px-3 py-2 text-right whitespace-nowrap">
                          {(movement.canEdit || movement.canReclassify) && (
                            <Button
                              type="button"
                              variant="outline"
                              size="sm"
                              className="mr-1"
                              onClick={() => setPanel({ kind: 'editMovement', movement })}
                            >
                              {movement.canReclassify ? t('movements.changeAccount') : t('movements.edit')}
                            </Button>
                          )}
                          {movement.canVoid && (
                            <Button
                              type="button"
                              variant="outline"
                              size="sm"
                              onClick={() => {
                                setVoiding(movement)
                                setVoidReason('')
                                setActionError(null)
                              }}
                            >
                              {t('void.action')}
                            </Button>
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </>
          )}
        </div>
      )}
    </section>
  )
}

function SubtotalCard({ title, items }: { title: string; items: { key: string; label: string; total: number }[] }) {
  return (
    <div className="flex flex-col gap-2 rounded-lg border border-border bg-card p-4">
      <span className="text-sm text-muted-foreground">{title}</span>
      <ul className="flex flex-col gap-1 text-sm">
        {items.map((item) => (
          <li key={item.key} className="flex justify-between gap-3">
            <span className="truncate">{item.label}</span>
            <span className={cn('font-medium tabular-nums', item.total < 0 && 'text-destructive')}>
              {formatMoney(item.total)}
            </span>
          </li>
        ))}
      </ul>
    </div>
  )
}

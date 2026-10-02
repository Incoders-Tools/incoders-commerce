import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react'
import { useNavigate, useParams } from 'react-router'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { DataView, type DataViewColumn } from '@/components/data/DataView'
import { ViewSwitch } from '@/components/data/ViewSwitch'
import { useViewPreference } from '@/components/data/useViewPreference'
import { FormPage } from '@/components/layout/FormPage'
import { ApiError } from '@/api/client'
import { getSupplier } from '@/api/suppliers'
import { getStatement, getSummary, reverseMovement } from '@/api/supplierAccount'
import type { AccountStatement, AccountSummary, StatementLine, SupplierRecord } from '@/api/types'
import { formatIsoDate, formatMoney } from '@/dashboard/format'
import { daysAgoIso, todayIso } from '@/lib/isoDate'
import { cn } from '@/lib/utils'
import { MovementForm } from './MovementForm'

/** The statement opens on the last 90 days: enough to see recent invoices and payments without a long scroll. */
const DEFAULT_RANGE_DAYS = 90

const AGING_KEYS = ['d0_30', 'd31_60', 'd61_90', 'd90plus'] as const

const isPhone = () => typeof window.matchMedia === 'function' && window.matchMedia('(max-width: 767px)').matches

/**
 * Supplier current account (`/app/suppliers/:id/account`): summary cards
 * (balance, overdue, not yet due), the aging of what is overdue, and the
 * statement for a date range with opening, running and closing balance.
 * Registering a movement opens a full-page form; a movement is never edited
 * or deleted, only reversed (the original stays visible as "Anulado").
 *
 * Sign convention, worded for the operator: a positive balance is what the
 * business owes the supplier ("Le debemos"); a negative one is credit in the
 * business's favour ("Saldo a favor").
 */
export function SupplierAccountScreen() {
  const { t } = useTranslation('supplierAccount')
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const [supplier, setSupplier] = useState<SupplierRecord | null>(null)
  const [summary, setSummary] = useState<AccountSummary | null>(null)
  const [statement, setStatement] = useState<AccountStatement | null>(null)
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [range, setRange] = useState(() => ({ from: daysAgoIso(DEFAULT_RANGE_DAYS), to: todayIso() }))
  const [draftRange, setDraftRange] = useState(range)
  const [rangeError, setRangeError] = useState<string | null>(null)
  const [registering, setRegistering] = useState(false)
  const [reversing, setReversing] = useState<StatementLine | null>(null)
  const [view, setView] = useViewPreference('supplierAccount', isPhone() ? 'cards' : 'table')
  /** Only the latest request may write state. */
  const latestRequest = useRef(0)

  const refresh = useCallback(async () => {
    const request = ++latestRequest.current
    setLoading(true)
    setLoadError(null)
    try {
      const [supplierRecord, summaryResult, statementResult] = await Promise.all([
        getSupplier(id),
        getSummary(id),
        getStatement(id, range),
      ])
      if (request === latestRequest.current) {
        setSupplier(supplierRecord)
        setSummary(summaryResult)
        setStatement(statementResult)
      }
    } catch (err) {
      if (request === latestRequest.current) {
        setLoadError(
          err instanceof ApiError && err.status === 404 ? t('errors.supplierNotFound') : t('errors.unexpectedLoad'),
        )
      }
    } finally {
      if (request === latestRequest.current) setLoading(false)
    }
  }, [t, id, range])

  useEffect(() => {
    void refresh()
  }, [refresh])

  const backToSuppliers = () => navigate('/app/suppliers')

  const applyRange = (event: FormEvent) => {
    event.preventDefault()
    if (draftRange.from > draftRange.to) {
      setRangeError(t('errors.rangeInvalid'))
      return
    }
    setRangeError(null)
    setRange(draftRange)
  }

  if (registering && supplier) {
    return (
      <MovementForm
        supplierId={supplier.id}
        supplierName={supplier.displayName}
        paymentTermsDays={supplier.paymentTermsDays}
        onCancel={() => setRegistering(false)}
        onSaved={() => {
          setRegistering(false)
          void refresh()
        }}
      />
    )
  }

  const columns: DataViewColumn<StatementLine>[] = [
    {
      key: 'concept',
      header: t('statement.columns.concept'),
      cell: (line) => {
        const reversal = line.reversedByMovementId
          ? statement?.movements.find((candidate) => candidate.id === line.reversedByMovementId)
          : undefined
        return (
          <span className="flex flex-col gap-1">
            <span className={cn(line.reversed && 'text-muted-foreground line-through')}>{line.concept}</span>
            {line.reversed && (
              <span className="flex flex-wrap items-center gap-2 text-xs">
                <span className="rounded-full bg-destructive/10 px-2 py-0.5 font-semibold text-destructive">
                  {t('statement.reversedBadge')}
                </span>
                {reversal && (
                  <a className="text-primary underline" href={`#movement-${reversal.id}`}>
                    {t('statement.viewReversal')}
                  </a>
                )}
              </span>
            )}
            {line.reversesMovementId && (
              <span className="text-xs text-muted-foreground">{t('statement.reversalOf')}</span>
            )}
          </span>
        )
      },
    },
    { key: 'date', header: t('statement.columns.date'), cell: (line) => formatIsoDate(line.occurredOn) },
    {
      key: 'kind',
      header: t('statement.columns.kind'),
      cell: (line) => t(`kinds.${line.kind}`),
      hideOnMobile: true,
    },
    {
      key: 'reference',
      header: t('statement.columns.reference'),
      cell: (line) => line.documentReference ?? <span className="text-muted-foreground">{t('statement.columns.noValue')}</span>,
      hideOnMobile: true,
    },
    {
      key: 'dueOn',
      header: t('statement.columns.dueOn'),
      cell: (line) =>
        line.dueOn ? formatIsoDate(line.dueOn) : <span className="text-muted-foreground">{t('statement.columns.noValue')}</span>,
      hideOnMobile: true,
    },
    {
      key: 'amount',
      header: t('statement.columns.amount'),
      cell: (line) => {
        const increases = line.direction === 'Credit'
        return (
          <span className="flex flex-col">
            <span className={cn(line.reversed && 'text-muted-foreground line-through')}>
              {increases ? '+' : '−'}
              {formatMoney(line.amount)}
            </span>
            <span className="text-xs text-muted-foreground">
              {increases ? t('statement.increasesDebt') : t('statement.decreasesDebt')}
            </span>
          </span>
        )
      },
    },
    {
      key: 'balance',
      header: t('statement.columns.balance'),
      headerHint: t('statement.runningHint'),
      cell: (line) => formatMoney(line.runningBalance),
    },
  ]

  return (
    <FormPage
      title={t('title', { name: supplier?.displayName ?? '' })}
      onBack={backToSuppliers}
      backLabel={t('backLabel')}
    >
      <div className="flex flex-col gap-6">
        {loadError && (
          <p role="alert" className="rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive">
            {loadError}
          </p>
        )}

        {loading && !statement && (
          <p role="status" className="text-sm text-muted-foreground">
            {t('loading')}
          </p>
        )}

        {summary && <SummaryCards summary={summary} />}

        {reversing && (
          <ReversePanel
            supplierId={id}
            movement={reversing}
            onCancel={() => setReversing(null)}
            onReversed={() => {
              setReversing(null)
              void refresh()
            }}
          />
        )}

        {statement && (
          <section aria-labelledby="statementTitle" className="flex flex-col gap-4">
            <div className="flex flex-col gap-3 sm:flex-row sm:items-end sm:justify-between">
              <h2 id="statementTitle" className="text-lg font-semibold">
                {t('statement.title')}
              </h2>
              <div className="flex flex-wrap items-center gap-2">
                <ViewSwitch value={view} onChange={setView} />
                <Button onClick={() => setRegistering(true)} disabled={!supplier}>
                  {t('statement.register')}
                </Button>
              </div>
            </div>

            <form className="flex flex-wrap items-end gap-3" onSubmit={applyRange}>
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="rangeFrom">{t('filters.from')}</Label>
                <Input
                  id="rangeFrom"
                  type="date"
                  value={draftRange.from}
                  onChange={(e) => setDraftRange({ ...draftRange, from: e.target.value })}
                />
              </div>
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="rangeTo">{t('filters.to')}</Label>
                <Input
                  id="rangeTo"
                  type="date"
                  value={draftRange.to}
                  onChange={(e) => setDraftRange({ ...draftRange, to: e.target.value })}
                />
              </div>
              <Button type="submit" variant="outline">
                {t('filters.apply')}
              </Button>
            </form>
            {rangeError && (
              <p role="alert" className="text-sm text-destructive">
                {rangeError}
              </p>
            )}

            <dl className="grid grid-cols-1 gap-3 text-sm sm:grid-cols-2">
              <div data-testid="statement-opening" className="flex justify-between gap-3 rounded-md border border-border bg-card px-4 py-2">
                <dt className="text-muted-foreground">{t('statement.opening')}</dt>
                <dd className="font-medium">{formatMoney(statement.openingBalance)}</dd>
              </div>
              <div data-testid="statement-closing" className="flex justify-between gap-3 rounded-md border border-border bg-card px-4 py-2">
                <dt className="text-muted-foreground">{t('statement.closing')}</dt>
                <dd className="font-medium">{formatMoney(statement.closingBalance)}</dd>
              </div>
            </dl>

            <DataView
              items={statement.movements}
              columns={columns}
              getRowKey={(line) => line.id}
              getRowId={(line) => `movement-${line.id}`}
              view={view}
              loading={loading}
              emptyMessage={t('statement.empty')}
              renderActions={(line) =>
                !line.reversed && line.reversesMovementId === null ? (
                  <Button variant="outline" size="sm" onClick={() => setReversing(line)}>
                    {t('statement.reverse')}
                  </Button>
                ) : null
              }
            />
          </section>
        )}
      </div>
    </FormPage>
  )
}

function SummaryCards({ summary }: { summary: AccountSummary }) {
  const { t } = useTranslation('supplierAccount')
  const caption =
    summary.balance > 0 ? t('summary.weOwe') : summary.balance < 0 ? t('summary.inOurFavour') : t('summary.settled')
  return (
    <div className="flex flex-col gap-4">
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
        <Card testId="summary-balance" title={t('summary.balance')} value={formatMoney(Math.abs(summary.balance))} caption={caption} />
        <Card
          testId="summary-overdue"
          title={t('summary.overdue')}
          value={formatMoney(summary.overdue)}
          caption={t('summary.overdueHint')}
          alert={summary.overdue > 0}
        />
        <Card testId="summary-current" title={t('summary.current')} value={formatMoney(summary.current)} caption={t('summary.currentHint')} />
      </div>
      <section aria-label={t('summary.agingTitle')} className="flex flex-col gap-2">
        <h2 className="text-sm font-semibold">{t('summary.agingTitle')}</h2>
        <dl className="grid grid-cols-2 gap-3 lg:grid-cols-4">
          {AGING_KEYS.map((key) => (
            <div key={key} data-testid={`aging-${key}`} className="flex flex-col rounded-md border border-border bg-card px-4 py-2">
              <dt className="text-xs text-muted-foreground">{t(`summary.aging.${key}`)}</dt>
              <dd className="font-medium">{formatMoney(summary.aging[key])}</dd>
            </div>
          ))}
        </dl>
      </section>
    </div>
  )
}

function Card({
  testId,
  title,
  value,
  caption,
  alert = false,
}: {
  testId: string
  title: string
  value: string
  caption: string
  alert?: boolean
}) {
  return (
    <div
      data-testid={testId}
      className={cn('flex flex-col gap-1 rounded-lg border bg-card p-4', alert ? 'border-destructive/50' : 'border-border')}
    >
      <p className="text-sm text-muted-foreground">{title}</p>
      <p className={cn('text-2xl font-semibold', alert && 'text-destructive')}>{value}</p>
      <p className="text-xs text-muted-foreground">{caption}</p>
    </div>
  )
}

/** Inline confirmation before reversing: the optional concept and date go to the compensating movement. */
function ReversePanel({
  supplierId,
  movement,
  onCancel,
  onReversed,
}: {
  supplierId: string
  movement: StatementLine
  onCancel: () => void
  onReversed: () => void
}) {
  const { t } = useTranslation('supplierAccount')
  const [concept, setConcept] = useState('')
  const [occurredOn, setOccurredOn] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  const confirm = async (event: FormEvent) => {
    event.preventDefault()
    setError(null)
    setSubmitting(true)
    try {
      await reverseMovement(supplierId, movement.id, {
        ...(concept.trim() !== '' ? { concept: concept.trim() } : {}),
        ...(occurredOn !== '' ? { occurredOn } : {}),
      })
      onReversed()
    } catch (err) {
      const code = err instanceof ApiError && err.status === 409 ? err.code : undefined
      setError(
        code === 'movement-already-reversed'
          ? t('errors.alreadyReversed')
          : code === 'movement-not-reversible'
            ? t('errors.notReversible')
            : err instanceof ApiError && err.status === 400
              ? err.message
              : t('errors.unexpectedReverse'),
      )
      setSubmitting(false)
    }
  }

  return (
    <form
      role="alertdialog"
      aria-labelledby="reverseTitle"
      aria-describedby="reverseMessage"
      onSubmit={confirm}
      className="flex flex-col gap-4 rounded-lg border border-destructive/40 bg-destructive/5 p-4"
    >
      <h2 id="reverseTitle" className="text-base font-semibold">
        {t('reverse.title')}
      </h2>
      <p id="reverseMessage" className="text-sm">
        {t('reverse.message', { concept: movement.concept, amount: formatMoney(movement.amount) })}
      </p>
      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2">
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="reverseConcept">{t('reverse.concept')}</Label>
          <Input id="reverseConcept" value={concept} onChange={(e) => setConcept(e.target.value)} />
        </div>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="reverseOccurredOn">{t('reverse.occurredOn')}</Label>
          <Input id="reverseOccurredOn" type="date" value={occurredOn} onChange={(e) => setOccurredOn(e.target.value)} />
        </div>
      </div>
      {error && <p className="text-sm text-destructive">{error}</p>}
      <div className="flex gap-2">
        <Button type="submit" variant="destructive" disabled={submitting}>
          {submitting ? t('reverse.confirming') : t('reverse.confirm')}
        </Button>
        <Button type="button" variant="outline" onClick={onCancel} disabled={submitting}>
          {t('reverse.cancel')}
        </Button>
      </div>
    </form>
  )
}

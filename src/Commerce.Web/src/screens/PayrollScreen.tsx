import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { Link, useNavigate } from 'react-router'
import { useTranslation } from 'react-i18next'
import { Button, buttonVariants } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select } from '@/components/ui/select'
import { DataView, type DataViewColumn } from '@/components/data/DataView'
import { PageHeader } from '@/components/data/PageHeader'
import { ApiError } from '@/api/client'
import { PAY_FREQUENCIES, createPayrollRun, listPayrollRuns, type PayFrequency, type PayrollRunSummary } from '@/api/employees'
import { useMissingBranch } from '@/branch/useMissingBranch'
import { formatIsoDate, formatMoney } from '@/dashboard/format'
import { todayIso } from '@/lib/isoDate'

/** The first day of the current month, as `yyyy-MM-dd`. */
const monthStartIso = () => `${todayIso().slice(0, 8)}01`

/**
 * The payroll ("Sueldos") of the selected branch: its runs, newest first, and a new one for a period. A new run is a
 * Draft with a payslip per active employee of the branch (paid that often, when a frequency is chosen); it is reviewed
 * and paid on its own page. INTERNAL payroll: no legal contributions (the accountant keeps doing those).
 */
export function PayrollScreen() {
  const { t } = useTranslation('payroll')
  const navigate = useNavigate()
  const missingBranch = useMissingBranch()
  const [runs, setRuns] = useState<PayrollRunSummary[]>([])
  const [loading, setLoading] = useState(!missingBranch)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [creating, setCreating] = useState(false)
  const [periodFrom, setPeriodFrom] = useState(monthStartIso())
  const [periodTo, setPeriodTo] = useState(todayIso())
  const [frequency, setFrequency] = useState<PayFrequency | ''>('Monthly')
  const [notes, setNotes] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)

  const load = useCallback(async () => {
    if (missingBranch) return
    setLoading(true)
    setLoadError(null)
    try {
      setRuns(await listPayrollRuns())
    } catch {
      setLoadError(t('errors.load'))
    } finally {
      setLoading(false)
    }
  }, [missingBranch, t])

  useEffect(() => {
    void load()
  }, [load])

  const create = async (event: FormEvent) => {
    event.preventDefault()
    if (periodFrom === '' || periodTo === '' || periodFrom > periodTo) return setError(t('new.errors.period'))
    setError(null)
    setSaving(true)
    try {
      const { runId } = await createPayrollRun({
        periodFrom,
        periodTo,
        ...(frequency ? { payFrequency: frequency } : {}),
        ...(notes.trim() ? { notes: notes.trim() } : {}),
      })
      navigate(`/app/payroll/${runId}`)
    } catch (err) {
      setError(err instanceof ApiError && err.code ? t(`errors.codes.${err.code}`, { defaultValue: err.message }) : t('errors.save'))
    } finally {
      setSaving(false)
    }
  }

  if (missingBranch) {
    return (
      <section className="flex w-full flex-col gap-6">
        <PageHeader title={t('title')} description={t('description')} />
        <p className="text-sm text-muted-foreground">{t('chooseBranch')}</p>
      </section>
    )
  }

  const columns: DataViewColumn<PayrollRunSummary>[] = [
    { key: 'number', header: t('columns.number'), cell: (run) => <span className="font-medium tabular-nums">{run.runNumber}</span> },
    {
      key: 'period',
      header: t('columns.period'),
      cell: (run) => t('period', { from: formatIsoDate(run.periodFrom), to: formatIsoDate(run.periodTo) }),
    },
    { key: 'frequency', header: t('columns.frequency'), cell: (run) => (run.payFrequency ? t(`frequency.${run.payFrequency}`) : t('frequency.all')), hideOnMobile: true },
    { key: 'employees', header: t('columns.employees'), cell: (run) => run.employeeCount, hideOnMobile: true },
    { key: 'total', header: t('columns.total'), cell: (run) => <span className="tabular-nums">{formatMoney(run.totalNet)}</span> },
    {
      key: 'status',
      header: t('columns.status'),
      cell: (run) =>
        run.status === 'Paid' ? t('status.paid', { date: run.paidOn ? formatIsoDate(run.paidOn) : '' }) : t('status.draft'),
    },
  ]

  return (
    <section className="flex w-full flex-col gap-6">
      <PageHeader
        title={t('title')}
        description={t('description')}
        actions={
          <Button type="button" onClick={() => setCreating((current) => !current)}>
            {t('actions.new')}
          </Button>
        }
      />

      {creating && (
        <form onSubmit={(event) => void create(event)} className="flex flex-col gap-4 rounded-lg border border-border bg-card p-4" noValidate>
          <h2 className="text-lg font-semibold">{t('new.title')}</h2>
          <p className="text-sm text-muted-foreground">{t('new.hint')}</p>
          <div className="grid gap-4 sm:grid-cols-4">
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="payrollFrom">{t('new.from')}</Label>
              <Input id="payrollFrom" type="date" value={periodFrom} onChange={(e) => setPeriodFrom(e.target.value)} autoFocus />
            </div>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="payrollTo">{t('new.to')}</Label>
              <Input id="payrollTo" type="date" value={periodTo} onChange={(e) => setPeriodTo(e.target.value)} />
            </div>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="payrollFrequency">{t('new.frequency')}</Label>
              <Select id="payrollFrequency" value={frequency} onChange={(e) => setFrequency(e.target.value as PayFrequency | '')}>
                {PAY_FREQUENCIES.map((value) => (
                  <option key={value} value={value}>
                    {t(`frequency.${value}`)}
                  </option>
                ))}
                <option value="">{t('frequency.all')}</option>
              </Select>
            </div>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="payrollNotes">{t('new.notes')}</Label>
              <Input id="payrollNotes" maxLength={500} value={notes} onChange={(e) => setNotes(e.target.value)} />
            </div>
          </div>
          {error && (
            <p role="alert" className="text-sm text-destructive">
              {error}
            </p>
          )}
          <div className="flex gap-2">
            <Button type="submit" disabled={saving}>
              {saving ? t('new.creating') : t('new.create')}
            </Button>
            <Button type="button" variant="outline" onClick={() => setCreating(false)}>
              {t('new.cancel')}
            </Button>
          </div>
        </form>
      )}

      <DataView
        items={runs}
        columns={columns}
        getRowKey={(run) => run.runId}
        view="table"
        loading={loading}
        loadErrorMessage={loadError}
        emptyMessage={t('empty')}
        renderActions={(run) => (
          <Link to={`/app/payroll/${run.runId}`} className={buttonVariants({ variant: 'outline', size: 'sm' })}>
            {run.status === 'Draft' ? t('actions.review') : t('actions.view')}
          </Link>
        )}
      />
    </section>
  )
}

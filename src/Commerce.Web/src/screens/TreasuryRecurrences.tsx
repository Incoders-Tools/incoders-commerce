import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select } from '@/components/ui/select'
import { ApiError } from '@/api/client'
import {
  createTreasuryRecurrence,
  listTreasuryRecurrences,
  setTreasuryRecurrenceActive,
  updateTreasuryRecurrence,
  type RecurrenceEndMode,
  type RecurrenceFrequency,
  type TreasuryAccount,
  type TreasuryRecurrence,
} from '@/api/treasury'
import { formatIsoDate, formatMoney } from '@/dashboard/format'
import { cn } from '@/lib/utils'
import { accountLabel, isoDay, parseAmount, recurrenceDates } from '@/treasury/treasuryInput'

const WEEKDAYS = ['domingo', 'lunes', 'martes', 'miércoles', 'jueves', 'viernes', 'sábado']

/**
 * "Gastos e ingresos recurrentes": fixed expenses (electricity, gas, internet, phone, rent...) and recurring income,
 * recorded automatically on their dates in the chosen account. Each one says how often (every N weeks, months or years
 * from a start date) and when it ends (never, on a date, after a number of times); its amount is updated when it changes
 * and the repetition goes on. A recorded date is an ordinary movement of the account (it can be edited or voided there).
 */
export function TreasuryRecurrences({
  accounts,
  onChanged,
}: {
  accounts: readonly TreasuryAccount[]
  /** A recurrence recorded movements: the accounts and their movements reload. */
  onChanged: () => void
}) {
  const { t } = useTranslation('treasury')
  const [items, setItems] = useState<TreasuryRecurrence[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [editing, setEditing] = useState<TreasuryRecurrence | 'new' | null>(null)
  const [status, setStatus] = useState<string | null>(null)
  const formRef = useRef<HTMLDivElement>(null)

  const load = useCallback(async () => {
    setLoading(true)
    try {
      setItems(await listTreasuryRecurrences())
      setError(null)
    } catch {
      setError(t('recurrences.errors.load'))
    } finally {
      setLoading(false)
    }
  }, [t])

  useEffect(() => {
    void load()
  }, [load])

  useEffect(() => {
    if (editing === null) return
    formRef.current?.scrollIntoView?.({ behavior: 'smooth', block: 'start' })
    formRef.current?.querySelector<HTMLElement>('select, input')?.focus({ preventScroll: true })
  }, [editing])

  const toggle = async (item: TreasuryRecurrence) => {
    try {
      await setTreasuryRecurrenceActive(item.recurrenceId, !item.isActive)
      setStatus(item.isActive ? t('recurrences.paused', { concept: item.concept }) : t('recurrences.resumed', { concept: item.concept }))
      await load()
      onChanged()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : t('errors.save'))
    }
  }

  return (
    <section className="flex flex-col gap-3" aria-labelledby="treasuryRecurrencesTitle">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h2 id="treasuryRecurrencesTitle" className="text-lg font-semibold">
            {t('recurrences.title')}
          </h2>
          <p className="text-sm text-muted-foreground">{t('recurrences.description')}</p>
        </div>
        <Button type="button" variant="outline" disabled={accounts.length === 0} onClick={() => setEditing(editing === 'new' ? null : 'new')}>
          {t('recurrences.new')}
        </Button>
      </div>

      {status && (
        <p role="status" className="text-sm text-muted-foreground">
          {status}
        </p>
      )}
      {error && (
        <p role="alert" className="text-sm text-destructive">
          {error}
        </p>
      )}

      {editing !== null && (
        <div ref={formRef} className="scroll-mt-4">
          <RecurrenceForm
            key={editing === 'new' ? 'new' : editing.recurrenceId}
            recurrence={editing === 'new' ? null : editing}
            accounts={accounts}
            onCancel={() => setEditing(null)}
            onSaved={async (message) => {
              setEditing(null)
              setStatus(message)
              await load()
              onChanged()
            }}
          />
        </div>
      )}

      {loading ? (
        <p className="text-sm text-muted-foreground">{t('loading')}</p>
      ) : items.length === 0 ? (
        <p className="text-sm text-muted-foreground">{t('recurrences.empty')}</p>
      ) : (
        <div className="overflow-x-auto rounded-lg border border-border">
          <table className="w-full text-sm">
            <thead className="bg-muted/50 text-left text-xs text-muted-foreground">
              <tr>
                <th className="px-3 py-2 font-medium">{t('recurrences.columns.concept')}</th>
                <th className="px-3 py-2 font-medium">{t('recurrences.columns.when')}</th>
                <th className="px-3 py-2 font-medium">{t('recurrences.columns.account')}</th>
                <th className="px-3 py-2 text-right font-medium">{t('recurrences.columns.amount')}</th>
                <th className="px-3 py-2 font-medium">{t('recurrences.columns.next')}</th>
                <th className="px-3 py-2">
                  <span className="sr-only">{t('movements.columns.actions')}</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {items.map((item) => (
                <tr key={item.recurrenceId} className={cn('border-t border-border', !item.isActive && 'text-muted-foreground')}>
                  <td className="px-3 py-2">
                    <span className="font-medium">{item.concept}</span>
                    <span className="block text-xs text-muted-foreground">
                      {item.direction === 'Out' ? t('recurrences.out') : t('recurrences.in')}
                      {' · '}
                      {t('recurrences.recorded', { count: item.occurrencesRecorded })}
                    </span>
                  </td>
                  <td className="px-3 py-2">
                    <ScheduleText recurrence={item} />
                  </td>
                  <td className="px-3 py-2">{item.accountName}</td>
                  <td className="px-3 py-2 text-right tabular-nums">
                    {item.direction === 'Out' ? '−' : '+'}
                    {formatMoney(item.amount)}
                  </td>
                  <td className="px-3 py-2 whitespace-nowrap">
                    {!item.isActive ? t('recurrences.pausedBadge') : item.nextOn ? formatIsoDate(item.nextOn) : t('recurrences.ended')}
                  </td>
                  <td className="px-3 py-2 text-right whitespace-nowrap">
                    <Button type="button" variant="outline" size="sm" className="mr-1" aria-label={`${t('recurrences.edit')} ${item.concept}`} onClick={() => setEditing(item)}>
                      {t('recurrences.edit')}
                    </Button>
                    <Button
                      type="button"
                      variant="outline"
                      size="sm"
                      aria-label={`${item.isActive ? t('recurrences.pause') : t('recurrences.resume')} ${item.concept}`}
                      onClick={() => void toggle(item)}
                    >
                      {item.isActive ? t('recurrences.pause') : t('recurrences.resume')}
                    </Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  )
}

/** "Todos los meses, el día 10 · desde el 10/07/2026 · nunca termina". */
function ScheduleText({ recurrence }: { recurrence: Pick<TreasuryRecurrence, 'frequency' | 'interval' | 'startDate' | 'endMode' | 'endDate' | 'maxOccurrences'> }) {
  const { t } = useTranslation('treasury')
  const [year, month, day] = recurrence.startDate.split('-').map(Number)
  const weekday = WEEKDAYS[new Date(year, month - 1, day).getDay()]
  const every = t(`recurrences.every.${recurrence.frequency}`, { count: recurrence.interval, day, weekday, date: `${day}/${month}` })
  const end =
    recurrence.endMode === 'OnDate' && recurrence.endDate
      ? t('recurrences.endsOn', { date: formatIsoDate(recurrence.endDate) })
      : recurrence.endMode === 'AfterCount'
        ? t('recurrences.endsAfter', { count: recurrence.maxOccurrences ?? 0 })
        : t('recurrences.endsNever')
  return (
    <span>
      {every}
      <span className="block text-xs text-muted-foreground">
        {t('recurrences.since', { date: formatIsoDate(recurrence.startDate) })} · {end}
      </span>
    </span>
  )
}

function RecurrenceForm({
  recurrence,
  accounts,
  onCancel,
  onSaved,
}: {
  recurrence: TreasuryRecurrence | null
  accounts: readonly TreasuryAccount[]
  onCancel: () => void
  onSaved: (message: string) => Promise<void>
}) {
  const { t } = useTranslation('treasury')
  const today = isoDay(new Date())
  const active = accounts.filter((a) => a.isActive !== false || a.accountId === recurrence?.accountId)
  const [direction, setDirection] = useState<'In' | 'Out'>(recurrence?.direction ?? 'Out')
  const [accountId, setAccountId] = useState(recurrence?.accountId ?? active[0]?.accountId ?? '')
  const [amount, setAmount] = useState(recurrence ? String(recurrence.amount).replace('.', ',') : '')
  const [concept, setConcept] = useState(recurrence?.concept ?? '')
  const [reference, setReference] = useState(recurrence?.documentReference ?? '')
  const [frequency, setFrequency] = useState<RecurrenceFrequency>(recurrence?.frequency ?? 'Monthly')
  const [intervalText, setIntervalText] = useState(String(recurrence?.interval ?? 1))
  const [startDate, setStartDate] = useState(recurrence?.startDate ?? today)
  const [endMode, setEndMode] = useState<RecurrenceEndMode>(recurrence?.endMode ?? 'Never')
  const [endDate, setEndDate] = useState(recurrence?.endDate ?? '')
  const [maxOccurrences, setMaxOccurrences] = useState(recurrence?.maxOccurrences ? String(recurrence.maxOccurrences) : '12')
  const [includePast, setIncludePast] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  // Once dates were recorded, the schedule stays: changing it would record the same period twice.
  const scheduleLocked = (recurrence?.occurrencesRecorded ?? 0) > 0
  const intervalValue = Number(intervalText)
  const preview =
    startDate && Number.isInteger(intervalValue) && intervalValue >= 1 && intervalValue <= 24
      ? recurrenceDates(startDate, frequency, intervalValue, 0, 3)
      : []

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    const value = parseAmount(amount)
    if (value === null) return setError(t('movementForm.errors.amount'))
    if (concept.trim() === '') return setError(t('movementForm.errors.concept'))
    if (!Number.isInteger(intervalValue) || intervalValue < 1 || intervalValue > 24) return setError(t('recurrences.errors.interval'))
    if (endMode === 'OnDate' && (endDate === '' || endDate < startDate)) return setError(t('recurrences.errors.endDate'))
    const count = Number(maxOccurrences)
    if (endMode === 'AfterCount' && (!Number.isInteger(count) || count < 1 || count > 1000)) return setError(t('recurrences.errors.count'))
    setError(null)
    setSaving(true)
    try {
      const request = {
        accountId,
        direction,
        amount: value,
        concept: concept.trim(),
        ...(reference.trim() ? { reference: reference.trim() } : {}),
        frequency,
        interval: intervalValue,
        startDate,
        endMode,
        ...(endMode === 'OnDate' ? { endDate } : {}),
        ...(endMode === 'AfterCount' ? { maxOccurrences: count } : {}),
      }
      if (recurrence) {
        await updateTreasuryRecurrence(recurrence.recurrenceId, request)
        await onSaved(t('recurrences.saved', { concept: request.concept }))
      } else {
        await createTreasuryRecurrence({ ...request, includePastDates: includePast })
        await onSaved(t('recurrences.created', { concept: request.concept }))
      }
    } catch (err) {
      setError(
        err instanceof ApiError && err.code
          ? t(`recurrences.errors.codes.${err.code}`, { defaultValue: t(`errors.codes.${err.code}`, { defaultValue: err.message }) })
          : t('errors.save'),
      )
    } finally {
      setSaving(false)
    }
  }

  return (
    <form onSubmit={(event) => void submit(event)} className="flex flex-col gap-4 rounded-lg border border-border bg-card p-4" noValidate>
      <h3 className="text-base font-semibold">{recurrence ? t('recurrences.editTitle') : t('recurrences.newTitle')}</h3>
      <p className="text-sm text-muted-foreground">{t('recurrences.formHint')}</p>
      <div className="grid gap-4 sm:grid-cols-3">
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="recurrenceDirection">{t('movementForm.type')}</Label>
          <Select id="recurrenceDirection" value={direction} onChange={(e) => setDirection(e.target.value as 'In' | 'Out')}>
            <option value="Out">{t('recurrences.out')}</option>
            <option value="In">{t('recurrences.in')}</option>
          </Select>
        </div>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="recurrenceConcept">{t('movementForm.concept')}</Label>
          <Input id="recurrenceConcept" maxLength={200} value={concept} placeholder={t('recurrences.conceptPlaceholder')} onChange={(e) => setConcept(e.target.value)} />
        </div>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="recurrenceAmount">{t('movementForm.amount')}</Label>
          <Input id="recurrenceAmount" inputMode="decimal" value={amount} placeholder="45000" onChange={(e) => setAmount(e.target.value)} />
          {recurrence && <p className="text-xs text-muted-foreground">{t('recurrences.amountHint')}</p>}
        </div>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="recurrenceAccount">{direction === 'Out' ? t('recurrences.accountOut') : t('recurrences.accountIn')}</Label>
          <Select id="recurrenceAccount" value={accountId} onChange={(e) => setAccountId(e.target.value)}>
            {active.map((account) => (
              <option key={account.accountId} value={account.accountId}>
                {accountLabel(account, t('wholeCompany'))}
              </option>
            ))}
          </Select>
        </div>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="recurrenceReference">{t('movementForm.reference')}</Label>
          <Input id="recurrenceReference" maxLength={60} value={reference} placeholder={t('recurrences.referencePlaceholder')} onChange={(e) => setReference(e.target.value)} />
        </div>
      </div>

      <fieldset className="grid gap-4 rounded-md border border-border p-3 sm:grid-cols-3" disabled={scheduleLocked}>
        <legend className="px-1 text-sm font-medium">{t('recurrences.repeat')}</legend>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="recurrenceFrequency">{t('recurrences.frequency')}</Label>
          <Select id="recurrenceFrequency" value={frequency} onChange={(e) => setFrequency(e.target.value as RecurrenceFrequency)}>
            <option value="Monthly">{t('recurrences.frequencies.Monthly')}</option>
            <option value="Weekly">{t('recurrences.frequencies.Weekly')}</option>
            <option value="Yearly">{t('recurrences.frequencies.Yearly')}</option>
          </Select>
        </div>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="recurrenceInterval">{t('recurrences.interval')}</Label>
          <Input id="recurrenceInterval" type="number" min={1} max={24} value={intervalText} onChange={(e) => setIntervalText(e.target.value)} />
        </div>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="recurrenceStart">{t('recurrences.start')}</Label>
          <Input id="recurrenceStart" type="date" value={startDate} onChange={(e) => setStartDate(e.target.value)} />
        </div>
        {scheduleLocked && <p className="text-xs text-muted-foreground sm:col-span-3">{t('recurrences.scheduleLocked')}</p>}
      </fieldset>

      <fieldset className="grid gap-4 rounded-md border border-border p-3 sm:grid-cols-3">
        <legend className="px-1 text-sm font-medium">{t('recurrences.ends')}</legend>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="recurrenceEndMode">{t('recurrences.endMode')}</Label>
          <Select id="recurrenceEndMode" value={endMode} onChange={(e) => setEndMode(e.target.value as RecurrenceEndMode)}>
            <option value="Never">{t('recurrences.endModes.Never')}</option>
            <option value="OnDate">{t('recurrences.endModes.OnDate')}</option>
            <option value="AfterCount">{t('recurrences.endModes.AfterCount')}</option>
          </Select>
        </div>
        {endMode === 'OnDate' && (
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="recurrenceEndDate">{t('recurrences.endDate')}</Label>
            <Input id="recurrenceEndDate" type="date" min={startDate} value={endDate} onChange={(e) => setEndDate(e.target.value)} />
          </div>
        )}
        {endMode === 'AfterCount' && (
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="recurrenceCount">{t('recurrences.count')}</Label>
            <Input id="recurrenceCount" type="number" min={1} max={1000} value={maxOccurrences} onChange={(e) => setMaxOccurrences(e.target.value)} />
          </div>
        )}
      </fieldset>

      {preview.length > 0 && (
        <div className="rounded-md bg-muted/40 p-3 text-sm">
          <ScheduleText
            recurrence={{
              frequency,
              interval: intervalValue,
              startDate,
              endMode,
              endDate: endDate || null,
              maxOccurrences: Number(maxOccurrences) || null,
            }}
          />
          <span className="mt-1 block text-xs text-muted-foreground">
            {t('recurrences.preview', { dates: preview.map(formatIsoDate).join(', ') })}
          </span>
        </div>
      )}

      {!recurrence && startDate < today && (
        <label className="flex items-start gap-2 text-sm">
          <input type="checkbox" className="mt-0.5" checked={includePast} onChange={(e) => setIncludePast(e.target.checked)} />
          <span>
            {t('recurrences.includePast')}
            <span className="block text-xs text-muted-foreground">{t('recurrences.includePastHint')}</span>
          </span>
        </label>
      )}

      {error && (
        <p role="alert" className="text-sm text-destructive">
          {error}
        </p>
      )}
      <div className="flex gap-2">
        <Button type="submit" disabled={saving || accountId === ''}>
          {saving ? t('saving') : recurrence ? t('recurrences.save') : t('recurrences.create')}
        </Button>
        <Button type="button" variant="outline" onClick={onCancel}>
          {t('cancel')}
        </Button>
      </div>
    </form>
  )
}

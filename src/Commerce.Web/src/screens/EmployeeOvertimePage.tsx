import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { ConfirmDialog } from '@/components/layout/ConfirmDialog'
import { FormPage } from '@/components/layout/FormPage'
import { ApiError } from '@/api/client'
import { deleteOvertime, listOvertime, recordOvertime, type EmployeeRecord, type OvertimeRecord } from '@/api/employees'
import { formatIsoDate, formatMoney } from '@/dashboard/format'
import { todayIso } from '@/lib/isoDate'
import { overtimeAmount, proposedMultiplier } from '@/lib/overtime'
import { parseAmount } from '@/treasury/treasuryInput'

const decimalText = (value: number) => String(value).replace('.', ',')

/**
 * employee-lifecycle E2: the overtime of one employee. Recorded when it is worked (hours x the employee's overtime hourly
 * rate x a multiplier: a Sunday proposes x2, any other day x1); the next payroll takes the pending ones by itself. Only a
 * pending record can be deleted (a mistake); one a payroll took is changed in that payroll.
 */
export function EmployeeOvertimePage({ employee, onBack }: { employee: EmployeeRecord; onBack: () => void }) {
  const { t } = useTranslation('employees')
  const rate = employee.overtimeHourlyRate
  const [records, setRecords] = useState<OvertimeRecord[]>([])
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [workDate, setWorkDate] = useState(todayIso())
  const [hours, setHours] = useState('')
  const [multiplier, setMultiplier] = useState(() => decimalText(proposedMultiplier(todayIso())))
  const [multiplierTouched, setMultiplierTouched] = useState(false)
  const [note, setNote] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [deleting, setDeleting] = useState<OvertimeRecord | null>(null)
  const [busy, setBusy] = useState(false)

  const errorText = (err: unknown) =>
    err instanceof ApiError && err.code ? t(`errors.codes.${err.code}`, { defaultValue: err.message }) : t('errors.save')

  const load = useCallback(async () => {
    setLoading(true)
    setLoadError(null)
    try {
      setRecords(await listOvertime(employee.id))
    } catch {
      setLoadError(t('overtime.loadError'))
    } finally {
      setLoading(false)
    }
  }, [employee.id, t])

  useEffect(() => {
    void load()
  }, [load])

  const changeDate = (value: string) => {
    setWorkDate(value)
    // The day proposes the multiplier until the operator chose one.
    if (!multiplierTouched && /^\d{4}-\d{2}-\d{2}$/.test(value)) setMultiplier(decimalText(proposedMultiplier(value)))
  }

  const parsedHours = parseAmount(hours)
  const parsedMultiplier = parseAmount(multiplier)
  const preview =
    rate !== null && parsedHours !== null && parsedMultiplier !== null ? overtimeAmount(parsedHours, rate, parsedMultiplier) : null

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    if (parsedHours === null || parsedHours > 24) return setError(t('overtime.errors.hours'))
    if (parsedMultiplier === null || parsedMultiplier < 1 || parsedMultiplier > 3) return setError(t('overtime.errors.multiplier'))
    if (workDate === '' || workDate > todayIso()) return setError(t('overtime.errors.date'))
    setError(null)
    setSaving(true)
    try {
      await recordOvertime(employee.id, {
        workDate,
        hours: parsedHours,
        multiplier: parsedMultiplier,
        ...(note.trim() ? { note: note.trim() } : {}),
      })
      setHours('')
      setNote('')
      await load()
    } catch (err) {
      setError(errorText(err))
    } finally {
      setSaving(false)
    }
  }

  const remove = async () => {
    if (!deleting) return
    setBusy(true)
    try {
      await deleteOvertime(employee.id, deleting.id)
      setDeleting(null)
      await load()
    } catch (err) {
      setDeleting(null)
      setError(errorText(err))
    } finally {
      setBusy(false)
    }
  }

  const statusText = (record: OvertimeRecord) => t(`overtime.status.${record.status}`, { run: record.runNumber })

  return (
    <FormPage
      title={t('overtime.title', { name: employee.fullName })}
      description={t('overtime.description')}
      onBack={onBack}
      backLabel={t('form.back')}
    >
      <div className="flex flex-col gap-6">
        {rate === null ? (
          <p role="status" className="rounded-md border border-amber-500/40 bg-amber-500/10 px-4 py-3 text-sm">
            {t('overtime.noRate')}
          </p>
        ) : (
          <form className="flex max-w-3xl flex-col gap-4" onSubmit={(event) => void submit(event)} noValidate>
            <p className="text-sm text-muted-foreground">{t('overtime.rate', { rate: formatMoney(rate) })}</p>
            <div className="grid gap-4 sm:grid-cols-3">
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="overtimeDate">{t('overtime.date')}</Label>
                <Input id="overtimeDate" type="date" max={todayIso()} value={workDate} onChange={(e) => changeDate(e.target.value)} />
              </div>
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="overtimeHours">{t('overtime.hours')}</Label>
                <Input id="overtimeHours" inputMode="decimal" placeholder="2,5" value={hours} onChange={(e) => setHours(e.target.value)} autoFocus />
              </div>
              <div className="flex flex-col gap-1.5">
                <Label htmlFor="overtimeMultiplier">{t('overtime.multiplier')}</Label>
                <Input
                  id="overtimeMultiplier"
                  inputMode="decimal"
                  value={multiplier}
                  onChange={(e) => {
                    setMultiplierTouched(true)
                    setMultiplier(e.target.value)
                  }}
                />
              </div>
            </div>
            <p className="text-xs text-muted-foreground">{t('overtime.multiplierHint')}</p>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="overtimeNote">{t('overtime.note')}</Label>
              <Input id="overtimeNote" maxLength={200} placeholder={t('overtime.notePlaceholder')} value={note} onChange={(e) => setNote(e.target.value)} />
            </div>
            {preview !== null && <p className="text-sm font-medium">{t('overtime.preview', { amount: formatMoney(preview) })}</p>}
            {error && (
              <p role="alert" className="text-sm text-destructive">
                {error}
              </p>
            )}
            <div>
              <Button type="submit" disabled={saving}>
                {saving ? t('overtime.saving') : t('overtime.save')}
              </Button>
            </div>
          </form>
        )}

        {loadError ? (
          <p role="alert" className="text-sm text-destructive">
            {loadError}
          </p>
        ) : loading ? null : records.length === 0 ? (
          <p className="rounded-lg border border-dashed border-border px-4 py-6 text-center text-sm text-muted-foreground">{t('overtime.empty')}</p>
        ) : (
          <div className="w-full overflow-x-auto rounded-lg border border-border bg-card">
            <table className="w-full text-left text-sm">
              <thead className="border-b border-border text-xs text-muted-foreground uppercase">
                <tr>
                  <th className="px-4 py-2 font-medium">{t('overtime.columns.date')}</th>
                  <th className="px-4 py-2 text-right font-medium">{t('overtime.columns.hours')}</th>
                  <th className="px-4 py-2 text-right font-medium">{t('overtime.columns.multiplier')}</th>
                  <th className="hidden px-4 py-2 text-right font-medium md:table-cell">{t('overtime.columns.rate')}</th>
                  <th className="px-4 py-2 text-right font-medium">{t('overtime.columns.amount')}</th>
                  <th className="hidden px-4 py-2 font-medium md:table-cell">{t('overtime.columns.note')}</th>
                  <th className="px-4 py-2 font-medium">{t('overtime.columns.status')}</th>
                  <th className="px-4 py-2" />
                </tr>
              </thead>
              <tbody>
                {records.map((record) => (
                  <tr key={record.id} className="border-b border-border last:border-0">
                    <td className="px-4 py-2">{formatIsoDate(record.workDate)}</td>
                    <td className="px-4 py-2 text-right tabular-nums">{decimalText(record.hours)}</td>
                    <td className="px-4 py-2 text-right tabular-nums">×{decimalText(record.multiplier)}</td>
                    <td className="hidden px-4 py-2 text-right tabular-nums md:table-cell">{formatMoney(record.hourlyRate)}</td>
                    <td className="px-4 py-2 text-right tabular-nums">{formatMoney(record.amount)}</td>
                    <td className="hidden px-4 py-2 md:table-cell">{record.note ?? ''}</td>
                    <td className="px-4 py-2">{statusText(record)}</td>
                    <td className="px-4 py-2 text-right">
                      {record.status === 'Pending' && (
                        <Button
                          type="button"
                          variant="outline"
                          size="sm"
                          aria-label={t('overtime.deleteLabel', { date: formatIsoDate(record.workDate) })}
                          onClick={() => setDeleting(record)}
                        >
                          {t('overtime.delete')}
                        </Button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {deleting && (
        <ConfirmDialog
          title={t('overtime.deleteTitle')}
          message={t('overtime.deleteMessage', { date: formatIsoDate(deleting.workDate), hours: decimalText(deleting.hours) })}
          confirmLabel={t('overtime.delete')}
          busyLabel={t('overtime.deleting')}
          busy={busy}
          destructive
          onConfirm={() => void remove()}
          onCancel={() => setDeleting(null)}
        />
      )}
    </FormPage>
  )
}

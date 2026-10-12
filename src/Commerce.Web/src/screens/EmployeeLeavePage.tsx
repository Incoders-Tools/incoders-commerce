import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import { FormPage } from '@/components/layout/FormPage'
import { ApiError } from '@/api/client'
import { createFinalSettlement, getEmployee, setEmployeeActive, type EmployeeRecord } from '@/api/employees'
import { formatIsoDate } from '@/dashboard/format'
import { todayIso } from '@/lib/isoDate'

const MAX_REASON = 500

/**
 * employee-lifecycle E3: "Gestionar baja". An active employee is given the leave with its date and reason; one given the
 * leave shows them and offers to settle its last salary (a payroll draft of it alone, opened in "Sueldos" to review and
 * pay) or to reinstate it (which clears the date and the reason).
 */
export function EmployeeLeavePage({
  employee: initial,
  onBack,
  onReinstated,
}: {
  employee: EmployeeRecord
  onBack: () => void
  onReinstated: (employee: EmployeeRecord) => void
}) {
  const { t } = useTranslation('employees')
  const navigate = useNavigate()
  const [employee, setEmployee] = useState(initial)
  const [date, setDate] = useState(todayIso())
  const [reason, setReason] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const errorText = (err: unknown) =>
    err instanceof ApiError && err.code ? t(`leave.codes.${err.code}`, { defaultValue: err.message }) : t('errors.save')

  const run = async (action: () => Promise<void>) => {
    setError(null)
    setBusy(true)
    try {
      await action()
    } catch (err) {
      setError(errorText(err))
    } finally {
      setBusy(false)
    }
  }

  const giveLeave = (event: FormEvent) => {
    event.preventDefault()
    if (reason.trim() === '') return setError(t('leave.errors.reason'))
    if (date === '' || date > todayIso()) return setError(t('leave.errors.date'))
    void run(async () => {
      await setEmployeeActive(employee.id, false, date, reason.trim())
      setEmployee(await getEmployee(employee.id))
    })
  }

  const settle = () =>
    void run(async () => {
      const { runId } = await createFinalSettlement(employee.id)
      navigate(`/app/payroll/${runId}`)
    })

  const reinstate = () =>
    void run(async () => {
      await setEmployeeActive(employee.id, true)
      onReinstated(employee)
    })

  const alert = error && (
    <p role="alert" className="text-sm text-destructive">
      {error}
    </p>
  )

  return (
    <FormPage title={t('leave.title', { name: employee.fullName })} description={t('leave.description')} onBack={onBack} backLabel={t('form.back')}>
      {employee.isActive ? (
        <form className="flex max-w-xl flex-col gap-4" onSubmit={giveLeave} noValidate>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="leaveDate">{t('leave.date')}</Label>
            <Input id="leaveDate" type="date" max={todayIso()} value={date} onChange={(e) => setDate(e.target.value)} />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="leaveReason">{t('leave.reason')}</Label>
            <Textarea
              id="leaveReason"
              rows={2}
              maxLength={MAX_REASON}
              placeholder={t('leave.reasonPlaceholder')}
              value={reason}
              onChange={(e) => setReason(e.target.value)}
              autoFocus
            />
          </div>
          {alert}
          <div className="flex gap-2">
            <Button type="submit" variant="destructive" disabled={busy}>
              {busy ? t('leave.saving') : t('leave.save')}
            </Button>
            <Button type="button" variant="outline" onClick={onBack}>
              {t('form.cancel')}
            </Button>
          </div>
        </form>
      ) : (
        <div className="flex max-w-2xl flex-col gap-6">
          <p role="status" className="rounded-md border border-border bg-muted px-4 py-3 text-sm">
            {employee.terminationDate ? t('leave.left', { date: formatIsoDate(employee.terminationDate) }) : null}
            {employee.terminationReason ? ` ${t('leave.leftReason', { reason: employee.terminationReason })}` : null}
          </p>
          <section className="flex flex-col gap-2">
            <h2 className="text-base font-semibold">{t('leave.settleTitle')}</h2>
            <p className="text-sm text-muted-foreground">{t('leave.settleHint')}</p>
            <div>
              <Button type="button" disabled={busy} onClick={settle}>
                {busy ? t('leave.settling') : t('leave.settle')}
              </Button>
            </div>
          </section>
          {alert}
          <section className="flex flex-col gap-2">
            <p className="text-sm text-muted-foreground">{t('leave.reinstateHint')}</p>
            <div>
              <Button type="button" variant="outline" disabled={busy} onClick={reinstate}>
                {t('leave.reinstate')}
              </Button>
            </div>
          </section>
        </div>
      )}
    </FormPage>
  )
}

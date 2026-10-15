import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select } from '@/components/ui/select'
import { ConfirmDialog } from '@/components/layout/ConfirmDialog'
import { FormPage } from '@/components/layout/FormPage'
import { ApiError } from '@/api/client'
import {
  correctAdvance,
  giveAdvance,
  listAdvances,
  voidAdvance,
  type EmployeeAdvance,
  type EmployeeRecord,
} from '@/api/employees'
import { listTreasuryAccounts, type TreasuryAccount } from '@/api/treasury'
import { formatIsoDate, formatMoney } from '@/dashboard/format'
import { todayIso } from '@/lib/isoDate'
import { payPeriodStartIso } from '@/lib/payPeriod'
import { cn } from '@/lib/utils'
import { parseAmount } from '@/treasury/treasuryInput'
import { MoneyInput } from '@/components/form/MoneyField'

const ISO_DATE = /^\d{4}-\d{2}-\d{2}$/

/**
 * owner feedback 2026-10-14, A4: the advances of an employee in one place. The form hands one out (money out of a
 * treasury account, a debit on the employee's account; the next payroll deducts it) or corrects one; the history below
 * reads the employee's current pay period by default (owner feedback 2026-10-15) and any range on request. An advance
 * still standing is corrected or voided whole: the server undoes its cash and the employee's account together, and the
 * voided one stays listed, struck through with why.
 */
export function EmployeeAdvancePage({ employee, onBack }: { employee: EmployeeRecord; onBack: () => void }) {
  const { t } = useTranslation('employees')
  const [accounts, setAccounts] = useState<TreasuryAccount[]>([])
  const [defaultAccountId, setDefaultAccountId] = useState('')
  const [accountId, setAccountId] = useState('')
  const [amount, setAmount] = useState('')
  const [date, setDate] = useState(todayIso())
  const [concept, setConcept] = useState('')
  const [reason, setReason] = useState('')
  const [correcting, setCorrecting] = useState<EmployeeAdvance | null>(null)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [status, setStatus] = useState<string | null>(null)

  const [to, setTo] = useState(todayIso())
  // owner feedback 2026-10-15, B5: the employee's current pay period, up to today.
  const [from, setFrom] = useState(() => payPeriodStartIso(employee.payFrequency, todayIso()))
  const [advances, setAdvances] = useState<EmployeeAdvance[]>([])
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [voidingAdvance, setVoidingAdvance] = useState<EmployeeAdvance | null>(null)
  const [voiding, setVoiding] = useState(false)
  // Only the latest read of the history counts: an answer for dates changed since is dropped.
  const latestRead = useRef(0)

  const codeText = useCallback(
    (err: unknown) =>
      err instanceof ApiError && err.code
        ? t(`advance.codes.${err.code}`, {
            name: employee.fullName,
            defaultValue: t(`errors.codes.${err.code}`, { defaultValue: err.message }),
          })
        : t('errors.save'),
    [employee.fullName, t],
  )

  useEffect(() => {
    listTreasuryAccounts().then(
      (list) => {
        const active = list.filter((account) => account.isActive !== false)
        // The drawer of the employee's branch first: the usual place an advance comes from.
        const preferred = active.find((a) => a.branchId === employee.branchId && a.kind === 'Cash')?.accountId ?? active[0]?.accountId ?? ''
        setAccounts(active)
        setDefaultAccountId(preferred)
        setAccountId(preferred)
      },
      () => setAccounts([]),
    )
  }, [employee.branchId])

  const rangeValid = ISO_DATE.test(from) && ISO_DATE.test(to) && from <= to

  const load = useCallback(async () => {
    const read = ++latestRead.current
    if (!rangeValid) return
    setLoading(true)
    setLoadError(null)
    try {
      const list = await listAdvances(employee.id, from, to)
      if (read === latestRead.current) setAdvances(list)
    } catch {
      if (read === latestRead.current) setLoadError(t('advance.errors.load'))
    } finally {
      if (read === latestRead.current) setLoading(false)
    }
  }, [employee.id, from, rangeValid, t, to])

  useEffect(() => {
    void load()
  }, [load])

  const resetForm = () => {
    setCorrecting(null)
    setAmount('')
    setDate(todayIso())
    setConcept('')
    setReason('')
    setAccountId(defaultAccountId)
    setError(null)
  }

  const startCorrecting = (advance: EmployeeAdvance) => {
    setCorrecting(advance)
    setAmount(String(advance.amount).replace('.', ','))
    setDate(advance.date)
    setConcept(advance.concept ?? '')
    setReason('')
    // An account deactivated since stays out of the list: the operator picks another.
    setAccountId(accounts.some((a) => a.accountId === advance.accountId) ? advance.accountId : defaultAccountId)
    setError(null)
    setStatus(null)
    document.getElementById('advanceAmount')?.focus()
  }

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    const value = parseAmount(amount)
    if (value === null) return setError(t('advance.errors.amount'))
    if (accountId === '') return setError(t('advance.errors.account'))
    if (correcting && reason.trim() === '') return setError(t('advance.errors.reason'))
    setError(null)
    setSaving(true)
    try {
      const text = concept.trim() ? { concept: concept.trim() } : {}
      if (correcting) {
        await correctAdvance(employee.id, correcting.advanceId, { amount: value, accountId, date, ...text, reason: reason.trim() })
        setStatus(t('advance.corrected', { date: formatIsoDate(correcting.date), amount: formatMoney(value) }))
      } else {
        await giveAdvance(employee.id, { amount: value, accountId, date, ...text })
        setStatus(t('status.advanced', { name: employee.fullName, amount: formatMoney(value) }))
      }
      resetForm()
      void load()
    } catch (err) {
      setError(codeText(err))
    } finally {
      setSaving(false)
    }
  }

  const confirmVoid = async (why: string) => {
    if (!voidingAdvance) return
    setVoiding(true)
    try {
      await voidAdvance(employee.id, voidingAdvance.advanceId, why)
      setStatus(t('advance.voided', { date: formatIsoDate(voidingAdvance.date), account: voidingAdvance.accountName }))
      setError(null)
      if (correcting?.advanceId === voidingAdvance.advanceId) resetForm()
      void load()
    } catch (err) {
      setError(codeText(err))
    } finally {
      setVoiding(false)
      setVoidingAdvance(null)
    }
  }

  const labelOf = (advance: EmployeeAdvance) => ({ date: formatIsoDate(advance.date), amount: formatMoney(advance.amount) })
  const standingTotal = advances.filter((a) => !a.isVoided).reduce((sum, a) => sum + a.amount, 0)

  return (
    <FormPage title={t('advance.title', { name: employee.fullName })} description={t('advance.description')} onBack={onBack} backLabel={t('form.back')}>
      <div className="flex flex-col gap-6">
        <form className="flex max-w-xl flex-col gap-4" onSubmit={(event) => void submit(event)} noValidate aria-labelledby="advanceFormTitle">
          <div>
            <h2 id="advanceFormTitle" className="text-base font-semibold">
              {correcting ? t('advance.correctTitle', { date: formatIsoDate(correcting.date) }) : t('advance.newTitle')}
            </h2>
            {correcting && <p className="text-sm text-muted-foreground">{t('advance.correctHint', labelOf(correcting))}</p>}
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="advanceAmount">{t('advance.amount')}</Label>
            <MoneyInput id="advanceAmount" value={amount} onChange={setAmount} autoFocus />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="advanceAccount">{t('advance.account')}</Label>
            <Select id="advanceAccount" value={accountId} onChange={(e) => setAccountId(e.target.value)}>
              {accounts.map((account) => (
                <option key={account.accountId} value={account.accountId}>
                  {account.name}
                </option>
              ))}
            </Select>
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="advanceDate">{t('advance.date')}</Label>
            <Input id="advanceDate" type="date" max={todayIso()} value={date} onChange={(e) => setDate(e.target.value)} />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="advanceConcept">{t('advance.concept')}</Label>
            <Input id="advanceConcept" value={concept} onChange={(e) => setConcept(e.target.value)} placeholder={t('advance.conceptPlaceholder')} />
          </div>
          {correcting && (
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="advanceReason">{t('advance.reason')}</Label>
              <Input id="advanceReason" value={reason} maxLength={150} onChange={(e) => setReason(e.target.value)} placeholder={t('advance.reasonPlaceholder')} />
            </div>
          )}
          {error && (
            <p role="alert" className="text-sm text-destructive">
              {error}
            </p>
          )}
          <div className="flex gap-2">
            <Button type="submit" disabled={saving}>
              {saving ? t('form.saving') : correcting ? t('advance.saveCorrection') : t('advance.save')}
            </Button>
            {correcting && (
              <Button type="button" variant="outline" onClick={resetForm}>
                {t('advance.cancelCorrection')}
              </Button>
            )}
          </div>
        </form>

        {status && (
          <p role="status" className="text-sm text-muted-foreground">
            {status}
          </p>
        )}

        <section className="flex flex-col gap-3" aria-labelledby="advanceHistoryTitle">
          <h2 id="advanceHistoryTitle" className="text-base font-semibold">
            {t('advance.history')}
          </h2>
          <div className="flex flex-wrap items-end gap-3">
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="advanceFrom">{t('advance.from')}</Label>
              <Input id="advanceFrom" type="date" max={to} value={from} onChange={(e) => setFrom(e.target.value)} />
            </div>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="advanceTo">{t('advance.to')}</Label>
              <Input id="advanceTo" type="date" min={from} value={to} onChange={(e) => setTo(e.target.value)} />
            </div>
          </div>
          {ISO_DATE.test(from) && ISO_DATE.test(to) && from > to && (
            <p role="alert" className="text-sm text-destructive">
              {t('advance.errors.range')}
            </p>
          )}

          {!rangeValid ? null : loadError ? (
            <p role="alert" className="text-sm text-destructive">
              {loadError}
            </p>
          ) : loading ? null : advances.length === 0 ? (
            <p className="rounded-lg border border-dashed border-border px-4 py-6 text-center text-sm text-muted-foreground">{t('advance.empty')}</p>
          ) : (
            <>
              <div className="w-full overflow-x-auto rounded-lg border border-border bg-card">
                <table className="w-full text-left text-sm" aria-labelledby="advanceHistoryTitle">
                  <thead className="border-b border-border text-xs text-muted-foreground uppercase">
                    <tr>
                      <th className="px-4 py-2 font-medium">{t('advance.columns.date')}</th>
                      <th className="px-4 py-2 text-right font-medium">{t('advance.columns.amount')}</th>
                      <th className="hidden px-4 py-2 font-medium md:table-cell">{t('advance.columns.account')}</th>
                      <th className="hidden px-4 py-2 font-medium md:table-cell">{t('advance.columns.concept')}</th>
                      <th className="px-4 py-2 font-medium">{t('advance.columns.status')}</th>
                      <th className="px-4 py-2 font-medium">
                        <span className="sr-only">{t('advance.columns.actions')}</span>
                      </th>
                    </tr>
                  </thead>
                  <tbody>
                    {advances.map((advance) => (
                      <tr
                        key={advance.advanceId}
                        className={cn('border-b border-border last:border-0', advance.isVoided && 'text-muted-foreground')}
                        aria-current={correcting?.advanceId === advance.advanceId ? 'true' : undefined}
                      >
                        <td className="px-4 py-2">{formatIsoDate(advance.date)}</td>
                        <td className="px-4 py-2 text-right tabular-nums">
                          <span className={cn(advance.isVoided && 'line-through')}>{formatMoney(advance.amount)}</span>
                        </td>
                        <td className="hidden px-4 py-2 md:table-cell">{advance.accountName}</td>
                        <td className="hidden px-4 py-2 md:table-cell">{advance.concept ?? '—'}</td>
                        <td className="px-4 py-2">
                          {advance.isVoided ? (
                            <>
                              <span className="block">{t('advance.status.voided')}</span>
                              {advance.voidReason && <span className="block text-xs">{advance.voidReason}</span>}
                            </>
                          ) : (
                            <>
                              <span className="block">{t('advance.status.active')}</span>
                              {advance.correctsAdvanceId && (
                                <span className="block text-xs text-muted-foreground">{t('advance.status.corrects')}</span>
                              )}
                            </>
                          )}
                        </td>
                        <td className="px-4 py-2">
                          {!advance.isVoided && (
                            <span className="flex gap-2">
                              <Button
                                type="button"
                                variant="outline"
                                size="sm"
                                aria-label={t('advance.correctLabel', labelOf(advance))}
                                onClick={() => startCorrecting(advance)}
                              >
                                {t('advance.correct')}
                              </Button>
                              <Button
                                type="button"
                                variant="outline"
                                size="sm"
                                aria-label={t('advance.voidLabel', labelOf(advance))}
                                onClick={() => setVoidingAdvance(advance)}
                              >
                                {t('advance.void')}
                              </Button>
                            </span>
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              <p className="text-sm text-muted-foreground">{t('advance.total', { amount: formatMoney(standingTotal) })}</p>
            </>
          )}
        </section>
      </div>

      {voidingAdvance && (
        <ConfirmDialog
          title={t('advance.voidTitle')}
          message={t('advance.voidMessage', { ...labelOf(voidingAdvance), account: voidingAdvance.accountName })}
          confirmLabel={t('advance.voidConfirm')}
          busyLabel={t('advance.voiding')}
          busy={voiding}
          destructive
          reason={{ label: t('advance.voidReason'), requiredMessage: t('advance.voidReasonRequired'), maxLength: 150 }}
          onConfirm={(why) => void confirmVoid(why)}
          onCancel={() => setVoidingAdvance(null)}
        />
      )}
    </FormPage>
  )
}

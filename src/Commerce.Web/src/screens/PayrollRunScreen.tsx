import { Fragment, useCallback, useEffect, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { useTranslation } from 'react-i18next'
import { Button, buttonVariants } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select } from '@/components/ui/select'
import { ConfirmDialog } from '@/components/layout/ConfirmDialog'
import { FormPage } from '@/components/layout/FormPage'
import { ApiError } from '@/api/client'
import {
  deletePayrollRun,
  getPayrollRun,
  payPayrollRun,
  removePayslip,
  updatePayslip,
  type PayrollRunDetail,
  type Payslip,
  type PayslipLineKind,
  type PayslipLineSource,
} from '@/api/employees'
import { listTreasuryAccounts, type TreasuryAccount } from '@/api/treasury'
import { formatIsoDate, formatMoney } from '@/dashboard/format'
import { todayIso } from '@/lib/isoDate'
import { cn } from '@/lib/utils'
import { parseAmount } from '@/treasury/treasuryInput'

interface EditableLine {
  key: number
  kind: PayslipLineKind
  source: PayslipLineSource
  concept: string
  amount: string
}

let nextKey = 1

/**
 * One payroll run. A Draft is reviewed employee by employee: its earnings and deductions (the base salary and the
 * pending advances come prepared; overtime, a bonus or another deduction are added by hand), and the discount the owner
 * grants on the goods taken. Paying it, from a treasury account and on a date, settles every employee's account and goods
 * and takes the net out of the treasury, in one step. A Paid run is read-only and its payslips are printed.
 */
export function PayrollRunScreen() {
  const { t } = useTranslation('payroll')
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const [detail, setDetail] = useState<PayrollRunDetail | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [editing, setEditing] = useState<string | null>(null)
  const [paying, setPaying] = useState(false)
  const [confirmPay, setConfirmPay] = useState(false)
  const [discarding, setDiscarding] = useState(false)
  const [accounts, setAccounts] = useState<TreasuryAccount[]>([])
  const [accountId, setAccountId] = useState('')
  const [paidOn, setPaidOn] = useState(todayIso())
  const [busy, setBusy] = useState(false)

  const errorText = useCallback(
    (err: unknown) =>
      err instanceof ApiError && err.code ? t(`errors.codes.${err.code}`, { defaultValue: err.message }) : t('errors.save'),
    [t],
  )

  const load = useCallback(async () => {
    try {
      setDetail(await getPayrollRun(id))
    } catch {
      setError(t('errors.load'))
    }
  }, [id, t])

  useEffect(() => {
    void load()
  }, [load])

  const openPay = () => {
    setPaying(true)
    listTreasuryAccounts().then(
      (list) => {
        const active = list.filter((account) => account.isActive !== false)
        setAccounts(active)
        setAccountId((current) => current || (active.find((a) => a.branchId === detail?.run.branchId && a.kind === 'Cash') ?? active[0])?.accountId || '')
      },
      () => setAccounts([]),
    )
  }

  const pay = async () => {
    setBusy(true)
    setError(null)
    try {
      await payPayrollRun(id, { accountId, paidOn })
      setConfirmPay(false)
      setPaying(false)
      await load()
    } catch (err) {
      setConfirmPay(false)
      setError(errorText(err))
    } finally {
      setBusy(false)
    }
  }

  const discard = async () => {
    setBusy(true)
    try {
      await deletePayrollRun(id)
      navigate('/app/payroll')
    } catch (err) {
      setDiscarding(false)
      setError(errorText(err))
    } finally {
      setBusy(false)
    }
  }

  const back = () => navigate('/app/payroll')
  if (detail === null) {
    return (
      <FormPage title={t('title')} onBack={back} backLabel={t('back')}>
        <p role={error ? 'alert' : 'status'} className={cn('text-sm', error ? 'text-destructive' : 'text-muted-foreground')}>
          {error ?? '…'}
        </p>
      </FormPage>
    )
  }

  const { run, payslips } = detail
  const draft = run.status === 'Draft'
  const totals = payslips.reduce(
    (sum, slip) => ({
      earnings: sum.earnings + slip.earnings,
      deductions: sum.deductions + slip.deductions,
      goods: sum.goods + slip.purchasesDeducted,
      net: sum.net + slip.netPaid,
    }),
    { earnings: 0, deductions: 0, goods: 0, net: 0 },
  )

  return (
    <FormPage
      title={t('run.title', { number: run.runNumber })}
      description={t('run.description', {
        from: formatIsoDate(run.periodFrom),
        to: formatIsoDate(run.periodTo),
        branch: detail.branchName ?? '',
      })}
      onBack={back}
      backLabel={t('back')}
    >
      <div className="flex flex-wrap items-center gap-2">
        <span className={cn('rounded px-2 py-0.5 text-xs font-medium', draft ? 'bg-muted' : 'bg-primary/10 text-primary')}>
          {draft
            ? t('status.draft')
            : t('status.paidFrom', { date: run.paidOn ? formatIsoDate(run.paidOn) : '', account: detail.paymentAccountName ?? '' })}
        </span>
        <span className="ml-auto inline-flex flex-wrap gap-2">
          {draft ? (
            <>
              <Button type="button" variant="destructive" disabled={busy} onClick={() => setDiscarding(true)}>
                {t('run.discard')}
              </Button>
              <Button type="button" disabled={busy || payslips.length === 0} onClick={openPay}>
                {t('run.pay')}
              </Button>
            </>
          ) : (
            <Link to={`/print/payslips/${run.runId}`} className={buttonVariants({ variant: 'outline' })}>
              {t('run.print')}
            </Link>
          )}
        </span>
      </div>

      {draft && <p className="text-sm text-muted-foreground">{t('run.draftHint')}</p>}
      {error && (
        <p role="alert" className="rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {error}
        </p>
      )}

      {paying && draft && (
        <div className="flex flex-col gap-3 rounded-lg border border-border bg-card p-4">
          <h2 className="text-lg font-semibold">{t('pay.title')}</h2>
          <p className="text-sm text-muted-foreground">{t('pay.hint')}</p>
          <div className="grid gap-4 sm:grid-cols-3">
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="payrollAccount">{t('pay.account')}</Label>
              <Select id="payrollAccount" value={accountId} onChange={(e) => setAccountId(e.target.value)}>
                {accounts.map((account) => (
                  <option key={account.accountId} value={account.accountId}>
                    {account.name}
                  </option>
                ))}
              </Select>
            </div>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="payrollPaidOn">{t('pay.date')}</Label>
              <Input id="payrollPaidOn" type="date" value={paidOn} max={todayIso()} onChange={(e) => setPaidOn(e.target.value)} />
            </div>
            <div className="flex flex-col justify-end text-sm">
              <span className="text-muted-foreground">{t('pay.total')}</span>
              <span className="text-xl font-semibold tabular-nums">{formatMoney(totals.net)}</span>
            </div>
          </div>
          <div className="flex gap-2">
            <Button type="button" disabled={accountId === '' || busy} onClick={() => setConfirmPay(true)}>
              {t('pay.confirm')}
            </Button>
            <Button type="button" variant="outline" onClick={() => setPaying(false)}>
              {t('pay.cancel')}
            </Button>
          </div>
        </div>
      )}

      <div className="overflow-x-auto rounded-lg border border-border bg-card">
        <table className="w-full min-w-[820px] text-sm" aria-label={t('run.payslips')}>
          <thead className="border-b border-border text-left text-xs text-muted-foreground">
            <tr>
              <th className="px-3 py-2 font-medium">{t('columns.employee')}</th>
              <th className="px-3 py-2 text-right font-medium">{t('columns.earnings')}</th>
              <th className="px-3 py-2 text-right font-medium">{t('columns.deductions')}</th>
              <th className="px-3 py-2 text-right font-medium">{t('columns.goods')}</th>
              <th className="px-3 py-2 text-right font-medium">{t('columns.net')}</th>
              <th className="px-3 py-2">
                <span className="sr-only">{t('columns.actions')}</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {payslips.map((slip) => (
              <Fragment key={slip.payslipId}>
                <tr className="border-b border-border align-top">
                  <td className="px-3 py-2">
                    <span className="font-medium">{slip.employeeName}</span>
                    <span className="block text-xs text-muted-foreground">
                      {t('file', { number: slip.fileNumber })}
                      {slip.roleName ? ` · ${slip.roleName}` : ''}
                    </span>
                  </td>
                  <td className="px-3 py-2 text-right tabular-nums">{formatMoney(slip.earnings)}</td>
                  <td className="px-3 py-2 text-right tabular-nums">{slip.deductions > 0 ? `−${formatMoney(slip.deductions)}` : '—'}</td>
                  <td className="px-3 py-2 text-right tabular-nums">
                    {slip.purchasesAmount > 0 ? (
                      <>
                        −{formatMoney(slip.purchasesDeducted)}
                        {slip.purchasesDiscountPercent > 0 && (
                          <span className="block text-xs text-muted-foreground">
                            {t('goodsDiscount', { amount: formatMoney(slip.purchasesAmount), percent: slip.purchasesDiscountPercent })}
                          </span>
                        )}
                      </>
                    ) : (
                      '—'
                    )}
                  </td>
                  <td className={cn('px-3 py-2 text-right font-semibold tabular-nums', slip.net < 0 && 'text-destructive')}>
                    {formatMoney(slip.net)}
                    {slip.net < 0 && <span className="block text-xs font-normal">{t('negativeNet')}</span>}
                  </td>
                  <td className="px-3 py-2 text-right whitespace-nowrap">
                    {draft && (
                      <>
                        <Button
                          type="button"
                          variant="outline"
                          size="sm"
                          className="mr-1"
                          onClick={() => setEditing(editing === slip.payslipId ? null : slip.payslipId)}
                        >
                          {editing === slip.payslipId ? t('actions.close') : t('actions.edit')}
                        </Button>
                        <Button
                          type="button"
                          variant="outline"
                          size="sm"
                          onClick={() => void removePayslip(run.runId, slip.payslipId).then(load, (err) => setError(errorText(err)))}
                        >
                          {t('actions.remove')}
                        </Button>
                      </>
                    )}
                  </td>
                </tr>
                {(editing === slip.payslipId || !draft) && (
                  <tr className="border-b border-border bg-muted/20">
                    <td colSpan={6} className="px-3 py-3">
                      {draft ? (
                        <PayslipEditor
                          runId={run.runId}
                          payslip={slip}
                          onSaved={async () => {
                            setEditing(null)
                            await load()
                          }}
                          onError={(err) => setError(errorText(err))}
                        />
                      ) : (
                        <PayslipLines payslip={slip} />
                      )}
                    </td>
                  </tr>
                )}
              </Fragment>
            ))}
          </tbody>
          <tfoot className="text-sm font-semibold">
            <tr>
              <td className="px-3 py-2">{t('totals')}</td>
              <td className="px-3 py-2 text-right tabular-nums">{formatMoney(totals.earnings)}</td>
              <td className="px-3 py-2 text-right tabular-nums">−{formatMoney(totals.deductions)}</td>
              <td className="px-3 py-2 text-right tabular-nums">−{formatMoney(totals.goods)}</td>
              <td className="px-3 py-2 text-right tabular-nums">{formatMoney(totals.net)}</td>
              <td />
            </tr>
          </tfoot>
        </table>
      </div>

      {confirmPay && (
        <ConfirmDialog
          title={t('pay.confirmTitle', { number: run.runNumber })}
          message={t('pay.confirmMessage', {
            total: formatMoney(totals.net),
            account: accounts.find((a) => a.accountId === accountId)?.name ?? '',
            count: payslips.length,
          })}
          confirmLabel={t('pay.confirm')}
          busyLabel={t('pay.paying')}
          busy={busy}
          onConfirm={() => void pay()}
          onCancel={() => setConfirmPay(false)}
        />
      )}
      {discarding && (
        <ConfirmDialog
          title={t('run.discardTitle', { number: run.runNumber })}
          message={t('run.discardMessage')}
          confirmLabel={t('run.discard')}
          busyLabel={t('run.discarding')}
          busy={busy}
          destructive
          onConfirm={() => void discard()}
          onCancel={() => setDiscarding(false)}
        />
      )}
    </FormPage>
  )
}

/** A paid payslip's lines, read-only. */
function PayslipLines({ payslip }: { payslip: Payslip }) {
  const { t } = useTranslation('payroll')
  return (
    <ul className="flex flex-col gap-1 text-sm">
      {payslip.lines.map((line) => (
        <li key={line.lineNo} className="flex justify-between gap-4">
          <span>
            {line.kind === 'Earning' ? '+' : '−'} {line.concept}
          </span>
          <span className="tabular-nums">{formatMoney(line.amount)}</span>
        </li>
      ))}
      {payslip.purchasesAmount > 0 && (
        <li className="flex justify-between gap-4">
          <span>− {t('editor.goodsLine', { percent: payslip.purchasesDiscountPercent })}</span>
          <span className="tabular-nums">{formatMoney(payslip.purchasesDeducted)}</span>
        </li>
      )}
    </ul>
  )
}

/** A Draft payslip's earnings and deductions, and the discount on the goods taken. */
function PayslipEditor({
  runId,
  payslip,
  onSaved,
  onError,
}: {
  runId: string
  payslip: Payslip
  onSaved: () => Promise<void>
  onError: (err: unknown) => void
}) {
  const { t } = useTranslation('payroll')
  const [lines, setLines] = useState<EditableLine[]>(() =>
    payslip.lines.map((line) => ({
      key: nextKey++,
      kind: line.kind,
      source: line.source,
      concept: line.concept,
      amount: String(line.amount).replace('.', ','),
    })),
  )
  const [percent, setPercent] = useState(String(payslip.purchasesDiscountPercent).replace('.', ','))
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)

  const add = (kind: PayslipLineKind) =>
    setLines((current) => [...current, { key: nextKey++, kind, source: 'Manual', concept: '', amount: '' }])
  const change = (key: number, patch: Partial<EditableLine>) =>
    setLines((current) => current.map((line) => (line.key === key ? { ...line, ...patch } : line)))

  const parsedPercent = Number(percent.replace(',', '.'))
  const goodsDiscount = Math.round(payslip.purchasesAmount * (Number.isFinite(parsedPercent) ? parsedPercent : 0)) / 100

  const save = async () => {
    const parsed = lines.map((line) => ({ ...line, value: parseAmount(line.amount) }))
    if (parsed.some((line) => line.concept.trim() === '' || line.value === null)) return setError(t('editor.errors.line'))
    if (!Number.isFinite(parsedPercent) || parsedPercent < 0 || parsedPercent > 100) return setError(t('editor.errors.percent'))
    setError(null)
    setSaving(true)
    try {
      await updatePayslip(runId, payslip.payslipId, {
        lines: parsed.map((line) => ({ kind: line.kind, source: line.source, concept: line.concept.trim(), amount: line.value! })),
        purchasesDiscountPercent: Math.round(parsedPercent * 100) / 100,
      })
      await onSaved()
    } catch (err) {
      onError(err)
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="flex flex-col gap-3">
      <ul className="flex flex-col gap-2" aria-label={t('editor.lines', { name: payslip.employeeName })}>
        {lines.map((line) => (
          <li key={line.key} className="flex flex-wrap items-center gap-2">
            <span className={cn('w-24 text-xs font-medium', line.kind === 'Earning' ? 'text-primary' : 'text-destructive')}>
              {t(`editor.kind.${line.kind}`)}
            </span>
            <Input
              aria-label={t('editor.concept')}
              className="h-8 min-w-48 flex-1"
              value={line.concept}
              maxLength={200}
              placeholder={line.kind === 'Earning' ? t('editor.earningPlaceholder') : t('editor.deductionPlaceholder')}
              onChange={(e) => change(line.key, { concept: e.target.value })}
            />
            <Input
              aria-label={t('editor.amount')}
              className="h-8 w-36"
              inputMode="decimal"
              value={line.amount}
              onChange={(e) => change(line.key, { amount: e.target.value })}
            />
            <Button type="button" variant="outline" size="sm" onClick={() => setLines((current) => current.filter((item) => item.key !== line.key))}>
              {t('editor.removeLine')}
            </Button>
          </li>
        ))}
      </ul>
      <div className="flex flex-wrap gap-2">
        <Button type="button" variant="outline" size="sm" onClick={() => add('Earning')}>
          {t('editor.addEarning')}
        </Button>
        <Button type="button" variant="outline" size="sm" onClick={() => add('Deduction')}>
          {t('editor.addDeduction')}
        </Button>
      </div>
      {payslip.takesGoods && (
        <div className="flex flex-wrap items-end gap-3 rounded-md border border-border p-3">
          <div className="text-sm">
            <span className="text-muted-foreground">{t('editor.goods')}</span>
            <span className="block font-medium tabular-nums">{formatMoney(payslip.purchasesAmount)}</span>
          </div>
          <div className="flex flex-col gap-1">
            <Label htmlFor={`discount-${payslip.payslipId}`}>{t('editor.discount')}</Label>
            <Input
              id={`discount-${payslip.payslipId}`}
              className="h-8 w-24"
              inputMode="decimal"
              value={percent}
              onChange={(e) => setPercent(e.target.value)}
            />
          </div>
          <p className="text-xs text-muted-foreground">
            {t('editor.goodsResult', {
              discount: formatMoney(goodsDiscount),
              deducted: formatMoney(Math.max(payslip.purchasesAmount - goodsDiscount, 0)),
            })}
          </p>
        </div>
      )}
      {error && (
        <p role="alert" className="text-sm text-destructive">
          {error}
        </p>
      )}
      <div>
        <Button type="button" size="sm" disabled={saving} onClick={() => void save()}>
          {saving ? t('editor.saving') : t('editor.save')}
        </Button>
      </div>
    </div>
  )
}

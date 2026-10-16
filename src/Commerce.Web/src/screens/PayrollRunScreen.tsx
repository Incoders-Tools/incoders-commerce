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
import { MoneyInput } from '@/components/form/MoneyField'
import { formatFileNumber } from '@/lib/fileNumber'
import { goodsDiscount, itemDiscount } from '@/lib/goodsDiscount'
import { parsePercentage } from '@/lib/percentage'

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
  // owner feedback P5: the payslips paid part by part ("pago mixto"); the others are paid whole from `accountId`.
  const [splits, setSplits] = useState<Record<string, SplitPart[]>>({})

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
    setSplits({})
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
      const asked = Object.entries(splits).map(([payslipId, parts]) => ({
        payslipId,
        parts: parts.map((part) => ({ accountId: part.accountId, amount: parseAmount(part.amount)! })),
      }))
      await payPayrollRun(id, { accountId, paidOn, ...(asked.length > 0 ? { splits: asked } : {}) })
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
  // owner feedback P5: what this payment takes out now (a split may pay less than the net), and whether it can go.
  const splitStates = Object.fromEntries(
    payslips.filter((slip) => splits[slip.payslipId]).map((slip) => [slip.payslipId, splitState(splits[slip.payslipId], slip.netPaid)]),
  )
  const payingNow = payslips.reduce((sum, slip) => sum + (splitStates[slip.payslipId]?.paid ?? slip.netPaid), 0)
  const splitsReady = Object.values(splitStates).every((state) => state.valid && state.over === 0)
  const paidAccounts = new Set(payslips.flatMap((slip) => (slip.payments ?? []).map((payment) => payment.accountId)))

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
            : paidAccounts.size > 1
              ? t('status.paidFromSeveral', { date: run.paidOn ? formatIsoDate(run.paidOn) : '' })
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
              <span className="text-xl font-semibold tabular-nums">{formatMoney(payingNow)}</span>
            </div>
          </div>
          <PaymentSplits
            payslips={payslips}
            accounts={accounts}
            defaultAccountId={accountId}
            splits={splits}
            states={splitStates}
            onChange={setSplits}
          />
          <div className="flex gap-2">
            <Button type="button" disabled={accountId === '' || busy || !splitsReady} onClick={() => setConfirmPay(true)}>
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
                      {t('file', { number: formatFileNumber(slip.fileNumber) })}
                      {slip.roleName ? ` · ${slip.roleName}` : ''}
                    </span>
                  </td>
                  <td className="px-3 py-2 text-right tabular-nums">{formatMoney(slip.earnings)}</td>
                  <td className="px-3 py-2 text-right tabular-nums">{slip.deductions > 0 ? `−${formatMoney(slip.deductions)}` : '—'}</td>
                  <td className="px-3 py-2 text-right tabular-nums">
                    {slip.purchasesAmount > 0 ? (
                      <>
                        −{formatMoney(slip.purchasesDeducted)}
                        {slip.purchasesDiscount > 0 && (
                          <span className="block text-xs text-muted-foreground">
                            {t('goodsDiscount', { amount: formatMoney(slip.purchasesAmount), discount: formatMoney(slip.purchasesDiscount) })}
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
            total: formatMoney(payingNow),
            account:
              Object.keys(splits).length > 0 ? t('pay.severalAccounts') : (accounts.find((a) => a.accountId === accountId)?.name ?? ''),
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

interface SplitPart {
  key: number
  accountId: string
  amount: string
}

/** What a payslip's parts pay, how far they are from its net, and whether every part has an amount. */
function splitState(parts: SplitPart[], net: number) {
  const amounts = parts.map((part) => parseAmount(part.amount))
  const paid = Math.round(amounts.reduce<number>((sum, amount) => sum + (amount ?? 0), 0) * 100) / 100
  return {
    paid,
    valid: amounts.every((amount) => amount !== null && amount > 0),
    over: Math.max(Math.round((paid - net) * 100) / 100, 0),
    short: Math.max(Math.round((net - paid) * 100) / 100, 0),
  }
}

const amountText = (amount: number) => (amount > 0 ? String(amount).replace('.', ',') : '')

/**
 * owner feedback P5: how each payslip is paid: whole from the run's account, or split among accounts ("pago mixto").
 * The parts never exceed the net (paying is blocked); what they leave unpaid stays owed to the employee.
 */
function PaymentSplits({
  payslips,
  accounts,
  defaultAccountId,
  splits,
  states,
  onChange,
}: {
  payslips: Payslip[]
  accounts: TreasuryAccount[]
  defaultAccountId: string
  splits: Record<string, SplitPart[]>
  states: Record<string, ReturnType<typeof splitState>>
  onChange: (splits: Record<string, SplitPart[]>) => void
}) {
  const { t } = useTranslation('payroll')
  const defaultName = accounts.find((account) => account.accountId === defaultAccountId)?.name ?? ''
  const set = (payslipId: string, parts: SplitPart[] | null) => {
    const next = { ...splits }
    if (parts) next[payslipId] = parts
    else delete next[payslipId]
    onChange(next)
  }

  return (
    <section aria-label={t('pay.splitsTitle')} className="flex flex-col gap-2">
      <h3 className="text-sm font-semibold">{t('pay.splitsTitle')}</h3>
      <ul className="flex flex-col gap-2">
        {payslips
          .filter((slip) => slip.netPaid > 0)
          .map((slip) => {
            const name = slip.employeeName
            const parts = splits[slip.payslipId]
            const state = states[slip.payslipId]
            const change = (key: number, patch: Partial<SplitPart>) =>
              set(slip.payslipId, parts.map((part) => (part.key === key ? { ...part, ...patch } : part)))
            const add = () => {
              const used = new Set(parts.map((part) => part.accountId))
              const account = accounts.find((candidate) => !used.has(candidate.accountId))?.accountId ?? defaultAccountId
              set(slip.payslipId, [...parts, { key: nextKey++, accountId: account, amount: amountText(state?.short ?? 0) }])
            }
            return (
              <li key={slip.payslipId} aria-label={t('pay.splitOf', { name })} className="flex flex-col gap-2 rounded-md border border-border p-2 text-sm">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="min-w-40 flex-1 font-medium">{name}</span>
                  <span className="tabular-nums">{formatMoney(slip.netPaid)}</span>
                  {parts ? (
                    <Button type="button" variant="outline" size="sm" onClick={() => set(slip.payslipId, null)}>
                      {t('pay.unsplit')}
                    </Button>
                  ) : (
                    <>
                      <span className="text-xs text-muted-foreground">{t('pay.whole', { account: defaultName })}</span>
                      <Button
                        type="button"
                        variant="outline"
                        size="sm"
                        aria-label={t('pay.splitLabel', { name })}
                        onClick={() => set(slip.payslipId, [{ key: nextKey++, accountId: defaultAccountId, amount: amountText(slip.netPaid) }])}
                      >
                        {t('pay.split')}
                      </Button>
                    </>
                  )}
                </div>
                {parts && (
                  <>
                    {parts.map((part, index) => (
                      <div key={part.key} className="flex flex-wrap items-center gap-2">
                        <Select
                          aria-label={t('pay.partAccount', { n: index + 1, name })}
                          className="h-8 w-56"
                          value={part.accountId}
                          onChange={(e) => change(part.key, { accountId: e.target.value })}
                        >
                          {accounts.map((account) => (
                            <option key={account.accountId} value={account.accountId}>
                              {account.name}
                            </option>
                          ))}
                        </Select>
                        <MoneyInput
                          compact
                          aria-label={t('pay.partAmount', { n: index + 1, name })}
                          className="h-8 w-36"
                          value={part.amount}
                          onChange={(amount) => change(part.key, { amount })}
                        />
                        <Button
                          type="button"
                          variant="outline"
                          size="sm"
                          disabled={parts.length === 1}
                          onClick={() => set(slip.payslipId, parts.filter((other) => other.key !== part.key))}
                        >
                          {t('pay.removePart')}
                        </Button>
                      </div>
                    ))}
                    <div>
                      <Button type="button" variant="outline" size="sm" onClick={add}>
                        {t('pay.addPart')}
                      </Button>
                    </div>
                    {state && (
                      <p className={cn('text-xs', state.over > 0 || !state.valid ? 'text-destructive' : 'text-muted-foreground')}>
                        {!state.valid
                          ? t('pay.partsInvalid')
                          : state.over > 0
                            ? t('pay.partsOver', { amount: formatMoney(state.over) })
                            : state.short > 0
                              ? t('pay.partsShort', { amount: formatMoney(state.short) })
                              : t('pay.partsSum', { paid: formatMoney(state.paid), net: formatMoney(slip.netPaid) })}
                      </p>
                    )}
                  </>
                )}
              </li>
            )
          })}
      </ul>
    </section>
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
          <span>
            −{' '}
            {payslip.purchasesDiscount > 0
              ? t('editor.goodsLineDiscount', { discount: formatMoney(payslip.purchasesDiscount) })
              : t('editor.goodsLine')}
          </span>
          <span className="tabular-nums">{formatMoney(payslip.purchasesDeducted)}</span>
        </li>
      )}
      {/* owner feedback P5: what paid it, and what stayed owed to the employee. */}
      {(payslip.payments ?? []).map((payment, index) => (
        <li key={`payment-${index}`} className="flex justify-between gap-4 text-muted-foreground">
          <span>{t('editor.paidFrom', { account: payment.accountName })}</span>
          <span className="tabular-nums">{formatMoney(payment.amount)}</span>
        </li>
      ))}
      {(payslip.payments ?? []).length > 0 && payslip.paid < payslip.netPaid && (
        <li className="flex justify-between gap-4 text-muted-foreground">
          <span>{t('editor.leftOwed')}</span>
          <span className="tabular-nums">{formatMoney(payslip.netPaid - payslip.paid)}</span>
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
  const percentText = (value: number) => (value === 0 ? '' : String(value).replace('.', ','))
  const [percent, setPercent] = useState(percentText(payslip.purchasesDiscountPercent))
  // employee-lifecycle E4: each goods item's discount; the percent above applies to what the items leave.
  const items = payslip.goodsItems ?? []
  const [itemPercents, setItemPercents] = useState<Record<string, string>>(() =>
    Object.fromEntries(items.map((item) => [item.id, percentText(item.discountPercent)])),
  )
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)

  const add = (kind: PayslipLineKind) =>
    setLines((current) => [...current, { key: nextKey++, kind, source: 'Manual', concept: '', amount: '' }])
  const change = (key: number, patch: Partial<EditableLine>) =>
    setLines((current) => current.map((line) => (line.key === key ? { ...line, ...patch } : line)))

  const readPercent = (text: string) => {
    const reading = parsePercentage(text)
    return reading.ok && (reading.value ?? 0) <= 100 ? (reading.value ?? 0) : null
  }
  const parsedPercent = readPercent(percent)
  const parsedItems = items.map((item) => ({ item, percent: readPercent(itemPercents[item.id] ?? '') }))
  const itemsDiscount = parsedItems.reduce((sum, { item, percent: value }) => sum + itemDiscount(item.lineTotal, value ?? 0), 0)
  const preview = goodsDiscount(payslip.purchasesAmount, parsedPercent ?? 0, itemsDiscount)
  // What the goods account owes beyond the items (a debt from before they were kept, or entered by hand).
  const untracked = Math.max(payslip.purchasesAmount - items.reduce((sum, item) => sum + item.lineTotal, 0), 0)

  const save = async () => {
    const parsed = lines.map((line) => ({ ...line, value: parseAmount(line.amount) }))
    if (parsed.some((line) => line.concept.trim() === '' || line.value === null)) return setError(t('editor.errors.line'))
    if (parsedPercent === null || parsedItems.some((read) => read.percent === null)) return setError(t('editor.errors.percent'))
    setError(null)
    setSaving(true)
    try {
      await updatePayslip(runId, payslip.payslipId, {
        lines: parsed.map((line) => ({ kind: line.kind, source: line.source, concept: line.concept.trim(), amount: line.value! })),
        purchasesDiscountPercent: parsedPercent,
        ...(items.length > 0 ? { goodsItems: parsedItems.map(({ item, percent: value }) => ({ itemId: item.id, discountPercent: value! })) } : {}),
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
              // employee-lifecycle E2: the server keeps the overtime line equal to the hours it took; it can only go.
              readOnly={line.source === 'Overtime'}
              maxLength={200}
              placeholder={line.kind === 'Earning' ? t('editor.earningPlaceholder') : t('editor.deductionPlaceholder')}
              onChange={(e) => change(line.key, { concept: e.target.value })}
            />
            <MoneyInput
              compact
              aria-label={t('editor.amount')}
              className="h-8 w-36"
              value={line.amount}
              readOnly={line.source === 'Overtime'}
              onChange={(amount) => change(line.key, { amount })}
            />
            <Button type="button" variant="outline" size="sm" onClick={() => setLines((current) => current.filter((item) => item.key !== line.key))}>
              {t('editor.removeLine')}
            </Button>
            {line.source === 'Overtime' && <span className="basis-full pl-26 text-xs text-muted-foreground">{t('editor.overtimeHint')}</span>}
          </li>
        ))}
        {/* owner feedback P1b: the goods deducted read among the deductions too; the block below sets their discount. */}
        {payslip.purchasesAmount > 0 && (
          <li aria-label={t('editor.goodsDeductionLine')} className="flex flex-wrap items-center gap-2">
            <span className="w-24 text-xs font-medium text-destructive">{t('editor.kind.Deduction')}</span>
            <span className="min-w-48 flex-1 text-sm">{t('editor.goodsDeductionLine')}</span>
            <span className="w-36 text-sm tabular-nums">{formatMoney(preview.deducted)}</span>
            <span className="basis-full pl-26 text-xs text-muted-foreground">{t('editor.goodsDeductionHint')}</span>
          </li>
        )}
      </ul>
      <div className="flex flex-wrap gap-2">
        <Button type="button" variant="outline" size="sm" onClick={() => add('Earning')}>
          {t('editor.addEarning')}
        </Button>
        <Button type="button" variant="outline" size="sm" onClick={() => add('Deduction')}>
          {t('editor.addDeduction')}
        </Button>
      </div>
      {(payslip.takesGoods || items.length > 0) && (
        <div className="flex flex-col gap-3 rounded-md border border-border p-3">
          <div className="text-sm">
            <span className="text-muted-foreground">{t('editor.goods')}</span>
            <span className="block font-medium tabular-nums">{formatMoney(payslip.purchasesAmount)}</span>
          </div>
          {items.length > 0 && (
            <ul className="flex flex-col gap-2" aria-label={t('editor.goodsItems', { name: payslip.employeeName })}>
              {parsedItems.map(({ item, percent: value }) => {
                const name = `${item.productName} ${item.presentationName}`
                const discount = itemDiscount(item.lineTotal, value ?? 0)
                return (
                  <li key={item.id} className="flex flex-wrap items-center gap-2 text-sm">
                    <span className="min-w-48 flex-1">
                      {name}
                      <span className="block text-xs text-muted-foreground">
                        {formatIsoDate(item.occurredOn)} · {String(item.quantity).replace('.', ',')} · {t(`editor.goodsSource.${item.sourceType}`)}
                      </span>
                    </span>
                    <span className="w-28 text-right tabular-nums">{formatMoney(item.lineTotal)}</span>
                    <Input
                      aria-label={t('editor.itemDiscount', { name })}
                      className="h-8 w-20"
                      inputMode="decimal"
                      placeholder="0"
                      value={itemPercents[item.id] ?? ''}
                      onChange={(e) => setItemPercents((current) => ({ ...current, [item.id]: e.target.value }))}
                    />
                    <span className="w-28 text-right text-xs text-muted-foreground tabular-nums">
                      {discount > 0 ? `−${formatMoney(discount)}` : ''}
                    </span>
                  </li>
                )
              })}
            </ul>
          )}
          {items.length > 0 && untracked > 0 && (
            <p className="text-xs text-muted-foreground">{t('editor.goodsUntracked', { amount: formatMoney(untracked) })}</p>
          )}
          <div className="flex flex-wrap items-end gap-3">
            <div className="flex flex-col gap-1">
              <Label htmlFor={`discount-${payslip.payslipId}`}>{items.length > 0 ? t('editor.discountRest') : t('editor.discount')}</Label>
              <Input
                id={`discount-${payslip.payslipId}`}
                className="h-8 w-24"
                inputMode="decimal"
                placeholder="0"
                value={percent}
                onChange={(e) => setPercent(e.target.value)}
              />
            </div>
            <p className="text-xs text-muted-foreground">
              {t('editor.goodsResult', { discount: formatMoney(preview.discount), deducted: formatMoney(preview.deducted) })}
            </p>
          </div>
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

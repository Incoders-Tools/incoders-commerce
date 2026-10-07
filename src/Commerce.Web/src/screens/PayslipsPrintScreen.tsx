import { useEffect, useRef, useState } from 'react'
import { useNavigate, useParams } from 'react-router'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { getPayrollRun, type PayrollRunDetail, type Payslip } from '@/api/employees'
import { getOrganizationDocumentProfile, type OrganizationDocumentProfile } from '@/api/fulfillment'
import { formatIsoDate, formatMoney } from '@/dashboard/format'

/**
 * The payslips of a run, printable: one A4 page per employee with two copies, the employee's and the business's (signed
 * "recibí conforme"). INTERNAL receipt of what was paid, not the legal payslip. Rendered outside the app shell so only
 * the receipts print; opens the print dialog once they are ready.
 */
export function PayslipsPrintScreen() {
  const { t } = useTranslation('payroll')
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const [detail, setDetail] = useState<PayrollRunDetail | null>(null)
  const [issuer, setIssuer] = useState<OrganizationDocumentProfile | null>(null)
  const [error, setError] = useState<string | null>(null)
  const printed = useRef(false)

  useEffect(() => {
    let cancelled = false
    getPayrollRun(id).then(
      (run) => {
        if (!cancelled) setDetail(run)
      },
      () => {
        if (!cancelled) setError(t('errors.load'))
      },
    )
    getOrganizationDocumentProfile().then(
      (profile) => {
        if (!cancelled) setIssuer(profile)
      },
      () => {},
    )
    return () => {
      cancelled = true
    }
  }, [id, t])

  useEffect(() => {
    if (detail && detail.payslips.length > 0 && !printed.current) {
      printed.current = true
      const timer = window.setTimeout(() => window.print(), 400)
      return () => window.clearTimeout(timer)
    }
  }, [detail])

  return (
    <div className="min-h-screen bg-muted/40 print:bg-white">
      <style>{`
        @page { size: A4; margin: 10mm; }
        @media print {
          .payslip-page { break-after: page; box-shadow: none !important; margin: 0 !important; border: none !important; }
          .payslip-page:last-child { break-after: auto; }
        }
      `}</style>
      <div className="sticky top-0 z-10 flex items-center gap-2 border-b border-border bg-card px-4 py-2 print:hidden">
        <Button type="button" variant="outline" size="sm" onClick={() => navigate(-1)}>
          {t('print.back')}
        </Button>
        <Button type="button" size="sm" disabled={!detail} onClick={() => window.print()}>
          {t('print.print')}
        </Button>
      </div>
      {error && (
        <p role="alert" className="m-6 text-sm text-destructive">
          {error}
        </p>
      )}
      {detail?.payslips.map((payslip) => (
        <div key={payslip.payslipId} className="payslip-page mx-auto my-6 flex max-w-[190mm] flex-col gap-6 bg-white p-6 text-black shadow">
          {(['employee', 'business'] as const).map((copy) => (
            <Receipt key={copy} copy={copy} detail={detail} payslip={payslip} issuer={issuer} />
          ))}
        </div>
      ))}
    </div>
  )
}

function Receipt({
  copy,
  detail,
  payslip,
  issuer,
}: {
  copy: 'employee' | 'business'
  detail: PayrollRunDetail
  payslip: Payslip
  issuer: OrganizationDocumentProfile | null
}) {
  const { t } = useTranslation('payroll')
  const earnings = payslip.lines.filter((line) => line.kind === 'Earning')
  const deductions = payslip.lines.filter((line) => line.kind === 'Deduction')
  return (
    <section className="flex flex-col gap-3 border border-black/40 p-4 text-sm" aria-label={t(`print.copy.${copy}`)}>
      <header className="flex items-start justify-between gap-4 border-b border-black/30 pb-2">
        <div>
          <p className="text-base font-semibold">{issuer?.legalName ?? issuer?.name ?? ''}</p>
          {issuer?.taxId && <p className="text-xs">CUIT {issuer.taxId}</p>}
          {detail.branchName && <p className="text-xs">{detail.branchName}</p>}
        </div>
        <div className="text-right">
          <p className="font-semibold">{t('print.title')}</p>
          <p className="text-xs">{t('print.copy.' + copy)}</p>
          <p className="text-xs">
            {t('run.title', { number: detail.run.runNumber })} · {formatIsoDate(detail.run.periodFrom)} – {formatIsoDate(detail.run.periodTo)}
          </p>
        </div>
      </header>
      <p>
        <span className="font-semibold">{payslip.employeeName}</span> · {t('file', { number: payslip.fileNumber })}
        {payslip.cuil ? ` · CUIL ${payslip.cuil}` : ''}
        {payslip.roleName ? ` · ${payslip.roleName}` : ''}
      </p>
      <table className="w-full">
        <tbody>
          {earnings.map((line) => (
            <tr key={line.lineNo}>
              <td>{line.concept}</td>
              <td className="text-right tabular-nums">{formatMoney(line.amount)}</td>
            </tr>
          ))}
          {deductions.map((line) => (
            <tr key={line.lineNo}>
              <td>{line.concept}</td>
              <td className="text-right tabular-nums">−{formatMoney(line.amount)}</td>
            </tr>
          ))}
          {payslip.purchasesAmount > 0 && (
            <tr>
              <td>
                {t('print.goods', { amount: formatMoney(payslip.purchasesAmount), percent: payslip.purchasesDiscountPercent })}
              </td>
              <td className="text-right tabular-nums">−{formatMoney(payslip.purchasesDeducted)}</td>
            </tr>
          )}
          <tr className="border-t border-black/40 font-semibold">
            <td>{t('print.net')}</td>
            <td className="text-right tabular-nums">{formatMoney(payslip.netPaid)}</td>
          </tr>
        </tbody>
      </table>
      <p className="text-xs">
        {t('print.paid', { date: detail.run.paidOn ? formatIsoDate(detail.run.paidOn) : '', account: detail.paymentAccountName ?? '' })}
      </p>
      <footer className="mt-6 flex items-end justify-between gap-6 text-xs">
        <p className="max-w-[60%] text-black/70">{t('print.disclaimer')}</p>
        <p className="w-56 border-t border-black pt-1 text-center">{t('print.signature')}</p>
      </footer>
    </section>
  )
}

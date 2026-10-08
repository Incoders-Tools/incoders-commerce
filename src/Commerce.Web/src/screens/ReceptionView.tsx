import { useState, type ReactNode } from 'react'
import { Link } from 'react-router'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { FormPage } from '@/components/layout/FormPage'
import { ConfirmDialog } from '@/components/layout/ConfirmDialog'
import { ReceptionStatusBadge } from '@/components/purchasing/ReceptionStatusBadge'
import { voidReception } from '@/api/purchases'
import { ApiError } from '@/api/client'
import type { ReceptionRecord } from '@/api/types'
import { formatIsoDate, formatMoney } from '@/dashboard/format'
import { useNumberFormat } from '@/organization/NumberFormatContext'

const VOID_REASON_MAX = 300

function Detail({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex flex-col gap-0.5">
      <dt className="text-xs font-medium uppercase tracking-wide text-muted-foreground">{label}</dt>
      <dd className="text-sm text-foreground">{children}</dd>
    </div>
  )
}

/**
 * Read-only view of a confirmed or voided reception: its number, supplier
 * (with a link to the account the invoice went to), lines and, while it is
 * confirmed, the "Anular" action. Voiding asks for a reason and explains that
 * it reverses both the stock and the invoice.
 */
export function ReceptionView({
  reception,
  onChanged,
  onBack,
}: {
  reception: ReceptionRecord
  onChanged: (reception: ReceptionRecord) => void
  onBack: () => void
}) {
  const { t } = useTranslation('purchases')
  const numberFormat = useNumberFormat()
  const [voiding, setVoiding] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const confirmVoid = async (reason: string) => {
    setBusy(true)
    setError(null)
    try {
      const voided = await voidReception(reception.id, reason)
      setVoiding(false)
      onChanged(voided)
    } catch (err) {
      setVoiding(false)
      setError(
        err instanceof ApiError
          ? err.code === 'reception-not-confirmed'
            ? t('form.errors.notConfirmed')
            : err.message
          : t('form.errors.unexpectedSave'),
      )
    } finally {
      setBusy(false)
    }
  }

  const noValue = <span className="text-muted-foreground">—</span>

  return (
    <FormPage title={t('form.titleView')} onBack={onBack} backLabel={t('form.backLabel')}>
      <div className="flex flex-col gap-8">
        <dl className="grid grid-cols-1 gap-x-6 gap-y-4 sm:grid-cols-2 xl:grid-cols-3">
          <Detail label={t('view.number')}>{reception.number ?? noValue}</Detail>
          <Detail label={t('view.status')}>
            <ReceptionStatusBadge status={reception.status} />
          </Detail>
          <Detail label={t('view.supplier')}>
            <Link className="font-medium text-primary underline" to={`/app/suppliers/${reception.supplierId}/account`}>
              {reception.supplierName}
            </Link>
          </Detail>
          <Detail label={t('view.document')}>
            {t(`documentType.${reception.documentType}`)}
            {reception.documentReference ? ` ${reception.documentReference}` : ''}
          </Detail>
          <Detail label={t('view.date')}>{formatIsoDate(reception.occurredOn)}</Detail>
          <Detail label={t('view.due')}>{reception.dueOn ? formatIsoDate(reception.dueOn) : noValue}</Detail>
          {reception.notes && <Detail label={t('view.notes')}>{reception.notes}</Detail>}
        </dl>

        <p>
          <Link
            className="text-sm font-medium text-primary underline"
            to={`/app/suppliers/${reception.supplierId}/account`}
          >
            {t('form.supplierAccount')}
          </Link>
        </p>

        {reception.status === 'Voided' && reception.voidReason && (
          <p className="rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive">
            {t('view.voidReason', { reason: reception.voidReason })}
          </p>
        )}

        <div className="overflow-x-auto rounded-lg border border-border">
          <table className="w-full text-sm">
            <thead className="bg-muted text-left text-xs uppercase tracking-wide text-muted-foreground">
              <tr>
                <th className="px-3 py-2">{t('lines.table.product')}</th>
                <th className="px-3 py-2">{t('lines.table.presentation')}</th>
                <th className="px-3 py-2 text-right">{t('lines.table.quantity')}</th>
                <th className="px-3 py-2 text-right">{t('lines.table.unitCost')}</th>
                <th className="px-3 py-2 text-right">{t('lines.table.lineTotal')}</th>
                <th className="px-3 py-2">{t('lines.table.lot')}</th>
                <th className="px-3 py-2">{t('lines.table.expires')}</th>
              </tr>
            </thead>
            <tbody>
              {reception.lines.map((line) => (
                <tr key={line.id} className="border-t border-border">
                  <td className="px-3 py-2">{line.productName}</td>
                  <td className="px-3 py-2">{line.presentationName}</td>
                  <td className="px-3 py-2 text-right tabular-nums">
                    {numberFormat.formatStock(line.quantity, line.quantityBehavior)}
                  </td>
                  <td className="px-3 py-2 text-right tabular-nums">{formatMoney(line.unitCost)}</td>
                  <td className="px-3 py-2 text-right tabular-nums">{formatMoney(line.lineTotal)}</td>
                  <td className="px-3 py-2">{line.lotCode ?? noValue}</td>
                  <td className="px-3 py-2">{line.expiresOn ? formatIsoDate(line.expiresOn) : noValue}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <p data-testid="reception-total" className="text-right text-base font-semibold tabular-nums">
          {t('form.total')}: {formatMoney(reception.totalAmount)}
        </p>

        {error && (
          <p role="alert" className="text-sm text-destructive">
            {error}
          </p>
        )}

        {reception.status === 'Confirmed' && (
          <div>
            <Button type="button" variant="destructive" onClick={() => setVoiding(true)}>
              {t('view.void')}
            </Button>
          </div>
        )}
      </div>

      {voiding && (
        <ConfirmDialog
          title={t('voidDialog.title')}
          message={t('voidDialog.message')}
          confirmLabel={t('voidDialog.confirm')}
          busyLabel={t('voidDialog.confirming')}
          busy={busy}
          destructive
          reason={{
            label: t('voidDialog.reason'),
            requiredMessage: t('voidDialog.reasonRequired'),
            maxLength: VOID_REASON_MAX,
          }}
          onConfirm={(reason) => void confirmVoid(reason)}
          onCancel={() => setVoiding(false)}
        />
      )}
    </FormPage>
  )
}

import { useCallback, useEffect, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { useTranslation } from 'react-i18next'
import { Button, buttonVariants } from '@/components/ui/button'
import { ConfirmDialog } from '@/components/layout/ConfirmDialog'
import { FormPage } from '@/components/layout/FormPage'
import { OrderStatusBadge } from '@/components/fulfillment/StatusBadges'
import { formatDateTime, fulfillmentErrorMessage } from '@/components/fulfillment/fulfillmentErrors'
import { changeOrderStatus, getOrder, type OrderStatus, type OrderTrackingDetail } from '@/api/fulfillment'
import { formatIsoDate, formatMoney } from '@/dashboard/format'
import { useNumberFormat } from '@/organization/NumberFormatContext'

/**
 * One order of the branch: who it is for, its lines (with what was delivered once its run came back), its run and
 * remito, and the manual steps the server allows from its status (cancelling asks for a reason). Delivering is not here:
 * an order goes out and comes back with its delivery run.
 */
export function OrderTrackingScreen() {
  const { t } = useTranslation('fulfillment')
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const numberFormat = useNumberFormat()
  const [detail, setDetail] = useState<OrderTrackingDetail | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [cancelling, setCancelling] = useState(false)

  const load = useCallback(async () => {
    setError(null)
    try {
      setDetail(await getOrder(id))
    } catch (err) {
      setError(fulfillmentErrorMessage(err, t, 'errors.unexpectedLoad'))
    }
  }, [id, t])

  useEffect(() => {
    void load()
  }, [load])

  const move = async (status: OrderStatus, reason?: string) => {
    setBusy(true)
    setError(null)
    try {
      await changeOrderStatus(id, status, reason)
      setCancelling(false)
      await load()
    } catch (err) {
      setCancelling(false)
      setError(fulfillmentErrorMessage(err, t))
    } finally {
      setBusy(false)
    }
  }

  const back = () => navigate('/app/orders')
  if (detail === null) {
    return (
      <FormPage title={t('orders.title')} onBack={back} backLabel={t('orders.detail.back')}>
        {error ? (
          <p role="alert" className="text-sm text-destructive">
            {error}
          </p>
        ) : (
          <p role="status" className="text-sm text-muted-foreground">
            …
          </p>
        )}
      </FormPage>
    )
  }

  const { summary, lines, party } = detail
  const quantity = (value: number, behavior: string) =>
    behavior === 'FixedQuantity' ? String(value) : `${numberFormat.formatNumber(value)} kg`
  const delivered = lines.some((line) => line.deliveredQuantity !== null)
  const address = [party.address, party.locality, party.province].filter(Boolean).join(', ')

  return (
    <FormPage
      title={t('orders.detail.title', { number: summary.orderNumber })}
      description={t('orders.detail.description', { date: formatDateTime(summary.submittedAtUtc) })}
      onBack={back}
      backLabel={t('orders.detail.back')}
    >
      {error && (
        <p role="alert" className="rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {error}
        </p>
      )}

      <div className="grid gap-4 lg:grid-cols-3">
        <div className="flex flex-col gap-2 rounded-lg border border-border bg-card p-4 text-sm lg:col-span-2">
          <div className="flex flex-wrap items-center gap-2">
            <OrderStatusBadge status={summary.status} />
            {summary.remitoNumber && (
              <span className="text-muted-foreground">
                {t('orders.detail.remito')}: {summary.remitoNumber}
              </span>
            )}
            {summary.runId && summary.runNumber !== null && (
              <Link to={`/app/deliveries/${summary.runId}`} className="text-muted-foreground underline-offset-2 hover:underline">
                {t('orders.detail.run')}: {t('orders.runLabel', { number: summary.runNumber, date: summary.runDate ? formatIsoDate(summary.runDate) : '' })}
              </Link>
            )}
          </div>
          <dl className="grid gap-x-6 gap-y-1 sm:grid-cols-2">
            <Field label={t('orders.detail.customer')} value={party.legalName ? `${party.displayName} (${party.legalName})` : party.displayName} />
            <Field label={t('orders.detail.taxId')} value={party.taxId} />
            <Field label={t('orders.detail.phone')} value={party.phone} />
            <Field label={t('orders.detail.address')} value={address || null} />
            <Field label={t('orders.detail.deliveryNotes')} value={party.deliveryNotes} />
            <Field label={t('orders.detail.note')} value={summary.note} />
            {summary.settlement && <Field label={t('orders.detail.settlement')} value={t(`settlement.${summary.settlement}`)} />}
            {summary.cancelReason && <Field label={t('orders.detail.cancelReason')} value={summary.cancelReason} />}
          </dl>
        </div>

        <div className="flex flex-col gap-2 rounded-lg border border-border bg-card p-4 text-sm">
          <h2 className="font-semibold">{t('orders.detail.actions')}</h2>
          {detail.allowedTransitions.length === 0 && <p className="text-muted-foreground">{t('orders.detail.noActions')}</p>}
          {detail.allowedTransitions.map((target) => (
            <Button
              key={target}
              type="button"
              variant={target === 'Cancelled' ? 'destructive' : target === 'Confirmed' ? 'outline' : 'default'}
              disabled={busy}
              onClick={() => (target === 'Cancelled' ? setCancelling(true) : void move(target))}
            >
              {t(`orders.detail.moveTo.${target}`)}
            </Button>
          ))}
          {summary.status !== 'Cancelled' && (
            <Link to={`/print/remitos?orders=${summary.orderId}`} className={buttonVariants({ variant: 'outline' })}>
              {t('orders.detail.printRemito')}
            </Link>
          )}
        </div>
      </div>

      <div className="overflow-x-auto rounded-lg border border-border bg-card">
        <table className="w-full text-sm">
          <thead className="border-b border-border text-left text-muted-foreground">
            <tr>
              <th className="px-4 py-2 font-medium">{t('orders.detail.product')}</th>
              <th className="px-4 py-2 text-right font-medium">{t('orders.detail.quantity')}</th>
              {delivered && <th className="px-4 py-2 text-right font-medium">{t('orders.detail.delivered')}</th>}
              <th className="px-4 py-2 text-right font-medium">{t('orders.detail.unitPrice')}</th>
              <th className="px-4 py-2 text-right font-medium">{t('orders.detail.subtotal')}</th>
            </tr>
          </thead>
          <tbody>
            {lines.map((line) => (
              <tr key={line.lineNo} className="border-b border-border last:border-0">
                <td className="px-4 py-2">
                  {line.productName} <span className="text-muted-foreground">{line.presentationName}</span>
                </td>
                <td className="px-4 py-2 text-right tabular-nums">{quantity(line.quantity, line.quantityBehavior)}</td>
                {delivered && (
                  <td className="px-4 py-2 text-right tabular-nums">
                    {line.deliveredQuantity === null ? '—' : quantity(line.deliveredQuantity, line.quantityBehavior)}
                  </td>
                )}
                <td className="px-4 py-2 text-right tabular-nums">{formatMoney(line.unitNetPrice)}</td>
                <td className="px-4 py-2 text-right tabular-nums">{formatMoney(line.lineTotal)}</td>
              </tr>
            ))}
          </tbody>
          <tfoot>
            <tr>
              <td colSpan={delivered ? 4 : 3} className="px-4 py-2 text-right font-medium">
                {t('orders.detail.total')}
              </td>
              <td className="px-4 py-2 text-right font-semibold tabular-nums">{formatMoney(summary.total)}</td>
            </tr>
            {summary.deliveredTotal !== null && (
              <tr>
                <td colSpan={delivered ? 4 : 3} className="px-4 py-2 text-right font-medium">
                  {t('orders.detail.deliveredTotal')}
                </td>
                <td className="px-4 py-2 text-right font-semibold tabular-nums">{formatMoney(summary.deliveredTotal)}</td>
              </tr>
            )}
          </tfoot>
        </table>
      </div>

      {cancelling && (
        <ConfirmDialog
          title={t('orders.detail.cancel.title', { number: summary.orderNumber })}
          message={t('orders.detail.cancel.message')}
          confirmLabel={t('orders.detail.cancel.confirm')}
          busyLabel={t('orders.detail.cancel.busy')}
          busy={busy}
          destructive
          reason={{ label: t('orders.detail.cancel.reason'), requiredMessage: t('orders.detail.cancel.reasonRequired'), maxLength: 200 }}
          onConfirm={(reason) => void move('Cancelled', reason)}
          onCancel={() => setCancelling(false)}
        />
      )}
    </FormPage>
  )
}

function Field({ label, value }: { label: string; value: string | null }) {
  return (
    <div className="flex gap-2">
      <dt className="text-muted-foreground">{label}:</dt>
      <dd>{value ?? '—'}</dd>
    </div>
  )
}

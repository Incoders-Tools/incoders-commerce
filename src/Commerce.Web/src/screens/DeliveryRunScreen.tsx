import { useCallback, useEffect, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { useTranslation } from 'react-i18next'
import { Button, buttonVariants } from '@/components/ui/button'
import { ConfirmDialog } from '@/components/layout/ConfirmDialog'
import { FormPage } from '@/components/layout/FormPage'
import { OrderStatusBadge, RunStatusBadge } from '@/components/fulfillment/StatusBadges'
import { fulfillmentErrorMessage } from '@/components/fulfillment/fulfillmentErrors'
import { deleteRun, dispatchRun, getRun, type DeliveryRunDetail } from '@/api/fulfillment'
import { formatIsoDate, formatMoney } from '@/dashboard/format'

/**
 * One delivery run: its stops in order and what can be done in its status: a planned run is edited or dispatched
 * (dispatching numbers the remitos and takes the orders out); its remitos are printed any time; a run out for delivery
 * is settled when the truck returns.
 */
export function DeliveryRunScreen() {
  const { t } = useTranslation('fulfillment')
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const [detail, setDetail] = useState<DeliveryRunDetail | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [confirming, setConfirming] = useState(false)
  const [deleting, setDeleting] = useState(false)
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    setError(null)
    try {
      setDetail(await getRun(id))
    } catch (err) {
      setError(fulfillmentErrorMessage(err, t, 'errors.unexpectedLoad'))
    }
  }, [id, t])

  useEffect(() => {
    void load()
  }, [load])

  const dispatch = async () => {
    setBusy(true)
    setError(null)
    try {
      await dispatchRun(id)
      setConfirming(false)
      await load()
    } catch (err) {
      setConfirming(false)
      setError(fulfillmentErrorMessage(err, t))
    } finally {
      setBusy(false)
    }
  }

  const discard = async () => {
    setBusy(true)
    setError(null)
    try {
      await deleteRun(id)
      navigate('/app/deliveries')
    } catch (err) {
      setDeleting(false)
      setError(fulfillmentErrorMessage(err, t))
    } finally {
      setBusy(false)
    }
  }

  const back = () => navigate('/app/deliveries')
  if (detail === null) {
    return (
      <FormPage title={t('runs.title')} onBack={back} backLabel={t('runs.view.back')}>
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

  const { run, stops } = detail
  const printable = stops.filter((stop) => stop.order.status !== 'Cancelled').map((stop) => stop.order.orderId)

  return (
    <FormPage
      title={t('runs.view.title', { number: run.runNumber })}
      description={t('runs.view.description', {
        date: formatIsoDate(run.runDate),
        driver: run.driverName ?? t('runs.view.noDriver'),
        vehicle: run.vehicle ?? t('runs.view.noVehicle'),
      })}
      onBack={back}
      backLabel={t('runs.view.back')}
    >
      {error && (
        <p role="alert" className="rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {error}
        </p>
      )}

      <div className="flex flex-wrap items-center gap-2">
        <RunStatusBadge status={run.status} />
        <span className="text-sm font-medium tabular-nums">{formatMoney(run.total)}</span>
        <span className="ml-auto inline-flex flex-wrap gap-2">
          {run.status === 'Planned' && (
            <>
              <Link to={`/app/deliveries/${run.runId}/edit`} className={buttonVariants({ variant: 'outline' })}>
                {t('runs.view.edit')}
              </Link>
              <Button type="button" variant="destructive" disabled={busy} onClick={() => setDeleting(true)}>
                {t('runs.view.delete')}
              </Button>
              <Button type="button" disabled={stops.length === 0 || busy} onClick={() => setConfirming(true)}>
                {t('runs.view.dispatch')}
              </Button>
            </>
          )}
          {printable.length > 0 && (
            <Link to={`/print/remitos?orders=${printable.join(',')}`} className={buttonVariants({ variant: 'outline' })}>
              {t('runs.view.printRemitos')}
            </Link>
          )}
          {run.status === 'OutForDelivery' && (
            <Link to={`/app/deliveries/${run.runId}/settle`} className={buttonVariants()}>
              {t('runs.view.settle')}
            </Link>
          )}
        </span>
      </div>

      {run.notes && (
        <p className="rounded-md border border-border bg-card px-4 py-3 text-sm">
          <span className="text-muted-foreground">{t('runs.view.notes')}: </span>
          {run.notes}
        </p>
      )}

      <div className="overflow-x-auto rounded-lg border border-border bg-card">
        <table className="w-full text-sm" aria-label={t('runs.view.stops')}>
          <thead className="border-b border-border text-left text-muted-foreground">
            <tr>
              <th className="px-4 py-2 font-medium">{t('runs.view.stop')}</th>
              <th className="px-4 py-2 font-medium">{t('orders.columns.number')}</th>
              <th className="px-4 py-2 font-medium">{t('orders.columns.customer')}</th>
              <th className="px-4 py-2 font-medium">{t('orders.columns.remito')}</th>
              <th className="px-4 py-2 font-medium">{t('orders.columns.status')}</th>
              <th className="px-4 py-2 text-right font-medium">{t('orders.columns.total')}</th>
            </tr>
          </thead>
          <tbody>
            {stops.map(({ stopNo, order }) => (
              <tr key={order.orderId} className="border-b border-border last:border-0">
                <td className="px-4 py-2 tabular-nums">{stopNo}</td>
                <td className="px-4 py-2">
                  <Link to={`/app/orders/${order.orderId}`} className="font-medium underline-offset-2 hover:underline">
                    {order.orderNumber}
                  </Link>
                </td>
                <td className="px-4 py-2">{order.customerName}</td>
                <td className="px-4 py-2">{order.remitoNumber ?? '—'}</td>
                <td className="px-4 py-2">
                  <OrderStatusBadge status={order.status} />
                </td>
                <td className="px-4 py-2 text-right tabular-nums">{formatMoney(order.deliveredTotal ?? order.total)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {deleting && (
        <ConfirmDialog
          title={t('runs.view.deleteTitle', { number: run.runNumber })}
          message={t('runs.view.deleteMessage', { count: stops.length })}
          confirmLabel={t('runs.view.delete')}
          busyLabel={t('runs.view.deleting')}
          busy={busy}
          destructive
          onConfirm={() => void discard()}
          onCancel={() => setDeleting(false)}
        />
      )}
      {confirming && (
        <ConfirmDialog
          title={t('runs.view.dispatchTitle', { number: run.runNumber })}
          message={t('runs.view.dispatchMessage')}
          confirmLabel={t('runs.view.dispatch')}
          busyLabel={t('runs.view.dispatching')}
          busy={busy}
          onConfirm={() => void dispatch()}
          onCancel={() => setConfirming(false)}
        />
      )}
    </FormPage>
  )
}

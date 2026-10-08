import { useEffect, useMemo, useState, type FormEvent } from 'react'
import { useNavigate, useParams, useSearchParams } from 'react-router'
import { useTranslation } from 'react-i18next'
import { ArrowDown, ArrowUp, Plus, X } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import { FormPage } from '@/components/layout/FormPage'
import { OrderStatusBadge } from '@/components/fulfillment/StatusBadges'
import { fulfillmentErrorMessage, todayIso } from '@/components/fulfillment/fulfillmentErrors'
import {
  createRun,
  getRun,
  listOrders,
  updateRun,
  type DeliveryRunSummary,
  type OrderTrackingSummary,
} from '@/api/fulfillment'
import { formatMoney } from '@/dashboard/format'

/**
 * Creates a delivery run, or edits a planned one: date, driver, vehicle, notes, and its orders in delivery order (add
 * from the orders in progress that are in no other run, remove, move up and down). `?orders=a,b` preselects orders
 * (from the orders list's "Armar reparto").
 */
export function DeliveryRunFormScreen() {
  const { t } = useTranslation('fulfillment')
  const { id } = useParams()
  const [searchParams] = useSearchParams()
  const navigate = useNavigate()
  const editing = id !== undefined

  const [run, setRun] = useState<DeliveryRunSummary | null>(null)
  const [runDate, setRunDate] = useState(todayIso())
  const [driverName, setDriverName] = useState('')
  const [vehicle, setVehicle] = useState('')
  const [notes, setNotes] = useState('')
  const [stops, setStops] = useState<OrderTrackingSummary[]>([])
  const [available, setAvailable] = useState<OrderTrackingSummary[]>([])
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)

  useEffect(() => {
    let cancelled = false
    const preselected = (searchParams.get('orders') ?? '').split(',').filter(Boolean)
    Promise.all([listOrders({ status: 'Active' }), editing ? getRun(id) : Promise.resolve(null)])
      .then(([active, detail]) => {
        if (cancelled) return
        const current = detail?.stops.map((stop) => stop.order) ?? active.filter((order) => preselected.includes(order.orderId))
        setStops(current)
        // Free orders, plus this run's own: one taken out of the run is offered again (it is free once saved).
        setAvailable(
          active.filter(
            (order) =>
              (order.runId === null || (editing && order.runId === id)) &&
              ['Confirmed', 'InPreparation', 'ReadyToDispatch'].includes(order.status),
          ),
        )
        if (detail) {
          setRun(detail.run)
          setRunDate(detail.run.runDate)
          setDriverName(detail.run.driverName ?? '')
          setVehicle(detail.run.vehicle ?? '')
          setNotes(detail.run.notes ?? '')
        }
      })
      .catch((err) => {
        if (!cancelled) setError(fulfillmentErrorMessage(err, t, 'errors.unexpectedLoad'))
      })
    return () => {
      cancelled = true
    }
  }, [editing, id, searchParams, t])

  const offered = useMemo(
    () => available.filter((order) => !stops.some((stop) => stop.orderId === order.orderId)),
    [available, stops],
  )
  const total = stops.reduce((sum, order) => sum + order.total, 0)

  const move = (index: number, delta: number) =>
    setStops((current) => {
      const next = [...current]
      const [item] = next.splice(index, 1)
      next.splice(index + delta, 0, item)
      return next
    })

  const back = () => navigate(editing ? `/app/deliveries/${id}` : '/app/deliveries')

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    setSaving(true)
    setError(null)
    const request = {
      runDate,
      driverName: driverName.trim() || null,
      vehicle: vehicle.trim() || null,
      notes: notes.trim() || null,
      orderIds: stops.map((order) => order.orderId),
    }
    try {
      if (editing) {
        await updateRun(id, request)
        navigate(`/app/deliveries/${id}`)
      } else {
        const created = await createRun(request)
        navigate(`/app/deliveries/${created.runId}`)
      }
    } catch (err) {
      setError(fulfillmentErrorMessage(err, t))
    } finally {
      setSaving(false)
    }
  }

  return (
    <FormPage
      title={editing ? t('runs.form.editTitle', { number: run?.runNumber ?? '' }) : t('runs.form.createTitle')}
      description={t('runs.form.description')}
      onBack={back}
      backLabel={t('runs.form.back')}
    >
      <form className="flex flex-col gap-6" onSubmit={submit} noValidate>
        <div className="grid max-w-3xl gap-4 sm:grid-cols-3">
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="runDate">{t('runs.form.date')}</Label>
            <Input id="runDate" type="date" required value={runDate} onChange={(e) => setRunDate(e.target.value)} />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="driverName">{t('runs.form.driver')}</Label>
            <Input id="driverName" maxLength={120} value={driverName} onChange={(e) => setDriverName(e.target.value)} />
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="vehicle">{t('runs.form.vehicle')}</Label>
            <Input id="vehicle" maxLength={120} value={vehicle} onChange={(e) => setVehicle(e.target.value)} />
          </div>
          <div className="flex flex-col gap-1.5 sm:col-span-3">
            <Label htmlFor="notes">{t('runs.form.notes')}</Label>
            <Textarea id="notes" maxLength={500} rows={2} value={notes} onChange={(e) => setNotes(e.target.value)} />
          </div>
        </div>

        <div className="grid gap-4 lg:grid-cols-2">
          <div className="flex flex-col gap-2 rounded-lg border border-border bg-card p-4">
            <div className="flex items-baseline justify-between gap-2">
              <h2 className="font-semibold">{t('runs.form.orders')}</h2>
              <span className="text-sm font-medium tabular-nums">{formatMoney(total)}</span>
            </div>
            <p className="text-xs text-muted-foreground">{t('runs.form.ordersHint')}</p>
            {stops.length === 0 && <p className="py-4 text-sm text-muted-foreground">{t('runs.form.none')}</p>}
            <ol className="flex flex-col gap-2" aria-label={t('runs.form.orders')}>
              {stops.map((order, index) => (
                <li key={order.orderId} className="flex items-center gap-2 rounded-md border border-border px-3 py-2 text-sm">
                  <span className="w-6 text-right font-semibold tabular-nums">{index + 1}</span>
                  <span className="min-w-0 flex-1">
                    <span className="font-medium">{order.orderNumber}</span> · {order.customerName}
                    <span className="block text-xs text-muted-foreground">{formatMoney(order.total)}</span>
                  </span>
                  <Button type="button" variant="outline" size="sm" aria-label={`${t('runs.form.up')} ${order.orderNumber}`} disabled={index === 0} onClick={() => move(index, -1)}>
                    <ArrowUp className="size-3.5" aria-hidden="true" />
                  </Button>
                  <Button type="button" variant="outline" size="sm" aria-label={`${t('runs.form.down')} ${order.orderNumber}`} disabled={index === stops.length - 1} onClick={() => move(index, 1)}>
                    <ArrowDown className="size-3.5" aria-hidden="true" />
                  </Button>
                  <Button type="button" variant="outline" size="sm" aria-label={`${t('runs.form.remove')} ${order.orderNumber}`} onClick={() => setStops((current) => current.filter((item) => item.orderId !== order.orderId))}>
                    <X className="size-3.5" aria-hidden="true" />
                  </Button>
                </li>
              ))}
            </ol>
          </div>

          <div className="flex flex-col gap-2 rounded-lg border border-border bg-card p-4">
            <h2 className="font-semibold">{t('runs.form.available')}</h2>
            {offered.length === 0 && <p className="py-4 text-sm text-muted-foreground">{t('runs.form.noAvailable')}</p>}
            <ul className="flex max-h-[28rem] flex-col gap-2 overflow-y-auto" aria-label={t('runs.form.available')}>
              {offered.map((order) => (
                <li key={order.orderId} className="flex items-center gap-2 rounded-md border border-border px-3 py-2 text-sm">
                  <span className="min-w-0 flex-1">
                    <span className="font-medium">{order.orderNumber}</span> · {order.customerName}
                    <span className="mt-0.5 flex items-center gap-2 text-xs text-muted-foreground">
                      <OrderStatusBadge status={order.status} /> {formatMoney(order.total)}
                    </span>
                  </span>
                  <Button type="button" size="sm" aria-label={`${t('runs.form.add')} ${order.orderNumber}`} onClick={() => setStops((current) => [...current, order])}>
                    <Plus className="size-3.5" aria-hidden="true" /> {t('runs.form.add')}
                  </Button>
                </li>
              ))}
            </ul>
          </div>
        </div>

        {error && (
          <p role="alert" className="text-sm text-destructive">
            {error}
          </p>
        )}
        <div className="flex gap-2">
          <Button type="submit" disabled={saving || runDate === ''}>
            {saving ? t('runs.form.saving') : t('runs.form.save')}
          </Button>
          <Button type="button" variant="outline" onClick={back} disabled={saving}>
            {t('runs.form.cancel')}
          </Button>
        </div>
      </form>
    </FormPage>
  )
}

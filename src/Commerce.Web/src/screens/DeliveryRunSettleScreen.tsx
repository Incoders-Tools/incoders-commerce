import { useEffect, useState, type FormEvent } from 'react'
import { useNavigate, useParams } from 'react-router'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { FormPage } from '@/components/layout/FormPage'
import { fulfillmentErrorMessage } from '@/components/fulfillment/fulfillmentErrors'
import {
  getOrder,
  getRun,
  settleRun,
  type DeliveryRunSummary,
  type OrderSettlement,
  type OrderTrackingDetail,
} from '@/api/fulfillment'
import { formatMoney } from '@/dashboard/format'
import { useNumberFormat } from '@/organization/NumberFormatContext'

interface OrderReturn {
  detail: OrderTrackingDetail
  delivered: boolean
  settlement: OrderSettlement
  /** What the operator typed per line (kilos may differ from the order); starts as the ordered quantity. */
  quantities: Record<number, string>
}

/** The value of a delivered line, rounded once to cents like the server. */
const amount = (unitNetPrice: number, quantity: number) => Math.round(unitNetPrice * quantity * 100) / 100

/**
 * Settling a run when the truck returns: for each order, delivered or not; if delivered, the real quantity of each
 * line (weighted cuts are re-weighed) and whether it goes to the customer's current account or was paid on delivery
 * (a guest always pays on delivery). Confirming moves the stock and the current accounts in one go on the server.
 */
export function DeliveryRunSettleScreen() {
  const { t } = useTranslation('fulfillment')
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const numberFormat = useNumberFormat()
  const [run, setRun] = useState<DeliveryRunSummary | null>(null)
  const [returns, setReturns] = useState<OrderReturn[]>([])
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)

  useEffect(() => {
    let cancelled = false
    getRun(id)
      .then(async (detail) => {
        const orders = await Promise.all(detail.stops.map((stop) => getOrder(stop.order.orderId)))
        if (cancelled) return
        setRun(detail.run)
        setReturns(
          orders.map((order) => ({
            detail: order,
            delivered: true,
            settlement: order.summary.customerId === null ? 'PaidOnDelivery' : 'CurrentAccount',
            quantities: Object.fromEntries(order.lines.map((line) => [line.lineNo, numberFormat.formatNumber(line.quantity)])),
          })),
        )
      })
      .catch((err) => {
        if (!cancelled) setError(fulfillmentErrorMessage(err, t, 'errors.unexpectedLoad'))
      })
    return () => {
      cancelled = true
    }
  }, [id, t, numberFormat])

  const update = (orderId: string, change: (current: OrderReturn) => OrderReturn) =>
    setReturns((current) => current.map((item) => (item.detail.summary.orderId === orderId ? change(item) : item)))

  const parsedQuantity = (item: OrderReturn, lineNo: number) => numberFormat.parse(item.quantities[lineNo] ?? '')
  const orderTotal = (item: OrderReturn) =>
    item.delivered
      ? item.detail.lines.reduce((sum, line) => sum + amount(line.unitNetPrice, parsedQuantity(item, line.lineNo) ?? 0), 0)
      : 0
  const invalid = returns.some(
    (item) => item.delivered && item.detail.lines.some((line) => {
      const value = parsedQuantity(item, line.lineNo)
      return value === null || value < 0
    }),
  )

  const back = () => navigate(`/app/deliveries/${id}`)

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    setSaving(true)
    setError(null)
    try {
      await settleRun(
        id,
        returns.map((item) => ({
          orderId: item.detail.summary.orderId,
          delivered: item.delivered,
          settlement: item.delivered ? item.settlement : null,
          lines: item.delivered
            ? item.detail.lines.map((line) => ({ lineNo: line.lineNo, deliveredQuantity: parsedQuantity(item, line.lineNo) ?? 0 }))
            : null,
        })),
      )
      navigate(`/app/deliveries/${id}`)
    } catch (err) {
      setError(fulfillmentErrorMessage(err, t))
    } finally {
      setSaving(false)
    }
  }

  return (
    <FormPage
      title={t('runs.settle.title', { number: run?.runNumber ?? '' })}
      description={t('runs.settle.description')}
      onBack={back}
      backLabel={t('runs.settle.back')}
    >
      <form className="flex flex-col gap-4" onSubmit={submit} noValidate>
        {returns.map((item) => {
          const { summary, lines } = item.detail
          const guest = summary.customerId === null
          return (
            <fieldset key={summary.orderId} className="flex flex-col gap-3 rounded-lg border border-border bg-card p-4">
              <legend className="px-1 text-sm font-semibold">
                {summary.orderNumber} · {summary.customerName}
                {summary.remitoNumber && <span className="font-normal text-muted-foreground"> · {summary.remitoNumber}</span>}
              </legend>
              <div className="flex flex-wrap items-center gap-4 text-sm">
                <label className="inline-flex items-center gap-2">
                  <input
                    type="radio"
                    name={`delivered-${summary.orderId}`}
                    className="accent-primary"
                    checked={item.delivered}
                    onChange={() => update(summary.orderId, (current) => ({ ...current, delivered: true }))}
                  />
                  {t('runs.settle.delivered')}
                </label>
                <label className="inline-flex items-center gap-2">
                  <input
                    type="radio"
                    name={`delivered-${summary.orderId}`}
                    className="accent-primary"
                    checked={!item.delivered}
                    onChange={() => update(summary.orderId, (current) => ({ ...current, delivered: false }))}
                  />
                  {t('runs.settle.notDelivered')}
                </label>
                {item.delivered && (
                  <label className="ml-auto inline-flex items-center gap-2">
                    {t('runs.settle.settlement')}
                    <Select
                      className="w-auto"
                      aria-label={`${t('runs.settle.settlement')} ${summary.orderNumber}`}
                      value={item.settlement}
                      disabled={guest}
                      onChange={(e) =>
                        update(summary.orderId, (current) => ({ ...current, settlement: e.target.value as OrderSettlement }))
                      }
                    >
                      <option value="CurrentAccount">{t('settlement.CurrentAccount')}</option>
                      <option value="PaidOnDelivery">{t('settlement.PaidOnDelivery')}</option>
                    </Select>
                  </label>
                )}
              </div>
              {guest && item.delivered && <p className="text-xs text-muted-foreground">{t('runs.settle.guestNote')}</p>}
              {item.delivered && (
                <table className="w-full text-sm">
                  <thead className="text-left text-muted-foreground">
                    <tr>
                      <th className="py-1 font-medium">{t('orders.detail.product')}</th>
                      <th className="py-1 text-right font-medium">{t('runs.settle.ordered')}</th>
                      <th className="py-1 text-right font-medium">{t('runs.settle.deliveredQuantity')}</th>
                      <th className="py-1 text-right font-medium">{t('orders.detail.subtotal')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {lines.map((line) => {
                      const value = parsedQuantity(item, line.lineNo)
                      return (
                        <tr key={line.lineNo}>
                          <td className="py-1">
                            {line.productName} <span className="text-muted-foreground">{line.presentationName}</span>
                          </td>
                          <td className="py-1 text-right tabular-nums">
                            {numberFormat.formatNumber(line.quantity)}
                            {line.quantityBehavior === 'FixedQuantity' ? '' : ' kg'}
                          </td>
                          <td className="py-1 text-right">
                            <Input
                              inputMode="decimal"
                              className="ml-auto w-28 text-right"
                              aria-label={`${t('runs.settle.deliveredQuantity')} ${line.productName} ${summary.orderNumber}`}
                              aria-invalid={value === null || value < 0}
                              value={item.quantities[line.lineNo] ?? ''}
                              onChange={(e) =>
                                update(summary.orderId, (current) => ({
                                  ...current,
                                  quantities: { ...current.quantities, [line.lineNo]: e.target.value },
                                }))
                              }
                            />
                          </td>
                          <td className="py-1 text-right tabular-nums">{formatMoney(amount(line.unitNetPrice, value ?? 0))}</td>
                        </tr>
                      )
                    })}
                  </tbody>
                </table>
              )}
              {item.delivered && (
                <p className="text-right text-sm font-semibold">{t('runs.settle.totals', { total: formatMoney(orderTotal(item)) })}</p>
              )}
            </fieldset>
          )
        })}

        {error && (
          <p role="alert" className="text-sm text-destructive">
            {error}
          </p>
        )}
        <div className="flex items-center gap-2">
          <Button type="submit" disabled={saving || invalid || returns.length === 0}>
            {saving ? t('runs.settle.confirming') : t('runs.settle.confirm')}
          </Button>
          <Button type="button" variant="outline" onClick={back} disabled={saving}>
            {t('runs.form.cancel')}
          </Button>
          <span className="ml-auto text-sm font-semibold">
            {t('runs.settle.totals', { total: formatMoney(returns.reduce((sum, item) => sum + orderTotal(item), 0)) })}
          </span>
        </div>
      </form>
    </FormPage>
  )
}

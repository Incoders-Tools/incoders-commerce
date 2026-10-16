import { Trans, useTranslation } from 'react-i18next'
import { OrderSubmissionOutcomeStatus, type OrderSubmissionOutcome } from '@/api/types'
import { parseOrderNumber } from '@/lib/orderNumber'

/**
 * The result line of an order submission (`data-testid="order-outcome"`). An accepted order shows its
 * human number ("Pedido P01-W-37 recibido") with a tooltip that explains each part of it, the way the POS
 * explains a sale number; the order id (a GUID) is never shown. An order without a readable number
 * (stored before orders were numbered) falls back to the plain accepted message.
 */
export function OrderOutcomeMessage({ outcome }: { outcome: OrderSubmissionOutcome }) {
  const { t } = useTranslation('orders')

  return (
    <p data-testid="order-outcome" className="text-sm text-neutral-700">
      {outcome.status === OrderSubmissionOutcomeStatus.Accepted ? (
        <AcceptedMessage orderNumber={outcome.order?.orderNumber} />
      ) : (
        t('placeOrder.outcome.denied', { reason: outcome.reason })
      )}
    </p>
  )
}

function AcceptedMessage({ orderNumber }: { orderNumber: string | null | undefined }) {
  const { t } = useTranslation('orders')
  const parts = parseOrderNumber(orderNumber)
  if (!parts) return <>{t('placeOrder.outcome.accepted')}</>

  return (
    <Trans
      t={t}
      i18nKey="placeOrder.outcome.received"
      values={{ number: orderNumber }}
      components={{ number: <span className="font-mono" title={t('placeOrder.outcome.numberHint', { ...parts })} /> }}
    />
  )
}

import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { submitOrder } from '@/api/orders'
import { ApiError } from '@/api/client'
import { OrderSubmissionOutcomeStatus, type OrderSubmissionOutcome } from '@/api/types'

/**
 * The pre-existing staff-operated order form (raw customerId/
 * accessCredential/destinationBranchId/actorId fields), extracted verbatim
 * out of `OrderScreen.tsx` (commerce-guest-ordering design.md "One screen,
 * guest and registered as peers" / tasks.md 6.8's regression guard: this
 * submission path — `POST /orders/` via `submitOrder` — is UNCHANGED
 * behaviorally). `OrderScreen.tsx` is now the public-facing guest/
 * registered peer screen; this component stays a separate, clearly-scoped
 * internal admin variant for staff who need to submit an order on a
 * customer's behalf with their ordering-access credential.
 */
export function StaffOrderScreen() {
  const { t } = useTranslation('orders')
  const [customerId, setCustomerId] = useState('')
  const [accessCredential, setAccessCredential] = useState('')
  const [destinationBranchId, setDestinationBranchId] = useState('')
  const [actorId, setActorId] = useState('')
  const [productId, setProductId] = useState('')
  const [presentationId, setPresentationId] = useState('')
  const [quantity, setQuantity] = useState('1')
  const [outcome, setOutcome] = useState<OrderSubmissionOutcome | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault()
    setError(null)
    setOutcome(null)
    setSubmitting(true)
    try {
      const result = await submitOrder({
        orderId: crypto.randomUUID(),
        customerId,
        accessCredential,
        destinationBranchId,
        actorId,
        lines: [
          {
            productId,
            presentationId,
            quantity: Number(quantity),
          },
        ],
        correlationId: crypto.randomUUID(),
      })
      setOutcome(result)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : t('staffOrder.errors.unexpectedSubmit'))
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Card className="mx-auto mt-8 w-full max-w-lg">
      <CardHeader>
        <CardTitle>{t('staffOrder.title')}</CardTitle>
      </CardHeader>
      <CardContent>
        <form className="flex flex-col gap-4" onSubmit={handleSubmit}>
          <Field id="customerId" label={t('staffOrder.customerIdLabel')} value={customerId} onChange={setCustomerId} />
          <Field id="accessCredential" label={t('staffOrder.accessCredentialLabel')} value={accessCredential} onChange={setAccessCredential} />
          <Field id="destinationBranchId" label={t('staffOrder.destinationBranchIdLabel')} value={destinationBranchId} onChange={setDestinationBranchId} />
          <Field id="actorId" label={t('staffOrder.actorIdLabel')} value={actorId} onChange={setActorId} />
          <Field id="productId" label={t('staffOrder.productIdLabel')} value={productId} onChange={setProductId} />
          <Field id="presentationId" label={t('staffOrder.presentationIdLabel')} value={presentationId} onChange={setPresentationId} />
          <Field id="quantity" label={t('staffOrder.quantityLabel')} value={quantity} onChange={setQuantity} type="number" />

          {error && (
            <p role="alert" className="text-sm text-red-600">
              {error}
            </p>
          )}
          {outcome && (
            <p data-testid="order-outcome" className="text-sm text-neutral-700">
              {outcome.status === OrderSubmissionOutcomeStatus.Accepted
                ? t('staffOrder.outcome.accepted')
                : t('staffOrder.outcome.denied', { reason: outcome.reason })}
            </p>
          )}
          {outcome?.status === OrderSubmissionOutcomeStatus.Accepted && outcome.order?.lines && (
            <p data-testid="order-total" className="text-sm text-neutral-700">
              {t('staffOrder.total', {
                total: outcome.order.lines.reduce((sum, line) => sum + line.lineTotal, 0).toFixed(2),
              })}
            </p>
          )}

          <Button type="submit" disabled={submitting}>
            {submitting ? t('staffOrder.submitting') : t('staffOrder.submit')}
          </Button>
        </form>
      </CardContent>
    </Card>
  )
}

function Field({
  id,
  label,
  value,
  onChange,
  type = 'text',
}: {
  id: string
  label: string
  value: string
  onChange: (value: string) => void
  type?: string
}) {
  return (
    <div className="flex flex-col gap-1.5">
      <Label htmlFor={id}>{label}</Label>
      <Input id={id} type={type} value={value} onChange={(e) => onChange(e.target.value)} required />
    </div>
  )
}

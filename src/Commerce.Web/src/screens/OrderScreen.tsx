import { useState, type FormEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { submitOrder } from '@/api/orders'
import { ApiError } from '@/api/client'
import type { OrderSubmissionOutcome } from '@/api/types'

export function OrderScreen() {
  const [customerId, setCustomerId] = useState('')
  const [accessCredential, setAccessCredential] = useState('')
  const [destinationBranchId, setDestinationBranchId] = useState('')
  const [actorId, setActorId] = useState('')
  const [productId, setProductId] = useState('')
  const [productName, setProductName] = useState('')
  const [presentationId, setPresentationId] = useState('')
  const [presentationName, setPresentationName] = useState('')
  const [unitId, setUnitId] = useState('')
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
        accessEnabled: true,
        destinationBranchId,
        actorId,
        lines: [
          {
            productId,
            productName,
            presentationId,
            presentationName,
            quantityBehavior: 0,
            unitId,
            quantity: Number(quantity),
          },
        ],
        correlationId: crypto.randomUUID(),
      })
      setOutcome(result)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Unexpected error submitting order.')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Card className="mx-auto mt-8 w-full max-w-lg">
      <CardHeader>
        <CardTitle>Submit order</CardTitle>
      </CardHeader>
      <CardContent>
        <form className="flex flex-col gap-4" onSubmit={handleSubmit}>
          <Field id="customerId" label="Customer ID" value={customerId} onChange={setCustomerId} />
          <Field id="accessCredential" label="Access credential" value={accessCredential} onChange={setAccessCredential} />
          <Field id="destinationBranchId" label="Destination branch ID" value={destinationBranchId} onChange={setDestinationBranchId} />
          <Field id="actorId" label="Actor ID" value={actorId} onChange={setActorId} />
          <Field id="productId" label="Product ID" value={productId} onChange={setProductId} />
          <Field id="productName" label="Product name" value={productName} onChange={setProductName} />
          <Field id="presentationId" label="Presentation ID" value={presentationId} onChange={setPresentationId} />
          <Field id="presentationName" label="Presentation name" value={presentationName} onChange={setPresentationName} />
          <Field id="unitId" label="Unit ID" value={unitId} onChange={setUnitId} />
          <Field id="quantity" label="Quantity" value={quantity} onChange={setQuantity} type="number" />

          {error && (
            <p role="alert" className="text-sm text-red-600">
              {error}
            </p>
          )}
          {outcome && (
            <p data-testid="order-outcome" className="text-sm text-neutral-700">
              {outcome.status === 'Accepted' ? 'Order accepted.' : `Denied: ${outcome.reason}`}
            </p>
          )}

          <Button type="submit" disabled={submitting}>
            {submitting ? 'Submitting…' : 'Submit order'}
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

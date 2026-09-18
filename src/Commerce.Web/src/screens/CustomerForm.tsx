import { useState, type FormEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { createCustomer, updateCustomer } from '@/api/customers'
import { ApiError } from '@/api/client'
import { CustomerKind, TaxCondition, TaxIdType, type CustomerRecord } from '@/api/types'

interface CustomerFormProps {
  customer?: CustomerRecord
  onSaved: () => void
  onCancel: () => void
}

/**
 * One `CustomerForm` component, two modes (design.md "Web form shape (create
 * vs. edit)"). `CustomerKind` is a required select at create and READ-ONLY at
 * edit — it drives which price list applies later (Phase C), so changing it
 * retroactively is a deliberate future operation, not a field edit.
 * `DisplayName` is the only other required field (the spec's "Retail
 * customer with only DisplayName and Phone" scenario forbids requiring
 * fiscal or address data). `IsEnabled` is a toggle on edit only; a created
 * customer is always enabled.
 */
export function CustomerForm({ customer, onSaved, onCancel }: CustomerFormProps) {
  const isEdit = customer !== undefined

  const [customerKind, setCustomerKind] = useState<CustomerKind>(customer?.customerKind ?? CustomerKind.Retail)
  const [displayName, setDisplayName] = useState(customer?.displayName ?? '')
  const [legalName, setLegalName] = useState(customer?.legalName ?? '')
  const [taxIdType, setTaxIdType] = useState<TaxIdType>(customer?.taxIdType ?? TaxIdType.None)
  const [taxId, setTaxId] = useState(customer?.taxId ?? '')
  const [taxCondition, setTaxCondition] = useState<TaxCondition>(customer?.taxCondition ?? TaxCondition.NoAplica)
  const [phone, setPhone] = useState(customer?.phone ?? '')
  const [email, setEmail] = useState(customer?.email ?? '')
  const [addressStreet, setAddressStreet] = useState(customer?.addressStreet ?? '')
  const [addressNumber, setAddressNumber] = useState(customer?.addressNumber ?? '')
  const [neighborhood, setNeighborhood] = useState(customer?.neighborhood ?? '')
  const [locality, setLocality] = useState(customer?.locality ?? '')
  const [province, setProvince] = useState(customer?.province ?? '')
  const [postalCode, setPostalCode] = useState(customer?.postalCode ?? '')
  const [deliveryNotes, setDeliveryNotes] = useState(customer?.deliveryNotes ?? '')
  const [discountPercentage, setDiscountPercentage] = useState(
    customer?.discountPercentage !== null && customer?.discountPercentage !== undefined
      ? String(customer.discountPercentage)
      : '',
  )
  const [paymentTerms, setPaymentTerms] = useState(customer?.paymentTerms ?? '')
  const [notes, setNotes] = useState(customer?.notes ?? '')
  const [isEnabled, setIsEnabled] = useState(customer?.isEnabled ?? true)
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault()
    setError(null)
    setSubmitting(true)
    try {
      const shared = {
        displayName,
        legalName: legalName || null,
        taxIdType,
        taxId: taxIdType === TaxIdType.None ? null : taxId,
        taxCondition,
        phone: phone || null,
        email: email || null,
        addressStreet: addressStreet || null,
        addressNumber: addressNumber || null,
        neighborhood: neighborhood || null,
        locality: locality || null,
        province: province || null,
        postalCode: postalCode || null,
        deliveryNotes: deliveryNotes || null,
        discountPercentage: discountPercentage === '' ? null : Number(discountPercentage),
        paymentTerms: paymentTerms || null,
        notes: notes || null,
      }

      if (isEdit) {
        await updateCustomer(customer.id, { ...shared, isEnabled })
      } else {
        await createCustomer({ ...shared, customerKind })
      }

      onSaved()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Unexpected error saving customer.')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Card className="mx-auto mt-8 w-full max-w-lg">
      <CardHeader>
        <CardTitle>{isEdit ? 'Edit customer' : 'New customer'}</CardTitle>
      </CardHeader>
      <CardContent>
        <form className="flex flex-col gap-4" onSubmit={handleSubmit}>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="customerKind">Customer kind</Label>
            <select
              id="customerKind"
              value={customerKind}
              disabled={isEdit}
              onChange={(e) => setCustomerKind(e.target.value as CustomerKind)}
              className="flex h-9 w-full rounded-md border border-neutral-300 bg-white px-3 py-1 text-sm shadow-sm disabled:cursor-not-allowed disabled:opacity-50"
            >
              <option value={CustomerKind.Retail}>Retail</option>
              <option value={CustomerKind.Wholesale}>Wholesale</option>
            </select>
          </div>

          <Field id="displayName" label="Display name" value={displayName} onChange={setDisplayName} required />
          <Field id="legalName" label="Legal name" value={legalName} onChange={setLegalName} />

          <div className="flex flex-col gap-1.5">
            <Label htmlFor="taxIdType">Tax ID type</Label>
            <select
              id="taxIdType"
              value={taxIdType}
              onChange={(e) => setTaxIdType(e.target.value as TaxIdType)}
              className="flex h-9 w-full rounded-md border border-neutral-300 bg-white px-3 py-1 text-sm shadow-sm"
            >
              <option value={TaxIdType.None}>None</option>
              <option value={TaxIdType.Cuit}>CUIT</option>
              <option value={TaxIdType.Cuil}>CUIL</option>
            </select>
          </div>
          {taxIdType !== TaxIdType.None && (
            <Field id="taxId" label="Tax ID" value={taxId} onChange={setTaxId} required />
          )}

          <div className="flex flex-col gap-1.5">
            <Label htmlFor="taxCondition">Tax condition</Label>
            <select
              id="taxCondition"
              value={taxCondition}
              onChange={(e) => setTaxCondition(e.target.value as TaxCondition)}
              className="flex h-9 w-full rounded-md border border-neutral-300 bg-white px-3 py-1 text-sm shadow-sm"
            >
              <option value={TaxCondition.ConsumidorFinal}>Consumidor Final</option>
              <option value={TaxCondition.ResponsableInscripto}>Responsable Inscripto</option>
              <option value={TaxCondition.Monotributo}>Monotributo</option>
              <option value={TaxCondition.Exento}>Exento</option>
              <option value={TaxCondition.NoAplica}>No aplica</option>
            </select>
          </div>

          <Field id="phone" label="Phone" value={phone} onChange={setPhone} />
          <Field id="email" label="Email" value={email} onChange={setEmail} />
          <Field id="addressStreet" label="Address street" value={addressStreet} onChange={setAddressStreet} />
          <Field id="addressNumber" label="Address number" value={addressNumber} onChange={setAddressNumber} />
          <Field id="neighborhood" label="Neighborhood" value={neighborhood} onChange={setNeighborhood} />
          <Field id="locality" label="Locality" value={locality} onChange={setLocality} />
          <Field id="province" label="Province" value={province} onChange={setProvince} />
          <Field id="postalCode" label="Postal code" value={postalCode} onChange={setPostalCode} />
          <Field id="deliveryNotes" label="Delivery notes" value={deliveryNotes} onChange={setDeliveryNotes} />
          <Field
            id="discountPercentage"
            label="Discount percentage"
            value={discountPercentage}
            onChange={setDiscountPercentage}
            type="number"
          />
          <Field id="paymentTerms" label="Payment terms" value={paymentTerms} onChange={setPaymentTerms} />
          <Field id="notes" label="Notes" value={notes} onChange={setNotes} />

          {isEdit && (
            <div className="flex items-center gap-2">
              <input
                id="isEnabled"
                type="checkbox"
                checked={isEnabled}
                onChange={(e) => setIsEnabled(e.target.checked)}
              />
              <Label htmlFor="isEnabled">Enabled</Label>
            </div>
          )}

          {error && (
            <p role="alert" className="text-sm text-red-600">
              {error}
            </p>
          )}

          <div className="flex gap-2">
            <Button type="submit" disabled={submitting}>
              {submitting ? 'Saving…' : 'Save'}
            </Button>
            <Button type="button" variant="outline" onClick={onCancel} disabled={submitting}>
              Cancel
            </Button>
          </div>
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
  required = false,
}: {
  id: string
  label: string
  value: string
  onChange: (value: string) => void
  type?: string
  required?: boolean
}) {
  return (
    <div className="flex flex-col gap-1.5">
      <Label htmlFor={id}>{label}</Label>
      <Input id={id} type={type} value={value} onChange={(e) => onChange(e.target.value)} required={required} />
    </div>
  )
}

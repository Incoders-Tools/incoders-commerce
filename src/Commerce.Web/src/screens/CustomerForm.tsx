import { useState, type FormEvent, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { Label } from '@/components/ui/label'
import { FormPage } from '@/components/layout/FormPage'
import { createCustomer, updateCustomer } from '@/api/customers'
import { ApiError } from '@/api/client'
import { CustomerKind, TaxCondition, TaxIdType, type CustomerRecord } from '@/api/types'
import { cn } from '@/lib/utils'

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
 *
 * T9: rendered on the shared `FormPage` shell instead of a centered `Card`.
 * `onCancel` also backs the header's back action, alongside the existing
 * Cancel button in the footer.
 *
 * T10: laid out as titled sections on a responsive grid instead of a single
 * `max-w-lg` column, so the ~20 fields actually use the full-screen shell.
 * Section headings are purely visual grouping — they never change a field's
 * accessible name, which stays the `Label htmlFor` text alone.
 */
export function CustomerForm({ customer, onSaved, onCancel }: CustomerFormProps) {
  const { t } = useTranslation('customers')
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
      setError(err instanceof ApiError ? err.message : t('errors.unexpectedSave'))
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <FormPage
      title={isEdit ? t('form.titleEdit') : t('form.titleNew')}
      onBack={onCancel}
      backLabel={t('form.backLabel')}
    >
      <form className="flex flex-col gap-8" onSubmit={handleSubmit}>
        <FormSection title={t('form.sections.identity')}>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="customerKind">{t('form.fields.customerKind')}</Label>
            <Select
              id="customerKind"
              value={customerKind}
              disabled={isEdit}
              onChange={(e) => setCustomerKind(e.target.value as CustomerKind)}
            >
              <option value={CustomerKind.Retail}>{t('kindOptions.retail')}</option>
              <option value={CustomerKind.Wholesale}>{t('kindOptions.wholesale')}</option>
            </Select>
          </div>

          <Field id="displayName" label={t('form.fields.displayName')} value={displayName} onChange={setDisplayName} required />
          <Field id="legalName" label={t('form.fields.legalName')} value={legalName} onChange={setLegalName} />
        </FormSection>

        <FormSection title={t('form.sections.tax')}>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="taxIdType">{t('form.fields.taxIdType')}</Label>
            <Select id="taxIdType" value={taxIdType} onChange={(e) => setTaxIdType(e.target.value as TaxIdType)}>
              <option value={TaxIdType.None}>{t('form.taxIdTypeOptions.none')}</option>
              <option value={TaxIdType.Cuit}>{t('form.taxIdTypeOptions.cuit')}</option>
              <option value={TaxIdType.Cuil}>{t('form.taxIdTypeOptions.cuil')}</option>
            </Select>
          </div>
          {taxIdType !== TaxIdType.None && (
            <Field id="taxId" label={t('form.fields.taxId')} value={taxId} onChange={setTaxId} required />
          )}

          <div className="flex flex-col gap-1.5">
            <Label htmlFor="taxCondition">{t('form.fields.taxCondition')}</Label>
            <Select
              id="taxCondition"
              value={taxCondition}
              onChange={(e) => setTaxCondition(e.target.value as TaxCondition)}
            >
              {/* Argentine AFIP tax-condition category names — already
                  Spanish, and are the canonical labels, so they are not
                  routed through i18n like the rest of this form. */}
              <option value={TaxCondition.ConsumidorFinal}>Consumidor Final</option>
              <option value={TaxCondition.ResponsableInscripto}>Responsable Inscripto</option>
              <option value={TaxCondition.Monotributo}>Monotributo</option>
              <option value={TaxCondition.Exento}>Exento</option>
              <option value={TaxCondition.NoAplica}>No aplica</option>
            </Select>
          </div>
        </FormSection>

        <FormSection title={t('form.sections.contact')}>
          <Field id="phone" label={t('form.fields.phone')} value={phone} onChange={setPhone} />
          <Field id="email" label={t('form.fields.email')} value={email} onChange={setEmail} />
        </FormSection>

        <FormSection title={t('form.sections.address')}>
          <Field id="addressStreet" label={t('form.fields.addressStreet')} value={addressStreet} onChange={setAddressStreet} />
          <Field id="addressNumber" label={t('form.fields.addressNumber')} value={addressNumber} onChange={setAddressNumber} />
          <Field id="neighborhood" label={t('form.fields.neighborhood')} value={neighborhood} onChange={setNeighborhood} />
          <Field id="locality" label={t('form.fields.locality')} value={locality} onChange={setLocality} />
          <Field id="province" label={t('form.fields.province')} value={province} onChange={setProvince} />
          <Field id="postalCode" label={t('form.fields.postalCode')} value={postalCode} onChange={setPostalCode} />
          <Field
            id="deliveryNotes"
            label={t('form.fields.deliveryNotes')}
            value={deliveryNotes}
            onChange={setDeliveryNotes}
            className="md:col-span-2 xl:col-span-3"
          />
        </FormSection>

        <FormSection title={t('form.sections.commercial')}>
          <Field
            id="discountPercentage"
            label={t('form.fields.discountPercentage')}
            value={discountPercentage}
            onChange={setDiscountPercentage}
            type="number"
          />
          <Field id="paymentTerms" label={t('form.fields.paymentTerms')} value={paymentTerms} onChange={setPaymentTerms} />
          <Field id="notes" label={t('form.fields.notes')} value={notes} onChange={setNotes} className="md:col-span-2 xl:col-span-3" />

          {isEdit && (
            <div className="flex items-center gap-2">
              <input
                id="isEnabled"
                type="checkbox"
                checked={isEnabled}
                onChange={(e) => setIsEnabled(e.target.checked)}
              />
              <Label htmlFor="isEnabled">{t('form.fields.enabled')}</Label>
            </div>
          )}
        </FormSection>

        {error && (
          <p role="alert" className="text-sm text-destructive">
            {error}
          </p>
        )}

        <div className="flex gap-2">
          <Button type="submit" disabled={submitting}>
            {submitting ? t('form.saving') : t('form.save')}
          </Button>
          <Button type="button" variant="outline" onClick={onCancel} disabled={submitting}>
            {t('form.cancel')}
          </Button>
        </div>
      </form>
    </FormPage>
  )
}

function FormSection({ title, children }: { title: string; children: ReactNode }) {
  return (
    <fieldset className="flex flex-col gap-4">
      <legend className="mb-1 text-sm font-semibold text-foreground">{title}</legend>
      <div className="grid grid-cols-1 gap-x-6 gap-y-4 md:grid-cols-2 xl:grid-cols-3">{children}</div>
    </fieldset>
  )
}

function Field({
  id,
  label,
  value,
  onChange,
  type = 'text',
  required = false,
  className,
}: {
  id: string
  label: string
  value: string
  onChange: (value: string) => void
  type?: string
  required?: boolean
  className?: string
}) {
  return (
    <div className={cn('flex flex-col gap-1.5', className)}>
      <Label htmlFor={id}>{label}</Label>
      <Input id={id} type={type} value={value} onChange={(e) => onChange(e.target.value)} required={required} />
    </div>
  )
}

import { useState, type FormEvent, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Select } from '@/components/ui/select'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import { FormPage } from '@/components/layout/FormPage'
import { CityPicker, type CityOption } from '@/components/geo/CityPicker'
import { createCustomer, getCustomer, updateCustomer } from '@/api/customers'
import { ApiError } from '@/api/client'
import {
  CustomerKind,
  TaxCondition,
  TaxIdType,
  type CustomerContactInput,
  type CustomerRecord,
  type MasterDataEntry,
} from '@/api/types'
import { cn } from '@/lib/utils'
import { ContactsEditor, draftsFromContacts, type ContactDraft } from './ContactsEditor'

/** The server's "no value" sentinel for a nullable id on PUT (an omitted id keeps the stored one). */
const NO_ID = '00000000-0000-0000-0000-000000000000'

interface CustomerFormProps {
  customer?: CustomerRecord
  /** Catalog to pick from: all entries; inactive ones are filtered out here. */
  businessTypes?: MasterDataEntry[]
  onSaved: () => void
  onCancel: () => void
  /** Called with the freshly read customer after a modification conflict; the parent re-renders the form with it. */
  onReload?: (customer: CustomerRecord) => void
}

/**
 * Mirrors the server's tax id rule: separators (spaces, dots, hyphens) are
 * ignored, then a DNI needs 7-8 digits and a CUIT/CUIL 11. `None` has no id.
 */
function isValidTaxId(type: TaxIdType, value: string): boolean {
  const digits = value.replace(/[\s.-]/g, '')
  if (type === TaxIdType.None) return true
  if (type === TaxIdType.Dni) return /^\d{7,8}$/.test(digits)
  return /^\d{11}$/.test(digits)
}

/** Active entries, plus the one the customer currently has even when it was deactivated. */
function selectableEntries(
  entries: MasterDataEntry[],
  currentId: string | null | undefined,
  currentName: string | null | undefined,
  inactiveSuffix: string,
): { id: string; name: string }[] {
  const options = entries
    .filter((entry) => entry.isActive || entry.id === currentId)
    .map((entry) => ({ id: entry.id, name: entry.isActive ? entry.name : `${entry.name} ${inactiveSuffix}` }))
  if (currentId && !options.some((option) => option.id === currentId)) {
    options.push({ id: currentId, name: currentName ?? currentId })
  }
  return options
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
 * T10: laid out as titled sections on a responsive grid so the fields use
 * the full-screen shell. Section headings are purely visual grouping — they
 * never change a field's accessible name, which stays the `Label htmlFor`
 * text alone.
 *
 * Customer master data: sections are Datos, Contacto, Ubicación, Fiscal,
 * Comercial and Observaciones. City is a server-searched `CityPicker`; business type is a
 * select over the catalog the parent screen loads (container-presentational). On edit an omitted field would keep the
 * stored value, so clearing sends the empty-id sentinel / empty string.
 */
export function CustomerForm({ customer, businessTypes = [], onSaved, onCancel, onReload }: CustomerFormProps) {
  const { t } = useTranslation('customers')
  const isEdit = customer !== undefined

  const [customerKind, setCustomerKind] = useState<CustomerKind>(customer?.customerKind ?? CustomerKind.Retail)
  const [displayName, setDisplayName] = useState(customer?.displayName ?? '')
  const [legalName, setLegalName] = useState(customer?.legalName ?? '')
  const [contacts, setContacts] = useState<ContactDraft[]>(() => draftsFromContacts(customer?.contacts ?? []))
  const [invalidContactKeys, setInvalidContactKeys] = useState<ReadonlySet<string>>(new Set())
  const [conflict, setConflict] = useState(false)
  const [reloadError, setReloadError] = useState<string | null>(null)
  // Cities are a ~4000-entry core catalog searched on the server by the
  // picker; the form only keeps the chosen one (from the customer's own
  // cityName/provinceName at edit, so showing it costs no request).
  const [city, setCity] = useState<CityOption | null>(
    customer?.cityId && customer.cityName
      ? { id: customer.cityId, name: customer.cityName, provinceName: customer.provinceName ?? '' }
      : null,
  )
  const [businessTypeId, setBusinessTypeId] = useState(customer?.businessTypeId ?? '')
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
  const [taxIdError, setTaxIdError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault()
    setError(null)
    setConflict(false)
    setReloadError(null)
    // Rows left completely blank are dropped; any other row needs a first name.
    const filledContacts = contacts.filter((c) =>
      [c.firstName, c.lastName, c.phone, c.email, c.role].some((value) => value.trim() !== ''),
    )
    const missingFirstName = filledContacts.filter((c) => c.firstName.trim() === '')
    setInvalidContactKeys(new Set(missingFirstName.map((c) => c.key)))
    if (missingFirstName.length > 0) return
    if (!isValidTaxId(taxIdType, taxId)) {
      setTaxIdError(t(taxIdType === TaxIdType.Dni ? 'form.errors.dniInvalid' : 'form.errors.cuitInvalid'))
      return
    }
    setTaxIdError(null)
    setSubmitting(true)
    try {
      const shared = {
        displayName,
        legalName: legalName || null,
        taxIdType,
        taxId: taxIdType === TaxIdType.None ? null : taxId.trim(),
        taxCondition,
        // PUT keeps an omitted value, so clearing sends the sentinel / empty
        // string; on create an empty choice is simply left out.
        ...(isEdit
          ? { cityId: city?.id ?? NO_ID, businessTypeId: businessTypeId || NO_ID }
          : {
              ...(city ? { cityId: city.id } : {}),
              ...(businessTypeId ? { businessTypeId } : {}),
            }),
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

      // Replace-set: sent in screen order, so the position is the sort order.
      const contactInputs: CustomerContactInput[] = filledContacts.map((c, index) => ({
        ...(c.id ? { id: c.id } : {}),
        firstName: c.firstName.trim(),
        lastName: c.lastName.trim() || null,
        phone: c.phone.trim() || null,
        email: c.email.trim() || null,
        role: c.role.trim() || null,
        isPrimary: c.isPrimary,
        sortOrder: index,
      }))

      if (isEdit) {
        // On edit `contacts` is always sent (an empty list clears them) and the
        // `updatedAtUtc` we read guards against overwriting a concurrent change.
        await updateCustomer(customer.id, {
          ...shared,
          contacts: contactInputs,
          isEnabled,
          expectedUpdatedAtUtc: customer.updatedAtUtc,
        })
      } else {
        await createCustomer({ ...shared, ...(contactInputs.length > 0 ? { contacts: contactInputs } : {}), customerKind })
      }

      onSaved()
    } catch (err) {
      if (err instanceof ApiError && err.status === 409 && err.code === 'customer-modified') {
        setConflict(true)
      } else {
        setError(err instanceof ApiError ? err.message : t('errors.unexpectedSave'))
      }
    } finally {
      setSubmitting(false)
    }
  }

  const handleReload = async () => {
    if (!customer) return
    setReloadError(null)
    try {
      onReload?.(await getCustomer(customer.id))
    } catch {
      setReloadError(t('form.conflict.reloadFailed'))
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

        <FormSection title={t('form.sections.contact')}>
          <Field id="phone" label={t('form.fields.phone')} value={phone} onChange={setPhone} />
          <Field id="email" label={t('form.fields.email')} value={email} onChange={setEmail} />
        </FormSection>

        <FormSection title={t('form.sections.contacts')} wide>
          <ContactsEditor contacts={contacts} onChange={setContacts} invalidKeys={invalidContactKeys} />
        </FormSection>

        <FormSection title={t('form.sections.address')}>
          <CityPicker id="cityId" label={t('form.fields.city')} value={city} onChange={setCity} />
          <Field id="locality" label={t('form.fields.locality')} value={locality} onChange={setLocality} />
          <Field id="province" label={t('form.fields.province')} value={province} onChange={setProvince} />
          <Field id="addressStreet" label={t('form.fields.addressStreet')} value={addressStreet} onChange={setAddressStreet} />
          <Field id="addressNumber" label={t('form.fields.addressNumber')} value={addressNumber} onChange={setAddressNumber} />
          <Field id="neighborhood" label={t('form.fields.neighborhood')} value={neighborhood} onChange={setNeighborhood} />
          <Field id="postalCode" label={t('form.fields.postalCode')} value={postalCode} onChange={setPostalCode} />
          <Field
            id="deliveryNotes"
            label={t('form.fields.deliveryNotes')}
            value={deliveryNotes}
            onChange={setDeliveryNotes}
            className="md:col-span-2"
          />
        </FormSection>

        <FormSection title={t('form.sections.tax')}>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="taxIdType">{t('form.fields.taxIdType')}</Label>
            <Select
              id="taxIdType"
              value={taxIdType}
              onChange={(e) => {
                setTaxIdType(e.target.value as TaxIdType)
                setTaxIdError(null)
              }}
            >
              <option value={TaxIdType.None}>{t('form.taxIdTypeOptions.none')}</option>
              <option value={TaxIdType.Dni}>{t('form.taxIdTypeOptions.dni')}</option>
              <option value={TaxIdType.Cuit}>{t('form.taxIdTypeOptions.cuit')}</option>
              <option value={TaxIdType.Cuil}>{t('form.taxIdTypeOptions.cuil')}</option>
            </Select>
          </div>
          {taxIdType !== TaxIdType.None && (
            <Field
              id="taxId"
              label={taxIdType === TaxIdType.Dni ? t('form.fields.dni') : t('form.fields.taxId')}
              value={taxId}
              onChange={(value) => {
                setTaxId(value)
                setTaxIdError(null)
              }}
              required
              error={taxIdError}
            />
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

        <FormSection title={t('form.sections.commercial')}>
          <CatalogSelect
            id="businessTypeId"
            label={t('form.fields.businessType')}
            emptyLabel={t('form.noBusinessType')}
            value={businessTypeId}
            onChange={setBusinessTypeId}
            options={selectableEntries(
              businessTypes,
              customer?.businessTypeId,
              customer?.businessTypeName,
              t('form.inactiveSuffix'),
            )}
          />
          <Field
            id="discountPercentage"
            label={t('form.fields.discountPercentage')}
            value={discountPercentage}
            onChange={setDiscountPercentage}
            type="number"
          />
          <Field id="paymentTerms" label={t('form.fields.paymentTerms')} value={paymentTerms} onChange={setPaymentTerms} />

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

        <FormSection title={t('form.sections.notes')} wide>
          {/* The section legend already names this field visually. */}
          <Label htmlFor="notes" className="sr-only">
            {t('form.fields.notes')}
          </Label>
          <Textarea id="notes" rows={4} value={notes} onChange={(e) => setNotes(e.target.value)} />
        </FormSection>

        {conflict && (
          <div
            role="alert"
            className="flex flex-wrap items-center gap-3 rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive"
          >
            <p>{t('form.conflict.message')}</p>
            <Button type="button" variant="outline" size="sm" onClick={() => void handleReload()}>
              {t('form.conflict.reload')}
            </Button>
          </div>
        )}
        {reloadError && (
          <p role="alert" className="text-sm text-destructive">
            {reloadError}
          </p>
        )}
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

function FormSection({ title, children, wide = false }: { title: string; children: ReactNode; wide?: boolean }) {
  return (
    <fieldset className="flex flex-col gap-4">
      <legend className="mb-1 text-sm font-semibold text-foreground">{title}</legend>
      <div
        className={cn(
          'grid grid-cols-1 gap-x-6 gap-y-4',
          // `wide` sections hold one full-width control (notes) on every breakpoint.
          !wide && 'md:grid-cols-2 xl:grid-cols-3',
        )}
      >
        {children}
      </div>
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
  error = null,
  className,
}: {
  id: string
  label: string
  value: string
  onChange: (value: string) => void
  type?: string
  required?: boolean
  error?: string | null
  className?: string
}) {
  return (
    <div className={cn('flex flex-col gap-1.5', className)}>
      <Label htmlFor={id}>{label}</Label>
      <Input
        id={id}
        type={type}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        required={required}
        aria-invalid={error ? true : undefined}
        aria-describedby={error ? `${id}-error` : undefined}
      />
      {error && (
        <p id={`${id}-error`} className="text-xs text-destructive">
          {error}
        </p>
      )}
    </div>
  )
}

function CatalogSelect({
  id,
  label,
  emptyLabel,
  value,
  onChange,
  options,
}: {
  id: string
  label: string
  emptyLabel: string
  value: string
  onChange: (value: string) => void
  options: { id: string; name: string }[]
}) {
  return (
    <div className="flex flex-col gap-1.5">
      <Label htmlFor={id}>{label}</Label>
      <Select id={id} value={value} onChange={(e) => onChange(e.target.value)}>
        <option value="">{emptyLabel}</option>
        {options.map((option) => (
          <option key={option.id} value={option.id}>
            {option.name}
          </option>
        ))}
      </Select>
    </div>
  )
}

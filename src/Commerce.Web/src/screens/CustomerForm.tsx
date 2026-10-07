import { useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Select } from '@/components/ui/select'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import { FormPage } from '@/components/layout/FormPage'
import { CityPicker, type CityOption } from '@/components/geo/CityPicker'
import { createCustomer, getCustomer, updateCustomer } from '@/api/customers'
import { ApiError } from '@/api/client'
import {
  CustomerKind,
  PartyType,
  TaxCondition,
  TaxIdType,
  type CustomerContactInput,
  type CustomerRecord,
  type GeoProvince,
  type MasterDataEntry,
  type PriceListRecord,
} from '@/api/types'
import { CatalogSelect, Field, FormSection } from '@/components/form/FormParts'
import { EmailField } from '@/components/form/EmailField'
import { selectableEntries } from '@/components/form/selectableEntries'
import { emailStatus } from '@/lib/email'
import { isValidTaxId } from '@/lib/taxId'
import { ContactsEditor, draftsFromContacts, type ContactDraft } from './ContactsEditor'
import { invalidEmailKeys, keepUnchangedEmailFlags, refusedEmailKeys } from './contactEmails'

/** The server's "no value" sentinel for a nullable id on PUT (an omitted id keeps the stored one). */
const NO_ID = '00000000-0000-0000-0000-000000000000'

interface CustomerFormProps {
  customer?: CustomerRecord
  /** Catalog to pick from: all entries; inactive ones are filtered out here. */
  businessTypes?: MasterDataEntry[]
  /** Price lists visible to the branch (loaded by the parent screen). */
  priceLists?: PriceListRecord[]
  /** The provinces of the organization's country (loaded by the parent screen). */
  provinces?: GeoProvince[]
  /** The organization's default customer list: preselected for a new customer. */
  defaultPriceListId?: string | null
  /** The organization's general payment term in days, shown as the fallback of an empty customer term. */
  defaultPaymentTermsDays?: number | null
  onSaved: () => void
  onCancel: () => void
  /** Called with the freshly read customer after a modification conflict; the parent re-renders the form with it. */
  onReload?: (customer: CustomerRecord) => void
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
 *
 * admin-console-field-fixes: one name field, labelled by the Person / Company choice (a company's people go in
 * the contacts). The address goes Province -> City (only that province's, by name) -> Postal code, prefilled from
 * the city when it has one; the province is not sent, it follows from the city. Every email uses `EmailField` and
 * an invalid one blocks the submit.
 */
export function CustomerForm({
  customer,
  businessTypes = [],
  priceLists = [],
  provinces = [],
  defaultPriceListId = null,
  defaultPaymentTermsDays = null,
  onSaved,
  onCancel,
  onReload,
}: CustomerFormProps) {
  const { t } = useTranslation('customers')
  const isEdit = customer !== undefined

  const [customerKind, setCustomerKind] = useState<CustomerKind>(customer?.customerKind ?? CustomerKind.Retail)
  const [partyType, setPartyType] = useState<PartyType>(customer?.partyType ?? PartyType.Person)
  const [displayName, setDisplayName] = useState(customer?.displayName ?? '')
  const [contacts, setContacts] = useState<ContactDraft[]>(() => draftsFromContacts(customer?.contacts ?? []))
  const [invalidContactKeys, setInvalidContactKeys] = useState<ReadonlySet<string>>(new Set())
  const [invalidContactEmailKeys, setInvalidContactEmailKeys] = useState<ReadonlySet<string>>(new Set())
  const [conflict, setConflict] = useState(false)
  const [reloadError, setReloadError] = useState<string | null>(null)
  // Cities are a ~4000-entry core catalog searched on the server by the
  // picker; the form only keeps the chosen one (from the customer's own
  // cityName/provinceName at edit, so showing it costs no request).
  const [city, setCity] = useState<CityOption | null>(
    customer?.cityId && customer.cityName
      ? {
          id: customer.cityId,
          name: customer.cityName,
          provinceName: customer.provinceName ?? '',
          provinceId: customer.provinceId ?? undefined,
        }
      : null,
  )
  const [provinceId, setProvinceId] = useState(customer?.provinceId ?? '')
  // The customer's own province stays selectable even before (or without) the catalog.
  const provinceOptions =
    provinceId !== '' && !provinces.some((province) => province.id === provinceId)
      ? [...provinces, { id: provinceId, name: customer?.provinceName ?? provinceId }]
      : provinces
  // With no province catalog (it failed to load) the city is searched across the country instead of locked.
  const cityLocked = provinces.length > 0 && provinceId === ''
  const [businessTypeId, setBusinessTypeId] = useState(customer?.businessTypeId ?? '')
  // Until the operator chooses, a new customer shows the organization default (which may arrive after the form
  // opens) and an existing one its own list.
  const [priceListChoice, setPriceListChoice] = useState<string | null>(null)
  const priceListId = priceListChoice ?? (isEdit ? (customer.priceListId ?? '') : (defaultPriceListId ?? ''))
  const priceListOptions =
    priceListId !== '' && !priceLists.some((list) => list.id === priceListId)
      ? [...priceLists, { id: priceListId, name: customer?.priceListName ?? t('form.otherBranchList') }]
      : priceLists
  const [taxIdType, setTaxIdType] = useState<TaxIdType>(customer?.taxIdType ?? TaxIdType.None)
  const [taxId, setTaxId] = useState(customer?.taxId ?? '')
  const [taxCondition, setTaxCondition] = useState<TaxCondition>(customer?.taxCondition ?? TaxCondition.NoAplica)
  const [phone, setPhone] = useState(customer?.phone ?? '')
  const [email, setEmail] = useState(customer?.email ?? '')
  const [emailError, setEmailError] = useState<string | null>(null)
  const [addressStreet, setAddressStreet] = useState(customer?.addressStreet ?? '')
  const [addressNumber, setAddressNumber] = useState(customer?.addressNumber ?? '')
  const [neighborhood, setNeighborhood] = useState(customer?.neighborhood ?? '')
  const [postalCode, setPostalCode] = useState(customer?.postalCode ?? '')
  /** Whether the postal code shown came from the chosen city (so it follows the city) rather than from the user. */
  const [postalCodeFromCity, setPostalCodeFromCity] = useState(false)
  const [deliveryNotes, setDeliveryNotes] = useState(customer?.deliveryNotes ?? '')
  const [discountPercentage, setDiscountPercentage] = useState(
    customer?.discountPercentage !== null && customer?.discountPercentage !== undefined
      ? String(customer.discountPercentage)
      : '',
  )
  const [paymentTerms, setPaymentTerms] = useState(customer?.paymentTerms ?? '')
  const [paymentTermsDaysError, setPaymentTermsDaysError] = useState<string | null>(null)
  const [paymentTermsDays, setPaymentTermsDays] = useState(
    customer?.paymentTermsDays !== null && customer?.paymentTermsDays !== undefined ? String(customer.paymentTermsDays) : '',
  )
  const [notes, setNotes] = useState(customer?.notes ?? '')
  const [isEnabled, setIsEnabled] = useState(customer?.isEnabled ?? true)
  const [error, setError] = useState<string | null>(null)
  const [taxIdError, setTaxIdError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  const chooseCity = (next: CityOption | null) => {
    // Picking the same city again keeps whatever postal code the user typed for it.
    if (next?.id === city?.id) {
      setCity(next)
      return
    }
    setCity(next)
    if (next?.postalCode) {
      setPostalCode(next.postalCode)
      setPostalCodeFromCity(true)
    } else if (postalCodeFromCity) {
      setPostalCode('')
      setPostalCodeFromCity(false)
    }
  }

  const chooseProvince = (next: string) => {
    setProvinceId(next)
    if (city && city.provinceId !== next) chooseCity(null)
  }

  const changeContacts = (next: ContactDraft[]) => {
    setInvalidContactEmailKeys((flagged) => keepUnchangedEmailFlags(flagged, contacts, next))
    setContacts(next)
  }

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
    const badContactEmails = invalidEmailKeys(filledContacts)
    setInvalidContactEmailKeys(badContactEmails)
    const badEmail = emailStatus(email) === 'invalid'
    setEmailError(badEmail ? t('common:email.invalid') : null)
    if (missingFirstName.length > 0 || badContactEmails.size > 0 || badEmail) return
    if (!isValidTaxId(taxIdType, taxId)) {
      setTaxIdError(t(taxIdType === TaxIdType.Dni ? 'form.errors.dniInvalid' : 'form.errors.cuitInvalid'))
      return
    }
    setTaxIdError(null)
    const termsText = paymentTermsDays.trim()
    const terms = Number(termsText)
    if (termsText !== '' && (!Number.isInteger(terms) || terms < 0 || terms > 365)) {
      setPaymentTermsDaysError(t('form.paymentTermsDays.invalid'))
      return
    }
    setPaymentTermsDaysError(null)
    setSubmitting(true)
    try {
      const shared = {
        partyType,
        displayName,
        taxIdType,
        taxId: taxIdType === TaxIdType.None ? null : taxId.trim(),
        taxCondition,
        // PUT keeps an omitted value, so clearing sends the sentinel / empty
        // string; on create an empty choice is simply left out.
        ...(isEdit
          ? { cityId: city?.id ?? NO_ID, businessTypeId: businessTypeId || NO_ID, priceListId: priceListId || NO_ID }
          : {
              ...(city ? { cityId: city.id } : {}),
              ...(businessTypeId ? { businessTypeId } : {}),
              ...(priceListId ? { priceListId } : {}),
            }),
        phone: phone || null,
        email: email.trim() || null,
        addressStreet: addressStreet || null,
        addressNumber: addressNumber || null,
        neighborhood: neighborhood || null,
        postalCode: postalCode.trim() || null,
        deliveryNotes: deliveryNotes || null,
        discountPercentage: discountPercentage === '' ? null : Number(discountPercentage),
        paymentTerms: paymentTerms || null,
        // Empty = the organization's general term: on PUT that is the -1 sentinel, on create the value is left out.
        ...(paymentTermsDays.trim() !== ''
          ? { paymentTermsDays: Number(paymentTermsDays) }
          : isEdit
            ? { paymentTermsDays: -1 }
            : {}),
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
      } else if (err instanceof ApiError && err.status === 400 && showRefusedEmails(err.fieldErrors, filledContacts)) {
        // The refused emails are flagged on their own fields.
      } else {
        setError(err instanceof ApiError ? err.message : t('errors.unexpectedSave'))
      }
    } finally {
      setSubmitting(false)
    }
  }

  /** Flags the emails a 400 refused on their fields; false when the refusal is about something else. */
  const showRefusedEmails = (fieldErrors: Record<string, string[]> | undefined, sent: ContactDraft[]) => {
    const refusedContacts = refusedEmailKeys(sent, fieldErrors?.contacts)
    const refusedEmail = fieldErrors?.email !== undefined
    if (refusedEmail) setEmailError(t('common:email.invalid'))
    setInvalidContactEmailKeys(refusedContacts)
    return refusedEmail || refusedContacts.size > 0
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
      <form className="flex flex-col gap-10" onSubmit={handleSubmit}>
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

          <fieldset className="flex flex-col gap-1.5">
            <legend className="mb-1.5 text-sm font-medium leading-none text-foreground">
              {t('form.fields.partyType')}
            </legend>
            <div className="flex h-9 items-center gap-6">
              {[PartyType.Person, PartyType.Company].map((option) => (
                <div key={option} className="flex items-center gap-2">
                  <input
                    id={`partyType-${option}`}
                    type="radio"
                    name="partyType"
                    value={option}
                    checked={partyType === option}
                    onChange={() => setPartyType(option)}
                  />
                  <Label htmlFor={`partyType-${option}`}>
                    {t(option === PartyType.Company ? 'form.partyTypeOptions.company' : 'form.partyTypeOptions.person')}
                  </Label>
                </div>
              ))}
            </div>
          </fieldset>

          <Field
            id="displayName"
            label={partyType === PartyType.Company ? t('form.fields.companyName') : t('form.fields.personName')}
            hint={partyType === PartyType.Company ? t('form.fields.companyNameHint') : undefined}
            value={displayName}
            onChange={setDisplayName}
            required
          />
        </FormSection>

        <FormSection title={t('form.sections.contact')}>
          <Field id="phone" label={t('form.fields.phone')} value={phone} onChange={setPhone} />
          <EmailField
            id="email"
            label={t('form.fields.email')}
            value={email}
            onChange={(value) => {
              setEmail(value)
              setEmailError(null)
            }}
            error={emailError}
          />
        </FormSection>

        <FormSection title={t('form.sections.contacts')} wide>
          <ContactsEditor
            contacts={contacts}
            onChange={changeContacts}
            invalidKeys={invalidContactKeys}
            invalidEmailKeys={invalidContactEmailKeys}
          />
        </FormSection>

        <FormSection title={t('form.sections.address')} wide>
          {/* Three rows: Province, City, Postal code / Neighborhood, Street, Number / Delivery notes. */}
          <div className="grid grid-cols-1 gap-x-6 gap-y-4 md:grid-cols-3">
            <CatalogSelect
              id="provinceId"
              label={t('form.fields.province')}
              emptyLabel={t('form.provincePlaceholder')}
              value={provinceId}
              onChange={chooseProvince}
              options={provinceOptions}
            />
            <CityPicker
              id="cityId"
              label={t('form.fields.city')}
              value={city}
              onChange={chooseCity}
              provinceId={provinceId || undefined}
              showProvince={provinceId === ''}
              disabled={cityLocked}
              placeholder={cityLocked ? t('form.cityNeedsProvince') : undefined}
            />
            <Field
              id="postalCode"
              label={t('form.fields.postalCode')}
              value={postalCode}
              onChange={(value) => {
                setPostalCode(value)
                setPostalCodeFromCity(false)
              }}
            />
            <Field id="neighborhood" label={t('form.fields.neighborhood')} value={neighborhood} onChange={setNeighborhood} />
            <Field id="addressStreet" label={t('form.fields.addressStreet')} value={addressStreet} onChange={setAddressStreet} />
            <Field id="addressNumber" label={t('form.fields.addressNumber')} value={addressNumber} onChange={setAddressNumber} />
            <Field
              id="deliveryNotes"
              label={t('form.fields.deliveryNotes')}
              value={deliveryNotes}
              onChange={setDeliveryNotes}
              className="md:col-span-3"
            />
          </div>
        </FormSection>

        <FormSection title={t('form.sections.tax')}>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="taxIdType">{t('form.fields.taxIdType')}</Label>
            <Select
              id="taxIdType"
              value={taxIdType}
              onChange={(e) => {
                const next = e.target.value as TaxIdType
                setTaxIdType(next)
                setTaxIdError(null)
                // Owner decision 2026-10-03: a CUIT suggests a company; the user can still pick Person.
                if (next === TaxIdType.Cuit) setPartyType(PartyType.Company)
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
          <CatalogSelect
            id="priceListId"
            label={t('form.fields.priceList')}
            emptyLabel={t('form.noPriceList')}
            value={priceListId}
            onChange={setPriceListChoice}
            options={priceListOptions}
          />
          <Field
            id="discountPercentage"
            label={t('form.fields.discountPercentage')}
            value={discountPercentage}
            onChange={setDiscountPercentage}
            type="number"
          />
          <Field
            id="paymentTermsDays"
            label={t('form.fields.paymentTermsDays')}
            value={paymentTermsDays}
            onChange={setPaymentTermsDays}
            type="number"
            step="1"
            inputMode="numeric"
            placeholder={
              defaultPaymentTermsDays === null
                ? t('form.paymentTermsDays.placeholderUnknown')
                : t('form.paymentTermsDays.placeholder', { days: defaultPaymentTermsDays })
            }
            hint={t('form.paymentTermsDays.hint')}
            error={paymentTermsDaysError}
          />
          <Field
            id="paymentTerms"
            label={t('form.fields.paymentTerms')}
            value={paymentTerms}
            onChange={setPaymentTerms}
            placeholder={t('form.paymentTermsNote.placeholder')}
            hint={t('form.paymentTermsNote.hint')}
          />

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

import { useState, type FormEvent } from 'react'
import { Link } from 'react-router'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Select } from '@/components/ui/select'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import { FormPage } from '@/components/layout/FormPage'
import { CityPicker, type CityOption } from '@/components/geo/CityPicker'
import { CatalogSelect, Field, FormSection } from '@/components/form/FormParts'
import { selectableEntries } from '@/components/form/selectableEntries'
import { createSupplier, getSupplier, updateSupplier } from '@/api/suppliers'
import { ApiError } from '@/api/client'
import {
  TaxCondition,
  TaxIdType,
  type MasterDataEntry,
  type SupplierContactInput,
  type SupplierRecord,
} from '@/api/types'
import { isValidTaxId } from '@/lib/taxId'
import { ContactsEditor, draftsFromContacts, type ContactDraft } from './ContactsEditor'

/** The server's "no value" sentinel for a nullable id on PUT (an omitted id keeps the stored one). */
const NO_ID = '00000000-0000-0000-0000-000000000000'

interface SupplierFormProps {
  supplier?: SupplierRecord
  /** Categories to pick from; inactive ones are filtered out here. */
  categories?: MasterDataEntry[]
  onSaved: () => void
  onCancel: () => void
  /** Called with the freshly read supplier after a modification conflict. */
  onReload?: (supplier: SupplierRecord) => void
}

/** CBU/CVU: 22 digits once separators (spaces, hyphens) are ignored. Blank means none. */
const isValidCbu = (value: string) => value.trim() === '' || /^\d{22}$/.test(value.replace(/[\s-]/g, ''))
/** Alias: 6-20 characters. Blank means none. */
const isValidAlias = (value: string) => value.trim() === '' || (value.trim().length >= 6 && value.trim().length <= 20)
/** Payment terms: a whole number of days between 0 and 365. Blank means none. */
const isValidTerms = (value: string) => {
  if (value.trim() === '') return true
  const days = Number(value)
  return Number.isInteger(days) && days >= 0 && days <= 365
}

/**
 * Full-page supplier form, modelled on `CustomerForm` (same shell, sections
 * and contacts editor): Datos, Contacto, Personas de contacto, Ubicación,
 * Fiscal, Comercial (category, payment terms, bank data) and Observaciones.
 * Client validation mirrors the server's rules so the operator hears about a
 * bad CBU or alias before the round trip; the server stays the authority.
 * On edit an omitted id would keep the stored value, so clearing sends the
 * empty-id sentinel. The `updatedAtUtc` read guards against concurrent edits.
 */
export function SupplierForm({ supplier, categories = [], onSaved, onCancel, onReload }: SupplierFormProps) {
  const { t } = useTranslation('suppliers')
  const isEdit = supplier !== undefined

  const [displayName, setDisplayName] = useState(supplier?.displayName ?? '')
  const [legalName, setLegalName] = useState(supplier?.legalName ?? '')
  const [contacts, setContacts] = useState<ContactDraft[]>(() => draftsFromContacts(supplier?.contacts ?? []))
  const [invalidContactKeys, setInvalidContactKeys] = useState<ReadonlySet<string>>(new Set())
  const [conflict, setConflict] = useState(false)
  const [reloadError, setReloadError] = useState<string | null>(null)
  const [city, setCity] = useState<CityOption | null>(
    supplier?.cityId && supplier.cityName
      ? { id: supplier.cityId, name: supplier.cityName, provinceName: supplier.provinceName ?? '' }
      : null,
  )
  const [categoryId, setCategoryId] = useState(supplier?.categoryId ?? '')
  const [taxIdType, setTaxIdType] = useState<TaxIdType>(supplier?.taxIdType ?? TaxIdType.None)
  const [taxId, setTaxId] = useState(supplier?.taxId ?? '')
  const [taxCondition, setTaxCondition] = useState<TaxCondition>(supplier?.taxCondition ?? TaxCondition.NoAplica)
  const [phone, setPhone] = useState(supplier?.phone ?? '')
  const [email, setEmail] = useState(supplier?.email ?? '')
  const [addressStreet, setAddressStreet] = useState(supplier?.addressStreet ?? '')
  const [addressNumber, setAddressNumber] = useState(supplier?.addressNumber ?? '')
  const [neighborhood, setNeighborhood] = useState(supplier?.neighborhood ?? '')
  const [postalCode, setPostalCode] = useState(supplier?.postalCode ?? '')
  const [paymentTermsDays, setPaymentTermsDays] = useState(
    supplier?.paymentTermsDays != null ? String(supplier.paymentTermsDays) : '',
  )
  const [bankCbu, setBankCbu] = useState(supplier?.bankCbu ?? '')
  const [bankAlias, setBankAlias] = useState(supplier?.bankAlias ?? '')
  const [notes, setNotes] = useState(supplier?.notes ?? '')
  const [isEnabled, setIsEnabled] = useState(supplier?.isEnabled ?? true)
  const [error, setError] = useState<string | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})
  const [submitting, setSubmitting] = useState(false)

  const clearFieldError = (name: string) => setFieldErrors((current) => ({ ...current, [name]: '' }))

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault()
    setError(null)
    setConflict(false)
    setReloadError(null)
    const filledContacts = contacts.filter((c) =>
      [c.firstName, c.lastName, c.phone, c.email, c.role].some((value) => value.trim() !== ''),
    )
    const missingFirstName = filledContacts.filter((c) => c.firstName.trim() === '')
    setInvalidContactKeys(new Set(missingFirstName.map((c) => c.key)))

    const errors: Record<string, string> = {}
    if (!isValidTaxId(taxIdType, taxId)) {
      errors.taxId = t(taxIdType === TaxIdType.Dni ? 'form.errors.dniInvalid' : 'form.errors.cuitInvalid')
    }
    if (!isValidTerms(paymentTermsDays)) errors.paymentTermsDays = t('form.errors.paymentTermsInvalid')
    if (!isValidCbu(bankCbu)) errors.bankCbu = t('form.errors.cbuInvalid')
    if (!isValidAlias(bankAlias)) errors.bankAlias = t('form.errors.aliasInvalid')
    setFieldErrors(errors)
    if (missingFirstName.length > 0 || Object.keys(errors).length > 0) return

    setSubmitting(true)
    try {
      const shared = {
        displayName,
        legalName: legalName || null,
        taxIdType,
        taxId: taxIdType === TaxIdType.None ? null : taxId.trim(),
        taxCondition,
        ...(isEdit
          ? { cityId: city?.id ?? NO_ID, categoryId: categoryId || NO_ID }
          : { ...(city ? { cityId: city.id } : {}), ...(categoryId ? { categoryId } : {}) }),
        phone: phone || null,
        email: email || null,
        addressStreet: addressStreet || null,
        addressNumber: addressNumber || null,
        neighborhood: neighborhood || null,
        postalCode: postalCode || null,
        paymentTermsDays: paymentTermsDays.trim() === '' ? null : Number(paymentTermsDays),
        bankCbu: bankCbu.trim() === '' ? null : bankCbu.trim(),
        bankAlias: bankAlias.trim() === '' ? null : bankAlias.trim(),
        notes: notes || null,
      }

      // Replace-set: sent in screen order, so the position is the sort order.
      const contactInputs: SupplierContactInput[] = filledContacts.map((c, index) => ({
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
        await updateSupplier(supplier.id, {
          ...shared,
          contacts: contactInputs,
          isEnabled,
          expectedUpdatedAtUtc: supplier.updatedAtUtc,
        })
      } else {
        await createSupplier({ ...shared, ...(contactInputs.length > 0 ? { contacts: contactInputs } : {}) })
      }
      onSaved()
    } catch (err) {
      if (err instanceof ApiError && err.status === 409 && err.code === 'supplier-modified') {
        setConflict(true)
      } else {
        setError(err instanceof ApiError ? err.message : t('errors.unexpectedSave'))
      }
    } finally {
      setSubmitting(false)
    }
  }

  const handleReload = async () => {
    if (!supplier) return
    setReloadError(null)
    try {
      onReload?.(await getSupplier(supplier.id))
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
      {supplier && (
        <p className="-mt-3 mb-4">
          <Link className="text-sm font-medium text-primary underline" to={`/app/suppliers/${supplier.id}/account`}>
            {t('form.account')}
          </Link>
        </p>
      )}
      <form className="flex flex-col gap-8" onSubmit={handleSubmit}>
        <FormSection title={t('form.sections.identity')}>
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
          <Field id="addressStreet" label={t('form.fields.addressStreet')} value={addressStreet} onChange={setAddressStreet} />
          <Field id="addressNumber" label={t('form.fields.addressNumber')} value={addressNumber} onChange={setAddressNumber} />
          <Field id="neighborhood" label={t('form.fields.neighborhood')} value={neighborhood} onChange={setNeighborhood} />
          <Field id="postalCode" label={t('form.fields.postalCode')} value={postalCode} onChange={setPostalCode} />
        </FormSection>

        <FormSection title={t('form.sections.tax')}>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="taxIdType">{t('form.fields.taxIdType')}</Label>
            <Select
              id="taxIdType"
              value={taxIdType}
              onChange={(e) => {
                setTaxIdType(e.target.value as TaxIdType)
                clearFieldError('taxId')
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
                clearFieldError('taxId')
              }}
              required
              error={fieldErrors.taxId}
            />
          )}
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="taxCondition">{t('form.fields.taxCondition')}</Label>
            <Select
              id="taxCondition"
              value={taxCondition}
              onChange={(e) => setTaxCondition(e.target.value as TaxCondition)}
            >
              {/* Canonical AFIP category names, already Spanish (same as the customer form). */}
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
            id="categoryId"
            label={t('form.fields.category')}
            emptyLabel={t('form.noCategory')}
            value={categoryId}
            onChange={setCategoryId}
            options={selectableEntries(categories, supplier?.categoryId, supplier?.categoryName, t('form.inactiveSuffix'))}
          />
          <Field
            id="paymentTermsDays"
            label={t('form.fields.paymentTermsDays')}
            value={paymentTermsDays}
            onChange={(value) => {
              setPaymentTermsDays(value)
              clearFieldError('paymentTermsDays')
            }}
            type="number"
            error={fieldErrors.paymentTermsDays}
          />
          <Field
            id="bankCbu"
            label={t('form.fields.bankCbu')}
            value={bankCbu}
            onChange={(value) => {
              setBankCbu(value)
              clearFieldError('bankCbu')
            }}
            error={fieldErrors.bankCbu}
          />
          <Field
            id="bankAlias"
            label={t('form.fields.bankAlias')}
            value={bankAlias}
            onChange={(value) => {
              setBankAlias(value)
              clearFieldError('bankAlias')
            }}
            error={fieldErrors.bankAlias}
          />
          {isEdit && (
            <div className="flex items-center gap-2">
              <input id="isEnabled" type="checkbox" checked={isEnabled} onChange={(e) => setIsEnabled(e.target.checked)} />
              <Label htmlFor="isEnabled">{t('form.fields.enabled')}</Label>
            </div>
          )}
        </FormSection>

        <FormSection title={t('form.sections.notes')} wide>
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

import { useEffect, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select } from '@/components/ui/select'
import { Textarea } from '@/components/ui/textarea'
import { ApiError } from '@/api/client'
import {
  getOrganizationDocumentProfile,
  updateOrganizationDocumentProfile,
  type OrganizationDocumentProfileRequest,
} from '@/api/fulfillment'

const TAX_CONDITIONS = ['ResponsableInscripto', 'Monotributo', 'Exento', 'ConsumidorFinal', 'NoAplica'] as const

const EMPTY: OrganizationDocumentProfileRequest = {
  legalName: null,
  taxId: null,
  taxCondition: null,
  grossIncomeNumber: null,
  activityStartDate: null,
  fiscalAddress: null,
  documentFooter: null,
  logoUrl: null,
  primaryColor: null,
}

/**
 * Settings → Documents: the organization data printed on the header of its remitos (legal name, CUIT, tax condition,
 * gross income number, activity start, fiscal address, footer) plus the logo and brand color the remito is styled
 * with. The address, phone and warehouse are per branch (Branches). The server validates the CUIT, URL and color.
 */
export function OrganizationDocumentsForm() {
  const { t } = useTranslation('fulfillment')
  const [form, setForm] = useState<OrganizationDocumentProfileRequest>(EMPTY)
  const [name, setName] = useState('')
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)
  const [saving, setSaving] = useState(false)

  useEffect(() => {
    let cancelled = false
    getOrganizationDocumentProfile()
      .then((profile) => {
        if (cancelled) return
        const { name: organizationName, ...rest } = profile
        setName(organizationName)
        setForm(rest)
      })
      .catch(() => {
        if (!cancelled) setLoadError(t('documents.loadError'))
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })
    return () => {
      cancelled = true
    }
  }, [t])

  const set = (field: keyof OrganizationDocumentProfileRequest) => (value: string) => {
    setSaved(false)
    setForm((current) => ({ ...current, [field]: value === '' ? null : value }))
  }

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    setSaving(true)
    setError(null)
    setSaved(false)
    try {
      await updateOrganizationDocumentProfile(form)
      setSaved(true)
    } catch (err) {
      const fieldErrors = err instanceof ApiError ? err.fieldErrors : undefined
      setError(fieldErrors ? Object.values(fieldErrors).flat().join(' ') : t('documents.saveError'))
    } finally {
      setSaving(false)
    }
  }

  return (
    <form onSubmit={(event) => void submit(event)} className="flex max-w-2xl flex-col gap-4" noValidate>
      <div>
        <h2 className="text-lg font-semibold">{t('documents.title')}</h2>
        <p className="text-sm text-muted-foreground">{t('documents.description')}</p>
      </div>
      {loadError && (
        <p role="alert" className="text-sm text-destructive">
          {loadError}
        </p>
      )}
      <div className="grid gap-4 sm:grid-cols-2">
        <TextField id="docLegalName" label={t('documents.legalName')} value={form.legalName} placeholder={name} onChange={set('legalName')} maxLength={200} />
        <TextField id="docTaxId" label={t('documents.taxId')} value={form.taxId} placeholder="30-12345678-9" onChange={set('taxId')} maxLength={13} />
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="docTaxCondition">{t('documents.taxCondition')}</Label>
          <Select id="docTaxCondition" value={form.taxCondition ?? ''} onChange={(e) => set('taxCondition')(e.target.value)}>
            <option value="">{t('documents.none')}</option>
            {TAX_CONDITIONS.map((condition) => (
              <option key={condition} value={condition}>
                {t(`taxCondition.${condition}`)}
              </option>
            ))}
          </Select>
        </div>
        <TextField id="docGrossIncome" label={t('documents.grossIncome')} value={form.grossIncomeNumber} onChange={set('grossIncomeNumber')} maxLength={40} />
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="docActivityStart">{t('documents.activityStart')}</Label>
          <Input id="docActivityStart" type="date" value={form.activityStartDate ?? ''} onChange={(e) => set('activityStartDate')(e.target.value)} />
        </div>
        <TextField id="docFiscalAddress" label={t('documents.fiscalAddress')} value={form.fiscalAddress} onChange={set('fiscalAddress')} maxLength={200} />
        <div className="flex flex-col gap-1.5 sm:col-span-2">
          <Label htmlFor="docLogoUrl">{t('documents.logoUrl')}</Label>
          <div className="flex items-center gap-3">
            <Input id="docLogoUrl" type="url" value={form.logoUrl ?? ''} onChange={(e) => set('logoUrl')(e.target.value)} />
            {form.logoUrl && <img src={form.logoUrl} alt="" className="h-10 w-10 shrink-0 rounded border border-border object-contain" />}
          </div>
          <p className="text-xs text-muted-foreground">{t('documents.logoHint')}</p>
        </div>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="docPrimaryColor">{t('documents.primaryColor')}</Label>
          <div className="flex items-center gap-2">
            <input
              id="docPrimaryColor"
              type="color"
              className="h-9 w-14 cursor-pointer rounded border border-border bg-background"
              value={form.primaryColor ?? '#1f2937'}
              onChange={(e) => set('primaryColor')(e.target.value)}
            />
            <span className="text-sm tabular-nums text-muted-foreground">{form.primaryColor ?? t('documents.none')}</span>
          </div>
        </div>
        <div className="flex flex-col gap-1.5 sm:col-span-2">
          <Label htmlFor="docFooter">{t('documents.footer')}</Label>
          <Textarea id="docFooter" rows={2} maxLength={300} value={form.documentFooter ?? ''} onChange={(e) => set('documentFooter')(e.target.value)} />
        </div>
      </div>
      {error && (
        <p role="alert" className="text-sm text-destructive">
          {error}
        </p>
      )}
      {saved && (
        <p role="status" className="text-sm text-emerald-700 dark:text-emerald-400">
          {t('documents.saved')}
        </p>
      )}
      <div>
        <Button type="submit" disabled={loading || saving || Boolean(loadError)}>
          {saving ? t('documents.saving') : t('documents.save')}
        </Button>
      </div>
    </form>
  )
}

function TextField({
  id,
  label,
  value,
  onChange,
  maxLength,
  placeholder,
}: {
  id: string
  label: string
  value: string | null
  onChange: (value: string) => void
  maxLength: number
  placeholder?: string
}) {
  return (
    <div className="flex flex-col gap-1.5">
      <Label htmlFor={id}>{label}</Label>
      <Input id={id} value={value ?? ''} maxLength={maxLength} placeholder={placeholder} onChange={(e) => onChange(e.target.value)} />
    </div>
  )
}

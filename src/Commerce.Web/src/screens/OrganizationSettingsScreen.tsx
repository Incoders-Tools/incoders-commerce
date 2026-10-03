import { useEffect, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select } from '@/components/ui/select'
import { PageHeader } from '@/components/data/PageHeader'
import { getOwnOrganizationSettings, updateOwnOrganizationSettings } from '@/api/account'
import { ApiError } from '@/api/client'
import { listPriceLists } from '@/api/pricing'
import type { OrganizationSettings, PriceListRecord, UpdateOrganizationSettingsRequest } from '@/api/types'
import { useNumberFormat } from '@/organization/NumberFormatContext'

type Separator = OrganizationSettings['quantityDecimalSeparator']

/** The server's default when an organization has no explicit country. */
const DEFAULT_COUNTRY_CODE = 'AR'

/** The country's name in the UI language ("Argentina"), or its code when the runtime cannot name it. */
function countryName(code: string, language: string): string {
  try {
    return new Intl.DisplayNames([language], { type: 'region' }).of(code) ?? code
  } catch {
    return code
  }
}

/**
 * Settings of the signed-in organization (a system administrator edits the one it is acting on). The first
 * setting is the number format for quantities; more fields join it here as they appear. A failed load disables
 * Save so the real stored value is never overwritten with the form default. The country (the provinces and
 * cities the forms offer) is shown read-only: Argentina is the only one loaded today.
 */
export function OrganizationSettingsScreen() {
  const { t, i18n } = useTranslation('organizations')
  const { reload } = useNumberFormat()
  const [separator, setSeparator] = useState<Separator>('Comma')
  const [loadedSeparator, setLoadedSeparator] = useState<Separator>('Comma')
  const [defaultListId, setDefaultListId] = useState('')
  const [loadedDefaultListId, setLoadedDefaultListId] = useState('')
  const [priceLists, setPriceLists] = useState<PriceListRecord[]>([])
  const [countryCode, setCountryCode] = useState(DEFAULT_COUNTRY_CODE)
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [reloadToken, setReloadToken] = useState(0)
  const [submitting, setSubmitting] = useState(false)
  const [submitError, setSubmitError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)

  useEffect(() => {
    let cancelled = false
    setLoading(true)
    setLoadError(null)
    getOwnOrganizationSettings()
      .then((settings) => {
        if (cancelled) return
        const loaded = settings.quantityDecimalSeparator === 'Dot' ? 'Dot' : 'Comma'
        setSeparator(loaded)
        setLoadedSeparator(loaded)
        setDefaultListId(settings.defaultCustomerPriceListId ?? '')
        setLoadedDefaultListId(settings.defaultCustomerPriceListId ?? '')
        setCountryCode(settings.countryCode || DEFAULT_COUNTRY_CODE)
      })
      .catch(() => {
        if (!cancelled) setLoadError(t('settings.unableToLoad'))
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })
    return () => {
      cancelled = true
    }
  }, [reloadToken, t])

  // The lists only feed the select below: if they cannot be read it just offers "Sin lista".
  useEffect(() => {
    listPriceLists().then(
      (lists) => setPriceLists(Array.isArray(lists) ? lists : []),
      () => setPriceLists([]),
    )
  }, [])

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    if (loadError) return
    setSubmitError(null)
    setSaved(false)
    setSubmitting(true)
    try {
      // Only what changed is sent (an omitted field stays as it is); with nothing changed the separator is
      // re-sent, as before.
      const request: UpdateOrganizationSettingsRequest = {
        ...(separator !== loadedSeparator ? { quantityDecimalSeparator: separator } : {}),
        ...(defaultListId !== loadedDefaultListId
          ? defaultListId === ''
            ? { clearDefaultCustomerPriceList: true }
            : { defaultCustomerPriceListId: defaultListId }
          : {}),
      }
      await updateOwnOrganizationSettings(
        Object.keys(request).length > 0 ? request : { quantityDecimalSeparator: separator },
      )
      setLoadedSeparator(separator)
      setLoadedDefaultListId(defaultListId)
      setSaved(true)
      reload()
    } catch (err) {
      setSubmitError(err instanceof ApiError ? err.message : t('settings.unableToSave'))
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <section className="flex w-full flex-col gap-6">
      <PageHeader title={t('settings.title')} description={t('settings.description')} />
      <form onSubmit={(event) => void submit(event)} className="flex max-w-md flex-col gap-6">
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="organizationCountry">{t('settings.country.label')}</Label>
          <Input
            id="organizationCountry"
            readOnly
            value={countryName(countryCode, i18n.language)}
            aria-describedby="organizationCountry-hint"
          />
          <p id="organizationCountry-hint" className="text-xs text-muted-foreground">
            {t('settings.country.hint')}
          </p>
        </div>

        <div className="flex flex-col gap-1.5">
          <Label htmlFor="quantityDecimalSeparator">{t('settings.numberFormat.label')}</Label>
          <Select
            id="quantityDecimalSeparator"
            value={separator}
            disabled={loading}
            onChange={(event) => {
              setSeparator(event.target.value as Separator)
              setSaved(false)
            }}
          >
            <option value="Comma">{t('settings.numberFormat.comma')}</option>
            <option value="Dot">{t('settings.numberFormat.point')}</option>
          </Select>
          <p className="text-xs text-muted-foreground">{t('settings.numberFormat.hint')}</p>
        </div>

        <div className="flex flex-col gap-1.5">
          <Label htmlFor="defaultCustomerPriceListId">{t('settings.defaultCustomerPriceList.label')}</Label>
          <Select
            id="defaultCustomerPriceListId"
            value={defaultListId}
            disabled={loading}
            onChange={(event) => {
              setDefaultListId(event.target.value)
              setSaved(false)
            }}
          >
            <option value="">{t('settings.defaultCustomerPriceList.none')}</option>
            {priceLists.map((list) => (
              <option key={list.id} value={list.id}>
                {list.name}
              </option>
            ))}
          </Select>
          <p className="text-xs text-muted-foreground">{t('settings.defaultCustomerPriceList.hint')}</p>
        </div>

        {loadError && (
          <div
            role="alert"
            className="flex flex-col items-start gap-2 rounded-md border border-destructive/50 bg-destructive/10 p-3 text-sm text-destructive"
          >
            <p>{loadError}</p>
            <Button type="button" variant="outline" size="sm" onClick={() => setReloadToken((token) => token + 1)}>
              {t('settings.retry')}
            </Button>
          </div>
        )}
        {submitError && (
          <p role="alert" className="text-sm text-destructive">
            {submitError}
          </p>
        )}
        {saved && (
          <p role="status" className="text-sm text-muted-foreground">
            {t('settings.saved')}
          </p>
        )}

        <div>
          <Button type="submit" disabled={loading || submitting || Boolean(loadError)}>
            {submitting ? t('settings.saving') : t('settings.save')}
          </Button>
        </div>
      </form>
    </section>
  )
}

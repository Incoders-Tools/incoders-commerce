import { useEffect, useState, type FormEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { FormPage } from '@/components/layout/FormPage'
import { getOrganizationBranding, updateOrganizationBranding } from '@/api/account'
import { ApiError } from '@/api/client'
import type { OrganizationSummary } from '@/api/types'

interface OrganizationBrandingFormProps {
  organization: OrganizationSummary
  onSaved: () => void
  onCancel: () => void
}

const DEFAULT_SWATCH_COLOR = '#000000'

/**
 * T5b: minimal branding editor — `logoUrl` + `primaryColor` only, per the
 * user's explicit scope decision ("lo mas simple posible, a futuro
 * ampliamos"). Date format, geolocation and usage plan are NOT here. Same
 * full-screen state-swap pattern `OrganizationForm`/`CustomerForm` use, and
 * the same `FormPage` shell.
 *
 * The native `<input type="color">` and the hex `Input` both edit the SAME
 * `primaryColor` string — there is one source of truth, not two fields kept
 * in sync. The color input always needs a valid 6-digit hex to render
 * (browsers reject blank/partial values), so it falls back to
 * `DEFAULT_SWATCH_COLOR` purely for its own display while `primaryColor`
 * itself may legitimately be empty (unset).
 */
export function OrganizationBrandingForm({ organization, onSaved, onCancel }: OrganizationBrandingFormProps) {
  const [logoUrl, setLogoUrl] = useState('')
  const [primaryColor, setPrimaryColor] = useState('')
  const [loading, setLoading] = useState(true)
  // R3-branding-load-failure-save-clears: a failed load left the fields
  // empty, and Save would then happily PUT those empty values, clearing
  // the organization's real stored branding. `loadError` is tracked
  // separately from `submitError` so Save can be disabled specifically
  // while the last load attempt is known to have failed, with a Retry
  // action instead of leaving the form silently broken.
  const [loadError, setLoadError] = useState<string | null>(null)
  const [submitError, setSubmitError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const [reloadToken, setReloadToken] = useState(0)

  useEffect(() => {
    let cancelled = false
    setLoading(true)
    setLoadError(null)
    getOrganizationBranding(organization.id)
      .then((branding) => {
        if (cancelled) return
        setLogoUrl(branding.logoUrl ?? '')
        setPrimaryColor(branding.primaryColor ?? '')
      })
      .catch((err) => {
        if (cancelled) return
        setLoadError(err instanceof ApiError ? err.message : 'Unable to load organization branding.')
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })
    return () => {
      cancelled = true
    }
  }, [organization.id, reloadToken])

  const handleRetry = () => setReloadToken((token) => token + 1)

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault()
    if (loadError) return
    setSubmitError(null)
    setSubmitting(true)
    try {
      await updateOrganizationBranding(organization.id, {
        logoUrl: logoUrl.trim() === '' ? null : logoUrl.trim(),
        primaryColor: primaryColor.trim() === '' ? null : primaryColor.trim(),
      })
      onSaved()
    } catch (err) {
      setSubmitError(err instanceof ApiError ? err.message : 'Unable to save organization branding.')
    } finally {
      setSubmitting(false)
    }
  }

  const trimmedLogoUrl = logoUrl.trim()
  const trimmedColor = primaryColor.trim()

  return (
    <FormPage
      title="Edit branding"
      description={`Logo and primary color for ${organization.name}.`}
      onBack={onCancel}
      backLabel="Back to organizations"
    >
      <form onSubmit={handleSubmit} className="flex flex-col gap-6">
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="logoUrl">Logo URL</Label>
          <Input
            id="logoUrl"
            type="url"
            value={logoUrl}
            onChange={(e) => setLogoUrl(e.target.value)}
            placeholder="https://…"
            disabled={loading}
          />
          {trimmedLogoUrl !== '' && (
            <img
              src={trimmedLogoUrl}
              alt="Organization logo preview"
              className="mt-2 h-16 w-16 rounded-md border border-border object-contain"
            />
          )}
        </div>

        <div className="flex flex-col gap-1.5">
          <Label htmlFor="primaryColor">Primary color</Label>
          <div className="flex items-center gap-3">
            <Label htmlFor="primaryColorPicker" className="sr-only">
              Primary color picker
            </Label>
            <input
              id="primaryColorPicker"
              type="color"
              value={/^#[0-9a-fA-F]{6}$/.test(trimmedColor) ? trimmedColor : DEFAULT_SWATCH_COLOR}
              onChange={(e) => setPrimaryColor(e.target.value)}
              disabled={loading}
              className="h-9 w-12 shrink-0 cursor-pointer rounded-md border border-input bg-background p-1"
            />
            <Input
              id="primaryColor"
              type="text"
              value={primaryColor}
              onChange={(e) => setPrimaryColor(e.target.value)}
              placeholder="#rrggbb"
              disabled={loading}
              className="max-w-xs"
            />
            {trimmedColor !== '' && (
              <span
                data-testid="primary-color-swatch"
                aria-hidden="true"
                className="size-6 shrink-0 rounded-full border border-border"
                style={{ backgroundColor: trimmedColor }}
              />
            )}
          </div>
        </div>

        {loadError && (
          <div
            role="alert"
            className="flex flex-col items-start gap-2 rounded-md border border-destructive/50 bg-destructive/10 p-3 text-sm text-destructive"
          >
            <p>{loadError}</p>
            <Button type="button" variant="outline" size="sm" onClick={handleRetry}>
              Retry
            </Button>
          </div>
        )}

        {submitError && (
          <p role="alert" className="text-sm text-destructive">
            {submitError}
          </p>
        )}

        <div className="flex gap-2">
          <Button type="submit" disabled={loading || submitting || Boolean(loadError)}>
            {submitting ? 'Saving…' : 'Save'}
          </Button>
          <Button type="button" variant="outline" onClick={onCancel} disabled={submitting}>
            Cancel
          </Button>
        </div>
      </form>
    </FormPage>
  )
}

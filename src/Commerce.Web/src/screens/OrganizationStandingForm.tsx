import { useEffect, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { ConfirmDialog } from '@/components/layout/ConfirmDialog'
import { FormPage } from '@/components/layout/FormPage'
import { getOrganizationStanding, reactivateOrganization, suspendOrganization, updateOrganizationStanding } from '@/api/account'
import { ApiError } from '@/api/client'
import type { OrganizationAccountStanding, OrganizationSummary } from '@/api/types'
import { formatIsoDate } from '@/dashboard/format'
import { todayIso } from '@/lib/isoDate'

interface OrganizationStandingFormProps {
  organization: OrganizationSummary
  onSaved: () => void
  onCancel: () => void
}

const MAX_GRACE_DAYS = 90

/**
 * organization-account-standing T6: the system administrator's account-standing editor for one organization. Same
 * full-screen state-swap and `FormPage` shell as `OrganizationBrandingForm`.
 *
 * - Not suspended by hand: Save sets the due date (empty = billing not tracked) and grace days; recording a payment is
 *   moving the due date to the next period. "Suspender ahora" suspends at once, after a confirmation.
 * - Suspended by hand: the submit action becomes "Reactivar", which needs a due date of today or later (the server
 *   enforces the same rule) and lifts the suspension together with it.
 *
 * The rules (what Overdue and Suspended mean) live on the server; this form only shows what it answers.
 */
export function OrganizationStandingForm({ organization, onSaved, onCancel }: OrganizationStandingFormProps) {
  const { t } = useTranslation('organizations')
  const [standing, setStanding] = useState<OrganizationAccountStanding | null>(null)
  const [dueOn, setDueOn] = useState('')
  const [graceDays, setGraceDays] = useState('')
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [fieldError, setFieldError] = useState<string | null>(null)
  const [submitError, setSubmitError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const [confirmingSuspend, setConfirmingSuspend] = useState(false)
  const [suspending, setSuspending] = useState(false)
  const [reloadToken, setReloadToken] = useState(0)

  useEffect(() => {
    let cancelled = false
    setLoading(true)
    setLoadError(null)
    getOrganizationStanding(organization.id)
      .then((loaded) => {
        if (cancelled) return
        setStanding(loaded)
        setDueOn(loaded.dueOn ?? '')
        setGraceDays(String(loaded.graceDays))
      })
      .catch((err) => {
        if (cancelled) return
        setLoadError(err instanceof ApiError ? err.message : t('standingForm.unableToLoad'))
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })
    return () => {
      cancelled = true
    }
  }, [organization.id, reloadToken, t])

  const suspendedByHand = standing?.suspendedAt != null

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault()
    if (loadError || standing === null) return
    setFieldError(null)
    setSubmitError(null)

    const trimmedGrace = graceDays.trim()
    const parsedGrace = Number(trimmedGrace)
    if (!/^\d+$/.test(trimmedGrace) || parsedGrace > MAX_GRACE_DAYS) {
      setFieldError(t('standingForm.graceInvalid'))
      return
    }
    // yyyy-MM-dd strings compare in calendar order.
    if (suspendedByHand && (dueOn === '' || dueOn < todayIso())) {
      setFieldError(t('standingForm.reactivateDueOnInvalid'))
      return
    }

    setSubmitting(true)
    try {
      if (suspendedByHand) {
        await reactivateOrganization(organization.id, { dueOn, graceDays: parsedGrace })
      } else {
        await updateOrganizationStanding(organization.id, { dueOn: dueOn === '' ? null : dueOn, graceDays: parsedGrace })
      }
      onSaved()
    } catch (err) {
      setSubmitError(err instanceof ApiError ? err.message : t('standingForm.unableToSave'))
    } finally {
      setSubmitting(false)
    }
  }

  const handleSuspend = async () => {
    setSuspending(true)
    setSubmitError(null)
    try {
      await suspendOrganization(organization.id)
      // Only the standing is re-read: the fields keep what is being typed (a suspension does not change them).
      setStanding(await getOrganizationStanding(organization.id))
      setConfirmingSuspend(false)
    } catch (err) {
      setConfirmingSuspend(false)
      setSubmitError(err instanceof ApiError ? err.message : t('standingForm.unableToSave'))
    } finally {
      setSuspending(false)
    }
  }

  return (
    <FormPage
      title={t('standingForm.title')}
      description={t('standingForm.description', { name: organization.name })}
      onBack={onCancel}
      backLabel={t('standingForm.backLabel')}
    >
      {/* noValidate: the min/max hints stay on the input, but the message shown is this form's own, in the user's language. */}
      <form onSubmit={handleSubmit} noValidate className="flex flex-col gap-6">
        {standing && (
          <div className="flex flex-col gap-1">
            <p className="text-sm font-medium">{t('standingForm.statusLabel')}</p>
            <p className={standing.status === 'Active' ? 'text-sm' : 'text-sm font-medium text-destructive'}>
              {describeStanding(standing, t)}
            </p>
          </div>
        )}

        <div className="flex flex-col gap-1.5">
          <Label htmlFor="standingDueOn">{t('standingForm.dueOnLabel')}</Label>
          <Input
            id="standingDueOn"
            type="date"
            value={dueOn}
            onChange={(e) => setDueOn(e.target.value)}
            disabled={loading}
            className="max-w-xs"
          />
          <p className="text-xs text-muted-foreground">{t('standingForm.dueOnHint')}</p>
        </div>

        <div className="flex flex-col gap-1.5">
          <Label htmlFor="standingGraceDays">{t('standingForm.graceDaysLabel')}</Label>
          <Input
            id="standingGraceDays"
            type="number"
            min={0}
            max={MAX_GRACE_DAYS}
            step={1}
            value={graceDays}
            onChange={(e) => setGraceDays(e.target.value)}
            disabled={loading}
            className="max-w-[8rem]"
          />
          <p className="text-xs text-muted-foreground">{t('standingForm.graceDaysHint')}</p>
        </div>

        {fieldError && (
          <p role="alert" className="text-sm text-destructive">
            {fieldError}
          </p>
        )}

        {loadError && (
          <div
            role="alert"
            className="flex flex-col items-start gap-2 rounded-md border border-destructive/50 bg-destructive/10 p-3 text-sm text-destructive"
          >
            <p>{loadError}</p>
            <Button type="button" variant="outline" size="sm" onClick={() => setReloadToken((token) => token + 1)}>
              {t('standingForm.retry')}
            </Button>
          </div>
        )}

        {submitError && (
          <p role="alert" className="text-sm text-destructive">
            {submitError}
          </p>
        )}

        <div className="flex flex-wrap gap-2">
          <Button type="submit" disabled={loading || submitting || Boolean(loadError)}>
            {suspendedByHand
              ? submitting ? t('standingForm.reactivating') : t('standingForm.reactivate')
              : submitting ? t('standingForm.saving') : t('standingForm.save')}
          </Button>
          <Button type="button" variant="outline" onClick={onCancel} disabled={submitting}>
            {t('standingForm.cancel')}
          </Button>
          {standing !== null && standing.status !== 'Suspended' && (
            <Button
              type="button"
              variant="destructive"
              className="sm:ml-auto"
              onClick={() => setConfirmingSuspend(true)}
              disabled={loading || submitting}
            >
              {t('standingForm.suspendNow')}
            </Button>
          )}
        </div>
      </form>

      {confirmingSuspend && (
        <ConfirmDialog
          title={t('standingForm.suspendTitle')}
          message={t('standingForm.suspendMessage', { name: organization.name })}
          confirmLabel={t('standingForm.suspendConfirm')}
          busyLabel={t('standingForm.suspending')}
          busy={suspending}
          destructive
          onConfirm={() => void handleSuspend()}
          onCancel={() => setConfirmingSuspend(false)}
        />
      )}
    </FormPage>
  )
}

/** A timestamp's calendar date on the business day (Buenos Aires, like the server's `IBusinessClock`), `dd/MM/yyyy`. */
const businessDate = new Intl.DateTimeFormat('es-AR', {
  timeZone: 'America/Argentina/Buenos_Aires',
  day: '2-digit',
  month: '2-digit',
  year: 'numeric',
})

function describeStanding(standing: OrganizationAccountStanding, t: (key: string, options?: Record<string, unknown>) => string): string {
  if (standing.suspendedAt != null) {
    return t('standingForm.suspendedManually', { date: businessDate.format(new Date(standing.suspendedAt)) })
  }
  if (standing.status === 'Suspended') {
    return t('standingForm.statusSuspended', { date: standing.suspendsOn ? formatIsoDate(standing.suspendsOn) : '—' })
  }
  if (standing.status === 'Overdue' && standing.suspendsOn != null && standing.daysLeft != null) {
    return t('standingForm.statusOverdue', { count: standing.daysLeft, date: formatIsoDate(standing.suspendsOn) })
  }
  return standing.dueOn == null ? t('standingForm.statusActiveUntracked') : t('standingForm.statusActive')
}

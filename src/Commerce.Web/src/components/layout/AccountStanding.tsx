import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { AlertTriangle, Lock } from 'lucide-react'
import type { AccountStandingSummary } from '@/api/types'
import { Button } from '@/components/ui/button'
import { formatIsoDate } from '@/dashboard/format'
import { todayIso } from '@/lib/isoDate'

/** Whole calendar days from `from` to `to` (both `yyyy-MM-dd`); NaN when either cannot be read. */
function daysBetween(from: string, to: string): number {
  const utc = (isoDate: string) => {
    const [year, month, day] = isoDate.split('-').map(Number)
    return Date.UTC(year, month - 1, day)
  }
  return Math.round((utc(to) - utc(from)) / 86_400_000)
}

/**
 * organization-account-standing T7: the overdue countdown. Rendered only when the server sent the dates, which it does
 * only to administrators (`ManageUsers`) and the system administrator; everyone else sees nothing while the account
 * is overdue (owner decision: a commercial matter is not shown to the staff on the web).
 *
 * The count moves with the days: the session lives in memory, and a tab left open for days must not keep showing the
 * count of the day it signed in. Once it reaches zero the banner hides; the server then answers 403
 * organization-suspended and the suspended screen takes over. A date it cannot read shows nothing, never NaN.
 */
export function AccountOverdueBanner({ standing }: { standing: AccountStandingSummary | null | undefined }) {
  const { t } = useTranslation('common')
  if (standing?.status !== 'Overdue' || standing.suspendsOn == null) return null

  // Preferred: the server's count minus the days elapsed since it arrived (only a RELATIVE use of the PC clock, so a
  // clock set to the wrong date does not skew it). Without that stamp, count to `suspendsOn` from today.
  const today = todayIso()
  const daysLeft =
    standing.daysLeft != null && standing.receivedOn
      ? standing.daysLeft - daysBetween(standing.receivedOn, today)
      : daysBetween(today, standing.suspendsOn)
  if (!Number.isFinite(daysLeft) || daysLeft < 1 || !Number.isFinite(daysBetween(today, standing.suspendsOn))) return null

  return (
    <div
      role="status"
      className="mb-4 flex items-start gap-3 rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive"
    >
      <AlertTriangle aria-hidden="true" className="mt-0.5 size-4 shrink-0" />
      <p>{t('accountStanding.overdue', { count: daysLeft, date: formatIsoDate(standing.suspendsOn) })}</p>
    </div>
  )
}

/**
 * What a suspended organization's web users see instead of the app. Administrators learn why and whom to call;
 * everyone else is pointed to their administrator, without the reason. `onCheckAgain` re-reads the standing, so a
 * reactivation is picked up without signing in again.
 */
export function AccountSuspendedScreen({ isAdmin, onCheckAgain }: { isAdmin: boolean; onCheckAgain?: () => Promise<void> }) {
  const { t } = useTranslation('common')
  const [checking, setChecking] = useState(false)

  const checkAgain = async () => {
    if (!onCheckAgain) return
    setChecking(true)
    try {
      await onCheckAgain()
    } catch {
      // Still unreachable or still suspended: the screen simply stays.
    } finally {
      setChecking(false)
    }
  }

  return (
    <section className="mx-auto mt-12 flex max-w-lg flex-col items-center gap-4 rounded-lg border border-border bg-card p-8 text-center">
      <Lock aria-hidden="true" className="size-10 text-destructive" />
      <h2 className="text-xl font-semibold">{t('accountStanding.suspendedTitle')}</h2>
      <p className="text-muted-foreground">
        {isAdmin ? t('accountStanding.suspendedAdmin') : t('accountStanding.suspendedStaff')}
      </p>
      {onCheckAgain && (
        <Button variant="outline" onClick={() => void checkAgain()} disabled={checking}>
          {checking ? t('accountStanding.checking') : t('accountStanding.checkAgain')}
        </Button>
      )}
    </section>
  )
}

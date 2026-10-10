import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { AlertTriangle, Lock } from 'lucide-react'
import type { AccountStandingSummary } from '@/api/types'
import { Button } from '@/components/ui/button'
import { formatIsoDate } from '@/dashboard/format'

/** Whole calendar days from `today` (local) to the `yyyy-MM-dd` date; negative once it has passed. */
function daysUntil(isoDate: string, today: Date): number {
  const [year, month, day] = isoDate.split('-').map(Number)
  const target = Date.UTC(year, month - 1, day)
  const start = Date.UTC(today.getFullYear(), today.getMonth(), today.getDate())
  return Math.round((target - start) / 86_400_000)
}

/**
 * organization-account-standing T7: the overdue countdown. Rendered only when the server sent the dates, which it does
 * only to administrators (`ManageUsers`) and the system administrator; everyone else sees nothing while the account
 * is overdue (owner decision: a commercial matter is not shown to the staff on the web).
 *
 * The days left are counted from today against `suspendsOn`, not taken from the sign-in response: the session lives
 * in memory, and a tab left open for days must not keep showing the count of the day it signed in. Once the date is
 * reached the banner hides; the server then answers 403 organization-suspended and the suspended screen takes over.
 */
export function AccountOverdueBanner({ standing }: { standing: AccountStandingSummary | null | undefined }) {
  const { t } = useTranslation('common')
  if (standing?.status !== 'Overdue' || standing.suspendsOn == null) return null

  const daysLeft = daysUntil(standing.suspendsOn, new Date())
  if (daysLeft < 1) return null

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

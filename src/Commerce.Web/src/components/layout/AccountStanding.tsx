import { useTranslation } from 'react-i18next'
import { AlertTriangle, Lock } from 'lucide-react'
import type { AccountStandingSummary } from '@/api/types'
import { formatIsoDate } from '@/dashboard/format'

/**
 * organization-account-standing T7: the overdue countdown. Rendered only when the server sent the dates, which it does
 * only to administrators (`ManageUsers`) and the system administrator; everyone else sees nothing while the account
 * is overdue (owner decision: a commercial matter is not shown to the staff on the web).
 */
export function AccountOverdueBanner({ standing }: { standing: AccountStandingSummary | null | undefined }) {
  const { t } = useTranslation('common')
  if (standing?.status !== 'Overdue' || standing.daysLeft == null || standing.suspendsOn == null) return null

  return (
    <div
      role="status"
      className="mb-4 flex items-start gap-3 rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive"
    >
      <AlertTriangle aria-hidden="true" className="mt-0.5 size-4 shrink-0" />
      <p>{t('accountStanding.overdue', { count: standing.daysLeft, date: formatIsoDate(standing.suspendsOn) })}</p>
    </div>
  )
}

/**
 * What a suspended organization's web users see instead of the app. Administrators learn why and whom to call;
 * everyone else is pointed to their administrator, without the reason.
 */
export function AccountSuspendedScreen({ isAdmin }: { isAdmin: boolean }) {
  const { t } = useTranslation('common')

  return (
    <section className="mx-auto mt-12 flex max-w-lg flex-col items-center gap-4 rounded-lg border border-border bg-card p-8 text-center">
      <Lock aria-hidden="true" className="size-10 text-destructive" />
      <h2 className="text-xl font-semibold">{t('accountStanding.suspendedTitle')}</h2>
      <p className="text-muted-foreground">
        {isAdmin ? t('accountStanding.suspendedAdmin') : t('accountStanding.suspendedStaff')}
      </p>
    </section>
  )
}

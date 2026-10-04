import { Outlet } from 'react-router'
import { useTranslation } from 'react-i18next'
import { useAuth } from '@/auth/AuthContext'
import { canTakeOrders } from '@/auth/canTakeOrders'
import { PageHeader } from '@/components/data/PageHeader'
import { useOptionalOrganizationContext } from '@/organization/OrganizationContext'

/**
 * staff-order-taking: `/app/orders` for a user who may take orders. Unlike `RequireAdmin` it does not redirect:
 * a cashier who opens the address directly sees a clear "no access" state instead of landing somewhere
 * unexpected. A UX guard only; every `/orders/staff` call answers 403 without `TakeOrders`.
 */
export function RequireTakeOrders() {
  const { t } = useTranslation('orders')
  const { user } = useAuth()
  const actingOnSelectedOrganization = useOptionalOrganizationContext()?.selectedOrganization != null

  if (canTakeOrders(user, actingOnSelectedOrganization)) return <Outlet />

  return (
    <section className="flex w-full flex-col gap-6">
      <PageHeader title={t('staffOrder.title')} />
      <p role="status" className="rounded-lg border border-border bg-card px-4 py-10 text-center text-sm text-muted-foreground">
        {t('staffOrder.noAccess')}
      </p>
    </section>
  )
}

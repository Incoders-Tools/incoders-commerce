import { Permission, type SignedInResponse } from '@/api/types'
import { hasPermission } from './AuthContext'

/**
 * staff-order-taking: sellers and business admins hold `TakeOrders`; a system administrator holds no bits of
 * their own but the server elevates them while they act on a selected organization, so the web mirrors that.
 * The nav, the `/app` landing and the `/app/orders` guard share this rule; the server's check is the real one.
 */
export function canTakeOrders(user: SignedInResponse | null, actingOnSelectedOrganization: boolean): boolean {
  return hasPermission(user, Permission.TakeOrders) || (Boolean(user?.isSystemAdmin) && actingOnSelectedOrganization)
}

import type { DashboardData, DashboardQuery } from './types'

/**
 * Port the dashboard screen depends on. The only implementation today is the
 * mock in `mockDashboardSource.ts`; a real one will call Cloud.Api aggregate
 * endpoints (see odd/tasks/business-dashboard.md, "Real-data cutover").
 * (File is not named after the type: `DashboardSource.ts` would collide with
 * `dashboardSource.ts` on case-insensitive file systems.)
 */
export interface DashboardSource {
  load(query: DashboardQuery): Promise<DashboardData>
}

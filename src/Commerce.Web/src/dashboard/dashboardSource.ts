// TEMPORARY MOCK — remove when the owner starts real data; see odd/tasks/business-dashboard.md
import type { DashboardSource } from './port'
import { mockDashboardSource } from './mockDashboardSource'

/**
 * The single place that selects the dashboard data source. Cutover to real
 * data is one swap here, plus dropping `SAMPLE_DATA` (the sample-data notice).
 */
export const dashboardSource: DashboardSource = mockDashboardSource

/** True while the selected source is the mock; drives the "sample data" notice. */
export const SAMPLE_DATA = true

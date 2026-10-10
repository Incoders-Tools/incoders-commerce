/** The business time zone, the same the server's `IBusinessClock` uses (`Business:TimeZone`, default Buenos Aires). */
export const BUSINESS_TIME_ZONE = 'America/Argentina/Buenos_Aires'

/**
 * A timestamp's calendar date, `dd/MM/yyyy`, in `timeZone` (the business zone by default), never the browser's own
 * zone. The zone is a parameter so the behavior is testable on any machine.
 */
export function formatInstantDate(instant: string, timeZone: string = BUSINESS_TIME_ZONE): string {
  return new Intl.DateTimeFormat('es-AR', { timeZone, day: '2-digit', month: '2-digit', year: 'numeric' }).format(new Date(instant))
}

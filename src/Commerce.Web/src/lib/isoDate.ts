const pad = (n: number) => String(n).padStart(2, '0')

/** `yyyy-MM-dd` of a date in the browser's local calendar (what an operator means by "today"). */
export function toIsoDate(date: Date): string {
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`
}

export const todayIso = (): string => toIsoDate(new Date())

/** The local date `days` before today, as `yyyy-MM-dd`. */
export function daysAgoIso(days: number): string {
  const date = new Date()
  date.setDate(date.getDate() - days)
  return toIsoDate(date)
}

/**
 * The same day of the month before `iso` (`yyyy-MM-dd`), or that month's last day when it is shorter (31/03 → 28/02),
 * as the server's `DateOnly.AddMonths(-1)`: where "the last month" starts.
 */
export function monthBeforeIso(iso: string): string {
  const [year, month, day] = iso.split('-').map(Number)
  const lastDay = new Date(year, month - 1, 0).getDate()
  return toIsoDate(new Date(year, month - 2, Math.min(day, lastDay)))
}

/** The local date after today, as `yyyy-MM-dd`: the earliest day a published change can take effect. */
export const tomorrowIso = (): string => daysAgoIso(-1)

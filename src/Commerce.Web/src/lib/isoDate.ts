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

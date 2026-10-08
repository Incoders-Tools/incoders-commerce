import type { MasterDataEntry } from '@/api/types'

/** Active entries, plus the one the record currently has even when it was deactivated. */
export function selectableEntries(
  entries: MasterDataEntry[],
  currentId: string | null | undefined,
  currentName: string | null | undefined,
  inactiveSuffix: string,
): { id: string; name: string }[] {
  const options = entries
    .filter((entry) => entry.isActive || entry.id === currentId)
    .map((entry) => ({ id: entry.id, name: entry.isActive ? entry.name : `${entry.name} ${inactiveSuffix}` }))
  if (currentId && !options.some((option) => option.id === currentId)) {
    options.push({ id: currentId, name: currentName ?? currentId })
  }
  return options
}

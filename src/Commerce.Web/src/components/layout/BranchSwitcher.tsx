import { type ChangeEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Store } from 'lucide-react'
import { Select } from '@/components/ui/select'
import { useOptionalBranchContext } from '@/branch/BranchContext'

/**
 * admin-console spec, "Top Navbar Branch Switcher": populated from the
 * session's selectable branches (`branch/BranchContext.tsx`). Hidden
 * entirely when there is nothing to select (no selectable branch, e.g. a
 * sysadmin with no organization selected yet); a single branch renders as a
 * static label instead of a pointless one-option dropdown.
 */
export function BranchSwitcher() {
  const { t } = useTranslation('nav')
  const branchContext = useOptionalBranchContext()

  if (!branchContext) return null

  const { selectedBranch, selectableBranches, selectBranch } = branchContext

  if (selectableBranches.length === 0) return null

  if (selectableBranches.length === 1) {
    return (
      <div className="flex items-center gap-2 text-sm text-muted-foreground">
        <Store aria-hidden="true" className="size-4 shrink-0" />
        <span className="font-medium text-foreground">{selectableBranches[0].name}</span>
      </div>
    )
  }

  const handleChange = (event: ChangeEvent<HTMLSelectElement>) => {
    const branch = selectableBranches.find((candidate) => candidate.id === event.target.value)
    if (branch) selectBranch(branch)
  }

  return (
    <div className="flex items-center gap-2">
      <Store aria-hidden="true" className="size-4 shrink-0 text-muted-foreground" />
      <Select
        id="branch-switcher"
        aria-label={t('branchSwitcher.label')}
        value={selectedBranch?.id ?? ''}
        onChange={handleChange}
        className="h-8 w-auto"
      >
        {selectableBranches.map((branch) => (
          <option key={branch.id} value={branch.id}>
            {branch.name}
          </option>
        ))}
      </Select>
    </div>
  )
}

import { useOptionalBranchContext } from './BranchContext'

/**
 * True when a branch selector exists but nothing is selected. Branch-scoped
 * screens (receptions, stock) answer 400 `branch-selection-required` without
 * a branch, so they show a clear state instead of calling the API. Hosts
 * without a `BranchProvider` (isolated screen tests) are never "missing".
 */
export function useMissingBranch(): boolean {
  const context = useOptionalBranchContext()
  return context !== null && context.selectedBranch === null
}

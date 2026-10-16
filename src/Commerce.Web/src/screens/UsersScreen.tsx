import { useCallback, useEffect, useMemo, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { adminResetPassword, createUser, listUsers, updateUserBranches, updateUserRoles, updateUserStatus } from '@/api/account'
import { ApiError } from '@/api/client'
import type { UserSummary } from '@/api/types'
import { useOptionalAuth } from '@/auth/AuthContext'
import { useOptionalBranchContext } from '@/branch/BranchContext'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { DataToolbar } from '@/components/data/DataToolbar'
import { DataView, type DataViewColumn } from '@/components/data/DataView'
import { PageHeader } from '@/components/data/PageHeader'
import { useViewPreference } from '@/components/data/useViewPreference'
import { EmailField } from '@/components/form/EmailField'
import { emailStatus } from '@/lib/email'

/**
 * What an organization-scoped caller may grant. Deliberately excludes
 * `platform-admin`: `Commerce.Domain/Identity/RoleCatalog.cs` keeps it out of
 * the organization-assignable set, and the server enforces that regardless of
 * what this screen offers.
 */
const assignableRoles = ['business-admin', 'seller', 'cashier', 'provider']

/**
 * T4b: migrated onto the shared data-view layer (`components/data/*`),
 * following `CatalogScreen.tsx`, and reformatted — the pre-T4b file packed the
 * whole screen into two unreadable ~1 KB lines.
 *
 * The create form stays permanently visible rather than moving behind a
 * "New user" toggle in `PageHeader`: `e2e/admin-console.spec.ts` fills
 * `User email` / `User password` straight after navigating to this screen,
 * and hiding the form would silently break that real-backend journey.
 *
 * Search is a client-side filter over what `GET /account/users` already
 * returned; there is no server-side user search endpoint.
 */
export function UsersScreen() {
  const { t } = useTranslation('users')
  const branchContext = useOptionalBranchContext()
  const ownUserId = useOptionalAuth()?.user?.userId ?? null
  const selectableBranches = useMemo(() => branchContext?.selectableBranches ?? [], [branchContext])
  const selectedBranchId = branchContext?.selectedBranch?.id ?? null
  const [users, setUsers] = useState<UserSummary[]>([])
  const [email, setEmail] = useState('')
  const [emailError, setEmailError] = useState<string | null>(null)
  const [password, setPassword] = useState('')
  const [roles, setRoles] = useState<string[]>(['seller'])
  /** The administrator's own choice for the new user; `null` until they touch it. */
  const [branchOverride, setBranchOverride] = useState<string[] | null>(null)
  // Until touched it follows the branch currently selected in the shell (which
  // can arrive late for a sysadmin acting on an organization).
  const branchIds = branchOverride ?? (selectedBranchId ? [selectedBranchId] : [])
  /** Per-row branch selection, keyed by user id and re-seeded from the server on every load. */
  const [rowBranches, setRowBranches] = useState<Record<string, string[]>>({})
  /** Per-row role selection, keyed by user id and re-seeded from the server on every load. */
  const [rowRoles, setRowRoles] = useState<Record<string, string[]>>({})
  const [resetPasswords, setResetPasswords] = useState<Record<string, string>>({})
  /** Why the last load failed, if it did. Never set by an action: an action
   * failing says nothing about whether the collection could be read. */
  const [loadError, setLoadError] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  /** The row waiting for the administrator to confirm a deactivation or reactivation. */
  const [pendingStatusUserId, setPendingStatusUserId] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [search, setSearch] = useState('')
  const [view, setView] = useViewPreference('users')

  const roleLabel = useCallback((role: string) => t(`roles.${role}.label`, { defaultValue: role }), [t])

  const refresh = useCallback(async () => {
    try {
      const loaded = await listUsers()
      setUsers(loaded)
      setRowRoles(Object.fromEntries(loaded.map((user) => [user.userId, user.roleNames])))
      setRowBranches(Object.fromEntries(loaded.map((user) => [user.userId, user.branchIds])))
      setLoadError(null)
    } catch {
      setLoadError(t('errors.unableToLoad'))
    } finally {
      setLoading(false)
    }
  }, [t])

  useEffect(() => {
    void refresh()
  }, [refresh])

  /**
   * Friendly text for the typed API failures (`{ "error": "<code>" }` and the
   * grant-cap 403); `null` when the failure has no dedicated message.
   */
  const friendlyError = (err: unknown): string | null => {
    if (!(err instanceof ApiError)) return null
    if (err.code === 'branch-required') return t('errors.branchRequired')
    if (err.code === 'branch-not-in-organization') return t('errors.branchNotInOrganization')
    if (err.status === 403) return t('errors.forbidden')
    return null
  }

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    setActionError(null)
    // Mirrors the server: a staff user must belong to at least one branch.
    if (branchIds.length === 0) {
      setActionError(t('errors.branchRequired'))
      return
    }
    if (emailStatus(email) !== 'valid') {
      setEmailError(t('common:email.invalid'))
      return
    }
    try {
      await createUser({ email: email.trim(), password, roleNames: roles, branchIds })
      setEmail('')
      setPassword('')
      await refresh()
    } catch (err) {
      if (err instanceof ApiError && err.status === 400 && err.fieldErrors?.email) {
        setEmailError(t('common:email.invalid'))
        return
      }
      setActionError(friendlyError(err) ?? t('errors.unableToCreate'))
    }
  }

  const toggleBranch = (branchId: string) =>
    setBranchOverride(
      branchIds.includes(branchId) ? branchIds.filter((x) => x !== branchId) : [...branchIds, branchId],
    )

  const selectedBranchesFor = (user: UserSummary) => rowBranches[user.userId] ?? user.branchIds

  const toggleRowBranch = (user: UserSummary, branchId: string) =>
    setRowBranches((current) => {
      const selected = current[user.userId] ?? user.branchIds
      return {
        ...current,
        [user.userId]: selected.includes(branchId) ? selected.filter((x) => x !== branchId) : [...selected, branchId],
      }
    })

  const saveBranches = async (user: UserSummary) => {
    setActionError(null)
    const selected = selectedBranchesFor(user)
    if (selected.length === 0) {
      setActionError(t('errors.branchRequired'))
      return
    }
    try {
      await updateUserBranches(user.userId, selected)
      await refresh()
    } catch (err) {
      setRowBranches((current) => ({ ...current, [user.userId]: user.branchIds }))
      setActionError(friendlyError(err) ?? t('errors.unableToSaveBranches', { email: user.email }))
    }
  }

  const branchName = (branchId: string) =>
    selectableBranches.find((branch) => branch.id === branchId)?.name ?? t('columns.unknownBranch')

  const toggle = (role: string) =>
    setRoles((current) => (current.includes(role) ? current.filter((x) => x !== role) : [...current, role]))

  const selectedRolesFor = (user: UserSummary) => rowRoles[user.userId] ?? user.roleNames

  const toggleRowRole = (user: UserSummary, role: string) =>
    setRowRoles((current) => {
      const selected = current[user.userId] ?? user.roleNames
      return {
        ...current,
        [user.userId]: selected.includes(role) ? selected.filter((x) => x !== role) : [...selected, role],
      }
    })

  const saveRoles = async (user: UserSummary) => {
    setActionError(null)
    try {
      await updateUserRoles(user.userId, selectedRolesFor(user))
      // Re-read the list so the row shows what the server actually stored.
      await refresh()
    } catch (err) {
      // The server applies a grant cap (a caller cannot hand out permissions
      // it does not itself hold). Discarding this promise would make the
      // rejection invisible and leave the row advertising roles that were
      // never persisted, so surface it and fall back to the stored roles.
      setRowRoles((current) => ({ ...current, [user.userId]: user.roleNames }))
      setActionError(
        friendlyError(err) ??
          (err instanceof ApiError
            ? t('errors.unableToSaveRolesWithDetail', { email: user.email, detail: err.message })
            : t('errors.unableToSaveRoles', { email: user.email })),
      )
    }
  }

  const statusErrorFor = (err: unknown, user: UserSummary): string => {
    if (err instanceof ApiError) {
      if (err.code === 'cannot-revoke-self') return t('errors.cannotRevokeSelf')
      if (err.status === 403) return t('errors.forbiddenStatus')
    }
    return t('errors.unableToChangeStatus', { email: user.email })
  }

  const confirmStatusChange = async (user: UserSummary) => {
    setActionError(null)
    setPendingStatusUserId(null)
    try {
      await updateUserStatus(user.userId, !user.isRevoked)
      await refresh()
    } catch (err) {
      setActionError(statusErrorFor(err, user))
    }
  }

  const forceReset = async (userId: string) => {
    const newPassword = resetPasswords[userId]?.trim()
    if (!newPassword) {
      setActionError(t('errors.enterReplacementPassword'))
      return
    }
    setActionError(null)
    try {
      await adminResetPassword(userId, { newPassword })
      setResetPasswords((current) => ({ ...current, [userId]: '' }))
    } catch {
      setActionError(t('errors.unableToResetPassword'))
    }
  }

  const trimmedSearch = search.trim().toLowerCase()
  const visibleUsers = useMemo(() => {
    if (trimmedSearch === '') return users
    return users.filter(
      (user) =>
        user.email.toLowerCase().includes(trimmedSearch) ||
        user.roleNames.some(
          (role) =>
            role.toLowerCase().includes(trimmedSearch) || roleLabel(role).toLowerCase().includes(trimmedSearch),
        ),
    )
  }, [users, trimmedSearch, roleLabel])

  const columns: DataViewColumn<UserSummary>[] = [
    { key: 'email', header: t('columns.email'), cell: (user) => user.email },
    {
      key: 'roleNames',
      header: t('columns.roles'),
      cell: (user) =>
        user.roleNames.length > 0 ? (
          user.roleNames.map(roleLabel).join(', ')
        ) : (
          <span className="text-muted-foreground">{t('columns.noRoles')}</span>
        ),
    },
    {
      key: 'branchIds',
      header: t('columns.branches'),
      cell: (user) =>
        user.branchIds.length > 0 ? (
          user.branchIds.map(branchName).join(', ')
        ) : (
          <span className="text-muted-foreground">{t('columns.noBranches')}</span>
        ),
    },
    {
      key: 'isRevoked',
      header: t('columns.status'),
      cell: (user) => (user.isRevoked ? t('statusOptions.revoked') : t('statusOptions.active')),
      hideOnMobile: true,
    },
  ]

  return (
    <section className="flex w-full flex-col gap-6">
      <PageHeader title={t('title')} description={t('description')} />

      {loadError && (
        <p role="alert" className="rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {loadError}
        </p>
      )}

      {actionError && (
        <p role="alert" className="rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {actionError}
        </p>
      )}

      <form
        onSubmit={submit}
        className="flex flex-col gap-3 rounded-lg border border-border bg-card p-4 sm:flex-row sm:flex-wrap sm:items-center"
      >
        <EmailField
          id="newUserEmail"
          label={t('createForm.emailAriaLabel')}
          hideLabel
          className="w-full sm:max-w-xs"
          placeholder={t('createForm.emailPlaceholder')}
          value={email}
          onChange={(value) => {
            setEmail(value)
            setEmailError(null)
          }}
          error={emailError}
          required
        />
        <Input
          aria-label={t('createForm.passwordAriaLabel')}
          className="sm:max-w-xs"
          placeholder={t('createForm.passwordPlaceholder')}
          type="password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          required
        />
        <div className="flex flex-wrap items-start gap-3">
          {assignableRoles.map((role) => (
            <label key={role} className="flex items-start gap-1.5 text-sm text-foreground">
              <input
                type="checkbox"
                className="mt-1"
                aria-label={t('createForm.roleForNewUser', { role: roleLabel(role) })}
                checked={roles.includes(role)}
                onChange={() => toggle(role)}
              />
              <span className="flex flex-col">
                {roleLabel(role)}
                <span className="text-xs text-muted-foreground">{t(`roles.${role}.description`)}</span>
              </span>
            </label>
          ))}
        </div>
        <fieldset className="flex flex-wrap items-center gap-3">
          <legend className="sr-only">{t('createForm.branchesLegend')}</legend>
          {selectableBranches.length === 0 && (
            <span className="text-sm text-muted-foreground">{t('createForm.noBranchesAvailable')}</span>
          )}
          {selectableBranches.map((branch) => (
            <label key={branch.id} className="flex items-center gap-1.5 text-sm text-foreground">
              <input
                type="checkbox"
                aria-label={t('createForm.branchForNewUser', { branch: branch.name })}
                checked={branchIds.includes(branch.id)}
                onChange={() => toggleBranch(branch.id)}
              />
              {branch.name}
            </label>
          ))}
        </fieldset>
        <Button type="submit">{t('createForm.submit')}</Button>
      </form>

      <DataToolbar
        searchValue={search}
        onSearchChange={setSearch}
        searchLabel={t('search.label')}
        searchPlaceholder={t('search.placeholder')}
        view={view}
        onViewChange={setView}
      />

      <DataView
        items={visibleUsers}
        columns={columns}
        getRowKey={(user) => user.userId}
        view={view}
        loading={loading}
        emptyMessage={users.length === 0 ? t('empty.none') : t('empty.noMatch')}
        loadErrorMessage={loadError === null ? null : t('empty.loadError')}
        renderActions={(user) => (
          <>
            {assignableRoles.map((role) => (
              <label key={role} className="flex items-center gap-1.5 text-sm text-foreground">
                <input
                  type="checkbox"
                  aria-label={t('row.roleForUser', { role: roleLabel(role), email: user.email })}
                  checked={selectedRolesFor(user).includes(role)}
                  onChange={() => toggleRowRole(user, role)}
                />
                {roleLabel(role)}
              </label>
            ))}
            <Button size="sm" onClick={() => void saveRoles(user)}>
              {t('row.saveRoles')}
            </Button>
            {selectableBranches.map((branch) => (
              <label key={branch.id} className="flex items-center gap-1.5 text-sm text-foreground">
                <input
                  type="checkbox"
                  aria-label={t('row.branchForUser', { branch: branch.name, email: user.email })}
                  checked={selectedBranchesFor(user).includes(branch.id)}
                  onChange={() => toggleRowBranch(user, branch.id)}
                />
                {branch.name}
              </label>
            ))}
            <Button size="sm" onClick={() => void saveBranches(user)}>
              {t('row.saveBranches')}
            </Button>
            <Input
              aria-label={t('row.replacementPasswordAriaLabel', { email: user.email })}
              className="h-8 w-40"
              type="password"
              placeholder={t('row.newPasswordPlaceholder')}
              value={resetPasswords[user.userId] ?? ''}
              onChange={(e) => setResetPasswords((current) => ({ ...current, [user.userId]: e.target.value }))}
            />
            <Button size="sm" onClick={() => void forceReset(user.userId)}>
              {t('row.forceReset')}
            </Button>
            {user.userId !== ownUserId &&
              (pendingStatusUserId === user.userId ? (
                <span className="flex flex-wrap items-center gap-2 text-sm text-foreground">
                  {t(user.isRevoked ? 'row.confirmReactivate' : 'row.confirmDeactivate', { email: user.email })}
                  <Button size="sm" variant={user.isRevoked ? 'default' : 'destructive'} onClick={() => void confirmStatusChange(user)}>
                    {t('row.confirm')}
                  </Button>
                  <Button size="sm" variant="outline" onClick={() => setPendingStatusUserId(null)}>
                    {t('row.cancel')}
                  </Button>
                </span>
              ) : (
                <Button
                  size="sm"
                  variant={user.isRevoked ? 'default' : 'destructive'}
                  onClick={() => {
                    setActionError(null)
                    setPendingStatusUserId(user.userId)
                  }}
                >
                  {t(user.isRevoked ? 'row.reactivate' : 'row.deactivate')}
                </Button>
              ))}
          </>
        )}
      />
    </section>
  )
}

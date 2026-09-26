import { useCallback, useEffect, useMemo, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { adminResetPassword, createUser, listUsers, updateUserRoles } from '@/api/account'
import { ApiError } from '@/api/client'
import type { UserSummary } from '@/api/types'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { DataToolbar } from '@/components/data/DataToolbar'
import { DataView, type DataViewColumn } from '@/components/data/DataView'
import { PageHeader } from '@/components/data/PageHeader'
import { useViewPreference } from '@/components/data/useViewPreference'

/**
 * What an organization-scoped caller may grant. Deliberately excludes
 * `platform-admin`: `Commerce.Domain/Identity/RoleCatalog.cs` keeps it out of
 * the organization-assignable set, and the server enforces that regardless of
 * what this screen offers.
 */
const assignableRoles = ['business-admin', 'seller', 'provider']

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
  const [users, setUsers] = useState<UserSummary[]>([])
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [roles, setRoles] = useState<string[]>(['seller'])
  /** Per-row role selection, keyed by user id and re-seeded from the server on every load. */
  const [rowRoles, setRowRoles] = useState<Record<string, string[]>>({})
  const [resetPasswords, setResetPasswords] = useState<Record<string, string>>({})
  /** Why the last load failed, if it did. Never set by an action: an action
   * failing says nothing about whether the collection could be read. */
  const [loadError, setLoadError] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [search, setSearch] = useState('')
  const [view, setView] = useViewPreference('users')

  const refresh = useCallback(async () => {
    try {
      const loaded = await listUsers()
      setUsers(loaded)
      setRowRoles(Object.fromEntries(loaded.map((user) => [user.userId, user.roleNames])))
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

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    setActionError(null)
    try {
      await createUser({ email, password, roleNames: roles, branchIds: [] })
      setEmail('')
      setPassword('')
      await refresh()
    } catch {
      setActionError(t('errors.unableToCreate'))
    }
  }

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
        err instanceof ApiError
          ? t('errors.unableToSaveRolesWithDetail', { email: user.email, detail: err.message })
          : t('errors.unableToSaveRoles', { email: user.email }),
      )
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
        user.roleNames.some((role) => role.toLowerCase().includes(trimmedSearch)),
    )
  }, [users, trimmedSearch])

  const columns: DataViewColumn<UserSummary>[] = [
    { key: 'email', header: t('columns.email'), cell: (user) => user.email },
    {
      key: 'roleNames',
      header: t('columns.roles'),
      cell: (user) =>
        user.roleNames.length > 0 ? (
          user.roleNames.join(', ')
        ) : (
          <span className="text-muted-foreground">{t('columns.noRoles')}</span>
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
        <Input
          aria-label={t('createForm.emailAriaLabel')}
          className="sm:max-w-xs"
          placeholder={t('createForm.emailPlaceholder')}
          value={email}
          onChange={(e) => setEmail(e.target.value)}
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
        <div className="flex flex-wrap items-center gap-3">
          {assignableRoles.map((role) => (
            <label key={role} className="flex items-center gap-1.5 text-sm text-foreground">
              <input
                type="checkbox"
                aria-label={t('createForm.roleForNewUser', { role })}
                checked={roles.includes(role)}
                onChange={() => toggle(role)}
              />
              {role}
            </label>
          ))}
        </div>
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
                  aria-label={t('row.roleForUser', { role, email: user.email })}
                  checked={selectedRolesFor(user).includes(role)}
                  onChange={() => toggleRowRole(user, role)}
                />
                {role}
              </label>
            ))}
            <Button size="sm" onClick={() => void saveRoles(user)}>
              {t('row.saveRoles')}
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
          </>
        )}
      />
    </section>
  )
}

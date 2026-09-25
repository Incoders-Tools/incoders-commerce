import { useEffect, useMemo, useState, type FormEvent } from 'react'
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
  const [users, setUsers] = useState<UserSummary[]>([])
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [roles, setRoles] = useState<string[]>(['seller'])
  /** Per-row role selection, keyed by user id and re-seeded from the server on every load. */
  const [rowRoles, setRowRoles] = useState<Record<string, string[]>>({})
  const [resetPasswords, setResetPasswords] = useState<Record<string, string>>({})
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [search, setSearch] = useState('')
  const [view, setView] = useViewPreference('users')

  const refresh = async () => {
    try {
      const loaded = await listUsers()
      setUsers(loaded)
      setRowRoles(Object.fromEntries(loaded.map((user) => [user.userId, user.roleNames])))
    } catch {
      setError('Unable to load users.')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    void refresh()
  }, [])

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    try {
      await createUser({ email, password, roleNames: roles, branchIds: [] })
      setEmail('')
      setPassword('')
      await refresh()
    } catch {
      setError('Unable to create user.')
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
      setError(
        err instanceof ApiError
          ? `Unable to save roles for ${user.email}: ${err.message}`
          : `Unable to save roles for ${user.email}.`,
      )
    }
  }

  const forceReset = async (userId: string) => {
    const newPassword = resetPasswords[userId]?.trim()
    if (!newPassword) {
      setError('Enter a replacement password.')
      return
    }
    try {
      await adminResetPassword(userId, { newPassword })
      setResetPasswords((current) => ({ ...current, [userId]: '' }))
    } catch {
      setError('Unable to reset password.')
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
    { key: 'email', header: 'Email', cell: (user) => user.email },
    {
      key: 'roleNames',
      header: 'Roles',
      cell: (user) =>
        user.roleNames.length > 0 ? (
          user.roleNames.join(', ')
        ) : (
          <span className="text-muted-foreground">No roles</span>
        ),
    },
    {
      key: 'isRevoked',
      header: 'Status',
      cell: (user) => (user.isRevoked ? 'Revoked' : 'Active'),
      hideOnMobile: true,
    },
  ]

  return (
    <section className="flex w-full flex-col gap-6">
      <PageHeader title="Users" description="Staff accounts, their roles, and password resets." />

      {error && (
        <p role="alert" className="rounded-md border border-destructive/40 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {error}
        </p>
      )}

      <form
        onSubmit={submit}
        className="flex flex-col gap-3 rounded-lg border border-border bg-card p-4 sm:flex-row sm:flex-wrap sm:items-center"
      >
        <Input
          aria-label="User email"
          className="sm:max-w-xs"
          placeholder="Email"
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          required
        />
        <Input
          aria-label="User password"
          className="sm:max-w-xs"
          placeholder="Password"
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
                aria-label={`${role} for new user`}
                checked={roles.includes(role)}
                onChange={() => toggle(role)}
              />
              {role}
            </label>
          ))}
        </div>
        <Button type="submit">Create user</Button>
      </form>

      <DataToolbar
        searchValue={search}
        onSearchChange={setSearch}
        searchLabel="Search users"
        searchPlaceholder="Search by email or role…"
        view={view}
        onViewChange={setView}
      />

      <DataView
        items={visibleUsers}
        columns={columns}
        getRowKey={(user) => user.userId}
        view={view}
        loading={loading}
        emptyMessage={users.length === 0 ? 'No users yet.' : 'No users match this search.'}
        renderActions={(user) => (
          <>
            {assignableRoles.map((role) => (
              <label key={role} className="flex items-center gap-1.5 text-sm text-foreground">
                <input
                  type="checkbox"
                  aria-label={`${role} for ${user.email}`}
                  checked={selectedRolesFor(user).includes(role)}
                  onChange={() => toggleRowRole(user, role)}
                />
                {role}
              </label>
            ))}
            <Button size="sm" onClick={() => void saveRoles(user)}>
              Save roles
            </Button>
            <Input
              aria-label={`Replacement password for ${user.email}`}
              className="h-8 w-40"
              type="password"
              placeholder="New password"
              value={resetPasswords[user.userId] ?? ''}
              onChange={(e) => setResetPasswords((current) => ({ ...current, [user.userId]: e.target.value }))}
            />
            <Button size="sm" onClick={() => void forceReset(user.userId)}>
              Force reset
            </Button>
          </>
        )}
      />
    </section>
  )
}

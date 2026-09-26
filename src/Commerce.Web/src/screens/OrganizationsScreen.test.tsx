import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { OrganizationsScreen } from './OrganizationsScreen'
import { OrganizationProvider } from '@/organization/OrganizationContext'
import type { OrganizationSummary } from '@/api/types'

/**
 * platform-administration spec, "Sysadmin Acts On A Selected Organization":
 * `OrganizationsScreen`'s new "Open" row action reads
 * `useOrganizationContext()` and `useNavigate()`, so every render now needs
 * an `OrganizationProvider` and a `MemoryRouter` — neither existed on this
 * screen before this change.
 */
function renderScreen() {
  return render(
    <MemoryRouter>
      <OrganizationProvider>
        <OrganizationsScreen />
      </OrganizationProvider>
    </MemoryRouter>,
  )
}

const acme: OrganizationSummary = {
  id: '11111111-1111-1111-1111-111111111111',
  name: 'Acme Co',
  createdAt: '2024-01-01T00:00:00Z',
}
const vacaVerde: OrganizationSummary = {
  id: '22222222-2222-2222-2222-222222222222',
  name: 'Vaca Verde',
  createdAt: '2024-02-15T00:00:00Z',
}

/**
 * T8: `OrganizationsScreen.tsx` had no test file at all before this task —
 * it was a single minified line with no layout classes. Migrated onto the
 * shared data-view layer (`components/data/*`), following
 * `BranchesScreen.test.tsx` / `CustomersScreen.test.tsx`.
 */
describe('OrganizationsScreen', () => {
  const fetchMock = vi.fn()

  beforeEach(() => vi.stubGlobal('fetch', fetchMock))
  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
    window.localStorage.clear()
  })

  const listOnce = (organizations: OrganizationSummary[]) =>
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify(organizations), { status: 200 }))

  it('lists organizations from GET /account/organizations', async () => {
    listOnce([acme])

    renderScreen()

    await screen.findByText('Acme Co')
    expect(fetchMock.mock.calls[0][0]).toBe('/account/organizations')
  })

  it('uses the full width the shell gives it, with no centered narrow column', async () => {
    listOnce([acme])

    const { container } = renderScreen()

    await screen.findByText('Acme Co')
    expect(container.querySelector('.mx-auto')).toBeNull()
    expect(container.querySelector('[class*="max-w-2xl"], [class*="max-w-3xl"], [class*="max-w-lg"]')).toBeNull()
  })

  it('renders the real organization columns for each listed record', async () => {
    listOnce([vacaVerde])

    renderScreen()

    const row = within(await screen.findByRole('table')).getAllByRole('row')[1]
    expect(within(row).getByText('Vaca Verde')).toBeInTheDocument()
    expect(within(row).getByText(new Date(vacaVerde.createdAt).toLocaleDateString())).toBeInTheDocument()
  })

  it('shows an empty state when there are no organizations', async () => {
    listOnce([])

    renderScreen()

    expect(await screen.findByText('No organizations yet.')).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
  })

  it('does not claim there are no organizations when the load failed', async () => {
    fetchMock.mockRejectedValueOnce(new TypeError('Failed to fetch'))

    renderScreen()

    await screen.findByRole('alert')
    expect(screen.queryByText('No organizations yet.')).not.toBeInTheDocument()
    expect(screen.getByTestId('data-view-load-error')).toHaveTextContent(/organizations could not be loaded/i)
  })

  it('filters the listed organizations client-side by name', async () => {
    listOnce([acme, vacaVerde])

    const user = userEvent.setup()
    renderScreen()

    await screen.findByText('Acme Co')
    expect(screen.getByText('Vaca Verde')).toBeInTheDocument()

    await user.type(screen.getByLabelText(/search organizations/i), 'vaca')

    expect(screen.getByText('Vaca Verde')).toBeInTheDocument()
    expect(screen.queryByText('Acme Co')).not.toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it('switches to the card view and persists it under the organizations key', async () => {
    listOnce([acme, vacaVerde])

    const user = userEvent.setup()
    renderScreen()

    await screen.findByText('Acme Co')
    expect(screen.getByRole('table')).toBeInTheDocument()

    await user.click(screen.getByRole('radio', { name: /vista de tarjetas/i }))

    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.getAllByTestId('data-view-card')).toHaveLength(2)
    expect(window.localStorage.getItem('view:organizations')).toBe('cards')
  })

  // ---- New organization form (T8: behind a toggle, full-width) ----

  it('hides the create form behind a "New organization" action', async () => {
    listOnce([])

    renderScreen()

    await screen.findByText('No organizations yet.')
    expect(screen.queryByLabelText('Organization name')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'New organization' })).toBeInTheDocument()
  })

  it('opens the create form with visible, exactly-named labels', async () => {
    listOnce([])

    const user = userEvent.setup()
    renderScreen()

    await screen.findByText('No organizations yet.')
    await user.click(screen.getByRole('button', { name: 'New organization' }))

    expect(screen.getByLabelText('Organization name')).toBeInTheDocument()
    expect(screen.getByLabelText('Branch name')).toBeInTheDocument()
    expect(screen.getByLabelText('Administrator email')).toBeInTheDocument()
    expect(screen.getByLabelText('Administrator password')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Create organization' })).toBeInTheDocument()
    // The list is replaced, not just overlaid.
    expect(screen.queryByRole('button', { name: 'New organization' })).not.toBeInTheDocument()
  })

  it('creates an organization with the typed payload and returns to a refreshed list', async () => {
    listOnce([]) // initial list
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({ organizationId: 'org-1', branchId: 'branch-1', userId: 'user-1' }),
          { status: 201 },
        ),
      ) // create
    listOnce([acme]) // refreshed list

    const user = userEvent.setup()
    renderScreen()

    await screen.findByText('No organizations yet.')
    await user.click(screen.getByRole('button', { name: 'New organization' }))

    await user.type(screen.getByLabelText('Organization name'), 'Acme Co')
    await user.type(screen.getByLabelText('Branch name'), 'HQ')
    await user.type(screen.getByLabelText('Administrator email'), 'admin@acme.test')
    await user.type(screen.getByLabelText('Administrator password'), 'correct-horse-battery-staple')
    await user.click(screen.getByRole('button', { name: 'Create organization' }))

    await screen.findByText('Acme Co')
    expect(fetchMock.mock.calls[1][0]).toBe('/account/organizations')
    expect(fetchMock.mock.calls[1][1].method).toBe('POST')
    expect(JSON.parse(fetchMock.mock.calls[1][1].body as string)).toEqual({
      organizationName: 'Acme Co',
      branchName: 'HQ',
      adminEmail: 'admin@acme.test',
      adminPassword: 'correct-horse-battery-staple',
    })
    // Back on the list screen.
    expect(screen.getByRole('button', { name: 'New organization' })).toBeInTheDocument()
  })

  it('creates an organization without a branch name when the optional field is left blank', async () => {
    listOnce([])
      .mockResolvedValueOnce(
        new Response(
          JSON.stringify({ organizationId: 'org-1', branchId: 'branch-1', userId: 'user-1' }),
          { status: 201 },
        ),
      )
    listOnce([acme])

    const user = userEvent.setup()
    renderScreen()

    await screen.findByText('No organizations yet.')
    await user.click(screen.getByRole('button', { name: 'New organization' }))

    await user.type(screen.getByLabelText('Organization name'), 'Acme Co')
    await user.type(screen.getByLabelText('Administrator email'), 'admin@acme.test')
    await user.type(screen.getByLabelText('Administrator password'), 'correct-horse-battery-staple')
    await user.click(screen.getByRole('button', { name: 'Create organization' }))

    await screen.findByText('Acme Co')
    expect(JSON.parse(fetchMock.mock.calls[1][1].body as string)).toEqual({
      organizationName: 'Acme Co',
      branchName: null,
      adminEmail: 'admin@acme.test',
      adminPassword: 'correct-horse-battery-staple',
    })
  })

  it('cancels back to the list without creating anything', async () => {
    listOnce([acme])

    const user = userEvent.setup()
    renderScreen()

    await screen.findByText('Acme Co')
    await user.click(screen.getByRole('button', { name: 'New organization' }))
    expect(screen.getByLabelText('Organization name')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: /cancel/i }))

    expect(screen.getByText('Acme Co')).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it('returns to the list via the full-screen back action too', async () => {
    listOnce([acme])

    const user = userEvent.setup()
    renderScreen()

    await screen.findByText('Acme Co')
    await user.click(screen.getByRole('button', { name: 'New organization' }))
    expect(screen.getByLabelText('Organization name')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: /back to organizations/i }))

    expect(screen.getByText('Acme Co')).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it('ignores a slow initial load that resolves after a newer refresh', async () => {
    // The initial mount load (call 1) is left pending; a refresh triggered
    // after a create (call 3, listed below) resolves first with fresher
    // data, then call 1 resolves late with stale data that must not win.
    let resolveInitialLoad!: (response: Response) => void
    fetchMock.mockImplementationOnce(
      () => new Promise<Response>((resolve) => { resolveInitialLoad = resolve }),
    )

    const user = userEvent.setup()
    renderScreen()

    // Still loading — the initial GET is deliberately unresolved.
    expect(screen.getByRole('status')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'New organization' }))
    await user.type(screen.getByLabelText('Organization name'), 'Acme Co')
    await user.type(screen.getByLabelText('Administrator email'), 'admin@acme.test')
    await user.type(screen.getByLabelText('Administrator password'), 'correct-horse-battery-staple')

    fetchMock
      .mockResolvedValueOnce(
        new Response(JSON.stringify({ organizationId: 'org-1', branchId: 'branch-1', userId: 'user-1' }), {
          status: 201,
        }),
      ) // POST create
      .mockResolvedValueOnce(new Response(JSON.stringify([acme]), { status: 200 })) // refresh after create (call 3)

    await user.click(screen.getByRole('button', { name: 'Create organization' }))

    await screen.findByText('Acme Co')
    expect(screen.getByRole('button', { name: 'New organization' })).toBeInTheDocument()

    // The stale initial load now resolves with different, older data.
    resolveInitialLoad(new Response(JSON.stringify([vacaVerde]), { status: 200 }))

    // Give the stale promise a turn to (incorrectly) apply its result.
    await new Promise((resolve) => setTimeout(resolve, 0))

    expect(screen.getByText('Acme Co')).toBeInTheDocument()
    expect(screen.queryByText('Vaca Verde')).not.toBeInTheDocument()
  })

  it('surfaces a create failure as an alert without leaving the form', async () => {
    listOnce([]).mockResolvedValueOnce(
      new Response(JSON.stringify({ title: 'An organization with that name already exists.' }), {
        status: 409,
        headers: { 'Content-Type': 'application/json' },
      }),
    )

    const user = userEvent.setup()
    renderScreen()

    await screen.findByText('No organizations yet.')
    await user.click(screen.getByRole('button', { name: 'New organization' }))

    await user.type(screen.getByLabelText('Organization name'), 'Acme Co')
    await user.type(screen.getByLabelText('Administrator email'), 'admin@acme.test')
    await user.type(screen.getByLabelText('Administrator password'), 'correct-horse-battery-staple')
    await user.click(screen.getByRole('button', { name: 'Create organization' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(/already exists/i)
    expect(screen.getByLabelText('Organization name')).toHaveValue('Acme Co')
  })

  // ---- Edit branding (T5b) ----

  it('offers an "Edit branding" action for each listed organization', async () => {
    listOnce([acme, vacaVerde])

    renderScreen()

    await screen.findByText('Acme Co')
    expect(screen.getAllByRole('button', { name: 'Edit branding' })).toHaveLength(2)
  })

  it('opens the branding form for the clicked organization and returns to the list on save', async () => {
    listOnce([acme])
      .mockResolvedValueOnce(new Response(JSON.stringify({ logoUrl: null, primaryColor: null }), { status: 200 })) // branding GET
      .mockResolvedValueOnce(new Response(null, { status: 204 })) // branding PUT

    const user = userEvent.setup()
    renderScreen()

    await screen.findByText('Acme Co')
    await user.click(screen.getByRole('button', { name: 'Edit branding' }))

    expect(await screen.findByLabelText('Logo URL')).toBeInTheDocument()
    expect(screen.getByLabelText('Primary color')).toBeInTheDocument()
    expect(fetchMock.mock.calls[1][0]).toBe(`/account/organizations/${acme.id}/branding`)

    await user.type(screen.getByLabelText('Logo URL'), 'https://cdn.example.com/logo.png')
    await user.click(screen.getByRole('button', { name: 'Save' }))

    // Back on the list, not the form.
    await screen.findByRole('button', { name: 'Edit branding' })
    expect(screen.queryByLabelText('Logo URL')).not.toBeInTheDocument()
  })

  it('cancels the branding form back to the list without saving', async () => {
    listOnce([acme]).mockResolvedValueOnce(
      new Response(JSON.stringify({ logoUrl: null, primaryColor: null }), { status: 200 }),
    )

    const user = userEvent.setup()
    renderScreen()

    await screen.findByText('Acme Co')
    await user.click(screen.getByRole('button', { name: 'Edit branding' }))

    await screen.findByLabelText('Logo URL')
    await user.click(screen.getByRole('button', { name: 'Cancel' }))

    expect(screen.getByRole('button', { name: 'Edit branding' })).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })
})

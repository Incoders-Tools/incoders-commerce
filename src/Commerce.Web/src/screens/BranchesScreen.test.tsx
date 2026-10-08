import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { BranchesScreen } from './BranchesScreen'
import { AuthContext } from '@/auth/AuthContext'
import type { BranchSummary, SignedInResponse } from '@/api/types'

const central: BranchSummary = { branchId: 'branch-1', branchName: 'Central warehouse', code: 1 }
const downtown: BranchSummary = { branchId: 'branch-2', branchName: 'Downtown store', code: 2 }

const signedIn = (isSystemAdmin: boolean): SignedInResponse => ({
  organizationId: 'org-1',
  userId: 'user-1',
  displayName: 'Ana',
  permissions: 15,
  isSystemAdmin,
  selectableBranches: [],
})

const renderAs = (isSystemAdmin: boolean) =>
  render(
    <AuthContext.Provider value={{ user: signedIn(isSystemAdmin), error: null, signIn: vi.fn(), signOut: vi.fn() }}>
      <BranchesScreen />
    </AuthContext.Provider>,
  )

describe('BranchesScreen', () => {
  const fetchMock = vi.fn()

  beforeEach(() => vi.stubGlobal('fetch', fetchMock))
  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
    window.localStorage.clear()
  })

  const listOnce = (branches: BranchSummary[]) =>
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify(branches), { status: 200 }))

  it('lists branches from GET /account/branches', async () => {
    listOnce([central])

    render(<BranchesScreen />)

    await screen.findByText('Central warehouse')
    expect(fetchMock.mock.calls[0][0]).toBe('/account/branches')
  })

  it('creates a branch and refreshes the list', async () => {
    listOnce([]).mockResolvedValueOnce(new Response(JSON.stringify({ branchId: 'branch-1', code: 1 }), { status: 201 }))
    listOnce([central])

    const user = userEvent.setup()
    render(<BranchesScreen />)

    await screen.findByText('Todavía no hay sucursales.')
    await user.type(screen.getByLabelText('Nombre de la sucursal'), 'Central warehouse')
    await user.click(screen.getByRole('button', { name: 'Crear sucursal' }))

    await screen.findByText('Central warehouse')
    expect(fetchMock.mock.calls[1][0]).toBe('/account/branches')
    expect(fetchMock.mock.calls[1][1].method).toBe('POST')
    expect(JSON.parse(fetchMock.mock.calls[1][1].body as string)).toEqual({ branchName: 'Central warehouse' })
    // The name field is cleared once the branch exists.
    expect(screen.getByLabelText('Nombre de la sucursal')).toHaveValue('')
  })

  it('surfaces a create failure as an alert without clearing the typed name', async () => {
    listOnce([]).mockRejectedValueOnce(new TypeError('Failed to fetch'))

    const user = userEvent.setup()
    render(<BranchesScreen />)

    await screen.findByText('Todavía no hay sucursales.')
    await user.type(screen.getByLabelText('Nombre de la sucursal'), 'Central warehouse')
    await user.click(screen.getByRole('button', { name: 'Crear sucursal' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(/no se pudo crear la sucursal/i)
    expect(screen.getByLabelText('Nombre de la sucursal')).toHaveValue('Central warehouse')
  })

  it('surfaces a load failure as an alert', async () => {
    fetchMock.mockRejectedValueOnce(new TypeError('Failed to fetch'))

    render(<BranchesScreen />)

    expect(await screen.findByRole('alert')).toHaveTextContent(/no se pudieron cargar las sucursales/i)
  })

  it('does not claim there are no branches when the load failed', async () => {
    fetchMock.mockRejectedValueOnce(new TypeError('Failed to fetch'))

    render(<BranchesScreen />)

    await screen.findByRole('alert')
    // "No branches yet." is a real rendering of this screen (see the empty
    // state case above), so its absence here is a fact about this state.
    expect(screen.queryByText('Todavía no hay sucursales.')).not.toBeInTheDocument()
    expect(screen.getByTestId('data-view-load-error')).toHaveTextContent(/no se pudieron cargar las sucursales/i)
  })

  it('stops reporting a load failure once a later load succeeds', async () => {
    fetchMock
      .mockRejectedValueOnce(new TypeError('Failed to fetch')) // initial load
      .mockResolvedValueOnce(new Response(JSON.stringify({ branchId: 'branch-1', code: 1 }), { status: 201 })) // create
    listOnce([central]) // refreshed list

    const user = userEvent.setup()
    render(<BranchesScreen />)

    await screen.findByTestId('data-view-load-error')
    await user.type(screen.getByLabelText('Nombre de la sucursal'), 'Central warehouse')
    await user.click(screen.getByRole('button', { name: 'Crear sucursal' }))

    await screen.findByText('Central warehouse')
    expect(screen.queryByTestId('data-view-load-error')).not.toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  // ---- T4b: the shared data-view layer ----

  it('uses the full width the shell gives it, with no centered narrow column', async () => {
    listOnce([central])

    const { container } = render(<BranchesScreen />)

    await screen.findByText('Central warehouse')
    expect(container.querySelector('.mx-auto')).toBeNull()
    expect(container.querySelector('[class*="max-w-2xl"], [class*="max-w-3xl"]')).toBeNull()
  })

  it('renders the real branch columns for each listed record', async () => {
    listOnce([downtown])

    render(<BranchesScreen />)

    const row = within(await screen.findByRole('table')).getAllByRole('row')[1]
    expect(within(row).getByText('Downtown store')).toBeInTheDocument()
    expect(within(row).getByText('02')).toBeInTheDocument()
    // The technical GUID is for system administrators only.
    expect(within(row).queryByText('branch-2')).not.toBeInTheDocument()
  })

  it('shows the short code padded to two digits, with a tooltip explaining it', async () => {
    listOnce([central, { branchId: 'branch-100', branchName: 'Hundredth', code: 100 }])

    render(<BranchesScreen />)

    const table = await screen.findByRole('table')
    const header = within(table).getByRole('columnheader', { name: 'Código' })
    const hint =
      'Código corto de la sucursal. Se asigna automáticamente y no cambia. Se usa en los números de venta (p. ej. V01-C2-125).'
    expect(header).toHaveAttribute('title', hint)
    expect(within(table).getByText('01')).toHaveAttribute('title', hint)
    expect(within(table).getByText('100')).toBeInTheDocument()
  })

  it('hides the identifier column from users who are not system administrators', async () => {
    listOnce([central])

    renderAs(false)

    const table = await screen.findByRole('table')
    expect(within(table).queryByRole('columnheader', { name: 'Identificador' })).not.toBeInTheDocument()
    expect(within(table).queryByText('branch-1')).not.toBeInTheDocument()
  })

  it('shows the identifier column to system administrators', async () => {
    listOnce([central])

    renderAs(true)

    const table = await screen.findByRole('table')
    expect(within(table).getByRole('columnheader', { name: 'Identificador' })).toBeInTheDocument()
    expect(within(table).getByText('branch-1')).toBeInTheDocument()
  })

  it('shows an empty state when there are no branches', async () => {
    listOnce([])

    render(<BranchesScreen />)

    expect(await screen.findByText('Todavía no hay sucursales.')).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
  })

  it('filters the listed branches client-side by name', async () => {
    listOnce([central, downtown])

    const user = userEvent.setup()
    render(<BranchesScreen />)

    await screen.findByText('Central warehouse')
    expect(screen.getByText('Downtown store')).toBeInTheDocument()

    await user.type(screen.getByLabelText(/buscar sucursales/i), 'downtown')

    expect(screen.getByText('Downtown store')).toBeInTheDocument()
    expect(screen.queryByText('Central warehouse')).not.toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it('shows a helpful empty state when the filter matches nothing', async () => {
    listOnce([central, downtown])

    const user = userEvent.setup()
    render(<BranchesScreen />)

    await screen.findByText('Central warehouse')
    await user.type(screen.getByLabelText(/buscar sucursales/i), 'zzzz')

    expect(screen.getByText(/ninguna sucursal coincide/i)).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.queryAllByTestId('data-view-card')).toHaveLength(0)
  })

  it('switches to the card view and restores that preference on remount', async () => {
    listOnce([central, downtown])
    listOnce([central, downtown])

    const user = userEvent.setup()
    const first = render(<BranchesScreen />)

    await screen.findByText('Central warehouse')
    expect(screen.getByRole('table')).toBeInTheDocument()

    await user.click(screen.getByRole('radio', { name: /vista de tarjetas/i }))

    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.getAllByTestId('data-view-card')).toHaveLength(2)
    expect(window.localStorage.getItem('view:branches')).toBe('cards')

    first.unmount()
    render(<BranchesScreen />)

    await screen.findByText('Central warehouse')
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.getAllByTestId('data-view-card')).toHaveLength(2)
    expect(screen.getByRole('radio', { name: /vista de tarjetas/i })).toHaveAttribute('aria-checked', 'true')
  })

  it('keeps the view preference separate from the other data screens', async () => {
    window.localStorage.setItem('view:customers', 'cards')
    listOnce([central])

    render(<BranchesScreen />)

    await screen.findByText('Central warehouse')
    // Branches reads `view:branches`, which is unset here, so it stays a table.
    expect(screen.getByRole('table')).toBeInTheDocument()
    await waitFor(() => expect(window.localStorage.getItem('view:branches')).toBeNull())
  })

  // ---- branch-discount-pin: set / rotate the branch discount PIN ----

  const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status })

  const openPinPanel = async (user: ReturnType<typeof userEvent.setup>, branchName: string) => {
    await user.click(await screen.findByRole('button', { name: `PIN de descuentos de ${branchName}` }))
    return screen.findByRole('region', { name: 'PIN de descuentos' })
  }

  it('shows that no discount PIN is set for the chosen branch', async () => {
    listOnce([central])
    fetchMock.mockResolvedValueOnce(json({ isSet: false, version: null, changedAtUtc: null }))

    const user = userEvent.setup()
    render(<BranchesScreen />)
    const panel = await openPinPanel(user, 'Central warehouse')

    expect(await within(panel).findByText('Esta sucursal todavía no tiene un PIN de descuentos.')).toBeInTheDocument()
    expect(fetchMock.mock.calls[1][0]).toBe('/account/branches/branch-1/discount-pin')
    expect(within(panel).getByRole('button', { name: 'Definir PIN' })).toBeInTheDocument()
  })

  it('shows only that a PIN is set and when it last changed, never the PIN', async () => {
    listOnce([central])
    fetchMock.mockResolvedValueOnce(json({ isSet: true, version: 3, changedAtUtc: '2026-09-01T12:00:00Z' }))

    const user = userEvent.setup()
    render(<BranchesScreen />)
    const panel = await openPinPanel(user, 'Central warehouse')

    expect(await within(panel).findByText(/PIN configurado\. Último cambio:/)).toBeInTheDocument()
    expect(within(panel).getByRole('button', { name: 'Cambiar PIN' })).toBeInTheDocument()
    expect(within(panel).getByLabelText('Nuevo PIN de descuentos')).toHaveValue('')
  })

  it('sets the PIN with a PUT, clears the field and reports the new status', async () => {
    listOnce([central])
    fetchMock.mockResolvedValueOnce(json({ isSet: false, version: null, changedAtUtc: null }))
    fetchMock.mockResolvedValueOnce(json({ isSet: true, version: 1, changedAtUtc: '2026-09-29T15:00:00Z' }))

    const user = userEvent.setup()
    render(<BranchesScreen />)
    const panel = await openPinPanel(user, 'Central warehouse')
    await within(panel).findByText('Esta sucursal todavía no tiene un PIN de descuentos.')

    const field = within(panel).getByLabelText('Nuevo PIN de descuentos')
    expect(field).toHaveAttribute('type', 'password')
    await user.type(field, '482913')
    await user.click(within(panel).getByRole('button', { name: 'Definir PIN' }))

    expect(await within(panel).findByText(/PIN configurado\. Último cambio:/)).toBeInTheDocument()
    const [url, init] = fetchMock.mock.calls[2]
    expect(url).toBe('/account/branches/branch-1/discount-pin')
    expect(init.method).toBe('PUT')
    expect(JSON.parse(init.body as string)).toEqual({ pin: '482913' })
    expect(within(panel).getByLabelText('Nuevo PIN de descuentos')).toHaveValue('')
    expect(panel).not.toHaveTextContent('482913')
  })

  it('refuses a PIN that is not 4 to 12 digits without calling the server', async () => {
    listOnce([central])
    fetchMock.mockResolvedValueOnce(json({ isSet: false, version: null, changedAtUtc: null }))

    const user = userEvent.setup()
    render(<BranchesScreen />)
    const panel = await openPinPanel(user, 'Central warehouse')
    await within(panel).findByText('Esta sucursal todavía no tiene un PIN de descuentos.')

    await user.type(within(panel).getByLabelText('Nuevo PIN de descuentos'), '12a')
    await user.click(within(panel).getByRole('button', { name: 'Definir PIN' }))

    expect(await within(panel).findByRole('alert')).toHaveTextContent('El PIN debe tener entre 4 y 12 dígitos.')
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })

  it('surfaces a failed save as an alert and keeps the typed PIN out of the page text', async () => {
    listOnce([central])
    fetchMock.mockResolvedValueOnce(json({ isSet: false, version: null, changedAtUtc: null }))
    fetchMock.mockResolvedValueOnce(json({}, 500))

    const user = userEvent.setup()
    render(<BranchesScreen />)
    const panel = await openPinPanel(user, 'Central warehouse')
    await within(panel).findByText('Esta sucursal todavía no tiene un PIN de descuentos.')

    await user.type(within(panel).getByLabelText('Nuevo PIN de descuentos'), '1357')
    await user.click(within(panel).getByRole('button', { name: 'Definir PIN' }))

    expect(await within(panel).findByRole('alert')).toHaveTextContent('No se pudo guardar el PIN de descuentos.')
    expect(panel).not.toHaveTextContent('1357')
  })
})

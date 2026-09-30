import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { UsersScreen } from './UsersScreen'
import { BranchContext } from '@/branch/BranchContext'
import type { UserSummary } from '@/api/types'

const ruta51 = { id: 'branch-a', name: 'Ruta 51' }
const centro = { id: 'branch-b', name: 'Centro' }

/** Renders the screen inside a branch context, as the app shell does. */
function renderScreen(selected: { id: string; name: string } | null = ruta51) {
  return render(
    <BranchContext.Provider
      value={{ selectedBranch: selected, selectableBranches: [ruta51, centro], selectBranch: () => {} }}
    >
      <UsersScreen />
    </BranchContext.Provider>,
  )
}

const seller: UserSummary = {
  userId: 'user-1',
  email: 'staff@example.com',
  roleNames: ['seller'],
  isRevoked: false,
  branchIds: ['branch-a'],
}

// T4b: a second record with different values in every asserted column, so the
// exclusion assertions below compare rows the screen really renders.
const revokedProvider: UserSummary = {
  userId: 'user-2',
  email: 'supplier@vendor.test',
  roleNames: ['provider'],
  isRevoked: true,
  branchIds: [],
}

describe('UsersScreen', () => {
  const fetchMock = vi.fn()

  beforeEach(() => vi.stubGlobal('fetch', fetchMock))
  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
    window.localStorage.clear()
  })

  const listOnce = (users: UserSummary[]) =>
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify(users), { status: 200 }))

  it('submits the administrator-entered replacement password for a forced reset', async () => {
    listOnce([seller]).mockResolvedValueOnce(new Response(null, { status: 204 }))

    const user = userEvent.setup()
    renderScreen()

    await screen.findByText('staff@example.com')
    await user.type(screen.getByLabelText('Contraseña de reemplazo para staff@example.com'), 'Unique-Password-42!')
    await user.click(screen.getByRole('button', { name: 'Forzar restablecimiento' }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2))
    expect(fetchMock.mock.calls[1][0]).toBe('/account/users/user-1/reset-password')
    expect(JSON.parse(fetchMock.mock.calls[1][1].body as string)).toEqual({ newPassword: 'Unique-Password-42!' })
  })

  it('refuses a forced reset with no replacement password and says why', async () => {
    listOnce([seller])

    const user = userEvent.setup()
    renderScreen()

    await screen.findByText('staff@example.com')
    await user.click(screen.getByRole('button', { name: 'Forzar restablecimiento' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(/contraseña de reemplazo/i)
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it('creates a user with the selected branch from the always-visible create form and refreshes the list', async () => {
    listOnce([])
      .mockResolvedValueOnce(new Response(JSON.stringify({ userId: 'user-2' }), { status: 201 }))
    listOnce([seller])

    const user = userEvent.setup()
    renderScreen()

    await screen.findByText('Todavía no hay usuarios.')
    await user.type(screen.getByLabelText('Correo electrónico del usuario'), 'staff@example.com')
    await user.type(screen.getByLabelText('Contraseña del usuario'), 'correct-horse-battery-staple')
    await user.click(screen.getByRole('button', { name: 'Crear usuario' }))

    await screen.findByText('staff@example.com')
    expect(fetchMock.mock.calls[1][0]).toBe('/account/users')
    expect(JSON.parse(fetchMock.mock.calls[1][1].body as string)).toEqual({
      email: 'staff@example.com',
      password: 'correct-horse-battery-staple',
      roleNames: ['seller'],
      branchIds: ['branch-a'],
    })
  })

  it('saves the selected roles for a listed user', async () => {
    listOnce([seller]).mockResolvedValueOnce(new Response(null, { status: 204 }))
    listOnce([seller])

    const user = userEvent.setup()
    renderScreen()

    await screen.findByText('staff@example.com')
    await user.click(screen.getByRole('button', { name: 'Guardar roles' }))

    // Three calls now, not two: a successful save re-reads the list so the
    // row cannot keep showing a selection the server may have adjusted.
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(3))
    expect(fetchMock.mock.calls[1][0]).toBe('/account/users/user-1/roles')
    expect(fetchMock.mock.calls[1][1].method).toBe('PUT')
    expect(fetchMock.mock.calls[2][0]).toBe('/account/users')
  })

  // ---- Per-row role editor ----

  const tableRows = async () => within(await screen.findByRole('table')).getAllByRole('row')

  it('offers exactly the organization-assignable roles per row, never platform-admin', async () => {
    listOnce([seller])

    renderScreen()

    const row = (await tableRows())[1]
    // Counting the real checkboxes rather than probing for one label: the
    // server's RoleCatalog deliberately excludes platform-admin from what an
    // organization can grant, so the row must offer that exact set.
    const roleBoxes = within(row)
      .getAllByRole('checkbox')
      .map((box) => box.getAttribute('aria-label'))
      .filter((label) => !label?.startsWith('Sucursal'))
    expect(roleBoxes).toEqual([
      'Administrador para staff@example.com',
      'Vendedor para staff@example.com',
      'Cajero para staff@example.com',
      'Proveedor para staff@example.com',
    ])
  })

  it("saves a row's own roles, not whatever the create form has checked", async () => {
    listOnce([seller, revokedProvider]).mockResolvedValueOnce(new Response(null, { status: 204 }))
    listOnce([seller, revokedProvider])

    const user = userEvent.setup()
    renderScreen()

    await screen.findByText('staff@example.com')
    // Make the create form's selection differ from every row's own roles.
    await user.click(screen.getByLabelText('Proveedor para nuevo usuario'))

    const sellerRow = (await tableRows())[1]
    await user.click(within(sellerRow).getByRole('button', { name: 'Guardar roles' }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(3))
    expect(fetchMock.mock.calls[1][0]).toBe('/account/users/user-1/roles')
    expect(JSON.parse(fetchMock.mock.calls[1][1].body as string)).toEqual({ roleNames: ['seller'] })
    // …and the list is re-read, so the row shows what the server really kept.
    expect(fetchMock.mock.calls[2][0]).toBe('/account/users')
  })

  it('edits one row without touching another row, and sends the edited set', async () => {
    listOnce([seller, revokedProvider]).mockResolvedValueOnce(new Response(null, { status: 204 }))
    listOnce([{ ...seller, roleNames: ['business-admin'] }, revokedProvider])

    const user = userEvent.setup()
    renderScreen()

    await screen.findByText('staff@example.com')
    await user.click(screen.getByLabelText('Administrador para staff@example.com'))
    await user.click(screen.getByLabelText('Vendedor para staff@example.com'))

    // The other row keeps its own, independent selection.
    expect(screen.getByLabelText('Proveedor para supplier@vendor.test')).toBeChecked()
    expect(screen.getByLabelText('Administrador para supplier@vendor.test')).not.toBeChecked()
    expect(screen.getByLabelText('Vendedor para supplier@vendor.test')).not.toBeChecked()

    const sellerRow = (await tableRows())[1]
    await user.click(within(sellerRow).getByRole('button', { name: 'Guardar roles' }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(3))
    expect(JSON.parse(fetchMock.mock.calls[1][1].body as string)).toEqual({ roleNames: ['business-admin'] })
    const refreshedCells = within((await tableRows())[1]).getAllByRole('cell')
    expect(within(refreshedCells[1]).getByText('Administrador')).toBeInTheDocument()
  })

  it('surfaces a rejected role change and leaves the row showing the stored roles', async () => {
    listOnce([seller]).mockResolvedValueOnce(
      new Response(JSON.stringify({ title: 'You cannot grant business-admin.' }), {
        status: 403,
        headers: { 'Content-Type': 'application/json' },
      }),
    )

    const user = userEvent.setup()
    renderScreen()

    await screen.findByText('staff@example.com')
    await user.click(screen.getByLabelText('Administrador para staff@example.com'))
    await user.click(screen.getByRole('button', { name: 'Guardar roles' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(/no tenés permiso para asignar esa sucursal o rol/i)
    // The grant cap rejected it, so the row must fall back to the stored roles.
    expect(screen.getByLabelText('Administrador para staff@example.com')).not.toBeChecked()
    expect(screen.getByLabelText('Vendedor para staff@example.com')).toBeChecked()
    // No refresh was issued: the PUT is the only call after the initial list.
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })

  it('edits and saves the roles of a row from the card view too', async () => {
    window.localStorage.setItem('view:users', 'cards')
    listOnce([seller]).mockResolvedValueOnce(new Response(null, { status: 204 }))
    listOnce([{ ...seller, roleNames: ['seller', 'business-admin'] }])

    const user = userEvent.setup()
    renderScreen()

    await screen.findByText('staff@example.com')
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.getAllByTestId('data-view-card')).toHaveLength(1)

    await user.click(screen.getByLabelText('Administrador para staff@example.com'))
    await user.click(screen.getByRole('button', { name: 'Guardar roles' }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(3))
    expect(fetchMock.mock.calls[1][0]).toBe('/account/users/user-1/roles')
    expect(JSON.parse(fetchMock.mock.calls[1][1].body as string)).toEqual({
      roleNames: ['seller', 'business-admin'],
    })
  })

  // ---- Staff branches and roles (T3) ----

  it('preselects the currently selected branch in the create form', async () => {
    listOnce([])
    renderScreen(centro)

    await screen.findByText('Todavía no hay usuarios.')
    expect(screen.getByLabelText('Sucursal Centro para nuevo usuario')).toBeChecked()
    expect(screen.getByLabelText('Sucursal Ruta 51 para nuevo usuario')).not.toBeChecked()
  })

  it('sends every branch ticked in the create form', async () => {
    listOnce([]).mockResolvedValueOnce(new Response(JSON.stringify({ userId: 'user-9' }), { status: 201 }))
    listOnce([])

    const user = userEvent.setup()
    renderScreen()

    await screen.findByText('Todavía no hay usuarios.')
    await user.click(screen.getByLabelText('Sucursal Centro para nuevo usuario'))
    await user.type(screen.getByLabelText('Correo electrónico del usuario'), 'new@example.com')
    await user.type(screen.getByLabelText('Contraseña del usuario'), 'correct-horse-battery-staple')
    await user.click(screen.getByRole('button', { name: 'Crear usuario' }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(3))
    expect(JSON.parse(fetchMock.mock.calls[1][1].body as string).branchIds).toEqual(['branch-a', 'branch-b'])
  })

  it('refuses to create a user with no branch and does not call the API', async () => {
    listOnce([])

    const user = userEvent.setup()
    renderScreen()

    await screen.findByText('Todavía no hay usuarios.')
    await user.click(screen.getByLabelText('Sucursal Ruta 51 para nuevo usuario'))
    await user.type(screen.getByLabelText('Correo electrónico del usuario'), 'new@example.com')
    await user.type(screen.getByLabelText('Contraseña del usuario'), 'correct-horse-battery-staple')
    await user.click(screen.getByRole('button', { name: 'Crear usuario' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(/al menos una sucursal/i)
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it('offers the cashier role in the create form with role descriptions', async () => {
    listOnce([])
    renderScreen()

    await screen.findByText('Todavía no hay usuarios.')
    expect(screen.getByLabelText('Cajero para nuevo usuario')).toBeInTheDocument()
    expect(screen.getByText('Opera el punto de venta')).toBeInTheDocument()
    expect(screen.getByText('Toma pedidos desde la web')).toBeInTheDocument()
  })

  it('renders roles with Spanish labels, never the machine names', async () => {
    listOnce([{ ...seller, roleNames: ['business-admin', 'cashier'] }])
    renderScreen()

    const cells = within((await tableRows())[1]).getAllByRole('cell')
    expect(within(cells[1]).getByText('Administrador, Cajero')).toBeInTheDocument()
  })

  it("shows each user's branches by name", async () => {
    listOnce([{ ...seller, branchIds: ['branch-a', 'branch-b'] }, revokedProvider])
    renderScreen()

    const rows = await tableRows()
    expect(within(within(rows[1]).getAllByRole('cell')[2]).getByText('Ruta 51, Centro')).toBeInTheDocument()
    expect(within(within(rows[2]).getAllByRole('cell')[2]).getByText('Sin sucursales')).toBeInTheDocument()
  })

  it("edits a user's branches and saves them with the branches endpoint", async () => {
    listOnce([seller]).mockResolvedValueOnce(new Response(null, { status: 204 }))
    listOnce([{ ...seller, branchIds: ['branch-a', 'branch-b'] }])

    const user = userEvent.setup()
    renderScreen()

    await screen.findByText('staff@example.com')
    await user.click(screen.getByLabelText('Sucursal Centro para staff@example.com'))
    await user.click(screen.getByRole('button', { name: 'Guardar sucursales' }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(3))
    expect(fetchMock.mock.calls[1][0]).toBe('/account/users/user-1/branches')
    expect(fetchMock.mock.calls[1][1].method).toBe('PUT')
    expect(JSON.parse(fetchMock.mock.calls[1][1].body as string)).toEqual({ branchIds: ['branch-a', 'branch-b'] })
  })

  it('refuses to save a user with no branches and does not call the API', async () => {
    listOnce([seller])

    const user = userEvent.setup()
    renderScreen()

    await screen.findByText('staff@example.com')
    await user.click(screen.getByLabelText('Sucursal Ruta 51 para staff@example.com'))
    await user.click(screen.getByRole('button', { name: 'Guardar sucursales' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(/al menos una sucursal/i)
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  const failWith = (status: number, body: unknown) =>
    new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

  it.each([
    ['branch-required', /al menos una sucursal/i],
    ['branch-not-in-organization', /no pertenece a la organización/i],
  ])('shows a friendly message for the %s error code, not the raw code', async (code, expected) => {
    listOnce([]).mockResolvedValueOnce(failWith(400, { error: code }))

    const user = userEvent.setup()
    renderScreen()

    await screen.findByText('Todavía no hay usuarios.')
    await user.type(screen.getByLabelText('Correo electrónico del usuario'), 'new@example.com')
    await user.type(screen.getByLabelText('Contraseña del usuario'), 'correct-horse-battery-staple')
    await user.click(screen.getByRole('button', { name: 'Crear usuario' }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(expected)
    expect(alert).not.toHaveTextContent(code)
  })

  it('shows a friendly permission message when the server answers 403 to a create', async () => {
    listOnce([]).mockResolvedValueOnce(new Response(null, { status: 403 }))

    const user = userEvent.setup()
    renderScreen()

    await screen.findByText('Todavía no hay usuarios.')
    await user.type(screen.getByLabelText('Correo electrónico del usuario'), 'new@example.com')
    await user.type(screen.getByLabelText('Contraseña del usuario'), 'correct-horse-battery-staple')
    await user.click(screen.getByRole('button', { name: 'Crear usuario' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('No tenés permiso para asignar esa sucursal o rol')
  })

  it('shows a friendly message when a branch edit is rejected', async () => {
    listOnce([seller]).mockResolvedValueOnce(failWith(400, { error: 'branch-not-in-organization' }))

    const user = userEvent.setup()
    renderScreen()

    await screen.findByText('staff@example.com')
    await user.click(screen.getByRole('button', { name: 'Guardar sucursales' }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent(/no pertenece a la organización/i)
    expect(alert).not.toHaveTextContent('branch-not-in-organization')
  })

  it('surfaces a load failure as an alert', async () => {
    fetchMock.mockRejectedValueOnce(new TypeError('Failed to fetch'))

    renderScreen()

    expect(await screen.findByRole('alert')).toHaveTextContent(/no se pudieron cargar los usuarios/i)
  })

  it('does not claim there are no users when the load failed', async () => {
    fetchMock.mockRejectedValueOnce(new TypeError('Failed to fetch'))

    renderScreen()

    await screen.findByRole('alert')
    // "No users yet." is a real rendering of this screen (see the empty state
    // case below), so its absence here is a fact about this state.
    expect(screen.queryByText('Todavía no hay usuarios.')).not.toBeInTheDocument()
    expect(screen.getByTestId('data-view-load-error')).toHaveTextContent(/no se pudieron cargar los usuarios/i)
  })

  it('keeps the load failure separate from a failed action', async () => {
    listOnce([seller]).mockResolvedValueOnce(
      new Response(JSON.stringify({ title: 'You cannot grant business-admin.' }), {
        status: 403,
        headers: { 'Content-Type': 'application/json' },
      }),
    )

    const user = userEvent.setup()
    renderScreen()

    await screen.findByText('staff@example.com')
    await user.click(screen.getByLabelText('Administrador para staff@example.com'))
    await user.click(screen.getByRole('button', { name: 'Guardar roles' }))

    await screen.findByRole('alert')
    // The list loaded fine: a failed action must not make the list area
    // claim the users could not be loaded.
    await user.type(screen.getByLabelText(/buscar usuarios/i), 'zzzz')
    expect(screen.getByText(/ningún usuario coincide/i)).toBeInTheDocument()
    expect(screen.queryByTestId('data-view-load-error')).not.toBeInTheDocument()
  })

  // ---- T4b: the shared data-view layer ----

  it('uses the full width the shell gives it, with no centered narrow column', async () => {
    listOnce([seller])

    const { container } = renderScreen()

    await screen.findByText('staff@example.com')
    expect(container.querySelector('.mx-auto')).toBeNull()
    expect(container.querySelector('[class*="max-w-2xl"], [class*="max-w-3xl"]')).toBeNull()
  })

  it('renders the real user columns for each listed record', async () => {
    listOnce([revokedProvider])

    renderScreen()

    // Scoped to the data cells: the actions cell now also carries a
    // per-row role editor whose checkbox labels repeat the role names, so an
    // unscoped `getByText('provider')` would be ambiguous rather than wrong.
    const cells = within(within(await screen.findByRole('table')).getAllByRole('row')[1]).getAllByRole('cell')
    expect(within(cells[0]).getByText('supplier@vendor.test')).toBeInTheDocument()
    expect(within(cells[1]).getByText('Proveedor')).toBeInTheDocument()
    expect(within(cells[2]).getByText('Sin sucursales')).toBeInTheDocument()
    expect(within(cells[3]).getByText('Revocado')).toBeInTheDocument()
  })

  it('shows an empty state when there are no users', async () => {
    listOnce([])

    renderScreen()

    expect(await screen.findByText('Todavía no hay usuarios.')).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
  })

  it('filters the listed users client-side by email or role', async () => {
    listOnce([seller, revokedProvider])

    const user = userEvent.setup()
    renderScreen()

    await screen.findByText('staff@example.com')
    expect(screen.getByText('supplier@vendor.test')).toBeInTheDocument()

    await user.type(screen.getByLabelText(/buscar usuarios/i), 'vendor')

    expect(screen.getByText('supplier@vendor.test')).toBeInTheDocument()
    expect(screen.queryByText('staff@example.com')).not.toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(1)

    await user.clear(screen.getByLabelText(/buscar usuarios/i))
    await user.type(screen.getByLabelText(/buscar usuarios/i), 'provider')

    expect(screen.getByText('supplier@vendor.test')).toBeInTheDocument()
    expect(screen.queryByText('staff@example.com')).not.toBeInTheDocument()
  })

  it('shows a helpful empty state when the filter matches nothing', async () => {
    listOnce([seller, revokedProvider])

    const user = userEvent.setup()
    renderScreen()

    await screen.findByText('staff@example.com')
    await user.type(screen.getByLabelText(/buscar usuarios/i), 'zzzz')

    expect(screen.getByText(/ningún usuario coincide/i)).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.queryAllByTestId('data-view-card')).toHaveLength(0)
  })

  it('switches to the card view and restores that preference on remount', async () => {
    listOnce([seller, revokedProvider])
    listOnce([seller, revokedProvider])

    const user = userEvent.setup()
    const first = renderScreen()

    await screen.findByText('staff@example.com')
    expect(screen.getByRole('table')).toBeInTheDocument()

    await user.click(screen.getByRole('radio', { name: /vista de tarjetas/i }))

    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.getAllByTestId('data-view-card')).toHaveLength(2)
    expect(window.localStorage.getItem('view:users')).toBe('cards')

    first.unmount()
    renderScreen()

    await screen.findByText('staff@example.com')
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.getAllByTestId('data-view-card')).toHaveLength(2)
    expect(screen.getByRole('radio', { name: /vista de tarjetas/i })).toHaveAttribute('aria-checked', 'true')
  })

  it('still forces a password reset from the card view', async () => {
    window.localStorage.setItem('view:users', 'cards')
    listOnce([seller]).mockResolvedValueOnce(new Response(null, { status: 204 }))

    const user = userEvent.setup()
    renderScreen()

    await screen.findByText('staff@example.com')
    // Prove we really are in the card layout, not just re-testing the table.
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.getAllByTestId('data-view-card')).toHaveLength(1)

    await user.type(screen.getByLabelText('Contraseña de reemplazo para staff@example.com'), 'Unique-Password-42!')
    await user.click(screen.getByRole('button', { name: 'Forzar restablecimiento' }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2))
    expect(fetchMock.mock.calls[1][0]).toBe('/account/users/user-1/reset-password')
  })
})

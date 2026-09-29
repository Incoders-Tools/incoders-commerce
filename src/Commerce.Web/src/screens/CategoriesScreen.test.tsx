import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { CategoriesScreen } from './CategoriesScreen'
import type { CategoryRecord } from '@/api/types'

/**
 * catalog-categories spec: admins manage the organization's categories (name +
 * icon key from the fixed set). The server is the authority for permissions,
 * uniqueness and the delete-while-in-use refusal; this screen only reflects it.
 */
const meat: CategoryRecord = {
  id: 'cat-1',
  organizationId: 'org-1',
  name: 'Carnes',
  iconKey: 'meat',
  createdAtUtc: '2024-01-01T00:00:00Z',
  updatedAtUtc: '2024-01-01T00:00:00Z',
}
const wine: CategoryRecord = { ...meat, id: 'cat-2', name: 'Vinos', iconKey: 'wine' }

describe('CategoriesScreen', () => {
  const fetchMock = vi.fn()

  beforeEach(() => vi.stubGlobal('fetch', fetchMock))
  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
    window.localStorage.clear()
  })

  const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status })

  it('lists categories from GET /catalog/categories', async () => {
    fetchMock.mockResolvedValueOnce(json([meat, wine]))

    render(<CategoriesScreen />)

    await screen.findByText('Carnes')
    expect(screen.getByText('Vinos')).toBeInTheDocument()
    expect(fetchMock.mock.calls[0][0]).toBe('/catalog/categories')
  })

  it('shows an empty state and a visible load error, never stale data', async () => {
    fetchMock.mockResolvedValueOnce(json([]))
    const first = render(<CategoriesScreen />)
    expect(await screen.findByText('Todavía no hay categorías.')).toBeInTheDocument()
    first.unmount()

    fetchMock.mockRejectedValueOnce(new TypeError('Failed to fetch'))
    render(<CategoriesScreen />)
    expect(await screen.findByRole('alert')).toHaveTextContent(/no se pudieron cargar las categorías/i)
  })

  it('creates a category with a name and the chosen icon, then lists it', async () => {
    fetchMock
      .mockResolvedValueOnce(json([]))
      .mockResolvedValueOnce(json({ ...wine }, 201))
      .mockResolvedValueOnce(json([wine]))

    const user = userEvent.setup()
    render(<CategoriesScreen />)

    await screen.findByText('Todavía no hay categorías.')
    await user.click(screen.getByRole('button', { name: /nueva categoría/i }))
    await user.type(screen.getByLabelText('Nombre'), 'Vinos')
    await user.click(screen.getByRole('radio', { name: 'Vino' }))
    await user.click(screen.getByRole('button', { name: /^guardar$/i }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(3))
    const [url, init] = fetchMock.mock.calls[1]
    expect(url).toBe('/catalog/categories')
    expect(init.method).toBe('POST')
    expect(JSON.parse(init.body as string)).toEqual({ name: 'Vinos', iconKey: 'wine' })
    expect(await screen.findByText('Vinos')).toBeInTheDocument()
  })

  it('offers exactly the fixed icon set and defaults to the generic icon', async () => {
    fetchMock.mockResolvedValueOnce(json([]))

    const user = userEvent.setup()
    render(<CategoriesScreen />)
    await screen.findByText('Todavía no hay categorías.')
    await user.click(screen.getByRole('button', { name: /nueva categoría/i }))

    const group = screen.getByRole('radiogroup', { name: 'Ícono' })
    expect(within(group).getAllByRole('radio')).toHaveLength(12)
    expect(within(group).getByRole('radio', { name: 'Genérico' })).toBeChecked()
  })

  it('renames a category and changes its icon via PUT', async () => {
    fetchMock
      .mockResolvedValueOnce(json([meat]))
      .mockResolvedValueOnce(json({ ...meat, name: 'Carnes rojas', iconKey: 'charcoal' }))
      .mockResolvedValueOnce(json([{ ...meat, name: 'Carnes rojas', iconKey: 'charcoal' }]))

    const user = userEvent.setup()
    render(<CategoriesScreen />)

    await screen.findByText('Carnes')
    await user.click(screen.getByRole('button', { name: /^editar$/i }))
    const name = screen.getByLabelText('Nombre')
    expect(name).toHaveValue('Carnes')
    expect(screen.getByRole('radio', { name: 'Carne' })).toBeChecked()
    await user.clear(name)
    await user.type(name, 'Carnes rojas')
    await user.click(screen.getByRole('radio', { name: 'Carbón' }))
    await user.click(screen.getByRole('button', { name: /^guardar$/i }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(3))
    const [url, init] = fetchMock.mock.calls[1]
    expect(url).toBe('/catalog/categories/cat-1')
    expect(init.method).toBe('PUT')
    expect(JSON.parse(init.body as string)).toEqual({ name: 'Carnes rojas', iconKey: 'charcoal' })
    expect(await screen.findByText('Carnes rojas')).toBeInTheDocument()
  })

  it('explains a duplicate name instead of a raw error', async () => {
    fetchMock
      .mockResolvedValueOnce(json([meat]))
      .mockResolvedValueOnce(json({ error: 'category-name-in-use' }, 409))

    const user = userEvent.setup()
    render(<CategoriesScreen />)

    await screen.findByText('Carnes')
    await user.click(screen.getByRole('button', { name: /nueva categoría/i }))
    await user.type(screen.getByLabelText('Nombre'), 'carnes')
    await user.click(screen.getByRole('button', { name: /^guardar$/i }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Ya existe una categoría con ese nombre.')
  })

  it('asks for confirmation before deleting, then deletes and removes the row', async () => {
    fetchMock
      .mockResolvedValueOnce(json([meat, wine]))
      .mockResolvedValueOnce(new Response(null, { status: 204 }))
      .mockResolvedValueOnce(json([wine]))

    const user = userEvent.setup()
    render(<CategoriesScreen />)

    await screen.findByText('Carnes')
    const row = screen.getByText('Carnes').closest('tr')!
    await user.click(within(row).getByRole('button', { name: /eliminar/i }))
    expect(fetchMock).toHaveBeenCalledTimes(1)

    await user.click(within(row).getByRole('button', { name: /confirmar eliminación/i }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(3))
    const [url, init] = fetchMock.mock.calls[1]
    expect(url).toBe('/catalog/categories/cat-1')
    expect(init.method).toBe('DELETE')
    await waitFor(() => expect(screen.queryByText('Carnes')).not.toBeInTheDocument())
  })

  it('shows why a category in use cannot be deleted and keeps it listed', async () => {
    fetchMock
      .mockResolvedValueOnce(json([meat]))
      .mockResolvedValueOnce(json({ error: 'category-in-use' }, 409))

    const user = userEvent.setup()
    render(<CategoriesScreen />)

    await screen.findByText('Carnes')
    await user.click(screen.getByRole('button', { name: /eliminar/i }))
    await user.click(screen.getByRole('button', { name: /confirmar eliminación/i }))

    expect(await screen.findByRole('alert')).toHaveTextContent(/hay productos que usan esta categoría/i)
    expect(screen.getByText('Carnes')).toBeInTheDocument()
  })
})

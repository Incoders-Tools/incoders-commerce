import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { CitiesScreen } from './CitiesScreen'
import type { MasterDataEntry } from '@/api/types'

const rosario: MasterDataEntry = {
  id: '11111111-1111-1111-1111-111111111111',
  organizationId: 'org-1',
  name: 'Rosario',
  key: 'rosario',
  sortOrder: 10,
  isActive: true,
  createdAtUtc: '2024-03-15T12:00:00Z',
  updatedAtUtc: '2024-04-20T12:00:00Z',
}

const funes: MasterDataEntry = {
  ...rosario,
  id: '22222222-2222-2222-2222-222222222222',
  name: 'Funes',
  key: 'funes',
  sortOrder: 20,
  isActive: false,
}

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

describe('CitiesScreen', () => {
  const fetchMock = vi.fn()

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
    window.localStorage.clear()
  })

  it('lists cities including inactive ones, with audit dates', async () => {
    fetchMock.mockResolvedValueOnce(json([rosario, funes]))

    render(<CitiesScreen />)

    await screen.findByText('Rosario')
    expect(fetchMock.mock.calls[0][0]).toBe('/customers/cities?includeInactive=true')
    expect(screen.getByRole('heading', { name: 'Ciudades' })).toBeInTheDocument()
    const rows = within(screen.getByRole('table')).getAllByRole('row')
    expect(within(rows[1]).getByText('Activa')).toBeInTheDocument()
    expect(within(rows[1]).getByText(/15\/03\/2024/)).toBeInTheDocument()
    expect(within(rows[2]).getByText('Inactiva')).toBeInTheDocument()
  })

  it('filters by name search and by active/inactive status', async () => {
    fetchMock.mockResolvedValueOnce(json([rosario, funes]))
    const user = userEvent.setup()
    render(<CitiesScreen />)
    await screen.findByText('Rosario')

    await user.selectOptions(screen.getByLabelText('Estado'), 'inactive')
    expect(screen.queryByText('Rosario')).not.toBeInTheDocument()
    expect(screen.getByText('Funes')).toBeInTheDocument()

    await user.selectOptions(screen.getByLabelText('Estado'), 'all')
    await user.type(screen.getByLabelText('Buscar ciudades'), 'rosa')
    expect(screen.getByText('Rosario')).toBeInTheDocument()
    expect(screen.queryByText('Funes')).not.toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it('creates a city from the full-page form, always sending isActive', async () => {
    fetchMock
      .mockResolvedValueOnce(json([]))
      .mockResolvedValueOnce(json(rosario, 201))
      .mockResolvedValueOnce(json([rosario]))
    const user = userEvent.setup()
    render(<CitiesScreen />)
    await screen.findByText('Todavía no hay ciudades.')

    await user.click(screen.getByRole('button', { name: 'Nueva ciudad' }))
    expect(screen.getByRole('heading', { name: 'Nueva ciudad' })).toBeInTheDocument()
    await user.type(screen.getByLabelText('Nombre'), 'Rosario')
    await user.click(screen.getByRole('button', { name: /^guardar$/i }))

    await screen.findByText('Rosario')
    const [url, init] = fetchMock.mock.calls[1]
    expect(url).toBe('/customers/cities')
    expect(init.method).toBe('POST')
    expect(JSON.parse(init.body as string)).toMatchObject({ name: 'Rosario', isActive: true })
  })

  it('edits a city with PUT and shows its created and updated dates in the form', async () => {
    fetchMock
      .mockResolvedValueOnce(json([rosario]))
      .mockResolvedValueOnce(json({ ...rosario, name: 'Rosario Centro' }))
      .mockResolvedValueOnce(json([rosario]))
    const user = userEvent.setup()
    render(<CitiesScreen />)
    await screen.findByText('Rosario')

    await user.click(screen.getByRole('button', { name: 'Editar' }))
    expect(screen.getByRole('heading', { name: 'Editar ciudad' })).toBeInTheDocument()
    expect(screen.getByText(/Creada/)).toHaveTextContent('15/03/2024')
    expect(screen.getByText(/Actualizada/)).toHaveTextContent('20/04/2024')
    await user.clear(screen.getByLabelText('Nombre'))
    await user.type(screen.getByLabelText('Nombre'), 'Rosario Centro')
    await user.click(screen.getByRole('button', { name: /^guardar$/i }))

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(3))
    const [url, init] = fetchMock.mock.calls[1]
    expect(url).toBe(`/customers/cities/${rosario.id}`)
    expect(init.method).toBe('PUT')
    expect(JSON.parse(init.body as string)).toEqual({
      name: 'Rosario Centro',
      key: 'rosario',
      sortOrder: 10,
      isActive: true,
    })
  })

  it('deactivates and reactivates a city with PUT instead of deleting', async () => {
    fetchMock
      .mockResolvedValueOnce(json([rosario]))
      .mockResolvedValueOnce(json({ ...rosario, isActive: false }))
      .mockResolvedValueOnce(json([{ ...rosario, isActive: false }]))
      .mockResolvedValueOnce(json({ ...rosario, isActive: true }))
      .mockResolvedValueOnce(json([rosario]))
    const user = userEvent.setup()
    render(<CitiesScreen />)
    await screen.findByText('Rosario')

    await user.click(screen.getByRole('button', { name: 'Desactivar' }))
    await screen.findByRole('button', { name: 'Activar' })
    expect(fetchMock.mock.calls[1][1].method).toBe('PUT')
    expect(JSON.parse(fetchMock.mock.calls[1][1].body as string).isActive).toBe(false)

    await user.click(screen.getByRole('button', { name: 'Activar' }))
    await screen.findByRole('button', { name: 'Desactivar' })
    expect(JSON.parse(fetchMock.mock.calls[3][1].body as string).isActive).toBe(true)
  })

  it('explains a duplicate name or key in plain Spanish', async () => {
    fetchMock
      .mockResolvedValueOnce(json([]))
      .mockResolvedValueOnce(json({ error: 'city-name-in-use' }, 409))
      .mockResolvedValueOnce(json({ error: 'city-key-in-use' }, 409))
    const user = userEvent.setup()
    render(<CitiesScreen />)
    await screen.findByText('Todavía no hay ciudades.')

    await user.click(screen.getByRole('button', { name: 'Nueva ciudad' }))
    await user.type(screen.getByLabelText('Nombre'), 'Rosario')
    await user.click(screen.getByRole('button', { name: /^guardar$/i }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Ya existe una ciudad con ese nombre.')

    await user.click(screen.getByRole('button', { name: /^guardar$/i }))
    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent('Ya existe una ciudad con esa clave.'),
    )
  })

  it('does not claim there are no cities when the load failed', async () => {
    fetchMock.mockRejectedValueOnce(new TypeError('Failed to fetch'))
    render(<CitiesScreen />)

    await screen.findByTestId('data-view-load-error')
    expect(screen.queryByText('Todavía no hay ciudades.')).not.toBeInTheDocument()
  })

  it('lays the form out on a responsive grid, not a single narrow column', async () => {
    fetchMock.mockResolvedValueOnce(json([]))
    const user = userEvent.setup()
    const { container } = render(<CitiesScreen />)
    await screen.findByText('Todavía no hay ciudades.')
    await user.click(screen.getByRole('button', { name: 'Nueva ciudad' }))

    expect(container.querySelector('form .grid')).not.toBeNull()
    expect(container.querySelector('[class*="max-w-xl"]')).toBeNull()
  })
})

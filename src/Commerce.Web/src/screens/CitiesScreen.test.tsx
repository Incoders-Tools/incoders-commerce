import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { CitiesScreen } from './CitiesScreen'
import type { GeoCity, GeoProvince } from '@/api/types'

const provinces: GeoProvince[] = [
  { id: '06', isoCode: 'AR-B', name: 'Buenos Aires', countryCode: 'AR', countryName: 'Argentina' },
  { id: '82', isoCode: 'AR-S', name: 'Santa Fe', countryCode: 'AR', countryName: 'Argentina' },
]

const rosario: GeoCity = {
  id: '11111111-1111-1111-1111-111111111111',
  indecId: '82084010',
  name: 'Rosario',
  provinceId: '82',
  provinceName: 'Santa Fe',
  countryCode: 'AR',
  departmentName: 'Rosario',
  isActive: true,
  createdAtUtc: '2024-03-15T12:00:00Z',
  updatedAtUtc: '2024-04-20T12:00:00Z',
}

const funes: GeoCity = {
  ...rosario,
  id: '22222222-2222-2222-2222-222222222222',
  indecId: null,
  name: 'Funes',
  departmentName: null,
  isActive: false,
}

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

describe('CitiesScreen (core geography, system administrator)', () => {
  const fetchMock = vi.fn()
  let cityPages: (url: URL) => GeoCity[]

  /** Query strings requested from `GET /geo/cities`, in order. */
  const cityQueries = () =>
    fetchMock.mock.calls
      .filter((call) => call[1]?.method === undefined && (call[0] as string).startsWith('/geo/cities'))
      .map((call) => new URL(call[0] as string, 'http://x').searchParams)

  beforeEach(() => {
    cityPages = () => [rosario, funes]
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
      if (url === '/geo/provinces') return json(provinces)
      if (init?.method === 'POST') return json(rosario, 201)
      if (init?.method === 'PUT') return json(rosario)
      return json(cityPages(new URL(url, 'http://x')))
    })
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
    window.localStorage.clear()
  })

  it('lists the first page with province, department, INDEC code, status and audit date', async () => {
    render(<CitiesScreen />)

    await screen.findByText('Funes')
    expect(screen.getByRole('heading', { name: 'Ciudades' })).toBeInTheDocument()
    expect(cityQueries()[0].get('limit')).toBe('25')
    expect(cityQueries()[0].get('includeInactive')).toBe('true')
    const rows = within(screen.getByRole('table')).getAllByRole('row')
    expect(within(rows[1]).getByText('Santa Fe')).toBeInTheDocument()
    expect(within(rows[1]).getByText('82084010')).toBeInTheDocument()
    expect(within(rows[1]).getByText('Activa')).toBeInTheDocument()
    expect(within(rows[1]).getByText(/20\/04\/2024/)).toBeInTheDocument()
    expect(within(rows[2]).getByText('Inactiva')).toBeInTheDocument()
  })

  it('searches on the server, debounced, and filters by province', async () => {
    const user = userEvent.setup()
    render(<CitiesScreen />)
    await screen.findByText('Funes')

    await user.type(screen.getByLabelText('Buscar ciudades'), 'rosa')
    await waitFor(() => expect(cityQueries().at(-1)?.get('search')).toBe('rosa'))
    expect(cityQueries()).toHaveLength(2)

    await screen.findByRole('option', { name: 'Santa Fe' })
    await user.selectOptions(screen.getByLabelText('Provincia'), '82')
    await waitFor(() => expect(cityQueries().at(-1)?.get('provinceId')).toBe('82'))
    expect(cityQueries().at(-1)?.get('search')).toBe('rosa')
  })

  it('can restrict the list to active cities', async () => {
    const user = userEvent.setup()
    render(<CitiesScreen />)
    await screen.findByText('Funes')

    await user.selectOptions(screen.getByLabelText('Estado'), 'active')

    await waitFor(() => expect(cityQueries().at(-1)?.has('includeInactive')).toBe(false))
  })

  it('loads more pages while the server keeps returning full pages', async () => {
    const page = (start: number) =>
      Array.from({ length: 25 }, (_, i) => ({ ...rosario, id: `id-${start + i}`, name: `Ciudad ${start + i}` }))
    cityPages = (url) => (url.searchParams.get('offset') === '25' ? [{ ...rosario, id: 'last', name: 'Ultima' }] : page(0))
    const user = userEvent.setup()
    render(<CitiesScreen />)

    await screen.findByText('Ciudad 0')
    await user.click(screen.getByRole('button', { name: 'Cargar más' }))

    await screen.findByText('Ultima')
    expect(screen.getByText('Ciudad 24')).toBeInTheDocument()
    expect(cityQueries().at(-1)?.get('offset')).toBe('25')
    expect(screen.queryByRole('button', { name: 'Cargar más' })).not.toBeInTheDocument()
  })

  it('creates a city from the full-page form', async () => {
    cityPages = () => []
    const user = userEvent.setup()
    render(<CitiesScreen />)
    await screen.findByText('Todavía no hay ciudades.')

    await user.click(screen.getByRole('button', { name: 'Nueva ciudad' }))
    await user.type(screen.getByLabelText('Nombre'), 'Funes')
    await user.selectOptions(await screen.findByRole('combobox', { name: 'Provincia' }), '82')
    await user.type(screen.getByLabelText('Departamento'), 'Rosario')
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(screen.getByRole('heading', { name: 'Ciudades' })).toBeInTheDocument())
    const post = fetchMock.mock.calls.find((call) => call[1]?.method === 'POST')!
    expect(post[0]).toBe('/geo/cities')
    expect(JSON.parse(post[1].body)).toEqual({
      name: 'Funes',
      provinceId: '82',
      departmentName: 'Rosario',
      isActive: true,
    })
  })

  it('edits a city, clearing the department with a blank value', async () => {
    const user = userEvent.setup()
    render(<CitiesScreen />)
    await screen.findByText('Funes')

    const rows = within(screen.getByRole('table')).getAllByRole('row')
    await user.click(within(rows[1]).getByRole('button', { name: 'Editar' }))
    expect(await screen.findByRole('heading', { name: 'Editar ciudad' })).toBeInTheDocument()
    expect(screen.getByLabelText('Nombre')).toHaveValue('Rosario')
    expect(screen.getByRole('combobox', { name: 'Provincia' })).toHaveValue('82')
    expect(screen.getByText('Código INDEC: 82084010')).toBeInTheDocument()

    await user.clear(screen.getByLabelText('Departamento'))
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    await waitFor(() => expect(screen.getByRole('heading', { name: 'Ciudades' })).toBeInTheDocument())
    const put = fetchMock.mock.calls.find((call) => call[1]?.method === 'PUT')!
    expect(put[0]).toBe(`/geo/cities/${rosario.id}`)
    expect(JSON.parse(put[1].body)).toEqual({ name: 'Rosario', provinceId: '82', departmentName: '', isActive: true })
  })

  it('deactivates a city from the list keeping everything else', async () => {
    const user = userEvent.setup()
    render(<CitiesScreen />)
    await screen.findByText('Funes')

    const rows = within(screen.getByRole('table')).getAllByRole('row')
    await user.click(within(rows[1]).getByRole('button', { name: 'Desactivar' }))

    await waitFor(() => expect(fetchMock.mock.calls.some((call) => call[1]?.method === 'PUT')).toBe(true))
    const put = fetchMock.mock.calls.find((call) => call[1]?.method === 'PUT')!
    expect(JSON.parse(put[1].body)).toEqual({ name: 'Rosario', isActive: false })
  })

  it('reports a duplicate name in Spanish and stays on the form', async () => {
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
      if (url === '/geo/provinces') return json(provinces)
      if (init?.method === 'POST') return json({ error: 'city-name-in-use' }, 409)
      return json([])
    })
    const user = userEvent.setup()
    render(<CitiesScreen />)
    await screen.findByText('Todavía no hay ciudades.')

    await user.click(screen.getByRole('button', { name: 'Nueva ciudad' }))
    await user.type(screen.getByLabelText('Nombre'), 'Funes')
    await user.selectOptions(await screen.findByRole('combobox', { name: 'Provincia' }), '82')
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Ya existe una ciudad con ese nombre en esa provincia.')
    expect(screen.getByRole('heading', { name: 'Nueva ciudad' })).toBeInTheDocument()
  })

  it('does not claim there are no cities when the load failed', async () => {
    fetchMock.mockImplementation(async (url: string) => {
      if (url === '/geo/provinces') return json(provinces)
      throw new TypeError('Failed to fetch')
    })
    render(<CitiesScreen />)

    expect(await screen.findByTestId('data-view-load-error')).toHaveTextContent('No se pudieron cargar las ciudades.')
    expect(screen.queryByText('Todavía no hay ciudades.')).not.toBeInTheDocument()
  })
})

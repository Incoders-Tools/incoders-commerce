import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Vehicle } from '@/api/vehicles'
import { VehiclesScreen } from './VehiclesScreen'

/**
 * vehicles V2: the business's vehicles under the lookup tables: listed by plate, created and edited (plate, brand, model,
 * year, notes, active), the plate written any way and kept as the server normalizes it.
 */
describe('VehiclesScreen', () => {
  const fetchMock = vi.fn()
  const json = (body: unknown, status = 200) => new Response(body === undefined ? null : JSON.stringify(body), { status })

  const truck: Vehicle = {
    id: 'v-1', plate: 'AB123CD', brand: 'Ford', model: 'F-4000', year: 2015, notes: 'El camión', isActive: true,
    label: 'AB123CD · Ford F-4000', createdAtUtc: '2026-10-13T00:00:00Z', updatedAtUtc: '2026-10-13T00:00:00Z',
  }

  let stored: Vehicle[]
  let create: (body: Record<string, unknown>) => Response

  beforeEach(() => {
    stored = [truck]
    create = (body) => {
      const created: Vehicle = {
        ...truck, id: 'v-2', plate: String(body.plate).replace(/[\s-]/g, '').toUpperCase(), brand: (body.brand as string) ?? null,
        model: (body.model as string) ?? null, year: (body.year as number) ?? null, notes: null, label: 'AA000AA · Fiat Fiorino',
      }
      stored = [created, ...stored]
      return json(created, 201)
    }
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
      const key = `${init?.method ?? 'GET'} ${url}`
      if (key === 'GET /vehicles?includeInactive=true') return json(stored)
      if (key === 'POST /vehicles') return create(JSON.parse(init!.body as string))
      if (key === 'PUT /vehicles/v-1') return json({ ...truck, ...JSON.parse(init!.body as string) })
      throw new TypeError(`unrouted ${key}`)
    })
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  const calls = (key: string) => fetchMock.mock.calls.filter(([url, init]) => `${init?.method ?? 'GET'} ${url}` === key)
  const bodyOf = (key: string) => JSON.parse(calls(key)[0][1].body as string)

  it('lists the vehicles and creates one with its plate written any way', async () => {
    const user = userEvent.setup()
    render(<VehiclesScreen />)

    const row = (await screen.findByText('AB123CD')).closest('tr')!
    expect(within(row).getByText('Ford F-4000')).toBeInTheDocument()
    expect(within(row).getByText('2015')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Nuevo vehículo' }))
    await user.type(screen.getByLabelText('Patente'), 'aa 000-aa')
    await user.type(screen.getByLabelText('Marca'), 'Fiat')
    await user.type(screen.getByLabelText('Modelo'), 'Fiorino')
    await user.type(screen.getByLabelText('Año'), '2020')
    await user.click(screen.getByRole('button', { name: 'Guardar vehículo' }))

    await waitFor(() => expect(calls('POST /vehicles')).toHaveLength(1))
    expect(bodyOf('POST /vehicles')).toEqual({ plate: 'aa 000-aa', brand: 'Fiat', model: 'Fiorino', year: 2020, notes: null })
    expect(await screen.findByText('Vehículo AA000AA guardado.')).toBeInTheDocument()
  })

  it('edits a vehicle and deactivates it', async () => {
    const user = userEvent.setup()
    render(<VehiclesScreen />)

    // A single action shows inline in the row.
    const row = (await screen.findByText('AB123CD')).closest('tr')!
    await user.click(within(row).getByRole('button', { name: 'Editar' }))
    expect(screen.getByLabelText('Patente')).toHaveValue('AB123CD')
    await user.click(screen.getByLabelText('Activo'))
    await user.click(screen.getByRole('button', { name: 'Guardar vehículo' }))

    await waitFor(() => expect(calls('PUT /vehicles/v-1')).toHaveLength(1))
    expect(bodyOf('PUT /vehicles/v-1')).toEqual({
      plate: 'AB123CD', brand: 'Ford', model: 'F-4000', year: 2015, notes: 'El camión', isActive: false,
    })
  })

  it('checks the plate and the year before saving, and explains a plate already in use', async () => {
    create = () => json({ error: 'vehicle-plate-in-use' }, 409)
    const user = userEvent.setup()
    render(<VehiclesScreen />)
    await user.click(await screen.findByRole('button', { name: 'Nuevo vehículo' }))

    await user.type(screen.getByLabelText('Patente'), 'A-1')
    await user.click(screen.getByRole('button', { name: 'Guardar vehículo' }))
    expect(await screen.findByText('La patente lleva de 5 a 10 letras o números (los espacios y guiones no cuentan).')).toBeInTheDocument()

    await user.clear(screen.getByLabelText('Patente'))
    await user.type(screen.getByLabelText('Patente'), 'AB123CD')
    await user.type(screen.getByLabelText('Año'), '1900')
    await user.click(screen.getByRole('button', { name: 'Guardar vehículo' }))
    expect(await screen.findByText(/El año va de 1950 a/)).toBeInTheDocument()
    expect(calls('POST /vehicles')).toHaveLength(0)

    await user.clear(screen.getByLabelText('Año'))
    await user.click(screen.getByRole('button', { name: 'Guardar vehículo' }))
    expect(await screen.findByText('Ya hay un vehículo con esa patente.')).toBeInTheDocument()
  })
})

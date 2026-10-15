import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { BusinessTypesScreen } from './BusinessTypesScreen'
import type { MasterDataEntry } from '@/api/types'

const bar: MasterDataEntry = {
  id: '33333333-3333-3333-3333-333333333333',
  organizationId: 'org-1',
  name: 'Bar',
  key: 'bar',
  sortOrder: 1,
  isActive: true,
  createdAtUtc: '2024-03-15T12:00:00Z',
  updatedAtUtc: '2024-03-15T12:00:00Z',
}

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

describe('BusinessTypesScreen', () => {
  const fetchMock = vi.fn()

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
    window.localStorage.clear()
  })

  it('lists business types from the business-types endpoint', async () => {
    fetchMock.mockResolvedValueOnce(json([bar]))
    render(<BusinessTypesScreen />)

    await screen.findByText('Bar')
    expect(fetchMock.mock.calls[0][0]).toBe('/customers/business-types?includeInactive=true')
    expect(screen.getByRole('heading', { name: 'Tipos de negocio' })).toBeInTheDocument()
  })

  it('maps a duplicate name to a business-type message', async () => {
    fetchMock
      .mockResolvedValueOnce(json([]))
      .mockResolvedValueOnce(json({ error: 'business-type-name-in-use' }, 409))
    const user = userEvent.setup()
    render(<BusinessTypesScreen />)
    await screen.findByText('Todavía no hay tipos de negocio.')

    await user.click(screen.getByRole('button', { name: 'Nuevo tipo de negocio' }))
    await user.type(screen.getByLabelText('Nombre'), 'Bar')
    await user.click(screen.getByRole('button', { name: /^guardar$/i }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Ya existe un tipo de negocio con ese nombre.')
    expect(fetchMock.mock.calls[1][0]).toBe('/customers/business-types')
  })

  // owner-requests-quick-adjustments Q2: the key and the order are the server's business, not the user's.
  it('shows neither the key nor the order, in the table or the form', async () => {
    fetchMock.mockResolvedValueOnce(json([bar]))
    const user = userEvent.setup()
    render(<BusinessTypesScreen />)
    await screen.findByText('Bar')

    expect(screen.queryByRole('columnheader', { name: 'Clave' })).not.toBeInTheDocument()
    expect(screen.queryByRole('columnheader', { name: 'Orden' })).not.toBeInTheDocument()
    expect(screen.queryByText('bar')).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Editar' }))
    expect(screen.getByLabelText('Nombre')).toHaveValue('Bar')
    expect(screen.queryByLabelText('Clave')).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Orden')).not.toBeInTheDocument()
  })

  it('sends only the name and the status, so the server derives or keeps the key', async () => {
    fetchMock.mockResolvedValueOnce(json([bar])).mockResolvedValueOnce(json(bar)).mockResolvedValueOnce(json([bar]))
    const user = userEvent.setup()
    render(<BusinessTypesScreen />)
    await screen.findByText('Bar')

    await user.click(screen.getByRole('button', { name: 'Editar' }))
    await user.clear(screen.getByLabelText('Nombre'))
    await user.type(screen.getByLabelText('Nombre'), 'Bar y café')
    await user.click(screen.getByRole('button', { name: /^guardar$/i }))

    await screen.findByText('Bar')
    expect(JSON.parse(fetchMock.mock.calls[1][1].body as string)).toEqual({ name: 'Bar y café', isActive: true })
  })

  it('deactivates without sending the key or the order', async () => {
    fetchMock.mockResolvedValueOnce(json([bar])).mockResolvedValueOnce(json(bar)).mockResolvedValueOnce(json([bar]))
    const user = userEvent.setup()
    render(<BusinessTypesScreen />)
    await screen.findByText('Bar')

    await user.click(screen.getByRole('button', { name: 'Desactivar' }))

    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(3))
    expect(JSON.parse(fetchMock.mock.calls[1][1].body as string)).toEqual({ name: 'Bar', isActive: false })
  })
})
